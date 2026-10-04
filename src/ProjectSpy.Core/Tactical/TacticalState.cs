using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// How far through the mission's own objective the team is.
/// </summary>
/// <remarks>
/// <para>
/// Progress is a step budget rather than a flag because every objective in this game is
/// "do a thing for a while" — hack a terminal for fifty steps, exfiltrate for eighty —
/// and a boolean would have to be set at an arbitrary moment. Counting steps makes the
/// cost of every objective legible: the player can see that exfiltrating takes five
/// times as long as a lockpick, which is a decision, where a flag would only tell them
/// it took a while.
/// </para>
/// <para>
/// <b>Blocked at 100 per cent, not at 100.</b> This is the shape the tables describe:
/// intel percentage, exfiltration progress, and a rescue search are all capped
/// percentages that something in the building can hold you at. Modelling it that way
/// means a locked vault door is a rule rather than a special case.
/// </para>
/// </remarks>
public sealed class ObjectiveProgress
{
    /// <summary>Localization key for what the objective is.</summary>
    public string NameKey { get; init; } = string.Empty;

    /// <summary>Steps the objective takes in total.</summary>
    public int TotalSteps { get; init; }

    /// <summary>Steps spent on it so far.</summary>
    public int SpentSteps { get; set; }

    /// <summary>
    /// True once the objective has been completed.
    /// </summary>
    /// <remarks>
    /// Latched separately from the percentage rather than being derived from it, because
    /// a player who reaches 100 per cent and is then interrupted must not drop back to
    /// 95. Knowledge does not un-happen, and neither does a finished hack.
    /// </remarks>
    public bool IsComplete { get; set; }

    /// <summary>How far along, in percent. Never above 100.</summary>
    public int Percent => TotalSteps <= 0
        ? 100
        : (int)Math.Min(100L, (long)SpentSteps * 100L / TotalSteps);

    /// <summary>Steps still owed, or zero once complete.</summary>
    public int RemainingSteps => IsComplete || TotalSteps <= 0
        ? 0
        : Math.Max(0, TotalSteps - SpentSteps);

    /// <inheritdoc/>
    public override string ToString()
        => $"{NameKey} {Percent}%{(IsComplete ? " complete" : string.Empty)}";
}

/// <summary>How far the site-wide alarm has been pushed, and in which band.</summary>
/// <remarks>
/// <para>
/// <b>Derived from awareness, not counted up by an event.</b> The brief is explicit that
/// the alarm must be "driven by NPC awareness rather than by an abstract counter", and
/// this type is how that is honoured: nothing in the simulation ever writes a number
/// into <see cref="Level"/> on its own initiative. Each step the alarm is recomputed from
/// what the NPCs actually know, and it may only move towards that figure.
/// </para>
/// <para>
/// <b>Movement is one-way per step and slow.</b> A level that tracked its target exactly
/// would be wrong in the way that matters most: a guard who looks away would drop the
/// alarm instantly, and the player's correct read on that would be "the alarm only goes
/// up", which is true and is the entire risk model of the game.
/// </para>
/// <para>
/// <b>Rise and fall at different speeds.</b> Alarm falls far more slowly than it rises,
/// because a site that forgot a break-in as fast as it noticed one would make hiding a
/// temporary state instead of a plan.
/// </para>
/// </remarks>
public sealed class AlarmState
{
    /// <summary>Top of the scale. Reaching it ends the mission.</summary>
    public const int Max = 100;

    /// <summary>Where the alarm stands, 0 to <see cref="Max"/>.</summary>
    public int Level { get; private set; }

    /// <summary>
    /// The band <see cref="Level"/> falls in, from the design document's five bands.
    /// </summary>
    public AlarmBand Band => BandFor(Level);

    /// <summary>True once the site is burned and the mission is over.</summary>
    public bool IsBurned => Band == AlarmBand.Burned;

