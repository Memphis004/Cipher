namespace ProjectSpy.Core;

/// <summary>
/// The complete, serializable state of one run.
/// </summary>
/// <remarks>
/// <para>
/// This is the root that stage 5 serializes. Everything the simulation reads or
/// writes lives here, so a save is a straight walk of this object and a replay is
/// "seed + command log" (see <see cref="GameSession"/>).
/// </para>
/// <para>
/// It contains no Unity types and no behavior — the systems that act on it live in
/// the stage-3 pipeline. That split is what lets the same state be constructed,
/// hashed and asserted on from a plain xUnit test.
/// </para>
/// </remarks>
public sealed class WorldState
{
    /// <summary>Creates a fresh world from a root seed.</summary>
    public WorldState(ulong seed, int baseWidth = 24, int baseHeight = 12)
    {
        Seed = seed;
        Clock = new GameClock(Tick.Zero);
        RngStreams = new RngStreams(seed);
        Resources = Resources.Starting;
        BaseLayout = new BaseLayout(baseWidth, baseHeight);
        Economy = new EconomyState();
        CounterIntel = new CounterIntelState();
        NextAgentId = 1;
        NextMissionId = 1;
        NextContractId = 1;
    }

    /// <summary>
    /// The root seed. Every random stream is derived from it, so two runs with the
    /// same seed start identically.
    /// </summary>
    public ulong Seed { get; init; }

    /// <summary>Current time.</summary>
    public GameClock Clock { get; init; }

    /// <summary>Current balances.</summary>
    public Resources Resources { get; set; } = Resources.Empty;

    /// <summary>The base grid.</summary>
    public BaseLayout BaseLayout { get; init; }

    /// <summary>Named independent random streams.</summary>
    public RngStreams RngStreams { get; init; }

    /// <summary>
    /// Where commands and systems publish their events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GameSession"/> sets this when it takes ownership of a world. It
    /// exists because <see cref="ICommand.Apply"/> predates the stage-3 systems and takes
    /// only a world and an RNG — changing that signature now would mean editing every
    /// command and every test that calls it directly, for the sake of one extra parameter.
    /// </para>
    /// <para>
    /// Events still go through the session's pending buffer, so a command that publishes
    /// here is buffered and flushed at exactly the same point as any other event: a
    /// handler still cannot re-enter mid-command and observe a half-mutated world.
    /// </para>
    /// <para>
    /// Null in tests that drive the systems directly, which publish their own events.
    /// </para>
    /// </remarks>
    public IEventSink? Events { get; set; }

    /// <summary>Next id to hand out to a new agent.</summary>
    public int NextAgentId { get; set; } = 1;

    /// <summary>Next id to hand out to a mission.</summary>
    public int NextMissionId { get; set; } = 1;

    /// <summary>Next id to hand out to a contract offer.</summary>
    public int NextContractId { get; set; } = 1;

    /// <summary>Loans, deficit countdown and the bankruptcy escalation stage.</summary>
    public EconomyState Economy { get; init; } = new();

    /// <summary>Running investigations and the mole's leak history.</summary>
    public CounterIntelState CounterIntel { get; init; } = new();

    /// <summary>Ticks until the recruiting pool refreshes.</summary>
    public int RecruitPoolRefreshTicks { get; set; }

    /// <summary>Tick the current recruit pool was generated on.</summary>
    public Tick RecruitPoolGeneratedOnTick { get; set; }

    /// <summary>
    /// Week of the last weekly settlement, so settlement runs exactly once per week
    /// even if a caller advances ticks in unusual batches.
    /// </summary>
    public int LastSettledWeek { get; set; } = -1;

    /// <summary>Hired agents, by id.</summary>
    public Dictionary<AgentId, Agent> Agents { get; } = new();

    /// <summary>Unhired candidates in the current recruiting pool.</summary>
    public Dictionary<AgentId, Agent> Recruits { get; } = new();

    /// <summary>Mission ids currently deployed or in progress.</summary>
    public Dictionary<int, MissionState> ActiveMissions { get; } = new();

