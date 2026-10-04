using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

using TableSupportAbility = ProjectSpy.Tables.SupportAbility;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// Why an order was refused before it reached the simulation.
/// </summary>
/// <remarks>
/// <para>
/// A second refusal vocabulary that sits <em>above</em>
/// <see cref="TacticalOrderReason"/>. The tactical one answers "can this body do this
/// thing right now"; this one answers "is this order something this person may be
/// given at all".
/// </para>
/// <para>
/// <b>Both are needed, and they are not interchangeable.</b> Telling a player the Point
/// Man cannot follow because he has no Follow in his role list tells them the truth but
/// not the reason; telling them he cannot follow because he is carrying somebody tells
/// them the reason and lets them fix it by dropping the body. The order-level check runs
/// first precisely because its refusals are the ones the player caused by choosing.
/// </para>
/// </remarks>
public enum SquadOrderReason
{
    /// <summary>Not a refusal.</summary>
    None = 0,

    /// <summary>Nobody with that agent id is in this squad.</summary>
    UnknownAgent = 1,

    /// <summary>The agent is the one the player is directly controlling.</summary>
    AgentIsDirectlyControlled = 2,

    /// <summary>The agent's role does not list this order.</summary>
    RoleDoesNotAllowOrder = 3,

    /// <summary>The order named a gadget the agent was not carrying.</summary>
    NoSuchGadget = 4,

    /// <summary>The agent has no uses of that gadget left.</summary>
    GadgetSpent = 5,

    /// <summary>The order named a point on a floor the target is not on.</summary>
    TargetOffFloor = 6,

    /// <summary>The whole squad is held and this member is among the held.</summary>
    SquadIsHeld = 7,

    /// <summary>The forward post is gone, so nothing may be asked of the Handler.</summary>
    CommandPostOffline = 8,

    /// <summary>The support ability is still on cooldown.</summary>
    AbilityOnCooldown = 9,

    /// <summary>The ability needs a room named and none was.</summary>
    RoomTargetRequired = 10,

    /// <summary>The ability needs a hacked door and no door has been hacked.</summary>
    HackedDoorRequired = 11,
}

/// <summary>The outcome of giving an order to a squad member.</summary>
/// <param name="Ok">Whether the order was accepted and queued.</param>
/// <param name="Reason">Why not, when it was refused.</param>
/// <param name="Args">Values for the localized template.</param>
public readonly record struct SquadOrderResult(bool Ok, SquadOrderReason Reason, IReadOnlyList<int> Args)
{
    /// <summary>The success result.</summary>
    public static SquadOrderResult Success { get; } = new(true, SquadOrderReason.None, Array.Empty<int>());

    /// <summary>Builds a refusal carrying values for the UI.</summary>
    public static SquadOrderResult Rejected(SquadOrderReason reason, params int[] args)
        => new(false, reason, args);

    /// <summary>Inverse of <see cref="Ok"/>.</summary>
    public bool IsRejected => !Ok;

    /// <summary>Localization key for this refusal.</summary>
    public string MessageKey => Ok
        ? "squad.order.ok"
        : $"squad.order.{Reason.ToString().ToLowerInvariant()}";
}

/// <summary>
/// One standing order held against a squad member, waiting for a reason to become an
/// action.
/// </summary>
/// <param name="Kind">What was asked for.</param>
/// <param name="Target">Where to go, for a movement.</param>
/// <param name="ConnectionId">Which door to stack on.</param>
/// <param name="InteractableId">Which thing to work.</param>
/// <param name="TargetActorId">Which person to carry, follow or heal.</param>
/// <param name="ItemId">Which gadget to use.</param>
/// <param name="LightId">Which light to work.</param>
/// <param name="Facing">Which way to face, for overwatch.</param>
/// <param name="IssuedOnStep">The step the player pressed the button.</param>
/// <remarks>
/// <para>
/// A record, so a queued order is a value: two orders issued on the same step are
/// distinguishable in the save without a sequence number, and the save/load round-trip
/// test can compare them for equality.
/// </para>
/// <para>
/// <b>This is not a <see cref="TacticalOrder"/>.</b> A standing order says what the
/// player wants; the tactical order says what the body is doing about it. One member can
/// hold a MoveTo for thirty steps while the actual <c>action.move_walk</c> in flight
/// changes four times — collapsing the two would make "hold that position" impossible to
/// express, because it is precisely the order with no action behind it.
/// </para>
/// </remarks>
public sealed record SquadStandingOrder(
    SquadOrderKind Kind,
    TacticalPosition Target = default,
    SiteConnectionId ConnectionId = default,
    int InteractableId = 0,
    TacticalActorId TargetActorId = default,
    int ItemId = 0,
    int LightId = 0,
    Facing Facing = Facing.Right,
    long IssuedOnStep = 0);

