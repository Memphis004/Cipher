using System;
using System.Collections.Generic;
using ProjectSpy.Core;

namespace ProjectSpy.Unity.UI.Presenters
{
    /// <summary>
    /// Why a placement was refused, in a form the UI can explain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors Core's <see cref="PlacementError"/> rather than replacing it. The UI's job
    /// is to turn a refusal into a sentence the player can act on, not to decide whether
    /// one happened — so the decision stays Core's and only the wording is Presentation's.
    /// </para>
    /// <para>
    /// A plain readonly struct rather than a record struct: <c>record struct</c> is C# 10
    /// and Unity 6.3 compiles C# 9. The equality members are written out because a plain
    /// struct defaults to reference equality, and a presenter returning values that are
    /// never <c>==</c> to themselves would fail assertions for reasons that have nothing
    /// to do with the logic under test.
    /// </para>
    /// </remarks>
    public readonly struct PlacementVerdict : IEquatable<PlacementVerdict>
    {
        /// <summary>Creates a verdict.</summary>
        public PlacementVerdict(bool isValid, string reasonKey, IReadOnlyList<object> reasonArgs)
        {
            IsValid = isValid;
            ReasonKey = reasonKey ?? string.Empty;
            ReasonArgs = reasonArgs ?? Array.Empty<object>();
        }

        /// <summary>True when the placement is allowed.</summary>
        public bool IsValid { get; }

        /// <summary>Localization key explaining the refusal. Empty when valid.</summary>
        public string ReasonKey { get; }

        /// <summary>Arguments for <see cref="ReasonKey"/>.</summary>
        public IReadOnlyList<object> ReasonArgs { get; }

        /// <summary>A placement that is allowed.</summary>
        public static PlacementVerdict Valid { get; } =
            new(true, string.Empty, Array.Empty<object>());

        /// <summary>A refusal carrying a localization key and its format arguments.</summary>
        public static PlacementVerdict Refused(string key, params object[] args)
            => new(false, key, args);

        /// <summary>
        /// Translates Core's own verdict.
        /// </summary>
        /// <remarks>
        /// Exhaustive over Core's enum. A new Core value with no case here falls through
        /// to a generic key: wrong, but visible. Rendering nothing would be the failure
        /// this whole project keeps designing against.
        /// </remarks>
        public static PlacementVerdict FromCore(PlacementError error, int layer, int slot)
        {
            if (error == PlacementError.None)
                return Valid;

            string key = error switch
            {
                PlacementError.None => "build.error.generic",
                PlacementError.LayerOutOfRange => "build.error.layer_out_of_range",
                PlacementError.SlotOutOfRange => "build.error.slot_out_of_range",
                PlacementError.SlotOccupied => "build.error.slot_occupied",
                PlacementError.NoSlots => "build.error.no_slots",
                PlacementError.UnknownRoomType => "build.error.unknown_room_type",
                PlacementError.LockedByStory => "build.error.locked_by_story",
                PlacementError.LayerTooShallow => "build.error.layer_too_shallow",
                PlacementError.UnknownNeighbour => "build.error.unknown_neighbour",
                PlacementError.NeighbourInOtherLayer => "build.error.neighbour_other_layer",
                _ => "build.error.generic",
            };

            return Refused(key, layer, slot);
        }

        /// <inheritdoc/>
        public bool Equals(PlacementVerdict other) =>
            IsValid == other.IsValid &&
            string.Equals(ReasonKey, other.ReasonKey, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is PlacementVerdict v && Equals(v);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(IsValid, ReasonKey);

        /// <inheritdoc/>
        public override string ToString() => IsValid ? "PlacementVerdict(valid)" : $"PlacementVerdict({ReasonKey})";

        /// <summary>Value equality.</summary>
        public static bool operator ==(PlacementVerdict left, PlacementVerdict right) => left.Equals(right);

        /// <summary>Value inequality.</summary>
        public static bool operator !=(PlacementVerdict left, PlacementVerdict right) => !left.Equals(right);
    }

    /// <summary>
    /// One catalogue entry, priced, as the build panel shows it.
    /// </summary>
    /// <remarks>
    /// <b>Every number here is Core's.</b> The cost is what
    /// <see cref="BaseLayout.ComputeBuildCost"/> produced, the upkeep is the room type's
    /// own row, and the depth modifier is the layer's entry in Core's curve. The presenter
    /// arranges them for display and adds nothing.
    /// </remarks>
    public readonly struct BuildCatalogueEntry
    {
        /// <summary>Creates a catalogue entry.</summary>
        public BuildCatalogueEntry(
            int roomTypeId,
            string nameKey,
            long cost,
            long weeklyUpkeep,
            int depthModifierPercent,
            int minimumDepth,
            bool isUnlocked,
            int slotsOccupied)
        {
            RoomTypeId = roomTypeId;
            NameKey = nameKey;
            Cost = cost;
            WeeklyUpkeep = weeklyUpkeep;
            DepthModifierPercent = depthModifierPercent;
            MinimumDepth = minimumDepth;
            IsUnlocked = isUnlocked;
            SlotsOccupied = slotsOccupied;
        }

        /// <summary>Room type id.</summary>
        public int RoomTypeId { get; }

        /// <summary>Localization key for the room's name.</summary>
        public string NameKey { get; }

        /// <summary>Base build cost, before depth.</summary>
        public long Cost { get; }

        /// <summary>Weekly upkeep.</summary>
        public long WeeklyUpkeep { get; }

        /// <summary>Depth cost modifier, as a percentage, at the entry's own depth.</summary>
        public int DepthModifierPercent { get; }

        /// <summary>Deepest layer this type may be placed on.</summary>
        public int MinimumDepth { get; }

        /// <summary>Whether the player has unlocked this type.</summary>
        public bool IsUnlocked { get; }

        /// <summary>How many base slots it occupies.</summary>
        public int SlotsOccupied { get; }

        /// <summary>
        /// The cost at a given depth modifier.
        /// </summary>
        /// <remarks>
        /// Presentation-only convenience for the catalogue list. Anything that actually
        /// charges the player asks <see cref="BaseLayout.ComputeBuildCost"/> instead —
        /// applying Core's depth curve here would be a second implementation of a rule
        /// that already exists, which is how the two drift apart.
        /// </remarks>
        public long CostAtDepth(long baseCost, int depthModifierPercent)
            => depthModifierPercent <= 0
                ? baseCost
                : Math.Max(0, baseCost) * (100 + depthModifierPercent) / 100L;
    }

    /// <summary>
    /// Sort orders the roster supports.
    /// </summary>
    /// <remarks>
    /// An explicit set rather than a string passed to a comparer, so a column header can
    /// only ever produce an order the UI knows how to render — including its tie-break.
    /// </remarks>
    public enum RosterSortKey
    {
        /// <summary>By codename.</summary>
        Codename = 0,

        /// <summary>By class name.</summary>
        ClassName = 1,

        /// <summary>By status.</summary>
        Status = 2,

        /// <summary>By level.</summary>
        Level = 3,

        /// <summary>By weekly salary.</summary>
        Salary = 4,

        /// <summary>By loyalty.</summary>
        Loyalty = 5,

        /// <summary>By physical stamina.</summary>
        PhysicalStamina = 6,

        /// <summary>By mental stamina.</summary>
        MentalStamina = 7,

        /// <summary>By average of the five skills.</summary>
        SkillAverage = 8,

        /// <summary>By highest single skill.</summary>
        HighestSkill = 9,
    }

    /// <summary>Sort direction.</summary>
    public enum SortDirection
    {
        /// <summary>Smallest first.</summary>
        Ascending = 0,

        /// <summary>Largest first.</summary>
        Descending = 1,
    }

    /// <summary>
    /// A roster filter, as the filter bar collects it.
    /// </summary>
    /// <remarks>
    /// <b>Filters compose as AND.</b> "Available only" and "Infiltrators only" are both
    /// things a player wants at once, and an OR would show a roster they did not ask for.
    /// </remarks>
    public readonly struct RosterFilter
    {
        /// <summary>Creates a filter.</summary>
        public RosterFilter(
            string classIdFilter,
            AgentStatus? statusFilter,
            bool availableOnly,
            bool unassignedOnly,
            int minimumLevel)
        {
            ClassIdFilter = classIdFilter;
            StatusFilter = statusFilter;
            AvailableOnly = availableOnly;
            UnassignedOnly = unassignedOnly;
            MinimumLevel = minimumLevel;
        }

        /// <summary>Class id to keep, as text from a filter box. Empty for all.</summary>
        public string ClassIdFilter { get; }

        /// <summary>Status to keep, or null for all.</summary>
        public AgentStatus? StatusFilter { get; }

        /// <summary>Keep only agents Core considers deployable.</summary>
        public bool AvailableOnly { get; }

        /// <summary>Keep only agents with no room assignment.</summary>
        public bool UnassignedOnly { get; }

        /// <summary>Minimum level to keep, or 0 for all.</summary>
        public int MinimumLevel { get; }

        /// <summary>No filtering at all.</summary>
        public static RosterFilter None => new(string.Empty, null, false, false, 0);

        /// <summary>True when this filter would keep every agent.</summary>
        public bool IsEmpty =>
            string.IsNullOrEmpty(ClassIdFilter) &&
            !StatusFilter.HasValue &&
            !AvailableOnly &&
            !UnassignedOnly &&
            MinimumLevel <= 0;
    }

    /// <summary>
    /// How ready a squad is, as the dispatch button shows it.
    /// </summary>
    /// <remarks>
    /// <b>Two separate questions, kept separate.</b> "Are they strong enough" is a number
    /// Core estimates; "may they legally go" is a set of refusals Core produced. Merging
    /// them into one score would hide a missing role behind a comfortable percentage.
    /// </remarks>
    public readonly struct SquadReadiness
    {
        /// <summary>Creates a readiness readout.</summary>
        public SquadReadiness(
            int teamPower,
            int requiredPower,
            int powerMargin,
            string bandKey,
            IReadOnlyList<ReadinessBlocker> blockers)
        {
            TeamPower = teamPower;
            RequiredPower = requiredPower;
            PowerMargin = powerMargin;
            BandKey = bandKey;
            Blockers = blockers ?? Array.Empty<ReadinessBlocker>();
        }

        /// <summary>The squad's power, from Core's estimate.</summary>
        public int TeamPower { get; }

        /// <summary>The site's demand, from Core's estimate.</summary>
        public int RequiredPower { get; }

        /// <summary>Signed surplus. Negative means short.</summary>
        public int PowerMargin { get; }

        /// <summary>Localization key for Core's difficulty band.</summary>
        public string BandKey { get; }

        /// <summary>Everything stopping dispatch, from Core's refusals.</summary>
        public IReadOnlyList<ReadinessBlocker> Blockers { get; }

        /// <summary>True when nothing blocks dispatch.</summary>
        public bool IsReady => Blockers.Count == 0;

        /// <summary>
        /// Readiness as a percentage, for the ring gauge.
        /// </summary>
        /// <remarks>
        /// Capped at 100. A squad far above the requirement is not "140% ready" in any
        /// useful sense — the player only needs to know the bar is full, and a bar that
        /// overflows reads as a bug.
        /// </remarks>
        public int Percent => RequiredPower <= 0
            ? 100
            : Math.Clamp(TeamPower * 100 / Math.Max(1, RequiredPower), 0, 100);
    }

    /// <summary>One reason a squad cannot be dispatched yet.</summary>
    public readonly struct ReadinessBlocker
    {
        /// <summary>Creates a blocker.</summary>
        public ReadinessBlocker(string key, AgentId agentId)
        {
            Key = key;
            AgentId = agentId;
        }

        /// <summary>Localization key explaining the blocker.</summary>
        public string Key { get; }

        /// <summary>The agent it concerns, or <see cref="Core.AgentId.None"/>.</summary>
        public AgentId AgentId { get; }
    }

    /// <summary>
    /// Formats money the way the top bar does.
    /// </summary>
    /// <remarks>
    /// Presentation owns formatting because it is a presentation concern — the same
    /// <c>long</c> is "1,240,000" in one locale and something else in another. Core never
    /// produces a formatted string (rule 4), and no arithmetic happens here: the sign is
    /// read from the value, never computed.
    /// </remarks>
    public static class MoneyFormat
    {
        /// <summary>A signed amount, e.g. "+1,200" or "-850".</summary>
        public static string Signed(long amount)
        {
            string magnitude = Math.Abs(amount).ToString("N0");
            return amount < 0 ? "-" + magnitude : "+" + magnitude;
        }

        /// <summary>An unsigned amount with thousands separators.</summary>
        public static string Plain(long amount) => Math.Abs(amount).ToString("N0");

        /// <summary>
        /// True when a delta should be drawn in the "good" colour.
        /// </summary>
        /// <remarks>
        /// Zero is neither. A delta of zero drawn as a gain implies the player earned
        /// something, and drawn as a loss implies they lost something; both are false.
        /// </remarks>
        public static bool IsGain(long amount) => amount > 0;

        /// <summary>True when a delta should be drawn in the "bad" colour.</summary>
        public static bool IsLoss(long amount) => amount < 0;
    }
}
