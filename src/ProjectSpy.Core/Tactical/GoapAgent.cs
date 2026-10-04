namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Why an NPC's plan was thrown away and rebuilt.
/// </summary>
/// <remarks>
/// The brief asks for replanning to be <em>event-driven, not polled</em>, and the enum
/// is the audit trail for that: every replan can name what caused it. A planner that
/// reconsiders because a counter crossed a threshold is doing something different from
/// one that reconsiders because a door opened, and the two are worth distinguishing when
/// a guard does something surprising.
/// </remarks>
public enum GoapInvalidationReason
{
    /// <summary>Nothing yet. The NPC is on its first plan.</summary>
    None = 0,

    /// <summary>It perceived the team for the first time, or lost them.</summary>
    PerceptionChanged = 1,

    /// <summary>Its own suspicion crossed a band.</summary>
    SuspicionThreshold = 2,

    /// <summary>It heard something.</summary>
    NoiseHeard = 3,

    /// <summary>A colleague it can see went down.</summary>
    AllyDown = 4,

    /// <summary>The site alarm moved band.</summary>
    AlarmBandChanged = 5,

    /// <summary>A door opened that should be shut.</summary>
    SuspiciousDoor = 6,

    /// <summary>It found a body.</summary>
    BodyFound = 7,

    /// <summary>A colleague was found missing.</summary>
    ColleagueMissing = 8,

    /// <summary>Something it guards was interfered with.</summary>
    ObjectiveTampered = 9,

    /// <summary>It finished the plan it was executing.</summary>
    PlanExhausted = 10,

    /// <summary>Its current action finished.</summary>
    ActionCompleted = 11,

    /// <summary>It was interrupted mid-plan by something more urgent.</summary>
    Interrupted = 12,
}

/// <summary>
/// One NPC's planner: its cached plan, what it knows, and why it last replanned.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cache is the point.</b> Re-running A* for thirty guards every 100 ms step
/// would cost far more than the brief's 2 ms budget, so a plan is computed once and
/// reused until an event invalidates it. This type is where that decision lives, which
/// means "why is this guard still walking the same route" has an answer
/// (<see cref="LastInvalidation"/>) rather than being a mystery.
/// </para>
/// <para>
/// <b>Invalidation is explicit, not inferred.</b> Nothing polls this class each step
/// looking for a reason to replan; the systems that notice something — perception,
/// noise, the alarm — call <see cref="Invalidate"/>. That is what the brief's "not polled
/// every step" means in practice, and it is also what bounds the work: at most the
/// number of real events, rather than one search per NPC per step.
/// </para>
/// <para>
/// <b>One plan at a time.</b> The plan is the NPC's declared intention and the
/// executor advances it; there is no queue of intentions, because an NPC holding two
/// plans would resolve them in whatever order the step pipeline happened to reach them.
/// </para>
/// </remarks>
public sealed class GoapAgent
{
    /// <summary>The NPC this agent plans for.</summary>
    public TacticalActor Actor { get; }

    /// <summary>What this NPC has been told or has noticed. Cleared by a full replan.</summary>
    public GoapFindings Findings { get; } = new();

    /// <summary>
    /// The plan being executed, or <see cref="GoapPlan.None"/>.
    /// </summary>
    /// <remarks>
    /// Public and settable because the mission's step executor is what advances it: the
    /// planner decides <em>what</em>, and the executor is the only thing that may say a
    /// step of it is done. Making it private would mean the executor reaching through a
    /// method for every step of every action, which is worse than a documented setter.
    /// </remarks>
    public GoapPlan Plan { get; set; } = GoapPlan.None;

    /// <summary>Why the current plan was chosen, for the debug surface.</summary>
    public GoapInvalidationReason LastInvalidation { get; private set; } = GoapInvalidationReason.None;

    /// <summary>How many times this NPC has replanned this mission.</summary>
    public int ReplanCount { get; private set; }

    /// <summary>Search nodes the most recent plan cost.</summary>
    public int LastNodesExpanded { get; private set; }

    /// <summary>The step on which the current plan was made.</summary>
    public long PlannedOnStep { get; private set; }

