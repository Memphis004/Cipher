using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Builds the initial state of a mission from a generated site and a squad.
/// </summary>
/// <remarks>
/// <para>
/// One factory rather than a constructor on <see cref="TacticalState"/>, because
/// populating a mission means resolving three different sources against each other —
/// the generated guards and civilians, the strategic agents, and the building itself —
/// and a constructor that took all three would be a four-argument method whose body is
/// a hundred lines.
/// </para>
/// <para>
/// <b>Everything derived is derived from the layout.</b> A guard's vision comes from its
/// archetype, its suspicion rates from the same row, and its start position from the
/// room its route begins in. Nothing is invented here, which is what makes the fuzz
/// sweep meaningful: if an actor is off the map it is because a layout said so, not
/// because a factory defaulted a coordinate to zero.
/// </para>
/// <para>
/// <b>Ids are handed out in one ascending sequence</b> — squad, then guards, then
/// civilians — so that "actor 7" is the same actor in the simulator, in a replay and in
/// a player's debrief.
/// </para>
/// </remarks>
public static class TacticalMission
{
    /// <summary>
    /// The squad a mission deploys, from the brief: three to five agents.
    /// </summary>
    /// <remarks>
    /// Structural rather than tuned: it is a design constraint on what a mission is,
    /// not a balance number, and there is no column for it.
    /// </remarks>
    public const int MinSquadSize = 3;

    /// <summary>Largest squad a mission deploys.</summary>
    public const int MaxSquadSize = 5;

    /// <summary>
    /// Steps the mission's objective takes when no interactable drives it.
    /// </summary>
    /// <remarks>
    /// Roughly ninety seconds of simulated time — long enough to be interrupted, short
    /// enough that a mission is not mostly waiting. The default only; an objective with
    /// its own work item is costed by that item's row instead.
    /// </remarks>
    public const int DefaultObjectiveSteps = 900;