    /// <summary>
    /// Recomputes the alarm from what the NPCs currently know.
    /// </summary>
    /// <param name="actors">Everyone in the mission.</param>
    /// <param name="step">The step being resolved, for the rate.</param>
    /// <returns>True when the band changed, which is the only thing a caller has to react to.</returns>
    public bool Update(IReadOnlyList<TacticalActor> actors, long step)
    {
        int target = ComputeAwarenessPressure(actors);

        // Rate-limited rather than snapped. Without this the alarm would equal its target
        // every step, which would make "how loud was that" and "how long until it settles"
        // the same question and would remove the lag that makes the meter readable.
        int perStep = RisePerStep;
        if (target < Level)
            perStep = FallPerStepPer1000Steps;

        int moved = target > Level
            ? Math.Min(target - Level, perStep)
            : Math.Min(Level - target, perStep);

        if (moved == 0)
            return false;

        AlarmBand before = Band;
        Level += target > Level ? moved : -moved;

        if (Level < 0)
            Level = 0;

        if (Level > Max)
            Level = Max;

        return Band != before;
    }

    /// <summary>
    /// Raises the alarm directly, for the few noises that are the alarm rather than
    /// something an NPC has to work out.
    /// </summary>
    public void Raise(int amount)
    {
        if (amount <= 0)
            return;

        long raised = (long)Level + amount;
        Level = raised > Max ? Max : (int)raised;
    }

    /// <summary>Sets the alarm outright. Load and test setup only.</summary>
    public void Set(int level)
        => Level = level < 0 ? 0 : level > Max ? Max : level;

    /// <summary>Clears the alarm. Used by a device that suppresses it.</summary>
    public void Clear() => Level = 0;

    /// <summary>The band a level falls in, from the design document.</summary>
    public static AlarmBand BandFor(int level) => level switch
    {
        >= 100 => AlarmBand.Burned,
        >= 76 => AlarmBand.Lockdown,
        >= 51 => AlarmBand.Alert,
        >= 26 => AlarmBand.Suspicious,
        _ => AlarmBand.Calm,
    };

    /// <summary>The lowest level that reaches a band.</summary>
    public static int FloorOf(AlarmBand band) => band switch
    {
        AlarmBand.Calm => 0,
        AlarmBand.Suspicious => 26,
        AlarmBand.Alert => 51,
        AlarmBand.Lockdown => 76,
        AlarmBand.Burned => 100,
        _ => 0,
    };

    /// <summary>
    /// The alarm level the site's NPCs are collectively justifying right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dominant suspicion sets the floor, and each additional guard who is aware adds
    /// a share on top — so one guard who is nearly certain is a serious problem, and six
    /// guards who are mildly uneasy are a worse one, but never both at once.
    /// </para>
    /// <para>
    /// Only guards count. Civilians are excluded because a panic is not awareness: it
    /// is what awareness <em>causes</em>, and feeding it back into the meter would let a
    /// mission escalate itself from nothing but people running. The squad is excluded
    /// for the mirror-image reason — the meter is the <em>site's</em> awareness, and
    /// counting the infiltrators among the things the site is aware of would let a
    /// mission raise its own alarm with nobody having noticed anything.
    /// </para>
    /// </remarks>
    private static int ComputeAwarenessPressure(IReadOnlyList<TacticalActor> actors)
    {
        int highest = 0;
        int awareOthers = 0;

        foreach (TacticalActor actor in actors)
        {
            if (!actor.IsGuard || actor.Condition != ActorCondition.Active)
                continue;

            int value = actor.Suspicion.Value;

            if (value > highest)
                highest = value;

            if (actor.Suspicion.IsAware)
                awareOthers++;
        }

        if (highest <= 0)
            return 0;

        // The first aware guard is the one that matters; each further one adds a fifth as
        // much again, and the total is capped so a building full of guards cannot push
        // the meter to maximum on its own.
        int reinforcement = (awareOthers - 1) * AdditionalAwarenessPercent / 100;

        return Math.Min(Max, highest + reinforcement);
    }

    /// <summary>Percent of the dominant suspicion added per further aware guard.</summary>
    private const int AdditionalAwarenessPercent = 20;

