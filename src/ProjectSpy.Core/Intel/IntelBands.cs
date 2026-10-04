namespace ProjectSpy.Core;

/// <summary>
/// How much of a building a sleeper operation has bought so far.
/// </summary>
/// <remarks>
/// <para>
/// The five bands are the reveal curve from knowledge.md rule 17. They are ordinal
/// because the curve only ever widens: a snapshot taken at a higher band is a superset
/// of one taken at a lower band, so <c>band &gt;= Details</c> is a legitimate test and
/// the promotion is monotone.
/// </para>
/// <para>
/// The band <em>edges</em> are not here. They live in <c>intel_rule</c> because they are
/// tuning a designer retunes, not structure (knowledge.md rule 3), and
/// <see cref="IntelBands"/> is the only thing that reads them.
/// </para>
/// </remarks>
public enum IntelBand
{
    /// <summary>0-24. The entrance, and nothing else.</summary>
    EntranceOnly = 0,

    /// <summary>25-49. Floor count and rough layout; room types still unknown.</summary>
    Layout = 1,

    /// <summary>50-74. Room types and connections; some of it stale.</summary>
    TypesAndConnections = 2,

    /// <summary>75-99. Patrols, guard counts, lights and where the objective is.</summary>
    Details = 3,

    /// <summary>100. Everything, current, plus where a prisoner is held.</summary>
    Complete = 4,
}

/// <summary>
/// How the player came to know a fact in an <see cref="IntelEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not an ordered ladder. <see cref="Observed"/> is not "more" than
/// <see cref="Reported"/> along the same axis — it is a different kind of knowledge,
/// arrived at by a different means. Ordering these by value would invite a comparison
/// somewhere that quietly means the wrong thing.
/// </para>
/// <para>
/// Nothing in a pre-mission snapshot is <see cref="Observed"/>. Observed is what the
/// tactical layer writes when an agent has seen the thing with their own eyes, and until
/// then every fact came from a sleeper, which is exactly why the whole system exists.
/// </para>
/// </remarks>
public enum IntelConfidence
{
    /// <summary>Told by the sleeper, and believed current.</summary>
    Reported = 0,

    /// <summary>Told by the sleeper some time ago; may no longer be true.</summary>
    Stale = 1,

    /// <summary>Seen directly by the team. Nothing pre-mission is ever this.</summary>
    Observed = 2,
}

/// <summary>Who a piece of intel came from.</summary>
public enum IntelSource
{
    /// <summary>The sleeper, reporting now.</summary>
    Sleeper = 0,

    /// <summary>The sleeper, reporting something they saw before the last refresh.</summary>
    SleeperPrior = 1,

    /// <summary>The team, in the building, with its own eyes.</summary>
    DirectObservation = 2,

    /// <summary>The agency learning where one of its own people is being held.</summary>
    CaptureRecord = 3,
}

/// <summary>Which special roles a room is known to have.</summary>
[Flags]
public enum IntelKnownRole
{
    /// <summary>Nothing known.</summary>
    None = 0,

    /// <summary>Known to be where the team goes in.</summary>
    Entrance = 1,

    /// <summary>Known to hold the objective.</summary>
    Objective = 2,

    /// <summary>Known to be a way out.</summary>
    Extraction = 4,
}

/// <summary>
/// What is known about one room and one connection at mission start, and during it.
/// </summary>
/// <remarks>
/// <para>
/// A ladder, and strictly monotone. Knowledge never goes backwards: a room that has
/// been observed cannot become unknown again because a guard walked back through it.
/// Demotion would mean the map flickers between states as the team moves, and the
/// player would learn nothing from it.
/// </para>
/// <para>
/// <see cref="Reported"/> is the state intel puts a room in, and it is also the state
/// that may be contradicted — the tactical layer promotes it to <see cref="Observed"/>
/// when reality disagrees. <see cref="Scouted"/> sits between reporting and observation
/// for the gadgets and informants that read a room without entering it.
/// </para>
/// </remarks>
public enum FogState
{
    /// <summary>Nothing is known. The room does not appear on the map at all.</summary>
    Unknown = 0,

