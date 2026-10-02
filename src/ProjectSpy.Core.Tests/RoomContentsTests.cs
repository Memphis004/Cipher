using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// <see cref="RoomContents"/>: the whole of a mission node's interior as far as Core
/// is concerned — interactables with ids, types, states and slot indices, and nothing
/// else (knowledge.md rule 10).
/// </summary>
public class RoomContentsTests
{
    private static RoomContents Room(int slots = 6)
        => new() { SlotCount = slots };

    // ---- identity ------------------------------------------------------------

    [Fact]
    public void AddedInteractablesGetStableDistinctIds()
    {
        RoomContents room = Room();

        Interactable a = room.Add(InteractableType.Container, 0);
        Interactable b = room.Add(InteractableType.Door, 1);

        Assert.True(a.Id.IsValid);
        Assert.True(b.Id.IsValid);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Same(a, room.Find(a.Id));
        Assert.Same(b, room.Find(b.Id));
    }

    [Fact]
    public void IdsAreNotReusedAfterARoomIsEmptied()
    {
        // A replay, a save and the player's own notes all refer to an object by id.
        // Reusing one would make an old reference point at something else.
        RoomContents room = Room();

        Interactable first = room.Add(InteractableType.Container, 0);
        Interactable second = room.Add(InteractableType.Container, 1);

        Assert.True(second.Id.Value > first.Id.Value);
    }

    [Fact]
    public void AnExplicitIdIsHonoured()
    {
        var room = Room();
        var terminal = room.Add(new Interactable
        {
            Id = new InteractableId(42),
            Type = InteractableType.Terminal,
            SlotIndex = 2,
        });

        Assert.Equal(new InteractableId(42), terminal.Id);
        Assert.Equal(42, terminal.Id.Value);

        // A later object must not collide with it.
        Interactable next = room.Add(InteractableType.Door, 3);
        Assert.True(next.Id.Value > 42);
    }

    [Fact]
    public void FindReturnsNullForAnUnknownId()
    {
        RoomContents room = Room();
        Assert.Null(room.Find(new InteractableId(999)));
    }

    // ---- composition ---------------------------------------------------------

    [Fact]
    public void EveryInteractableTypeCanBeRepresented()
    {
        RoomContents room = Room(10);

        foreach (InteractableType type in Enum.GetValues<InteractableType>())
        {
            room.Add(type, 0, InteractableState.Idle);
        }

        Assert.Equal(Enum.GetValues<InteractableType>().Length, room.Count);
    }

    [Fact]
    public void OfTypeReturnsOnlyThatType()
    {
        RoomContents room = Room();

        room.Add(InteractableType.Door, 0);
        room.Add(InteractableType.Door, 1);
        room.Add(InteractableType.Guard, 2);

        IReadOnlyList<Interactable> doors = room.OfType(InteractableType.Door);

        Assert.Equal(2, doors.Count);
        Assert.All(doors, d => Assert.Equal(InteractableType.Door, d.Type));
    }

    [Fact]
    public void SeveralObjectsMayShareOneSlot()
    {
        // Deliberate: it is what lets one room's data read sensibly in more than one
        // projection. A guard beside a terminal is one object each in 3D and might
        // reasonably be drawn overlapping in 2D.
        RoomContents room = Room();

        room.Add(InteractableType.Terminal, 4);
        room.Add(InteractableType.Guard, 4);

        Assert.Equal(2, room.InSlot(4).Count);
        Assert.Empty(room.InSlot(5));
    }

    [Fact]
    public void TheExitIsFoundByType()
    {
        RoomContents room = Room();
        room.Add(InteractableType.Container, 0);
        Interactable exit = room.Add(InteractableType.Exit, 5);

        Assert.Same(exit, room.Exit);
    }

    [Fact]
    public void ARoomWithNoExitReportsNone()
    {
        RoomContents room = Room();
        room.Add(InteractableType.Container, 0);

        Assert.Null(room.Exit);
    }

    // ---- state ---------------------------------------------------------------

    [Fact]
    public void StateIsMutableAndReadable()
    {
        RoomContents room = Room();
        Interactable door = room.Add(InteractableType.Door, 1, InteractableState.Locked);

        Assert.Equal(InteractableState.Locked, door.State);

        door.State = InteractableState.Open;

        Assert.Equal(InteractableState.Open, room.Find(door.Id)!.State);
    }

    [Theory]
    [InlineData(InteractableType.Container, InteractableState.Empty, true)]
    [InlineData(InteractableType.Container, InteractableState.Destroyed, true)]
    [InlineData(InteractableType.Guard, InteractableState.Disabled, true)]
    [InlineData(InteractableType.Guard, InteractableState.Active, false)]
    [InlineData(InteractableType.Container, InteractableState.Closed, false)]
    public void IsSpentDistinguishesExhaustedObjectsFromWorkingOnes(
        InteractableType type,
        InteractableState state,
        bool expected)
    {
        var interactable = new Interactable { Type = type, State = state };

        Assert.Equal(expected, interactable.IsSpent);
    }

    [Theory]
    [InlineData(InteractableType.Door, InteractableState.Closed, true)]
    [InlineData(InteractableType.Door, InteractableState.Locked, true)]
    [InlineData(InteractableType.Door, InteractableState.Open, false)]
    [InlineData(InteractableType.Container, InteractableState.Closed, false)]
    [InlineData(InteractableType.Guard, InteractableState.Active, false)]
    public void OnlyShutOrLockedDoorsBlock(InteractableType type, InteractableState state, bool expected)
    {
        var interactable = new Interactable { Type = type, State = state };

        Assert.Equal(expected, interactable.IsBlocking);
    }

