using System;
using System.IO;
using ProjectSpy.Unity.Boot;
using UnityEngine;
using UnityEngine.InputSystem;

// RebindingOperation is nested inside InputActionRebindingExtensions (confirmed against
// the loaded assembly's type table, not guessed). Aliased because the file's own namespace
// is ProjectSpy.Unity.Input and the bare name would otherwise be ambiguous.
using RebindingOperation =
    UnityEngine.InputSystem.InputActionRebindingExtensions.RebindingOperation;

namespace ProjectSpy.Unity.Input
{
    /// <summary>
    /// Owns the InputActionAsset, its rebinding, and the step-boundary sampling that turns
    /// live input into Core commands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Input is sampled at tactical step boundaries and nowhere else</b> (rule 14). That is
    /// not an optimisation; it is what makes a replay reproduce a mission step for step. A
    /// value read in <c>Update</c> depends on frame timing, and a replay that reads it in
    /// <c>Update</c> would diverge from the original run the moment the two machines had
    /// different frame rates. <see cref="SampleInto"/> is therefore the single place a
    /// frame's input is turned into a command, and
    /// <see cref="Simulation.SimulationRunner"/> is what decides when it is called.
    /// </para>
    /// <para>
    /// <b>No gameplay rule reads an input device.</b> The values this service hands out are
    /// raw intent — "move left", "interact" — with no range, cost or legality applied. Those
    /// are Core's, decided at a step boundary against Core's own state.
    /// </para>
    /// <para>
    /// <b>Rebinds are overrides, not edits.</b> <see cref="SaveBindingOverrides"/> writes the
    /// override JSON rather than modifying the <c>.inputactions</c> asset, so a rebinding is
    /// per-player, survives no asset merge, and cannot be committed by accident.
    /// </para>
    /// </remarks>
    public sealed class InputService : Services.IProjectSpyService
    {
        private InputActionAsset _actions;
        private string _bindingOverridePath;

        // Held so a rebind can be cancelled from elsewhere, e.g. when a window closes.
        // Cancel() lives on the operation instance, not on a static, so the only way to reach
        // an in-flight rebind is to keep it.
        private RebindingOperation _activeRebind;

        // RebindingOperation exposes no public "finished" flag, so in-progress state is
        // tracked here instead: it is set when a rebind starts and cleared by whichever of
        // OnComplete/OnCancel fires. Deriving it from the operation instead would mean
        // guessing at internal flags, which is exactly the kind of thing that breaks on a
        // package upgrade.
        private bool _rebindInProgress;

        /// <summary>The loaded action asset, or null before <see cref="Load"/>.</summary>
        public InputActionAsset Actions => _actions;

        /// <summary>True once the asset is loaded and enabled.</summary>
        public bool IsLoaded => _actions != null;

        /// <summary>Action map names the game reads. Kept here so maps are not stringly typed.</summary>
        public const string GameplayMap = "Gameplay";

        /// <summary>Action map for global UI, e.g. Escape and window management.</summary>
        public const string UiMap = "UI";

        /// <summary>
        /// Loads the action asset and enables its maps.
        /// </summary>
        /// <param name="asset">The asset, normally loaded from Resources.</param>
        /// <param name="bindingOverridePath">
        /// Where rebindings are stored. Defaults to a per-user file under
        /// <c>persistentDataPath</c>.
        /// </param>
        public void Load(InputActionAsset asset, string bindingOverridePath = null)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            _actions = asset;
            _bindingOverridePath = bindingOverridePath ?? Path.Combine(
                Application.persistentDataPath, ProjectSpyPaths.SaveFolder, "bindings.json");

            LoadBindingOverrides();

            _actions.Enable();
        }

        /// <summary>Enables or disables the whole asset, e.g. while a modal is up.</summary>
        public void SetEnabled(bool enabled)
        {
            if (_actions == null)
                return;

            if (enabled)
                _actions.Enable();
            else
                _actions.Disable();
        }

        /// <summary>Finds an action by name, or null.</summary>
        public InputAction Find(string actionName)
        {
            if (_actions == null)
                return null;

            foreach (var map in _actions.actionMaps)
            {
                var action = map.FindAction(actionName, throwIfNotFound: false);
                if (action != null)
                    return action;
            }

            return null;
        }