    /// <summary>How far the alarm may rise in one step.</summary>
    private const int RisePerStep = 3;

    /// <summary>
    /// How far the alarm may fall in one step.
    /// </summary>
    /// <remarks>
    /// Far slower than it rises, and deliberately so. Alarm that fell as fast as it rose
    /// would mean standing still undid a firefight, which would make the correct strategy
    /// to fight and then wait — and the correct strategy here is to not be found at all.
    /// </remarks>
    private const int FallPerStepPer1000Steps = 1;

    /// <inheritdoc/>
    public override string ToString() => $"{Level}/100 {Band}";
}

/// <summary>
/// How the running mission ended, if it has.
/// </summary>
/// <remarks>
/// The distinction the brief insists on lives here: a mission can end with the team
/// alive, with the team dead, and with the team <em>taken</em>, and the third is neither
/// of the others.
/// </remarks>
public enum MissionOutcome
{
    /// <summary>Still running.</summary>
    InProgress = 0,

    /// <summary>Everyone who went in came out. The good ending.</summary>
    Exfiltrated = 1,

    /// <summary>The site reached the top band and the team could not get out.</summary>
    Burned = 2,

    /// <summary>The objective was done and the team walked away from it.</summary>
    Aborted = 3,
}

/// <summary>
/// The state of the tactical mission currently being played, or null when none is.
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>ActiveMission</c> slot on <see cref="WorldState"/>: non-null exactly
/// while <see cref="GameSession"/> is in <see cref="SessionMode.Tactical"/>.
/// </para>
/// <para>
/// <b>The building is here, the seed is on the layout.</b> The structure itself — floors,
/// rooms, connections, lights, guards — is committed to the layout rather than copied
/// into the state, so this object holds a reference to a structure that regenerates
/// identically from <see cref="SiteLayout.MapSeed"/>. Only <em>progress</em> lives here:
/// where the actors are, which doors are shut, what the alarm is doing. That split is
/// what makes the save small enough to reason about.
/// </para>
/// <para>
/// <b>Nothing here is derived lazily.</b> Every collection is populated at mission start
/// and mutated only by the step pipeline. A tactical state that computed something on
/// access would be computing it at a moment the caller chose, and the pipeline order is
/// part of the save contract.
/// </para>
/// </remarks>
public sealed class TacticalState
{
    /// <summary>What stage 4c added to this type. Kept as a constant rather than as
    /// scattered <c>TODO</c> comments so the gap is greppable and cannot quietly rot.</summary>
    public const string PENDING_STAGE_4D =
        "GOAP goal selection, goal satisfaction conditions, and the replanning budget";

    /// <summary>The mission being played. Matches a key in <see cref="WorldState.ActiveMissions"/>.</summary>
    public int MissionId { get; init; }

    /// <summary>The site the mission takes place in. Foreign key into site_template (stage 2).</summary>
    public int SiteId { get; init; }

    /// <summary>The strategic tick the mission was dispatched on.</summary>
    public Tick StartedOnTick { get; init; }

    /// <summary>
    /// How far into the mission the tactical simulation has run.
    /// </summary>
    /// <remarks>
    /// Stored as a <see cref="long"/> step count rather than a <see cref="Step"/>
    /// wrapper so the canonical serializer and the state hash can write it as one
    /// integer, and so the mission-time conversion at mission end reads a plain number
    /// rather than reaching through a type it does not otherwise need.
    /// </remarks>
    public long Step { get; set; }

    /// <summary>
    /// True once the elapsed steps have been converted to strategic ticks.
    /// </summary>
    /// <remarks>
    /// This is the "exactly once" half of knowledge.md rule 13. Without it, a mission
    /// closed twice — a quit-to-menu followed by a load, say — silently advances the
    /// base clock a second time, and the player loses an hour for a technicality they
    /// never see.
    /// </remarks>
    public bool TimeConverted { get; set; }

    // ---- structure -----------------------------------------------------------

    /// <summary>The building the mission is played in.</summary>
    public SiteLayout Layout { get; init; } = null!;