    /// <summary>A sleeper said so. May be wrong, and rendered differently because of it.</summary>
    Reported = 1,

    /// <summary>Read from outside — a gadget, a terminal, an informant — but not entered.</summary>
    Scouted = 2,

    /// <summary>Seen directly by the team.</summary>
    Observed = 3,

    /// <summary>Searched. Everything in it has been taken or accounted for.</summary>
    Cleared = 4,
}

/// <summary>
/// The intel reveal curve, and the one place the band edges are read.
/// </summary>
/// <remarks>
/// <para>
/// Every threshold comes from <c>intel_rule</c>. The fallbacks are the values the
/// knowledge.md rule 17 bands imply, and they exist only so a missing table row produces
/// the documented curve rather than a divide by zero; the validator fails the build if
/// any of them is actually absent.
/// </para>
/// </remarks>
public static class IntelBands
{
    /// <summary>The bands in ascending order.</summary>
    public static IReadOnlyList<IntelBand> Ladder { get; } = new[]
    {
        IntelBand.EntranceOnly,
        IntelBand.Layout,
        IntelBand.TypesAndConnections,
        IntelBand.Details,
        IntelBand.Complete,
    };

    /// <summary>The band an intel percentage falls into.</summary>
    /// <remarks>
    /// Clamped rather than throwing: <c>IntelPercent</c> is a 0-100 field that a
    /// mis-written table could push past the top, and a snapshot builder that threw would
    /// turn a data typo into an unwinnable mission.
    /// </remarks>
    public static IntelBand For(int intelPercent)
    {
        int percent = Math.Clamp(intelPercent, 0, 100);

        if (percent <= SimulationRules.Intel("intel_band_entrance_max", 24))
            return IntelBand.EntranceOnly;

        if (percent <= SimulationRules.Intel("intel_band_layout_max", 49))
            return IntelBand.Layout;

        if (percent <= SimulationRules.Intel("intel_band_types_max", 74))
            return IntelBand.TypesAndConnections;

        if (percent <= SimulationRules.Intel("intel_band_details_max", 99))
            return IntelBand.Details;

        return IntelBand.Complete;
    }

    /// <summary>
    /// The lowest intel percentage that reaches a band, for "progress to next band" UI.
    /// </summary>
    /// <remarks>
    /// The <em>inclusive</em> bottom, so a player at exactly 25 is told they are 25 away
    /// from the layout band rather than 24, which is what they would see if this
    /// returned one past the top of the previous band.
    /// </remarks>
    public static int MinPercentFor(IntelBand band) => band switch
    {
        IntelBand.EntranceOnly => 0,
        IntelBand.Layout => SimulationRules.Intel("intel_band_entrance_max", 24) + 1,
        IntelBand.TypesAndConnections => SimulationRules.Intel("intel_band_layout_max", 49) + 1,
        IntelBand.Details => SimulationRules.Intel("intel_band_types_max", 74) + 1,
        IntelBand.Complete => SimulationRules.Intel("intel_band_full", 100),
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown intel band."),
    };

