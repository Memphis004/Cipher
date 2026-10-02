namespace ProjectSpy.Core;

/// <summary>
/// Stable identifier for an interactable inside a mission node.
/// </summary>
/// <remarks>
/// Ids are assigned per room and are stable for the life of that room, so a
/// replay, a save, and the player's own notes all refer to the same object. They
/// are deliberately not world-global: two rooms may both contain a terminal with
/// id 3 without meaning the same thing.
/// </remarks>
public readonly record struct InteractableId(int Value) : IComparable<InteractableId>
{
    public static readonly InteractableId None = new(0);

    public bool IsValid => Value > 0;

    public int CompareTo(InteractableId other) => Value.CompareTo(other.Value);

    public override string ToString() => $"I{Value:D3}";
}

/// <summary>
/// What an interactable fundamentally is.
/// </summary>
/// <remarks>
/// These are rule categories, not visual ones. Two objects with the same type
/// behave identically no matter how the presentation layer chooses to draw them —
/// a "door" in 2D and a "door" in 3D are the same rule object.
/// </remarks>
public enum InteractableType
{
    /// <summary>Something searchable that yields loot or intel.</summary>
    Container = 0,

    /// <summary>Blocks movement between slots until opened.</summary>
    Door = 1,

    /// <summary>A device that can be used, and sometimes hacked.</summary>
    Terminal = 2,

    /// <summary>A hostile presence with its own state and position.</summary>
    Guard = 3,

    /// <summary>A hazard that triggers when a slot is entered.</summary>
    Trap = 4,

    /// <summary>The way out. A room has at most one.</summary>
    Exit = 5,
}

/// <summary>
/// The state of one interactable.
/// </summary>
/// <remarks>
/// <para>
/// A flat set of named states rather than a bitset. Every state is reachable
/// from more than one other state, so a bitset would produce combinations that
/// no rule can ever produce — <c>Locked | Broken | Empty</c> and friends — and
/// every consumer would then need to defend against them.
/// </para>
/// <para>
/// Not every type uses every state: a terminal is never <c>Empty</c>, an exit is
/// never <c>Locked</c>. That is checked in <see cref="RoomContents.Validate"/>, not
/// in the enum, because the rules belong with the data.
/// </para>
/// </remarks>
public enum InteractableState
{
    /// <summary>Untouched default.</summary>
    Idle = 0,

    /// <summary>In use, engaged, or otherwise active.</summary>
    Active = 1,

    /// <summary>Opened or unlocked and usable.</summary>
    Open = 2,

    /// <summary>Shut and usable.</summary>
    Closed = 3,

    /// <summary>Cannot be opened yet. Rules gate this, not the player.</summary>
    Locked = 4,

    /// <summary>Searched or emptied; yields nothing further.</summary>
    Empty = 5,

    /// <summary>Defeated or disabled.</summary>
    Disabled = 6,

    /// <summary>Triggered. A trap does not reset.</summary>
    Triggered = 7,

    /// <summary>Destroyed. Structurally gone rather than merely off.</summary>
    Destroyed = 8,
}

/// <summary>
/// One interactable inside a mission node's interior.
/// </summary>
/// <remarks>
/// <para>
/// <b>No coordinates.</b> <see cref="SlotIndex"/> is an abstract index into the
/// room's slot space. Presentation decides that slot 3 is two metres to the left
/// in 3D, or the leftmost alcove in 2D, or nothing at all. Core cares about the
/// number because rules reference it — "the terminal in the slot past the locked
/// door" has to mean something — and nothing else (knowledge.md rule 10).
/// </para>
/// <para>
/// The type is a mutable class rather than a record because interactables are
/// long-lived mutable state that must hash into the save and the replay
/// fingerprint, and because the mission systems will hold references to them
/// across ticks.
/// </para>
/// </remarks>
public sealed class Interactable
{
    /// <summary>Stable id, unique within the owning room.</summary>
    public InteractableId Id { get; init; }

    /// <summary>Rule category.</summary>
    public InteractableType Type { get; init; }

    /// <summary>
    /// Abstract slot this object occupies. Presentation maps it to a position.
    /// </summary>
    /// <remarks>
    /// Not unique: two objects may share a slot, which is how a container sitting
    /// inside a room reads in 2D versus how a guard standing beside a terminal
    /// reads in 3D. Core permits it; presentation decides whether it is visible.
    /// </remarks>
    public int SlotIndex { get; set; }