    /// <summary>Builds a mission.</summary>
    /// <param name="layout">The generated building.</param>
    /// <param name="squad">The agents deploying, in roster order.</param>
    /// <param name="missionId">The mission being played.</param>
    /// <param name="startedOnTick">The strategic tick it was dispatched on.</param>
    public static TacticalState Create(
        SiteLayout layout,
        IReadOnlyList<Agent> squad,
        int missionId,
        Tick startedOnTick)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));
        if (squad is null) throw new ArgumentNullException(nameof(squad));

        if (squad.Count is < MinSquadSize or > MaxSquadSize)
        {
            throw new ArgumentException(
                $"A mission deploys {MinSquadSize}-{MaxSquadSize} agents; got {squad.Count}.",
                nameof(squad));
        }

        var state = new TacticalState
        {
            MissionId = missionId,
            SiteId = layout.SiteTemplateId,
            StartedOnTick = startedOnTick,
            Layout = layout,
            Lights = new LightState(layout),
        };

        // Doors start in the state the layout committed to: open, unless the generator
        // made it locked. A door with no entry is open, which is the same answer the
        // pathfinder would give it and keeps the two from disagreeing on a fresh mission.
        foreach (SiteConnection connection in layout.Connections)
        {
            state.Doors[connection.Id] = connection.IsLocked
                ? ConnectionState.Locked
                : ConnectionState.Open;
        }

        int nextId = 1;
        var placement = new StartPlacement();
        SiteRoom? entrance = layout.Find(layout.EntranceRoomId);

        if (entrance is null)
        {
            throw new InvalidOperationException(
                $"Site {layout.SiteTemplateId} has no entrance room {layout.EntranceRoomId}, so a squad " +
                "cannot be deployed into it. That is a generator fault, not a recoverable condition.");
        }

        foreach (Agent agent in squad)
        {
            state.Add(new TacticalActor
            {
                Id = new TacticalActorId(nextId++),
                Kind = TacticalActorKind.Agent,
                AgentId = agent.Id,
                NameKey = "agent." + agent.Codename,
                Position = placement.Take(entrance),
                Facing = Facing.Right,
                Posture = Posture.Crouch,
                Condition = ActorCondition.Active,
                Health = AgentMaxHealth,
                MaxHealth = AgentMaxHealth,
                Stamina = MaxStamina,
                SkillSnapshot = agent.Skills,
                Vision = VisionFor(agent),
            });
        }

        foreach (SiteGuard guard in layout.Guards)
        {
            state.Add(new TacticalActor
            {
                Id = new TacticalActorId(nextId++),
                Kind = TacticalActorKind.Guard,
                GuardId = guard.Id,
                GuardArchetypeId = guard.ArchetypeId,
                NameKey = guard.NameKey,
                Position = GuardStartPosition(layout, guard, placement),
                Facing = Facing.Right,
                Posture = Posture.Walk,
                Condition = ActorCondition.Active,
                Health = GuardMaxHealth(guard.ArchetypeId),
                MaxHealth = GuardMaxHealth(guard.ArchetypeId),
                Stamina = MaxStamina,
                RouteIndex = 0,
                Vision = GuardVision(guard.ArchetypeId),
            });
        }

        foreach (SiteCivilian civilian in layout.Civilians)
        {
            SiteRoom? room = layout.Find(civilian.RoomId);
            if (room is null)
                continue;

            state.Add(new TacticalActor
            {
                Id = new TacticalActorId(nextId++),
                Kind = TacticalActorKind.Civilian,
                CivilianId = civilian.Id,
                NameKey = "civilian." + civilian.Id,
                Position = placement.Take(room),
                Facing = Facing.Left,
                Posture = Posture.Walk,
                Condition = ActorCondition.Active,
                Health = CivilianMaxHealth,
                MaxHealth = CivilianMaxHealth,
                Stamina = MaxStamina,
            });
        }

        state.Objective = new ObjectiveProgress
        {
            NameKey = "objective.default",
            TotalSteps = DefaultObjectiveSteps,
        };

        return state;
    }

    /// <summary>
    /// Hands out starting positions, so that two actors deployed into one room do not
    /// begin the mission standing on the same centimetre of floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The squad deploys into the entrance room, and a guard whose patrol route begins
    /// there is standing in the entrance room too. Both were placed at "the middle of
    /// the room", so a routine generated site began every mission with two guards' faces
    /// 30cm from the whole squad — which the perception system correctly reported as a
    /// confirmed identification, which correctly drove two guards to full suspicion,
    /// which correctly burned the site inside half a minute. Every link in that chain was
    /// working; the only thing wrong was where people were put.
    /// </para>
    /// <para>
    /// Slots are taken from the middle outwards in a fixed order, so deployment is a
    /// function of the layout and the actor order alone — two actors in one room never
    /// share a position, and re-running the same mission produces the same one.
    /// </para>
    /// </remarks>
    private sealed class StartPlacement
    {
        /// <summary>How far apart two actors in one room stand, in centimetres.</summary>
        private const int SlotSpacingCm = 60;

        private readonly Dictionary<SiteRoomId, int> _taken = new();

        /// <summary>The next free starting position in <paramref name="room"/>.</summary>
        public TacticalPosition Take(SiteRoom room)
        {
            _taken.TryGetValue(room.Id, out int used);
            _taken[room.Id] = used + 1;

            return new TacticalPosition(room.FloorIndex, SlotX(room, used));
        }

        /// <summary>
        /// The nth slot in a room: the middle, then alternating outwards.
        /// </summary>
        /// <remarks>
        /// Clamped inside the room, because a generator is free to produce a room narrower
        /// than the spacing and an actor outside its own room is off the map. Two actors
        /// in a very narrow room therefore do share a position — which is survivable,
        /// where an actor outside every room is not.
        /// </remarks>
        private static Fixed32 SlotX(SiteRoom room, int slot)
        {
            int centre = roomCentreX(room).Raw;
            int offset = ((slot + 1) / 2) * SlotSpacingCm * (slot % 2 == 0 ? -1 : 1);

            int lowest = room.StartX.Raw;
            int highest = room.LastX.Raw;

            int x = centre + offset;

            if (x < lowest) x = lowest;
            if (x > highest) x = highest;

            return new Fixed32(x);
        }
    }

    /// <summary>
    /// Where a guard starts: the room its patrol route begins in.
    /// </summary>
    /// <remarks>
    /// The route's first room rather than its home room, which the generator guarantees
    /// are the same. Using the route directly means a guard is standing where its route
    /// says it should be, and a player who has read the route from an intel report can
    /// find it there — which is the whole point of a report being worth having.
    /// </remarks>
    private static TacticalPosition GuardStartPosition(SiteLayout layout, SiteGuard guard, StartPlacement placement)
    {
        SiteRoomId roomId = guard.PatrolRoute.Count > 0 ? guard.PatrolRoute[0] : guard.HomeRoomId;
        SiteRoom? room = layout.Find(roomId);

        if (room is null)
            room = layout.Find(guard.HomeRoomId);

        // A guard whose home room somehow does not exist is placed at the entrance rather
        // than at a default coordinate: an actor off the map fails the mission's own
        // invariant, and a guard the generator lost is better caught by the validator
        // than by a mission that will not run.
        if (room is null)
        {
            SiteRoom? entrance = layout.Find(layout.EntranceRoomId);
            return entrance is null ? TacticalPosition.None : placement.Take(entrance);
        }

        return placement.Take(room);
    }

    /// <summary>The centre of a room, in centimetres.</summary>
    private static Fixed32 roomCentreX(SiteRoom room) => (room.StartX + room.EndX) / 2;

    /// <summary>
    /// A guard's sight and hearing, from its <c>guard_archetype</c> row.
    /// </summary>
    /// <remarks>
    /// Read once at deployment and copied onto the actor. Every perception check on
    /// every pair of actors on every step reads this, and it never changes during a
    /// mission — a guard does not get better-sighted when the alarm rises, they get
    /// faster — so there is nothing to look up later.
    /// </remarks>
    private static VisionStats GuardVision(int archetypeId)
    {
        ProjectSpy.Tables.GuardArchetype? archetype = SimulationRules.GuardArchetypeFor(archetypeId);

        if (archetype is null)
            return VisionStats.Default;

        return new VisionStats
        {
            VisionRangeCm = archetype.VisionRangeCm,
            VisionConeDegrees = archetype.VisionConeDegrees,
            HearingRangeCm = archetype.HearingRangeCm,
            RangeBonusPercent = 0,
        };
    }

    /// <summary>
    /// A squad member's sight, derived from Infiltration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rule 16 makes Infiltration govern perception range, so an operative who has
    /// trained up can see further and does so from the first step of the mission rather
    /// than only after some unlock. The base range and the per-point bonus are
    /// structural: they say what the skill means, not how strong it is.
    /// </para>
    /// <para>
    /// Public because the HUD has to be able to show it. A player choosing who to send
    /// somewhere needs to read "this one can see 1320cm", and letting the screen
    /// recompute it from the agent's skill would put a second copy of the formula in the
    /// codebase — the exact thing the rule-4 data contract exists to prevent.
    /// </para>
    /// </remarks>
    public static VisionStats VisionFor(Agent agent)
    {
        int skill = Math.Max(0, agent.Skills.Infiltration);

        return new VisionStats
        {
            VisionRangeCm = AgentBaseVisionCm + skill * AgentCmPerInfiltrationPoint,
            VisionConeDegrees = AgentConeDegrees,
            HearingRangeCm = AgentHearingCm + skill * AgentCmPerInfiltrationPoint / 2,
            RangeBonusPercent = 0,
        };
    }

    /// <summary>Sight range an operative has at zero Infiltration.</summary>
    private const int AgentBaseVisionCm = 600;

    /// <summary>Extra centimetres of sight per point of Infiltration.</summary>
    private const int AgentCmPerInfiltrationPoint = 8;

    /// <summary>An operative's cone. Wide, because a team member is looking, not guarding.</summary>
    private const int AgentConeDegrees = 180;

    /// <summary>Base hearing range for an operative.</summary>
    private const int AgentHearingCm = 500;

    /// <summary>An operative's hit points, from their class.</summary>
    private const int AgentMaxHealth = 100;

    /// <summary>A member of the public's hit points.</summary>
    private const int CivilianMaxHealth = 40;

    /// <summary>Top of the stamina scale.</summary>
    private const int MaxStamina = 100;

    /// <summary>
    /// A guard's hit points, from <c>guard_archetype.health</c>.
    /// </summary>
    /// <remarks>
    /// Falls back to a sentry's value rather than to zero: an archetype row that has
    /// gone missing should produce an ordinary guard, not an actor that dies to the
    /// first tap and quietly removes a patrol.
    /// </remarks>
    private static int GuardMaxHealth(int archetypeId)
        => SimulationRules.GuardArchetypeFor(archetypeId)?.Health ?? 70;
}

