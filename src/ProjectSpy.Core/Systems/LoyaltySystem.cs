using ProjectSpy.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// The daily loyalty pass and the incidents it escalates into.
/// </summary>
/// <remarks>
/// <para>
/// Once a day every active agent accumulates drift from whichever of six sources apply
/// to them. The sources and their magnitudes come from <c>loyalty_drift.csv</c>; this
/// class only decides which conditions are currently true.
/// </para>
/// <list type="bullet">
/// <item><b>Workload</b> — training below the stamina threshold.</item>
/// <item><b>Pay versus market rate</b> — compared against
/// <c>agent_class.salary_base</c>, so underpaying a class is felt by that class.</item>
/// <item><b>Mission losses and successes</b> — applied by the mission system, read here.</item>
/// <item><b>Idle time</b> — assigned nowhere and not deployed.</item>
/// <item><b>Roommate traits</b> — sharing a room with someone whose traits grate.</item>
/// <item><b>Facility quality</b> — the condition of the room they work in.</item>
/// </list>
/// <para>
/// Drift is clamped so an agent's loyalty can never leave 0-100, and the net daily
/// change is bounded by a table value. Without that bound a badly-run base would take
/// someone from devoted to resigned in a single day, which reads as a bug rather than
/// as a consequence.
/// </para>
/// <para>
/// Past a threshold, an agent may instead produce an incident: a complaint, a demand
/// for a raise, notice of resignation, or defection. Each has a cooldown, so one bad
/// week cannot emit four complaints. The UI is told the coarse
/// <see cref="LoyaltyBand"/> and the escalation enum — never the number
/// (knowledge.md rule 4).
/// </para>
/// </remarks>
public static class LoyaltySystem
{
    /// <summary>Runs the daily drift pass and the escalation check for every active agent.</summary>
    public static void DailyPass(WorldState world, IEventSink? events)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        foreach (Agent agent in world.Agents.Values.OrderBy(a => a.Id.Value).ToArray())
        {
            if (!agent.Status.IsActive())
                continue;

            if (agent.LoyaltyEscalationCooldown > 0)
                agent.LoyaltyEscalationCooldown--;

            if (agent.Status != AgentStatus.OnMission && agent.Status != AgentStatus.Captured)
                ApplyDrift(world, agent);

            // Escalation only considers the day's result, not a cumulative debt: an
            // agent who has just been rescued by a raise should not immediately produce
            // another complaint.
            if (agent.LoyaltyEscalationCooldown == 0)
                CheckEscalation(world, agent, events);
        }
    }

    /// <summary>
    /// Applies one day of drift to one agent and returns the net change.
    /// </summary>
    public static int ApplyDrift(WorldState world, Agent agent)
    {
        int before = agent.Loyalty;
        int delta = 0;

        foreach (LoyaltyDriftCondition condition in ActiveConditions(world, agent))
            delta += SimulationRules.LoyaltyDrift(condition);

        // A hard daily cap. Without it a single catastrophic day could zero someone's
        // loyalty outright and the intermediate states — the ones the player actually
        // sees and reacts to — would be skipped.
        const int maxDailyChange = 15;
        delta = Math.Clamp(delta, -maxDailyChange, maxDailyChange);

        agent.AdjustLoyalty(delta);
        return agent.Loyalty - before;
    }

    /// <summary>
    /// Which drift conditions currently apply to an agent.
    /// </summary>
    /// <remarks>
    /// Several can be true at once and they sum. A burnt-out agent who is also
    /// overworked, in a run-down dorm, and sharing with someone they dislike accumulates
    /// all four — which is the point, because that agent should be visibly on their way
    /// out and the player should be able to see exactly why.
    /// </remarks>
    public static List<LoyaltyDriftCondition> ActiveConditions(WorldState world, Agent agent)
    {
        var active = new List<LoyaltyDriftCondition>();

        if (agent.IsBurntOut)
            active.Add(LoyaltyDriftCondition.Burnout);

        if (agent.IsOverworking)
            active.Add(LoyaltyDriftCondition.Overworked);

        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));

        bool idle = agent.Status == AgentStatus.Idle && room is null;
        if (idle)
            active.Add(LoyaltyDriftCondition.Idle);

        if (agent.Status == AgentStatus.Training)
            active.Add(LoyaltyDriftCondition.Working);

        if (room is not null)
        {
            if (room.Condition >= FacilityQualityGood)
                active.Add(LoyaltyDriftCondition.GoodFacility);
            else if (room.Condition <= FacilityQualityPoor)
                active.Add(LoyaltyDriftCondition.PoorFacility);
        }

        if (IsPaidFairly(agent))
        {
            // Fair pay is a consolation for having work to do, not a reward for
            // clocking in. Gating the bonus on engagement keeps the idle penalty from
            // being cancelled by a generous salary, which would make idleness free.
            // Underpaying an idle agent still stings — that penalty always applies.
            if (!idle)
                active.Add(LoyaltyDriftCondition.PayFair);
        }
        else
        {
            active.Add(LoyaltyDriftCondition.PayUnfair);
        }

        if (HasBadRoommate(world, agent))
            active.Add(LoyaltyDriftCondition.BadRoommate);

        return active;
    }

    /// <summary>Facility condition at or above which a room feels well kept.</summary>
    public const int FacilityQualityGood = 80;

    /// <summary>Facility condition at or below which a room feels run down.</summary>
    public const int FacilityQualityPoor = 40;

    /// <summary>
    /// True when the agent is paid at or above the market rate for their class.
    /// </summary>
    /// <remarks>
    /// Compared against <c>agent_class.salary_base</c>. An unknown class reports 0,
    /// which makes every salary look fair; that stops the penalty rather than inventing
    /// one from a missing table row.
    /// </remarks>
    public static bool IsPaidFairly(Agent agent)
    {
        long market = AgentClasses.MarketSalaryFor(agent.ClassId);
        if (market <= 0)
            return true;

        return agent.SalaryPerWeek >= market;
    }

    /// <summary>
    /// True when someone sharing the agent's room carries a trait they would resent.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: the negative traits that damage a shared space
    /// (absent-minded, drunk, gossipy) rather than the ones that only affect mission
    /// work. A specific roommate complaint is more interesting than a generic morale
    /// tax on every crowded dorm.
    /// </remarks>
    public static bool HasBadRoommate(WorldState world, Agent agent)
    {
        if (agent.AssignedRoomId == 0)
            return false;

        Room? room = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
        if (room is null)
            return false;

        foreach (AgentId occupantId in room.AssignedAgentIds)
        {
            // Nobody is a bad roommate to themselves, so sharing with someone who is
            // already unhappy about someone else does not stack.
            if (occupantId == agent.Id)
                continue;

            Agent? occupant = world.GetAgent(occupantId);
            if (occupant is null)
                continue;

            foreach (int traitId in occupant.TraitIds)
            {
                if (IsResentedTrait(traitId))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for the negative, non-hidden traits that make a shared living space worse.
    /// Hidden traits never count here: the player cannot be penalised for a roommate's
    /// mole-ness before they have any way of knowing about it.
    /// </summary>
    private static bool IsResentedTrait(int traitId)
    {
        Trait? trait = TraitLookup.Find(traitId);
        if (trait is null || trait.IsHidden)
            return false;

        return trait.Polarity == TraitPolarity.Negative
               && trait.EffectType is "TeamMoralePenalty" or "LoyaltyDecay";
    }

    /// <summary>
    /// Rolls for a loyalty incident against one agent.
    /// </summary>
    /// <remarks>
    /// Thresholds are walked from the most severe down, so an agent at 5 loyalty is
    /// offered defection rather than a complaint. The first threshold whose roll
    /// succeeds wins and the agent is put on cooldown for that row's period.
    /// </remarks>
    public static LoyaltyEscalation? CheckEscalation(WorldState world, Agent agent, IEventSink? events)
    {
        IRng rng = world.RngStreams[RngStreams.StreamKind.Event];

        IReadOnlyList<ProjectSpy.Tables.LoyaltyThreshold> thresholds = SimulationRules.LoyaltyThresholds();

        // Descending severity: the lowest threshold is the worst outcome.
        for (int i = thresholds.Count - 1; i >= 0; i--)
        {
            ProjectSpy.Tables.LoyaltyThreshold threshold = thresholds[i];

            // A threshold means "at or below this loyalty". Loyalty above it is safe.
            if (agent.Loyalty > threshold.Threshold)
                continue;

            if (threshold.ChancePercent <= 0)
                continue;

            if (rng.NextInt(1, 101) > threshold.ChancePercent)
                continue;

            LoyaltyEscalation escalation = ParseEscalation(threshold.Escalation);
            ApplyEscalation(world, agent, escalation, events);

            agent.LoyaltyEscalationCooldown = Math.Max(agent.LoyaltyEscalationCooldown, threshold.CooldownDays);
            return escalation;
        }

        return null;
    }

    /// <summary>Maps a table escalation string to the Core enum.</summary>
    private static LoyaltyEscalation ParseEscalation(string raw)
    {
        if (raw.Contains("defection", StringComparison.OrdinalIgnoreCase))
            return LoyaltyEscalation.Defection;

        if (raw.Contains("resignation", StringComparison.OrdinalIgnoreCase))
            return LoyaltyEscalation.ResignationNotice;

        if (raw.Contains("raise", StringComparison.OrdinalIgnoreCase))
            return LoyaltyEscalation.RaiseDemand;

        return LoyaltyEscalation.Complaint;
    }

    /// <summary>
    /// Carries out an escalation. Raising pay and resignation are cheap; defection takes
    /// the agency's intel with them, which is what makes it the real cliff-edge.
    /// </summary>
    public static void ApplyEscalation(
        WorldState world,
        Agent agent,
        LoyaltyEscalation escalation,
        IEventSink? events)
    {
        agent.LastEscalation = escalation;
        events?.Publish(new LoyaltyEscalated(world.Clock.Current, agent.Id, escalation, agent.LoyaltyBand));

        switch (escalation)
        {
            case LoyaltyEscalation.Complaint:
                // A complaint is noise. No state change beyond the recorded escalation.
                break;

            case LoyaltyEscalation.RaiseDemand:
                // Meeting the demand is the player's decision, not the agent's: Core
                // surfaces the demand and leaves the salary alone.
                break;

            case LoyaltyEscalation.ResignationNotice:
                Resign(world, agent, LoyaltyEscalation.ResignationNotice, events);
                break;

            case LoyaltyEscalation.Defection:
                Defect(world, agent, events);
                break;
        }
    }

    /// <summary>Takes an agent off the roster.</summary>
    public static void Resign(WorldState world, Agent agent, LoyaltyEscalation cause, IEventSink? events)
    {
        if (agent.AssignedRoomId != 0)
        {
            world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId))?.AssignedAgentIds.Remove(agent.Id);
            agent.AssignedRoomId = 0;
        }

        AgentStatus from = agent.Status;
        agent.Status = AgentStatus.Retired;
        agent.Normalize();

        events?.Publish(new AgentResigned(world.Clock.Current, agent.Id, cause));
        events?.Publish(new AgentStatusChanged(world.Clock.Current, agent.Id, from, AgentStatus.Retired));
    }

    /// <summary>
    /// An agent leaves for a rival and takes what they knew.
    /// </summary>
    /// <remarks>
    /// The intel cost is bounded by what the agency actually holds. Losing more intel
    /// than exists would underflow, and a runaway negative resource is the single worst
    /// thing a rule path in this codebase can do.
    /// </remarks>
    public static void Defect(WorldState world, Agent agent, IEventSink? events)
    {
        const int baseIntelLoss = 25;

        // Clamp to what is actually held, then deduct through the audited spend path:
        // AddIntel ignores non-positive amounts, so a negative "add" would silently
        // discard the loss and a defection would cost the player nothing.
        int lost = Math.Min(world.Resources.Intel, baseIntelLoss);
        if (lost > 0 && world.Resources.TrySpendIntel(lost, out Resources afterIntel))
            world.Resources = afterIntel;

        // A defector also takes their own secrets to the grave in a way that raises the
        // chance someone else is still out there — expressed as heat, the game's word
        // for "attention".
        world.Resources = world.Resources.AddHeat(1);

        Resign(world, agent, LoyaltyEscalation.Defection, events);
        events?.Publish(new AgentDefected(world.Clock.Current, agent.Id, lost));
    }

    /// <summary>
    /// Applies the result of a finished mission to the agents who went on it.
    /// </summary>
    /// <remarks>
    /// Takes the deployed roster explicitly rather than sweeping the whole base:
    /// only people who were actually in the field should feel the outcome, and the
    /// mission system already knows who that was.
    /// </remarks>
    public static void ApplyMissionOutcome(WorldState world, IEnumerable<AgentId> deployed, bool success)
    {
        int delta = SimulationRules.LoyaltyDrift(success
            ? LoyaltyDriftCondition.MissionSuccess
            : LoyaltyDriftCondition.MissionLoss);

        if (delta == 0)
            return;

        foreach (AgentId id in deployed)
        {
            Agent? agent = world.GetAgent(id);
            if (agent is not null && agent.Status.IsActive())
                agent.AdjustLoyalty(delta);
        }
    }

    /// <summary>
    /// The coarse band the UI shows for a loyalty value, resolved from
    /// <c>morale_band.csv</c>.
    /// </summary>
    /// <remarks>
    /// This is the only loyalty-derived value Presentation is permitted to see.
    /// </remarks>
    public static LoyaltyBand BandFor(int loyalty)
    {
        LoyaltyBand band = LoyaltyBand.Resentful;

        foreach (ProjectSpy.Tables.MoraleBand row in SimulationRules.MoraleBands())
        {
            if (loyalty >= row.LoyaltyFloor && loyalty <= row.LoyaltyCeiling)
            {
                band = ToBand(row.Band);
                break;
            }
        }

        return band;
    }

    private static LoyaltyBand ToBand(int bandNumber) => bandNumber switch
    {
        1 => LoyaltyBand.Devoted,
        2 => LoyaltyBand.Content,
        3 => LoyaltyBand.Uneasy,
        _ => LoyaltyBand.Resentful,
    };
}