    /// <summary>Who is standing in it. In ascending id order, always.</summary>
    public List<TacticalActor> Actors { get; } = new();

    /// <summary>The current state of every connection that has one.</summary>
    public Dictionary<SiteConnectionId, ConnectionState> Doors { get; } = new();

    /// <summary>
    /// Doors the <em>alarm</em> shut, as opposed to doors the player shut.
    /// </summary>
    /// <remarks>
    /// The distinction is what lets the site relax without tidying up after the player:
    /// a door here reopens when the alarm falls, and a door that is not here stays
    /// however it was left. Without it, a false alarm would quietly reopen every door
    /// the team had closed behind them, and the cost of nearly being caught would be
    /// nothing at all.
    /// </remarks>
    public HashSet<SiteConnectionId> DoorsShutByAlarm { get; } = new();

    /// <summary>Which emitters are working, and what is lit where.</summary>
    public LightState Lights { get; set; } = null!;

    /// <summary>
    /// The site-wide alarm, in five bands, driven by what the NPCs actually know.
    /// </summary>
    public AlarmState Alarm { get; } = new();

    /// <summary>
    /// How much faster guards move under the current band, as a percentage of the
    /// archetype's patrol speed.
    /// </summary>
    /// <remarks>
    /// Held here rather than recomputed by every caller from
    /// <see cref="AlarmBand"/>, so the planner, the perception phase and the UI cannot
    /// disagree about what "alerted" means. Written only by
    /// <see cref="AlarmSystem.OnBandChanged"/>.
    /// </remarks>
    public int GuardSpeedMultiplierPercent { get; set; } = 100;

    /// <summary>
    /// How far the mission's own objective has got.
    /// </summary>
    /// <remarks>
    /// Settable rather than get-only because the cost of an objective depends on what it
    /// is: a hack costs the hack row's steps and an exfiltration costs its own, and the
    /// mission is not playable until the factory has resolved which. Mutated in place
    /// thereafter — <see cref="ObjectiveProgress"/> is a class precisely so that a
    /// hundred steps of progress do not mean reallocating the object every step.
    /// </remarks>
    public ObjectiveProgress Objective { get; set; } = new();

    /// <summary>How the mission ended, when it has.</summary>
    public MissionOutcome Outcome { get; set; } = MissionOutcome.InProgress;

    // ---- squad (stage 4e) ----------------------------------------------------
    //
    // Held here rather than in a strategic singleton because a squad is a property of a
    // mission: two missions can be in flight, a save must carry this one with everything
    // else about it, and a static controller would silently belong to whichever mission
    // started most recently.

    /// <summary>
    /// Who was dispatched, in what role, and carrying what.
    /// </summary>
    /// <remarks>
    /// Non-null exactly when <see cref="Squad"/> has more than the default deployment.
    /// Null on a mission built before stage 4e or by a test that only wanted actors, and
    /// every reader treats null as "no squad, so no orders, no behaviours" rather than
    /// lazily constructing one — a lazily created composition would validate against a
    /// roster nobody chose.
    /// </remarks>
    public SquadComposition? Composition { get; set; }

    /// <summary>
    /// Who the player is driving and what the rest of the squad has been told.
    /// </summary>
    public SquadControl Control { get; set; } = new();

    /// <summary>The forward command post, when this site has one.</summary>
    public CommandPostState CommandPost { get; set; } = new();

    /// <summary>How the objective is going.</summary>
    public ObjectiveOutcome ObjectiveOutcome { get; set; } = ObjectiveSystem.Create(
        ProjectSpy.Tables.ObjectiveType.StealData);

    /// <summary>True when the player called the abort.</summary>
    public bool AbortCalled { get; set; }

    /// <summary>
    /// The step the abort was last called on, for the order's cooldown.
    /// </summary>
    /// <remarks>
    /// Kept even after the abort completes, so that aborting, changing your mind and
    /// aborting again inside one mission pays the cooldown twice. Without it a player
    /// could hit the button every step and the cooldown would be a number nobody reached.
    /// </remarks>
    public long LastAbortStep { get; set; }

