using ProjectSpy.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// Generates the candidate pool and turns candidates into hires.
/// </summary>
/// <remarks>
/// <para>
/// Pool size and quality are gated by the HR room's level and the agency's
/// Reputation, via <c>recruit_rule</c>. Without an HR room there are no candidates at
/// all, which is what makes HR worth building rather than a stat to ignore.
/// </para>
/// <para>
/// Candidate generation draws from two independent streams: the <c>Recruit</c> stream
/// for the class, skills and salary, and the <c>Trait</c> stream for traits. Keeping
/// them separate matters — adding a trait roll must not shift every subsequent
/// candidate's skills, because that would invalidate saved replays (knowledge.md rule
/// 6).
/// </para>
/// <para>
/// Hidden traits are generated like any other but stored in
/// <see cref="Agent.UndiscoveredTraitIds"/>, so no UI query can leak the mole by
/// accident. The player is only told a candidate "has something hidden" once enough
/// investigation has gone in.
/// </para>
/// </remarks>
public static class RecruitmentSystem
{
    /// <summary>
    /// Refreshes the candidate pool when it is due. Safe to call every tick; it no-ops
    /// until the refresh interval has elapsed.
    /// </summary>
    /// <returns>True when the pool was regenerated.</returns>
    public static bool RefreshPoolIfDue(WorldState world, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (world.RecruitPoolRefreshTicks > 0)
        {
            world.RecruitPoolRefreshTicks--;
            return false;
        }

        RefreshPool(world, events);
        return true;
    }

    /// <summary>Regenerates the pool immediately.</summary>
    public static void RefreshPool(WorldState world, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        // The pool is a snapshot of the labour market, so it is replaced wholesale
        // rather than appended to. Agents already hired are untouched.
        world.Recruits.Clear();
        world.RecruitPoolGeneratedOnTick = world.Clock.Current;

        int hrLevel = HrRoomLevel(world);
        int reputationTier = SimulationRules.ReputationTier(world.Resources.Reputation);
        RecruitRule? rule = SimulationRules.RecruitFor(hrLevel, reputationTier);

        if (rule is null)
        {
            // No HR room built: nobody is applying. Refreshing the timer anyway keeps
            // the check cheap and means an HR room takes effect on the next cycle
            // rather than requiring an extra tick.
            world.RecruitPoolRefreshTicks = RefreshIntervalDays(world);
            return;
        }

        IRng rng = world.RngStreams[RngStreams.StreamKind.Recruit];

        for (int i = 0; i < rule.PoolSize; i++)
        {
            Agent candidate = GenerateCandidate(world, rule, rng);
            events?.Publish(new CandidateGenerated(
                world.Clock.Current, candidate.Id, candidate.ClassId, (int)candidate.SalaryPerWeek));
        }

        world.RecruitPoolRefreshTicks = RefreshIntervalDays(world);

        events?.Publish(new RecruitPoolRefreshed(
            world.Clock.Current, rule.PoolSize, hrLevel, reputationTier));
    }

    /// <summary>
    /// Days between pool refreshes. Tighter when the HR room is better staffed.
    /// </summary>
    public static int RefreshIntervalDays(WorldState world)
    {
        int hrLevel = HrRoomLevel(world);

        // A level-1 HR room refreshes weekly; a level-3 one twice a week. Divided rather
        // than subtracted so the ladder stays sane if a designer adds a fourth level.
        return Math.Max(1, 7 / Math.Max(1, hrLevel));
    }

    /// <summary>The level of the best HR room in the base, or zero when there is none.</summary>
    public static int HrRoomLevel(WorldState world)
    {
        int best = 0;
        foreach (Room room in world.BaseLayout.Rooms)
        {
            RoomType? type = RoomDefinitions.Find(room.TypeId);
            if (type is null || type.EffectType != EffectType.RecruitQuality)
                continue;

            if (room.IsUnderConstruction)
                continue;

            // Only rooms that actually advertise recruiting quality count as HR. The
            // armory and workshop share the effect, which is deliberate: they improve
            // candidate quality but should not stand in for an HR office's throughput.
            if (room.TypeId != HrRoomTypeId(world))
                continue;

            best = Math.Max(best, room.Level);
        }

        return best;
    }

    /// <summary>The room type id of the HR office, resolved from the table by merge group.</summary>
    public static int HrRoomTypeId(WorldState world)
    {
        foreach (RoomType type in SimulationRules.AllRoomTypes())
        {
            if (type.Category == RoomCategory.Admin && type.EffectType == EffectType.RecruitQuality)
                return type.Id;
        }

        return 0;
    }

    /// <summary>
    /// Generates one candidate: class, skills around the class baseline with variance,
    /// then traits.
    /// </summary>
    public static Agent GenerateCandidate(WorldState world, RecruitRule rule, IRng rng)
    {
        IReadOnlyList<AgentClass> classes = AgentClasses.All();
        int classId = classes.Count > 0 ? rng.Pick(classes).Id : 0;

        SkillSet baseSkills = AgentClasses.BaseSkillsFor(classId);
        int variance = Math.Max(0, rule.SkillVariance);
        int quality = rule.QualityBonus;

        var skills = new SkillSet(
            RollSkill(baseSkills.Infiltration, variance, quality, rng),
            RollSkill(baseSkills.Combat, variance, quality, rng),
            RollSkill(baseSkills.Tech, variance, quality, rng),
            RollSkill(baseSkills.Social, variance, quality, rng),
            RollSkill(baseSkills.Nerve, variance, quality, rng));

        int market = AgentClasses.MarketSalaryFor(classId);
        long salary = market <= 0 ? 0 : market + rng.NextInt(0, Math.Max(1, variance + 1));

        var candidate = new Agent
        {
            Name = Names.Next(rng),
            Codename = Names.NextCodename(rng),
            ClassId = classId,
            Level = 1,
            Skills = SkillSet.ClampNonNegative(skills),
            PhysicalStamina = Agent.MaxStamina,
            MentalStamina = Agent.MaxStamina,
            Loyalty = Agent.StartingLoyalty,
            SalaryPerWeek = salary,
            Status = AgentStatus.Idle,
        };

        AssignTraits(world, candidate, rule, rng);

        return world.AddRecruit(candidate);
    }