    /// <summary>Current rule state.</summary>
    public InteractableState State { get; set; } = InteractableState.Idle;

    /// <summary>
    /// Slot indices this object's own footprint covers, when it covers more than
    /// the one slot in <see cref="SlotIndex"/>.
    /// </summary>
    /// <remarks>
    /// A wide container in 2D and a deep one in 3D are the same object. Which
    /// slots it *covers* is a rule question (a door blocks the slot behind it);
    /// how many pixels or metres that is belongs to presentation.
    /// </remarks>
    public List<int> CoveredSlotIndices { get; } = new();

    /// <summary>Slots this object blocks while it is shut or locked.</summary>
    public List<int> BlockedSlotIndices { get; } = new();

    /// <summary>
    /// True when the object still does something. A searched container does not.
    /// </summary>
    public bool IsSpent => State is InteractableState.Empty
        or InteractableState.Destroyed
        or InteractableState.Disabled;

    /// <summary>True when the object is between the player and somewhere else.</summary>
    public bool IsBlocking => Type == InteractableType.Door
        && State is InteractableState.Closed or InteractableState.Locked;

    /// <summary>Clamps the object into legal ranges.</summary>
    public void Normalize()
    {
        if (SlotIndex < 0)
            SlotIndex = 0;

        BlockedSlotIndices.Remove(SlotIndex);
    }

    /// <summary>
    /// True when <paramref name="state"/> is legal for this object's type.
    /// </summary>
    /// <remarks>
    /// Kept as a pure function of the enum pair so the validator, the mission
    /// generator and the tests all agree on what "legal" means.
    /// </remarks>
    public static bool IsStateLegal(InteractableType type, InteractableState state) => type switch
    {
        InteractableType.Container => state is not InteractableState.Triggered,
        InteractableType.Door => state is not (InteractableState.Empty or InteractableState.Triggered),
        InteractableType.Terminal => state is not (InteractableState.Empty or InteractableState.Triggered),
        InteractableType.Guard => state is not InteractableState.Open,
        InteractableType.Trap => state is InteractableState.Idle
            or InteractableState.Triggered
            or InteractableState.Disabled
            or InteractableState.Destroyed,
        InteractableType.Exit => state is InteractableState.Idle
            or InteractableState.Open
            or InteractableState.Destroyed,
        _ => false,
    };
}

/// <summary>
/// The abstract interior of a mission node.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole of a node's interior as far as Core is concerned.</b> A
/// list of interactables, each with an id, a type, a state, and a slot index.
/// There is no geometry here and adding any would mean the same node had to be
/// re-authored for every presentation (knowledge.md rule 10).
/// </para>
/// <para>
/// Walking a character around the room is presentation. What the player *does*
/// reaches Core only as an <see cref="ICommand"/>, which is what keeps a room
/// traversal replayable — a position that moved without a command in the log
/// would break determinism on the next save.
/// </para>
/// </remarks>
public sealed class RoomContents
{
    private readonly Dictionary<InteractableId, Interactable> _byId = new();

    /// <summary>Interactables in this room, in generation order.</summary>
    public List<Interactable> Interactables { get; } = new();

    /// <summary>
    /// How many slots this room has. Slot indices must be below this.
    /// </summary>
    /// <remarks>
    /// A rule input, not a size: it bounds which slot indices are legal and how
    /// far apart two objects can be described as being. Presentation is free to
    /// draw those slots at any size, in any projection.
    /// </remarks>
    public int SlotCount { get; set; } = 1;

    /// <summary>Next id to hand out. Ids are per-room and never reused.</summary>
    private int _nextId = 1;

    /// <summary>Number of interactables present.</summary>
    public int Count => Interactables.Count;

    /// <summary>Adds an interactable, assigning it an id if it does not have one.</summary>
    public Interactable Add(Interactable interactable)
    {
        if (interactable is null) throw new ArgumentNullException(nameof(interactable));

        Interactable stored = interactable;

        if (!stored.Id.IsValid)
            stored = CloneWithId(interactable, new InteractableId(_nextId));

        _nextId = Math.Max(_nextId, stored.Id.Value + 1);

        stored.Normalize();
        Interactables.Add(stored);
        _byId[stored.Id] = stored;
        return stored;
    }