    /// <summary>
    /// Whether the roll breakdown is being kept for the debrief.
    /// </summary>
    /// <remarks>
    /// The brief's debug flag, set at mission start from a developer setting. It is on
    /// the state rather than being read from a global at debrief time so that a recorded
    /// run says whether it recorded rolls — otherwise the same mission would produce two
    /// different reports depending on when the file was examined.
    /// </remarks>
    public bool RecordRolls { get; set; }

    /// <summary>The roll log, populated only while <see cref="RecordRolls"/> is set.</summary>
    public List<RollRecord> Rolls { get; } = new();

    /// <summary>Every perception, for the debrief's contact timeline.</summary>
    public List<PerceptionRecord> Perceptions { get; } = new();

    // ---- consequences -------------------------------------------------------
    //
    // Rule 19: a lethal act is a tuning decision, not a code decision. These three
    // counters are where the table's heat_cost and evidence_level land, and nothing in
    // this assembly inspects them to decide whether killing was a good idea. The debrief
    // reads them, the strategic layer spends the heat at mission end, and a designer
    // changes the price by editing a CSV.

    /// <summary>Heat earned during this mission, from lethal and loud actions.</summary>
    public int HeatGained { get; set; }

    /// <summary>Evidence a later investigation could find, from what happened here.</summary>
    public int EvidenceLevel { get; set; }

    /// <summary>How many lethal acts the team committed.</summary>
    public int LethalActs { get; set; }

    // ---- noise ---------------------------------------------------------------

    /// <summary>
    /// Noises made this step and not yet propagated.
    /// </summary>
    /// <remarks>
    /// A staging buffer rather than a queue the moment of making fills directly.
    /// Movement and actions run before noise propagation in the canonical order, so a
    /// noise made by a step of movement has to land somewhere that the propagation
    /// phase will pick it up in a fixed order — and a list preserves that order, where
    /// a set would not.
    /// </remarks>
    public List<NoiseEvent> NoiseInFlight { get; } = new();

    /// <summary>
    /// Every noise the mission has made, for the mission log.
    /// </summary>
    /// <remarks>
    /// Never trimmed. The brief calls noise "the primary way a careless player loses", so
    /// the record of <em>how</em> they lost has to outlive the moment it happened; a
    /// bounded log would drop exactly the early mistakes that teach the rule.
    /// </remarks>
    public List<NoiseLogEntry> NoiseLog { get; } = new();

    // ---- orders --------------------------------------------------------------

    /// <summary>
    /// Orders handed in since the last step, applied at the top of the next one.
    /// </summary>
    /// <remarks>
    /// Input is sampled only at step boundaries (rule 14), so an order raised during a
    /// step waits here until the next one begins. Draining it in submission order is
    /// what makes a pair of orders issued in the same frame resolve the same way on
    /// every replay.
    /// </remarks>
    public List<TacticalOrder> PendingOrders { get; } = new();

    /// <summary>
    /// The orders this mission has accepted, in order.
    /// </summary>
    /// <remarks>
    /// The tactical half of the replay log. Rule 6 requires that seed plus command log
    /// reproduce the mission step for step, and this is the command log for a mission:
    /// everything that is not a consequence of the previous step arrives here.
    /// </remarks>
    public List<AcceptedOrder> OrderLog { get; } = new();

    // ---- mission log ---------------------------------------------------------

    /// <summary>What has happened, for the debrief and the in-mission log.</summary>
    public List<MissionLogEntry> Log { get; } = new();

    // ---- indices -------------------------------------------------------------

    private Dictionary<int, TacticalActor>? _actorsById;
    private Dictionary<AgentId, TacticalActor>? _agentsByAgentId;

    /// <summary>An actor by id, or null.</summary>
    public TacticalActor? Actor(TacticalActorId id)
    {
        EnsureIndex();
        return _actorsById!.TryGetValue(id.Value, out TacticalActor? actor) ? actor : null;
    }