    /// <summary>
    /// Rolls one skill around the class baseline.
    /// </summary>
    /// <remarks>
    /// Variance is a spread, not a bonus: a candidate can land below their class
    /// baseline as well as above it, which is what stops every Technician from
    /// arriving with identical Tech. The quality bonus pushes the whole roll upward,
    /// so a good HR room produces better people rather than luckier ones.
    /// </remarks>
    private static int RollSkill(int baseline, int variance, int quality, IRng rng)
    {
        int offset = variance <= 0 ? 0 : rng.NextInt(-variance, variance + 1);
        return Math.Max(0, baseline + offset + quality);
    }

    /// <summary>
    /// Rolls 1-3 traits, respecting <c>conflicts_with</c>, and separates hidden traits
    /// into the undiscovered list.
    /// </summary>
    /// <remarks>
    /// Traits come from the <c>Trait</c> stream so that changing the number of skills
    /// rolled above cannot shift which traits any other candidate receives.
    /// </remarks>
    public static void AssignTraits(WorldState world, Agent agent, RecruitRule rule, IRng rng)
    {
        IRng traitRng = world.RngStreams[RngStreams.StreamKind.Trait];

        int count = rule.TraitCountMin;
        if (rule.TraitCountMax > rule.TraitCountMin)
            count = traitRng.NextInt(rule.TraitCountMin, rule.TraitCountMax + 1);

        bool rollHidden = rule.HiddenTraitChance > 0
                          && traitRng.NextInt(1, 101) <= rule.HiddenTraitChance;

        var chosen = new List<int>();
        var conflicts = new HashSet<int>();

        for (int attempt = 0; attempt < count * 4 && chosen.Count < count; attempt++)
        {
            bool wantHidden = rollHidden && chosen.Count == 0;
            IReadOnlyList<int> pool = wantHidden ? TraitLookup.HiddenTraitIds() : TraitLookup.VisibleTraitIds();
            if (pool.Count == 0)
                continue;

            int traitId = traitRng.Pick(pool);

            // conflicts_with is symmetric in intent but stored one-way, so both
            // directions are checked. Skipping the check would let a designer author a
            // conflict that never actually fires.
            if (conflicts.Contains(traitId))
                continue;

            Trait? trait = TraitLookup.Find(traitId);
            if (trait is null)
                continue;

            bool blocked = false;
            foreach (int existing in chosen)
            {
                if (ConflictsWith(existing, traitId) || ConflictsWith(traitId, existing))
                {
                    blocked = true;
                    break;
                }
            }

            if (blocked)
                continue;

            chosen.Add(traitId);
            foreach (int c in ParseIdList(trait.ConflictsWith))
                conflicts.Add(c);
        }

        foreach (int traitId in chosen)
        {
            Trait? trait = TraitLookup.Find(traitId);

            // Hidden traits are stored but flagged unseen. The distinction is the whole
            // security model: Core knows, the player does not.
            if (trait is not null && trait.IsHidden)
                agent.UndiscoveredTraitIds.Add(traitId);
            else
                agent.TraitIds.Add(traitId);
        }
    }

    /// <summary>True when trait <paramref name="a"/> lists trait <paramref name="b"/> as conflicting.</summary>
    private static bool ConflictsWith(int a, int b)
    {
        Trait? trait = TraitLookup.Find(a);
        if (trait is null || string.IsNullOrWhiteSpace(trait.ConflictsWith))
            return false;

        foreach (int id in ParseIdList(trait.ConflictsWith))
        {
            if (id == b)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<int> ParseIdList(string raw)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(raw))
            return ids;

        foreach (string part in raw.Split(','))
        {
            if (int.TryParse(part.Trim(), out int id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The up-front fee to hire a candidate, from the class's base and the pool's
    /// quality. Rises with quality so a strong candidate is a real commitment.
    /// </summary>
    public static long HiringFeeFor(Agent candidate)
    {
        AgentClass? agentClass = AgentClasses.Find(candidate.ClassId);
        long fee = agentClass?.RecruitCostBase ?? 0;
        if (fee <= 0)
            return 0;

        // A visibly exceptional candidate costs more; the scaling is on total skill so
        // it tracks quality without needing a separate column.
        int quality = candidate.Skills.Average();
        return SimulationRules.PercentOf(fee, 100 + (quality / 2));
    }
}

/// <summary>
/// Candidate name generation from the <c>agent_name</c> table.
/// </summary>
/// <remarks>
/// Names are pure flavour, but they are generated from the seeded <c>Recruit</c> stream
/// so a replay reproduces the same roster — including the names.
/// </remarks>
public static class Names
{
    private static string PickOf(IRng rng, string kind)
    {
        var matching = new List<string>();
        foreach (AgentName row in NameTable.All())
        {
            if (row.Kind.ToString().Equals(kind, StringComparison.Ordinal))
                matching.Add(row.Value);
        }

        return matching.Count > 0 ? rng.Pick(matching) : string.Empty;
    }

    /// <summary>A given name.</summary>
    public static string Next(IRng rng) => PickOf(rng, "First");

    /// <summary>A codename.</summary>
    public static string NextCodename(IRng rng) => PickOf(rng, "Codename");
}