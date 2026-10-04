using System;
using System.Collections.Generic;
using ProjectSpy.Core;
using RoomTypeRow = ProjectSpy.Tables.RoomType;

namespace ProjectSpy.Unity.UI.Presenters
{
    /// <summary>
    /// Prices room types and explains why a placement was refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and static, like the other presenters: no services, no scene. Every cost it
    /// shows comes out of Core — <see cref="BaseLayout.ComputeBuildCost"/> for the depth
    /// curve, the room type row for upkeep — and every refusal is
    /// <see cref="BaseLayout.CheckPlacement"/>'s verdict, translated into a key.
    /// </para>
    /// <para>
    /// <b>Affordability is asked of Core, not of a comparison.</b> Core owns the
    /// <see cref="Resources"/> record and the rule for spending it; a UI-side "can I afford
    /// this" would be a second opinion about the same question, and the two would disagree
    /// the moment a cost other than funds appeared.
    /// </para>
    /// </remarks>
    public static class BuildPresenter
    {
        /// <summary>
        /// The build catalogue, filtered by act.
        /// </summary>
        /// <remarks>
        /// <paramref name="byAct"/> filters by the room type's <c>required_act</c>, which is
        /// the grouping the brief asks for. An empty act list means "no filter", so the
        /// caller does not have to special-case the unfiltered request.
        /// </remarks>
        /// <param name="layout">The base, for unlocks and the depth curve.</param>
        /// <param name="roomTypes">Every room type row to consider.</param>
        /// <param name="byAct">Act ids to keep, or empty for all.</param>
        public static IReadOnlyList<BuildCatalogueEntry> Catalogue(
            BaseLayout? layout,
            IReadOnlyList<RoomTypeRow>? roomTypes,
            IReadOnlyList<int>? byAct = null)
        {
            var entries = new List<BuildCatalogueEntry>();
            if (roomTypes is null) return entries;

            foreach (RoomTypeRow type in roomTypes)
            {
                if (type is null) continue;
                if (!ActAllowed(type, byAct)) continue;

                int minDepth = MinimumDepthOf(layout, type);

                entries.Add(new BuildCatalogueEntry(
                    roomTypeId: type.Id,
                    nameKey: type.NameKey,
                    cost: type.BuildCost,
                    weeklyUpkeep: type.UpkeepPerWeek,
                    depthModifierPercent: DepthModifierAt(layout, minDepth),
                    minimumDepth: minDepth,
                    isUnlocked: IsUnlocked(layout, type),
                    slotsOccupied: type.Width));
            }

            return entries;
        }

        /// <summary>
        /// The layer a type must sit at or below.
        /// </summary>
        /// <remarks>
        /// The row's own <c>min_depth</c> is authoritative; the layout's map is a
        /// story-driven override that may push a type deeper. The deeper of the two wins,
        /// because that is the layer the room may actually go on.
        /// </remarks>
        private static int MinimumDepthOf(BaseLayout? layout, RoomTypeRow type)
        {
            int minDepth = type.MinDepth;

            if (layout is not null &&
                layout.MinimumDepthByType.TryGetValue(type.Id, out int overrideDepth) &&
                overrideDepth > minDepth)
            {
                minDepth = overrideDepth;
            }

            return minDepth;
        }

        private static bool IsUnlocked(BaseLayout? layout, RoomTypeRow type)
        {
            // An empty unlock set means nothing is story-gated yet, which is the state of
            // a fresh save — not "everything is locked".
            return layout is null ||
                   layout.UnlockedRoomTypeIds.Count == 0 ||
                   layout.UnlockedRoomTypeIds.Contains(type.Id);
        }

        private static int DepthModifierAt(BaseLayout? layout, int depth)
        {
            if (layout is null || layout.DepthCostModifier.Length == 0)
                return 0;

            return layout.DepthCostModifier[Math.Clamp(depth, 0, layout.DepthCostModifier.Length - 1)];
        }

