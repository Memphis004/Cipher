using System;
using System.Collections.Generic;
using R3;
using UnityEngine;

namespace ProjectSpy.Unity.UI
{
    /// <summary>
    /// A stacked, pooled window manager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pooled because a management game opens and closes windows constantly — an agent
    /// detail, a contract, a confirm — and instantiating a canvas per open produces the
    /// frame spikes that make a UI feel cheap. Windows are pushed on a stack, so Escape
    /// closes the top one, which is what the player expects from every other game.
    /// </para>
    /// <para>
    /// <b>Auto-pause is opt-in per window.</b> Some windows are worth pausing for and some
    /// are not; a toast must never pause, and a running roster list should not. Making it a
    /// per-window flag rather than a global setting is the difference between a pause that
    /// feels deliberate and one that feels broken.
    /// </para>
    /// </remarks>
    public sealed class WindowService : Services.IProjectSpyService
    {
        private readonly Stack<WindowHandle> _stack = new();
        private readonly Dictionary<WindowHandle, GameObject> _instances = new();
        private readonly Stack<GameObject> _pool = new();

        /// <summary>Transform windows are parented to. Injected by the UIRoot at boot.</summary>
        public Transform WindowLayer { get; set; }

        /// <summary>Raised whenever the open stack changes. Carries the open count.</summary>
        public Subject<int> StackChanged { get; } = new();

        /// <summary>How many windows are open.</summary>
        public int OpenCount => _stack.Count;

        /// <summary>The topmost window, or null.</summary>
        public WindowHandle Top => _stack.Count > 0 ? _stack.Peek() : null;

        /// <summary>
        /// Opens a window.
        /// </summary>
        /// <param name="id">Stable identifier, also the prefab name to look for.</param>
        /// <param name="autoPause">Whether opening this window pauses the strategic clock.</param>
        public WindowHandle Open(string id, bool autoPause = false)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Window id is required.", nameof(id));

            var go = Rent();
            go.name = $"Window_{id}";
            if (WindowLayer != null)
                go.transform.SetParent(WindowLayer, false);

            var handle = new WindowHandle(id, autoPause, this);
            _instances[handle] = go;
            _stack.Push(handle);
            StackChanged.OnNext(_stack.Count);

            handle.RaiseOpened();
            return handle;
        }

        /// <summary>
        /// Closes a specific window, and any windows stacked above it.
        /// </summary>
        /// <remarks>
        /// Closing a window out of order would leave a hole in the stack with the window
        /// above it orphaned, so the windows above are closed too. Escape therefore only
        /// ever needs the top, but a "close this panel" button can target any depth.
        /// </remarks>
        public void Close(WindowHandle handle)
        {
            if (handle == null || !_instances.ContainsKey(handle))
                return;

            var ordered = new List<WindowHandle>(_stack);
            int index = ordered.IndexOf(handle);
            if (index < 0)
                return;

            // Close from the top down to and including the target.
            for (int i = ordered.Count - 1; i >= index; i--)
                CloseOne(ordered[i]);
        }

        /// <summary>Closes the topmost window. Bound to Escape.</summary>
        public bool CloseTop() => CloseTop(out _);

        /// <summary>Closes the topmost window, reporting whether anything was open.</summary>
        public bool CloseTop(out WindowHandle closed)
        {
            closed = Top;
            if (closed == null)
                return false;

            CloseOne(closed);
            return true;
        }

        private void CloseOne(WindowHandle handle)
        {
            if (!_instances.Remove(handle, out GameObject go))
                return;

            handle.RaiseClosing();
            go.SetActive(false);
            _pool.Push(go);

            // Rebuild the stack without it. Popping directly would be wrong when a
            // lower window was closed and the ones above went with it.
            var kept = new List<WindowHandle>();
            foreach (var existing in _stack)
            {
                if (!ReferenceEquals(existing, handle))
                    kept.Add(existing);
            }

            _stack.Clear();
            for (int i = kept.Count - 1; i >= 0; i--)
                _stack.Push(kept[i]);

            StackChanged.OnNext(_stack.Count);
        }

        /// <summary>Closes everything.</summary>
        public void CloseAll()
        {
            var ordered = new List<WindowHandle>(_stack);
            for (int i = ordered.Count - 1; i >= 0; i--)
                CloseOne(ordered[i]);
        }

        private GameObject Rent()
        {
            while (_pool.Count > 0)
            {
                var pooled = _pool.Pop();
                if (pooled == null)
                    continue;
                pooled.SetActive(true);
                return pooled;
            }

            // A plain object rather than a prefab at this stage. Stage 8 supplies the real
            // window prefabs; the pooling, stacking and Escape behaviour are what this stage
            // is establishing, and they are all testable against an empty object.
            return new GameObject("Window");
        }
    }

    /// <summary>A handle to one open window.</summary>
    public sealed class WindowHandle
    {
        private readonly WindowService _owner;

        internal WindowHandle(string id, bool autoPause, WindowService owner)
        {
            Id = id;
            AutoPause = autoPause;
            _owner = owner;
        }

        /// <summary>Stable identifier of this window.</summary>
        public string Id { get; }

        /// <summary>Whether this window wants the strategic clock paused while open.</summary>
        public bool AutoPause { get; }

        /// <summary>Whether the player asked for this window to pause the clock.</summary>
        public bool PauseRequested { get; set; }

        /// <summary>Raised just after the window opens.</summary>
        public event Action Opened;

        /// <summary>Raised just before the window is pooled away.</summary>
        public event Action Closing;

        /// <summary>Closes this window.</summary>
        public void Close() => _owner.Close(this);

        internal void RaiseOpened() => Opened?.Invoke();
        internal void RaiseClosing() => Closing?.Invoke();
    }

    /// <summary>
    /// Transient notifications, stacked from the bottom of the screen.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="WindowService"/> because a toast must never be modal, never
    /// take a slot in the stack and never pause. Folding them into one service is how a
    /// notification ends up blocking a mission.
    /// </remarks>
    public sealed class ToastService : Services.IProjectSpyService
    {
        private readonly List<string> _live = new();

        /// <summary>Transform toasts are parented to. Injected by the UIRoot at boot.</summary>
        public Transform ToastLayer { get; set; }

        /// <summary>How long a toast stays up, in seconds.</summary>
        public float DefaultDurationSeconds = 3f;

        /// <summary>Currently visible toast messages, oldest first.</summary>
        public IReadOnlyList<string> Live => _live;

        /// <summary>Raised when a toast is shown. Carries the message.</summary>
        public Subject<string> Shown { get; } = new();

        /// <summary>
        /// Shows a message.
        /// </summary>
        /// <remarks>
        /// The text is a localization key, not prose. Core never produces English for the
        /// player (rule 4), so a toast raised from a Core event has to be resolved through
        /// <see cref="Localisation.LocalizationService"/> rather than interpolated here.
        /// </remarks>
        public void Show(string localizationKey)
        {
            if (string.IsNullOrWhiteSpace(localizationKey))
                return;

            _live.Add(localizationKey);
            Shown.OnNext(localizationKey);

            var go = new GameObject($"Toast_{localizationKey}");
            if (ToastLayer != null)
                go.transform.SetParent(ToastLayer, false);

            UnityEngine.Object.Destroy(go, DefaultDurationSeconds);
            _live.Remove(localizationKey);
        }
    }
}