    /// <summary>The band above this one, or null at the top.</summary>
    public static IntelBand? NextAbove(IntelBand band)
    {
        int index = -1;
        for (int i = 0; i < Ladder.Count; i++)
        {
            if (Ladder[i] == band)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown intel band.");

        return index + 1 < Ladder.Count ? Ladder[index + 1] : null;
    }

    /// <summary>
    /// What a band contains, as data.
    /// </summary>
    /// <remarks>
    /// The base-side screen needs to say "25 more percent and you learn where the guards
    /// walk" before the player commits the days. That sentence is localized text, so it
    /// cannot be built here (knowledge.md rule 4) — but the facts it would be built from
    /// can, and this is them. One flat set of booleans rather than a per-band struct so
    /// the screen can render "new in the next band" as a diff against the current one.
    /// </remarks>
    public static IntelBandContents ContentsOf(IntelBand band) => band switch
    {
        IntelBand.EntranceOnly => new IntelBandContents(
            RevealsEntrance: true,
            RevealsFloorCount: false,
            RevealsRoomPlacement: false,
            RevealsRoomTypes: false,
            RevealsConnections: false,
            RevealsLockStates: false,
            RevealsGuardCounts: false,
            RevealsLightLevels: false,
            RevealsPatrolRoutes: false,
            RevealsObjective: false,
            RevealsExtraction: false,
            RevealsHoldingRooms: false,
            MayBeStale: false),

        IntelBand.Layout => new IntelBandContents(
            RevealsEntrance: true,
            RevealsFloorCount: true,
            RevealsRoomPlacement: true,
            RevealsRoomTypes: false,
            RevealsConnections: false,
            RevealsLockStates: false,
            RevealsGuardCounts: false,
            RevealsLightLevels: false,
            RevealsPatrolRoutes: false,
            RevealsObjective: false,
            RevealsExtraction: false,
            RevealsHoldingRooms: false,
            MayBeStale: false),

        IntelBand.TypesAndConnections => new IntelBandContents(
            RevealsEntrance: true,
            RevealsFloorCount: true,
            RevealsRoomPlacement: true,
            RevealsRoomTypes: true,
            RevealsConnections: true,
            RevealsLockStates: true,
            RevealsGuardCounts: false,
            RevealsLightLevels: false,
            RevealsPatrolRoutes: false,
            RevealsObjective: false,
            RevealsExtraction: false,
            RevealsHoldingRooms: false,
            MayBeStale: true),

        IntelBand.Details => new IntelBandContents(
            RevealsEntrance: true,
            RevealsFloorCount: true,
            RevealsRoomPlacement: true,
            RevealsRoomTypes: true,
            RevealsConnections: true,
            RevealsLockStates: true,
            RevealsGuardCounts: true,
            RevealsLightLevels: true,
            RevealsPatrolRoutes: true,
            RevealsObjective: true,
            RevealsExtraction: true,
            RevealsHoldingRooms: false,
            MayBeStale: false),

        _ => new IntelBandContents(
            RevealsEntrance: true,
            RevealsFloorCount: true,
            RevealsRoomPlacement: true,
            RevealsRoomTypes: true,
            RevealsConnections: true,
            RevealsLockStates: true,
            RevealsGuardCounts: true,
            RevealsLightLevels: true,
            RevealsPatrolRoutes: true,
            RevealsObjective: true,
            RevealsExtraction: true,
            RevealsHoldingRooms: true,
            MayBeStale: false),
    };
}

/// <summary>
/// What one intel band reveals. A flat record of booleans so a UI can diff two bands.
/// </summary>
/// <param name="RevealsEntrance">Which room the team enters by.</param>
/// <param name="RevealsFloorCount">How many floors the building has.</param>
/// <param name="RevealsRoomPlacement">Which rooms exist, and where each one sits.</param>
/// <param name="RevealsRoomTypes">What kind of place each room is.</param>
/// <param name="RevealsConnections">Which rooms join to which, and by what kind.</param>
/// <param name="RevealsLockStates">Whether a connection is locked.</param>
/// <param name="RevealsGuardCounts">How many guards are in each room.</param>
/// <param name="RevealsLightLevels">How well lit each room is.</param>
/// <param name="RevealsPatrolRoutes">Where the guards walk.</param>
/// <param name="RevealsObjective">Which room holds the objective.</param>
/// <param name="RevealsExtraction">Which rooms are ways out.</param>
/// <param name="RevealsHoldingRooms">Where a captured agent is being held.</param>
/// <param name="MayBeStale">
/// Whether facts at this band are allowed to be reported as out of date rather than current.
/// </param>
public sealed record IntelBandContents(
    bool RevealsEntrance,
    bool RevealsFloorCount,
    bool RevealsRoomPlacement,
    bool RevealsRoomTypes,
    bool RevealsConnections,
    bool RevealsLockStates,
    bool RevealsGuardCounts,
    bool RevealsLightLevels,
    bool RevealsPatrolRoutes,
    bool RevealsObjective,
    bool RevealsExtraction,
    bool RevealsHoldingRooms,
    bool MayBeStale);