    /// <summary>
    /// The suspicion band this agent last reacted to, so a threshold crossing is
    /// detected by comparing against the previous value rather than by testing
    /// thresholds every step.
    /// </summary>
    private int _lastSuspicionBand = -1;

    /// <summary>The alarm band this agent last reacted to.</summary>
    private AlarmBand _lastAlarmBand = AlarmBand.Calm;

    /// <summary>
    /// The highest perception level already reported as a trigger, so a guard is told
    /// about a sighting once rather than on every step it remains true.
    /// </summary>
    /// <remarks>
    /// A "seen" trigger is level-triggered, not edge-triggered, deliberately. A guard
    /// that has identified the team keeps knowing it, and re-requesting a plan every
    /// step would let one guard consume the whole per-step budget for as long as the
    /// player stayed visible. Recording the high-water mark means the trigger fires on
    /// the step perception <em>improves</em>, which is the only time it carries news.
    /// </remarks>
    public PerceptionLevel LastNotifiedPerception { get; set; } = PerceptionLevel.None;

    /// <summary>The step of the last noise already reported as a trigger.</summary>
    /// <remarks>
    /// Also edge-triggered, on the same reasoning: a noise stays audible to a guard for
    /// as long as it is fresh, and only the step it was first heard on is news.
    /// </remarks>
    public long LastNotifiedNoiseStep { get; set; } = -1;

    /// <summary>Whether a visible downed colleague has already been reported.</summary>
    public bool LastNotifiedAllyDown { get; set; }

    /// <summary>Whether a replan has been requested and not yet serviced.</summary>
    public bool NeedsPlan { get; private set; }

    /// <summary>The step on which a replan was first requested.</summary>
    /// <remarks>
    /// Recorded so that a starved NPC can be identified in a test rather than merely
    /// looking slow. The fair queue is supposed to prevent starvation; this is how a
    /// regression in it would be noticed.
    /// </remarks>
    public long RequestedOnStep { get; private set; }

    /// <summary>Creates an agent for one NPC.</summary>
    public GoapAgent(TacticalActor actor)
        => Actor = actor ?? throw new ArgumentNullException(nameof(actor));

    /// <summary>
    /// This NPC has noticed a door on its own route standing open.
    /// </summary>
    /// <remarks>
    /// Compound on purpose: noticing a door is not only a fact about the world, it is
    /// also what makes a guard uneasy, and that unease is what lets
    /// <c>goal.report_missing_colleague</c> and <c>goal.raise_alarm</c> out-score the
    /// standing order to patrol. Recording the fact without the feeling would leave a
    /// guard that knows a door is open and cannot be moved to do anything about it.
    /// </remarks>
    public void NoticeOpenDoor(int doorsSeen, long step)
    {
        Findings.SuspiciousDoorsSeen = Math.Max(Findings.SuspiciousDoorsSeen, doorsSeen);
        Actor.Suspicion.Add(DiscoverySuspicionGain);
        Invalidate(GoapInvalidationReason.SuspiciousDoor, step);
    }

    /// <summary>
    /// This NPC has noticed that a colleague is not at the post they should be holding.
    /// </summary>
    /// <remarks>
    /// The brief's second behaviour. Recorded as a compound event for the same reason as
    /// <see cref="NoticeOpenDoor"/>: the noticing has to make the guard uneasy, or the
    /// goal is unreachable in practice — every factor on
    /// <c>goal.report_missing_colleague</c> is the alarm and lost composure, and in a
    /// quiet building with a calm guard both are zero.
    /// </remarks>
    public void NoticeColleagueMissing(long step)
    {
        Findings.ColleagueMissing = true;
        Actor.Suspicion.Add(DiscoverySuspicionGain);
        Invalidate(GoapInvalidationReason.ColleagueMissing, step);
    }

    /// <summary>
    /// This NPC has found a body.
    /// </summary>
    /// <remarks>
    /// Also implies <see cref="GoapFindings.ContactIdentified"/> in the sense that
    /// matters: somebody was killed here, so this NPC now knows an intruder has been in
    /// the building. It deliberately does <em>not</em> put anything in
    /// <see cref="PerceptionMemory"/> — a corpse is not a sighting, and a guard that has
    /// found a body has not seen anybody alive to chase.
    /// </remarks>
    public void NoticeBody(long step)
    {
        Findings.BodyFound = true;
        Findings.ToldOfIntruder = true;
        Actor.Suspicion.Add(BodySuspicionGain);
        Invalidate(GoapInvalidationReason.BodyFound, step);
    }

