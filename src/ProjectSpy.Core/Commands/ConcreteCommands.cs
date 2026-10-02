namespace ProjectSpy.Core;

/// <summary>
/// Places a room into abstract slots and a depth layer, for a cost that already
/// includes the layer modifier.
/// </summary>
/// <remarks>
/// <para>
/// The caller supplies the final cost because the base cost comes from
/// room_type.csv and the layer modifier from <see cref="BaseLayout"/>.
/// </para>
/// <para>
/// <b>No coordinates (knowledge.md rule 10).</b> The command names slots, a layer,
/// and the rooms this one touches. Presentation decides what any of that looks
/// like — which is precisely why the command carries no position: a placement the
/// player made by dragging a sprite has to be replayable from the log alone.
/// </para>
/// </remarks>
public sealed record BuildRoomCommand(
    int TypeId,
    int Layer,
    IReadOnlyList<int> SlotIndices,
    IReadOnlyList<RoomId>? AdjacentRoomIds,
    string NameKey,
    long TotalCost) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (TotalCost < 0)
            return CommandResult.Rejected(CommandReason.InsufficientFunds, CommandArgs.Amount(TotalCost));

        // Affordability must be checked here, not only in Apply: the UI asks Validate
        // to grey out an unaffordable button, and Apply's hard failure must never be
        // reachable from a normal Execute.
        if (world.Resources.Funds < TotalCost)
        {
            return CommandResult.Rejected(
                CommandReason.InsufficientFunds,
                CommandArgs.Shortfall(TotalCost, world.Resources.Funds));
        }

        PlacementError error = world.BaseLayout.CheckPlacement(TypeId, Layer, SlotIndices, AdjacentRoomIds);
        return error switch
        {
            PlacementError.None => CommandResult.Ok,
            PlacementError.LayerOutOfRange => CommandResult.Rejected(CommandReason.PlacementOutOfRange),
            PlacementError.SlotOutOfRange => CommandResult.Rejected(CommandReason.PlacementOutOfRange),
            PlacementError.SlotOccupied => CommandResult.Rejected(CommandReason.PlacementSlotOccupied),
            PlacementError.NoSlots => CommandResult.Rejected(CommandReason.PlacementNoSlots),
            PlacementError.LockedByStory => CommandResult.Rejected(CommandReason.RoomTypeLocked, CommandArgs.Named(NameKey)),
            PlacementError.LayerTooShallow => CommandResult.Rejected(CommandReason.RoomLayerTooShallow),
            PlacementError.UnknownNeighbour => CommandResult.Rejected(CommandReason.PlacementUnknownNeighbour),
            PlacementError.NeighbourInOtherLayer => CommandResult.Rejected(CommandReason.PlacementNeighbourOtherLayer),
            _ => CommandResult.Rejected(CommandReason.PlacementOutOfRange),
        };
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng)
    {
        // Charge first and bail loudly if it fails: reaching here without enough funds
        // means Validate and Apply disagree, which is a bug worth crashing on.
        if (!world.Resources.TrySpendFunds(TotalCost, out Resources charged))
        {
            throw new InvalidOperationException(
                $"BuildRoomCommand passed validation but funds were insufficient for {TotalCost}.");
        }

        world.Resources = charged;

        Room? room = world.BaseLayout.Place(
            TypeId, Layer, SlotIndices, world.Clock.Current, out PlacementError error, AdjacentRoomIds);

        if (room is null)
            throw new InvalidOperationException($"BuildRoomCommand passed validation but placement failed: {error}.");

        // Construction time comes from room_type.build_ticks (stage 2 table).
        room.ConstructionTicksRemaining = RoomDefinitions.BuildTicksFor(world, TypeId);
    }
}

/// <summary>Moves a room's agents out and demolishes it, refunding a portion of the cost.</summary>
public sealed record DemolishRoomCommand(RoomId RoomId, long RefundAmount) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        Room? room = world.BaseLayout.GetRoom(RoomId);
        return room is null
            ? CommandResult.Rejected(CommandReason.UnknownRoom, CommandArgs.Entity(RoomId.Value))
            : CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng)
    {
        Room room = world.BaseLayout.GetRoom(RoomId)
                    ?? throw new InvalidOperationException($"DemolishRoomCommand lost room {RoomId} between validate and apply.");

        // Displaced agents go Idle rather than vanishing: an agent with nowhere to work
        // is a real (worse) outcome than a wasted room.
        foreach (AgentId agentId in room.AssignedAgentIds.ToArray())
        {
            Agent? agent = world.GetAgent(agentId);
            if (agent is null)
                continue;

            agent.AssignedRoomId = 0;
            agent.Status = AgentStatus.Idle;
        }

        room.AssignedAgentIds.Clear();
        world.BaseLayout.Demolish(RoomId);

        if (RefundAmount > 0)
            world.Resources = world.Resources.EarnFunds(RefundAmount);
    }
}