    /// <summary>Creates and adds an interactable from its parts.</summary>
    public Interactable Add(InteractableType type, int slotIndex, InteractableState state = InteractableState.Idle)
        => Add(new Interactable { Type = type, SlotIndex = slotIndex, State = state });

    private static Interactable CloneWithId(Interactable source, InteractableId id)
    {
        var copy = new Interactable
        {
            Id = id,
            Type = source.Type,
            SlotIndex = source.SlotIndex,
            State = source.State,
        };

        copy.CoveredSlotIndices.AddRange(source.CoveredSlotIndices);
        copy.BlockedSlotIndices.AddRange(source.BlockedSlotIndices);
        return copy;
    }

    /// <summary>Looks up an interactable by id, or null.</summary>
    public Interactable? Find(InteractableId id)
        => _byId.TryGetValue(id, out Interactable? found) ? found : null;

    /// <summary>Every interactable of a given type.</summary>
    public IReadOnlyList<Interactable> OfType(InteractableType type)
    {
        var matches = new List<Interactable>();
        foreach (Interactable interactable in Interactables)
        {
            if (interactable.Type == type)
                matches.Add(interactable);
        }

        return matches;
    }

    /// <summary>Every interactable occupying a given slot.</summary>
    /// <remarks>
    /// Several objects may share a slot; the caller decides which one it meant.
    /// That ambiguity is intentional — it is what lets the same room data render
    /// sensibly in more than one projection.
    /// </remarks>
    public IReadOnlyList<Interactable> InSlot(int slotIndex)
    {
        var matches = new List<Interactable>();
        foreach (Interactable interactable in Interactables)
        {
            if (interactable.SlotIndex == slotIndex)
                matches.Add(interactable);
        }

        return matches;
    }

    /// <summary>The room's exit, or null when it has none.</summary>
    public Interactable? Exit =>
        Interactables.Count == 0 ? null : OfType(InteractableType.Exit).FirstOrDefault();

    /// <summary>
    /// Whether an object at <paramref name="slotIndex"/> is reachable right now.
    /// </summary>
    /// <remarks>
    /// A pure reachability query over abstract state: no pathfinding, no geometry.
    /// Presentation owns how a character gets from one slot to another; this says
    /// only whether the rules currently permit it.
    /// </remarks>
    public bool IsSlotReachable(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= SlotCount)
            return false;

        foreach (Interactable blocker in Interactables)
        {
            if (blocker.IsBlocking && blocker.BlockedSlotIndices.Contains(slotIndex))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks the room's internal consistency. Returns false and names the first
    /// problem when the room is malformed.
    /// </summary>
    public bool Validate(out string problem)
    {
        problem = string.Empty;

        if (SlotCount <= 0)
        {
            problem = "RoomSlotCountInvalid";
            return false;
        }

        var seenIds = new HashSet<int>();

        foreach (Interactable interactable in Interactables)
        {
            if (!interactable.Id.IsValid)
            {
                problem = $"InteractableIdInvalid:{interactable.Type}";
                return false;
            }

            if (!seenIds.Add(interactable.Id.Value))
            {
                problem = $"DuplicateInteractableId:{interactable.Id}";
                return false;
            }

            if (interactable.SlotIndex < 0 || interactable.SlotIndex >= SlotCount)
            {
                problem = $"InteractableSlotOutOfRange:{interactable.Id}:{interactable.SlotIndex}";
                return false;
            }

            if (!Interactable.IsStateLegal(interactable.Type, interactable.State))
            {
                problem = $"InteractableStateIllegal:{interactable.Id}:{interactable.Type}:{interactable.State}";
                return false;
            }

            foreach (int slot in interactable.BlockedSlotIndices)
            {
                if (slot < 0 || slot >= SlotCount)
                {
                    problem = $"BlockedSlotOutOfRange:{interactable.Id}:{slot}";
                    return false;
                }
            }
        }

        // More than one exit would leave "where does the team leave from"
        // ambiguous, and every consumer would have to pick one arbitrarily.
        int exits = OfType(InteractableType.Exit).Count;
        if (exits > 1)
        {
            problem = $"MultipleExits:{exits}";
            return false;
        }

        return true;
    }
}
