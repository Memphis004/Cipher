using System.Collections.Generic;
using R3;
using UnityEngine;

namespace ProjectSpy.Unity.Audio
{
    /// <summary>
    /// Pooled one-shot and looping audio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pooled for the same reason windows are: a mission produces a great many short sounds
    /// (footsteps per step per agent), and instantiating an <c>AudioSource</c> per footstep
    /// is a guaranteed allocation spike in the middle of the one moment the player is
    /// watching most closely.
    /// </para>
    /// <para>
    /// <b>Silence is never null.</b> <see cref="Play"/> on a missing clip logs once and
    /// returns rather than throwing, because the audio assets are placeholders at this stage
    /// and a missing placeholder must not be able to break a mission. Stage 9b generates
    /// silent clips so that "nothing is ever null" holds structurally rather than by
    /// discipline.
    /// </para>
    /// <para>
    /// <b>Occlusion comes from Core.</b> The service accepts a per-listener gain computed
    /// from the connection graph, not from Unity physics: how far away something sounds is a
    /// <em>rule</em> about the building, and Core already owns that graph.
    /// </para>
    /// </remarks>
    public sealed class AudioService : Services.IProjectSpyService, System.IDisposable
    {
        private readonly Stack<AudioSource> _pool = new();
        private readonly List<AudioSource> _active = new();
        private readonly Dictionary<string, AudioClip> _clips = new();
        private readonly HashSet<string> _reportedMissing = new();

        /// <summary>Raised when a clip is requested that is not loaded.</summary>
        public Subject<string> MissingClip { get; } = new();

        /// <summary>How many sources are currently in use.</summary>
        public int ActiveSourceCount => _active.Count;

        /// <summary>How many sources are pooled and idle.</summary>
        public int PooledSourceCount => _pool.Count;

        /// <summary>Registers a clip under a key.</summary>
        public void Register(string key, AudioClip clip)
        {
            if (!string.IsNullOrEmpty(key) && clip != null)
                _clips[key] = clip;
        }

        /// <summary>
        /// Plays a one-shot sound.
        /// </summary>
        /// <param name="key">Clip key.</param>
        /// <param name="volume">Linear volume.</param>
        /// <param name="pitch">Pitch multiplier; a small random spread avoids machine-gun repetition.</param>
        public void Play(string key, float volume = 1f, float pitch = 1f)
        {
            if (!_clips.TryGetValue(key, out AudioClip clip) || clip == null)
            {
                if (_reportedMissing.Add(key))
                {
                    Debug.LogWarning($"[ProjectSpy] No audio clip registered for '{key}'.");
                    MissingClip.OnNext(key);
                }

                return;
            }

            var source = Rent();
            source.pitch = pitch;
            source.PlayOneShot(clip, volume);
        }

        /// <summary>
        /// Plays a sound whose volume has already been attenuated by Core.
        /// </summary>
        /// <param name="key">Clip key.</param>
        /// <param name="coreGain">
        /// Linear gain computed from Core's connection graph. Clamped here rather than in
        /// Core because a negative or absurd gain is a rendering concern, and clamping a
        /// float is exactly the arithmetic this layer is allowed to do.
        /// </param>
        public void PlayAttenuated(string key, float coreGain, float pitch = 1f)
            => Play(key, Mathf.Clamp01(coreGain), pitch);

        /// <summary>Starts a looping layer and returns its source, e.g. an ambient bed.</summary>
        public AudioSource PlayLoop(string key, float volume = 1f)
        {
            var source = Rent();
            source.loop = true;

            if (_clips.TryGetValue(key, out AudioClip clip) && clip != null)
            {
                source.clip = clip;
                source.volume = Mathf.Clamp01(volume);
                source.Play();
            }

            return source;
        }

        /// <summary>Sets the volume of a looping layer.</summary>
        public void SetLoopVolume(AudioSource source, float volume)
        {
            if (source != null)
                source.volume = Mathf.Clamp01(volume);
        }

        /// <summary>Stops and returns a looping layer to the pool.</summary>
        public void StopLoop(AudioSource source) => Release(source);

        /// <summary>Stops everything.</summary>
        public void StopAll()
        {
            foreach (var source in _active.ToArray())
                Release(source);
        }

        private AudioSource Rent()
        {
            while (_pool.Count > 0)
            {
                var pooled = _pool.Pop();
                if (pooled == null)
                    continue;

                _active.Add(pooled);
                return pooled;
            }

            var go = new GameObject("AudioSource");
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f; // everything is 2.5D, heard through the cutaway.
            _active.Add(source);
            return source;
        }

        private void Release(AudioSource source)
        {
            if (source == null)
                return;

            _active.Remove(source);
            source.Stop();
            source.clip = null;
            source.loop = false;
            _pool.Push(source);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            StopAll();

            foreach (var source in _pool)
            {
                if (source != null)
                    Object.Destroy(source.gameObject);
            }

            _pool.Clear();
            MissingClip.Dispose();
        }
    }
}