/// <summary>
/// The live state of one squad member's orders: who is holding, what they are about to
/// do, and why they have not done it yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held is a latch, not a toggle on the next order.</b> "Hold all" has to be
/// unambiguous in the middle of a firefight — the player presses it because something
/// just went wrong and they need everybody to stop <em>now</em>, including the person
/// halfway into a door. An order that only applies to subsequent orders would leave that
/// person walking.
/// </para>
/// <para>
/// So <see cref="Held"/> aborts the action in flight as well as blocking the next one,
/// and <see cref="Release"/> is what re-opens the member to their role behaviour.
/// </para>
/// </remarks>
public sealed class SquadMemberOrders
{
    /// <summary>The member this belongs to.</summary>
    public AgentId AgentId { get; }

    /// <summary>
    /// True when the player has stopped this member with "hold all" or a hold order.
    /// </summary>
    public bool Held { get; private set; }

    /// <summary>
    /// The standing orders waiting to be acted on, oldest first.
    /// </summary>
    /// <remarks>
    /// A queue rather than a single slot because the brief says orders queue per agent.
    /// A queue's length is also what the UI shows on the squad list, so it is visible
    /// state rather than an implementation detail.
    /// </remarks>
    public List<SquadStandingOrder> Queue { get; } = new();

    /// <summary>The order this member is currently working towards, for the UI.</summary>
    public SquadStandingOrder? Current => Queue.Count > 0 ? Queue[0] : null;

    /// <summary>How many orders are waiting. Zero means the member is on role behaviour.</summary>
    public int PendingCount => Queue.Count;

    /// <summary>
    /// The step this member last received an order, for the reaction-delay rule.
    /// </summary>
    /// <remarks>
    /// <see cref="SimulationRules.Squad"/>'s <c>squad_role_reaction_steps</c> is spent
    /// against this: a role behaviour does not react on the same step its member's
    /// situation changed, which is what stops a five-person squad from pivoting in
    /// lockstep like a single unit controlled by a machine.
    /// </remarks>
    public long LastOrderStep { get; set; }

    /// <summary>Creates the order state for one member.</summary>
    public SquadMemberOrders(AgentId agentId) => AgentId = agentId;

    /// <summary>Holds this member and clears what they were about to do.</summary>
    public void Hold()
    {
        Held = true;
        Queue.Clear();
    }

    /// <summary>Lets this member act on their role behaviour again.</summary>
    public void Release() => Held = false;

    /// <summary>
    /// Throws away whatever this member was told to do.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Hold"/>, which keeps the history: holding a member who
    /// had three orders queued leaves the queue intact so that resuming picks up where
    /// they left off, and an abort needs the opposite — the old orders are not wanted
    /// any more, and leaving them behind the new one would mean waiting out the queue.
    /// </remarks>
    public void ClearQueue() => Queue.Clear();

    /// <summary>
    /// Takes this member off hold, for an order that was issued while the squad was held.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Release"/> so that "resume all" is one player's action
    /// and "you gave this person an order, so they are doing it" is the system's. A
    /// member who is held but has been given a MoveTo is not a contradiction to resolve
    /// in the UI; they are simply doing what they were told.
    /// </remarks>
    public void ClearHold() => Held = false;

    /// <inheritdoc/>
    public override string ToString()
        => Held ? $"{AgentId} held" : $"{AgentId} {Queue.Count} queued";
}

