namespace ProjectSpy.Core;

/// <summary>
/// Something that already happened, published for Presentation to react to.
/// </summary>
/// <remarks>
/// <para>
/// Events are immutable records of facts, published after the state change they
/// describe. Presentation renders from these; it never reads Core state on a frame
/// loop to work out what happened (knowledge.md rule 4 and the stage-8 binding rule).
/// </para>
/// <para>
/// Every event carries <see cref="Tick"/>, so the UI can order and timestamp
/// anything it is told about.
/// </para>
/// </remarks>
public abstract record GameEvent(Tick Tick)
{
    /// <summary>Stable discriminator for switch-based dispatch and for replay logs.</summary>
    public abstract GameEventKind Kind { get; }
}

/// <summary>Discriminators for the event hierarchy.</summary>
public enum GameEventKind
{
    TickAdvanced = 0,
    DayChanged = 1,
    WeekSettled = 2,
    AgentHired = 3,
    AgentStatusChanged = 4,
    AgentInjured = 5,
    AgentTrained = 6,
    AgentRetired = 7,
    AgentDied = 8,
    RoomBuilt = 9,
    RoomDemolished = 10,
    RoomMerged = 11,
    RoomConstructionCompleted = 12,
    ResourcesChanged = 13,
    MissionStarted = 14,
    MissionProgressed = 15,
    MissionCompleted = 16,
    AgentCaptured = 17,
    AgentRescued = 18,
    ContractOffered = 19,
    ContractAccepted = 20,
    CommandRejected = 21,
    FlagChanged = 22,
}

// ---- time ------------------------------------------------------------------

/// <summary>A tick elapsed.</summary>
public sealed record TickAdvanced(Tick Tick, int Day, int HourOfDay) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.TickAdvanced;
}

/// <summary>Midnight passed.</summary>
public sealed record DayChanged(Tick Tick, int Day, int Week) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.DayChanged;
}

/// <summary>
/// Weekly settlement ran: salaries and upkeep were deducted. The totals travel with
/// the event so the top bar can animate the change without re-deriving it.
/// </summary>
public sealed record WeekSettled(Tick Tick, int Week, long SalariesPaid, long UpkeepPaid, long InterestPaid)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.WeekSettled;

    public long TotalPaid => SalariesPaid + UpkeepPaid + InterestPaid;
}

// ---- agents ----------------------------------------------------------------

public sealed record AgentHired(Tick Tick, AgentId AgentId, int ClassId, long SalaryPerWeek) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentHired;
}

public sealed record AgentStatusChanged(Tick Tick, AgentId AgentId, AgentStatus From, AgentStatus To)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentStatusChanged;
}

/// <summary>An agent took damage. <see cref="Severity"/> is 0-100.</summary>
public sealed record AgentInjured(Tick Tick, AgentId AgentId, int PhysicalDamage, int MentalDamage, int Severity)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentInjured;

    public bool IsSerious => Severity >= 50;
}

/// <summary>An agent gained experience in one skill.</summary>
public sealed record AgentTrained(Tick Tick, AgentId AgentId, SkillKind Skill, int GainedExp, int NewTotalExp)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentTrained;
}

public sealed record AgentRetired(Tick Tick, AgentId AgentId, AgentStatus Reason) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentRetired;
}

public sealed record AgentDied(Tick Tick, AgentId AgentId, int MissionId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentDied;
}

// ---- rooms -----------------------------------------------------------------

public sealed record RoomBuilt(Tick Tick, RoomId RoomId, int TypeId, int GridX, int GridY, int Width, long CostPaid)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RoomBuilt;
}

public sealed record RoomDemolished(Tick Tick, RoomId RoomId, int TypeId, int Refund) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RoomDemolished;
}

public sealed record RoomMerged(Tick Tick, RoomId RoomId, int NewWidth, int NewGridX) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RoomMerged;
}

public sealed record RoomConstructionCompleted(Tick Tick, RoomId RoomId, int TypeId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RoomConstructionCompleted;
}

// ---- economy ---------------------------------------------------------------

/// <summary>A resource balance changed. Carries the full new snapshot.</summary>
public sealed record ResourcesChanged(Tick Tick, Resources Before, Resources After) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.ResourcesChanged;

    public long FundsDelta => After.Funds - Before.Funds;
    public int IntelDelta => After.Intel - Before.Intel;
    public int MaterialsDelta => After.Materials - Before.Materials;
    public int ReputationDelta => After.Reputation - Before.Reputation;
    public int HeatDelta => After.Heat - Before.Heat;
}

// ---- missions --------------------------------------------------------------

public sealed record MissionStarted(Tick Tick, int MissionId, int TypeId, IReadOnlyList<AgentId> AgentIds)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.MissionStarted;
}

public sealed record MissionProgressed(Tick Tick, int MissionId, int NodeId, int Alarm) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.MissionProgressed;
}

public sealed record MissionCompleted(Tick Tick, int MissionId, MissionOutcome Outcome, long FundsReward, int IntelReward)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.MissionCompleted;
}

public sealed record AgentCaptured(Tick Tick, AgentId AgentId, int MissionId, int TicksUntilLost) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentCaptured;
}

public sealed record AgentRescued(Tick Tick, AgentId AgentId, int MissionId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentRescued;
}

// ---- contracts and diagnostics ---------------------------------------------

public sealed record ContractOffered(Tick Tick, int ContractId, int TypeId, long RewardFunds, Tick ExpiresOnTick)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.ContractOffered;
}

public sealed record ContractAccepted(Tick Tick, int ContractId, int TypeId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.ContractAccepted;
}

/// <summary>
/// A command was refused. Carries the reason code so the UI can render its own
/// localized explanation — Core does not choose the wording.
/// </summary>
public sealed record CommandRejected(Tick Tick, CommandReason Reason, CommandArgs Args) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.CommandRejected;
}

public sealed record FlagChanged(Tick Tick, string FlagName, bool NewValue) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.FlagChanged;
}
