using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Where something stands in the building: a floor, and a point on that floor's line.
/// </summary>
/// <remarks>
/// <para>
/// The one shape knowledge.md rule 10 explicitly permits inside Core, and the whole
/// tactical space model rests on it (rule 15). A cutaway building is one-dimensional
/// per floor, so a position is two integers and nothing else: which floor, and how far
/// along it. There is no depth, no height and no screen direction, because none of
/// those change a rule.
/// </para>
/// <para>
/// <b>Why centimetres.</b> <see cref="X"/> is a <see cref="Fixed32"/>, so 1 unit is
/// 1 cm. Vision ranges, noise radii, light radii and room spans are all interval maths
/// over the same integer, and a perception check becomes an integer comparison whose
/// inputs the UI can print exactly — which is the only reason the perception breakdown
/// can exist at all (knowledge.md rule 4: Core hands back numbers, not sentences).
/// </para>
/// <para>
/// A position is only meaningful together with the room it is in, and the room is
/// recoverable from it: <see cref="SiteLayout.RoomContaining"/> maps a point back to
/// its half-open <c>[StartX, EndX)</c> interval. Movement therefore never stores a room
/// id alongside the position, because two sources of truth about where something is
/// would eventually disagree.
/// </para>
/// </remarks>
public readonly record struct TacticalPosition(int FloorIndex, Fixed32 X) : IComparable<TacticalPosition>
{
    /// <summary>
    /// The absence of a position. Deliberately not a room's edge: a sentinel that is
    /// merely "somewhere" can be half-remembered and compared, and one that is out of
    /// range always fails a containment test.
    /// </summary>
    public static readonly TacticalPosition None = new(-1, new Fixed32(-1));

    /// <summary>True for a real position on a real floor.</summary>
    public bool IsValid => FloorIndex >= 0 && !X.IsNegative;

    /// <summary>True when both positions sit on the same floor.</summary>
    public bool IsOnSameFloorAs(TacticalPosition other) => FloorIndex == other.FloorIndex;

    /// <summary>
    /// Distance along the line between two positions.
    /// </summary>
    /// <remarks>
    /// Cross-floor distance has no single answer on a 1-D-per-floor model — a stair
    /// lands wherever it lands — so this returns <see cref="Fixed32"/> only for
    /// same-floor pairs and callers must route cross-floor distance through
    /// <see cref="Pathfinder"/>, which knows about connections. Silently summing
    /// floors here would be the kind of plausible-looking approximation that makes a
    /// noise radius wrong on a building with a basement.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The two positions are on different floors.</exception>
    public Fixed32 DistanceAlongFloorTo(TacticalPosition other)
    {
        if (!IsOnSameFloorAs(other))
        {
            throw new InvalidOperationException(
                $"Distance along a floor was asked for between floor {FloorIndex} and floor {other.FloorIndex}. " +
                "Use the pathfinder, which routes through connections.");
        }

        return Fixed32.Distance(X, other.X);
    }

    /// <summary>
    /// Orders positions by floor, then by distance along the floor.
    /// </summary>
    /// <remarks>
    /// Total and stable, which is what lets a <c>SortedSet</c> of positions behave
    /// identically on two runs. Two distinct positions never compare equal here unless
    /// they are the same point, so ordering never hides a duplicate.
    /// </remarks>
    public int CompareTo(TacticalPosition other)
    {
        int byFloor = FloorIndex.CompareTo(other.FloorIndex);
        return byFloor != 0 ? byFloor : X.Raw.CompareTo(other.X.Raw);
    }

    /// <summary>Stable, culture-invariant text form.</summary>
    public override string ToString() => $"F{FloorIndex}@{X.Raw}cm";
}

/// <summary>Which way an entity is looking along its floor.</summary>
/// <remarks>
/// One bit, not a heading. The building is a cutaway, so an entity faces left or right
/// along one line and there is no third option — a 2-D direction would be a coordinate
/// Core does not have. Perception reads this for the cone test, which is why it is
/// state rather than a derived value: a guard that turns is a different guard.
/// </remarks>
public enum Facing
{
    /// <summary>Towards decreasing <c>X</c> — the left-hand end of the floor.</summary>
    Left = 0,

    /// <summary>Towards increasing <c>X</c> — the right-hand end of the floor.</summary>
    Right = 1,
}

