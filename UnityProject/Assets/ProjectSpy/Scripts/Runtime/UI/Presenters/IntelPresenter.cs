using System;
using System.Collections.Generic;
using ProjectSpy.Core;

namespace ProjectSpy.Unity.UI.Presenters
{
    /// <summary>
    /// Projects the Sleeper Operations screen from Core's own per-operation view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost nothing is computed here. <see cref="SleeperOperationView"/> already carries
    /// the progress, the elapsed and remaining ticks, the per-tick discovery risk and the
    /// next band's contents — all produced by Core, all decided by Core. This class chooses
    /// which of those to show and supplies localization keys.
    /// </para>
    /// <para>
    /// <b>Poisoned operations are shown as unreported.</b> The brief is explicit:
    /// "discovered operations show their status; poisoned ones do not". The status
    /// therefore depends on what the player knows rather than on Core's internal truth, so
    /// a poisoned operation renders as "still running, nothing to report" — which is what
    /// is true from the player's side.
    /// </para>
    /// </remarks>
    public static class IntelPresenter
    {
        /// <summary>One row of the Sleeper Operations list.</summary>
        public readonly struct SleeperRow
        {
            /// <summary>Builds a row.</summary>
            public SleeperRow(
                AgentId agentId,
                int siteId,
                int siteTier,
                int intelPercent,
                string bandKey,
                string statusKey,
                int elapsedTicks,
                int estimatedRemainingTicks,
                int discoveryRiskPercent,
                bool canRecall,
                bool isDiscovered,
                bool isPoisoned,
                string nextBandKey,
                int percentToNextBand,
                IReadOnlyList<string> nextBandUnlocks)
            {
                AgentId = agentId;
                SiteId = siteId;
                SiteTier = siteTier;
                IntelPercent = intelPercent;
                BandKey = bandKey;
                StatusKey = statusKey;
                ElapsedTicks = elapsedTicks;
                EstimatedRemainingTicks = estimatedRemainingTicks;
                DiscoveryRiskPercent = discoveryRiskPercent;
                CanRecall = canRecall;
                IsDiscovered = isDiscovered;
                IsPoisoned = isPoisoned;
                NextBandKey = nextBandKey;
                PercentToNextBand = percentToNextBand;
                NextBandUnlocks = nextBandUnlocks ?? Array.Empty<string>();
            }

            /// <summary>The agent running the operation.</summary>
            public AgentId AgentId { get; }

            /// <summary>The site.</summary>
            public int SiteId { get; }

            /// <summary>The site's tier.</summary>
            public int SiteTier { get; }

            /// <summary>Intel gathered, 0..100.</summary>
            public int IntelPercent { get; }

            /// <summary>Localization key for the current intel band.</summary>
            public string BandKey { get; }

            /// <summary>Localization key for the status the player can see.</summary>
            public string StatusKey { get; }

            /// <summary>Ticks Core has been running.</summary>
            public int ElapsedTicks { get; }

            /// <summary>Core's own estimate of the ticks still to run.</summary>
            public int EstimatedRemainingTicks { get; }

            /// <summary>Core's discovery risk per tick, as a percentage.</summary>
            public int DiscoveryRiskPercent { get; }

            /// <summary>Whether Core allows a recall right now.</summary>
            public bool CanRecall { get; }

            /// <summary>Whether the player knows this operation's real status.</summary>
            public bool IsDiscovered { get; }

            /// <summary>Whether Core considers this operation poisoned.</summary>
            public bool IsPoisoned { get; }

            /// <summary>Localization key for the next band, empty at complete.</summary>
            public string NextBandKey { get; }

            /// <summary>Intel percent still needed for the next band.</summary>
            public int PercentToNextBand { get; }

            /// <summary>Localization keys for what the next band unlocks.</summary>
            public IReadOnlyList<string> NextBandUnlocks { get; }

            /// <summary>
            /// True when the next tick could expose the operation.
            /// </summary>
            /// <remarks>
            /// Reported per tick because that is the unit the player thinks in when
            /// deciding whether to speed the clock up. A per-week figure would be a
            /// different decision, and Core does not publish one.
            /// </remarks>
            public bool IsAtRisk => DiscoveryRiskPercent > 0;

            /// <summary>Progress through the current band, clamped to 0..100.</summary>
            public int ProgressPercent => Math.Clamp(IntelPercent, 0, 100);
        }

