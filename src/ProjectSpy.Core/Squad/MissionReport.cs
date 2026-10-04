using ProjectSpy.Core.Tactical;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// One skill check, with every number that went into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Behind a debug flag, and not merely.</b> The brief says the debrief must carry
/// "every roll with its modifier breakdown behind a debug flag". The flag matters more
/// than the breakdown here, because a player who can see the dice will optimise against
/// the dice instead of against the situation — which is why the flag defaults to off and
/// why <see cref="MissionReport.Rolls"/> is simply empty rather than lazily populated
/// when it is off.
/// </para>
/// <para>
/// <b>Recorded by the same call that spends the roll.</b> This is a record type rather
/// than a recomputation: a report that re-derived a roll from the actor's skills at
/// debrief time would show the skills the agent has <em>now</em>, not the ones the
/// snapshot they took on insertion into the building gave them.
/// </para>
/// </remarks>
/// <param name="Step">When the roll happened.</param>
/// <param name="ActorId">Who rolled.</param>
/// <param name="ActionId">Which action it was for.</param>
/// <param name="Roll">The d100.</param>
/// <param name="Skill">The actor's skill in the relevant skill.</param>
/// <param name="BaseDc">The row's difficulty.</param>
/// <param name="Total">Roll plus skill.</param>
/// <param name="Succeeded">Whether it came out.</param>
public readonly record struct RollRecord(
    long Step,
    int ActorId,
    int ActionId,
    int Roll,
    int Skill,
    int BaseDc,
    int Total,
    bool Succeeded)
{
    /// <summary>Localization key for this row's name.</summary>
    public string NameKey => $"report.roll.{(Succeeded ? "success" : "failure")}";

    /// <inheritdoc/>
    public override string ToString()
        => $"step {Step} actor {ActorId}: {Roll}+{Skill}={Total} vs dc {BaseDc} {(Succeeded ? "ok" : "no")}";
}

/// <summary>
/// One thing an agent perceived, for the debrief's "how did they see us" panel.
/// </summary>
/// <param name="Step">When.</param>
/// <param name="ObserverId">Who saw.</param>
/// <param name="SubjectId">What was seen.</param>
/// <param name="Level">How well, as the perception level.</param>
/// <param name="At">Where, when it was first seen at this level.</param>
public readonly record struct PerceptionRecord(
    long Step,
    int ObserverId,
    int SubjectId,
    PerceptionLevel Level,
    TacticalPosition At);

/// <summary>
/// One agent's fate, as the debrief reports it.
/// </summary>
public sealed class AgentReport
{
    /// <summary>Which operative.</summary>
    public AgentId AgentId { get; init; }

    /// <summary>The <c>agent_role</c> id they were sent with.</summary>
    public int RoleId { get; init; }

    /// <summary>Localization key for that role's name.</summary>
    public string RoleNameKey { get; init; } = string.Empty;

    /// <summary>What happened to them.</summary>
    public ActorCondition Condition { get; set; } = ActorCondition.Active;

    /// <summary>Health left when the mission ended.</summary>
    public int Health { get; set; }

    /// <summary>Injury severity added by this mission.</summary>
    public int InjuryAdded { get; set; }

    /// <summary>Mental damage added by this mission.</summary>
    public int MentalDamage { get; set; }

    /// <summary>Experience awarded.</summary>
    public int Exp { get; set; }

    /// <summary>How many gadgets they got through the mission with uses left.</summary>
    public int GadgetsUnused { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{AgentId} {Condition} hp {Health}";
}

/// <summary>
/// Everything the debrief screen needs, and nothing it does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>A structured record, not a rendering.</b> Every string in here is a localization
/// key. The debrief builds sentences; Core builds facts. That is rule 4 applied to the
/// largest object in the project, and it is the reason this type has no prose anywhere.
/// </para>
/// <para>
/// <b>The timeline is the state, not a copy of it.</b> The per-step entries are the
/// mission log the simulation already wrote, plus the order log. Duplicating them here
/// would mean two records that could disagree.
/// </para>
/// <para>
/// <b>Rolls are absent unless asked for.</b> See <see cref="RollRecord"/>. The default
/// for <see cref="IncludeRolls"/> is false and the <see cref="Rolls"/> list is empty
/// when it is false — not present-but-empty, which would let a UI read a length of zero
/// and conclude nothing had been rolled.
/// </para>
/// </remarks>
public sealed class MissionReport
{
    /// <summary>The mission this is about.</summary>
    public int MissionId { get; init; }