/// <summary>
/// How an entity is holding itself: the posture table of the tactical simulation.
/// </summary>
/// <remarks>
/// <para>
/// Each posture trades three numbers against each other — how fast it covers ground,
/// how loud it is, and how much of it a guard can make out — and every stealth decision
/// in the game is a choice along that trade-off rather than a single "stealthy" stat.
/// The ordering is deliberate: <see cref="Prone"/> is the slowest and quietest and the
/// ordinal values increase monotonically with speed, so a comparison like
/// <c>posture &gt;= Walk</c> answers "is this entity moving conspicuously" without a
/// table.
/// </para>
/// <para>
/// The multipliers themselves are structural rather than tuned: they define what the
/// posture <em>means</em> on a one-dimensional cutaway, where a prone body really is
/// hard to see at the bottom of a corridor and a sprint really does carry. The
/// designer-tunable parts — how many steps a step of each posture costs and what noise
/// each makes — live in <c>tactical_action</c> and <c>noise_profile</c> and are read
/// from there (knowledge.md rule 3).
/// </para>
/// </remarks>
public enum Posture
{
    /// <summary>Flat on the floor. Slowest, quietest, hardest to see.</summary>
    Prone = 0,

    /// <summary>Low and slow. The default posture for someone moving carefully.</summary>
    Crouch = 1,

    /// <summary>Ordinary walking pace.</summary>
    Walk = 2,

    /// <summary>Sprinting. Fastest, loudest, most visible.</summary>
    Run = 3,
}

/// <summary>
/// What an entity in the mission is: the player's squad, the site's guards, or its
/// civilians.
/// </summary>
/// <remarks>
/// Three kinds because the three populations differ in what can happen to them, not
/// because they are stored differently. Guards can raise the alarm and can be killed
/// without consequence to the player's roster; agents carry everything the mission
/// cares about; civilians are neither targets nor squad. Collapsing them into one class
/// with a flag would push those three "only guards can" checks into the callers, and
/// they would be missed.
/// </remarks>
public enum TacticalActorKind
{
    /// <summary>A member of the player's squad. Bound to a strategic <see cref="AgentId"/>.</summary>
    Agent = 0,

    /// <summary>Site security. Bound to a <see cref="Missions.SiteGuardId"/>.</summary>
    Guard = 1,

    /// <summary>A member of the public. Bound to a <see cref="Missions.SiteCivilianId"/>.</summary>
    Civilian = 2,
}

/// <summary>Whether an entity can act, and what is left of it.</summary>
/// <remarks>
/// Ordered by how alive it is, so <c>&lt; <see cref="Condition.Downed"/></c> answers
/// "can this thing still act" without a switch. <see cref="Captured"/> sits above
/// <see cref="Dead"/> rather than below it because capture is a different outcome with
/// different consequences — a captured agent is a rescue mission later, a dead one is
/// a roster line — and code that only wanted "is this agent still available" must not
/// be able to catch both with one comparison.
/// </remarks>
public enum ActorCondition
{
    /// <summary>Gone for good. Permanent, and never reversed by any later event.</summary>
    Dead = 0,

    /// <summary>Alive, in enemy hands. Ended for this mission, not for the agent.</summary>
    Captured = 1,

    /// <summary>Unconscious and bleeding out. Recoverable, on a timer.</summary>
    Downed = 2,

    /// <summary>Carried or dragged by somebody else.</summary>
    Carried = 3,

    /// <summary>Up and acting on their own orders.</summary>
    Active = 4,
}

/// <summary>What an actor can be asked to do next.</summary>
/// <remarks>
/// An action that has not finished yet is the same action, not a different one, so this
/// is a member of the pending record rather than a state of the actor. That is what
/// makes an interrupted action resumable: the steps already spent stay spent, and the
/// action continues from where it was.
/// </remarks>
public enum TacticalActionKind
{
    /// <summary>Walk or run a number of centimetres towards a point on the current floor.</summary>
    Move = 1,

    /// <summary>Cross a connection into the next room.</summary>
    Traverse = 2,

    /// <summary>Open, close, lock, force or pick a connection.</summary>
    OperateConnection = 3,

    /// <summary>Work an interactable: hack, search, plant, or exfiltrate.</summary>
    UseInteractable = 4,

