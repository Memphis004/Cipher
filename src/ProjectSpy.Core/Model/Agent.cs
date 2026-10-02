namespace ProjectSpy.Core;

/// <summary>Stable identifier for an agent.</summary>
public readonly record struct AgentId(int Value) : IComparable<AgentId>
{
    public static readonly AgentId None = new(0);
    public bool IsValid => Value > 0;
    public int CompareTo(AgentId other) => Value.CompareTo(other.Value);
    public override string ToString() => $"A{Value:D4}";
}

/// <summary>
/// An operative: the game's central unit of simulation.
/// </summary>
/// <remarks>
/// A plain mutable class with no Unity types, so it round-trips through the stage-5
/// save format unchanged. Presentation layers read it and never write it.
/// </remarks>
public sealed class Agent
{
    /// <summary>Stamina above this and training yields its full rate.</summary>
    public const int OverworkThreshold = 20;

    /// <summary>Maximum value of either stamina pool.</summary>
    public const int MaxStamina = 100;

    /// <summary>
    /// Stable identity. Settable only by <see cref="WorldState.AddAgent"/> /
    /// <see cref="WorldState.AddRecruit"/>, which own id allocation; a plain `init`
    /// would not let them assign it after construction.
    /// </summary>
    public AgentId Id { get; set; }

    /// <summary>Given name. Displayed via a localization key in stage 8; raw here.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Codename used on missions and in intel reports.</summary>
    public string Codename { get; set; } = string.Empty;

    /// <summary>Foreign key into the agent_class table (stage 2).</summary>
    public int ClassId { get; set; }

    public int Level { get; set; } = 1;

    /// <summary>Experience toward the next level.</summary>
    public int Exp { get; set; }

    public SkillSet Skills { get; set; }

    public int PhysicalStamina { get; set; } = MaxStamina;

    public int MentalStamina { get; set; } = MaxStamina;

    /// <summary>
    /// Raw loyalty, 0-100. The UI only ever receives <see cref="LoyaltyBand"/>.
    /// </summary>
    public int Loyalty { get; set; } = 50;

    /// <summary>Trait ids from trait.csv (stage 2). Includes ids the player has not discovered.</summary>
    public List<int> TraitIds { get; } = new();

    /// <summary>
    /// Hidden trait ids the player has not yet uncovered. Kept separate from
    /// <see cref="TraitIds"/> so no UI query can leak the mole by accident.
    /// </summary>
    public List<int> UndiscoveredTraitIds { get; } = new();

    public AgentStatus Status { get; set; } = AgentStatus.Idle;

    /// <summary>Room the agent is assigned to, if any.</summary>
    public int AssignedRoomId { get; set; }

    /// <summary>Weekly salary commitment.</summary>
    public long SalaryPerWeek { get; set; }

    public Tick HiredOnTick { get; set; }

    public int MissionsCompleted { get; set; }

    // ---- Derived -------------------------------------------------------------

    /// <summary>
    /// The coarse band the UI displays. Thresholds are structural presentation
    /// bands, not balance numbers, so they stay here (knowledge.md rule 3).
    /// </summary>
    public LoyaltyBand LoyaltyBand => Loyalty switch
    {
        >= 75 => LoyaltyBand.Devoted,
        >= 50 => LoyaltyBand.Content,
        >= 25 => LoyaltyBand.Uneasy,
        _ => LoyaltyBand.Resentful,
    };

    /// <summary>True when either stamina pool is too low to train at full rate.</summary>
    public bool IsOverworking => PhysicalStamina < OverworkThreshold || MentalStamina < OverworkThreshold;

    /// <summary>True when the agent can be sent on a mission right now.</summary>
    public bool IsDeployable => Status.IsActive() && !IsOverworking && Status != AgentStatus.Captured;

    /// <summary>True when the agent has any trait, discovered or not.</summary>
    public bool HasTrait(int traitId) => TraitIds.Contains(traitId) || UndiscoveredTraitIds.Contains(traitId);

    /// <summary>True only for traits the player has actually uncovered.</summary>
    public bool HasRevealedTrait(int traitId) => TraitIds.Contains(traitId);

    // ---- Behaviour -----------------------------------------------------------

    /// <summary>
    /// Clamps stamina and loyalty into valid ranges. Called after any mutation so a
    /// scripted effect can never leave the agent in an illegal state.
    /// </summary>
    public void Normalize()
    {
        PhysicalStamina = Math.Clamp(PhysicalStamina, 0, MaxStamina);
        MentalStamina = Math.Clamp(MentalStamina, 0, MaxStamina);
        Loyalty = Math.Clamp(Loyalty, 0, 100);
        Exp = Math.Max(0, Exp);
        if (Level < 1) Level = 1;
    }

    /// <summary>
    /// Moves stamina pools by signed deltas, clamping rather than overflowing.
    /// </summary>
    public void AdjustStamina(int physicalDelta, int mentalDelta)
    {
        PhysicalStamina += physicalDelta;
        MentalStamina += mentalDelta;
        Normalize();
    }

    /// <summary>Nudges loyalty, clamped to 0-100.</summary>
    public void AdjustLoyalty(int delta)
    {
        Loyalty += delta;
        Normalize();
    }

    /// <summary>Awards experience and applies level-ups.</summary>
    /// <remarks>
    /// The exp curve is a stage-2 table (skill_curve.csv). Until that exists this
    /// uses a fixed placeholder curve, which stage 3 replaces with a table lookup —
    /// tracked by the TODO below.
    /// </remarks>
    public void AddExp(int amount)
    {
        if (amount <= 0)
            return;

        Exp += amount;

        // TODO(stage-3): replace with skill_curve.csv lookup (exp_required per level).
        const int expPerLevel = 100;
        while (Exp >= expPerLevel * Level && Level < 20)
        {
            Exp -= expPerLevel * Level;
            Level++;
        }
    }
}
