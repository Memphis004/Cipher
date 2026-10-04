using System.Collections.Generic;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Audio;
using ProjectSpy.Unity.Localisation;
using UnityEngine;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// How one alarm band looks and sounds.
    /// </summary>
    /// <remarks>
    /// A value, not a decision. <see cref="AlarmLooks.For"/> maps Core's band to a look;
    /// it never decides which band applies, and it never changes when the band changes.
    /// </remarks>
    public readonly struct AlarmLook
    {
        /// <summary>Which band this is.</summary>
        public AlarmBand Band { get; init; }

        /// <summary>
        /// The colour the building's own lights shift toward in this band.
        /// </summary>
        /// <remarks>
        /// Tinting the lights rather than grading the whole frame is the point: a light
        /// that turns red means the building's lighting changed, which is a different
        /// and more legible claim than "the screen looks red now". A global grade would
        /// tint the agents and the fog the same way, which would make a claim about the
        /// site look like a claim about the player.
        /// </remarks>
        public Color LightTint { get; init; }

        /// <summary>How far the lights shift, 0 to 1. Zero at Calm.</summary>
        public float LightTintStrength { get; init; }

        /// <summary>The vignette colour.</summary>
        public Color VignetteColour { get; init; }

        /// <summary>How heavy the vignette is, 0 to 1. Zero at Calm.</summary>
        public float VignetteStrength { get; init; }

        /// <summary>The one-line in-world message's localization key.</summary>
        public string MessageKey { get; init; }

        /// <summary>The stinger played on entering this band.</summary>
        public string StingerKey { get; init; }

        /// <summary>The ambient bed layer this band adds.</summary>
        public string AmbientLayerKey { get; init; }

        /// <inheritdoc/>
        public override string ToString()
            => $"{Band} tint {LightTintStrength:0.00} vignette {VignetteStrength:0.00}";
    }

    /// <summary>
    /// The five alarm bands, as presentation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Presentation only.</b> Nothing here re-derives what a band means. Core's
    /// <see cref="AlarmSystem"/> decides that Alert shuts doors and sends responders;
    /// this decides what Alert looks like. The moment a look disagreed with the rule it
    /// was drawn from, the player would be shown a building that has not caught up with
    /// the building they are in.
    /// </para>
    /// <para>
    /// <b>Total over the enum.</b> An unrecognised band resolves to Calm rather than
    /// throwing, and a test asserts that every value has a look. A band that Core adds
    /// should not be able to blank the screen.
    /// </para>
    /// <para>
    /// <b>Calm is a look too.</b> It is not "no look": it is the look with no tint and
    /// no vignette, which is the state the player needs to be able to see restored. A
    /// system that only reacted when something changed would leave the last band stuck on
    /// screen for the rest of the mission.
    /// </para>
    /// </remarks>
    public static class AlarmLooks
    {
        private static readonly Dictionary<AlarmBand, AlarmLook> Looks = new()
        {
            [AlarmBand.Calm] = new AlarmLook
            {
                Band = AlarmBand.Calm,
                LightTint = Color.white,
                LightTintStrength = 0f,
                VignetteColour = new Color(0.02f, 0.03f, 0.05f),
                VignetteStrength = 0f,
                MessageKey = AlarmStrings.BandCalm,
                StingerKey = AudioCues.AlarmStinger(AlarmBand.Calm),
                AmbientLayerKey = AudioCues.AmbientLayer(AlarmBand.Calm),
            },

            // Cold and barely there. Suspicion is a suspicion: the building has not yet
            // decided anything, and a look that shouts would spend the player's attention
            // that the band has not earned.
            [AlarmBand.Suspicious] = new AlarmLook
            {
                Band = AlarmBand.Suspicious,
                LightTint = new Color(0.72f, 0.84f, 1.00f),
                LightTintStrength = 0.18f,
                VignetteColour = new Color(0.10f, 0.18f, 0.30f),
                VignetteStrength = 0.22f,
                MessageKey = AlarmStrings.BandSuspicious,
                StingerKey = AudioCues.AlarmStinger(AlarmBand.Suspicious),
                AmbientLayerKey = AudioCues.AmbientLayer(AlarmBand.Suspicious),
            },

            // Amber. Doors are shut and pursuit is real, so this is the band the player
            // will be looking at most often and it has to be readable in one glance.
            [AlarmBand.Alert] = new AlarmLook
            {
                Band = AlarmBand.Alert,
                LightTint = new Color(1.00f, 0.72f, 0.36f),
                LightTintStrength = 0.42f,
                VignetteColour = new Color(0.34f, 0.18f, 0.04f),
                VignetteStrength = 0.42f,
                MessageKey = AlarmStrings.BandAlert,
                StingerKey = AudioCues.AlarmStinger(AlarmBand.Alert),
                AmbientLayerKey = AudioCues.AmbientLayer(AlarmBand.Alert),
            },

            // Red, and heavy. Responders are out and extractions are closing.
            [AlarmBand.Lockdown] = new AlarmLook
            {
                Band = AlarmBand.Lockdown,
                LightTint = new Color(1.00f, 0.40f, 0.34f),
                LightTintStrength = 0.62f,
                VignetteColour = new Color(0.40f, 0.06f, 0.06f),
                VignetteStrength = 0.62f,
                MessageKey = AlarmStrings.BandLockdown,
                StingerKey = AudioCues.AlarmStinger(AlarmBand.Lockdown),
                AmbientLayerKey = AudioCues.AmbientLayer(AlarmBand.Lockdown),
            },

            // Near-white and absolute. Burned means the mission is over whatever the
            // player wanted, and the frame should stop pretending otherwise.
            [AlarmBand.Burned] = new AlarmLook
            {
                Band = AlarmBand.Burned,
                LightTint = new Color(1.00f, 0.86f, 0.86f),
                LightTintStrength = 0.80f,
                VignetteColour = new Color(0.52f, 0.02f, 0.02f),
                VignetteStrength = 0.85f,
                MessageKey = AlarmStrings.BandBurned,
                StingerKey = AudioCues.AlarmStinger(AlarmBand.Burned),
                AmbientLayerKey = AudioCues.AmbientLayer(AlarmBand.Burned),
            },
        };

        /// <summary>
        /// The look for a band. Never throws.
        /// </summary>
        /// <remarks>
        /// A band Core defines and this table has not heard of renders as Calm. That is
        /// the safe direction: an unstyled band looks like an ordinary quiet building
        /// rather than like a broken one.
        /// </remarks>
        public static AlarmLook For(AlarmBand band)
            => Looks.TryGetValue(band, out AlarmLook look) ? look : Looks[AlarmBand.Calm];

        /// <summary>Every band Core defines.</summary>
        public static IReadOnlyCollection<AlarmBand> Bands => Looks.Keys;
    }

    /// <summary>
    /// The one-line messages the alarm shows, in both shipped languages.
    /// </summary>
    /// <remarks>
    /// Registered from code for the same reason <c>TacticalStrings</c> is: a fixed, tiny,
    /// presentation-only set. Each line names a consequence the player can verify against
    /// the building rather than restating the band — "Security sweep initiated" tells the
    /// player what kind of thing is happening to them, which a label reading "ALERT" does
    /// not.
    /// </remarks>
    public static class AlarmStrings
    {
        /// <summary>Nothing is happening.</summary>
        public const string BandCalm = "alarm.band.calm";

        /// <summary>Extra patrols.</summary>
        public const string BandSuspicious = "alarm.band.suspicious";

        /// <summary>Doors sealed.</summary>
        public const string BandAlert = "alarm.band.alert";

        /// <summary>Responders out.</summary>
        public const string BandLockdown = "alarm.band.lockdown";

        /// <summary>The mission is over.</summary>
        public const string BandBurned = "alarm.band.burned";

        /// <summary>The alarm banner's heading, so the player knows what they are reading.</summary>
        public const string BannerHeading = "alarm.banner.heading";

        private static readonly (string Key, string Thai, string English)[] Rows =
        {
            (BandCalm, "ระบบอยู่ในสถานะปกติ", "All quiet"),
            (BandSuspicious, "เริ่มมีการเดินตรวจเพิ่มเติม", "Security sweep initiated — patrols increased"),
            (BandAlert, "ปิดกั้นทางเข้าออกแล้ว", "Alert — stairwells sealed, pursuit active"),
            (BandLockdown, "ส่งกำลังเข้าระบบแล้ว", "Lockdown — responders inbound, extractions closing"),
            (BandBurned, "ภารกิจล้มเหลว ต้องออกจากพื้นที่ทันที", "Burned — the site knows. Extract now"),
            (BannerHeading, "ระดับสถานการณ์", "Security level"),
        };

        /// <summary>Adds every row to the localization service.</summary>
        public static void Register(LocalizationService localization)
        {
            if (localization is null)
                return;

            foreach (var row in Rows)
            {
                localization.Add(row.Key, UiLanguage.Thai, row.Thai);
                localization.Add(row.Key, UiLanguage.English, row.English);
            }
        }
    }
}