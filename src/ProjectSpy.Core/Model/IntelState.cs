namespace ProjectSpy.Core;

/// <summary>What an investigation has produced so far.</summary>
public enum InvestigationStatus
{
    /// <summary>Running; no result yet.</summary>
    Running = 0,

    /// <summary>Concluded and the subject was cleared.</summary>
    Cleared = 1,

    /// <summary>Concluded and the subject was found to be the mole.</summary>
    Exposed = 2,

    /// <summary>Concluded with no usable evidence.</summary>
    Inconclusive = 3,
}

/// <summary>How strongly an investigation points at one agent.</summary>
public sealed class InvestigationState
{
    /// <summary>The agent under investigation.</summary>
    public AgentId SubjectId { get; init; }

    /// <summary>Tick the investigation started.</summary>
    public Tick StartedOnTick { get; init; }

    /// <summary>Ticks of work the counter-intel room has actually put in.</summary>
    public int ProgressTicks { get; set; }

    /// <summary>
    /// Evidence accumulated, 0 to 100. Every completed step either raises it or
    /// produces a false lead that lowers it, so the number the player sees is the
    /// honest state of the case rather than a scripted outcome.
    /// </summary>
    public int Evidence { get; set; }

    /// <summary>Number of false leads thrown up so far.</summary>
    public int FalseLeads { get; set; }

    /// <summary>How many investigation steps have completed.</summary>
    public int StepsCompleted { get; set; }

    /// <summary>Set once the investigation concludes.</summary>
    public InvestigationStatus Status { get; set; } = InvestigationStatus.Running;

    /// <summary>True while the investigation is still running.</summary>
    public bool IsRunning => Status == InvestigationStatus.Running;
}

/// <summary>
/// The counter-intelligence desk: running investigations and the evidence they hold.
/// </summary>
/// <remarks>
/// <para>
/// Kept on <see cref="WorldState"/> because it is serializable state that must survive
/// a save and hash into the state fingerprint.
/// </para>
/// <para>
/// The design constraint that matters here is falsifiability: the player must be able
/// to narrow the mole down by watching weekly Heat spikes and comparing them against
/// mission logs. That only works if the mole's Heat contribution is <em>the same</em>
/// every week and attributable, and if investigations give graded evidence rather
/// than a single hidden roll. <see cref="InvestigationState.Evidence"/> is therefore
/// public and monotonic-ish, and the leak log is a separate record — see
/// <c>docs/MOLE_DESIGN.md</c>.
/// </para>
/// </remarks>
public sealed class CounterIntelState
{
    /// <summary>Creates an empty desk.</summary>
    public CounterIntelState()
    {
        Investigations = new List<InvestigationState>();
        LeakLog = new List<MoleLeak>();
    }

    /// <summary>Every investigation the desk has opened, running or concluded.</summary>
    public List<InvestigationState> Investigations { get; }

    /// <summary>
    /// Missions a mole has tipped the enemy off about. Appended once per leak so the
    /// player can correlate the spike with the mission that followed it.
    /// </summary>
    public List<MoleLeak> LeakLog { get; }

    /// <summary>How many moles have been conclusively exposed this run.</summary>
    public int ExposedCount { get; set; }

    /// <summary>How many times the player accused an innocent agent.</summary>
    public int WrongAccusations { get; set; }

    /// <summary>The running investigation for an agent, or null.</summary>
    public InvestigationState? FindFor(AgentId id)
    {
        foreach (InvestigationState investigation in Investigations)
        {
            if (investigation.SubjectId == id)
                return investigation;
        }

        return null;
    }

    /// <summary>The strongest completed evidence against any agent, by evidence value.</summary>
    public int PeakEvidence()
    {
        int peak = 0;
        foreach (InvestigationState investigation in Investigations)
            peak = Math.Max(peak, investigation.Evidence);

        return peak;
    }
}

/// <summary>
/// A record that a mission was compromised before it ran.
/// </summary>
/// <remarks>
/// <para>
/// The mole does not have to be caught for it to hurt. A leak means the mission was
/// harder than it looked, and this record is what lets a player connect "Heat jumped
/// on Tuesday" to "that mission got harder" to "who was home on Tuesday".
/// </para>
/// <para>
/// <paramref name="IsPublic"/> is what makes the system falsifiable: an unattributed
/// leak would be noise, but one the player can trace back to a single agent turns Heat
/// into an actual investigative lead rather than an atmosphere.
/// </para>
/// </remarks>
/// <param name="Tick">When the leak happened.</param>
/// <param name="MissionId">The mission that was compromised.</param>
/// <param name="AgentId">
/// The agent responsible. Tracked internally; whether the player is told depends on
/// whether the mole was already exposed.
/// </param>
/// <param name="HeatAdded">Heat added by this leak.</param>
/// <param name="DifficultyBonus">Difficulty added to the mission before it ran.</param>
/// <param name="IsPublic">True once the player can see which agent did it.</param>
public sealed record MoleLeak(
    Tick Tick,
    int MissionId,
    AgentId AgentId,
    int HeatAdded,
    int DifficultyBonus,
    bool IsPublic);