    /// <summary>Strike, threaten or shoot somebody.</summary>
    Attack = 5,

    /// <summary>Throw something at a range.</summary>
    Throw = 6,

    /// <summary>Heal, revive, carry, signal or simply hold.</summary>
    Support = 7,

    /// <summary>Hide a body, drag a body, put on a disguise, switch or break a light.</summary>
    Stealth = 8,
}

/// <summary>How a door or other connection currently stands.</summary>
/// <remarks>
/// A real state on every connection rather than a boolean on the ones that matter,
/// because the brief treats blocked, locked and barricaded as three different problems
/// with three different answers: a locked door is a skill check, a barricade is
/// something somebody put there, and a blocked connection is scenery that will not open
/// at all. A single <c>IsLocked</c> flag cannot tell "pick it" from "no" and forces the
/// caller to guess which action is offered.
/// </remarks>
public enum ConnectionState
{
    /// <summary>Passable straight through.</summary>
    Open = 0,

    /// <summary>Shut, but openable. Blocks sight and sound until opened.</summary>
    Closed = 1,

    /// <summary>Shut and locked. Needs a skill check, a key, or force.</summary>
    Locked = 2,

    /// <summary>Deliberately obstructed. Must be cleared before anyone passes.</summary>
    Barricaded = 3,

    /// <summary>Impassable. Not a door: a collapse, a sealed wall.</summary>
    Blocked = 4,
}

/// <summary>What one observer managed to make out of one target.</summary>
/// <remarks>
/// Three levels, and the gap between them is the whole stealth game: <see cref="None"/>
/// is invisible, <see cref="Noticed"/> is "something is there", and
/// <see cref="Identified"/> is "that is the infiltrator". A guard that jumps straight
/// from <see cref="None"/> to <see cref="Identified"/> would make a stealth mission
/// unwinnable by accident, which is why the middle level is a state rather than a step.
/// </remarks>
public enum PerceptionLevel
{
    /// <summary>Nothing registered at all.</summary>
    None = 0,

    /// <summary>A shape. Enough to raise suspicion, not enough to act on.</summary>
    Noticed = 1,

    /// <summary>Known for what it is. Enough to pursue, raise the alarm, or attack.</summary>
    Identified = 2,
}

/// <summary>
/// Why a perception came out the way it did, when it came out as nothing.
/// </summary>
/// <remarks>
/// Carried on the result rather than inferred by the caller, because "the guard did not
/// see me" is a question the player asks constantly and the honest answer is usually a
/// specific one — you were in shadow, or you were behind them, or something was in the
/// way. A single <c>None</c> would make the perception breakdown a list of zeroes, which
/// teaches the player nothing.
/// </remarks>
public enum PerceptionBlocker
{
    /// <summary>Not blocked. Something was perceived, at some level.</summary>
    None = 0,

    /// <summary>The observer's range fell short of the distance.</summary>
    OutOfRange = 1,

    /// <summary>The target was behind the observer and outside the cone.</summary>
    OutsideCone = 2,

    /// <summary>Furniture or a shut door stood in the way.</summary>
    Occluded = 3,

    /// <summary>The target was in the dark and the observer could not resolve a shape.</summary>
    Unlit = 4,

    /// <summary>The target was disguised as something the observer does not challenge.</summary>
    Disguised = 5,
}

/// <summary>The site-wide alarm, in the five bands the design document names.</summary>
/// <remarks>
/// <para>
/// Ordered, and the boundaries are the design document's: 0-25 calm, 26-50 suspicious,
/// 51-75 alert, 76-99 lockdown, 100 burned. The order is what
/// <see cref="AlarmState.Band"/> is, so a band can be compared and clamped rather than
/// switched over a five-way case.
/// </para>
/// <para>
/// <see cref="Burned"/> is not "one more than lockdown". It ends the mission: anyone
/// not at an extraction point when the site reaches it is at risk, which is a different
/// kind of state from "the guards are shooting" and needs its own name to be visible.
/// </para>
/// </remarks>
public enum AlarmBand
{
    /// <summary>0-25. Normal patrols, nothing unusual.</summary>
    Calm = 0,

    /// <summary>26-50. Extra patrols, difficulty checks up by ten.</summary>
    Suspicious = 1,