    /// <summary>Contract ids currently offered.</summary>
    public Dictionary<int, ContractState> Contracts { get; } = new();

    /// <summary>Free-form boolean-ish story flags keyed by name.</summary>
    public Dictionary<string, bool> Flags { get; } = new();

    /// <summary>Numeric counters for story progression and statistics.</summary>
    public Dictionary<string, int> Counters { get; } = new();

    // ---- Lookups -------------------------------------------------------------

    /// <summary>Looks up an agent by id.</summary>
    public Agent? GetAgent(AgentId id) => Agents.TryGetValue(id, out Agent? agent) ? agent : null;

    /// <summary>Looks up a recruit by id.</summary>
    public Agent? GetRecruit(AgentId id) => Recruits.TryGetValue(id, out Agent? agent) ? agent : null;

    /// <summary>Hired agents who are neither dead nor retired.</summary>
    public IEnumerable<Agent> ActiveRoster()
    {
        foreach (Agent agent in Agents.Values)
        {
            if (agent.Status.IsActive())
                yield return agent;
        }
    }

    /// <summary>Hired agents who can be deployed right now.</summary>
    public IEnumerable<Agent> DeployableRoster()
    {
        foreach (Agent agent in ActiveRoster())
        {
            if (agent.IsDeployable)
                yield return agent;
        }
    }

    /// <summary>Total weekly salary for the active roster.</summary>
    public long WeeklySalaryCost() => Resources.ProjectedWeeklyCost(ActiveRoster());

    // ---- Mutation helpers ----------------------------------------------------

    /// <summary>
    /// Registers a new agent and assigns it the next id. Returns the agent with its
    /// id already set.
    /// </summary>
    public Agent AddAgent(Agent agent)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        if (!agent.Id.IsValid)
            agent.Id = new AgentId(NextAgentId++);
        else if (agent.Id.Value >= NextAgentId)
            NextAgentId = agent.Id.Value + 1;