    /// <summary>Suspicion gained by finding something wrong on one's own round.</summary>
    /// <remarks>
    /// From <c>goap_rule.suspicion_threshold</c> rather than a literal: how shaken a
    /// guard gets by an open door is a balance decision, and the same threshold the
    /// planner uses for "this guard is now aware" is the right anchor for "this guard has
    /// just found something wrong".
    /// </remarks>
    private static int DiscoverySuspicionGain
        => Math.Max(1, SimulationRules.Goap("goap_suspicion_threshold", 20));

    /// <summary>
    /// Suspicion gained by finding a body, which is a different order of event from an
    /// open door and is worth more.
    /// </summary>
    private static int BodySuspicionGain => DiscoverySuspicionGain * 3;

    /// <summary>
    /// Requests a replan, recording why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>first</em> reason wins. A guard that finds a body on the same step it
    /// hears a noise has one thing to do next, and recording "body found" rather than
    /// whichever system happened to call in last means the debug surface names the most
    /// significant thing that happened to it. Overwriting would make the explanation
    /// depend on the order unrelated systems run in.
    /// </para>
    /// <para>
    /// Idempotent while a replan is already pending, so six guards noticing the same
    /// body costs six entries in the queue rather than thirty-six.
    /// </para>
    /// </remarks>
    public void Invalidate(GoapInvalidationReason reason, long step)
    {
        if (NeedsPlan)
            return;

        NeedsPlan = true;
        RequestedOnStep = step;
        LastInvalidation = reason;
    }

    /// <summary>
    /// Watches for the event-driven triggers that do not come from another system:
    /// suspicion crossing a band and the alarm changing band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called every planning step, which looks like polling and is not: it compares
    /// this NPC's last known band against the current one and only raises a replan when
    /// they differ. The comparison is O(1) and the common case — nothing changed — does
    /// no work. What it deliberately avoids is re-planning on a schedule, which is the
    /// thing that would actually cost the frame budget.
    /// </para>
    /// <para>
    /// Both thresholds are read from <c>goap_rule</c> so a designer tuning how twitchy
    /// guards are edits a CSV.
    /// </para>
    /// </remarks>
    public void ObserveTriggers(GoapPriorityInputs inputs, AlarmBand alarmBand, long step)
    {
        int band = BandOf(inputs.Curiosity);

        if (band != _lastSuspicionBand)
        {
            _lastSuspicionBand = band;
            Invalidate(GoapInvalidationReason.SuspicionThreshold, step);
        }

        if (alarmBand != _lastAlarmBand)
        {
            _lastAlarmBand = alarmBand;
            Invalidate(GoapInvalidationReason.AlarmBandChanged, step);
        }
    }

    /// <summary>
    /// The suspicion band a value falls in, at the table's threshold.
    /// </summary>
    /// <remarks>
    /// Two bands rather than <see cref="SuspicionTier"/>'s four. The table gives one
    /// number, and the brief names two events — becoming aware, and becoming certain.
    /// Mapping onto the existing four-tier enum instead would mean inventing two of the
    /// thresholds here, which is exactly the hand-typed balance number rule 3 forbids.
    /// </remarks>
    private static int BandOf(int suspicion)
    {
        int threshold = Math.Max(1, SimulationRules.Goap("goap_suspicion_threshold", 20));

        if (suspicion >= SuspicionTiers.HostileThreshold)
            return 2;

        return suspicion >= threshold ? 1 : 0;
    }

    /// <summary>
    /// Records a freshly computed plan, clearing the pending flag.
    /// </summary>
    /// <remarks>
    /// Called by the director after a search, whether or not the search found anything.
    /// Clearing the flag on failure as well as success is deliberate: an NPC that cannot
    /// reach its goal should go back to doing what it was doing rather than spinning on
    /// the same unsatisfiable goal for the rest of the mission.
    /// </remarks>
    public void Commit(GoapPlan plan, long step, int nodesExpanded)
    {
        Plan = plan ?? GoapPlan.None;
        PlannedOnStep = step;
        LastNodesExpanded = nodesExpanded;
        ReplanCount++;
        NeedsPlan = false;
    }

