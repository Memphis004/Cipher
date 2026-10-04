using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using ObjectiveRuleRow = ProjectSpy.Tables.ObjectiveRule;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// Why a proposed squad was refused at dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Reason codes, never sentences, for the same reason every other command result in
/// this assembly is a code: the dispatch screen has to say "Vanya is too injured to
/// climb a wall" in whichever language the player chose, and the sentence would have to
/// be written twice.
/// </para>
/// <para>
/// <b>Every reason here is recoverable by the player.</b> There is no "invalid squad"
/// catch-all, because a message that cannot be acted on is a dead end, and a dispatch
/// screen full of dead ends is a screen people stop reading.
/// </para>
/// </remarks>
public enum DispatchRefusalReason
{
    /// <summary>Not a refusal.</summary>
    None = 0,

    /// <summary>Fewer agents than <c>squad_rule.squad_min_size</c>.</summary>
    SquadTooSmall = 1,

    /// <summary>More agents than <c>squad_rule.squad_max_size</c>.</summary>
    SquadTooLarge = 2,

    /// <summary>The agent id is not on the roster.</summary>
    UnknownAgent = 3,

    /// <summary>The agent is burnt out, captured, resting or otherwise unavailable.</summary>
    AgentNotDeployable = 4,

    /// <summary>Physical stamina below <c>squad_rule.squad_min_physical_stamina</c>.</summary>
    PhysicalStaminaTooLow = 5,

    /// <summary>Mental stamina below <c>squad_rule.squad_min_mental_stamina</c>.</summary>
    MentalStaminaTooLow = 6,

    /// <summary>Injury severity above <c>squad_rule.squad_max_injury_severity</c>.</summary>
    InjuryTooSevere = 7,

    /// <summary>The <c>agent_role</c> id does not exist.</summary>
    UnknownRole = 8,

    /// <summary>The objective type requires a role nobody in the squad has.</summary>
    MissingRequiredRole = 9,

    /// <summary>
    /// The site has a forward command post and nobody is staffing it.
    /// </summary>
    /// <remarks>
    /// Reported rather than refused, and the distinction matters. A site with a post and
    /// no Handler is a real choice — five support abilities are on the table and the
    /// player has decided against them — while a <em>Handler</em> on a site with no post
    /// is simply a wasted specialist, and is not an error at all. See
    /// <see cref="SquadComposition.UnstaffedPost"/>.
    /// </remarks>
    UnstaffedCommandPost = 10,

    /// <summary>More than one member was given the post-only Handler role.</summary>
    DuplicateCommandPost = 11,

    /// <summary>An agent was assigned more gadgets than the per-agent slot count.</summary>
    TooManyGadgets = 12,

    /// <summary>The squad's total carry weight is above the cap.</summary>
    CarryWeightTooHigh = 13,

    /// <summary>The objective type has no row in <c>objective_rule</c>.</summary>
    UnknownObjectiveType = 14,

    /// <summary>Two members of the squad share an agent id.</summary>
    DuplicateAgent = 15,
}

/// <summary>
/// One reason a squad was refused, with the values a UI needs to name it.
/// </summary>
/// <param name="Reason">The code.</param>
/// <param name="AgentId">
/// The agent the refusal is about, or <see cref="AgentId.None"/> when it is about the
/// squad as a whole.
/// </param>
/// <remarks>
/// Named "refusal" because that is what nearly all of them are. One value —
/// <see cref="DispatchRefusalReason.UnstaffedCommandPost"/> — is advisory, and the
/// dispatch screen shows it as a warning the player can dispatch through. A single
/// result type for "no" and for "yes, but" is what lets the player tell the two apart
/// without a second structure.
/// </remarks>
/// <param name="Args">
/// Numbers the localization template interpolates. Which ones are meaningful is a
/// property of <paramref name="Reason"/> and is documented on each member above; Core
/// does not know the order the Thai translator will want them in, and fixing it here
/// would fix it for one language.
/// </param>
public readonly record struct DispatchRefusal(
    DispatchRefusalReason Reason,
    AgentId AgentId,
    IReadOnlyList<int> Args)
{
    /// <summary>The localization key the dispatch screen looks this refusal up by.</summary>
    public string MessageKey => $"dispatch.refusal.{Reason.ToString().ToLowerInvariant()}";

    /// <summary>Builds a squad-wide refusal with no values.</summary>
    public static DispatchRefusal Squad(DispatchRefusalReason reason)
        => new(reason, AgentId.None, Array.Empty<int>());

    /// <summary>Builds a refusal about one agent.</summary>
    public static DispatchRefusal About(DispatchRefusalReason reason, AgentId agentId, params int[] args)
        => new(reason, agentId, args);
}

