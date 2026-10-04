using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectSpy.Unity.Scenes
{
    /// <summary>
    /// Loads scenes additively over a persistent root, so Base and Tactical never destroy
    /// the services or the simulation underneath each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why additive rather than single.</b> The base clock freezes during a tactical
    /// mission and the mission's end hands its elapsed steps back to the strategic clock.
    /// Both halves are live across a mission: returning from a mission must not tear down
    /// the campaign's state, and entering one must not tear down the services registered in
    /// <c>RootLifetimeScope</c>. Single-scene loading would put the scope in whichever scene
    /// happened to load last, and unloading that scene would dispose the whole session.
    /// </para>
    /// <para>
    /// <b>Boot is index 0 and loads alone.</b> It is the only scene loaded by
    /// <c>LoadSceneAsync</c> in Single mode, and it builds the persistent root. Everything
    /// after it is additive. Keeping Boot as scene 0 in the build settings means a fresh
    /// launch lands there without any code deciding it should.
    /// </para>
    /// </remarks>
    public sealed class SceneRouter : Services.IProjectSpyService
    {
        private readonly Dictionary<string, Scene> _loaded = new();
        private readonly List<string> _stack = new();

        /// <summary>Raised after any scene finishes loading. Carries the scene name.</summary>
        public event Action<string> SceneLoaded;

        /// <summary>Raised after any scene finishes unloading. Carries the scene name.</summary>
        public event Action<string> SceneUnloaded;

        /// <summary>Names of the currently loaded scenes, in load order.</summary>
        public IReadOnlyList<string> LoadedScenes
        {
            get
            {
                var names = new List<string>(_loaded.Count);
                foreach (var pair in _loaded)
                    names.Add(pair.Key);
                return names;
            }
        }

        /// <summary>True when the named scene is currently loaded.</summary>
        public bool IsLoaded(string sceneName) => _loaded.ContainsKey(sceneName);

        /// <summary>
        /// Loads a scene additively and registers it.
        /// </summary>
        /// <remarks>
        /// Loading a scene already present is a no-op rather than an error: the router is
        /// called from menu code that has no cheap way to know what is loaded, and a
        /// double-load of an additive scene would throw.
        /// </remarks>
        public async Task LoadAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Additive)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Scene name is required.", nameof(sceneName));

            if (_loaded.ContainsKey(sceneName))
                return;

            // LoadSceneAsync returns an AsyncOperation, not a Scene. The scene itself is looked up by
            // name after the await, which is the only way to get a handle on it — and a scene
            // that failed to load simply is not in the loaded set.
            await SceneManager.LoadSceneAsync(
                sceneName, mode == LoadSceneMode.Single ? LoadSceneMode.Single : LoadSceneMode.Additive);

            int found = SceneManager.GetSceneByName(sceneName).buildIndex;
            if (found < 0)
            {
                // Unity reports the same empty result both for "not in build settings" and
                // for a genuine load failure, so the name is repeated here rather than left
                // to the caller's guess.
                Debug.LogError(
                    $"[ProjectSpy] Could not load scene '{sceneName}'. Is it in Build Settings?");
                return;
            }

            _loaded[sceneName] = SceneManager.GetSceneByName(sceneName);
            _stack.Add(sceneName);
            SceneLoaded?.Invoke(sceneName);
        }

        /// <summary>
        /// Unloads a scene and forgets it.
        /// </summary>
        /// <remarks>
        /// Never called on Boot. The root's scene is what holds the lifetime scope, and
        /// unloading it would dispose every service mid-session.
        /// </remarks>
        public async Task UnloadAsync(string sceneName)
        {
            if (sceneName == SceneNames.Boot)
            {
                Debug.LogWarning(
                    "[ProjectSpy] Refusing to unload the Boot scene: it owns the root " +
                    "lifetime scope, and unloading it would dispose the whole session.");
                return;
            }

            if (!_loaded.TryGetValue(sceneName, out Scene scene) || !scene.IsValid())
            {
                _loaded.Remove(sceneName);
                return;
            }

            await SceneManager.UnloadSceneAsync(scene);
            _loaded.Remove(sceneName);
            _stack.Remove(sceneName);
            SceneUnloaded?.Invoke(sceneName);
        }

        /// <summary>
        /// Unloads every additive scene except Boot, e.g. when aborting a mission.
        /// </summary>
        public async Task UnloadAllAdditiveAsync()
        {
            // Copied first: the loop mutates _loaded.
            var names = new List<string>(_loaded.Keys);
            foreach (string name in names)
            {
                if (name != SceneNames.Boot)
                    await UnloadAsync(name);
            }
        }

        /// <summary>The most recently loaded scene, or null.</summary>
        public string Current => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
    }

    /// <summary>Scene names, in one place so nothing loads a scene by a mistyped literal.</summary>
    public static class SceneNames
    {
        /// <summary>Build index 0. Builds the persistent root and the lifetime scope.</summary>
        public const string Boot = "Boot";

        /// <summary>The strategic cutaway view. Loaded additively over Boot.</summary>
        public const string Base = "Base";

        /// <summary>The mission scene. Loaded additively over Boot, on top of Base.</summary>
        public const string Tactical = "Tactical";
    }
}