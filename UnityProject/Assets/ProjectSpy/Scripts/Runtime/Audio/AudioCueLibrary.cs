using System.Collections.Generic;
using UnityEngine;

namespace ProjectSpy.Unity.Audio
{
    /// <summary>
    /// Loads every key in <see cref="AudioCues"/> and registers it with the service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Loaded up front, not on demand.</b> A loader that fetched a clip the first time
    /// it was needed would move a <c>Resources.Load</c> — a disk read and an allocation —
    /// into the middle of the frame where a guard first sees the player. Loading the whole
    /// catalogue once at boot is a few dozen small reads before the mission starts, which
    /// is the only place that cost is cheap.
    /// </para>
    /// <para>
    /// <b>A missing clip is reported, not thrown.</b> <c>AudioService</c> already logs a
    /// missing key once and plays nothing; this counts them so the failure is visible as a
    /// number rather than only as a log line nobody reads.
    /// </para>
    /// </remarks>
    public static class AudioCueLibrary
    {
        /// <summary>
        /// Registers every catalogue key with a service.
        /// </summary>
        /// <returns>How many keys could not be loaded.</returns>
        public static int LoadInto(AudioService audio, IReadOnlyList<string> keys = null)
        {
            if (audio is null)
                throw new System.ArgumentNullException(nameof(audio));

            keys ??= AudioCues.AllIncludingBands();

            int missing = 0;

            foreach (string key in keys)
            {
                var clip = Resources.Load<AudioClip>(AudioCues.ResourcePathFor(key));

                if (clip == null)
                {
                    missing++;
                    continue;
                }

                audio.Register(key, clip);
            }

            return missing;
        }

        /// <summary>
        /// Loads the whole catalogue, logging if anything is absent.
        /// </summary>
        /// <remarks>
        /// A missing placeholder is a content bug with a one-line fix — run the generator —
        /// so it says which key and how to fix it rather than just reporting a count.
        /// </remarks>
        public static void LoadOrReport(AudioService audio)
        {
            int missing = LoadInto(audio);

            if (missing > 0)
            {
                Debug.LogWarning(
                    $"[ProjectSpy] {missing} audio cue(s) have no clip. Run " +
                    "ProjectSpy > Audio > Generate Placeholder Clips.");
            }
        }
    }
}