/// <summary>
/// Runs a tactical mission, one 100 ms step at a time.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="TickPipeline"/>, and the second of the two processing
/// orders rule 6 protects. <see cref="Step"/> executes the ten phases in the canonical
/// order and advances the mission by exactly one step; it is the only way a tactical
/// simulation moves.
/// </para>
/// <para>
/// <b>One step, one order.</b> Everything that can happen to the world happens here, in
/// the order the contract fixes. A caller that wants to know what a step did reads
/// <see cref="LastReports"/> rather than reaching into the state and inferring it, which
/// is what makes the pipeline observable and therefore testable.
/// </para>
/// </remarks>
public sealed class TacticalMissionRunner
{
    private readonly TacticalStepPipeline _pipeline;
    private readonly IRng _tactical;
    private readonly IRng _planner;

    /// <summary>Creates a runner for a mission, drawing from the world's named streams.</summary>
    /// <remarks>
    /// Two streams, not one. Perception rolls and skill checks come from
    /// <see cref="RngStreams.StreamKind.Tactical"/>; NPC planning comes from
    /// <see cref="RngStreams.StreamKind.Goap"/>. The separation is the one
    /// <see cref="RngStreams"/> was built for — a planner that searched one node more
    /// would otherwise shift every subsequent perception roll, and a save would stop
    /// reproducing the moment the planner's node budget was tuned.
    /// </remarks>
    public TacticalMissionRunner(TacticalState state, RngStreams streams)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (streams is null) throw new ArgumentNullException(nameof(streams));