/// <summary>
/// How many agents are being driven directly, and what the rest of the squad is doing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one at a time, always.</b> Not "zero or one": zero would mean the player
/// has nobody, which is not a state the brief allows and which would leave the mission
/// running itself with a controlled-agent UI that expects an answer. The controller
/// starts with nobody and the mission is not playable until the player picks, so the
/// invariant is enforced on the <em>first</em> switch rather than seeded with a guess
/// about who should lead.
/// </para>
/// <para>
/// <b>Orders are accepted while paused.</b> The brief calls pausing and issuing orders a
/// core part of how the game is played, and it works because this type holds orders in a
/// queue the step pipeline drains — not because the simulation is stopped. A paused
/// mission's <see cref="TacticalState"/> is still a valid object and still validates
/// orders, so the UI can grey out an illegal order correctly at pause time rather than
/// discovering it was illegal three steps later.
/// </para>
/// <para>
/// <b>Swapping control does not swap orders.</b> Whoever was controlled keeps their
/// standing orders and resumes their role behaviour; the new controlled agent keeps theirs
/// too, they simply stop acting on them for as long as they are being driven. Otherwise
/// taking the wheel of the Hacker would silently hand the Point Man back to his own
/// orders, which is a surprising thing for a player to have to undo.
/// </para>
/// </remarks>
public sealed class SquadControl
{
    private readonly Dictionary<AgentId, SquadMemberOrders> _byAgent = new();

    /// <summary>Every member's order state, in the order the squad was composed.</summary>
    public List<SquadMemberOrders> Members { get; } = new();

    /// <summary>
    /// True when "hold all" is in force across the squad.
    /// </summary>
    /// <remarks>
    /// Separate from each member's own <see cref="SquadMemberOrders.Held"/> because the
    /// player needs to be able to see, at a glance, that the whole team is frozen. If
    /// "hold all" were only a set of five holds, the squad panel would show five
    /// individually-fine members and nothing indicating they were all stopped.
    /// </remarks>
    public bool AllHeld { get; private set; }

    /// <summary>
    /// The one agent the player is driving, or null before the first switch.
    /// </summary>
    public AgentId? Controlled { get; private set; }

    /// <summary>The tactical actor currently under direct control.</summary>
    public TacticalActor? ControlledActor(TacticalState state)
        => Controlled is { } id ? state.AgentActor(id) : null;

    /// <summary>
    /// Registers a squad, discarding any previous order state.
    /// </summary>
    /// <remarks>
    /// Called at mission start. Rebuilding rather than merging is deliberate: the orders
    /// in a queue belonged to a mission that is now over, and carrying them into the next
    /// one would let a player leave an agent mid-mission on a different site.
    /// </remarks>
    public void Begin(SquadComposition composition)
    {
        if (composition is null) throw new ArgumentNullException(nameof(composition));

        _byAgent.Clear();
        Members.Clear();
        AllHeld = false;
        Controlled = null;

        foreach (SquadMember member in composition.Members)
        {
            var orders = new SquadMemberOrders(member.AgentId);
            _byAgent[member.AgentId] = orders;
            Members.Add(orders);
        }
    }

    /// <summary>Order state for an agent, or null when they are not in this squad.</summary>
    public SquadMemberOrders? For(AgentId agentId)
        => _byAgent.TryGetValue(agentId, out SquadMemberOrders? orders) ? orders : null;

    /// <summary>
    /// Takes direct control of an agent, releasing the previous one.
    /// </summary>
    /// <returns>False when the agent is not in this squad.</returns>
    public bool SwitchTo(AgentId agentId)
    {
        if (!_byAgent.ContainsKey(agentId))
            return false;

        Controlled = agentId;
        return true;
    }

    /// <summary>
    /// Gives the whole squad a standing order, one member at a time.
    /// </summary>
    /// <remarks>
    /// The controller is not the right place to interpret what a MoveTo means — only
    /// <see cref="RoleBehaviours"/> knows how to turn an intent into a legal action. What
    /// this does is everything that is about <em>the order</em> rather than the action:
    /// that the target exists, that the role may be given this at all, that the gadget
    /// exists and has uses left, and that the player is not asking a frozen squad to
    /// move.
    /// </remarks>
    public SquadOrderResult Issue(SquadComposition composition, AgentId agentId, SquadStandingOrder order)
    {
        SquadMemberOrders? state = For(agentId);

        if (state is null)
            return SquadOrderResult.Rejected(SquadOrderReason.UnknownAgent, agentId.Value);

        if (Controlled == agentId)
        {
            return SquadOrderResult.Rejected(
                SquadOrderReason.AgentIsDirectlyControlled, agentId.Value);
        }

        SquadMember? member = composition?.MemberFor(agentId);

        if (member?.Role is null)
            return SquadOrderResult.Rejected(SquadOrderReason.UnknownAgent, agentId.Value);

        if (!member.Role.Allows(order.Kind))
        {
            return SquadOrderResult.Rejected(
                SquadOrderReason.RoleDoesNotAllowOrder, (int)order.Kind);
        }

        SquadOrderResult gadgetCheck = CheckGadget(member, order);
        if (gadgetCheck.IsRejected)
            return gadgetCheck;

        state.Queue.Add(order with { IssuedOnStep = LastStep });
        state.ClearHold();
        return SquadOrderResult.Success;
    }

