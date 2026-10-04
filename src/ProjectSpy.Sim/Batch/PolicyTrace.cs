using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// Dumps what each policy actually ordered, step by step, so two policies can be compared
/// order-for-order rather than outcome-for-outcome.
/// </summary>
/// <remarks>
/// <para>
/// A debrief says two runs ended the same way; it cannot say whether they walked the same
/// route or arrived by different roads at the same step. That distinction decides
/// whether a policy is doing nothing or is doing something whose effect is being absorbed
/// downstream, and the two call for completely different fixes. So this records the
/// <see cref="TacticalState.OrderLog"/> and compares it positionally.
/// </para>
/// <para>
/// Also records a per-step journal of the events that explain a mission's end —
/// perception, noise, damage, capture, mission-end — because "3 lost at step 225" is a
/// number and the thirty steps before it are the diagnosis.
/// </para>
/// <para>
/// A probe, not a test: it asserts nothing on purpose, so an odd seed does not stop the
/// investigation at the moment it is most interesting.
/// </para>
/// </remarks>
public static class PolicyTrace
{
    /// <summary>One recorded order, flattened to the fields worth diffing.</summary>
    public sealed record OrderLine(long Step, int ActorId, int ActionId, string Action, int ConnectionId, int InteractableId, int TargetActorId, int TargetRoom, string Posture);

    /// <summary>One recorded step that changed something worth explaining.</summary>
    public sealed record JournalLine(long Step, string Event, string Detail);

    /// <summary>A whole traced run.</summary>
    public sealed record Trace(
        SquadPolicy Policy,
        int TemplateId,
        ulong Seed,
        TacticalOutcome Outcome,
        ResolveClass Class,
        IReadOnlyList<OrderLine> Orders,
        IReadOnlyList<JournalLine> Journal,
        IReadOnlyList<string> LostReport)
    {
        /// <summary>The mission's outcome and the steps it took, on one line.</summary>
        public string Summary => $"{Policy,-9} {Outcome,-14} {Class,-12} orders={Orders.Count,3} lastStep={(Orders.Count > 0 ? Orders[^1].Step : 0)}";
    }

    /// <summary>Runs one mission under one policy and records it.</summary>
    public static Trace Run(
        ulong seed,
        SquadPolicy policy,
        int templateId,
        int tier,
        TableObjectiveType objective,
        int missionId)
    {
        MissionFixture fixture = MissionFixture.Create(
            seed, templateId, tier, objective, missionId, MissionFixture.RolesFor(objective));

        return Play(fixture, policy);
    }

    private static Trace Play(MissionFixture fixture, SquadPolicy policy)
    {
        TacticalState state = fixture.Mission;
        var driver = new SquadPolicyDriver(policy);
        var runner = new TacticalMissionRunner(state, new RngStreams(fixture.Seed));

        var orders = new List<OrderLine>();
        var journal = new List<JournalLine>();

        // Actor state as of the previous step, so a change is a change rather than a
        // snapshot. Detection is by diff, which means a transition the snapshot misses is
        // a transition this trace cannot see — recorded here so a reader knows the limit.
        var previous = Snapshot(state);

        // Its own high-water mark, not journal[^1]: the snapshot lines and the log lines
        // interleave within one step, so sharing a mark would skip every log entry that
        // landed on a step a snapshot transition had already written about.
        long logHighWater = long.MinValue;

        // Per run, not static. A static one leaked between the four policies a single
        // invocation traces, so the second policy through reported no alarm changes at
        // all and the output depended on the order the policies happened to run in.
        var bandState = new BandState();

        while (!state.IsOver && state.Step < MissionRun.StepCeiling)
        {
            driver.Think(state, fixture.Composition);

            // Orders land in PendingOrders during Think and are applied at the top of the
            // next step, so the order log is drained after Step rather than before it.
            runner.Step();

            Drain(state, orders);

            var current = Snapshot(state);

            foreach ((TacticalActorId id, ActorFacts facts) in current)
            {
                if (!previous.TryGetValue(id, out ActorFacts before))
                    continue;

                Compare(state, id, before, facts, journal, bandState);
            }

            previous = current;

            DrainLog(state, journal, ref logHighWater);

        }

        MissionResolution resolved = ResolveSystem.Classify(
            state, state.ObjectiveOutcome, aborted: state.AbortCalled);

        var lost = new List<string>();

        foreach (TacticalActor actor in state.SortedActors)
        {
            if (actor.Kind == TacticalActorKind.Agent && !actor.CanAct)
                lost.Add($"  {actor.AgentId} lost — {actor.Condition} hp={actor.Health} room={state.RoomOf(actor)?.Id.ToString() ?? "?"}");
        }

        return new Trace(policy, fixture.TemplateId, fixture.Seed, state.Outcome, resolved.Class, orders, journal, lost);
    }