        agent.Normalize();
        Agents[agent.Id] = agent;
        return agent;
    }

    /// <summary>Adds a candidate to the recruiting pool.</summary>
    public Agent AddRecruit(Agent recruit)
    {
        if (recruit is null) throw new ArgumentNullException(nameof(recruit));

        if (!recruit.Id.IsValid)
            recruit.Id = new AgentId(-(NextAgentId++)); // negative ids keep recruits out of the roster
        else if (-recruit.Id.Value >= NextAgentId)
            NextAgentId = -recruit.Id.Value + 1;

        recruit.Normalize();
        Recruits[recruit.Id] = recruit;
        return recruit;
    }

    /// <summary>Moves a recruit onto the roster, returning false if not in the pool.</summary>
    public bool HireRecruit(AgentId id)
    {
        if (!Recruits.TryGetValue(id, out Agent? recruit))
            return false;

        Recruits.Remove(id);

        // Renumber to a positive roster id so roster and pool id spaces never collide.
        var hired = new Agent
        {
            Id = new AgentId(NextAgentId++),
            Name = recruit.Name,
            Codename = recruit.Codename,
            ClassId = recruit.ClassId,
            Level = recruit.Level,
            Exp = recruit.Exp,
            Skills = recruit.Skills,
            PhysicalStamina = recruit.PhysicalStamina,
            MentalStamina = recruit.MentalStamina,
            Loyalty = recruit.Loyalty,
            Status = AgentStatus.Idle,
            SalaryPerWeek = recruit.SalaryPerWeek,
            HiredOnTick = Clock.Current,
            MissionsCompleted = recruit.MissionsCompleted,
        };

        foreach (int traitId in recruit.TraitIds) hired.TraitIds.Add(traitId);
        foreach (int traitId in recruit.UndiscoveredTraitIds) hired.UndiscoveredTraitIds.Add(traitId);

        Agents[hired.Id] = hired;
        return true;
    }

    /// <summary>Removes an agent from the roster entirely.</summary>
    public bool RemoveAgent(AgentId id) => Agents.Remove(id);

    /// <summary>Sets or reads a story flag.</summary>
    public bool GetFlag(string name) => Flags.TryGetValue(name, out bool value) && value;

    /// <summary>Sets a story flag.</summary>
    public void SetFlag(string name, bool value = true) => Flags[name] = value;

    /// <summary>Adds to a named counter, defaulting to zero.</summary>
    public void AddCounter(string name, int amount = 1)
        => Counters[name] = GetCounter(name) + amount;

    /// <summary>Reads a named counter, defaulting to zero.</summary>
    public int GetCounter(string name) => Counters.TryGetValue(name, out int value) ? value : 0;

    // ---- Integrity -----------------------------------------------------------

    /// <summary>
    /// Checks the invariants that must hold after any mutation. Called by tests and
    /// by the stage-3 simulation loop rather than trusting callers to remember.
    /// </summary>
    public bool Validate(out string problem)
    {
        if (!Resources.IsValid)
        {
            problem = "ResourceNegative";
            return false;
        }

        if (Clock.Current.Value < 0)
        {
            problem = "NegativeTick";
            return false;
        }

        if (NextAgentId < 1)
        {
            problem = "AgentIdCounterExhausted";
            return false;
        }

        foreach (Agent agent in Agents.Values)
        {
            if (!agent.Id.IsValid)
            {
                problem = "InvalidAgentId";
                return false;
            }

            if (agent.PhysicalStamina is < 0 or > Agent.MaxStamina ||
                agent.MentalStamina is < 0 or > Agent.MaxStamina)
            {
                problem = "StaminaOutOfRange";
                return false;
            }

            if (agent.Loyalty is < 0 or > 100)
            {
                problem = "LoyaltyOutOfRange";
                return false;
            }

            if (agent.InjurySeverity is < 0 or > Agent.MaxInjurySeverity)
            {
                problem = "InjurySeverityOutOfRange";
                return false;
            }

            if (agent.AssignedRoomId != 0 && BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId)) is null)
            {
                problem = "AgentAssignedToMissingRoom";
                return false;
            }

            // A burnt-out agent must not be holding down a workstation: the whole point
            // of burnout is that they refuse, so the invariant is enforced on state
            // rather than trusted to every assignment path.
            if (agent.IsBurntOut && agent.Status == AgentStatus.Training)
            {
                problem = "BurntOutAgentStillTraining";
                return false;
            }
        }

        if (Economy.DaysInDeficit < 0)
        {
            problem = "NegativeDeficitDays";
            return false;
        }

        foreach (LoanState loan in Economy.Loans)
        {
            if (loan.Outstanding < 0 || loan.Principal < 0)
            {
                problem = "NegativeLoanBalance";
                return false;
            }
        }

        foreach (InvestigationState investigation in CounterIntel.Investigations)
        {
            if (investigation.Evidence is < 0 or > 100)
            {
                problem = "EvidenceOutOfRange";
                return false;
            }

            if (investigation.ProgressTicks < 0)
            {
                problem = "NegativeInvestigationProgress";
                return false;
            }
        }

        foreach (Room room in BaseLayout.Rooms)
        {
            if (room.Condition is < Room.MinCondition or > Room.MaxCondition)
            {
                problem = "RoomConditionOutOfRange";
                return false;
            }

            if (room.Width < 1)
            {
                problem = "RoomWidthInvalid";
                return false;
            }

            foreach (AgentId agentId in room.AssignedAgentIds)
            {
                if (Agents.TryGetValue(agentId, out Agent? agent) && agent.AssignedRoomId != room.Id.Value)
                {
                    problem = "RoomAssignmentMismatch";
                    return false;
                }
            }
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// A stable fingerprint of the simulation-visible state, used by the stage-5
    /// replay verifier and by determinism tests.
    /// </summary>
    /// <remarks>
    /// Deliberately integer-only and order-independent (roster is sorted by id), so
    /// two runs that agree produce the same hash regardless of dictionary layout.
    /// Presentation-only fields are excluded on purpose.
    /// </remarks>
    public ulong ComputeStateHash()
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL; // FNV-1a offset basis
            hash = Mix(hash, Seed);
            hash = Mix(hash, (ulong)Clock.Current.Value);

            Resources r = Resources;
            hash = Mix(hash, (ulong)r.Funds);
            hash = Mix(hash, (uint)r.Intel);
            hash = Mix(hash, (uint)r.Materials);
            hash = Mix(hash, (uint)r.Reputation);
            hash = Mix(hash, (uint)r.Heat);

            hash = Mix(hash, (ulong)BaseLayout.RoomCount);

            foreach (Agent agent in Agents.Values.OrderBy(a => a.Id.Value))
            {
                hash = Mix(hash, (ulong)agent.Id.Value);
                hash = Mix(hash, (ulong)agent.ClassId);
                hash = Mix(hash, (uint)agent.Level);
                hash = Mix(hash, (uint)agent.Exp);
                hash = Mix(hash, (uint)agent.Skills.Infiltration);
                hash = Mix(hash, (uint)agent.Skills.Combat);
                hash = Mix(hash, (uint)agent.Skills.Tech);
                hash = Mix(hash, (uint)agent.Skills.Social);
                hash = Mix(hash, (uint)agent.Skills.Nerve);
                hash = Mix(hash, (uint)agent.PhysicalStamina);
                hash = Mix(hash, (uint)agent.MentalStamina);
                hash = Mix(hash, (uint)agent.Loyalty);
                hash = Mix(hash, (uint)agent.Status);
                hash = Mix(hash, (uint)agent.AssignedRoomId);
                hash = Mix(hash, (ulong)agent.SalaryPerWeek);
                hash = Mix(hash, (uint)agent.MissionsCompleted);
                hash = Mix(hash, (uint)agent.InjurySeverity);
                hash = Mix(hash, agent.IsBurntOut ? 1UL : 0UL);
                hash = Mix(hash, (uint)agent.BurnoutRecoveryTicks);
                hash = Mix(hash, (uint)agent.LoyaltyEscalationCooldown);
                hash = Mix(hash, (uint)agent.LastEscalation);

                // Hidden traits participate. They are simulation state, so a world
                // where a mole has been hired must not hash the same as one where they
                // were not — otherwise the determinism test would happily bless two
                // genuinely different worlds.
                foreach (int traitId in agent.TraitIds.OrderBy(t => t))
                    hash = Mix(hash, (uint)traitId);

                foreach (int traitId in agent.UndiscoveredTraitIds.OrderBy(t => t))
                    hash = Mix(hash, (uint)(traitId | 0x8000_0000));
            }

            foreach (Agent recruit in Recruits.Values.OrderBy(a => a.Id.Value))
            {
                hash = Mix(hash, (ulong)recruit.Id.Value);
                hash = Mix(hash, (ulong)recruit.ClassId);
                hash = Mix(hash, (uint)recruit.Skills.Highest());
                hash = Mix(hash, (ulong)recruit.SalaryPerWeek);
                hash = Mix(hash, (uint)recruit.TraitIds.Count);
                hash = Mix(hash, (uint)recruit.UndiscoveredTraitIds.Count);
            }

            // Economy and counter-intel are part of the world, so they must be part of
            // the fingerprint. Leaving them out would let two runs that differ only in
            // bankruptcy stage compare equal.
            hash = Mix(hash, (ulong)Economy.TotalOutstanding);
            hash = Mix(hash, (ulong)Economy.PendingInterest);
            hash = Mix(hash, (ulong)Economy.TotalInterestPaid);
            hash = Mix(hash, (ulong)Economy.TotalSalariesPaid);
            hash = Mix(hash, (ulong)Economy.TotalUpkeepPaid);
            hash = Mix(hash, (uint)Economy.DaysInDeficit);
            hash = Mix(hash, (uint)Economy.PenaltyStage);
            hash = Mix(hash, (uint)Economy.ActiveLoanCount);

            foreach (InvestigationState investigation in CounterIntel.Investigations
                         .OrderBy(i => i.SubjectId.Value))
            {
                hash = Mix(hash, (ulong)investigation.SubjectId.Value);
                hash = Mix(hash, (uint)investigation.Evidence);
                hash = Mix(hash, (uint)investigation.ProgressTicks);
                hash = Mix(hash, (uint)investigation.Status);
            }

            foreach (MoleLeak leak in CounterIntel.LeakLog)
            {
                hash = Mix(hash, (ulong)leak.Tick.Value);
                hash = Mix(hash, (ulong)leak.MissionId);
                hash = Mix(hash, (ulong)leak.AgentId.Value);
                hash = Mix(hash, (uint)leak.HeatAdded);
                hash = Mix(hash, (uint)leak.DifficultyBonus);
            }

            hash = Mix(hash, (uint)CounterIntel.ExposedCount);
            hash = Mix(hash, (uint)CounterIntel.WrongAccusations);

            hash = Mix(hash, (uint)Recruits.Count);
            hash = Mix(hash, (uint)RecruitPoolRefreshTicks);

            foreach (Room room in BaseLayout.Rooms.OrderBy(x => x.Id.Value))
            {
                hash = Mix(hash, (ulong)room.Id.Value);
                hash = Mix(hash, (uint)room.TypeId);
                hash = Mix(hash, (uint)room.GridX);
                hash = Mix(hash, (uint)room.GridY);
                hash = Mix(hash, (uint)room.Width);
                hash = Mix(hash, (uint)room.Level);
                hash = Mix(hash, (uint)room.Condition);
                hash = Mix(hash, (uint)room.ConstructionTicksRemaining);
            }

            // RNG state participates: two worlds that agree visually but will roll
            // differently are not the same world.
            RngStreamsState rng = RngStreams.SaveState();
            foreach (RngState state in rng.Streams)
            {
                hash = Mix(hash, state.S0);
                hash = Mix(hash, state.S1);
                hash = Mix(hash, state.S2);
                hash = Mix(hash, state.S3);
            }

            return hash;
        }
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= 1099511628211UL;
        }

        return hash;
    }
}