    /// <summary>
    /// Notes that an action from the current plan has finished.
    /// </summary>
    /// <remarks>
    /// Advances the plan by one and, when nothing is left, asks for a replan. The
    /// replan request rather than an immediate search is what keeps execution cheap: a
    /// finishing action costs one queue entry here and one budgeted search when the
    /// director next gets to this NPC.
    /// </remarks>
    public void AdvancePlan(long step)
    {
        if (Plan == GoapPlan.None || !Plan.HasActions)
            return;

        Plan = Plan.Advance();

        if (!Plan.HasActions)
            Invalidate(GoapInvalidationReason.PlanExhausted, step);
    }

    /// <summary>
    /// Applies an action's effects to this NPC's knowledge.
    /// </summary>
    /// <remarks>
    /// The bridge from "the planner decided" to "the world changed". An NPC that closes
    /// a door has, from that moment, closed a door — and the next plan must see that or
    /// it will close the same door again.
    /// </remarks>
    public void NoteActionCompleted(GoapActionDef action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        foreach (GoapKeys.Condition effect in action.Effects)
        {
            if (!effect.Negated)
                continue;

            // A negated effect names something that has stopped being true, which is the
            // only kind worth translating into a finding. Recording it as a finding
            // rather than as world-state surgery keeps the builder the single place
            // that assembles a world.
            switch (effect.Key)
            {
                case GoapKey.SuspiciousDoorsClosed:
                    Findings.SuspiciousDoorsSeen = 0;
                    break;
                case GoapKey.ColleagueReportedMissing:
                    Findings.ColleagueMissing = false;
                    break;
                case GoapKey.BodySearched:
                    break;
            }
        }
    }

    /// <summary>
    /// A snapshot of everything a player or a test needs to explain this NPC.
    /// </summary>
    /// <remarks>
    /// <b>Mandatory, not optional.</b> The brief is blunt that a stealth game whose AI
    /// is opaque is a frustrating one, and the cost of getting this wrong is not a bug
    /// report — it is a player who cannot work out that hiding a body in a cupboard was
    /// a mistake, and who concludes the guards are cheating.
    /// </remarks>
    public GoapDebugInfo DebugInfo(IReadOnlyList<GoapGoalScore> rankedGoals, long step)
    {
        PerceptionMemory memory = Actor.Memory;

        return new GoapDebugInfo
        {
            ActorId = Actor.Id,
            GoalId = Plan.GoalId,
            GoalNameKey = Plan.GoalNameKey,
            PlanStatus = Plan.Status,
            NextActionId = Plan.NextActionId,
            RemainingActionIds = Plan.ActionIds,
            PlanCost = Plan.TotalCost,
            NodesExpanded = LastNodesExpanded,
            Suspicion = Actor.Suspicion.Value,
            SuspicionTier = Actor.Suspicion.Tier,
            LastSeenWhere = memory.LastSeen,
            LastSeenStep = memory.LastSeenStep,
            StepsSinceContact = memory.StepsSinceContact(step),
            HasIdentification = memory.HasIdentification,
            LastNoiseOrigin = memory.LastNoiseOrigin,
            LastNoiseStep = memory.LastNoiseStep,
            LastInvalidation = LastInvalidation,
            ReplanCount = ReplanCount,
            PlannedOnStep = PlannedOnStep,
            NeedsPlan = NeedsPlan,
            RequestedOnStep = RequestedOnStep,
            CarriesRadio = CarriesRadio,
            RadioTransmitted = Findings.RadioTransmitted,
            HidingPlaceQuality = Findings.HidingPlaceQuality,
            SuspiciousDoorsSeen = Findings.SuspiciousDoorsSeen,
            RankedGoals = rankedGoals,
        };
    }

    /// <summary>
    /// True when this guard carries a radio, from <c>guard_archetype.carries_radio</c>.
    /// </summary>
    /// <remarks>
    /// Read from the archetype rather than copied onto the actor, because the archetype
    /// is immutable mission data and a copy would be a second source of truth that could
    /// disagree with it.
    /// </remarks>
    public bool CarriesRadio
        => SimulationRules.GuardArchetypeFor(Actor.GuardArchetypeId)?.CarriesRadio ?? false;

