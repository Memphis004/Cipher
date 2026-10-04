using ProjectSpy.Tables;

using RoleRow = ProjectSpy.Tables.AgentRole;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// What a squad member does when the player is not directly controlling them and has
/// issued no standing order.
/// </summary>
/// <remarks>
/// <para>
/// A closed set of seven, deliberately. The brief says the player's squad must be
/// <em>predictable</em>: "a constrained, readable behaviour set (not GOAP — the player
/// must be able to predict their own squad)". Every member of this enum is something a
/// player can say out loud before the mission starts — "the Point Man will push to the
/// next waypoint and hold; the Medic will go to whoever is worst off" — which is a
/// promise the simulation can keep and a planner cannot.
/// </para>
/// <para>
/// <b>Not a superset of the guards' GOAP, and not a subset either.</b> The site's NPCs
/// plan because they have no player and must react to a whole building. The squad does
/// not, because the player is watching it and expects it to do what its role says. That
/// asymmetry is the whole reason this type exists next to <c>GoapDirector</c> rather than
/// being a special goal set inside it.
/// </para>
/// <para>
/// The mapping from the table's <c>auto_behaviour_set</c> string to this enum is a
/// switch in <see cref="SquadRoles"/> rather than a parse, so adding a behaviour set to
/// the CSV without adding it here is a compile-visible gap rather than a role that
/// silently does nothing.
/// </para>
/// </remarks>
public enum RoleBehaviour
{
    /// <summary>Nothing: this role acts only on orders.</summary>
    None = 0,

    /// <summary>Advances to the next marked waypoint along the route and holds there.</summary>
    PointMan = 1,

    /// <summary>Stays put facing an assigned direction and reports contacts.</summary>
    Overwatch = 2,

    /// <summary>Picks up and carries loot and downed allies.</summary>
    Mule = 3,

    /// <summary>Moves to the nearest hackable thing and works it.</summary>
    Hacker = 4,

    /// <summary>Moves to the most injured ally.</summary>
    Medic = 5,

    /// <summary>Moves ahead of the squad and reports what is in the rooms it enters.</summary>
    Scout = 6,

    /// <summary>Holds the forward command post and grants support abilities.</summary>
    Handler = 7,
}

/// <summary>
/// An order the player may give to a squad member.
/// </summary>
/// <remarks>
/// <para>
/// The eleven the brief names, and every one of them is issued against a non-controlled
/// agent: <c>MoveTo</c>, <c>HoldPosition</c>, <c>Follow</c>, <c>Stack</c> on a connection,
/// <c>OverwatchDirection</c>, <c>UseGadget</c>, <c>HideBody</c>, <c>CarryAlly</c>,
/// <c>Regroup</c> and <c>Abort</c> to extraction, plus the post-only
/// <c>RequestExtraction</c>.
/// </para>
/// <para>
/// <b>An order is not an action.</b> An order says what a person should be doing; the
/// <c>tactical_action</c> rows say how a body spends a step doing it. That separation is
/// what lets a role behaviour be written once and reused for both the player's orders and
/// the autonomous behaviour, and it is why the role behaviour test can assert that every
/// order resolves to a legal action without either side knowing about the other.
/// </para>
/// <para>
/// The names match <c>agent_role.allowed_orders</c> exactly, and the table is the
/// authority: a role that does not list an order cannot be given it, which is how "the
/// Handler may not be told to follow somebody" is expressed as data rather than as a
/// branch.
/// </para>
/// </remarks>
public enum SquadOrderKind
{
    /// <summary>Walk to a chosen point.</summary>
    MoveTo = 0,

    /// <summary>
    /// Stand still where you are, ignoring role behaviour.
    /// </summary>
    /// <remarks>
    /// Named <c>Hold</c> rather than <c>HoldPosition</c> even though the CSV spells it
    /// the other way, because rule 10 bans position-shaped member names in Core outside
    /// the site-layout namespaces. <see cref="TryParseOrder"/> still accepts the CSV
    /// spelling, so the table reads naturally and the guard stays meaningful.
    /// </remarks>
    Hold = 1,

    /// <summary>Stay with the directly controlled agent, trailing by a standoff.</summary>
    Follow = 2,

