using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// An order handed to the simulation, waiting for the top of the next step.
/// </summary>
/// <remarks>
/// <para>
/// Rule 14 says input is sampled only at step boundaries and converted into commands on
/// the command log. This is that command, expressed for the tactical layer: a record of
/// an intent, validated before it is applied, and applied at a point in the pipeline
/// whose position is fixed.
/// </para>
/// <para>
/// <b>Separate from <c>ICommand</c>, and deliberately.</b> A strategic command mutates a
/// <see cref="WorldState"/> and is validated against the whole world; a tactical order
/// mutates a running mission that the strategic world only holds a reference to, and is
/// validated against that mission and nothing else. Forcing them through one interface
/// would mean every tactical order reached through <c>World.ActiveMission</c> and threw
/// <c>NullReferenceException</c> when validating outside a mission — which is exactly the
/// state a UI spends most of its time in, when the player is planning the next mission.
/// The contract that matters — validate is side-effect free, nothing mutates except
/// through an applied order, and every applied order is logged — is preserved here.
/// </para>
/// <para>
/// <b>Records, not classes.</b> They go into <see cref="TacticalState.OrderLog"/>, which
/// a replay re-executes; a class with a mutable field would let a logged order differ
/// from the one that was actually applied.
/// </para>
/// </remarks>
/// <param name="ActorId">Who the order is for.</param>
/// <param name="ActionId">The <c>tactical_action</c> row being ordered.</param>
/// <param name="ConnectionId">The connection involved, or none.</param>
/// <param name="InteractableId">The interactable involved, or zero.</param>
/// <param name="LightId">The light involved, or zero.</param>
/// <param name="TargetActorId">The other actor involved, or none.</param>
/// <param name="ItemId">The weapon or throwable used, or zero.</param>
/// <param name="Target">Where to move to, when the order is a movement.</param>
public sealed record TacticalOrder(
    TacticalActorId ActorId,
    int ActionId,
    SiteConnectionId ConnectionId = default,
    int InteractableId = 0,
    int LightId = 0,
    TacticalActorId TargetActorId = default,
    int ItemId = 0,
    TacticalPosition Target = default)
{
    /// <summary>
    /// An order to stop whatever is in flight and do nothing until told otherwise.
    /// </summary>
    /// <remarks>
    /// Not a <c>tactical_action</c> row, because there is no cost to stopping and no row
    /// that says so — and an order id of zero is the honest encoding of "there is no
    /// action here", rather than borrowing <c>action.support_wait</c> and pretending
    /// that standing still and ceasing to act are the same request.
    /// </remarks>
    public const int StopActionId = 0;

    /// <summary>True when this order asks the actor to stop.</summary>
    public bool IsStop => ActionId == StopActionId;
}

/// <summary>An order that passed validation and was applied.</summary>
/// <remarks>
/// The replay log entry. It carries the order verbatim plus the step it landed on, because
/// a replay has to know <em>when</em> as well as <em>what</em>: the same order applied a
/// step earlier sees a different world, and a log that recorded only the orders would
/// replay a mission with every actor a hundred milliseconds ahead of where they were.
/// </remarks>
/// <param name="Step">The step whose <c>ApplyQueuedCommands</c> phase applied it.</param>
/// <param name="Order">The order, verbatim.</param>
public sealed record AcceptedOrder(long Step, TacticalOrder Order);

/// <summary>
/// Why a tactical order was refused.
/// </summary>
/// <remarks>
/// A reason code and structured values, never a sentence — the same contract strategic
/// commands have under rule 4, and for the same reason: the tactical HUD needs to say
/// "you cannot open that, it is locked" in whichever language the player chose, and
/// Core is not the place that knows which.
/// </remarks>
public enum TacticalOrderReason
{
    /// <summary>Not a refusal.</summary>
    None = 0,

    /// <summary>No actor with that id is in this mission.</summary>
    UnknownActor = 1,

    /// <summary>No <c>tactical_action</c> row carries that id.</summary>
    UnknownAction = 2,

    /// <summary>The actor is dead, captured, unconscious or already being carried.</summary>
    ActorCannotAct = 3,

    /// <summary>The actor does not have the stamina the action costs.</summary>
    NotEnoughStamina = 4,

    /// <summary>The connection is locked, barricaded or blocked.</summary>
    ConnectionBlocked = 5,

    /// <summary>The connection is too small for this kind of actor to use.</summary>
    ConnectionUnusable = 6,

    /// <summary>The named interactable or light is not in this mission.</summary>
    UnknownTarget = 7,

    /// <summary>The named target is out of reach.</summary>
    OutOfReach = 8,

    /// <summary>The named target is not there any more.</summary>
    TargetGone = 9,

    /// <summary>The actor is already doing something that cannot be interrupted.</summary>
    BusyWithAnotherAction = 10,

    /// <summary>The action needs a skill the actor has not got, or a roll failed.</summary>
    SkillCheckFailed = 11,

    /// <summary>The action cannot be done while downed or carrying somebody.</summary>
    ImpossibleInCurrentState = 12,

    /// <summary>There is nothing left to use.</summary>
    OutOfUses = 13,
}

/// <summary>The outcome of validating a tactical order.</summary>
/// <remarks>
/// Deliberately the same shape as the strategic <see cref="CommandResult"/>: a bool, a
/// reason code and integer arguments for the UI to interpolate. A tactical HUD and a
/// base screen then share one way of saying "that did not work", and neither has to
/// parse the other's reasons.
/// </remarks>
public readonly record struct TacticalOrderResult(bool Ok, TacticalOrderReason Reason, IReadOnlyList<int> Args)
{
    /// <summary>The success result.</summary>
    public static TacticalOrderResult Success { get; } = new(true, TacticalOrderReason.None, Array.Empty<int>());

    /// <summary>Builds a refusal with no arguments.</summary>
    public static TacticalOrderResult Rejected(TacticalOrderReason reason)
        => new(false, reason, Array.Empty<int>());

    /// <summary>Builds a refusal carrying values for the UI to interpolate.</summary>
    public static TacticalOrderResult Rejected(TacticalOrderReason reason, params int[] args)
        => new(false, reason, args);

    /// <summary>True when the order was refused.</summary>
    public bool IsRejected => !Ok;

    /// <summary>Introspection key the UI looks up. A key, not prose.</summary>
    public string MessageKey => Ok
        ? "tactical.order.ok"
        : $"tactical.order.{Reason.ToString().ToLowerInvariant()}";
}