/// <summary>Assigns an agent to a room, optionally moving them out of their current one.</summary>
public sealed record AssignAgentToRoomCommand(AgentId AgentId, RoomId RoomId) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        Agent? agent = world.GetAgent(AgentId);
        if (agent is null)
        {
            return CommandResult.Rejected(
                world.GetRecruit(AgentId) is not null
                    ? CommandReason.AgentNotRecruited
                    : CommandReason.UnknownAgent,
                CommandArgs.Entity(AgentId.Value));
        }

        if (agent.Status is AgentStatus.Dead or AgentStatus.Retired)
            return CommandResult.Rejected(CommandReason.AgentDead, CommandArgs.Entity(AgentId.Value));

        if (agent.Status == AgentStatus.OnMission)
            return CommandResult.Rejected(CommandReason.AgentUnavailable, CommandArgs.Entity(AgentId.Value));

        Room? room = world.BaseLayout.GetRoom(RoomId);
        if (room is null)
            return CommandResult.Rejected(CommandReason.UnknownRoom, CommandArgs.Entity(RoomId.Value));

        // Capacity comes from room_type.worker_slots (stage 2 table).
        int capacity = RoomDefinitions.WorkerSlotsFor(world, room.TypeId);
        if (!room.AssignedAgentIds.Contains(AgentId) && room.AssignedAgentIds.Count >= capacity)
            return CommandResult.Rejected(CommandReason.RoomAtCapacity, CommandArgs.Entity(RoomId.Value));

        return CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng)
    {
        Agent agent = world.GetAgent(AgentId)
                      ?? throw new InvalidOperationException($"AssignAgentToRoomCommand lost agent {AgentId}.");

        Room room = world.BaseLayout.GetRoom(RoomId)
                    ?? throw new InvalidOperationException($"AssignAgentToRoomCommand lost room {RoomId}.");

        // Unassign from the previous room first so both sides stay consistent.
        if (agent.AssignedRoomId != 0 && agent.AssignedRoomId != room.Id.Value)
        {
            Room? previous = world.BaseLayout.GetRoom(new RoomId(agent.AssignedRoomId));
            previous?.AssignedAgentIds.Remove(AgentId);
        }

        if (!room.AssignedAgentIds.Contains(AgentId))
            room.AssignedAgentIds.Add(AgentId);

        agent.AssignedRoomId = room.Id.Value;
    }
}

/// <summary>Pays the up-front hiring fee and moves a candidate onto the roster.</summary>
public sealed record HireRecruitCommand(AgentId RecruitId, long Fee) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (world.GetRecruit(RecruitId) is null)
        {
            return CommandResult.Rejected(
                world.GetAgent(RecruitId) is not null
                    ? CommandReason.AgentAlreadyAssigned
                    : CommandReason.AgentNotRecruited,
                CommandArgs.Entity(RecruitId.Value));
        }

        if (Fee < 0)
            return CommandResult.Rejected(CommandReason.InsufficientFunds, CommandArgs.Amount(Fee));

        if (world.Resources.Funds < Fee)
        {
            return CommandResult.Rejected(
                CommandReason.InsufficientFunds,
                CommandArgs.Shortfall(Fee, world.Resources.Funds));
        }

        return CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng)
    {
        if (Fee > 0)
        {
            if (!world.Resources.TrySpendFunds(Fee, out Resources charged))
            {
                throw new InvalidOperationException(
                    $"HireRecruitCommand passed validation but funds were insufficient for {Fee}.");
            }

            world.Resources = charged;
        }

        if (!world.HireRecruit(RecruitId))
        {
            throw new InvalidOperationException(
                $"HireRecruitCommand passed validation but recruit {RecruitId} was not in the pool.");
        }
    }
}

/// <summary>Sets or clears a story flag.</summary>
public sealed record SetFlagCommand(string FlagName, bool Value) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        return string.IsNullOrEmpty(FlagName)
            ? CommandResult.Rejected(CommandReason.UnknownAgent)
            : CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng) => world.SetFlag(FlagName, Value);
}