    /// <summary>The squad member bound to a strategic agent, or null.</summary>
    public TacticalActor? AgentActor(AgentId agentId)
    {
        EnsureIndex();
        return _agentsByAgentId!.TryGetValue(agentId, out TacticalActor? actor) ? actor : null;
    }

    /// <summary>Every actor, in ascending id order.</summary>
    public IReadOnlyList<TacticalActor> SortedActors
    {
        get
        {
            EnsureIndex();
            var sorted = new List<TacticalActor>(Actors);
            sorted.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
            return sorted;
        }
    }

    /// <summary>The squad, in ascending id order.</summary>
    public IReadOnlyList<TacticalActor> Squad
    {
        get
        {
            var squad = new List<TacticalActor>();
            foreach (TacticalActor actor in SortedActors)
            {
                if (actor.IsAgent)
                    squad.Add(actor);
            }

            return squad;
        }
    }

    /// <summary>Everyone who is site security, in ascending id order.</summary>
    public IReadOnlyList<TacticalActor> Guards
    {
        get
        {
            var guards = new List<TacticalActor>();
            foreach (TacticalActor actor in SortedActors)
            {
                if (actor.IsGuard)
                    guards.Add(actor);
            }

            return guards;
        }
    }

    /// <summary>True once the mission has ended, for whatever reason.</summary>
    public bool IsOver => Outcome != MissionOutcome.InProgress;

    /// <summary>
    /// Finds the room an actor stands in, or null when they are off the map.
    /// </summary>
    /// <remarks>
    /// Routed through the layout so that the "is this actor somewhere legal" question has
    /// exactly one answer, and so the fuzz sweep can assert it about every actor on
    /// every step rather than each caller writing its own bounds check.
    /// </remarks>
    public SiteRoom? RoomOf(TacticalActor actor)
        => actor is null ? null : Layout.RoomContaining(actor.Position);

    /// <summary>
    /// Whether this member is still inside the building the team went into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One predicate for two questions that have to agree: whether the mission can end,
    /// and what happens to this person when it does. They disagreed, and a run where the
    /// squad walked out clean was scored a <c>Disaster</c> with somebody lost, because
    /// the ending counted the radio operator as still inside and the resolution then
    /// duly captured them.
    /// </para>
    /// <para>
    /// <b>Two ways to be outside.</b> Standing in the forward post, which is joined to
    /// the building by one stairwell. And being a member whose role does not permit a
    /// <c>MoveTo</c> at all — <c>agent_role</c> gives the handler and the overwatch
    /// member no move order, because neither is an infiltrator, and a mission that
    /// requires somebody to obey an order they cannot be given is a mission that never
    /// ends. On the four templates with no forward post a handler is deployed into the
    /// building anyway, and would otherwise wait at a ground-floor exit for ever.
    /// </para>
    /// <para>
    /// The cost is worth naming rather than hiding: an overwatch member told to hold the
    /// objective no longer keeps the mission open on their own. That is the right way
    /// round — the ending is "the team got out", and a marksman the player never told to
    /// leave is not holding anybody up.
    /// </para>
    /// </remarks>
    public bool IsStillInside(TacticalActor actor)
    {
        SiteRoom? here = RoomOf(actor);

        if (here is null)
            return true;

        if (Layout.IsInPost(here.Id))
            return false;

        return !(Composition?.MemberFor(actor.AgentId)?.Role is { } role)
               || role.Allows(Core.Squad.SquadOrderKind.MoveTo);
    }

    /// <summary>The state of a connection, defaulting to what the layout generated.</summary>
    public ConnectionState StateOf(SiteConnectionId connectionId)
    {
        if (Doors.TryGetValue(connectionId, out ConnectionState state))
            return state;

        return Layout.FindConnectionFor(connectionId)?.IsLocked == true
            ? ConnectionState.Locked
            : ConnectionState.Open;
    }

