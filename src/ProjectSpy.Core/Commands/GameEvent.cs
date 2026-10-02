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

    // Stage 3. Values are appended; existing values are never renumbered because the
    // integer is written into save and replay files.
    AgentOverworked = 23,
    AgentBurnoutEntered = 24,
    AgentBurnoutRecovered = 25,
    AgentHealed = 26,
    LoyaltyEscalated = 27,
    AgentResigned = 28,
    AgentDefected = 29,
    RecruitPoolRefreshed = 30,
    CandidateGenerated = 31,
    LoanTaken = 32,
    BankruptcyStageChanged = 33,
    EconomyCollapsed = 34,
    InvestigationStarted = 35,
    InvestigationProgressed = 36,
    InvestigationConcluded = 37,
    AgentAccused = 38,
    AgentExposedAsMole = 39,
    AgentCleared = 40,
    MissionCompromised = 41,
    ContractOfferRefreshed = 42,
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

/// <summary>
/// A room finished being built.
/// </summary>
/// <remarks>
/// Carries the layer and slot count rather than a grid position, because Core has no
/// coordinates (knowledge.md rule 10). Presentation looks the room up in the layout
/// when it needs to draw it.
/// </remarks>
public sealed record RoomBuilt(Tick Tick, RoomId RoomId, int TypeId, int Layer, int SlotCount, long CostPaid)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RoomBuilt;
}

public sealed record RoomDemolished(Tick Tick, RoomId RoomId, int TypeId, int Refund) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RoomDemolished;
}

/// <summary>
/// Two rooms merged into one.
/// </summary>
/// <remarks>
/// Reports the surviving room's layer and slot count rather than a grid position,
/// because Core has no coordinates (knowledge.md rule 10).
/// </remarks>
public sealed record RoomMerged(Tick Tick, RoomId RoomId, int Layer, int SlotCount) : GameEvent(Tick)
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

// ---- stage 3: training, recovery and morale ---------------------------------

/// <summary>
/// An agent trained below the stamina threshold. The event exists so the player sees
/// the cost of overworking rather than inferring it from a loyalty band.
/// </summary>
public sealed record AgentOverworked(Tick Tick, AgentId AgentId, int PhysicalStamina, int MentalStamina)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentOverworked;
}

/// <summary>Mental hit zero and the agent burnt out.</summary>
public sealed record AgentBurnoutEntered(Tick Tick, AgentId AgentId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentBurnoutEntered;
}

/// <summary>Burnout lifted after enough dedicated rest.</summary>
public sealed record AgentBurnoutRecovered(Tick Tick, AgentId AgentId, int TicksOfRest) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentBurnoutRecovered;
}

/// <summary>An injury cleared in the infirmary.</summary>
public sealed record AgentHealed(Tick Tick, AgentId AgentId, int RemainingSeverity) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentHealed;
}

/// <summary>
/// An agent's loyalty crossed a threshold and produced an incident.
/// </summary>
/// <remarks>
/// Carries the enum and the coarse band, never a sentence and never the raw loyalty
/// number. The band is what the UI is allowed to display (knowledge.md rule 4).
/// </remarks>
public sealed record LoyaltyEscalated(Tick Tick, AgentId AgentId, LoyaltyEscalation Escalation, LoyaltyBand Band)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.LoyaltyEscalated;
}

/// <summary>An agent left of their own accord.</summary>
public sealed record AgentResigned(Tick Tick, AgentId AgentId, LoyaltyEscalation Cause) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentResigned;
}

/// <summary>An agent defected to a rival, taking what they knew with them.</summary>
public sealed record AgentDefected(Tick Tick, AgentId AgentId, int IntelLost) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentDefected;
}

// ---- stage 3: recruitment ---------------------------------------------------

/// <summary>The candidate pool was regenerated.</summary>
public sealed record RecruitPoolRefreshed(Tick Tick, int PoolSize, int HrLevel, int ReputationTier)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.RecruitPoolRefreshed;
}

/// <summary>A new candidate appeared. Hidden traits exist but are not disclosed here.</summary>
public sealed record CandidateGenerated(Tick Tick, AgentId RecruitId, int ClassId, int SalaryPerWeek)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.CandidateGenerated;
}

// ---- stage 3: economy -------------------------------------------------------

/// <summary>A loan was taken out.</summary>
public sealed record LoanTaken(Tick Tick, int TierId, long Amount, int WeeklyInterestPercent) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.LoanTaken;
}

/// <summary>The bankruptcy ladder moved to a new penalty stage.</summary>
public sealed record BankruptcyStageChanged(Tick Tick, int Stage, int DaysInDeficit, EconomyStatus Status)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.BankruptcyStageChanged;
}

/// <summary>The grace period and every penalty stage are spent. The run is over.</summary>
public sealed record EconomyCollapsed(Tick Tick, int DaysInDeficit) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.EconomyCollapsed;
}

// ---- stage 3: counter-intelligence ------------------------------------------

/// <summary>An investigation opened against an agent.</summary>
public sealed record InvestigationStarted(Tick Tick, AgentId SubjectId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.InvestigationStarted;
}

/// <summary>An investigation step produced evidence, or a false lead.</summary>
public sealed record InvestigationProgressed(Tick Tick, AgentId SubjectId, int Evidence, bool GainedEvidence)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.InvestigationProgressed;
}

/// <summary>An investigation concluded.</summary>
public sealed record InvestigationConcluded(Tick Tick, AgentId SubjectId, InvestigationStatus Status, int Evidence)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.InvestigationConcluded;
}

/// <summary>The player named an agent as the mole.</summary>
public sealed record AgentAccused(Tick Tick, AgentId AgentId, int Evidence, bool WasCorrect) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentAccused;
}

/// <summary>An accusation landed: the agent's hidden traits are now public.</summary>
public sealed record AgentExposedAsMole(Tick Tick, AgentId AgentId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentExposedAsMole;
}

/// <summary>An accusation missed and the agent was cleared.</summary>
public sealed record AgentCleared(Tick Tick, AgentId AgentId) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.AgentCleared;
}

/// <summary>A mole tipped the enemy off before a mission ran.</summary>
public sealed record MissionCompromised(Tick Tick, int MissionId, int DifficultyBonus, int HeatAdded, bool IsPublic)
    : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.MissionCompromised;
}

/// <summary>The set of offered contracts was regenerated for the week.</summary>
public sealed record ContractOfferRefreshed(Tick Tick, int OfferCount) : GameEvent(Tick)
{
    public override GameEventKind Kind => GameEventKind.ContractOfferRefreshed;
}
