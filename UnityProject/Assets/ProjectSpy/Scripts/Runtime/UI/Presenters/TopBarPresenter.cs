using System;
using System.Collections.Generic;
using ProjectSpy.Core;
using HeatTierRow = ProjectSpy.Tables.HeatTier;

namespace ProjectSpy.Unity.UI.Presenters
{
    /// <summary>
    /// Projects the top bar: clock, speed, resources and heat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and static. Every figure it shows is read from Core — the day and week from
    /// the clock's <see cref="Tick"/>, the resources from the <see cref="Resources"/>
    /// record, the weekly delta from <see cref="Resources.ProjectedWeeklyCost"/>. Nothing
    /// here recomputes a Core number, and the heat curve below is a <em>colour</em>
    /// function, not a second heat model: it takes Core's heat value as given.
    /// </para>
    /// <para>
    /// <b>The weekly delta is a projection, and is framed as one.</b>
    /// <see cref="Resources.ProjectedWeeklyCost"/> is Core's arithmetic over the roster, so
    /// the figure is honest, but it is still a projection — the player will earn money this
    /// week too. Showing it without that framing would make a solvent base look broke.
    /// </para>
    /// </remarks>
    public static class TopBarPresenter
    {
        /// <summary>How the heat gauge presents a given heat value.</summary>
        public readonly struct HeatGauge
        {
            /// <summary>Builds a gauge readout.</summary>
            public HeatGauge(int heat, float normalised, string labelKey)
            {
                Heat = heat;
                Normalised = normalised;
                LabelKey = labelKey;
            }

            /// <summary>Core's heat value.</summary>
            public int Heat { get; }

            /// <summary>Fill fraction, 0..1.</summary>
            public float Normalised { get; }

            /// <summary>Localization key for the tier name.</summary>
            public string LabelKey { get; }

            /// <summary>True when the presentation should be visibly dark.</summary>
            /// <remarks>
            /// The brief asks for the gauge to "visibly darken as it climbs". A colour ramp
            /// alone reads as a lighting change the player cannot attribute; the label does
            /// the work and the darkening supports it.
            /// </remarks>
            public bool IsDark => Normalised >= HeatDarkThreshold;

            private const float HeatDarkThreshold = 0.6f;
        }

        /// <summary>
        /// Where the heat gauge currently sits.
        /// </summary>
        /// <remarks>
        /// <b>Normalised against Core's own heat tiers, not a guessed maximum.</b> The
        /// tables define the bands, so the gauge reaching full at the top of the highest
        /// band means "as bad as the game says it can get", and a balance change moves the
        /// gauge with it. A hardcoded ceiling would be a UI-side opinion about when Heat is
        /// maxed, which is Core's.
        /// </remarks>
        public static HeatGauge Heat(Resources resources)
        {
            if (resources is null) throw new ArgumentNullException(nameof(resources));

            int heat = Math.Max(0, resources.Heat);
            IReadOnlyList<HeatTierRow> tiers = SimulationRules.HeatTiers();

            int ceiling = CeilingOf(tiers);
            if (ceiling <= 0)
                ceiling = Math.Max(1, heat);

            return new HeatGauge(
                heat: heat,
                normalised: Math.Clamp((float)heat / ceiling, 0f, 1f),
                labelKey: TierKeyFor(heat, tiers));
        }

        /// <summary>
        /// The top of the highest heat tier.
        /// </summary>
        /// <remarks>
        /// Falls back to the current heat when the tiers table is empty, so the gauge still
        /// renders something sensible on a fresh save with no tables loaded instead of
        /// dividing by zero and drawing NaN.
        /// </remarks>
        private static int CeilingOf(IReadOnlyList<HeatTierRow>? tiers)
        {
            if (tiers is null) return 0;

            int ceiling = 0;
            foreach (HeatTierRow tier in tiers)
            {
                if (tier.Threshold > ceiling)
                    ceiling = tier.Threshold;
            }

            return ceiling;
        }

        private static string TierKeyFor(int heat, IReadOnlyList<HeatTierRow>? tiers)
        {
            if (tiers is null || tiers.Count == 0)
                return "heat.tier.unknown";

            // heat_tier has no name column, so the band is identified by its position in
            // the ascending list. That is stable: Core sorts the rows by Threshold, and a
            // new row inserted mid-table shifts the index below it — which is why the key
            // is derived here rather than trusted from the table to be self-describing.
            for (int i = 0; i < tiers.Count; i++)
            {
                if (heat <= tiers[i].Threshold)
                    return $"heat.tier.{i}";
            }

            // Above the highest declared threshold: hotter than the table describes.
            return "heat.tier.overflow";
        }

        /// <summary>Funds with the weekly net delta, as the bar shows them.</summary>
        public readonly struct FundsReadout
        {
            /// <summary>Builds a funds readout.</summary>
            public FundsReadout(long funds, long weeklyNet)
            {
                Funds = funds;
                WeeklyNet = weeklyNet;
            }

            /// <summary>Current funds.</summary>
            public long Funds { get; }

            /// <summary>This week's projected net. Negative when the base costs money.</summary>
            public long WeeklyNet { get; }

            /// <summary>True when the delta should draw in the gain colour.</summary>
            public bool IsGain => MoneyFormat.IsGain(WeeklyNet);

            /// <summary>True when the delta should draw in the loss colour.</summary>
            public bool IsLoss => MoneyFormat.IsLoss(WeeklyNet);

            /// <summary>Localization key for the delta's sign word.</summary>
            public string DeltaKey => IsGain ? "topbar.delta.gain" : "topbar.delta.loss";
        }

