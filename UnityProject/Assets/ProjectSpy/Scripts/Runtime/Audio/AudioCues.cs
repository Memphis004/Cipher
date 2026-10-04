using System;
using System.Collections.Generic;

namespace ProjectSpy.Unity.Audio
{
    /// <summary>
    /// Every clip key the game can ask for, in one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalogue is the contract; the files are an implementation detail.</b>
    /// Nothing in the game holds an <c>AudioClip</c> reference. Everything asks for a
    /// key, and a key that is not in this list has no clip, so the list is what
    /// "nothing is ever null" is measured against. Adding a cue means adding a line
    /// here first, which is why the placeholder generator can be checked against it:
    /// a cue added in code but not here is a cue that silently plays silence forever.
    /// </para>
    /// <para>
    /// <b>Why keys and not clips.</b> A clip reference in code is a GUID that a merge can
    /// break, an import that can fail, and an <c>AudioClip</c> that is null in a build
    /// where the asset was excluded. A string key that resolves to nothing logs once and
    /// is silent, which is a far better failure for a placeholder stage than a null
    /// reference in the middle of a firefight.
    /// </para>
    /// <para>
    /// <b>Why footsteps are per posture and not per surface.</b> Core models four
    /// postures and does not model floor surfaces yet — <c>noise_profile</c> has no
    /// surface column. Keying by surface now would mean a second, parallel idea of what
    /// the building is made of, owned by the presentation layer, that Core would have to
    /// learn about later. The keys are ready for a <c>.</c> suffix to be added when Core
    /// grows one.
    /// </para>
    /// </remarks>
    public static class AudioCues
    {
        // ------------------------------------------------------------------ footsteps

        /// <summary>Flat on the floor. Quietest, and the clip is nearly silent for that reason.</summary>
        public const string FootstepProne = "footstep.prone";

        /// <summary>Low and slow.</summary>
        public const string FootstepCrouch = "footstep.crouch";

        /// <summary>Ordinary walking pace.</summary>
        public const string FootstepWalk = "footstep.walk";

        /// <summary>Sprinting. Loudest.</summary>
        public const string FootstepRun = "footstep.run";

        // ------------------------------------------------------------------ world

        /// <summary>A door swinging open.</summary>
        public const string DoorOpen = "door.open";

        /// <summary>A door pulled shut.</summary>
        public const string DoorClose = "door.close";

        /// <summary>A door that will not open, because the alarm sealed it.</summary>
        public const string DoorLocked = "door.locked";

        /// <summary>A vent panel, which is how most missions get through a wall.</summary>
        public const string Vent = "vent";

        /// <summary>Working a terminal. Loops while hacking.</summary>
        public const string Hack = "hack";

        /// <summary>Squad radio chatter. Carries the squad's own voice over the scene.</summary>
        public const string RadioChatter = "radio.chatter";

        /// <summary>A body being dragged across a floor.</summary>
        public const string BodyDrag = "body.drag";

        // ------------------------------------------------------------------ violence

        /// <summary>A close-quarters strike.</summary>
        public const string Melee = "melee";

        /// <summary>A takedown: contact, and the specific sound of it going quiet.</summary>
        public const string Takedown = "takedown";

        /// <summary>A gunshot, and everything that follows from one.</summary>
        public const string Gunshot = "gunshot";

        // ------------------------------------------------------------------ voice

        /// <summary>Breathing, steady.</summary>
        public const string BreathCalm = "breath.calm";

        /// <summary>Breathing faster, as suspicion becomes attention.</summary>
        public const string BreathTense = "breath.tense";

        /// <summary>Breathing hard, at the point of being identified.</summary>
        public const string BreathPanic = "breath.panic";

        // ------------------------------------------------------------------ alarm

        /// <summary>
        /// The stinger for a band. Indexed by <see cref="ProjectSpy.Core.Tactical.AlarmBand"/>.
        /// </summary>
        /// <remarks>
        /// One per band rather than one generic alarm: the player's ear is the fastest
        /// channel this game has, and a sound that does not change when the building's
        /// state changes teaches the player to stop listening to it.
        /// </remarks>
        public const string AlarmStingerPrefix = "alarm.stinger.";

        /// <summary>The stinger key for a band.</summary>
        public static string AlarmStinger(ProjectSpy.Core.Tactical.AlarmBand band)
            => AlarmStingerPrefix + band.ToString().ToLowerInvariant();

        // ------------------------------------------------------------------ beds

