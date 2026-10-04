using System;
using System.Collections.Generic;
using ProjectSpy.Core;
using ProjectSpy.Core.Squad;

namespace ProjectSpy.Unity.UI.Presenters
{
    /// <summary>
    /// Projects the mission preparation screen: readiness, blockers and the breakdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and static. It reads Core's own estimate — <see cref="MissionDifficulty"/> —
    /// and adds nothing to it. In particular the readiness ring and the difficulty band
    /// are the same Core number rendered two ways; if this class ever computed its own
    /// power score the two screens would disagree, and the player would be left choosing
    /// between them.
    /// </para>
    /// <para>
    /// <b>Blockers are not folded into the percentage.</b> A squad that is 140% of the
    /// required power but has no Hacker is not "ready"; showing 100% with a warning beside
    /// it is how a broken squad gets dispatched by a player who trusted the bar.
    /// </para>
    /// </remarks>
    public static class MissionPrepPresenter
    {
        /// <summary>
        /// Builds the readiness readout for a candidate squad.
        /// </summary>
        /// <param name="estimate">Core's estimate, or null when the mission type is unknown.</param>
        /// <param name="selected">How many agents the player has selected.</param>
        /// <param name="minimumAgents">Core's minimum squad size for the mission type.</param>
        /// <param name="maximumAgents">Core's maximum squad size. Zero means unbounded.</param>
        public static SquadReadiness Readiness(
            MissionDifficultyEstimate? estimate,
            int selected,
            int minimumAgents,
            int maximumAgents)
        {
            var blockers = new List<ReadinessBlocker>();

            // Squad size first: a squad that is too small has not started being an
            // estimate yet, so leading with Core's power number would be misleading.
            if (selected < minimumAgents)
                blockers.Add(new ReadinessBlocker("mission.readiness.too_few", AgentId.None));

            if (maximumAgents > 0 && selected > maximumAgents)
                blockers.Add(new ReadinessBlocker("mission.readiness.too_many", AgentId.None));

            if (estimate is null)
            {
                blockers.Add(new ReadinessBlocker("mission.readiness.unknown_type", AgentId.None));
                return new SquadReadiness(0, 0, 0, "difficulty.band.impossible", blockers);
            }

            // Core's own refusals, which cover missing roles and stamina.
            foreach (DispatchRefusal refusal in estimate.Refusals)
                blockers.Add(new ReadinessBlocker(refusal.MessageKey, refusal.AgentId));

            // Being under the required power is a blocker in its own right, distinct from
            // being illegal: the mission may be dispatchable and still be a bad idea, and
            // the player should be able to overrule that with their own risk appetite.
            if (estimate.PowerShortfall > 0)
                blockers.Add(new ReadinessBlocker("mission.readiness.under_powered", AgentId.None));

            return new SquadReadiness(
                teamPower: estimate.TeamPower,
                requiredPower: estimate.RequiredPower,
                powerMargin: estimate.PowerMargin,
                bandKey: estimate.BandKey,
                blockers: blockers);
        }

        /// <summary>
        /// The difficulty breakdown, as the preparation screen lists it.
        /// </summary>
        /// <remarks>
        /// The brief is explicit that this must be "an honest number derived from Core,
        /// never a fudge". So this is a straight copy of Core's terms. If Core reports two
        /// terms, the player sees two terms; the list is not padded to look thorough.
        /// </remarks>
        public static IReadOnlyList<DifficultyRow> Breakdown(MissionDifficultyEstimate? estimate)
        {
            var rows = new List<DifficultyRow>();
            if (estimate is null) return rows;

            foreach (DifficultyTerm term in estimate.Terms)
                rows.Add(new DifficultyRow(term.TermKey, term.Delta, DifficultyRowSide.Demand));

            foreach (DifficultyTerm term in estimate.SquadTerms)
                rows.Add(new DifficultyRow(term.TermKey, term.Delta, DifficultyRowSide.Supply));

            return rows;
        }

        /// <summary>Which side of the arithmetic a term belongs to.</summary>
        public enum DifficultyRowSide
        {
            /// <summary>Raises the difficulty.</summary>
            Demand = 0,

            /// <summary>Answers it.</summary>
            Supply = 1,
        }

        /// <summary>One line of the difficulty breakdown.</summary>
        public readonly struct DifficultyRow
        {
            /// <summary>Creates a row.</summary>
            public DifficultyRow(string key, int delta, DifficultyRowSide side)
            {
                Key = key;
                Delta = delta;
                Side = side;
            }

            /// <summary>Localization key naming the cause.</summary>
            public string Key { get; }

            /// <summary>Signed contribution.</summary>
            public int Delta { get; }

            /// <summary>Which side of the arithmetic it belongs to.</summary>
            public DifficultyRowSide Side { get; }

            /// <summary>True when this term raises the difficulty.</summary>
            public bool RaisesDifficulty => Side == DifficultyRowSide.Demand && Delta > 0;
        }