    /// <summary>
    /// Breaks off: sends every free member to the extraction point.
    /// </summary>
    /// <returns>
    /// False when the abort is still on cooldown. The player has a button and the
    /// command has a cost in steps, and a command with no cost would be pressed every
    /// time somebody got nervous — which would make the firefight a decision to reset
    /// rather than a decision to survive, and would make the cooldown a number nobody
    /// ever reached.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The controlled agent is not sent.</b> Aborting is a decision the player makes
    /// about the mission, and the character under their hand is the one they are already
    /// driving to the exit. Telling it to do what they are already doing would replace
    /// their own movement order with the pathfinder's.
    /// </para>
    /// <para>
    /// <b>Aborting does not end the mission.</b> It sets a flag and sends people walking;
    /// reaching the extraction still takes the number of steps the building says it
    /// takes. The mission ends when the objective check sees everybody out, which is what
    /// makes an abort cost what the brief says it costs.
    /// </para>
    /// </remarks>
    public bool AbortAll(
        TacticalState state,
        SquadComposition composition,
        CommandPostState post,
        IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (post is null) throw new ArgumentNullException(nameof(post));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        int cooldown = SimulationRules.Squad(
            "squad_abort_order_cooldown_steps", DefaultAbortCooldownSteps);

        // Measured from the last abort rather than from mission start, so a player who
        // aborts, changes their mind, and aborts again pays twice.
        if (state.Step - state.LastAbortStep < cooldown && state.AbortCalled)
            return false;

        state.AbortCalled = true;
        state.LastAbortStep = state.Step;

        foreach (SquadMemberOrders orders in Members)
        {
            if (Controlled == orders.AgentId)
                continue;

            // Whatever else was queued, it does not matter any more. An abort that
            // queued behind a "wait here" order would be a button that does nothing for
            // as long as the player forgets to cancel.
            orders.ClearQueue();
            orders.ClearHold();
            orders.Queue.Add(new SquadStandingOrder(
                SquadOrderKind.Abort, IssuedOnStep: state.Step));
        }

        return true;
    }

    /// <summary>
    /// Stops every non-controlled agent where they stand.
    /// </summary>
    /// <remarks>
    /// The directly controlled agent is deliberately not stopped: "hold all" is about
    /// the squad the player is <em>not</em> driving, and freezing the character under
    /// the player's hand would make the command useless for its actual purpose, which is
    /// stopping the three people currently running off on their own.
    /// </remarks>
    public void HoldAll()
    {
        AllHeld = true;

        foreach (SquadMemberOrders orders in Members)
            orders.Hold();
    }

    /// <summary>Releases the whole squad back to its role behaviours.</summary>
    public void ResumeAll()
    {
        AllHeld = false;

        foreach (SquadMemberOrders orders in Members)
            orders.Release();
    }

    /// <summary>
    /// The step orders are currently being stamped with.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="Run"/> each step, and left at the last stepped value while the
    /// game is paused. That is what lets an order issued during a pause carry the step it
    /// was issued on, so replaying it reproduces the pause rather than pretending the
    /// order arrived at the moment unpause was pressed.
    /// </remarks>
    public long LastStep { get; set; }

    /// <summary>
    /// Gives every free member whose order is ready an action.
    /// </summary>
    /// <returns>How many orders were started this step.</returns>
    public int Run(
        TacticalState state,
        SquadComposition composition,
        IRng rng,
        CommandPostState post)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        LastStep = state.Step;

        int started = 0;

