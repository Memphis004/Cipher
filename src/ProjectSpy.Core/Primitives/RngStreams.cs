namespace ProjectSpy.Core;

/// <summary>
/// The named random streams. Each subsystem draws from its own generator so that
/// adding a roll in one system cannot shift the sequence in another.
/// </summary>
/// <remarks>
/// <para>
/// Without this, every system sharing one generator means a single extra
/// <c>NextRoll100</c> in, say, trait generation silently changes every mission
/// map afterwards — which invalidates saved replays and makes bugs nearly
/// impossible to bisect.
/// </para>
/// <para>Stream state is captured by value so a save can restore it exactly.</para>
/// </remarks>
public sealed class RngStreams
{
    /// <summary>The five streams, in a fixed order.</summary>
    public enum StreamKind
    {
        /// <summary>Ambient world simulation: weather, heat drift, world events.</summary>
        World = 0,

        /// <summary>Mission map generation and mission resolution.</summary>
        Mission = 1,

        /// <summary>Story and narrative event selection.</summary>
        Event = 2,

        /// <summary>Candidate generation and recruitment.</summary>
        Recruit = 3,

        /// <summary>Trait assignment and hidden-trait rolls.</summary>
        Trait = 4,
    }

    /// <summary>Number of independent streams. Keep in sync with <see cref="StreamKind"/>.</summary>
    public const int StreamCount = 5;

    // Fixed ordinal layout so serialized state cannot drift when a member is added.
    private readonly XorShift128Rng[] _streams;

    /// <summary>Derives every stream from one root seed.</summary>
    public RngStreams(ulong rootSeed)
    {
        _streams = new XorShift128Rng[StreamCount];
        for (int i = 0; i < StreamCount; i++)
            _streams[i] = new XorShift128Rng(DeriveSeed(rootSeed, i));
    }

    private RngStreams(XorShift128Rng[] streams) => _streams = streams;

    /// <summary>The root seed these streams were derived from.</summary>
    public ulong RootSeed { get; private set; }

    /// <summary>Gets one stream by kind.</summary>
    public IRng this[StreamKind kind] => _streams[(int)kind];

    /// <summary>
    /// Mixes a stream index into the root seed. Distinct streams therefore start
    /// from well-separated states rather than from the same seed.
    /// </summary>
    public static ulong DeriveSeed(ulong rootSeed, int index)
    {
        unchecked
        {
            ulong z = rootSeed + (0x9E3779B97F4A7C15UL * (ulong)(index + 1));
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Captures the full serializable state of every stream.</summary>
    public RngStreamsState SaveState()
    {
        var states = new RngState[StreamCount];
        for (int i = 0; i < StreamCount; i++)
            states[i] = _streams[i].SaveState();

        return new RngStreamsState(RootSeed, states);
    }

    /// <summary>Restores every stream from a previously captured state.</summary>
    public void LoadState(RngStreamsState state)
    {
        if (state.Streams is null || state.Streams.Length != StreamCount)
            throw new ArgumentException(
                $"Expected {StreamCount} stream states, got {state.Streams?.Length ?? 0}.",
                nameof(state));

        for (int i = 0; i < StreamCount; i++)
            _streams[i].LoadState(state.Streams[i]);

        RootSeed = state.RootSeed;
    }

    /// <summary>
    /// Builds a set of streams directly from saved state, skipping derivation.
    /// </summary>
    public static RngStreams FromState(RngStreamsState state)
    {
        if (state.Streams is null || state.Streams.Length != StreamCount)
            throw new ArgumentException(
                $"Expected {StreamCount} stream states, got {state.Streams?.Length ?? 0}.",
                nameof(state));

        var rngs = new XorShift128Rng[StreamCount];
        for (int i = 0; i < StreamCount; i++)
            rngs[i] = new XorShift128Rng(state.Streams[i]);

        return new RngStreams(rngs) { RootSeed = state.RootSeed };
    }
}

/// <summary>Serializable snapshot of every named stream.</summary>
public sealed record RngStreamsState(ulong RootSeed, RngState[] Streams);
