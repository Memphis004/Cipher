namespace ProjectSpy.Core;

/// <summary>
/// The six per-tick phases, each a stub for stage 3.
/// </summary>
/// <remarks>
/// Every body throws <see cref="NotImplementedException"/> with a
/// <c>TODO(stage-3)</c> marker rather than sitting empty, per knowledge.md rule 5:
/// an empty body reads as "implemented" and hides the missing work.
/// <see cref="TickPipeline"/> only runs phases that have been registered, so these
/// stubs are inert until stage 3 implements and registers them.
/// </remarks>
public sealed class NotImplementedPhases
{
    /// <summary>Advances construction timers and completes finished rooms.</summary>
    public sealed class RoomConstructionPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.RoomConstruction;

        public void Run(WorldState world, PhaseContext context)
            => throw new NotImplementedException(
                "TODO(stage-3): decrement ConstructionTicksRemaining per placed room and publish " +
                "RoomConstructionCompleted when it reaches zero.");
    }

    /// <summary>Applies training gains and the stamina they cost.</summary>
    public sealed class TrainingPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.Training;

        public void Run(WorldState world, PhaseContext context)
            => throw new NotImplementedException(
                "TODO(stage-3): for each agent with Status == Training, compute " +
                "train_rate_per_tick * roomLevelMultiplier * classAffinity * traitModifier toward the " +
                "room's trained skill, consume stamina, and apply the overwork penalty below stamina 20.");
    }

    /// <summary>Restores stamina and heals injuries.</summary>
    public sealed class RecoveryPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.Recovery;

        public void Run(WorldState world, PhaseContext context)
            => throw new NotImplementedException(
                "TODO(stage-3): restore Physical and Mental stamina by room effect, at the table-defined " +
                "mental-to-physical ratio, clear injuries in the infirmary, and enter/exit Burnout.");
    }

    /// <summary>Advances in-flight missions and resolves node events.</summary>
    public sealed class MissionProgressPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.MissionProgress;

        public void Run(WorldState world, PhaseContext context)
            => throw new NotImplementedException(
                "TODO(stage-3): tick alarm decay for each active mission. Node resolution itself is " +
                "stage 4 (MissionRunner) and arrives via the MissionProgress stream.");
    }

    /// <summary>Rolls narrative and world events.</summary>
    public sealed class EventChecksPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.EventChecks;

        public void Run(WorldState world, PhaseContext context)
            => throw new NotImplementedException(
                "TODO(stage-3): roll for heat-driven raids and daily world events using the Event stream, " +
                "then apply the resulting consequences.");
    }

    /// <summary>Applies status drift, morale and burnout decay.</summary>
    public sealed class StatusDecayPhase : ITickPhase
    {
        public TickPhase Phase => TickPhase.StatusDecay;

        public void Run(WorldState world, PhaseContext context)
            => throw new NotImplementedException(
                "TODO(stage-3): apply daily loyalty drift from workload, pay fairness, losses, idleness, " +
                "roommate traits and facility quality, then handle complaint/demand/resign/defect thresholds.");
    }

    /// <summary>
    /// Registers every stub. Convenient for stage 3 development, but it makes
    /// <see cref="AdvanceTick"/> throw until the phases are implemented — so it is not
    /// called by default.
    /// </summary>
    public static TickPipeline CreateStubPipeline(IEventSink events)
    {
        var pipeline = new TickPipeline(events);
        pipeline.Register(new RoomConstructionPhase());
        pipeline.Register(new TrainingPhase());
        pipeline.Register(new RecoveryPhase());
        pipeline.Register(new MissionProgressPhase());
        pipeline.Register(new EventChecksPhase());
        pipeline.Register(new StatusDecayPhase());
        return pipeline;
    }
}
