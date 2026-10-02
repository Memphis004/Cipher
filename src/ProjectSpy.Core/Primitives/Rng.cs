namespace ProjectSpy.Core;

/// <summary>
/// Deterministic random source. Every roll in the simulation goes through this
/// interface — never <c>System.Random</c> (knowledge.md rule 2).
/// </summary>
/// <remarks>
/// Implementations must be serializable by value: the whole internal state is
/// captured by <see cref="SaveState"/> and restored by <see cref="LoadState"/>,
/// because a save must be able to continue the exact same sequence.
/// </remarks>
public interface IRng
{
    /// <summary>Uniform integer in <c>[minInclusive, maxExclusive)</c>.</summary>
    int NextInt(int minInclusive, int maxExclusive);

    /// <summary>Uniform roll in <c>[1, 100]</c>. The d100 used by every skill check.</summary>
    int NextRoll100();

    /// <summary>Uniform choice from a non-empty list.</summary>
    T Pick<T>(IReadOnlyList<T> items);

    /// <summary>
    /// Choice from <paramref name="items"/> with probability proportional to
    /// <paramref name="weights"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The lists differ in length, or every weight is zero.
    /// </exception>
    T WeightedPick<T>(IReadOnlyList<T> items, IReadOnlyList<int> weights);

    /// <summary>Opaque serializable state, as four 32-bit words.</summary>
    RngState SaveState();

    /// <summary>Restores state previously captured by <see cref="SaveState"/>.</summary>
    void LoadState(RngState state);
}

/// <summary>The four 32-bit words of an xorshift128 generator.</summary>
public readonly record struct RngState(uint S0, uint S1, uint S2, uint S3)
{
    public bool IsAllZero => S0 == 0 && S1 == 0 && S2 == 0 && S3 == 0;
}

/// <summary>
/// xorshift128 (Marsaglia). Fast, tiny, and — critically for this project —
/// reproducible across platforms and across .NET versions, because it uses only
/// exact integer arithmetic.
/// </summary>
/// <remarks>
/// Why not <c>System.Random</c>: its algorithm is an implementation detail that
/// changed between .NET Framework and .NET Core, so a save written by one runtime
/// could not be replayed on another. This generator is frozen here instead.
/// </remarks>
public sealed class XorShift128Rng : IRng
{
    private uint _s0;
    private uint _s1;
    private uint _s2;
    private uint _s3;

    /// <summary>Seeds the generator from a 64-bit value.</summary>
    public XorShift128Rng(ulong seed) => Reseed(seed);

    /// <summary>
    /// Restores a generator directly from saved state, so a loaded save continues
    /// the same sequence.
    /// </summary>
    public XorShift128Rng(RngState state) => LoadState(state);

    /// <summary>The seed this generator was originally constructed with.</summary>
    public ulong Seed { get; private set; }

    /// <summary>
    /// Re-seeds and mixes in a stream tag so that two subsystems given the same
    /// root seed still produce different sequences.
    /// </summary>
    public void Reseed(ulong seed)
    {
        Seed = seed;
        // SplitMix64-style avalanche so that adjacent seeds (1, 2, 3...) do not
        // produce visibly correlated opening rolls.
        ulong z = seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;

        ulong a = Mix(z);
        ulong b = Mix(z ^ 0xD1B54A32D192ED03UL);

        _s0 = (uint)(a & 0xFFFFFFFFUL);
        _s1 = (uint)(a >> 32);
        _s2 = (uint)(b & 0xFFFFFFFFUL);
        _s3 = (uint)(b >> 32);

        // xorshift128 is degenerate at all-zero state; the mixing above makes that
        // effectively impossible, but guard anyway so the invariant is total.
        if (_s0 == 0 && _s1 == 0 && _s2 == 0 && _s3 == 0)
            _s3 = 0x9E3779B9;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Raw 32-bit output. This is the single point all other draws build on.</summary>
    public uint NextUInt()
    {
        uint t = _s3;
        uint s = _s0;
        _s3 = _s2;
        _s2 = _s1;
        _s1 = s;

        t ^= t << 11;
        t ^= t >> 8;

        _s0 = t ^ s ^ (s >> 19);
        return _s0;
    }

    /// <summary>
    /// Unbiased integer below <paramref name="bound"/> using rejection sampling.
    /// Plain modulo would bias the low values whenever the bound does not divide
    /// 2^32, which shows up as suspicious skew over 10,000 mission rolls.
    /// </summary>
    public uint NextUIntBounded(uint bound)
    {
        if (bound == 0)
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "Bound must be positive.");

        // Largest multiple of `bound` that fits in uint; anything at or above it
        // is the biased tail and gets redrawn.
        uint threshold = (uint)((0x100000000UL - bound) % bound);

        while (true)
        {
            uint r = NextUInt();
            if (r >= threshold)
                return r % bound;
        }
    }

    /// <inheritdoc />
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(
                nameof(maxExclusive), maxExclusive,
                $"Require min ({minInclusive}) < max ({maxExclusive}).");

        uint range = (uint)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + NextUIntBounded(range));
    }

    /// <inheritdoc />
    public int NextRoll100() => NextInt(1, 101);

    /// <inheritdoc />
    public T Pick<T>(IReadOnlyList<T> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.", nameof(items));

        return items[NextInt(0, items.Count)];
    }

    /// <inheritdoc />
    public T WeightedPick<T>(IReadOnlyList<T> items, IReadOnlyList<int> weights)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (weights is null) throw new ArgumentNullException(nameof(weights));
        if (items.Count == 0) throw new ArgumentException("Cannot pick from an empty list.", nameof(items));
        if (items.Count != weights.Count)
            throw new ArgumentException(
                $"Item/weight count mismatch: {items.Count} items vs {weights.Count} weights.",
                nameof(weights));

        long total = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] < 0)
                throw new ArgumentException($"Negative weight at index {i}.", nameof(weights));
            total += weights[i];
        }

        if (total <= 0)
            throw new ArgumentException("Total weight must be positive.", nameof(weights));

        // Draw against the long total so that large weight lists cannot overflow.
        int roll = NextInt(0, (int)Math.Min(total, int.MaxValue));
        long cursor = roll;
        for (int i = 0; i < items.Count; i++)
        {
            cursor -= weights[i];
            if (cursor < 0)
                return items[i];
        }

        // Only reachable if the caller mutated the weight list mid-draw.
        throw new InvalidOperationException("WeightedPick could not select an item.");
    }

    /// <inheritdoc />
    public RngState SaveState() => new(_s0, _s1, _s2, _s3);

    /// <inheritdoc />
    public void LoadState(RngState state)
    {
        _s0 = state.S0;
        _s1 = state.S1;
        _s2 = state.S2;
        _s3 = state.S3;

        if (_s0 == 0 && _s1 == 0 && _s2 == 0 && _s3 == 0)
            throw new ArgumentException("An all-zero xorshift state is degenerate.", nameof(state));
    }
}
