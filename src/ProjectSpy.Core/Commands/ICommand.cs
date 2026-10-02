namespace ProjectSpy.Core;

/// <summary>
/// A player intent. The only way anything mutates a <see cref="WorldState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Splitting <see cref="Validate"/> from <see cref="Apply"/> is deliberate: the UI
/// needs to know whether a command is legal <em>before</em> committing to it (a ghost
/// placement preview, an affordability check on a button), and the stage-10 fuzzer
/// needs to be able to hammer <c>Validate</c> without mutating anything.
/// </para>
/// <para>
/// Implementations must be serializable value-like records so that the
/// <see cref="GameSession.CommandLog"/> can be written to a replay file and replayed
/// byte-identically.
/// </para>
/// </remarks>
public interface ICommand
{
    /// <summary>
    /// Checks legality against the current world without mutating it.
    /// </summary>
    /// <remarks>
    /// Must be side-effect free. If it needs randomness for a rejection roll, use
    /// nothing at all — validation either depends only on state or it is not
    /// validation.
    /// </remarks>
    CommandResult Validate(WorldState world);

    /// <summary>
    /// Mutates the world. Only ever called after <see cref="Validate"/> returned Ok,
    /// and only with the same world instance that was validated.
    /// </summary>
    /// <param name="rng">
    /// A stream chosen by the command: pass <c>world.RngStreams[Mission]</c> from a
    /// mission command, never a shared instance.
    /// </param>
    void Apply(WorldState world, IRng rng);
}

/// <summary>
/// The outcome of a command. Carries a reason code and structured args — never a
/// player-facing sentence (knowledge.md rule 4).
/// </summary>
public readonly record struct CommandResult
{
    private CommandResult(bool ok, CommandReason reason, CommandArgs args)
    {
        IsOk = ok;
        Reason = reason;
        Args = args;
    }

    /// <summary>True when the command may be applied.</summary>
    public bool IsOk { get; }

    /// <summary>Why the command was rejected. <see cref="CommandReason.None"/> when ok.</summary>
    public CommandReason Reason { get; }

    /// <summary>
    /// Values the UI interpolates into its localized template for
    /// <see cref="Reason"/> — a room name, a shortfall amount, and so on.
    /// </summary>
    public CommandArgs Args { get; }

    /// <summary>The success result.</summary>
    public static readonly CommandResult Ok = new(true, CommandReason.None, CommandArgs.None);

    /// <summary>Builds a rejection carrying a reason and args.</summary>
    public static CommandResult Rejected(CommandReason reason, CommandArgs args)
    {
        if (reason == CommandReason.None)
            throw new ArgumentException("A rejection must carry a reason.", nameof(reason));

        return new CommandResult(false, reason, args);
    }

    /// <summary>Builds a rejection with no args.</summary>
    public static CommandResult Rejected(CommandReason reason) => Rejected(reason, CommandArgs.None);

    /// <summary>Inverse of <see cref="IsOk"/>.</summary>
    public bool IsRejected => !IsOk;

    /// <summary>
    /// Introspection key the UI uses to look up the localized template, e.g.
    /// <c>command.reject.insufficient_funds</c>. This is a key, not prose.
    /// </summary>
    public string MessageKey => IsOk ? "command.ok" : $"command.reject.{Reason.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Every way a command can be refused. Grows by appending only — never renumber
/// existing members, because the enum value is written into save and replay files.
/// </summary>
public enum CommandReason
{
    None = 0,

    // ---- generic ----
    UnknownAgent = 1,
    UnknownRoom = 2,
    UnknownMission = 3,
    UnknownContract = 4,
    AgentUnavailable = 5,

    // ---- economy ----
    InsufficientFunds = 10,
    InsufficientMaterials = 11,
    InsufficientIntel = 12,
    InsufficientReputation = 13,

    // ---- layout ----
    PlacementOutOfBounds = 20,
    PlacementOverlaps = 21,
    PlacementNonPositiveWidth = 22,
    RoomTypeLocked = 23,
    RoomDepthTooShallow = 24,
    RoomDemolished = 25,

    // ---- roster ----
    RoomAtCapacity = 30,
    AgentAlreadyAssigned = 31,
    AgentNotRecruited = 32,
    AgentDead = 33,

    // ---- mission ----
    NoActiveMission = 40,
    MissionNotOnMission = 41,
    EdgeClosed = 42,
    GadgetUnavailable = 43,
    AlarmTooHigh = 44,

    // ---- contract ----
    ContractExpired = 50,
    ContractAlreadyAccepted = 51,
    ContractAlreadyDispatched = 52,

    // ---- stage 3: economy, counter-intelligence ----
    LoanLimitReached = 60,
    UnknownLoanTier = 61,
    NoOutstandingLoan = 62,
    InvalidAmount = 63,
    InvestigationAlreadyRunning = 64,
    NoCounterIntelCapacity = 65,
    AgentNotOnRoster = 66,
}

/// <summary>
/// Structured values for a rejection message. Presentation substitutes these into
/// its localized template.
/// </summary>
/// <remarks>
/// A fixed set of slots rather than a dictionary: it keeps a command serializable
/// with stable, explicit fields, which is what the stage-5 save format wants.
/// </remarks>
public readonly record struct CommandArgs(
    long Primary = 0,
    long Secondary = 0,
    int Id = 0,
    string Key = "")
{
    public static readonly CommandArgs None = default;

    /// <summary>Shortfall amount — how much more the player needs.</summary>
    public static CommandArgs Amount(long value) => new(Primary: value);

    /// <summary>A shortfall plus what is currently available.</summary>
    public static CommandArgs Shortfall(long needed, long available)
        => new(Primary: needed, Secondary: available);

    /// <summary>A referenced entity id.</summary>
    public static CommandArgs Entity(int id) => new(Id: id);

    /// <summary>A localization key (e.g. a room type's name_key).</summary>
    public static CommandArgs Named(string localizationKey) => new(Key: localizationKey ?? string.Empty);
}