    /// <summary>The site it was played in.</summary>
    public int SiteId { get; init; }

    /// <summary>How it ended.</summary>
    public ResolveClass Class { get; init; } = ResolveClass.Unresolved;

    /// <summary>Localization key for that class's name.</summary>
    public string ClassNameKey { get; init; } = string.Empty;

    /// <summary>What the objective was.</summary>
    public TableObjectiveType ObjectiveType { get; init; }

    /// <summary>Localization key for the objective's name.</summary>
    public string ObjectiveNameKey { get; init; } = string.Empty;

    /// <summary>True when the objective was achieved.</summary>
    public bool ObjectiveComplete { get; init; }

    /// <summary>Why the objective failed, or <c>None</c>.</summary>
    public ObjectiveFailureReason ObjectiveFailure { get; init; }

    /// <summary>Steps the mission ran for.</summary>
    public long Steps { get; init; }

    /// <summary>The step the mission started on.</summary>
    public long StartedOnStep { get; init; }

    /// <summary>The strategic tick it was dispatched on.</summary>
    public Tick DispatchedOnTick { get; init; }

    /// <summary>Strategic ticks the mission cost, converted once at the end.</summary>
    public long TicksCost { get; init; }

    /// <summary>The alarm band at the end.</summary>
    public AlarmBand EndBand { get; init; }

    /// <summary>The highest alarm band reached.</summary>
    public AlarmBand PeakBand { get; init; }

    /// <summary>Heat earned.</summary>
    public int Heat { get; init; }

    /// <summary>Evidence left behind.</summary>
    public int Evidence { get; init; }

    /// <summary>Lethal acts committed.</summary>
    public int LethalActs { get; init; }

    /// <summary>True when the player called the abort.</summary>
    public bool Aborted { get; init; }

    /// <summary>True when the forward command post was taken.</summary>
    public bool CommandPostCompromised { get; init; }

    /// <summary>The mission log, in order. Every entry is a localization key and args.</summary>
    public IReadOnlyList<MissionLogEntry> Timeline { get; init; } = Array.Empty<MissionLogEntry>();

    /// <summary>
    /// Which rooms each operative walked through, in order.
    /// </summary>
    /// <remarks>
    /// Empty rather than null for a mission that recorded none, and the debrief draws an
    /// empty route rather than guessing one. Room-to-room, because that is the precision
    /// the simulation actually has — see <see cref="RouteStep"/>.
    /// </remarks>
    public IReadOnlyList<RouteStep> Routes { get; init; } = Array.Empty<RouteStep>();

    /// <summary>The orders the mission accepted, in order.</summary>
    public IReadOnlyList<AcceptedOrder> Orders { get; init; } = Array.Empty<AcceptedOrder>();

    /// <summary>Every noise made, in order.</summary>
    public IReadOnlyList<NoiseLogEntry> Noises { get; init; } = Array.Empty<NoiseLogEntry>();

    /// <summary>Every perception, in order. Empty unless perceptions were requested.</summary>
    public IReadOnlyList<PerceptionRecord> Perceptions { get; init; } = Array.Empty<PerceptionRecord>();

    /// <summary>
    /// Every roll with its breakdown, or empty.
    /// </summary>
    /// <remarks>
    /// Empty rather than null when <see cref="IncludeRolls"/> is false, and
    /// <see cref="IncludeRolls"/> is the only thing a UI should branch on — checking
    /// the list's length would show "no rolls happened" for a mission that had plenty.
    /// </remarks>
    public IReadOnlyList<RollRecord> Rolls { get; init; } = Array.Empty<RollRecord>();