        /// <summary>
        /// The ambient bed, one layer per band.
        /// </summary>
        /// <remarks>
        /// Layers rather than five separate beds because tension is cumulative: a player
        /// who has been in <c>Alert</c> and drops back to <c>Suspicious</c> should lose
        /// the top layer and keep the ones under it, which a crossfade between two beds
        /// cannot express.
        /// </remarks>
        public const string AmbientLayerPrefix = "ambient.layer.";

        /// <summary>The ambient bed layer key for a band.</summary>
        public static string AmbientLayer(ProjectSpy.Core.Tactical.AlarmBand band)
            => AmbientLayerPrefix + (int)band;

        /// <summary>
        /// Every bed layer that should be sounding at a band, lowest first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Tension accumulates, so the set is every layer up to and including this one. That
        /// is the whole reason for layers rather than one bed per band: a player who has
        /// been in Lockdown and drops to Alert should lose the top layer and keep the ones
        /// beneath it. A crossfade between two complete beds cannot express that — it can
        /// only say where you are now, not what you have been through.
        /// </para>
        /// <para>
        /// Enumerated from zero rather than "one below the current band" so that Calm means
        /// exactly one layer, not none. A mission that never escalates still gets a room
        /// tone, and dropping all the way to Calm is an audible event rather than dead air.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> LayersUpTo(ProjectSpy.Core.Tactical.AlarmBand band)
        {
            var layers = new List<string>((int)band + 1);

            for (int i = 0; i <= (int)band; i++)
                layers.Add(AmbientLayer((ProjectSpy.Core.Tactical.AlarmBand)i));

            return layers;
        }

        // ------------------------------------------------------------------ the list

        /// <summary>
        /// The Resources path a key's clip is loaded from.
        /// </summary>
        /// <remarks>
        /// Lives here rather than in the generator so the two cannot drift. A convention
        /// written twice fails as a missing clip at runtime — silent, and only in a build —
        /// rather than as a compile error, which is the worst way for a path to be wrong.
        /// </remarks>
        public static string ResourcePathFor(string key) => $"Audio/Clips/{key}";

        /// <summary>
        /// Every key, in one enumerable list.
        /// </summary>
        /// <remarks>
        /// Built by hand rather than reflected so that a key added to a constant but not
        /// to this list is a bug a test can catch, which is the entire reason the list
        /// exists.
        /// </remarks>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            FootstepProne, FootstepCrouch, FootstepWalk, FootstepRun,
            DoorOpen, DoorClose, DoorLocked,
            Vent, Hack, RadioChatter, BodyDrag,
            Melee, Takedown, Gunshot,
            BreathCalm, BreathTense, BreathPanic,
        };

        /// <summary>
        /// Every key including the per-band stingers and ambient layers.
        /// </summary>
        /// <remarks>
        /// What the generator must produce and what a test asserts coverage over. The
        /// band-derived keys are generated here from Core's enum rather than written out,
        /// so a sixth band would be covered by construction.
        /// </remarks>
        public static IReadOnlyList<string> AllIncludingBands()
        {
            var keys = new List<string>(All);

            foreach (ProjectSpy.Core.Tactical.AlarmBand band in Enum.GetValues(typeof(ProjectSpy.Core.Tactical.AlarmBand)))
            {
                keys.Add(AlarmStinger(band));
                keys.Add(AmbientLayer(band));
            }

            return keys;
        }

        /// <summary>
        /// The footstep key for a posture.
        /// </summary>
        /// <remarks>
        /// Total over the enum, defaulting to the crouch step for a value Core does not
        /// define. Quiet is the safe default: a posture the game has not heard of should
        /// not be the one that announces the player.
        /// </remarks>
        public static string Footstep(ProjectSpy.Core.Tactical.Posture posture) => posture switch
        {
            ProjectSpy.Core.Tactical.Posture.Prone => FootstepProne,
            ProjectSpy.Core.Tactical.Posture.Crouch => FootstepCrouch,
            ProjectSpy.Core.Tactical.Posture.Walk => FootstepWalk,
            ProjectSpy.Core.Tactical.Posture.Run => FootstepRun,
            _ => FootstepCrouch,
        };

        /// <summary>
        /// The breathing clip for how close the player is to being seen.
        /// </summary>
        /// <param name="perception">The highest perception level anyone currently has of the controlled agent.</param>
        public static string Breath(ProjectSpy.Core.Tactical.PerceptionLevel perception) => perception switch
        {
            ProjectSpy.Core.Tactical.PerceptionLevel.Identified => BreathPanic,
            ProjectSpy.Core.Tactical.PerceptionLevel.Noticed => BreathTense,
            _ => BreathCalm,
        };
    }
}