        /// <summary>
        /// Builds one row from Core's view.
        /// </summary>
        /// <remarks>
        /// <paramref name="discovered"/> is the player's knowledge, not the operation's
        /// state. Keeping them as separate inputs is what makes the poisoned case
        /// expressible: Core knows the operation is poisoned, the player does not, and this
        /// method is told only what the player knows.
        /// </remarks>
        public static SleeperRow Row(SleeperOperationView view, bool discovered)
        {
            if (view is null) throw new ArgumentNullException(nameof(view));

            bool poisoned = !discovered && view.Status == SleeperStatus.Poisoned;

            return new SleeperRow(
                agentId: view.AgentId,
                siteId: view.SiteId,
                siteTier: view.SiteTier,
                intelPercent: view.IntelPercent,
                bandKey: BandKeyFor(view.Band),
                statusKey: StatusKeyFor(view.Status, discovered, poisoned),
                elapsedTicks: view.ElapsedTicks,
                estimatedRemainingTicks: view.EstimatedRemainingTicks,
                discoveryRiskPercent: view.DiscoveryRiskPerTickPercent,
                canRecall: view.CanRecall,
                isDiscovered: discovered,
                isPoisoned: poisoned,
                nextBandKey: view.NextBand.HasValue ? BandKeyFor(view.NextBand.Value) : string.Empty,
                percentToNextBand: view.PercentToNextBand,
                nextBandUnlocks: UnlocksOf(view.NextBandContents));
        }

        /// <summary>Builds every row, in Core's order.</summary>
        /// <param name="views">Core's views, or null.</param>
        /// <param name="isDiscovered">
        /// Whether the player knows each operation's real status. Null treats every
        /// operation as undiscovered, which is the safe default.
        /// </param>
        public static IReadOnlyList<SleeperRow> Rows(
            IReadOnlyList<SleeperOperationView>? views,
            Func<AgentId, bool>? isDiscovered)
        {
            var rows = new List<SleeperRow>();
            if (views is null) return rows;

            foreach (SleeperOperationView view in views)
            {
                bool discovered = isDiscovered is not null && isDiscovered(view.AgentId);
                rows.Add(Row(view, discovered));
            }

            return rows;
        }

        /// <summary>Localization key for an intel band.</summary>
        public static string BandKeyFor(IntelBand band)
            => $"intel.band.{band.ToString().ToLowerInvariant()}";

        /// <summary>
        /// What the next band will unlock, as localization keys.
        /// </summary>
        /// <remarks>
        /// Core's <see cref="IntelBandContents"/> is a set of flags; this turns each set flag
        /// into the key the UI resolves. Empty when the band reveals nothing, or when the
        /// operation is already complete and there is no next band.
        /// </remarks>
        private static IReadOnlyList<string> UnlocksOf(IntelBandContents? contents)
        {
            var keys = new List<string>();
            if (contents is null) return keys;

            if (contents.RevealsEntrance) keys.Add("intel.unlock.entrance");
            if (contents.RevealsFloorCount) keys.Add("intel.unlock.floor_count");
            if (contents.RevealsRoomPlacement) keys.Add("intel.unlock.room_placement");
            if (contents.RevealsRoomTypes) keys.Add("intel.unlock.room_types");
            if (contents.RevealsConnections) keys.Add("intel.unlock.connections");
            if (contents.RevealsLockStates) keys.Add("intel.unlock.lock_states");
            if (contents.RevealsGuardCounts) keys.Add("intel.unlock.guard_counts");
            if (contents.RevealsLightLevels) keys.Add("intel.unlock.light_levels");
            if (contents.RevealsPatrolRoutes) keys.Add("intel.unlock.patrol_routes");
            if (contents.RevealsObjective) keys.Add("intel.unlock.objective");
            if (contents.RevealsExtraction) keys.Add("intel.unlock.extraction");
            if (contents.RevealsHoldingRooms) keys.Add("intel.unlock.holding_rooms");

            // MayBeStale is not an unlock, it is a caveat on everything above it, so it is
            // reported separately rather than as one more thing the band reveals.
            if (contents.MayBeStale) keys.Add("intel.unlock.may_be_stale");

            return keys;
        }

