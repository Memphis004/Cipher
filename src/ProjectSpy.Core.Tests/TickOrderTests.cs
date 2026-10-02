using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The tick order contract.
/// </summary>
/// <remarks>
/// The pipeline order is part of the save contract: reordering the phases changes
/// every outcome for an existing save and invalidates every recorded replay. These
/// tests exist so that reordering is a deliberate, visible act rather than an accident
/// that only shows up as a desync weeks later.
/// </remarks>
public class TickOrderTests
{
    [Fact]
    public void TheCanonicalOrderIsTheOrderTheBriefSpecifies()
    {
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
    public void EveryPhaseValueHasASlotInTheCanonicalOrder()
    {
        // A phase added to the enum without a slot would silently never run.
        var ordered = TickPipeline.CanonicalOrder.ToHashSet();

        foreach (TickPhase phase in Enum.GetValues<TickPhase>())
            Assert.Contains(phase, ordered);
    }

    [Fact]
    public void TheCanonicalOrderContainsNoDuplicates()
    {
        Assert.Equal(
            TickPipeline.CanonicalOrder.Count,
            TickPipeline.CanonicalOrder.Distinct().Count());
    }

    [Fact]
    public void TheDefaultPipelineRegistersEveryPhase()
    {
        GameSession session = World.Session();

        var registered = session.Pipeline.Registered.Select(p => p.Phase).ToHashSet();

        foreach (TickPhase phase in TickPipeline.CanonicalOrder)
            Assert.Contains(phase, registered);
    }

    [Fact]
    public void PhasesRunInCanonicalOrderRegardlessOfRegistrationOrder()
    {
        // Registered back to front on purpose. The pipeline must execute in enum order,
        // so a phase cannot be moved by the order it happened to be added in.
        var order = new List<TickPhase>();

        GameSession session = new(Seed);
        TickPipeline pipeline = new(session);

        foreach (TickPhase phase in TickPipeline.CanonicalOrder.Reverse())
        {
            pipeline.Register(new RecordingPhase(phase, order));
        }

        pipeline.RunTick(session.World, new PhaseContext(new Tick(1)) { Events = session });

        Assert.Equal(TickPipeline.CanonicalOrder, order);
    }

    [Fact]
    public void UnregisteredPhasesAreSkippedRatherThanFailing()
    {
        var order = new List<TickPhase>();

        GameSession session = new(Seed);
        TickPipeline pipeline = new(session);

        pipeline.Register(new RecordingPhase(TickPhase.StatusDecay, order));

        pipeline.RunTick(session.World, new PhaseContext(new Tick(1)) { Events = session });

        Assert.Equal(new[] { TickPhase.StatusDecay }, order);
    }

    [Fact]
    public void ReplacingAPhaseDoesNotRunBothImplementations()
    {
        var order = new List<TickPhase>();

        GameSession session = new(Seed);
        TickPipeline pipeline = new(session);

        pipeline.Register(new RecordingPhase(TickPhase.Training, order));
        pipeline.Register(new RecordingPhase(TickPhase.Training, order));

        pipeline.RunTick(session.World, new PhaseContext(new Tick(1)) { Events = session });

        Assert.Equal(new[] { TickPhase.Training }, order);
    }

    [Fact]
    public void ConstructionResolvesBeforeTrainingUsesTheRoom()
    {
        // Not an ordering curiosity: if training ran first, a room that completes
        // construction this tick could not be used until next tick, and every new room
        // would silently cost the player a tick.
        var order = new List<TickPhase>();

        GameSession session = new(Seed);
        TickPipeline pipeline = new(session);

        foreach (TickPhase phase in TickPipeline.CanonicalOrder)
            pipeline.Register(new RecordingPhase(phase, order));

        pipeline.RunTick(session.World, new PhaseContext(new Tick(1)) { Events = session });

        int construction = order.IndexOf(TickPhase.RoomConstruction);
        int training = order.IndexOf(TickPhase.Training);

        Assert.True(construction < training,
            "RoomConstruction must resolve before Training runs");
    }

    [Fact]
    public void TrainingIsChargedStaminaBeforeRecoveryRefillsIt()
    {
        // Recovery before Training would mean training never actually costs anything:
        // the stamina the phase consumes would be topped up in the same tick.
        List<TickPhase> order = TickPipeline.CanonicalOrder.ToList();
        int training = order.IndexOf(TickPhase.Training);
        int recovery = order.IndexOf(TickPhase.Recovery);

        Assert.True(training < recovery, "Training must run before Recovery");
    }

    [Fact]
    public void StatusDecayRunsLastSoItSeesTheWholeTicksWorth()
    {
        List<TickPhase> order = TickPipeline.CanonicalOrder.ToList();
        int decay = order.IndexOf(TickPhase.StatusDecay);

        Assert.True(
            decay == order.Count - 1,
            $"StatusDecay must be the final phase, found it at {decay} of {order.Count}");
    }

    [Fact]
    public void TheOrderIsStableAcrossManyRuns()
    {
        // Cheap, and it turns an accidental reorder in a future edit into a failure
        // rather than a slow drift in saved-game behaviour.
        for (int i = 0; i < 5; i++)
        {
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
    }

    private const ulong Seed = 1234UL;

    /// <summary>A phase that only appends its own enum value to a list.</summary>
    private sealed class RecordingPhase : ITickPhase
    {
        private readonly List<TickPhase> _order;

        internal RecordingPhase(TickPhase phase, List<TickPhase> order)
        {
            Phase = phase;
            _order = order;
        }

        public TickPhase Phase { get; }

        public void Run(WorldState world, PhaseContext context) => _order.Add(Phase);
    }
}