/// <summary>
/// A squad as the player proposed it at the dispatch screen, before anybody has checked
/// it against the objective or the roster.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mutable, and checked on demand.</b> The dispatch screen is a list the player edits:
/// they add a Mule, take away the Medic, hand somebody a gadget and then take it back.
/// Making this a sealed immutable value would mean rebuilding it on every keystroke and
/// would not stop any of the mistakes the validation exists to catch. The checks are a
/// pure function of the composition and the roster, so calling
/// <see cref="SquadComposition.Validate"/> every time the UI changes is free of side
/// effects.
/// </para>
/// <para>
/// <b>It holds no <see cref="Agent"/> objects.</b> It holds ids, and reads the roster
/// through the <see cref="WorldState"/> passed to validation. A composition that
/// captured an agent's stamina at the moment it was built would validate a team against
/// numbers that had already changed.
/// </para>
/// </remarks>
public sealed class SquadComposition
{
    /// <summary>The members, in the order they were added. Order is the tie-break for
    /// every "who is nearest / who is first" question the mission asks.</summary>
    public List<SquadMember> Members { get; } = new();

    /// <summary>
    /// What the team was dispatched to do.
    /// </summary>
    /// <remarks>
    /// Settable rather than init-only because this is built by the dispatch screen
    /// before the contract is chosen, and rebuilding the member list to change the
    /// objective would lose the player's gadget assignments.
    /// </remarks>
    public TableObjectiveType ObjectiveType { get; set; } = TableObjectiveType.StealData;

    /// <summary>
    /// The member holding the forward command post, if one was assigned.
    /// </summary>
    /// <remarks>
    /// Not derived from the roles on purpose: whether the post is staffed is a fact
    /// about this dispatch, and it can be false even when somebody holds a post-only
    /// role, because the site template may have no post to staff.
    /// </remarks>
    public AgentId? CommandPostHandler { get; set; }

    /// <summary>Creates a member with a role already assigned.</summary>
    public SquadMember Add(AgentId agentId, int roleId)
    {
        var member = new SquadMember { AgentId = agentId, RoleId = roleId };
        Members.Add(member);
        return member;
    }

    /// <summary>The member for an agent, or null.</summary>
    public SquadMember? MemberFor(AgentId agentId)
    {
        foreach (SquadMember member in Members)
        {
            if (member.AgentId == agentId)
                return member;
        }

        return null;
    }

    /// <summary>Every gadget the squad is carrying, for the carry-weight total.</summary>
    public int TotalWeight
    {
        get
        {
            int total = 0;

            foreach (SquadMember member in Members)
                total += member.Gadgets.TotalWeight;

            return total;
        }
    }

    /// <summary>
    /// The largest total carry weight this squad is allowed.
    /// </summary>
    /// <remarks>
    /// <see cref="SimulationRules.Squad"/> for the base, plus the Mule's bonus when
    /// somebody is playing the Mule. The bonus is a squad property on purpose: a mule is
    /// only useful when the rest of the team hands them everything, so "the squad may
    /// carry more" is what hiring a Mule buys, not "one person may carry more".
    /// </remarks>
    public int CarryWeightCap
    {
        get
        {
            int cap = SimulationRules.Squad("squad_carry_weight_base", DefaultCarryWeightBase);

            foreach (SquadMember member in Members)
            {
                if (member.Behaviour == RoleBehaviour.Mule)
                    cap += SimulationRules.Squad(
                        "squad_carry_weight_mule_bonus", DefaultMuleCarryBonus);
            }

            return Math.Min(
                cap,
                SimulationRules.Squad("squad_carry_weight_max", DefaultCarryWeightMax));
        }
    }

    /// <summary>
    /// Checks the whole composition against the roster and the objective.
    /// </summary>
    /// <param name="world">The world the agents come from.</param>
    /// <param name="siteHasCommandPost">
    /// Whether the site template has a forward post to staff. A post-only role on a site
    /// without a post is a mistake worth reporting rather than a harmless spare hand.
    /// </param>
    /// <returns>
    /// Every reason the squad was refused, in a fixed order: squad-wide reasons first,
    /// then per-agent reasons in roster order. Empty means the mission may be dispatched.
    /// </returns>
    public IReadOnlyList<DispatchRefusal> Validate(WorldState world, bool siteHasCommandPost)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        var refusals = new List<DispatchRefusal>();