    /// <summary>Forgets everything, for a mission whose alarm has fully reset.</summary>
    public void Reset()
    {
        Findings.Clear();
        Plan = GoapPlan.None;
        LastInvalidation = GoapInvalidationReason.None;
        NeedsPlan = false;
        ReplanCount = 0;
        _lastSuspicionBand = -1;
        _lastAlarmBand = AlarmBand.Calm;
    }
}

/// <summary>
/// Everything needed to explain one NPC's behaviour, as structured data.
/// </summary>
/// <remarks>
/// Keys, ids and numbers only — never prose (rule 4). Presentation maps
/// <see cref="GoalNameKey"/> and <see cref="LastInvalidation"/> to localized text.
/// </remarks>
public sealed class GoapDebugInfo
{
    /// <summary>Which NPC this describes.</summary>
    public TacticalActorId ActorId { get; init; }

    /// <summary>The <c>goap_goal</c> row being pursued, or zero.</summary>
    public int GoalId { get; init; }

    /// <summary>Its localization key.</summary>
    public string GoalNameKey { get; init; } = string.Empty;

    /// <summary>How the last planning attempt ended.</summary>
    public GoapPlanStatus PlanStatus { get; init; }

    /// <summary>The next <c>goap_action</c> id, or zero.</summary>
    public int NextActionId { get; init; }

    /// <summary>Everything after that, in plan order.</summary>
    public IReadOnlyList<int> RemainingActionIds { get; init; } = Array.Empty<int>();

    /// <summary>Planner cost of the current plan.</summary>
    public int PlanCost { get; init; }

    /// <summary>Search nodes the current plan cost.</summary>
    public int NodesExpanded { get; init; }

    /// <summary>Suspicion, 0-100.</summary>
    public int Suspicion { get; init; }

    /// <summary>Which suspicion band that is.</summary>
    public SuspicionTier SuspicionTier { get; init; }

    /// <summary>Where this NPC last actually saw the team.</summary>
    /// <remarks>
    /// A position rather than a room, deliberately: the gap between "where they were"
    /// and "where they are" is what the player is playing against, and rounding this to
    /// a room name would hide the part that matters.
    /// </remarks>
    public TacticalPosition LastSeenWhere { get; init; }

    /// <summary>The step of that sighting.</summary>
    public long LastSeenStep { get; init; }

    /// <summary>Steps since it, or a large number if never.</summary>
    public long StepsSinceContact { get; init; }

    /// <summary>True when the sighting was an identification rather than a glimpse.</summary>
    public bool HasIdentification { get; init; }

    /// <summary>Where the last heard noise came from.</summary>
    public TacticalPosition LastNoiseOrigin { get; init; }

    /// <summary>The step it was heard on.</summary>
    public long LastNoiseStep { get; init; }

    /// <summary>Why this NPC last replanned.</summary>
    public GoapInvalidationReason LastInvalidation { get; init; }

    /// <summary>How many times it has replanned.</summary>
    public int ReplanCount { get; init; }

    /// <summary>The step the current plan was made on.</summary>
    public long PlannedOnStep { get; init; }

    /// <summary>True when a replan has been requested and not yet serviced.</summary>
    public bool NeedsPlan { get; init; }

    /// <summary>When that request was made, for detecting a starved NPC.</summary>
    public long RequestedOnStep { get; init; }

    /// <summary>True when this guard carries a radio.</summary>
    public bool CarriesRadio { get; init; }

    /// <summary>True once this guard has transmitted on it.</summary>
    public bool RadioTransmitted { get; init; }

    /// <summary>Quality of the last place this NPC hid something, 0-100.</summary>
    public int HidingPlaceQuality { get; init; }

    /// <summary>How many open doors this NPC has found.</summary>
    public int SuspiciousDoorsSeen { get; init; }

    /// <summary>Every goal this NPC could have pursued, with its score.</summary>
    public IReadOnlyList<GoapGoalScore> RankedGoals { get; init; } = Array.Empty<GoapGoalScore>();
}