        /// <summary>
        /// True when an action is pressed this frame, reading buttons only.
        /// </summary>
        /// <remarks>
        /// <c>WasPressedThisFrame</c> rather than <c>IsPressed</c>, because the sample is
        /// taken at a step boundary: a held button must not fire once per step.
        /// </remarks>
        public bool PressedThisFrame(string actionName)
            => Find(actionName)?.WasPressedThisFrame() ?? false;

        /// <summary>Reads a 2D move vector from an action.</summary>
        public Vector2 ReadVector2(string actionName)
            => Find(actionName)?.ReadValue<Vector2>() ?? Vector2.zero;

        /// <summary>
        /// Runs the interactive rebinding flow for one action.
        /// </summary>
        /// <remarks>
        /// Uses the package's own flow, which handles the waiting-for-input, cancel and
        /// timeout states that a hand-rolled rebinder gets wrong. Callbacks are registered so
        /// the caller learns when it finished rather than polling.
        /// </remarks>
        /// <returns>False when the action could not be found, in which case nothing is bound.</returns>
        public bool BeginRebind(
            string actionName,
            Action<InputAction> onCompleted = null,
            Action<InputAction> onCanceled = null)
        {
            var action = Find(actionName);
            if (action == null)
            {
                Debug.LogError($"[ProjectSpy] Cannot rebind unknown action '{actionName}'.");
                return false;
            }

            action.Disable();

            var rebind = action.PerformInteractiveRebinding();

            // Without this a stray left-stick nudge binds itself to "move", and the player
            // spends ten minutes wondering why their character walks on its own.
            rebind.WithControlsExcluding("<Mouse>/position")
                  .WithControlsExcluding("<Mouse>/delta")
                  .WithCancelingThrough("<Keyboard>/escape")
                  .OnComplete(_ =>
                  {
                      _activeRebind = null;
                      _rebindInProgress = false;
                      action.Enable();
                      SaveBindingOverrides();
                      onCompleted?.Invoke(action);
                  })
                  .OnCancel(_ =>
                  {
                      _activeRebind = null;
                      _rebindInProgress = false;
                      action.Enable();
                      onCanceled?.Invoke(action);
                  });

            _rebindInProgress = true;

            _activeRebind = rebind.Start();
            return true;
        }

        /// <summary>True while a rebind is waiting for the player to press a key.</summary>
        public bool IsRebinding => _rebindInProgress;

        /// <summary>
        /// Cancels an in-progress rebind, if there is one.
        /// </summary>
        /// <remarks>
        /// Safe to call when nothing is rebinding. The operation's own cancel callback
        /// re-enables the action, so nothing is left disabled by cancelling.
        /// </remarks>
        public void CancelRebind()
        {
            if (!_rebindInProgress)
                return;

            // Cancel() fires OnCancel, which clears the flag and re-enables the action, so
            // nothing needs to be undone here.
            _activeRebind?.Cancel();
        }

        /// <summary>Resets every action back to the asset's authored bindings.</summary>
        public void ResetBindingOverrides()
        {
            if (_actions == null)
                return;

            foreach (var map in _actions.actionMaps)
            {
                foreach (var action in map.actions)
                    action.RemoveAllBindingOverrides();
            }

            SaveBindingOverrides();
        }

        /// <summary>
        /// Writes the rebindings to disk.
        /// </summary>
        /// <remarks>
        /// Saves overrides, never the asset. Editing the <c>.inputactions</c> file would make
        /// a rebinding a repository change that two players on one branch would fight over.
        /// </remarks>
        public void SaveBindingOverrides()
        {
            if (_actions == null || string.IsNullOrEmpty(_bindingOverridePath))
                return;

            string json = _actions.SaveBindingOverridesAsJson();
            string folder = Path.GetDirectoryName(_bindingOverridePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(_bindingOverridePath, json);
        }

        /// <summary>Applies previously saved rebindings.</summary>
        public void LoadBindingOverrides()
        {
            if (_actions == null || !File.Exists(_bindingOverridePath))
                return;

            try
            {
                _actions.LoadBindingOverridesFromJson(File.ReadAllText(_bindingOverridePath));
            }
            catch (Exception ex)
            {
                // Corrupt override JSON is not worth failing a boot over; the player gets
                // default bindings and the reason is in the log.
                Debug.LogWarning(
                    $"[ProjectSpy] Could not load binding overrides from '{_bindingOverridePath}': {ex.Message}");
            }
        }

        /// <summary>Where rebindings are stored.</summary>
        public string BindingOverridePath => _bindingOverridePath;
    }
}