    /// <summary>
    /// Pulls the mission-log entries that explain an escalation into the journal.
    /// </summary>
    /// <remarks>
    /// Only the keys that answer "what made the site angry" are copied. The full log
    /// carries every action the site completed, and a 4,000-step run produces tens of
    /// thousands of those; what is wanted here is the short causal chain, so the filter
    /// is a list of keys rather than a volume cap.
    /// </remarks>
    private static void DrainLog(TacticalState state, List<JournalLine> journal, ref long highWater)
    {
        foreach (MissionLogEntry entry in state.Log)
        {
            if (entry.Step <= highWater || !Watched.Contains(entry.Key))
                continue;

            highWater = entry.Step;

            journal.Add(new JournalLine(
                entry.Step,
                entry.Key,
                string.Join(",", entry.Args.Select(a => a.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }
    }

    /// <summary>
    /// The log keys worth reading when asking why a site escalated.
    /// </summary>
    /// <remarks>
    /// Every key <c>TacticalState.Record</c> is ever called with, minus the per-step
    /// chaff. <c>log.action.completed</c> alone fires tens of thousands of times in a
    /// long run and says nothing about the alarm.
    /// </remarks>
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal)
    {
        "log.alarm.raised",
        "log.alarm.calmed",
        "log.alarm.burned",
        "log.radio.transmitted",
        "log.goap.alarm_raised",
        "log.goap.backup_called",
        "log.goap.body_searched",
        "log.mission.burned",
        "log.objective.failed",
        "log.objective.complete",
        "log.objective.target_fled",
        "log.actor.captured",
        "log.actor.exfiltrated",
        "log.actor.dead",
        "log.actor.downed",
        "log.actor.bled_out",
        "log.commandpost.compromised",
        "log.commandpost.extraction",
    };

    /// <summary>
    /// Flattens the order log into comparable lines.
    /// </summary>
    /// <remarks>
    /// The log only grows, so the last line already written is the high-water mark and
    /// anything at or below it was drained by an earlier call. Draining the whole log
    /// every step would make this quadratic in the run length.
    /// </remarks>
    private static void Drain(TacticalState state, List<OrderLine> orders)
    {
        long highWater = orders.Count > 0 ? orders[^1].Step : long.MinValue;

        foreach (AcceptedOrder accepted in state.OrderLog)
        {
            if (accepted.Step <= highWater)
                continue;

            TacticalOrder order = accepted.Order;

            orders.Add(new OrderLine(
                accepted.Step,
                order.ActorId.Value,
                order.ActionId,
                NameFor(order.ActionId),
                order.ConnectionId.Value,
                order.InteractableId,
                order.TargetActorId.Value,
                RoomAt(state, order),
                PostureFor(state, order.ActorId)));
        }
    }

    private static string PostureFor(TacticalState state, TacticalActorId id)
    {
        foreach (TacticalActor actor in state.SortedActors)
        {
            if (actor.Id == id)
                return actor.Posture.ToString();
        }

        return "?";
    }

    private static int RoomAt(TacticalState state, TacticalOrder order)
    {
        if (!order.Target.IsValid)
            return 0;

        foreach (SiteRoom room in state.Layout.AllRooms)
        {
            if (room.FloorIndex == order.Target.FloorIndex)
                return (int)room.Id.Value;
        }

        return 0;
    }

    /// <summary>
    /// The action's table name, so two traces can be read without the ids side by side.
    /// </summary>
    private static string NameFor(int actionId)
    {
        if (actionId == TacticalOrder.StopActionId)
            return "stop";

        foreach (ProjectSpy.Tables.TacticalAction row in SimulationRules.AllTacticalActions())
        {
            if (row.Id == actionId)
                return row.NameKey.Replace("action.", string.Empty, StringComparison.Ordinal);
        }

        return actionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The last alarm band written to one journal, for change detection.</summary>
    private sealed class BandState
    {
        public string Value { get; set; } = "Calm";
    }

    /// <summary>What an actor looked like at one step.</summary>
    private readonly record struct ActorFacts(ActorCondition Condition, int Health, int Stamina, string Room, int Perception, string PeakLevel, string Plan);

    private static Dictionary<TacticalActorId, ActorFacts> Snapshot(TacticalState state)
    {
        var facts = new Dictionary<TacticalActorId, ActorFacts>();

        foreach (TacticalActor actor in state.SortedActors)
        {
            facts[actor.Id] = new ActorFacts(
                actor.Condition,
                actor.Health,
                actor.Stamina,
                state.RoomOf(actor)?.Id.ToString() ?? "?",
                actor.Memory.HasContact ? 1 : 0,
                actor.Memory.PeakLevel.ToString(),
                actor.Action?.ActionId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
        }

        return facts;
    }

    /// <summary>Records the transitions between two snapshots.</summary>
    private static void Compare(
        TacticalState state,
        TacticalActorId id,
        ActorFacts before,
        ActorFacts after,
        List<JournalLine> journal,
        BandState bandState)
    {
        string who = Who(state, id);

        if (before.Condition != after.Condition)
        {
            journal.Add(new JournalLine(state.Step, "status", $"{who} {before.Condition} -> {after.Condition} (hp {before.Health}->{after.Health}, room {before.Room})"));
        }

        if (before.Health != after.Health)
        {
            journal.Add(new JournalLine(state.Step, "health", $"{who} {before.Health} -> {after.Health} (room {before.Room})"));
        }

        if (before.PeakLevel != after.PeakLevel && after.PeakLevel != "None")
        {
            journal.Add(new JournalLine(state.Step, "perception", $"{who} peak {before.PeakLevel} -> {after.PeakLevel} (room {before.Room})"));
        }

        if (before.Perception == 0 && after.Perception == 1)
        {
            journal.Add(new JournalLine(state.Step, "contact", $"{who} gained contact in room {before.Room}"));
        }
        else if (before.Perception == 1 && after.Perception == 0)
        {
            journal.Add(new JournalLine(state.Step, "contact.lost", $"{who} lost contact in room {before.Room}"));
        }

        if (before.Plan != after.Plan && before.Plan != "-")
        {
            journal.Add(new JournalLine(state.Step, "action", $"{who} {before.Plan} -> {after.Plan} (room {before.Room})"));
        }

        // The band, not the level: the reader is asking "which band", and the level
        // changes every step while the alarm is moving, which would be a line per step.
        string band = state.Alarm.Band.ToString();

        if (band != bandState.Value)
        {
            journal.Add(new JournalLine(state.Step, "alarm", $"band -> {band}"));
            bandState.Value = band;
        }
    }

    private static string Who(TacticalState state, TacticalActorId id)
    {
        foreach (TacticalActor actor in state.SortedActors)
        {
            if (actor.Id == id)
                return actor.AgentId == AgentId.None ? actor.Kind.ToString() : $"{actor.Kind}/{actor.AgentId.Value}";
        }

        return id.ToString();
    }

    /// <summary>
    /// The first step at which two traces order differently, or null when they never do.
    /// </summary>
    public static (long Step, string Here, string There)? FirstDivergence(Trace a, Trace b)
    {
        int shared = Math.Min(a.Orders.Count, b.Orders.Count);

        for (int i = 0; i < shared; i++)
        {
            if (Line(a.Orders[i]) != Line(b.Orders[i]))
                return (a.Orders[i].Step, Line(a.Orders[i]), Line(b.Orders[i]));
        }

        if (a.Orders.Count != b.Orders.Count)
        {
            OrderLine tail = a.Orders.Count > b.Orders.Count ? a.Orders[^1] : b.Orders[^1];
            return (tail.Step, $"{a.Policy} has {a.Orders.Count} orders", $"{b.Policy} has {b.Orders.Count} orders");
        }

        return null;
    }

    private static string Line(OrderLine line)
        => $"s{line.Step} a{line.ActorId} {line.Action} conn{line.ConnectionId} int{line.InteractableId} tgt{line.TargetActorId} {line.Posture}";

    /// <summary>The CLI entry point: trace a seed under every policy and compare them.</summary>
    public static int Execute(string[] args)
    {
        Options options = Options.Parse(args);

        int template = options.Int("template", 11001);
        int seed = options.Int("seed", 1);
        string objectiveName = options.String("objective", "stealdata");
        var objective = (TableObjectiveType)Enum.Parse<TableObjectiveType>(objectiveName, ignoreCase: true);
        int window = options.Int("window", 40);

        ProjectSpy.Tables.SiteTemplate? row = null;

        foreach (ProjectSpy.Tables.SiteTemplate candidate in SimulationRules.AllSiteTemplates())
        {
            if (candidate.Id == template)
                row = candidate;
        }

        if (row is null)
        {
            Console.Error.WriteLine($"no site template {template}");
            return 1;
        }

        var traces = new List<Trace>();

        foreach (SquadPolicy policy in Enum.GetValues<SquadPolicy>())
        {
            Trace trace = Run((ulong)seed, policy, template, row.Tier, objective, 1);
            traces.Add(trace);
            Console.WriteLine(trace.Summary);

            foreach (string line in trace.LostReport)
                Console.WriteLine(line);
        }

        Console.WriteLine();

        foreach (int i in Enumerable.Range(0, traces.Count))
        {
            for (int j = i + 1; j < traces.Count; j++)
            {
                (long Step, string Here, string There)? divergence = FirstDivergence(traces[i], traces[j]);

                Console.WriteLine(divergence is null
                    ? $"{traces[i].Policy,-9} vs {traces[j].Policy,-9} IDENTICAL — no step differs"
                    : $"{traces[i].Policy,-9} vs {traces[j].Policy,-9} first differ at step {divergence.Value.Step}: "
                      + $"[{divergence.Value.Here}] vs [{divergence.Value.There}]");
            }
        }

        // One journal per policy, named, rather than only the first: the four runs happen
        // in one process, and a reader comparing an alarm escalation in one policy
        // against another needs both on screen at once.
        if (options.Has("journal"))
        {
            foreach (PolicyTrace.Trace traced in traces)
            {
                Console.WriteLine();
                Console.WriteLine($"--- {traced.Policy}: journal ---");

                foreach (JournalLine line in traced.Journal)
                    Console.WriteLine($"  step {line.Step,5} {line.Event,-22} {line.Detail}");
            }

            return 0;
        }

        if (!options.Has("orders"))
            return 0;

        PolicyTrace.Trace sample = traces[0];

        Console.WriteLine();
        Console.WriteLine($"--- {sample.Policy}: first {window} orders ---");

        foreach (OrderLine line in sample.Orders.Take(window))
            Console.WriteLine($"  {Line(line)}");

        Console.WriteLine();
        Console.WriteLine($"--- {sample.Policy}: journal (first {window}) ---");

        foreach (JournalLine line in sample.Journal.Take(window))
            Console.WriteLine($"  step {line.Step,5} {line.Event,-14} {line.Detail}");

        return 0;
    }
}