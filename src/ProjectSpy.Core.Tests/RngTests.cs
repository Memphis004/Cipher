using ProjectSpy.Core;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the RNG is reproducible from a seed, that its draws are in range, and that
/// the named streams do not interfere with one another.
/// </summary>
public class RngTests
{
    private static int[] DrawSequence(ulong seed, int count)
    {
        var rng = new XorShift128Rng(seed);
        var result = new int[count];
        for (int i = 0; i < count; i++)
            result[i] = rng.NextInt(0, 1000);
        return result;
    }

    [Fact]
    public void SameSeed_ProducesIdenticalSequence()
    {
        int[] a = DrawSequence(12345, 500);
        int[] b = DrawSequence(12345, 500);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        int[] a = DrawSequence(1, 200);
        int[] b = DrawSequence(2, 200);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AdjacentSeeds_AreNotVisiblyCorrelated()
    {
        // Guards against a weak seeding routine: 1,2,3... must not open with the
        // same handful of values, which is the classic symptom of a poor mixer.
        var openings = new List<int>();
        for (ulong seed = 1; seed <= 8; seed++)
            openings.Add(new XorShift128Rng(seed).NextInt(0, 1_000_000));

        Assert.True(
            openings.Distinct().Count() > 6,
            $"Adjacent seeds produced suspiciously similar openings: {string.Join(",", openings)}");
    }

    [Fact]
    public void SaveState_ThenLoadState_ContinuesTheSameSequence()
    {
        var rng = new XorShift128Rng(999);
        for (int i = 0; i < 37; i++)
            rng.NextInt(0, 1000);

        RngState snapshot = rng.SaveState();

        int[] expected = new int[100];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = rng.NextInt(0, 1000);

        // A second generator restored from the snapshot must continue identically.
        var restored = new XorShift128Rng(1); // deliberately a different seed
        restored.LoadState(snapshot);

        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], restored.NextInt(0, 1000));
    }

    [Fact]
    public void NextInt_StaysWithinBounds()
    {
        var rng = new XorShift128Rng(4242);

        for (int i = 0; i < 10_000; i++)
        {
            int value = rng.NextInt(-5, 5);
            Assert.InRange(value, -5, 4);
        }
    }

    [Fact]
    public void NextInt_RejectsEmptyRange()
    {
        var rng = new XorShift128Rng(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(6, 5));
    }

    [Fact]
    public void NextRoll100_IsInOneToOneHundredAndRoughlyUniform()
    {
        var rng = new XorShift128Rng(7);
        var counts = new int[101];

        const int rolls = 100_000;
        for (int i = 0; i < rolls; i++)
        {
            int roll = rng.NextRoll100();
            Assert.InRange(roll, 1, 100);
            counts[roll]++;
        }

        // Expected 1000 per face. A 40% band is wide enough to ignore sampling noise
        // but tight enough to catch a broken or biased generator.
        for (int face = 1; face <= 100; face++)
        {
            Assert.InRange(counts[face], 600, 1400);
        }
    }

    [Fact]
    public void Pick_ReturnsOnlyMembersOfTheList()
    {
        var rng = new XorShift128Rng(31337);
        string[] items = { "a", "b", "c" };

        for (int i = 0; i < 1_000; i++)
            Assert.Contains(rng.Pick(items), items);
    }

    [Fact]
    public void Pick_RejectsEmptyList()
    {
        var rng = new XorShift128Rng(1);
        Assert.Throws<ArgumentException>(() => rng.Pick(Array.Empty<string>()));
    }

    [Fact]
    public void WeightedPick_RespectsWeights()
    {
        var rng = new XorShift128Rng(2024);
        string[] items = { "common", "rare" };
        int[] weights = { 99, 1 };

        int rare = 0;
        const int rolls = 100_000;
        for (int i = 0; i < rolls; i++)
        {
            if (rng.WeightedPick(items, weights) == "rare")
                rare++;
        }

        // 1% expected. Generous bounds so this does not flake on a slow CI box.
        Assert.InRange(rare, rolls / 200, rolls / 50);
    }

    [Fact]
    public void WeightedPick_ZeroWeightIsNeverChosen()
    {
        var rng = new XorShift128Rng(5);
        string[] items = { "never", "always" };
        int[] weights = { 0, 10 };

        for (int i = 0; i < 5_000; i++)
            Assert.Equal("always", rng.WeightedPick(items, weights));
    }

    [Fact]
    public void WeightedPick_RejectsMismatchedOrZeroTotal()
    {
        var rng = new XorShift128Rng(1);
        string[] items = { "a", "b" };

        Assert.Throws<ArgumentException>(() => rng.WeightedPick(items, new[] { 1 }));
        Assert.Throws<ArgumentException>(() => rng.WeightedPick(items, new[] { 0, 0 }));
        Assert.Throws<ArgumentException>(() => rng.WeightedPick(items, new[] { 1, -1 }));
    }

    [Fact]
    public void LoadState_RejectsDegenerateAllZeroState()
    {
        var rng = new XorShift128Rng(1);
        Assert.Throws<ArgumentException>(() => rng.LoadState(new RngState(0, 0, 0, 0)));
    }

    // ---- Stream independence -------------------------------------------------

    [Fact]
    public void Streams_FromTheSameRootSeed_ProduceDifferentSequences()
    {
        var streams = new RngStreams(555);

        int[] world = DrawMany(streams[RngStreams.StreamKind.World], 200);
        int[] mission = DrawMany(streams[RngStreams.StreamKind.Mission], 200);
        int[] recruit = DrawMany(streams[RngStreams.StreamKind.Recruit], 200);

        // Consuming one stream must not make another replay a duplicate of it.
        Assert.NotEqual(world, mission);
        Assert.NotEqual(world, recruit);
        Assert.NotEqual(mission, recruit);
    }

    [Fact]
    public void EveryStreamIsDistinctFromEveryOther()
    {
        // The pairwise check above covers three streams by hand. With nine of them the
        // interesting failure is two streams nobody compared — so compare all of them.
        var streams = new RngStreams(90210);
        var sequences = new Dictionary<RngStreams.StreamKind, int[]>();

        foreach (RngStreams.StreamKind kind in Enum.GetValues<RngStreams.StreamKind>())
            sequences[kind] = DrawMany(streams[kind], 64);

        var kinds = new List<RngStreams.StreamKind>(sequences.Keys);
        for (int i = 0; i < kinds.Count; i++)
        {
            for (int j = i + 1; j < kinds.Count; j++)
            {
                Assert.NotEqual(
                    sequences[kinds[i]],
                    sequences[kinds[j]]);
            }
        }
    }

    [Fact]
    public void AddingARollToAnyStreamDoesNotShiftAnyOther()
    {
        // The general form of the independence guarantee, across all nine streams
        // rather than the one hand-written pair. Burn randomness through one stream and
        // every other stream must produce byte-identical output afterwards — this is
        // what lets a designer add a roll in, say, the GOAP planner without
        // invalidating every recorded replay.
        foreach (RngStreams.StreamKind disturbed in Enum.GetValues<RngStreams.StreamKind>())
        {
            var control = new RngStreams(31337);
            var baseline = new Dictionary<RngStreams.StreamKind, int[]>();
            foreach (RngStreams.StreamKind kind in Enum.GetValues<RngStreams.StreamKind>())
                baseline[kind] = DrawMany(control[kind], 40);

            var trial = new RngStreams(31337);
            for (int i = 0; i < 50_000; i++)
                trial[disturbed].NextRoll100();

            foreach (RngStreams.StreamKind kind in Enum.GetValues<RngStreams.StreamKind>())
            {
                // The disturbed stream itself is supposed to have moved; every other
                // one must not have.
                if (kind == disturbed)
                    continue;

                Assert.Equal(
                    baseline[kind],
                    DrawMany(trial[kind], 40));
            }
        }
    }

    [Fact]
    public void TheTacticalStreamsExistAndAreIndependent()
    {
        // Named explicitly because these four arrived with the tactical layer and a
        // future refactor that folds Tactical back into Mission would pass every other
        // test here while quietly breaking mid-mission saves.
        var streams = new RngStreams(1);

        Assert.NotNull(streams[RngStreams.StreamKind.Tactical]);
        Assert.NotNull(streams[RngStreams.StreamKind.Goap]);
        Assert.NotNull(streams[RngStreams.StreamKind.Sleeper]);
        Assert.NotNull(streams[RngStreams.StreamKind.Loot]);

        int[] tactical = DrawMany(streams[RngStreams.StreamKind.Tactical], 50);
        int[] goap = DrawMany(streams[RngStreams.StreamKind.Goap], 50);
        int[] sleeper = DrawMany(streams[RngStreams.StreamKind.Sleeper], 50);
        int[] loot = DrawMany(streams[RngStreams.StreamKind.Loot], 50);
        int[] mission = DrawMany(streams[RngStreams.StreamKind.Mission], 50);

        Assert.NotEqual(tactical, mission);
        Assert.NotEqual(tactical, goap);
        Assert.NotEqual(goap, sleeper);
        Assert.NotEqual(sleeper, loot);
    }

    [Fact]
    public void StreamCountMatchesTheEnum()
    {
        // If these ever disagree, LoadState starts rejecting its own saves and the
        // failure surfaces as "corrupt save" rather than as a build error.
        Assert.Equal(RngStreams.StreamCount, Enum.GetValues<RngStreams.StreamKind>().Length);
    }

    [Fact]
    public void ExistingStreamOrdinalsDidNotMoveWhenNewStreamsWereAdded()
    {
        // Saved stream state is positional. Appending Tactical/Goap/Sleeper/Loot is
        // only safe because World..Trait kept the values they had; a save written
        // before this change still restores into the right generators.
        Assert.Equal(0, (int)RngStreams.StreamKind.World);
        Assert.Equal(1, (int)RngStreams.StreamKind.Mission);
        Assert.Equal(2, (int)RngStreams.StreamKind.Event);
        Assert.Equal(3, (int)RngStreams.StreamKind.Recruit);
        Assert.Equal(4, (int)RngStreams.StreamKind.Trait);
        Assert.Equal(5, (int)RngStreams.StreamKind.Tactical);
        Assert.Equal(6, (int)RngStreams.StreamKind.Goap);
        Assert.Equal(7, (int)RngStreams.StreamKind.Sleeper);
        Assert.Equal(8, (int)RngStreams.StreamKind.Loot);
    }

    [Fact]
    public void Streams_SameRootSeed_AreReproducible()
    {
        var first = new RngStreams(777);
        var second = new RngStreams(777);

        foreach (RngStreams.StreamKind kind in Enum.GetValues<RngStreams.StreamKind>())
        {
            Assert.Equal(
                DrawMany(first[kind], 100),
                DrawMany(second[kind], 100));
        }
    }

    [Fact]
    public void ConsumingOneStream_DoesNotShiftAnother()
    {
        // The whole point of named streams: burn a lot of randomness through the
        // Event stream, then confirm the Mission stream is untouched.
        var control = new RngStreams(31337);
        int[] missionBefore = DrawMany(control[RngStreams.StreamKind.Mission], 50);

        var trial = new RngStreams(31337);
        for (int i = 0; i < 100_000; i++)
            trial[RngStreams.StreamKind.Event].NextRoll100();

        int[] missionAfter = DrawMany(trial[RngStreams.StreamKind.Mission], 50);

        Assert.Equal(missionBefore, missionAfter);
    }

    [Fact]
    public void Streams_SaveAndLoadState_RoundTripsExactly()
    {
        var rng = new RngStreams(4242);
        for (int i = 0; i < 500; i++)
            rng[RngStreams.StreamKind.Mission].NextInt(0, 10_000);

        RngStreamsState state = rng.SaveState();
        RngStreams restored = RngStreams.FromState(state);

        Assert.Equal(state.RootSeed, restored.RootSeed);
        Assert.Equal(RngStreams.StreamCount, state.Streams.Length);
        Assert.Equal(
            DrawMany(rng[RngStreams.StreamKind.Mission], 100),
            DrawMany(restored[RngStreams.StreamKind.Mission], 100));

        // Every stream, not just the one the test happened to exercise.
        foreach (RngStreams.StreamKind kind in Enum.GetValues<RngStreams.StreamKind>())
        {
            Assert.Equal(
                DrawMany(rng[kind], 40),
                DrawMany(restored[kind], 40));
        }
    }

    [Fact]
    public void Streams_DerivedSeeds_AreDistinctPerStream()
    {
        var seeds = new HashSet<ulong>();
        for (int i = 0; i < RngStreams.StreamCount; i++)
            seeds.Add(RngStreams.DeriveSeed(1234, i));

        Assert.Equal(RngStreams.StreamCount, seeds.Count);
    }

    private static int[] DrawMany(IRng rng, int count)
    {
        var result = new int[count];
        for (int i = 0; i < count; i++)
            result[i] = rng.NextInt(0, 1_000_000);
        return result;
    }
}