    [Fact]
    public void ImpossibleTypeStateCombinationsAreRejected()
    {
        // A guard cannot be "open" and a trap cannot sit idle-and-usable in a state
        // that means it already fired. These are the combinations no rule can produce,
        // and letting them exist means every consumer has to defend against them.
        Assert.False(Interactable.IsStateLegal(InteractableType.Guard, InteractableState.Open));
        Assert.False(Interactable.IsStateLegal(InteractableType.Door, InteractableState.Triggered));
        Assert.False(Interactable.IsStateLegal(InteractableType.Exit, InteractableState.Locked));
        Assert.False(Interactable.IsStateLegal(InteractableType.Terminal, InteractableState.Empty));
    }

    [Fact]
    public void OrdinaryTypeStateCombinationsAreAccepted()
    {
        Assert.True(Interactable.IsStateLegal(InteractableType.Guard, InteractableState.Active));
        Assert.True(Interactable.IsStateLegal(InteractableType.Door, InteractableState.Open));
        Assert.True(Interactable.IsStateLegal(InteractableType.Exit, InteractableState.Open));
        Assert.True(Interactable.IsStateLegal(InteractableType.Trap, InteractableState.Idle));
    }

    // ---- reachability --------------------------------------------------------

    [Fact]
    public void AClosedDoorBlocksTheSlotBehindIt()
    {
        RoomContents room = Room();

        var door = room.Add(InteractableType.Door, 2, InteractableState.Locked);
        door.BlockedSlotIndices.Add(3);

        Assert.False(room.IsSlotReachable(3));
        Assert.True(room.IsSlotReachable(2));
        Assert.True(room.IsSlotReachable(4));
    }

    [Fact]
    public void OpeningTheDoorUnblocksTheSlot()
    {
        RoomContents room = Room();

        var door = room.Add(InteractableType.Door, 2, InteractableState.Locked);
        door.BlockedSlotIndices.Add(3);

        Assert.False(room.IsSlotReachable(3));

        door.State = InteractableState.Open;

        Assert.True(room.IsSlotReachable(3));
    }

    [Fact]
    public void SlotsOutsideTheRoomAreNotReachable()
    {
        RoomContents room = Room(4);

        Assert.False(room.IsSlotReachable(-1));
        Assert.False(room.IsSlotReachable(4));
        Assert.False(room.IsSlotReachable(999));
    }

    // ---- validation ----------------------------------------------------------

    [Fact]
    public void AWellFormedRoomValidates()
    {
        RoomContents room = Room();

        room.Add(InteractableType.Container, 0);
        room.Add(InteractableType.Door, 1, InteractableState.Closed);
        room.Add(InteractableType.Exit, 5, InteractableState.Open);

        Assert.True(room.Validate(out string problem), problem);
    }

    [Fact]
    public void AnEmptyRoomValidates()
    {
        Assert.True(Room().Validate(out _));
    }

    [Fact]
    public void ARoomWithNoSlotsIsRejected()
    {
        var room = new RoomContents { SlotCount = 0 };

        Assert.False(room.Validate(out string problem));
        Assert.Equal("RoomSlotCountInvalid", problem);
    }

    [Fact]
    public void AnInteractableInAnOutOfRangeSlotIsRejected()
    {
        RoomContents room = Room(4);
        room.Add(InteractableType.Container, 9);

        Assert.False(room.Validate(out string problem));
        Assert.Contains("InteractableSlotOutOfRange", problem);
    }

    [Fact]
    public void AnIllegalTypeStateCombinationIsRejected()
    {
        RoomContents room = Room();
        room.Add(InteractableType.Guard, 0, InteractableState.Open);

        Assert.False(room.Validate(out string problem));
        Assert.Contains("InteractableStateIllegal", problem);
    }

    [Fact]
    public void TwoExitsAreRejected()
    {
        // "Where does the team leave from" has to have one answer, or every consumer
        // picks one arbitrarily and two consumers pick differently.
        RoomContents room = Room();
        room.Add(InteractableType.Exit, 1);
        room.Add(InteractableType.Exit, 4);

        Assert.False(room.Validate(out string problem));
        Assert.Contains("MultipleExits", problem);
    }

    [Fact]
    public void AnOutOfRangeBlockedSlotIsRejected()
    {
        RoomContents room = Room(3);

        var door = room.Add(InteractableType.Door, 0, InteractableState.Closed);
        door.BlockedSlotIndices.Add(9);

        Assert.False(room.Validate(out string problem));
        Assert.Contains("BlockedSlotOutOfRange", problem);
    }

    // ---- no coordinates ------------------------------------------------------

    [Fact]
    public void NothingInTheRoomModelDescribesAPosition()
    {
        // The rule's point, asserted directly: an interactable is an identity, a type,
        // a state and a slot index. Nothing in this object graph could tell a renderer
        // where to draw it, and that is deliberate.
        RoomContents room = Room(8);

        Interactable guard = room.Add(InteractableType.Guard, 3, InteractableState.Active);

        var described = new List<string>();
        foreach (var property in typeof(Interactable).GetProperties())
        {
            described.Add($"{property.Name}:{property.PropertyType.Name}");
        }

        Assert.Equal(
            new[]
            {
                "Id:InteractableId",
                "Type:InteractableType",
                "SlotIndex:Int32",
                "State:InteractableState",
                "CoveredSlotIndices:List`1",
                "BlockedSlotIndices:List`1",
                "IsSpent:Boolean",
                "IsBlocking:Boolean",
            },
            described);

        Assert.Equal(8, room.SlotCount);
        Assert.Equal(3, guard.SlotIndex);
    }

    [Fact]
    public void SlotIndicesAreClampedToNonNegativeOnAdd()
    {
        RoomContents room = Room();
        Interactable box = room.Add(InteractableType.Container, -5);

        Assert.Equal(0, box.SlotIndex);
    }
}
