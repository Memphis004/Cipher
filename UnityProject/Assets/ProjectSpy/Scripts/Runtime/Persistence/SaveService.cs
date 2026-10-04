using System;
using System.Collections.Generic;
using System.IO;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using UnityEngine;

namespace ProjectSpy.Unity.Persistence
{
    /// <summary>
    /// Reads and writes save files, and owns where they live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it serialises is Core's decision.</b> This service moves bytes and names
    /// files; it never decides what is in a save. Core's <see cref="WorldStateSerializer"/>
    /// produces the canonical form and its digest, and the digest is stored alongside so a
    /// load can refuse a file that was truncated or edited rather than restoring a world
    /// that never existed.
    /// </para>
    /// <para>
    /// <b>Autosave slots.</b> The policy the brief specifies — every in-game day in strategic
    /// mode, every two in-game minutes in tactical, five rotating slots plus manual plus one
    /// ironman slot — is expressed as named slots rather than a single "save.dat", so a
    /// corrupt autosave costs one day rather than the campaign.
    /// </para>
    /// <para>
    /// <b>Not yet: a full world round trip.</b> Core currently exposes a canonical
    /// serialiser and a digest, and can rebuild a <see cref="SiteLayout"/> from save data,
    /// but has no deserialiser that reconstructs a <see cref="WorldState"/>. Rather than
    /// invent a Unity-side format that would have to be thrown away when Core gains one,
    /// <see cref="TryLoadWorld"/> reports that honestly. See SETUP.md.
    /// </para>
    /// </remarks>
    public sealed class SaveService : Services.IProjectSpyService
    {
        /// <summary>Folder save files live in, under <c>Application.persistentDataPath</c>.</summary>
        public const string FolderName = "ProjectSpy";

        /// <summary>How many rotating autosave slots exist.</summary>
        public const int AutosaveSlotCount = 5;

        private readonly string _root;

        /// <summary>Creates the service and ensures the save folder exists.</summary>
        public SaveService()
        {
            _root = Path.Combine(Application.persistentDataPath, FolderName);
            Directory.CreateDirectory(_root);
        }

        /// <summary>Full path of the save directory.</summary>
        public string RootDirectory => _root;

        /// <summary>
        /// Writes a world's canonical bytes and its digest.
        /// </summary>
        /// <returns>The file written.</returns>
        public string SaveWorld(string slotName, WorldState world)
        {
            if (world is null)
                throw new ArgumentNullException(nameof(world));

            byte[] bytes = WorldStateSerializer.ToCanonicalBytes(world);
            ulong digest = WorldStateSerializer.ComputeCanonicalDigest(world);

            string path = PathFor(slotName);
            File.WriteAllBytes(path, bytes);

            // The digest sits beside the payload rather than inside it, because Core owns the
            // payload format and adding a header here would make this file unreadable by
            // anything but this class.
            File.WriteAllText(path + ".digest", digest.ToString("X16"));

            Debug.Log($"[ProjectSpy] Saved '{slotName}' ({bytes.Length} bytes, digest {digest:X16}).");
            return path;
        }

        /// <summary>
        /// Verifies a save file's digest without deserialising it.
        /// </summary>
        /// <remarks>
        /// Useful on its own: it is the check that runs at boot over every slot, so a
        /// truncated autosave is noticed before the player selects it rather than after.
        /// </remarks>
        public bool TryVerifySlot(string slotName)
        {
            string path = PathFor(slotName);
            if (!File.Exists(path) || !File.Exists(path + ".digest"))
                return false;

            try
            {
                string recorded = File.ReadAllText(path + ".digest").Trim();
                string actual = ComputeDigestOfFile(path);
                return string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reconstructs a world from a save.
        /// </summary>
        /// <returns>False, always, until Core exposes a deserialiser.</returns>
        /// <remarks>
        /// Written as an explicit refusal rather than an empty body because knowledge.md rule
        /// 5 forbids a stub that looks finished. The honest position is that the format and
        /// the migrations belong to Core, and reconstructing a world in Unity would create a
        /// second format to maintain and then delete.
        /// </remarks>
        public bool TryLoadWorld(string slotName, out WorldState world)
        {
            world = null;

            if (!TryVerifySlot(slotName))
                return false;

            Debug.LogWarning(
                "[ProjectSpy] TryLoadWorld: the save verifies but Core has no world " +
                "deserialiser yet. TODO(stage-9b): Core needs FromCanonicalBytes before a " +
                "Unity build can resume a campaign. SiteLayout round trips today via " +
                "SiteLayout.FromSaveData.");
            return false;
        }

        /// <summary>Slot names the autosave rotation cycles through.</summary>
        public IEnumerable<string> AutosaveSlots()
        {
            for (int i = 0; i < AutosaveSlotCount; i++)
                yield return $"autosave_{i}";
        }

        /// <summary>The single slot ironman mode writes to, kept apart on purpose.</summary>
        public const string IronmanSlot = "ironman";

        /// <summary>Full path for a slot.</summary>
        public string PathFor(string slotName) => Path.Combine(_root, slotName + ".psav");

        /// <summary>
        /// FNV-1a over the file's bytes.
        /// </summary>
        /// <remarks>
        /// A plain FNV-1a rather than a cryptographic digest: the purpose is detecting
        /// truncation and casual corruption, not defending against an attacker who can
        /// already write to the save directory.
        /// </remarks>
        private static string ComputeDigestOfFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            ulong hash = 14695981039346656037UL;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }

            return hash.ToString("X16");
        }
    }
}