        /// <summary>
        /// The status a row shows, given what the player knows.
        /// </summary>
        /// <remarks>
        /// The poisoned branch is the whole point of this method. A poisoned operation
        /// reports "running, nothing to report" — which is what the player can actually
        /// observe — rather than Core's internal truth.
        /// </remarks>
        private static string StatusKeyFor(SleeperStatus status, bool discovered, bool poisoned)
        {
            if (poisoned || !discovered)
                return "sleeper.status.unreported";

            return status switch
            {
                SleeperStatus.Inserting => "sleeper.status.inserting",
                SleeperStatus.Embedded => "sleeper.status.embedded",
                SleeperStatus.Discovered => "sleeper.status.discovered",
                SleeperStatus.Burned => "sleeper.status.burned",
                SleeperStatus.Extracted => "sleeper.status.extracted",
                SleeperStatus.Poisoned => "sleeper.status.poisoned",
                _ => "sleeper.status.unreported",
            };
        }

        /// <summary>
        /// The next band and what it unlocks, for the band preview panel.
        /// </summary>
        public readonly struct BandPreview
        {
            /// <summary>Builds a preview.</summary>
            public BandPreview(
                string nextBandKey,
                int percentToNextBand,
                int estimatedTicksToNextBand,
                IReadOnlyList<string> unlocks)
            {
                NextBandKey = nextBandKey;
                PercentToNextBand = percentToNextBand;
                EstimatedTicksToNextBand = estimatedTicksToNextBand;
                Unlocks = unlocks ?? Array.Empty<string>();
            }

            /// <summary>Localization key for the next band, empty at complete.</summary>
            public string NextBandKey { get; }

            /// <summary>Intel percent still needed.</summary>
            public int PercentToNextBand { get; }

            /// <summary>Core's own estimate of the ticks still to run.</summary>
            public int EstimatedTicksToNextBand { get; }

            /// <summary>What the next band reveals.</summary>
            public IReadOnlyList<string> Unlocks { get; }

            /// <summary>
            /// True when this operation has nothing further to reveal.
            /// </summary>
            /// <remarks>
            /// The panel hides its progress bar in this case. A bar sitting at 100% with
            /// no destination reads as "almost done", which is the opposite of true.
            /// </remarks>
            public bool IsComplete => Unlocks.Count == 0;
        }

        /// <summary>Builds the band preview from Core's view.</summary>
        public static BandPreview Preview(SleeperOperationView view)
        {
            if (view is null) throw new ArgumentNullException(nameof(view));

            return new BandPreview(
                nextBandKey: view.NextBand.HasValue ? BandKeyFor(view.NextBand.Value) : string.Empty,
                percentToNextBand: view.PercentToNextBand,
                estimatedTicksToNextBand: view.EstimatedTicksToNextBand,
                unlocks: UnlocksOf(view.NextBandContents));
        }

        /// <summary>
        /// Whether recalling an operation would cost most of what it has gathered.
        /// </summary>
        /// <remarks>
        /// Reported rather than decided, and the caller supplies the resume cost as a
        /// percentage so this presenter holds no policy of its own. The player is allowed to
        /// recall a nearly-complete operation if they want the agent back, and telling them
        /// in words they cannot override that it was a mistake would be worse than showing
        /// them the number.
        /// </remarks>
        public static bool RecallIsCostly(SleeperOperationView view, int resumeCostPercent)
        {
            if (view is null) throw new ArgumentNullException(nameof(view));

            return view.IsCompleteIntel ||
                   view.RecallKeepsIntelPercent * resumeCostPercent / 100 > view.RecallKeepsIntelPercent / 2;
        }
    }
}