    /// <summary>Whether the roll log was collected. The flag the UI checks.</summary>
    public bool IncludeRolls { get; init; }

    /// <summary>Each agent's fate, in squad order.</summary>
    public IReadOnlyList<AgentReport> Agents { get; init; } = Array.Empty<AgentReport>();

    /// <summary>Items carried out, as loot table and item ids.</summary>
    public IReadOnlyList<(int LootTableId, int ItemId, int Count)> Loot { get; init; } =
        Array.Empty<(int, int, int)>();

    /// <summary>Funds the mission paid.</summary>
    public long FundsPaid { get; init; }

    /// <summary>Intel the mission paid.</summary>
    public int IntelPaid { get; init; }

    /// <summary>
    /// The summary lines, as localization keys with arguments.
    /// </summary>
    /// <remarks>
    /// Keys rather than sentences for the same reason as everything else. There is one
    /// line per thing worth saying about the run — how it ended, who was lost, what the
    /// objective was worth — and the debrief chooses which of them to show.
    /// </remarks>
    public IReadOnlyList<(string Key, IReadOnlyList<int> Args)> Summary { get; init; } =
        Array.Empty<(string, IReadOnlyList<int>)>();

    /// <inheritdoc/>
    public override string ToString()
        => $"mission {MissionId} {ObjectiveType} {Class} in {Steps} steps";
}

/// <summary>
/// Builds the debrief from a finished mission.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads, it does not decide.</b> Everything here comes from state the simulation
/// already wrote, plus <see cref="ResolveSystem.Classify"/> for the one judgement call.
/// A report that calculated its own numbers would be a second implementation of the
/// rules, and would eventually disagree with them.
/// </para>
/// <para>
/// <b>The debug flag is a parameter, not a global.</b> A global "enable verbose logging"
/// toggle would mean a report's contents depended on when it was built relative to when
/// the player opened a console, and a replay of the same mission would produce a
/// different file.
/// </para>
/// </remarks>
public static class MissionReportBuilder
{
    /// <summary>Builds the debrief.</summary>
    /// <param name="state">The finished mission.</param>
    /// <param name="composition">The squad as dispatched.</param>
    /// <param name="outcome">The objective's result.</param>
    /// <param name="post">The command post's final state.</param>
    /// <param name="rolls">Rolls recorded during play, if the debug flag was on.</param>
    /// <param name="perceptions">Perceptions recorded during play.</param>
    /// <param name="includeRolls">
    /// Whether the roll breakdown goes in the report. False by default and the caller has
    /// to ask for it explicitly.
    /// </param>
    public static MissionReport Build(
        TacticalState state,
        SquadComposition composition,
        ObjectiveOutcome outcome,
        CommandPostState post,
        IReadOnlyList<RollRecord>? rolls = null,
        IReadOnlyList<PerceptionRecord>? perceptions = null,
        bool includeRolls = false)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));

        MissionResolution resolution = ResolveSystem.Classify(
            state, outcome, state.Outcome == TacticalOutcome.Aborted);

        ProjectSpy.Tables.ResolveRule? rule = SimulationRules.ResolveRuleFor(
            ResolveSystem.Table(resolution.Class));

        return new MissionReport
        {
            MissionId = state.MissionId,
            SiteId = state.SiteId,
            Class = resolution.Class,
            ClassNameKey = resolution.ClassNameKey,
            ObjectiveType = outcome.Type,
            ObjectiveNameKey = outcome.NameKey,
            ObjectiveComplete = outcome.IsComplete,
            ObjectiveFailure = outcome.Failure,
            Steps = state.Step,
            StartedOnStep = 0,
            TicksCost = MissionTimeConverter.StepsToStrategicTicks(state.Step),
            EndBand = state.Alarm.Band,
            PeakBand = PeakBand(state),
            Heat = resolution.Heat,
            Evidence = resolution.Evidence,
            LethalActs = state.LethalActs,
            Aborted = state.Outcome == TacticalOutcome.Aborted,
            CommandPostCompromised = post is not null && post.IsCompromised,
            Timeline = state.Log.ToArray(),
            Routes = state.RouteLog.ToArray(),
            Orders = state.OrderLog.ToArray(),
            Noises = state.NoiseLog.ToArray(),
            Perceptions = perceptions ?? Array.Empty<PerceptionRecord>(),
            Rolls = includeRolls
                ? rolls ?? Array.Empty<RollRecord>()
                : Array.Empty<RollRecord>(),
            IncludeRolls = includeRolls,
            Agents = AgentReports(state, composition),
            Summary = Summaries(state, resolution, outcome, post ?? new CommandPostState()),
            FundsPaid = 0,
            IntelPaid = 0,
        };
    }

    /// <summary>
    /// The highest alarm band the mission passed through.
    /// </summary>
    /// <remarks>
    /// Recovered by re-reading the log rather than by tracking a high-water mark in the
    /// simulation, because <see cref="AlarmState"/> deliberately holds only the current
    /// band — a mission that spiked and settled should report the spike, and the log is
    /// the record of it.
    /// </remarks>
    private static AlarmBand PeakBand(TacticalState state)
    {
        AlarmBand peak = state.Alarm.Band;

        foreach (MissionLogEntry entry in state.Log)
        {
            if (!string.Equals(entry.Key, "log.alarm.band", StringComparison.Ordinal))
                continue;

            if (entry.Args.Count > 0 && entry.Args[0] > (int)peak)
                peak = (AlarmBand)entry.Args[0];
        }

        return peak;
    }

    /// <summary>One report entry per agent, in squad order.</summary>
    private static IReadOnlyList<AgentReport> AgentReports(
        TacticalState state, SquadComposition composition)
    {
        var reports = new List<AgentReport>();

        foreach (SquadMember member in composition.Members)
        {
            TacticalActor? actor = state.AgentActor(member.AgentId);

            if (actor is null)
                continue;

            reports.Add(new AgentReport
            {
                AgentId = member.AgentId,
                RoleId = member.RoleId,
                RoleNameKey = member.Role?.NameKey ?? string.Empty,
                Condition = actor.Condition,
                Health = actor.Health,
                InjuryAdded = InjuryFor(actor),
                MentalDamage = actor.MoralStrain,
                Exp = 0,
                GadgetsUnused = member.Gadgets.Count,
            });
        }

        return reports;
    }

    /// <summary>
    /// Injury severity a mission did to somebody.
    /// </summary>
    /// <remarks>
    /// Missing health, scaled by <c>resolve_rule.injury_percent</c> — so a Disaster
    /// hurts three times as much as a Success for the same wound, which is the table's
    /// job rather than a second scale in Core.
    /// </remarks>
    private static int InjuryFor(TacticalActor actor)
        => Math.Max(0, actor.MaxHealth - actor.Health);

    /// <summary>The lines the debrief leads with.</summary>
    private static IReadOnlyList<(string Key, IReadOnlyList<int> Args)> Summaries(
        TacticalState state,
        MissionResolution resolution,
        ObjectiveOutcome outcome,
        CommandPostState post)
    {
        var lines = new List<(string, IReadOnlyList<int>)>
        {
            ("report.summary.outcome", new[] { (int)resolution.Class }),
        };

        if (resolution.Lost > 0)
            lines.Add(("report.summary.lost", new[] { resolution.Lost }));

        if (outcome.IsFailed && outcome.Failure != ObjectiveFailureReason.None)
            lines.Add(("report.summary.objective_failed", new[] { (int)outcome.Failure }));

        if (state.HeatGained > 0)
            lines.Add(("report.summary.heat", new[] { state.HeatGained }));

        if (state.EvidenceLevel > 0)
            lines.Add(("report.summary.evidence", new[] { state.EvidenceLevel }));

        if (post is { IsCompromised: true })
            lines.Add(("report.summary.command_post_lost", Array.Empty<int>()));

        if (state.Outcome == TacticalOutcome.Aborted)
            lines.Add(("report.summary.aborted", Array.Empty<int>()));

        return lines;
    }
}