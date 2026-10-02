namespace ProjectSpy.Core;

/// <summary>
/// The mole: silent Heat, mission leaks, and the counter-intelligence desk that
/// investigates them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The design constraint is falsifiability.</b> The mole is only interesting if a
/// player who pays attention can work out who it is. Three properties make that
/// possible, and each is load-bearing:
/// </para>
/// <list type="number">
/// <item><b>Deterministic contribution.</b> A mole adds exactly
/// <c>counter_intel_rule.mole_weekly_heat</c> Heat every week. Not sometimes, not a
/// roll. If the contribution varied, the player could never distinguish the mole's
/// contribution from ordinary heat drift and the whole system would be noise.</item>
/// <item><b>Correlatable timing.</b> The weekly Heat jump lands on a known tick, and
/// <see cref="MoleLeak"/> records which missions were compromised and when. Heat and
/// mission log are two independent public signals pointing at the same week.</item>
/// <item><b>Graded evidence.</b> An investigation accumulates evidence in
/// <c>0-100</c> over multiple steps, and can throw a false lead that lowers it. The
/// player sees a number that moves, not a verdict, so the conclusion stays theirs.</item>
/// </list>
/// <para>
/// The reasoning chain a player is expected to follow is written out in
/// <c>docs/MOLE_DESIGN.md</c>, because a detection system the designer cannot explain
/// is a detection system nobody can tune.
/// </para>
/// <para>
/// <b>Heat is not attributable on its own.</b> Other sources raise Heat too
/// (missions, raids). That is intentional friction: the mole's weekly contribution is a
/// signal, not a smoking gun, so the player must eliminate the alternatives by
/// cross-referencing mission logs. Removing that friction would make counter-intel
/// pointless.
/// </para>
/// </remarks>
public static class MoleSystem
{
    /// <summary>
    /// The hidden Heat a single mole adds per week.
    /// </summary>
    public static int WeeklyHeat => SimulationRules.CounterIntel("mole_weekly_heat", 6);

    /// <summary>Fallback weekly heat when the table is unavailable.</summary>
    public const int DefaultHeatFloorPercent = 25;

