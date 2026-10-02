namespace ProjectSpy.Core;

/// <summary>
/// The five core skills, held as ints.
/// </summary>
/// <remarks>
/// A readonly struct rather than five loose fields so that an agent's skill vector
/// can be passed around, compared and summed as one value. Ints throughout to keep
/// rule paths free of float arithmetic (knowledge.md rule 6).
/// </remarks>
public readonly record struct SkillSet(
    int Infiltration,
    int Combat,
    int Tech,
    int Social,
    int Nerve)
{
    /// <summary>All skills zero.</summary>
    public static readonly SkillSet Zero = default;

    /// <summary>The five skills in a fixed order, for iteration and table lookups.</summary>
    public static readonly SkillKind[] Kinds =
    {
        SkillKind.Infiltration,
        SkillKind.Combat,
        SkillKind.Tech,
        SkillKind.Social,
        SkillKind.Nerve,
    };

    /// <summary>Reads one skill by kind.</summary>
    public int this[SkillKind kind] => kind switch
    {
        SkillKind.Infiltration => Infiltration,
        SkillKind.Combat => Combat,
        SkillKind.Tech => Tech,
        SkillKind.Social => Social,
        SkillKind.Nerve => Nerve,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown skill."),
    };

    /// <summary>Returns a copy with one skill replaced.</summary>
    public SkillSet With(SkillKind kind, int value) => kind switch
    {
        SkillKind.Infiltration => this with { Infiltration = value },
        SkillKind.Combat => this with { Combat = value },
        SkillKind.Tech => this with { Tech = value },
        SkillKind.Social => this with { Social = value },
        SkillKind.Nerve => this with { Nerve = value },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown skill."),
    };

    /// <summary>Component-wise sum.</summary>
    public static SkillSet operator +(SkillSet a, SkillSet b) => new(
        a.Infiltration + b.Infiltration,
        a.Combat + b.Combat,
        a.Tech + b.Tech,
        a.Social + b.Social,
        a.Nerve + b.Nerve);

    /// <summary>Component-wise sum clamped at zero — a penalty cannot push a skill negative.</summary>
    public static SkillSet ClampNonNegative(SkillSet s) => new(
        Math.Max(0, s.Infiltration),
        Math.Max(0, s.Combat),
        Math.Max(0, s.Tech),
        Math.Max(0, s.Social),
        Math.Max(0, s.Nerve));

    /// <summary>Mean of the five skills, rounded down. Used for "average skill" checks.</summary>
    public int Average() => (Infiltration + Combat + Tech + Social + Nerve) / Kinds.Length;

    /// <summary>The highest single skill value.</summary>
    public int Highest() => Math.Max(Math.Max(Infiltration, Combat), Math.Max(Tech, Math.Max(Social, Nerve)));

    /// <summary>The kind with the highest value; ties resolve in <see cref="Kinds"/> order.</summary>
    public SkillKind HighestKind()
    {
        SkillKind best = SkillKind.Infiltration;
        int bestValue = Infiltration;

        foreach (SkillKind kind in Kinds)
        {
            int value = this[kind];
            if (value > bestValue)
            {
                best = kind;
                bestValue = value;
            }
        }

        return best;
    }
}

/// <summary>Identifies one of the five skills.</summary>
public enum SkillKind
{
    Infiltration = 0,
    Combat = 1,
    Tech = 2,
    Social = 3,
    Nerve = 4,
}

/// <summary>
/// Coarse band shown to the player instead of the raw loyalty number
/// (knowledge.md rule 4: no numbers the player must read as prose).
/// </summary>
public enum LoyaltyBand
{
    Devoted = 0,
    Content = 1,
    Uneasy = 2,
    Resentful = 3,
}

/// <summary>What an agent is currently doing.</summary>
public enum AgentStatus
{
    /// <summary>At the base with nothing scheduled.</summary>
    Idle = 0,

    /// <summary>Assigned to a training room.</summary>
    Training = 1,

    /// <summary>Assigned to a rest or recovery room.</summary>
    Resting = 2,

    /// <summary>In the infirmary healing an injury.</summary>
    Recovering = 3,

    /// <summary>Deployed on a mission.</summary>
    OnMission = 4,

    /// <summary>Held by the enemy; a rescue countdown is running.</summary>
    Captured = 5,

    /// <summary>Dead. Permanent, and the reason permadeath has teeth.</summary>
    Dead = 6,

    /// <summary>Left the agency voluntarily.</summary>
    Retired = 7,
}

public static class AgentStatusExtensions
{
    /// <summary>True when the agent still takes part in the simulation.</summary>
    public static bool IsActive(this AgentStatus status)
        => status is not (AgentStatus.Dead or AgentStatus.Retired);

    /// <summary>True when the agent cannot be assigned to a room or a mission.</summary>
    public static bool IsUnavailable(this AgentStatus status)
        => status is AgentStatus.OnMission or AgentStatus.Captured or AgentStatus.Dead or AgentStatus.Retired;
}