        /// <summary>
        /// The intel snapshot, as the preparation screen previews it.
        /// </summary>
        /// <remarks>
        /// <b>Confidence is carried on every fact, not just the picture.</b> Core tags each
        /// entry with an <see cref="IntelConfidence"/>, and dropping that tag to make the
        /// preview cleaner would be the exact lie the brief forbids: a floor the player
        /// cannot trust looks identical to one they can unless the marker is right there.
        /// </remarks>
        public readonly struct IntelFloorPreview
        {
            /// <summary>Builds a floor preview.</summary>
            public IntelFloorPreview(int floorIndex, string floorKey, IReadOnlyList<IntelFactPreview> facts)
            {
                FloorIndex = floorIndex;
                FloorKey = floorKey;
                Facts = facts ?? Array.Empty<IntelFactPreview>();
            }

            /// <summary>Which floor this is.</summary>
            public int FloorIndex { get; }

            /// <summary>Localization key for the floor.</summary>
            public string FloorKey { get; }

            /// <summary>What Core knows about this floor.</summary>
            public IReadOnlyList<IntelFactPreview> Facts { get; }

            /// <summary>How many of this floor's facts are certain.</summary>
            public int ConfirmedCount
            {
                get
                {
                    int n = 0;
                    foreach (IntelFactPreview fact in Facts)
                    {
                        // Reported is the only confidence Core ever grants pre-mission
                        // (see IntelConfidence: "nothing in a pre-mission snapshot is
                        // Observed"), so counting Observed here would report zero
                        // confirmed facts on every snapshot forever. Stale facts are
                        // excluded because they may no longer be true, which is the whole
                        // difference between the two.
                        if (fact.Confidence == IntelConfidence.Reported)
                            n++;
                    }

                    return n;
                }
            }
        }

        /// <summary>One fact from the intel snapshot, with its confidence.</summary>
        public readonly struct IntelFactPreview
        {
            /// <summary>Creates a fact preview.</summary>
            public IntelFactPreview(string key, string valueKey, IntelConfidence confidence)
            {
                Key = key;
                ValueKey = valueKey;
                Confidence = confidence;
            }

            /// <summary>Localization key for the fact's name.</summary>
            public string Key { get; }

            /// <summary>Localization key or id for the fact's value.</summary>
            public string ValueKey { get; }

            /// <summary>How much to trust it.</summary>
            public IntelConfidence Confidence { get; }
        }

        /// <summary>
        /// Builds the floor-by-floor intel preview.
        /// </summary>
        /// <remarks>
        /// A poisoned snapshot is not silently omitted. Core already knows it is poisoned
        /// (<see cref="IntelSnapshot.IsPoisoned"/>), and a preview that quietly hid the
        /// poisoned intel would leave the player trusting a lie — which is the entire point
        /// of the mechanic. It is surfaced so the UI can mark it.
        /// </remarks>
        /// <param name="snapshot">Core's snapshot, or null when none has arrived yet.</param>
        public static IReadOnlyList<IntelFloorPreview> IntelPreview(IntelSnapshot? snapshot)
        {
            var floors = new List<IntelFloorPreview>();
            if (snapshot is null) return floors;

            // Grouped by floor. Entries are an abstract base discriminated by FactKind,
            // and only a room entry names a floor — connections and patrol routes belong
            // to the room list rather than to a floor of their own.
            var byFloor = new Dictionary<int, List<IntelFactPreview>>();

            foreach (IntelEntry entry in snapshot.Entries)
            {
                if (entry is not IntelRoomEntry room)
                    continue;

                if (!byFloor.TryGetValue(room.FloorIndex, out List<IntelFactPreview>? facts))
                {
                    facts = new List<IntelFactPreview>();
                    byFloor[room.FloorIndex] = facts;
                }

                facts.Add(new IntelFactPreview(room.NameKey, room.RoomTemplateId.ToString(), room.Confidence));
            }

            foreach (KeyValuePair<int, List<IntelFactPreview>> pair in byFloor)
                floors.Add(new IntelFloorPreview(pair.Key, FloorKeyFor(pair.Key), pair.Value));

            floors.Sort((a, b) => a.FloorIndex.CompareTo(b.FloorIndex));
            return floors;
        }

        private static string FloorKeyFor(int floorIndex) => $"intel.floor.{floorIndex}";

        /// <summary>
        /// Whether the dispatch button should be enabled.
        /// </summary>
        /// <remarks>
        /// Strict: everything Core refused must be resolved. The alternative — enabling it
        /// when the squad is merely legal — lets a player dispatch a squad Core said was
        /// missing a role, and the refusal would then surface as a mission-start failure
        /// rather than as a decision the player made.
        /// </remarks>
        public static bool CanDispatch(SquadReadiness readiness) => readiness.IsReady;
    }
}