    /// <summary>Hold one side of a named connection and cover it.</summary>
    Stack = 3,

    /// <summary>Hold this spot facing a chosen direction and report contacts.</summary>
    OverwatchDirection = 4,

    /// <summary>Use an assigned gadget.</summary>
    UseGadget = 5,

    /// <summary>Hide or drag a body out of the way.</summary>
    HideBody = 6,

    /// <summary>Pick up a downed ally and carry them.</summary>
    CarryAlly = 7,

    /// <summary>Come back to the controlled agent's position.</summary>
    Regroup = 8,

    /// <summary>Break off and head for extraction, whatever else was queued.</summary>
    Abort = 9,

    /// <summary>Ask the Handler to call the vehicle in. Post roles only.</summary>
    RequestExtraction = 10,
}

/// <summary>
/// A squad role: the table row, resolved into the vocabulary Core works in.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper rather than a use of <see cref="RoleRow"/> directly, for one reason: the
/// table stores <c>auto_behaviour_set</c> and <c>allowed_orders</c> as comma-separated
/// strings, and every call site that needed them would otherwise re-parse them and
/// re-decide what an unrecognised token means. Parsing once, here, means "a role whose
/// allowed_orders contains a name Core does not know" is a single testable condition
/// rather than a behaviour that differs between two call sites.
/// </para>
/// <para>
/// <b>The row is copied, not referenced.</b> Same reason <see cref="TacticalActor"/>
/// snapshots an agent's skills: a mission must reproduce from seed plus order log alone,
/// and a role that read through to live table data would let a designer edit change a
/// recorded run.
/// </para>
/// </remarks>
public sealed class SquadRole
{
    private SquadRole(RoleRow row)
    {
        Id = row.Id;
        NameKey = row.NameKey;
        Behaviour = BehaviourFor(row.AutoBehaviourSet);
        AllowedOrders = ParseOrders(row.AllowedOrders);
        PassiveBonusKey = row.PassiveBonus;
        PassiveBonusValue = row.PassiveBonusValue;
        RequiresCommandPost = row.RequiresCommandPost;
    }

    /// <summary>The <c>agent_role</c> id.</summary>
    public int Id { get; }

    /// <summary>Localization key for the role's name. Core never writes prose.</summary>
    public string NameKey { get; }

    /// <summary>
    /// The role's short name, as <c>objective_rule.required_roles</c> spells it.
    /// </summary>
    /// <remarks>
    /// <b>A role, not a behaviour.</b> The saboteur and the point man share the
    /// <see cref="RoleBehaviour.PointMan"/> behaviour — both walk to the next waypoint —
    /// and a PlantBug mission that asked for the pointman behaviour could be staffed by
    /// either of them. The mission needs the saboteur, who is the one who carries the
    /// device and knows the plant action. Matching on the behaviour would have made the
    /// requirement unsatisfiable in the way the table validator caught, and would have
    /// been wrong in the way it did not.
    /// </remarks>
    public string RoleKey
    {
        get
        {
            const string prefix = "role.";
            return NameKey.StartsWith(prefix, StringComparison.Ordinal)
                ? NameKey.Substring(prefix.Length)
                : NameKey;
        }
    }

    /// <summary>What a member of this role does with no order.</summary>
    public RoleBehaviour Behaviour { get; }

    /// <summary>Which orders the player may give this role, in table order.</summary>
    public IReadOnlyList<SquadOrderKind> AllowedOrders { get; }

    /// <summary>Localization key for the role's passive bonus.</summary>
    public string PassiveBonusKey { get; }

    /// <summary>The bonus's magnitude. Meaning depends on the key.</summary>
    public int PassiveBonusValue { get; }

    /// <summary>True when the role may only be filled by someone at the command post.</summary>
    public bool RequiresCommandPost { get; }

    /// <summary>True when this role may be given an order.</summary>
    public bool Allows(SquadOrderKind order) => AllowedOrders.Contains(order);

    /// <summary>Wraps a table row. Returns null for a role id that does not exist.</summary>
    public static SquadRole? For(int roleId)
    {
        RoleRow? row = SimulationRules.AgentRoleFor(roleId);
        return row is null ? null : new SquadRole(row);
    }