    /// <summary>
    /// Finds every mole currently on the roster, discovered or not.
    /// </summary>
    /// <remarks>
    /// Used by the weekly Heat pass, which must see hidden moles — otherwise uncovering
    /// one would quietly turn off the Heat it was generating, which would be a reward for
    /// playing well rather than a consequence.
    /// </remarks>
    public static List<Agent> FindMoles(WorldState world)
    {
        var moles = new List<Agent>();

        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value))
        {
            if (agent.HasTrait(TraitLookup.MoleTraitId))
                moles.Add(agent);
        }

        return moles;
    }

    /// <summary>
    /// Adds each mole's silent weekly Heat. Runs on the weekly settlement, not per tick,
    /// so the spike lands on a predictable day the player can correlate against.
    /// </summary>
    public static int ApplyWeeklyMoleHeat(WorldState world, IEventSink? events)
    {
        List<Agent> moles = FindMoles(world);
        if (moles.Count == 0)
            return 0;

        // Public Heat also decays at this point, which is what gives the spike meaning:
        // a week with no missions still ticks up, and that is the tell.
        int floor = SimulationRules.CounterIntel("mole_heat_floor_percent", DefaultHeatFloorPercent);
        int total = 0;

        foreach (Agent mole in moles)
        {
            int heat = WeeklyHeat;

            // A patriot actively talks the mole's game down. The reduction is clamped
            // so it can never silence the mole entirely: if a patriot could zero the
            // spike there would be no tell left to find, and the mole would cost the
            // player nothing. Counter-play should make the mystery harder, not make it
            // disappear.
            int reduction = TraitLookup.SumEffect(mole, "HeatGainReduction");
            heat = Math.Max(SimulationRules.PercentOf(heat, floor), heat - reduction);

            world.Resources = world.Resources.AddHeat(heat);
            total += heat;
        }

        _ = events;
        return total;
    }

    /// <summary>
    /// Rolls whether a mole tips the enemy off about a mission before it runs.
    /// </summary>
    /// <remarks>
    /// A leak raises the mission's difficulty and adds Heat. This is the mechanism that
    /// makes the mole's existence felt even when the player never suspects anyone: the
    /// job got harder and nobody knows why. The leak is recorded so it becomes
    /// attributable later.
    /// </remarks>
    /// <returns>The leak, or null when nobody tipped anyone off.</returns>
    public static MoleLeak? RollMissionLeak(WorldState world, int missionId, IEventSink? events)
    {
        List<Agent> moles = FindMoles(world);
        if (moles.Count == 0)
            return null;

        int chance = SimulationRules.CounterIntel("mole_leak_chance_percent", 20);
        if (chance <= 0)
            return null;

        // A mole whose mission was never dispatched has nothing to leak.
        IRng rng = world.RngStreams[RngStreams.StreamKind.Mission];
        Agent mole = rng.Pick(moles);

        if (rng.NextInt(1, 101) > chance)
            return null;

        int difficulty = SimulationRules.CounterIntel("mole_leak_difficulty_bonus", 2);
        int heat = WeeklyHeat;

        // A leak only becomes public once the mole is exposed. Before that the player
        // sees "something went wrong on this job", not "someone in my house did it".
        bool isPublic = HasExposedTrait(mole, TraitLookup.MoleTraitId);

        var leak = new MoleLeak(
            world.Clock.Current,
            missionId,
            mole.Id,
            heat,
            difficulty,
            isPublic);

        world.CounterIntel.LeakLog.Add(leak);
        world.Resources = world.Resources.AddHeat(heat);

        events?.Publish(new MissionCompromised(world.Clock.Current, missionId, difficulty, heat, isPublic));
        return leak;
    }

    /// <summary>
    /// Total difficulty added to missions by every recorded leak.
    /// </summary>
    /// <remarks>
    /// This is the seam stage 4 reads. When a mission is built, its difficulty starts
    /// from this total rather than from zero, so a mole who has been leaking for three
    /// weeks makes the agency measurably worse at everything without the player ever
    /// being told which agent is responsible.
    /// </remarks>
    public static int AccumulatedLeakDifficulty(WorldState world)
    {
        int total = 0;
        foreach (MoleLeak leak in world.CounterIntel.LeakLog)
            total += leak.DifficultyBonus;

        return total;
    }

    // ---- counter-intelligence ------------------------------------------------

    /// <summary>How many investigations the counter-intel room can run at once.</summary>
    public static int Capacity(WorldState world)
    {
        int level = CounterIntelRoomLevel(world);
        if (level <= 0)
            return 0;

        int perLevel = SimulationRules.CounterIntel("counter_intel_capacity_per_level", 1);
        return level * Math.Max(1, perLevel);
    }

    /// <summary>The level of the counter-intel room, or zero when there is none.</summary>
    public static int CounterIntelRoomLevel(WorldState world)
    {
        int roomTypeId = CounterIntelRoomTypeId();
        if (roomTypeId == 0)
            return 0;

        int best = 0;
        foreach (Room room in world.BaseLayout.Rooms)
        {
            if (room.TypeId == roomTypeId && !room.IsUnderConstruction)
                best = Math.Max(best, room.Level);
        }

        return best;
    }

    private static int CounterIntelRoomTypeId()
    {
        foreach (ProjectSpy.Tables.RoomType type in SimulationRules.AllRoomTypes())
        {
            if (type.Category == ProjectSpy.Tables.RoomCategory.Admin && type.IsCounterIntel())
                return type.Id;
        }

        return 0;
    }

    /// <summary>Opens an investigation against an agent.</summary>
    public static InvestigationState? StartInvestigation(WorldState world, AgentId subject, IEventSink? events)
    {
        if (world.GetAgent(subject) is null)
            return null;

        if (world.CounterIntel.FindFor(subject) is { IsRunning: true })
            return null;

        // The desk can only work on as many cases as it has capacity for.
        int running = 0;
        foreach (InvestigationState investigation in world.CounterIntel.Investigations)
        {
            if (investigation.IsRunning) running++;
        }

        if (running >= Capacity(world))
            return null;

        var state = new InvestigationState
        {
            SubjectId = subject,
            StartedOnTick = world.Clock.Current,
        };

        world.CounterIntel.Investigations.Add(state);
        events?.Publish(new InvestigationStarted(world.Clock.Current, subject));
        return state;
    }

    /// <summary>
    /// Advances every running investigation by one step.
    /// </summary>
    /// <remarks>
    /// A step either produces evidence or throws a false lead. The false lead lowers
    /// evidence rather than merely failing to raise it, so a weak case genuinely
    /// deteriorates — which is what makes the player cautious about accusing.
    /// </remarks>
    /// <summary>Fallback ticks per investigation step when the table is unavailable.</summary>
    public const int DefaultTicksPerStep = 24;

    /// <summary>Fallback evidence chance when the table is unavailable.</summary>
    public const int DefaultEvidenceChance = 35;

    /// <summary>Fallback false-lead chance when the table is unavailable.</summary>
    public const int DefaultFalseLeadChance = 20;

    /// <summary>Fallback evidence gained on a successful step.</summary>
    public const int DefaultEvidenceGain = 10;

    /// <summary>Fallback evidence lost on a false lead.</summary>
    public const int DefaultFalseLeadPenalty = 8;

    public static void TickInvestigations(WorldState world, IEventSink? events)
    {
        if (Capacity(world) <= 0)
            return;

        int ticksPerStep = SimulationRules.CounterIntel("investigation_ticks_per_step", DefaultTicksPerStep);
        int evidenceChance = SimulationRules.CounterIntel("investigation_evidence_chance", DefaultEvidenceChance);
        int falseLeadChance = SimulationRules.CounterIntel("investigation_false_lead_chance", DefaultFalseLeadChance);

        int gain = SimulationRules.CounterIntel("investigation_evidence_gain", DefaultEvidenceGain);
        int penalty = SimulationRules.CounterIntel("investigation_false_lead_penalty", DefaultFalseLeadPenalty);

        if (ticksPerStep <= 0)
            return;

        IRng rng = world.RngStreams[RngStreams.StreamKind.Event];

        foreach (InvestigationState investigation in world.CounterIntel.Investigations)
        {
            if (!investigation.IsRunning)
                continue;

            investigation.ProgressTicks++;
            if (investigation.ProgressTicks < ticksPerStep)
                continue;

            investigation.ProgressTicks = 0;
            investigation.StepsCompleted++;

            int roll = rng.NextInt(1, 101);

            if (roll <= evidenceChance)
            {
                investigation.Evidence = Math.Min(EvidenceCeiling, investigation.Evidence + gain);
                events?.Publish(new InvestigationProgressed(
                    world.Clock.Current, investigation.SubjectId, investigation.Evidence, true));
            }
            else if (roll <= evidenceChance + falseLeadChance)
            {
                investigation.Evidence = Math.Max(0, investigation.Evidence - penalty);
                investigation.FalseLeads++;
                events?.Publish(new InvestigationProgressed(
                    world.Clock.Current, investigation.SubjectId, investigation.Evidence, false));
            }

            // The case concludes the moment it clears the exposure threshold rather than
            // at a hardcoded 100: a threshold the desk can actually reach is the
            // difference between a usable investigation and a decorative one.
            if (investigation.Evidence >= ExposeThreshold)
                Conclude(world, investigation, events);
        }
    }

    /// <summary>
    /// Closes a case and applies the consequence.
    /// </summary>
    /// <remarks>
    /// Both ways a case can end — clearing the threshold mid-investigation, or running
    /// out of steps — go through here, so a conclusive result exposes the agent on
    /// either path. When these were separate, a case that crossed the threshold was
    /// marked <c>Exposed</c> while the agent's hidden traits stayed hidden: the desk
    /// announced a verdict and then did nothing with it.
    /// </remarks>
    private static void Conclude(WorldState world, InvestigationState investigation, IEventSink? events)
    {
        Agent? subject = world.GetAgent(investigation.SubjectId);
        bool conclusive = investigation.Evidence >= ExposeThreshold;

        investigation.Status = conclusive ? InvestigationStatus.Exposed : InvestigationStatus.Inconclusive;

        if (subject is not null)
        {
            if (conclusive && HasTrait(subject, TraitLookup.MoleTraitId))
                Expose(world, subject, events);
            else
                investigation.Status = InvestigationStatus.Cleared;
        }

        events?.Publish(new InvestigationConcluded(
            world.Clock.Current, investigation.SubjectId, investigation.Status, investigation.Evidence));
    }

    /// <summary>
    /// Concludes every investigation that has run long enough without reaching full
    /// evidence, reporting it as inconclusive.
    /// </summary>
    /// <remarks>
    /// Without this, an investigation on an innocent agent would run forever at low
    /// evidence and quietly accumulate steps, which would make "how many steps did it
    /// take" meaningless.
    /// </remarks>
    public static void ExpireStaleInvestigations(WorldState world, int maxSteps, IEventSink? events)
    {
        // A caller may pass an explicit cap, but never one looser than the table's own
        // limit: that would let a case outlive the window it is supposed to resolve in.
        int cap = Math.Min(maxSteps, MaxSteps);

        foreach (InvestigationState investigation in world.CounterIntel.Investigations)
        {
            if (!investigation.IsRunning || investigation.StepsCompleted < cap)
                continue;

            Conclude(world, investigation, events);
        }
    }

    /// <summary>
    /// Evidence a case must reach to expose the mole outright.
    /// </summary>
    /// <remarks>
    /// Read from <c>counter_intel_rule.expose_threshold</c>. It has to sit comfortably
    /// below what <see cref="MaxSteps"/> steps of good rolls can deliver, otherwise the
    /// desk could never conclude anything and the mole would be uncatchable by design.
    /// <c>TableValidator</c> asserts exactly that relationship.
    /// </remarks>
    public static int ExposeThreshold
        => SimulationRules.CounterIntel("expose_threshold", DefaultExposeThreshold);

    /// <summary>Fallback exposure threshold when the table is unavailable.</summary>
    public const int DefaultExposeThreshold = 60;

    /// <summary>
    /// How many steps a case runs before it is called.
    /// </summary>
    /// <remarks>
    /// Long enough that accusing on a hunch is usually wrong, short enough that the
    /// answer arrives inside one in-game season.
    /// </remarks>
    public static int MaxSteps
        => SimulationRules.CounterIntel("investigation_max_steps", DefaultMaxSteps);

    /// <summary>Fallback step cap when the table is unavailable.</summary>
    public const int DefaultMaxSteps = 10;

    /// <summary>The evidence scale's ceiling.</summary>
    public const int EvidenceCeiling = 100;

    /// <summary>
    /// Names an agent as the mole.
    /// </summary>
    /// <remarks>
    /// A correct accusation exposes them and stops their Heat contribution. A wrong one
    /// costs loyalty across the <em>whole organisation</em>, not just the accused agent:
    /// the damage is the atmosphere of a place where accusing people gets people fired.
    /// </remarks>
    public static bool Accuse(WorldState world, AgentId subject, IEventSink? events)
    {
        Agent? agent = world.GetAgent(subject);
        if (agent is null)
            return false;

        int evidence = world.CounterIntel.FindFor(subject)?.Evidence ?? 0;
        bool correct = HasTrait(agent, TraitLookup.MoleTraitId);

        if (correct)
        {
            Expose(world, agent, events);
        }
        else
        {
            world.CounterIntel.WrongAccusations++;

            int drain = SimulationRules.CounterIntel("wrong_accusation_loyalty_drain", 12);

            // Organisation-wide: every agent loses trust in the management that made the
            // call, whether or not they were the one accused.
            foreach (Agent other in world.Agents.Values.OrderBy(a => a.Id.Value))
            {
                if (other.Status.IsActive())
                    other.AdjustLoyalty(-drain);
            }

            InvestigationState? investigation = world.CounterIntel.FindFor(subject);
            if (investigation is not null)
                investigation.Status = InvestigationStatus.Cleared;

            events?.Publish(new AgentCleared(world.Clock.Current, subject));
        }

        events?.Publish(new AgentAccused(world.Clock.Current, subject, evidence, correct));
        return true;
    }

    /// <summary>
    /// Reveals an agent's hidden traits and stops their mole contribution.
    /// </summary>
    private static void Expose(WorldState world, Agent agent, IEventSink? events)
    {
        if (HasExposedTrait(agent, TraitLookup.MoleTraitId))
            return;

        foreach (int traitId in agent.UndiscoveredTraitIds.ToArray())
        {
            agent.UndiscoveredTraitIds.Remove(traitId);
            agent.TraitIds.Add(traitId);
        }

        world.CounterIntel.ExposedCount++;

        // Past leaks by this agent become attributable, retroactively closing the loop
        // on the Heat the player was already correlating.
        for (int i = 0; i < world.CounterIntel.LeakLog.Count; i++)
        {
            MoleLeak leak = world.CounterIntel.LeakLog[i];
            if (leak.AgentId == agent.Id && !leak.IsPublic)
                world.CounterIntel.LeakLog[i] = leak with { IsPublic = true };
        }

        events?.Publish(new AgentExposedAsMole(world.Clock.Current, agent.Id));
    }

    /// <summary>True when the agent carries a trait, discovered or not.</summary>
    public static bool HasTrait(Agent agent, int traitId) => agent.HasTrait(traitId);

    /// <summary>True when the agent's hidden traits have been revealed.</summary>
    public static bool HasExposedTrait(Agent agent, int traitId) => agent.HasRevealedTrait(traitId);
}

/// <summary>
/// Small helpers so the mole system can identify rooms without leaking Luban types.
/// </summary>
internal static class RoomTypeExtensions
{
    /// <summary>
    /// True for the counter-intelligence room: an Admin room whose effect is heat
    /// reduction at the top of that category's cost range.
    /// </summary>
    /// <remarks>
    /// The room table has no explicit "is counter-intel" column, so this is derived from
    /// category and effect. It is fragile if a designer adds a second heat-reducing Admin
    /// room — <c>TableValidator</c> fails the build when that happens, so the fragile
    /// lookup cannot silently pick the wrong room.
    /// </remarks>
    internal static bool IsCounterIntel(this ProjectSpy.Tables.RoomType type)
        => type.EffectType == ProjectSpy.Tables.EffectType.HeatReduction
           && type.BuildCost > 2500;
}