    /// <summary>
    /// Checks the invariants every actor must satisfy, and names the first breach.
    /// </summary>
    /// <remarks>
    /// Called by the runner at the end of every step in the sweep test. "No entity
    /// off-map" is the cheapest way to catch a movement bug that would otherwise be
    /// found by a player standing in a wall, and naming the actor and its position makes
    /// the failure a bug report rather than a mystery.
    /// </remarks>
    public bool ValidateActors(out string problem)
    {
        problem = string.Empty;

        foreach (TacticalActor actor in SortedActors)
        {
            if (!actor.Position.IsValid)
            {
                problem = $"Actor{actor.Id} has an invalid position {actor.Position}";
                return false;
            }

            if (RoomOf(actor) is null)
            {
                problem = $"Actor{actor.Id} at {actor.Position} is not in any room";
                return false;
            }

            if (actor.Stamina < 0)
            {
                problem = $"Actor{actor.Id} has negative stamina {actor.Stamina}";
                return false;
            }

            if (actor.Health > actor.MaxHealth)
            {
                problem = $"Actor{actor.Id} health {actor.Health} exceeds max {actor.MaxHealth}";
                return false;
            }

            if (actor.Health < 0)
            {
                problem = $"Actor{actor.Id} has negative health {actor.Health}";
                return false;
            }

            if (actor.Condition == ActorCondition.Downed && actor.BleedOutStepsRemaining <= 0)
            {
                problem = $"Actor{actor.Id} is down with no bleed-out timer";
                return false;
            }

            if (actor.Condition != ActorCondition.Downed && actor.BleedOutStepsRemaining != 0)
            {
                problem = $"Actor{actor.Id} is {actor.Condition} but still bleeding out";
                return false;
            }
        }

        if (Alarm.Level is < 0 or > AlarmState.Max)
        {
            problem = $"Alarm {Alarm.Level} is outside 0-{AlarmState.Max}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Adds an actor and keeps the id index honest.
    /// </summary>
    /// <remarks>
    /// The only way an actor joins a mission. Going through here means the index can
    /// never be missing an actor that <see cref="Actors"/> holds, which is the failure
    /// that makes a lookup silently return null in the middle of a combat resolution.
    /// </remarks>
    public void Add(TacticalActor actor)
    {
        if (actor is null) throw new ArgumentNullException(nameof(actor));
        if (!actor.Id.IsValid)
            throw new ArgumentException("An actor needs a valid id.", nameof(actor));

        EnsureIndex();

        if (_actorsById!.ContainsKey(actor.Id.Value))
            throw new ArgumentException($"Actor {actor.Id} is already in this mission.", nameof(actor));

        Actors.Add(actor);
        _actorsById[actor.Id.Value] = actor;

        if (actor.IsAgent)
            _agentsByAgentId![actor.AgentId] = actor;

        _actorsById = null; // force a re-sort on next read
    }

    /// <summary>Appends to the mission log.</summary>
    public void Record(string key, params int[] args)
        => Log.Add(new MissionLogEntry(Step, key, args ?? Array.Empty<int>()));

    /// <inheritdoc/>
    public override string ToString()
        => $"mission {MissionId} site {SiteId} step {Step} actors {Actors.Count} alarm {Alarm}";

    private void EnsureIndex()
    {
        if (_actorsById is not null)
            return;

        var byId = new Dictionary<int, TacticalActor>();
        var byAgent = new Dictionary<AgentId, TacticalActor>();

        foreach (TacticalActor actor in Actors)
        {
            byId[actor.Id.Value] = actor;

            if (actor.IsAgent)
                byAgent[actor.AgentId] = actor;
        }

        _actorsById = byId;
        _agentsByAgentId = byAgent;
    }
}

/// <summary>One line of the in-mission log.</summary>
/// <remarks>
/// A localization key and integer arguments, never a sentence (rule 4). The step travels
/// with it so the log can be shown as a timeline without the UI having to correlate
/// entries against anything.
/// </remarks>
/// <param name="Step">When it happened.</param>
/// <param name="Key">Localization key, e.g. <c>log.action.completed</c>.</param>
/// <param name="Args">Values the UI substitutes into its template.</param>
public readonly record struct MissionLogEntry(long Step, string Key, IReadOnlyList<int> Args);