    /// <summary>51-75. Doors shut behind the team, routes lost, guards pursue.</summary>
    Alert = 2,

    /// <summary>76-99. Some extractions close; the team has to fight out.</summary>
    Lockdown = 3,

    /// <summary>100. The mission is over whether the team is ready or not.</summary>
    Burned = 4,
}

/// <summary>
/// A noise made somewhere, and the step it was made on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Origin"/> is a <see cref="TacticalPosition"/> and
/// <see cref="Radius"/> is the distance the sound reaches before any attenuation, from
/// <c>noise_profile.base_radius_cm</c>. Everything after that — how far it actually
/// travels, who hears it — belongs to <see cref="NoiseSystem"/>, which needs the
/// building to answer and therefore cannot be a property of the event.
/// </para>
/// <para>
/// <see cref="ProfileId"/> travels with the event rather than being flattened into the
/// radius on creation, because the profile also carries the per-connection and
/// per-floor attenuation and a suspicion weight, and a noise that has already crossed
/// two doors still needs to cross the third.
/// </para>
/// <para>
/// <see cref="Step"/> is on the event rather than read from the clock, for the reason
/// the state hash cares about: a noise in flight is a fact about the past that the
/// mission log reports, and reading "now" for it would rewrite history every step.
/// </para>
/// </remarks>
public sealed record NoiseEvent(
    TacticalPosition Origin,
    Fixed32 Radius,
    int ProfileId,
    long Step)
{
    /// <summary>What made the noise. Entity id, or zero for a world event.</summary>
    public int SourceActorId { get; init; }

    /// <summary>Localization key for the source, e.g. <c>action.combat_melee</c>.</summary>
    public string SourceKey { get; init; } = string.Empty;

    /// <summary>Sounds heard by each actor, filled in as propagation runs.</summary>
    public List<NoiseHeard> HeardBy { get; } = new();

    /// <inheritdoc/>
    public override string ToString()
        => $"noise {ProfileId} r={Radius.Raw}cm f{FloorIndexOfOrigin} @{Origin.X.Raw}cm step {Step}";

    private int FloorIndexOfOrigin => Origin.FloorIndex;
}

/// <summary>
/// One actor hearing one noise, and how loudly.
/// </summary>
/// <param name="ActorId">Who heard it.</param>
/// <param name="IntensityPercent">
/// How loud it arrived, as a percentage of the profile's base radius. 100 means nothing
/// stood between the noise and the listener.
/// </param>
/// <param name="DistanceCm">The distance the sound had travelled to reach them.</param>
public readonly record struct NoiseHeard(int ActorId, int IntensityPercent, Fixed32 DistanceCm);

/// <summary>
/// One room an operative walked into, and when.
/// </summary>
/// <remarks>
/// <para>
/// Room-to-room rather than per-centimetre positions, because that is what a floor
/// diagram can honestly draw: a line between room centres, through the doors that were
/// actually used. Sampling every actor's exact position would produce a scribble that
/// implies a precision the debrief does not have, and would cost a point per actor per
/// step for a picture nobody reads at that resolution.
/// </para>
/// <para>
/// Recorded in Core rather than sampled by the view. A route sampled from the renderer
/// would depend on frame rate, so the same mission replayed at a different frame rate
/// would produce a different debrief — and a debrief that changes between two runs of the
/// same seed is not evidence of anything.
/// </para>
/// </remarks>
public readonly record struct RouteStep(long Step, int ActorId, AgentId Agent, SiteRoomId RoomId);

/// <summary>Stable identifier for an actor inside a running mission.</summary>
/// <remarks>
/// One sequence for the whole mission rather than one per kind, because an id is looked
/// up in a single flat table of actors and three overlapping sequences would make
/// "which guard is 3" ambiguous at every call site. Allocation is in ascending order:
/// squad first, then guards, then civilians, and the order is never reused.
/// </remarks>
public readonly record struct TacticalActorId(int Value) : IComparable<TacticalActorId>
{
    /// <summary>The absence of an actor.</summary>
    public static readonly TacticalActorId None = new(0);

    /// <summary>True for a real actor id.</summary>
    public bool IsValid => Value > 0;

    /// <inheritdoc/>
    public int CompareTo(TacticalActorId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public override string ToString() => $"E{Value:D3}";
}