        foreach (TacticalActor actor in state.SortedActors)
        {
            if (!actor.IsAgent || !actor.CanAct)
                continue;

            SquadMemberOrders? orders = For(actor.AgentId);
            if (orders is null)
                continue;

            // Mid-action members are left alone, exactly as the GOAP executor leaves its
            // own NPCs alone. Re-deciding every step would mean no order ever finishes:
            // the second step of a walk would be replaced by a fresh plan to walk.
            if (actor.Action is { IsComplete: false })
                continue;

            if (Controlled == actor.AgentId)
            {
                // Being driven by hand does not discard standing orders; it only stops
                // them being acted on.
                continue;
            }

            if (orders.Held)
            {
                // Re-arm the stop each step rather than only on the step it was issued,
                // so a member whose action completed during the hold does not drift back
                // into role behaviour on the next step.
                IssueStop(state, actor, rng);
                continue;
            }

            if (orders.Queue.Count > 0)
            {
                if (StartStandingOrder(state, composition, actor, orders, rng, post))
                    started++;

                continue;
            }

            if (RoleBehaviours.TryStart(state, composition, actor, rng))
                started++;
        }

        return started;
    }

    /// <summary>
    /// Turns the head of a member's queue into an action, or drops it.
    /// </summary>
    /// <remarks>
    /// A dropped order is not a failure: "move to that room" is a standing intent that
    /// stops being legal the moment the route closes or the actor runs out of stamina,
    /// and keeping it at the head of the queue forever would wedge the queue behind an
    /// impossible order. Popping it lets the next thing the player asked for happen,
    /// which is what a player would expect from a list of intentions.
    /// </remarks>
    private bool StartStandingOrder(
        TacticalState state,
        SquadComposition composition,
        TacticalActor actor,
        SquadMemberOrders orders,
        IRng rng,
        CommandPostState post)
    {
        SquadStandingOrder order = orders.Queue[0];

        // Give the member a beat before reacting to what they were just told. Without it
        // a five-person squad turns as one body, which both looks wrong and lets a
        // player chain reactions into a single-step compound move.
        if (state.Step - orders.LastOrderStep < SimulationRules.Squad(
                "squad_role_reaction_steps", DefaultReactionSteps))
        {
            return false;
        }

        orders.LastOrderStep = state.Step;

        TacticalOrder? action = RoleBehaviours.TacticalOrderFor(
            state, composition, actor, order, post);

        if (action is null || ActionSystem.Validate(state, action).IsRejected)
        {
            orders.Queue.RemoveAt(0);
            return false;
        }

        if (!ActionSystem.TryBegin(state, action, rng, state.Step, out _))
        {
            orders.Queue.RemoveAt(0);
            return false;
        }

        orders.Queue.RemoveAt(0);
        return true;
    }

    /// <summary>Puts a member on the stop order so their behaviour cannot resume.</summary>
    /// <remarks>
    /// The stop order costs nothing and rolls nothing, so the rng it is handed is
    /// irrelevant to the outcome — it is passed rather than constructed so that a
    /// deterministic run's stream is not perturbed by a stop, and so that if a stop ever
    /// did gain a roll it would draw from the tactical stream like everything else.
    /// </remarks>
    private static void IssueStop(TacticalState state, TacticalActor actor, IRng rng)
    {
        if (actor.Action is { IsComplete: false })
            return;

        var stop = new TacticalOrder(actor.Id, TacticalOrder.StopActionId);

        if (ActionSystem.Validate(state, stop).IsRejected)
            return;

        ActionSystem.TryBegin(state, stop, rng, state.Step, out _);
    }

    /// <summary>
    /// Checks that a UseGadget order names something the agent is carrying.
    /// </summary>
    private static SquadOrderResult CheckGadget(SquadMember member, SquadStandingOrder order)
    {
        if (order.Kind != SquadOrderKind.UseGadget || order.ItemId == 0)
            return SquadOrderResult.Success;

        if (!member.Gadgets.Has(order.ItemId))
            return SquadOrderResult.Rejected(SquadOrderReason.NoSuchGadget, order.ItemId);

        if (member.Gadgets.UsesOf(order.ItemId) <= 0)
            return SquadOrderResult.Rejected(SquadOrderReason.GadgetSpent, order.ItemId);

        return SquadOrderResult.Success;
    }

    /// <summary>Steps a member waits between taking an order and acting on it.</summary>
    private const int DefaultReactionSteps = 10;

    /// <summary>Steps between two aborts, absent the table.</summary>
    private const int DefaultAbortCooldownSteps = 100;

    /// <inheritdoc/>
    public override string ToString()
        => $"controlled {Controlled?.ToString() ?? "none"}{(AllHeld ? " (all held)" : string.Empty)}";
}