namespace ProjectSpy.Core;

/// <summary>
/// The agency's five tracked resources.
/// </summary>
/// <remarks>
/// <para>
/// The central rule is that <em>no resource can ever go negative</em>. A plain
/// subtraction that clamps would silently swallow an unaffordable purchase;
/// arithmetic that underflows would poison every downstream total. So every
/// deduction goes through an explicit <c>TrySpend</c> that reports success and
/// leaves the balance untouched on failure.
/// </para>
/// <para>
/// Every deduction returns the new snapshot through an <c>out</c> parameter rather
/// than mutating in place. This record is immutable, so a bool-only overload would
/// compile, appear to work, and silently discard the deduction — the kind of bug that
/// makes the whole economy free.
/// </para>
/// <para>
/// Funds are <c>long</c> because weekly settlement multiplies salaries by up to a
/// few dozen agents and the other fields are <c>int</c> because they are bounded
/// game quantities.
/// </para>
/// </remarks>
public sealed record Resources
{
    /// <summary>Money. Only this field is allowed to be large.</summary>
    public long Funds { get; init; }

    /// <summary>Actionable intelligence.</summary>
    public int Intel { get; init; }

    /// <summary>Build materials.</summary>
    public int Materials { get; init; }

    /// <summary>Standing with clients. Gated the mission tiers that are offered.</summary>
    public int Reputation { get; init; }

    /// <summary>Attention attracted by past work. Drives raid frequency.</summary>
    public int Heat { get; init; }

    /// <summary>Everything zero.</summary>
    public static readonly Resources Empty = new();

    /// <summary>A new agency starting balance.</summary>
    public static readonly Resources Starting = new()
    {
        Funds = 10_000,
        Intel = 50,
        Materials = 100,
        Reputation = 10,
        Heat = 0,
    };

    /// <summary>
    /// True when every field is at or above zero. Asserted by tests and by the
    /// stage-3 365-day simulation run.
    /// </summary>
    public bool IsValid => Funds >= 0 && Intel >= 0 && Materials >= 0 && Reputation >= 0 && Heat >= 0;

    /// <summary>
    /// Deducts funds if affordable.
    /// </summary>
    /// <returns><c>false</c> and no change when <paramref name="amount"/> is not affordable.</returns>
    public bool TrySpendFunds(long amount, out Resources result) => TrySpend(amount, out result);

    /// <summary>Deducts intel if available.</summary>
    public bool TrySpendIntel(int amount, out Resources result) => TrySpend(0, amount, 0, out result);

    /// <summary>Deducts materials if available.</summary>
    public bool TrySpendMaterials(int amount, out Resources result) => TrySpend(0, 0, amount, out result);

    /// <summary>
    /// Attempts to deduct every named amount at once. All-or-nothing: if any one
    /// field is short, nothing is deducted.
    /// </summary>
    /// <remarks>
    /// Atomicity matters because a purchase that costs both funds and materials must
    /// not charge the funds and then fail on the materials.
    /// </remarks>
    public bool TrySpend(long funds, int intel, int materials, out Resources result)
    {
        result = this;

        // Non-positive amounts are rejected rather than silently ignored: a negative
        // "cost" would be a money-printing bug in the caller.
        if (funds < 0 || intel < 0 || materials < 0)
            return false;

        if (Funds < funds || Intel < intel || Materials < materials)
            return false;

        result = this with
        {
            Funds = Funds - funds,
            Intel = Intel - intel,
            Materials = Materials - materials,
        };

        return true;
    }

    /// <summary>
    /// Attempts to deduct funds only.
    /// </summary>
    public bool TrySpend(long amount, out Resources result)
    {
        if (amount < 0)
        {
            result = this;
            return false;
        }

        if (Funds < amount)
        {
            result = this;
            return false;
        }

        result = this with { Funds = Funds - amount };
        return true;
    }

    /// <summary>Adds funds.</summary>
    public Resources EarnFunds(long amount)
        => amount <= 0 ? this : this with { Funds = SaturatingAdd(Funds, amount) };

    /// <summary>Adds intel, clamped at <see cref="int.MaxValue"/>.</summary>
    public Resources AddIntel(int amount)
        => amount <= 0 ? this : this with { Intel = SaturatingAdd(Intel, amount) };

    /// <summary>Adds materials, clamped at <see cref="int.MaxValue"/>.</summary>
    public Resources AddMaterials(int amount)
        => amount <= 0 ? this : this with { Materials = SaturatingAdd(Materials, amount) };

    /// <summary>Adds reputation, clamped at <see cref="int.MaxValue"/>.</summary>
    public Resources AddReputation(int amount)
        => amount <= 0 ? this : this with { Reputation = SaturatingAdd(Reputation, amount) };

    /// <summary>
    /// Adds heat, clamped at zero. Heat is never negative — there is no such thing as
    /// negative attention — so a passive decay that would go below zero clamps.
    /// </summary>
    public Resources AddHeat(int amount)
        => amount <= 0 ? this with { Heat = Math.Max(0, Heat + amount) }
                       : this with { Heat = SaturatingAdd(Heat, amount) };

    /// <summary>Applies a signed heat delta, never dropping below zero.</summary>
    public Resources DecayHeat(int amount) => AddHeat(-Math.Abs(amount));

    private static int SaturatingAdd(int current, int amount)
        => (int)Math.Min((long)current + amount, int.MaxValue);

    /// <summary>Saturating add for funds, so a huge payout cannot wrap to negative.</summary>
    private static long SaturatingAdd(long current, long amount)
        => current > long.MaxValue - amount ? long.MaxValue : current + amount;

    /// <summary>Clamps every field to its legal range.</summary>
    public Resources Normalize() => new()
    {
        Funds = Math.Max(0, Funds),
        Intel = Math.Max(0, Intel),
        Materials = Math.Max(0, Materials),
        Reputation = Math.Max(0, Reputation),
        Heat = Math.Max(0, Heat),
    };

    /// <summary>
    /// Total weekly salary the current roster costs. Used by the stage-3 weekly
    /// settlement and shown in the top bar as the net delta.
    /// </summary>
    /// <remarks>
    /// TODO(stage-3): fold in room upkeep and loan interest once those systems exist.
    /// </remarks>
    public long ProjectedWeeklyCost(IEnumerable<Agent> roster)
    {
        long total = 0;
        foreach (Agent agent in roster)
        {
            if (agent.Status.IsActive())
                total += (long)agent.SalaryPerWeek;
        }

        return total;
    }
}
