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

    /// <summary>Maximum injury severity. A structural ceiling on the 0-100 scale.</summary>
    public const int MaxInjurySeverity = 100;

    /// <summary>
    /// Loyalty a new hire starts at. Midpoint of the 0-100 scale — neutral, not
    /// already grateful. This is structural (it defines the middle of the loyalty
    /// scale) rather than a designer-tuned rate, so it stays in Core.
    /// </summary>
    public const int StartingLoyalty = 50;

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
    public int Loyalty { get; set; } = StartingLoyalty;

    /// <summary>Trait ids from trait.csv (stage 2). Includes ids the player has not discovered.</summary>
    public List<int> TraitIds { get; } = new();

    /// <summary>
    /// Hidden trait ids the player has not yet uncovered. Kept separate from
    /// <see cref="TraitIds"/> so no UI query can leak the mole by accident.
    /// </summary>
    public List<int> UndiscoveredTraitIds { get; } = new();

    /// <summary>
    /// Injury severity, 0 (fit) to 100 (critical). Cleared by the infirmary over
    /// several ticks at a rate that depends on severity.
    /// </summary>
    public int InjurySeverity { get; set; }

    /// <summary>
    /// True while the agent is burnt out. Entered when Mental hits zero and only
    /// lifted by dedicated rest, so overworking is expensive to undo.
    /// </summary>
    public bool IsBurntOut { get; set; }

    /// <summary>Ticks of dedicated rest accrued since burnout began.</summary>
    public int BurnoutRecoveryTicks { get; set; }

    /// <summary>
    /// Ticks until this agent's next loyalty incident check is allowed. Set after a
    /// complaint/raise/resignation so one bad week cannot emit four in a row.
    /// </summary>
    public int LoyaltyEscalationCooldown { get; set; }

    public AgentStatus Status { get; set; } = AgentStatus.Idle;

    /// <summary>
    /// The most recent escalation this agent triggered, kept so Presentation can
    /// show a running history without Core logging prose.
    /// </summary>
    public LoyaltyEscalation LastEscalation { get; set; } = LoyaltyEscalation.None;

    /// <summary>Room the agent is assigned to, if any.</summary>
    public int AssignedRoomId { get; set; }

    /// <summary>Weekly salary commitment.</summary>
    public long SalaryPerWeek { get; set; }

    public Tick HiredOnTick { get; set; }

    public int MissionsCompleted { get; set; }

    // ---- Derived -------------------------------------------------------------

    /// <summary>
    /// The coarse band the UI displays. This is the only loyalty-derived value
    /// Presentation is allowed to see; the raw number stays inside Core.
    /// </summary>
    /// <remarks>
    /// Thresholds come from <c>morale_band.csv</c> so the bands a designer sees in the
    /// table are the bands the game uses. See <see cref="LoyaltySystem.BandFor"/>.
    /// </remarks>
    public LoyaltyBand LoyaltyBand => LoyaltySystem.BandFor(Loyalty);

    /// <summary>True when either stamina pool is too low to train at full rate.</summary>
    public bool IsOverworking => PhysicalStamina < OverworkThreshold || MentalStamina < OverworkThreshold;

    /// <summary>True when the agent can be sent on a mission right now.</summary>
    public bool IsDeployable =>
        Status.IsActive() && !IsOverworking && !IsBurntOut && Status != AgentStatus.Captured;

    /// <summary>
    /// True when the agent will refuse a new assignment. Burnout is the one status
    /// that overrides availability, which is what makes it a real punishment rather
    /// than a flavour label.
    /// </summary>
    public bool RefusesAssignment => IsBurntOut || !Status.IsActive();

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
        InjurySeverity = Math.Clamp(InjurySeverity, 0, MaxInjurySeverity);
        if (BurnoutRecoveryTicks < 0) BurnoutRecoveryTicks = 0;
        if (LoyaltyEscalationCooldown < 0) LoyaltyEscalationCooldown = 0;
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

        while (Level < SimulationRules.MaxLevel)
        {
            int required = SimulationRules.ExpRequiredForLevel(Level);
            if (required <= 0 || Exp < required)
                break;

            Exp -= required;
            Level++;
        }
    }

    /// <summary>
    /// Awards training progress toward a single skill, respecting that skill's cap.
    /// </summary>
    /// <remarks>
    /// Progress on a skill already at its hard cap is discarded rather than banked.
    /// Banking it would let a capped agent sit on an unspent pool and cash it in if
    /// the cap were ever raised — hidden state that makes balance impossible to
    /// reason about from the outside.
    /// </remarks>
    public int AddSkillExp(SkillKind skill, int amount)
    {
        if (amount <= 0)
            return 0;

        int before = Skills[skill];
        int cap = SimulationRules.HardCapFor(skill);

        if (cap > 0 && before >= cap)
            return 0;

        // Clamp to the cap rather than adding then trimming: adding first would let the
        // stored value overshoot the cap, and the overshoot would then be "spent"
        // against a future cap increase — hidden state that no UI could explain.
        int target = before + amount;
        if (cap > 0 && target > cap)
            target = cap;

        Skills = SkillSet.ClampNonNegative(Skills.With(skill, target));
        return Skills[skill] - before;
    }
}

/// <summary>
/// How far a loyalty problem has escalated. Presentation maps these to localized
/// text; Core never writes the sentence itself (knowledge.md rule 4).
/// </summary>
public enum LoyaltyEscalation
{
    None = 0,
    Complaint = 1,
    RaiseDemand = 2,
    ResignationNotice = 3,
    Defection = 4,
}