        State = state;
        _tactical = streams[RngStreams.StreamKind.Tactical];
        _planner = streams[RngStreams.StreamKind.Goap];
        _pipeline = TacticalPhases.CreateDefault();
    }

    /// <summary>The mission being run.</summary>
    public TacticalState State { get; }

    /// <summary>What the last step's phases each did, in canonical order.</summary>
    public IReadOnlyList<TacticalPhaseReport> LastReports { get; private set; } =
        Array.Empty<TacticalPhaseReport>();

    /// <summary>Total steps this runner has executed.</summary>
    public long StepsRun { get; private set; }

    /// <summary>
    /// Executes one 100 ms step and returns what each phase did.
    /// </summary>
    /// <remarks>
    /// The phase order inside here is the contract. It is not written as a call sequence
    /// but delegated to <see cref="TacticalStepPipeline"/>, so that the order a test
    /// asserts and the order a run performs are read from the same declaration and
    /// cannot drift apart.
    /// </remarks>
    public IReadOnlyList<TacticalPhaseReport> Step()
    {
        if (State.IsOver)
            return LastReports;

        LastReports = _pipeline.RunStep(new StepContext(State, _tactical, _planner));
        StepsRun++;
        State.Step = StepsRun;

        return LastReports;
    }

    /// <summary>Runs <paramref name="steps"/> steps, stopping early if the mission ends.</summary>
    public void StepMany(long steps)
    {
        for (long i = 0; i < steps && !State.IsOver; i++)
            Step();
    }

    /// <summary>
    /// Closes the mission: applies every actor's fate and converts elapsed steps to
    /// strategic ticks exactly once.
    /// </summary>
    /// <remarks>
    /// The conversion is guarded by <see cref="TacticalState.TimeConverted"/> — rule
    /// 13's "exactly once". A mission closed twice, by a quit-to-menu followed by a load
    /// or by a caller being careless, must not advance the base clock a second time and
    /// cost the player an hour they never saw go.
    /// </remarks>
    /// <returns>The strategic ticks the mission was worth, or zero if already converted.</returns>
    public long Close()
    {
        if (!State.IsOver)
        {
            DamageSystem.ResolveMissionEnd(State, State.Layout.ExtractionRoomIds);
            State.Outcome = State.Objective.IsComplete ? MissionOutcome.Exfiltrated : MissionOutcome.Aborted;
        }

        if (State.TimeConverted)
            return 0;

        State.TimeConverted = true;
        return MissionTimeConverter.StepsToStrategicTicks(State.Step);
    }
}