    /// <summary>Every role, in table order.</summary>
    public static IReadOnlyList<SquadRole> All()
    {
        IReadOnlyList<RoleRow> rows = SimulationRules.AllAgentRoles();
        var roles = new List<SquadRole>(rows.Count);

        foreach (RoleRow row in rows)
            roles.Add(new SquadRole(row));

        return roles;
    }

    /// <summary>
    /// The first role carrying a behaviour, optionally restricted to post-only roles.
    /// </summary>
    /// <remarks>
    /// Used by dispatch validation to report "this mission needs a Hacker and you have
    /// none", and by the tests' fixtures to build a squad of known roles. The restriction
    /// exists because the table has two rows per behaviour at the support end — a plain
    /// Hacker and a post Hacker — and "give me a Hacker" must not silently hand back the
    /// one that only works beside a radio.
    /// </remarks>
    public static SquadRole? FirstWith(RoleBehaviour behaviour, bool postOnly = false)
    {
        foreach (RoleRow row in SimulationRules.AllAgentRoles())
        {
            if (BehaviourFor(row.AutoBehaviourSet) != behaviour)
                continue;

            if (postOnly && !row.RequiresCommandPost)
                continue;

            if (!postOnly && row.RequiresCommandPost)
                continue;

            return new SquadRole(row);
        }

        return null;
    }

    /// <summary>
    /// Resolves a table <c>auto_behaviour_set</c> token to a behaviour.
    /// </summary>
    /// <remarks>
    /// An unknown token is <see cref="RoleBehaviour.None"/>, not an exception. A designer
    /// adding a behaviour set to the CSV should get a role that waits for orders rather
    /// than a dispatch screen that throws — and <c>TableValidator</c> fails the build on
    /// the unknown token, so the mistake is caught where it is made rather than at 3am
    /// in a mission.
    /// </remarks>
    private static RoleBehaviour BehaviourFor(string token) => token switch
    {
        "pointman" => RoleBehaviour.PointMan,
        "overwatch" => RoleBehaviour.Overwatch,
        "mule" => RoleBehaviour.Mule,
        "hacker" => RoleBehaviour.Hacker,
        "medic" => RoleBehaviour.Medic,
        "scout" => RoleBehaviour.Scout,
        "handler" => RoleBehaviour.Handler,
        _ => RoleBehaviour.None,
    };

    private static IReadOnlyList<SquadOrderKind> ParseOrders(string csv)
    {
        var orders = new List<SquadOrderKind>();

        foreach (string token in SimulationRules.Tags(csv))
        {
            if (TryParseOrder(token, out SquadOrderKind order))
                orders.Add(order);
        }

        return orders;
    }

    /// <summary>
    /// Parses one <c>allowed_orders</c> token, matching the CSV spellings exactly.
    /// </summary>
    /// <remarks>
    /// The table writes <c>OverwatchDirection</c> and <c>UseGadget</c> in one word
    /// rather than the enum's two, so the mapping is spelled out rather than a
    /// case-insensitive parse. A silent mismatch between the CSV and the enum would
    /// remove an order from a role with nothing to show for it.
    /// </remarks>
    public static bool TryParseOrder(string token, out SquadOrderKind order)
    {
        switch (token)
        {
            case "MoveTo": order = SquadOrderKind.MoveTo; return true;
            case "HoldPosition":
            case "Hold": order = SquadOrderKind.Hold; return true;
            case "Follow": order = SquadOrderKind.Follow; return true;
            case "Stack": order = SquadOrderKind.Stack; return true;
            case "OverwatchDirection": order = SquadOrderKind.OverwatchDirection; return true;
            case "UseGadget": order = SquadOrderKind.UseGadget; return true;
            case "HideBody": order = SquadOrderKind.HideBody; return true;
            case "CarryAlly": order = SquadOrderKind.CarryAlly; return true;
            case "Regroup": order = SquadOrderKind.Regroup; return true;
            case "Abort": order = SquadOrderKind.Abort; return true;
            case "RequestExtraction": order = SquadOrderKind.RequestExtraction; return true;
            default: order = default; return false;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Id} {NameKey} {Behaviour} [{string.Join(",", AllowedOrders)}]";
}