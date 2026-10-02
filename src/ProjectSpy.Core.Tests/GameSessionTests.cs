using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the command/event contract Presentation depends on: validation gates
/// mutation, rejections change nothing, events describe what happened, and the log
/// makes a run replayable.
/// </summary>
public class GameSessionTests
{
    private const ulong Seed = 20240101UL;

    private static GameSession NewSession() => new(Seed);

    private static WorldState UnlockedWorld(int funds = 10_000)
    {
        var world = new WorldState(Seed);
        world.Resources = world.Resources with { Funds = funds };
        world.BaseLayout.UnlockedRoomTypeIds.Add(1);
        world.BaseLayout.UnlockedRoomTypeIds.Add(2);
        return world;
    }

    // ---- command flow --------------------------------------------------------

    [Fact]
    public void Execute_ValidCommand_AppliesItAndLogsIt()
    {
        var session = new GameSession(UnlockedWorld());
        var command = new BuildRoomCommand(1, 0, 0, 3, "room.training", 500);

        CommandResult result = session.Execute(command);

        Assert.True(result.IsOk);
        Assert.Single(session.World.BaseLayout.Rooms);
        Assert.Single(session.CommandLog);
        Assert.Equal(9_500, session.World.Resources.Funds);
    }

    [Fact]
    public void Execute_RejectedCommand_ChangesNothing()
    {
        var session = new GameSession(UnlockedWorld());
        session.Execute(new BuildRoomCommand(1, 0, 0, 3, "room.training", 500));
        long fundsAfterBuild = session.World.Resources.Funds;

        // Overlaps the room just built.
        CommandResult result = session.Execute(new BuildRoomCommand(2, 2, 0, 3, "room.ops", 100));

        Assert.True(result.IsRejected);
        Assert.Equal(CommandReason.PlacementOverlaps, result.Reason);
        Assert.Single(session.World.BaseLayout.Rooms);
        Assert.Equal(fundsAfterBuild, session.World.Resources.Funds);
        Assert.Single(session.CommandLog); // rejected commands are not logged
    }