        private static bool ActAllowed(RoomTypeRow type, IReadOnlyList<int>? byAct)
        {
            // An explicit loop rather than Contains: on netstandard2.1 IReadOnlyList<int>
            // resolves Contains through the span extension, which wants a StringComparison
            // this call has no meaning for. A two-line loop says what it means and works
            // on every target the assembly is built for.
            if (byAct is null || byAct.Count == 0)
                return true;

            foreach (int act in byAct)
            {
                if (act == type.RequiredAct)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the player can currently pay for a room at a layer.
        /// </summary>
        /// <remarks>
        /// The depth-adjusted cost is asked of Core, then tested by Core's own
        /// <see cref="Resources.TrySpendFunds(long, out Resources)"/>. Comparing longs here
        /// and spending later would leave a gap where the two disagreed; asking Core once
        /// is the same question asked a single time.
        /// </remarks>
        /// <param name="layout">For the depth curve.</param>
        /// <param name="resources">The player's current resources.</param>
        /// <param name="typeId">Room type being placed.</param>
        /// <param name="layer">Layer it would go on.</param>
        /// <param name="verdict">Out: the refusal if unaffordable.</param>
        /// <param name="effectiveCost">Out: the depth-adjusted cost, affordable or not.</param>
        /// <returns>True when Core accepted the spend.</returns>
        public static bool CanAfford(
            BaseLayout? layout,
            Resources resources,
            int typeId,
            int layer,
            out PlacementVerdict verdict,
            out long effectiveCost)
        {
            if (resources is null) throw new ArgumentNullException(nameof(resources));

            RoomTypeRow? type = SimulationRules.RoomTypeFor(typeId);
            effectiveCost = type is null ? 0 : CostAt(layout, type, layer);
            verdict = PlacementVerdict.Valid;

            if (type is null)
            {
                verdict = PlacementVerdict.Refused("build.error.unknown_room_type", typeId);
                return false;
            }

            if (!resources.TrySpendFunds(effectiveCost, out _))
            {
                // A distinct message from an illegal placement: the player knows the room
                // is allowed there and is short of money, which is a different problem
                // with a different fix.
                verdict = PlacementVerdict.Refused(
                    "build.error.insufficient_funds",
                    MoneyFormat.Plain(effectiveCost),
                    MoneyFormat.Plain(resources.Funds));
                return false;
            }

            return true;
        }

        /// <summary>The depth-adjusted cost, from Core's own curve.</summary>
        public static long CostAt(BaseLayout? layout, RoomTypeRow type, int layer)
            => layout is null ? type.BuildCost : layout.ComputeBuildCost(type.BuildCost, layer);

        /// <summary>
        /// The full placement verdict: legal, unlocked, deep enough, and affordable.
        /// </summary>
        /// <remarks>
        /// <b>Core's placement check runs first.</b> Asking "can I afford it" about a slot
        /// that is already occupied produces a cost the player does not need to see yet,
        /// and reporting the money first makes an illegal placement look like a money
        /// problem.
        /// </remarks>
        public static PlacementVerdict Evaluate(
            BaseLayout? layout,
            Resources resources,
            int typeId,
            int layer,
            IReadOnlyCollection<int>? slots)
        {
            if (layout is null)
                return PlacementVerdict.Refused("build.error.generic");

            if (slots is null || slots.Count == 0)
                return PlacementVerdict.FromCore(PlacementError.NoSlots, layer, 0);

            PlacementError legal = layout.CheckPlacement(typeId, layer, slots);
            if (legal != PlacementError.None)
                return PlacementVerdict.FromCore(legal, layer, FirstSlot(slots));

            bool affordable = CanAfford(layout, resources, typeId, layer, out PlacementVerdict costVerdict, out _);
            return affordable ? PlacementVerdict.Valid : costVerdict;
        }

        /// <summary>
        /// The ghost preview: what this placement would cost, and whether it is legal.
        /// </summary>
        /// <remarks>
        /// Returned as one value because the panel draws both at once, and a preview that
        /// showed a cost with no validity colour would be asking the player to trust it.
        /// </remarks>
        public readonly struct GhostPreview
        {
            /// <summary>Builds a preview.</summary>
            public GhostPreview(
                int roomTypeId,
                int layer,
                IReadOnlyList<int> slots,
                long cost,
                long weeklyUpkeep,
                int depthModifierPercent,
                PlacementVerdict verdict)
            {
                RoomTypeId = roomTypeId;
                Layer = layer;
                Slots = slots;
                Cost = cost;
                WeeklyUpkeep = weeklyUpkeep;
                DepthModifierPercent = depthModifierPercent;
                Verdict = verdict;
            }

            /// <summary>Room type being previewed.</summary>
            public int RoomTypeId { get; }

            /// <summary>Layer it would go on.</summary>
            public int Layer { get; }

            /// <summary>Slots it would occupy.</summary>
            public IReadOnlyList<int> Slots { get; }

            /// <summary>Depth-adjusted build cost.</summary>
            public long Cost { get; }

            /// <summary>Weekly upkeep once built.</summary>
            public long WeeklyUpkeep { get; }

            /// <summary>Depth cost modifier in force at this layer.</summary>
            public int DepthModifierPercent { get; }

            /// <summary>Why it is or is not allowed.</summary>
            public PlacementVerdict Verdict { get; }

            /// <summary>True when the ghost should draw in the valid colour.</summary>
            public bool IsValid => Verdict.IsValid;

            /// <summary>Localization key naming this validity, for the hint line.</summary>
            public string VerdictKey => Verdict.IsValid ? "build.ghost.valid" : Verdict.ReasonKey;
        }

        /// <summary>Builds the ghost preview for a candidate placement.</summary>
        public static GhostPreview Preview(
            BaseLayout? layout,
            Resources resources,
            int typeId,
            int layer,
            IReadOnlyCollection<int>? slots)
        {
            RoomTypeRow? type = SimulationRules.RoomTypeFor(typeId);
            long cost = type is null ? 0 : CostAt(layout, type, layer);

            var slotList = new List<int>();
            if (slots is not null)
            {
                foreach (int slot in slots)
                    slotList.Add(slot);
            }

            int minDepth = type is null ? 0 : MinimumDepthOf(layout, type);

            return new GhostPreview(
                roomTypeId: typeId,
                layer: layer,
                slots: slotList,
                cost: cost,
                weeklyUpkeep: type?.UpkeepPerWeek ?? 0,
                depthModifierPercent: DepthModifierAt(layout, minDepth),
                verdict: Evaluate(layout, resources, typeId, layer, slotList));
        }

        private static int FirstSlot(IReadOnlyCollection<int> slots)
        {
            foreach (int slot in slots)
                return slot;

            return 0;
        }
    }
}
