using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectSpy.Unity.Audio;
using UnityEditor;
using UnityEngine;

namespace ProjectSpy.Unity.EditorTools
{
    /// <summary>
    /// Writes a silent WAV for every key in <see cref="AudioCues"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why generate instead of shipping silence.</b> <c>AudioService</c> already logs a
    /// missing clip once and plays nothing rather than throwing, so a missing file is not
    /// a crash — but it is a sound that never arrives, and at this stage there are dozens
    /// of cues wired to nothing. Generating the placeholders makes "nothing is ever null"
    /// structural: every key in the catalogue has a real, loadable asset behind it, and
    /// the failure mode of forgetting to author audio becomes an obviously silent build
    /// rather than a null reference somewhere in a firefight.
    /// </para>
    /// <para>
    /// <b>Why WAV written by hand.</b> <c>AudioClip</c> cannot be saved without leaving
    /// the Editor, and a generated asset that only existed in memory would be null again
    /// the next time the project was opened. The bytes go to disk as a normal .wav so the
    /// import pipeline owns them from then on.
    /// </para>
    /// <para>
    /// <b>Why the clips are not all the same length.</b> A looped bed that is a quarter of
    /// a second long will click audibly the moment anything is layered on top of it, which
    /// defeats the point of generating it. Looping cues get a couple of seconds so that a
    /// later pass can crossfade them; one-shots stay short so the project does not fill
    /// with megabytes of silence.
    /// </para>
    /// </remarks>
    public static class PlaceholderAudioGenerator
    {
        /// <remarks>
        /// Under <c>Resources</c> because the game loads clips by key at runtime and the
        /// tactical scene builds its own audio service rather than taking one from a DI
        /// container. Same reason the fonts live where they do.
        /// </remarks>
        private const string OutputFolder = "Assets/ProjectSpy/Resources/Audio/Clips";

        private const int SampleRate = 44100;
        private const short BitsPerSample = 16;
        private const short Channels = 1;

        private const float OneShotSeconds = 0.25f;
        private const float LoopSeconds = 2f;

        /// <summary>Cue keys that are meant to loop, and so get a longer placeholder.</summary>
        private static bool IsLooped(string key) => key.StartsWith(AudioCues.AmbientLayerPrefix);

        /// <summary>Menu entry: writes every missing placeholder clip.</summary>
        [MenuItem("ProjectSpy/Audio/Generate Placeholder Clips")]
        public static void GeneratePlaceholders()
        {
            int written = GeneratePlaceholders(AudioCues.AllIncludingBands());
            Debug.Log($"[ProjectSpy] Placeholder audio: {written} clip(s) written to {OutputFolder}.");
        }

        /// <summary>
        /// Writes a silent clip for every key that does not already have one.
        /// </summary>
        /// <returns>How many files were written.</returns>
        public static int GeneratePlaceholders(IReadOnlyList<string> keys)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            EnsureFolder(OutputFolder);
            EnsureFolder($"{OutputFolder}/footstep");
            EnsureFolder($"{OutputFolder}/door");
            EnsureFolder($"{OutputFolder}/alarm");
            EnsureFolder($"{OutputFolder}/ambient.layer");

            int written = 0;

            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                string path = $"{OutputFolder}/{PathFor(key)}";

                // Never overwrite real audio. A designer who has authored a clip must not
                // lose it to a menu item about placeholders.
                if (File.Exists(path))
                    continue;

                File.WriteAllBytes(path, SilentWav(IsLooped(key) ? LoopSeconds : OneShotSeconds));
                written++;
            }

            AssetDatabase.Refresh();
            return written;
        }

        private static string PathFor(string key) => key + ".wav";

        private static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;

            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            string leaf = Path.GetFileName(assetFolder);

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// Builds a silent mono 16-bit PCM WAV of a given length.
        /// </summary>
        /// <remarks>
        /// A hand-built RIFF rather than <c>AudioClip.EncodeToWav</c> because that needs a
        /// live <c>AudioClip</c>, and creating one requires the audio system this class
        /// exists to stand in for. The format is the simplest one Unity imports without
        /// asking questions: PCM, one channel, 16-bit.
        /// </remarks>
        public static byte[] SilentWav(float seconds)
        {
            int samples = Mathf.Max(1, Mathf.RoundToInt(SampleRate * Mathf.Max(0.01f, seconds)));
            int dataBytes = samples * Channels * (BitsPerSample / 8);

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII);

            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataBytes);          // Everything after this field.
            writer.Write(new[] { 'W', 'A', 'V', 'E' });

            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);                      // PCM header length.
            writer.Write((short)1);                // Format 1: uncompressed PCM.
            writer.Write(Channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * Channels * (BitsPerSample / 8)); // Byte rate.
            writer.Write((short)(Channels * (BitsPerSample / 8)));     // Block align.
            writer.Write(BitsPerSample);

            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataBytes);

            // Silence is not zero-length silence. A clip with a sample count of zero is a
            // degenerate asset that some platforms refuse to play at all, which would put
            // the null back in through a different door.
            writer.Write(new byte[dataBytes]);

            writer.Flush();
            return stream.ToArray();
        }
    }
}