        ValidateSize(refusals);
        ValidateAgents(world, refusals);
        ValidateRoles(refusals, siteHasCommandPost);
        ValidateLoad(refusals);

        return refusals;
    }

    /// <summary>True when the squad may be dispatched.</summary>
    public bool CanDispatch(WorldState world, bool siteHasCommandPost)
        => Validate(world, siteHasCommandPost).Count == 0;

    /// <summary>
    /// The roles the objective needs, as <c>auto_behaviour_set</c> tokens.
    /// </summary>
    /// <remarks>
    /// Read from <c>objective_rule.required_roles</c> rather than from a switch, so a
    /// designer can add a required role to an objective without a code change — and so
    /// the dispatch screen's "this mission needs a Hacker" line comes from the same row
    /// the validator reads, rather than a second copy that can drift.
    /// </remarks>
    public IReadOnlyList<string> RequiredRoles()
    {
        ObjectiveRuleRow? rule = SimulationRules.ObjectiveRuleFor(ObjectiveType);
        return rule is null
            ? Array.Empty<string>()
            : SimulationRules.Tags(rule.RequiredRoles);
    }

    private void ValidateSize(List<DispatchRefusal> refusals)
    {
        int min = SimulationRules.Squad("squad_min_size", DefaultMinSize);
        int max = SimulationRules.Squad("squad_max_size", DefaultMaxSize);

        if (Members.Count < min)
            refusals.Add(DispatchRefusal.Squad(DispatchRefusalReason.SquadTooSmall));

        if (Members.Count > max)
            refusals.Add(DispatchRefusal.Squad(DispatchRefusalReason.SquadTooLarge));
    }

    private void ValidateAgents(WorldState world, List<DispatchRefusal> refusals)
    {
        int minPhysical = SimulationRules.Squad(
            "squad_min_physical_stamina", DefaultMinPhysicalStamina);
        int minMental = SimulationRules.Squad(
            "squad_min_mental_stamina", DefaultMinMentalStamina);
        int maxInjury = SimulationRules.Squad(
            "squad_max_injury_severity", DefaultMaxInjurySeverity);

        foreach (SquadMember member in Members)
        {
            Agent? agent = world.Agents.TryGetValue(member.AgentId, out Agent? found)
                ? found
                : null;

            if (agent is null)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.UnknownAgent, member.AgentId));
                continue;
            }

            // Seen before the stamina checks: an agent who is captured is not
            // "slightly too tired", and telling the player to send them for a rest when
            // they are in a cell across the country would be a worse message than the
            // true one.
            if (!agent.IsDeployable)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.AgentNotDeployable, member.AgentId));
                continue;
            }

            if (agent.PhysicalStamina < minPhysical)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.PhysicalStaminaTooLow,
                    member.AgentId, agent.PhysicalStamina, minPhysical));
            }

            if (agent.MentalStamina < minMental)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.MentalStaminaTooLow,
                    member.AgentId, agent.MentalStamina, minMental));
            }

            if (agent.InjurySeverity > maxInjury)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.InjuryTooSevere,
                    member.AgentId, agent.InjurySeverity, maxInjury));
            }
        }

        ValidateNoDuplicates(refusals);
    }

    private void ValidateNoDuplicates(List<DispatchRefusal> refusals)
    {
        var seen = new HashSet<int>();

        foreach (SquadMember member in Members)
        {
            if (!seen.Add(member.AgentId.Value))
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.DuplicateAgent, member.AgentId));
            }
        }
    }

    private void ValidateRoles(List<DispatchRefusal> refusals, bool siteHasCommandPost)
    {
        if (SimulationRules.ObjectiveRuleFor(ObjectiveType) is null)
        {
            refusals.Add(DispatchRefusal.Squad(DispatchRefusalReason.UnknownObjectiveType));
            return;
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);

        foreach (SquadMember member in Members)
        {
            SquadRole? role = member.Role;

            if (role is null)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.UnknownRole, member.AgentId, member.RoleId));
                continue;
            }

            roles.Add(role.RoleKey);
        }

        ValidateRequiredRoles(refusals, roles);
        ValidateCommandPost(refusals, siteHasCommandPost);
    }

    /// <summary>
    /// Checks the squad covers every role the objective names.
    /// </summary>
    /// <remarks>
    /// Matched by role, not by behaviour. The saboteur and the point man run the same
    /// behaviour but are not interchangeable on a PlantBug — the mission needs somebody
    /// who can plant — and a requirement expressed in behaviours would have been
    /// satisfied by the wrong person, or by nobody at all.
    /// </remarks>
    private void ValidateRequiredRoles(List<DispatchRefusal> refusals, HashSet<string> roles)
    {
        foreach (string token in RequiredRoles())
        {
            // An unknown token is caught by TableValidator against the agent_role name
            // vocabulary, so one reaching here unrecognised is a bug in Core rather than
            // bad data. Skipping it is the response that does not make the dispatch
            // screen claim this mission needs nothing when it plainly does.
            if (roles.Contains(token))
                continue;

            refusals.Add(DispatchRefusal.Squad(DispatchRefusalReason.MissingRequiredRole));
        }
    }

    private void ValidateCommandPost(List<DispatchRefusal> refusals, bool siteHasCommandPost)
    {
        int handlers = 0;

        foreach (SquadMember member in Members)
        {
            if (member.RequiresCommandPost)
                handlers++;
        }

        // Two Handlers is not a stronger post. It is a role the table will not give a
        // second set of support abilities to, so accepting it would be promising the
        // player a thing the simulation cannot deliver.
        if (handlers > 1)
        {
            refusals.Add(DispatchRefusal.Squad(DispatchRefusalReason.DuplicateCommandPost));
        }

        // An unstaffed post on a site that has one is a warning, not a block. The player
        // may want five people and no radio operator, and the game owes them that. The
        // reverse — a Handler sent to a site with no post — is not even a warning: the
        // role simply brings its gadgets and nothing else, which is a legitimate thing to
        // do and is why there is no refusal for it.
        if (handlers == 0 && siteHasCommandPost)
        {
            refusals.Add(DispatchRefusal.Squad(DispatchRefusalReason.UnstaffedCommandPost));
        }
    }

    /// <summary>
    /// Whether this dispatch leaves the site's forward command post with nobody in it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Validate"/> because this is a question the dispatch
    /// screen asks rather than an answer it acts on. A squad that cannot be sent because
    /// a post is unstaffed is a squad the player cannot choose to send, and choosing to
    /// forgo the radio is the whole point of having five roles to pick from.
    /// </remarks>
    public bool UnstaffedPost(bool siteHasCommandPost)
        => siteHasCommandPost && CommandPostHandler is null && !Members.Any(m => m.RequiresCommandPost);

    private void ValidateLoad(List<DispatchRefusal> refusals)
    {
        int slots = SimulationRules.Squad("squad_gadget_slots_per_agent", DefaultGadgetSlots);

        foreach (SquadMember member in Members)
        {
            if (member.Gadgets.Count > slots)
            {
                refusals.Add(DispatchRefusal.About(
                    DispatchRefusalReason.TooManyGadgets,
                    member.AgentId, member.Gadgets.Count, slots));
            }
        }

        int cap = CarryWeightCap;
        int weight = TotalWeight;

        if (weight > cap)
        {
            refusals.Add(new DispatchRefusal(
                DispatchRefusalReason.CarryWeightTooHigh,
                AgentId.None,
                new[] { weight, cap }));
        }
    }

    // Fallbacks for every squad_rule key.
    //
    // Only reachable when the table has not been loaded, which in this assembly means a
    // unit test that deliberately skipped tables. They are the CSV's values so that a
    // missing table degrades to the shipped balance rather than to a squad that can carry
    // nothing and needs no rest.

    /// <summary>Smallest deployable squad, absent the table.</summary>
    private const int DefaultMinSize = 3;

    /// <summary>Largest deployable squad, absent the table.</summary>
    private const int DefaultMaxSize = 5;

    /// <summary>Physical stamina floor, absent the table.</summary>
    private const int DefaultMinPhysicalStamina = 60;

    /// <summary>Mental stamina floor, absent the table.</summary>
    private const int DefaultMinMentalStamina = 40;

    /// <summary>Injury ceiling, absent the table.</summary>
    private const int DefaultMaxInjurySeverity = 40;

    /// <summary>Base carry weight cap, absent the table.</summary>
    private const int DefaultCarryWeightBase = 30;

    /// <summary>Absolute carry weight cap, absent the table.</summary>
    private const int DefaultCarryWeightMax = 45;

    /// <summary>The Mule's carry bonus, absent the table.</summary>
    private const int DefaultMuleCarryBonus = 15;

    /// <summary>Gadget slots per agent, absent the table.</summary>
    private const int DefaultGadgetSlots = 3;

    /// <inheritdoc/>
    public override string ToString()
        => $"{Members.Count} for {ObjectiveType}, {TotalWeight}/{CarryWeightCap} weight";
}