    [Fact]
    public void RejectionCarriesAReasonCode_NotAnEnglishSentence()
    {
        var session = new GameSession(UnlockedWorld(funds: 10));

        CommandResult result = session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 500));

        Assert.True(result.IsRejected);
        Assert.Equal(CommandReason.InsufficientFunds, result.Reason);

        // The UI resolves this key into localized text; Core supplies only the key
        // and the numbers to interpolate.
        Assert.Equal("command.reject.insufficientfunds", result.MessageKey);
        Assert.Equal(500, result.Args.Primary);
    }

    [Fact]
    public void InsufficientFunds_RejectionCarriesShortfallAndAvailable()
    {
        var session = new GameSession(UnlockedWorld(funds: 200));

        CommandResult result = session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 500));

        Assert.Equal(CommandReason.InsufficientFunds, result.Reason);
        Assert.Equal(500, result.Args.Primary);   // needed
        Assert.Equal(200, result.Args.Secondary); // available
    }

    [Fact]
    public void Execute_UnknownAgent_IsRejected()
    {
        var session = new GameSession(UnlockedWorld());

        CommandResult result = session.Execute(new AssignAgentToRoomCommand(new AgentId(999), new RoomId(1)));

        Assert.True(result.IsRejected);
        Assert.Equal(CommandReason.UnknownAgent, result.Reason);
    }

    [Fact]
    public void Execute_NullCommand_Throws()
    {
        var session = NewSession();
        Assert.Throws<ArgumentNullException>(() => session.Execute(null!));
    }

    [Fact]
    public void CommandResult_MessageKeyIsStable()
    {
        Assert.Equal("command.ok", CommandResult.Ok.MessageKey);
        Assert.Equal(
            "command.reject.placementoutofbounds",
            CommandResult.Rejected(CommandReason.PlacementOutOfBounds).MessageKey);
    }

    [Fact]
    public void CommandResult_RejectionRequiresAReason()
    {
        Assert.Throws<ArgumentException>(() => CommandResult.Rejected(CommandReason.None));
    }

    // ---- events --------------------------------------------------------------

    [Fact]
    public void Execute_PublishesEventsToSubscribers()
    {
        var session = new GameSession(UnlockedWorld());
        var kinds = new List<GameEventKind>();

        using IDisposable _ = session.Subscribe(e => kinds.Add(e.Kind));

        session.Execute(new BuildRoomCommand(1, 0, 0, 3, "room.training", 500));

        Assert.Contains(GameEventKind.ResourcesChanged, kinds);
    }

    [Fact]
    public void RejectedCommand_PublishesCommandRejected()
    {
        var session = new GameSession(UnlockedWorld(funds: 0));
        var events = new List<GameEvent>();

        using IDisposable _ = session.Subscribe(events.Add);

        session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 500));

        CommandRejected rejected = Assert.IsType<CommandRejected>(events.Single());
        Assert.Equal(CommandReason.InsufficientFunds, rejected.Reason);
    }

    [Fact]
    public void ResourcesChanged_CarriesTheDeltas()
    {
        var session = new GameSession(UnlockedWorld());
        ResourcesChanged? captured = null;

        using IDisposable _ = session.Subscribe(e =>
        {
            if (e is ResourcesChanged changed)
                captured = changed;
        });

        session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 250));

        Assert.NotNull(captured);
        Assert.Equal(-250, captured!.FundsDelta);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var session = NewSession();
        int count = 0;

        IDisposable subscription = session.Subscribe(_ => count++);
        session.AdvanceTick();
        int afterFirst = count;
        Assert.True(afterFirst > 0);

        subscription.Dispose();
        session.AdvanceTick();

        Assert.Equal(afterFirst, count);
    }

    [Fact]
    public void Subscribe_ByKind_FiltersOtherEvents()
    {
        var session = NewSession();
        int dayEvents = 0;

        using IDisposable _ = session.Subscribe(GameEventKind.DayChanged, _ => dayEvents++);

        session.AdvanceTick();

        Assert.Equal(0, dayEvents); // not midnight yet
        session.AdvanceToNextDay();
        Assert.Equal(1, dayEvents);
    }

    // ---- ticking -------------------------------------------------------------

    [Fact]
    public void AdvanceTick_MovesTimeAndPublishesTickAdvanced()
    {
        var session = NewSession();
        var ticks = new List<long>();

        using IDisposable _ = session.Subscribe(e =>
        {
            if (e is TickAdvanced advanced)
                ticks.Add(advanced.Tick.Value);
        });

        session.AdvanceTick();
        session.AdvanceTick();

        Assert.Equal(new[] { 1L, 2L }, ticks);
        Assert.Equal(new Tick(2), session.CurrentTick);
    }

    [Fact]
    public void AdvanceToNextDay_LandsExactlyOnMidnight()
    {
        var session = NewSession();
        session.AdvanceTicks(5);

        session.AdvanceToNextDay();

        Assert.Equal(0, session.CurrentTick.HourOfDay);
        Assert.Equal(1, session.CurrentTick.Day);
    }

    [Fact]
    public void AdvanceToNextWeek_LandsOnTheWeekBoundary()
    {
        var session = NewSession();
        session.AdvanceTicks(30);

        session.AdvanceToNextWeek();

        Assert.Equal(0, session.CurrentTick.TickOfWeek);
        Assert.Equal(1, session.CurrentTick.Week);
    }

    [Fact]
    public void WorldStateSurvivesAYearOfTicks_WithoutInvalidState()
    {
        var session = NewSession();

        session.AdvanceTicks(Tick.TicksPerDay * 365);

        Assert.Equal(365, session.CurrentTick.Day);
        Assert.True(session.ValidateWorld(out string problem), problem);
    }

    [Fact]
    public void TickPipeline_CanonicalOrder_IsTheOrderInTheBrief()
    {
        // This ordering is part of the save contract: changing it invalidates every
        // existing replay, so it is asserted rather than assumed.
        Assert.Equal(
            new[]
            {
                TickPhase.RoomConstruction,
                TickPhase.Training,
                TickPhase.Recovery,
                TickPhase.MissionProgress,
                TickPhase.EventChecks,
                TickPhase.StatusDecay,
            },
            TickPipeline.CanonicalOrder);
    }

    [Fact]
    public void TickPipeline_RunsPhasesInCanonicalOrder_NotRegistrationOrder()
    {
        var session = NewSession();
        var ran = new List<TickPhase>();

        // Registered in deliberately reversed order.
        session.Pipeline.Register(new RecordingPhase(TickPhase.StatusDecay, ran));
        session.Pipeline.Register(new RecordingPhase(TickPhase.EventChecks, ran));
        session.Pipeline.Register(new RecordingPhase(TickPhase.RoomConstruction, ran));
        session.Pipeline.Register(new RecordingPhase(TickPhase.Recovery, ran));
        session.Pipeline.Register(new RecordingPhase(TickPhase.Training, ran));
        session.Pipeline.Register(new RecordingPhase(TickPhase.MissionProgress, ran));

        session.AdvanceTick();

        Assert.Equal(TickPipeline.CanonicalOrder, ran);
    }

    [Fact]
    public void TickPipeline_WithNoPhasesRegistered_StillTicksCleanly()
    {
        var session = NewSession();

        session.AdvanceTick();

        Assert.Equal(1, session.CurrentTick.Value);
    }

    [Fact]
    public void StubPhases_ThrowNotImplemented_RatherThanFailingSilently()
    {
        // knowledge.md rule 5: an out-of-scope body must be loud.
        var pipeline = NotImplementedPhases.CreateStubPipeline(new NullSink());
        var world = new WorldState(Seed);
        var context = new PhaseContext(Tick.Zero) { Events = new NullSink() };

        Assert.Throws<NotImplementedException>(() => pipeline.RunTick(world, context));
    }

    // ---- determinism ---------------------------------------------------------

    [Fact]
    public void SameSeed_ProducesTheSameStateHash()
    {
        var a = new GameSession(Seed);
        var b = new GameSession(Seed);

        a.AdvanceTicks(500);
        b.AdvanceTicks(500);

        Assert.Equal(a.World.ComputeStateHash(), b.World.ComputeStateHash());
    }

    [Fact]
    public void DifferentSeeds_DivergeAfterEnoughRolls()
    {
        var a = new GameSession(1);
        var b = new GameSession(2);

        for (int i = 0; i < 50; i++)
        {
            a.World.RngStreams[RngStreams.StreamKind.World].NextRoll100();
            b.World.RngStreams[RngStreams.StreamKind.World].NextRoll100();
        }

        a.World.RngStreams[RngStreams.StreamKind.World].NextRoll100();
        b.World.RngStreams[RngStreams.StreamKind.World].NextRoll100();

        Assert.NotEqual(a.World.ComputeStateHash(), b.World.ComputeStateHash());
    }

    [Fact]
    public void StateHash_IsStableAcrossIdenticalRuns()
    {
        static ulong Run()
        {
            var session = new GameSession(Seed);
            session.World.BaseLayout.UnlockedRoomTypeIds.Add(1);
            session.Execute(new BuildRoomCommand(1, 0, 0, 4, "room.training", 300));
            session.Execute(new BuildRoomCommand(1, 5, 0, 4, "room.training", 300));
            session.AdvanceTicks(200);
            return session.World.ComputeStateHash();
        }

        Assert.Equal(Run(), Run());
    }

    // ---- roster --------------------------------------------------------------

    [Fact]
    public void HireRecruit_MovesCandidateOntoTheRoster()
    {
        var world = UnlockedWorld(funds: 5_000);
        Agent candidate = world.AddRecruit(new Agent
        {
            Name = "Nok",
            Codename = "Lantern",
            ClassId = 2,
            Skills = new SkillSet(10, 20, 30, 40, 50),
            SalaryPerWeek = 250,
        });

        var session = new GameSession(world);
        CommandResult result = session.Execute(new HireRecruitCommand(candidate.Id, 1_000));

        Assert.True(result.IsOk);
        Assert.Empty(session.World.Recruits);
        Agent hired = Assert.Single(session.World.Agents.Values);

        // Renumbered into the positive roster id space.
        Assert.True(hired.Id.IsValid);
        Assert.Equal("Lantern", hired.Codename);
        Assert.Equal(4_000, session.World.Resources.Funds);
    }

    [Fact]
    public void HireRecruit_Unaffordable_IsRejectedAndKeepsTheCandidate()
    {
        var world = UnlockedWorld(funds: 100);
        Agent candidate = world.AddRecruit(new Agent { Name = "Ploy", SalaryPerWeek = 10 });

        var session = new GameSession(world);
        CommandResult result = session.Execute(new HireRecruitCommand(candidate.Id, 5_000));

        Assert.Equal(CommandReason.InsufficientFunds, result.Reason);
        Assert.Single(session.World.Recruits);
        Assert.Empty(session.World.Agents);
    }

    [Fact]
    public void AssignAgentToRoom_RejectsWhenTheRoomIsFull()
    {
        var world = UnlockedWorld();
        var session = new GameSession(world);
        session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 100));

        Room room = session.World.BaseLayout.Rooms[0];
        Agent a = session.World.AddAgent(new Agent { Name = "A" });
        Agent b = session.World.AddAgent(new Agent { Name = "B" });

        Assert.True(session.Execute(new AssignAgentToRoomCommand(a.Id, room.Id)).IsOk);

        CommandResult second = session.Execute(new AssignAgentToRoomCommand(b.Id, room.Id));

        Assert.Equal(CommandReason.RoomAtCapacity, second.Reason);
    }

    [Fact]
    public void DemolishRoom_FreesAssignedAgents()
    {
        var world = UnlockedWorld();
        var session = new GameSession(world);
        session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 100));

        Room room = session.World.BaseLayout.Rooms[0];
        Agent agent = session.World.AddAgent(new Agent { Name = "Mali" });
        session.Execute(new AssignAgentToRoomCommand(agent.Id, room.Id));

        CommandResult result = session.Execute(new DemolishRoomCommand(room.Id, RefundAmount: 40));

        Assert.True(result.IsOk);
        Assert.Empty(session.World.BaseLayout.Rooms);
        Assert.Equal(0, agent.AssignedRoomId);
        Assert.Equal(AgentStatus.Idle, agent.Status);
        Assert.Equal(9_940, session.World.Resources.Funds);
    }

    [Fact]
    public void DeadAgent_CannotBeAssigned()
    {
        var world = UnlockedWorld();
        var session = new GameSession(world);
        session.Execute(new BuildRoomCommand(1, 0, 0, 2, "room.training", 100));
        Room room = session.World.BaseLayout.Rooms[0];

        Agent agent = session.World.AddAgent(new Agent { Name = "Ghost", Status = AgentStatus.Dead });

        CommandResult result = session.Execute(new AssignAgentToRoomCommand(agent.Id, room.Id));

        Assert.Equal(CommandReason.AgentDead, result.Reason);
    }

    [Fact]
    public void SetFlag_AppliesAndValidates()
    {
        var session = NewSession();

        Assert.True(session.Execute(new SetFlagCommand("act_2_unlocked", true)).IsOk);
        Assert.True(session.World.GetFlag("act_2_unlocked"));

        Assert.True(session.Execute(new SetFlagCommand(string.Empty, true)).IsRejected);
    }

    [Fact]
    public void Validate_StaysGreenThroughNormalPlay()
    {
        var world = UnlockedWorld();
        var session = new GameSession(world);
        session.Execute(new BuildRoomCommand(1, 0, 0, 3, "room.training", 100));
        session.Execute(new BuildRoomCommand(2, 4, 0, 3, "room.ops", 100));
        session.AdvanceTicks(200);

        Assert.True(session.ValidateWorld(out string problem), problem);
    }

    // ---- helpers -------------------------------------------------------------

    private sealed class RecordingPhase : ITickPhase
    {
        private readonly List<TickPhase> _ran;

        public RecordingPhase(TickPhase phase, List<TickPhase> ran)
        {
            Phase = phase;
            _ran = ran;
        }

        public TickPhase Phase { get; }

        public void Run(WorldState world, PhaseContext context) => _ran.Add(Phase);
    }

    private sealed class NullSink : IEventSink
    {
        public void Publish(GameEvent gameEvent)
        {
            // Deliberately discards: this sink exists only to satisfy the pipeline.
        }
    }
}