/// <summary>
/// An in-flight mission. Stage 4 owns the real field set; stage 1 keeps the
/// identity and lifecycle hooks the command layer needs to reference it.
/// </summary>
public sealed class MissionState
{
    public int Id { get; init; }

    /// <summary>Foreign key into mission_type (stage 2).</summary>
    public int TypeId { get; init; }

    /// <summary>Tick the mission was dispatched.</summary>
    public Tick StartTick { get; init; }

    /// <summary>Expected tick of completion.</summary>
    public Tick EstimatedEndTick { get; init; }

    public MissionOutcome Outcome { get; set; } = MissionOutcome.Unresolved;

    /// <summary>Agents dispatched, by id.</summary>
    public List<AgentId> AgentIds { get; } = new();

    /// <summary>Mission-specific seed, derived from the world seed plus the mission id.</summary>
    public ulong MapSeed { get; set; }

    /// <summary>Reads a set of mission-local values. Stage 4 fills this in.</summary>
    public Dictionary<string, int> Values { get; } = new();

    public int Get(string key) => Values.TryGetValue(key, out int value) ? value : 0;

    public void Set(string key, int value) => Values[key] = value;
}

/// <summary>How a mission ended.</summary>
public enum MissionOutcome
{
    Unresolved = 0,
    ObjectiveComplete = 1,
    PartialSuccess = 2,
    Aborted = 3,
    Failed = 4,

    /// <summary>Alarm hit 100: the team was rolled up and any agent not at extraction is captured.</summary>
    Burned = 5,
}

/// <summary>An offered or accepted contract.</summary>
public sealed class ContractState
{
    public int Id { get; init; }

    /// <summary>Foreign key into mission_type (stage 2).</summary>
    public int TypeId { get; init; }

    /// <summary>Foreign key into the client faction table (stage 2).</summary>
    public int ClientFactionId { get; init; }

    public long RewardFunds { get; init; }

    public int RewardIntel { get; init; }

    public int HeatGain { get; init; }

    /// <summary>Tick after which this offer expires.</summary>
    public Tick ExpiresOnTick { get; init; }

    /// <summary>True once the player has accepted it.</summary>
    public bool Accepted { get; set; }

    /// <summary>True once a mission was dispatched against it.</summary>
    public bool Dispatched { get; set; }
}