        /// <summary>
        /// The funds figure and this week's net.
        /// </summary>
        /// <remarks>
        /// <b>The net is negative whenever the base costs money to run.</b> A weekly salary
        /// bill with no contract income is the normal state of a base between contracts, so
        /// a permanently red delta is not alarming on its own — the UI pairs it with the
        /// absolute figure so the player can see whether they can afford to run out.
        /// </remarks>
        public static FundsReadout Funds(Resources resources, long weeklyCost)
        {
            if (resources is null) throw new ArgumentNullException(nameof(resources));

            return new FundsReadout(resources.Funds, -weeklyCost);
        }

        /// <summary>
        /// The weekly net for the roster, asked of Core.
        /// </summary>
        /// <remarks>
        /// The roster is passed in rather than read from a world, so this stays pure and
        /// testable. The cost itself is
        /// <see cref="Resources.ProjectedWeeklyCost(IEnumerable{Agent})"/> — Core's
        /// arithmetic over Core's own rules.
        /// </remarks>
        public static FundsReadout FundsFor(Resources resources, IEnumerable<Agent>? roster)
        {
            if (resources is null) throw new ArgumentNullException(nameof(resources));

            return Funds(resources, resources.ProjectedWeeklyCost(roster ?? Array.Empty<Agent>()));
        }

        /// <summary>The clock line: day, week and weekday.</summary>
        public readonly struct ClockReadout
        {
            /// <summary>Builds a clock readout.</summary>
            public ClockReadout(int day, int week, int dayOfWeek)
            {
                Day = day;
                Week = week;
                DayOfWeek = dayOfWeek;
            }

            /// <summary>Core's day count.</summary>
            public int Day { get; }

            /// <summary>Core's week count.</summary>
            public int Week { get; }

            /// <summary>Zero-based day within the week, from Core.</summary>
            public int DayOfWeek { get; }

            /// <summary>Localization key for the weekday.</summary>
            /// <remarks>
            /// Clamped to the seven the localization table defines. A day count that ran
            /// past week 7 would otherwise ask for a weekday the table has never heard of,
            /// and every such lookup renders as its own key.
            /// </remarks>
            public string WeekdayKey => $"ui.weekday.{Math.Clamp(DayOfWeek, 0, 6)}";

            /// <summary>Localization key for the date line.</summary>
            public string DateKey => "ui.topbar.date";
        }

        /// <summary>
        /// Builds the clock readout from Core's clock.
        /// </summary>
        /// <remarks>
        /// The week and weekday are asked of Core's <see cref="Tick"/>, which already
        /// computes both from its own calendar constant. Recomputing them here from the day
        /// count would be a second definition of when a week ends, and the two would
        /// disagree the moment Core changed its calendar — which would show the wrong
        /// weekday on the day the week turns over.
        /// </remarks>
        public static ClockReadout Clock(StrategicClock clock)
        {
            if (clock is null) throw new ArgumentNullException(nameof(clock));

            Tick now = clock.Current;
            return new ClockReadout(now.Day, now.Week, now.DayOfWeek);
        }

        /// <summary>One speed button on the top bar.</summary>
        public readonly struct SpeedButton
        {
            /// <summary>Builds a speed button.</summary>
            public SpeedButton(StrategicTimeScale speed, bool isActive)
            {
                Speed = speed;
                IsActive = isActive;
            }

            /// <summary>The speed this button selects.</summary>
            public StrategicTimeScale Speed { get; }

            /// <summary>Whether this is the current speed.</summary>
            public bool IsActive { get; }

            /// <summary>Localization key for this speed's label.</summary>
            public string LabelKey => $"ui.speed.{Speed.ToString().ToLowerInvariant()}";

            /// <summary>Localization key for this speed's keyboard binding.</summary>
            public string BindingKey => BindingKeyFor(Speed);
        }

        /// <summary>
        /// The speed buttons, in Core's own order.
        /// </summary>
        /// <remarks>
        /// <b>Pause first, then ascending.</b> The set is spelled out rather than derived by
        /// casting, because Core's <see cref="StrategicTimeScale"/> values are 0, 1, 4, 16
        /// and 64 — the gaps are real multipliers, not missing entries, so an ordinal walk
        /// would produce buttons for 2 and 3 that Core has never heard of.
        /// </remarks>
        public static IReadOnlyList<SpeedButton> Speeds(StrategicTimeScale current)
        {
            var buttons = new List<SpeedButton>(5)
            {
                new(StrategicTimeScale.Pause, current == StrategicTimeScale.Pause),
                new(StrategicTimeScale.Normal, current == StrategicTimeScale.Normal),
                new(StrategicTimeScale.Fast, current == StrategicTimeScale.Fast),
                new(StrategicTimeScale.Faster, current == StrategicTimeScale.Faster),
                new(StrategicTimeScale.Fastest, current == StrategicTimeScale.Fastest),
            };

            return buttons;
        }

        /// <summary>
        /// The localization key naming a speed's keyboard binding.
        /// </summary>
        /// <remarks>
        /// A key name, not a Unity <c>KeyCode</c>. The rebinding system owns what key does
        /// what, and a UI string naming a key is a claim that goes stale the moment the
        /// player rebinds. The binding service resolves the name when the tooltip opens.
        /// </remarks>
        public static string BindingKeyFor(StrategicTimeScale speed) => speed switch
        {
            StrategicTimeScale.Pause => "ui.key.pause",
            StrategicTimeScale.Normal => "ui.key.speed_1",
            StrategicTimeScale.Fast => "ui.key.speed_2",
            StrategicTimeScale.Faster => "ui.key.speed_3",
            StrategicTimeScale.Fastest => "ui.key.speed_4",
            _ => "ui.key.speed_unknown",
        };
    }
}
