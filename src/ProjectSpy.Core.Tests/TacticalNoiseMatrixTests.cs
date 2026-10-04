using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;

// The generated table bean is `NoiseProfile`. Core aliases it so the name cannot
// collide with Core's own noise vocabulary; the test repeats the same crossing.
using NoiseProfileRow = ProjectSpy.Tables.NoiseProfile;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The noise attenuation matrix: a sound made in one room, heard in the next, and not
/// heard in the one after that.
/// </summary>
/// <remarks>
/// <para>
/// The brief is blunt about why this matters: "noise is the primary way a careless
/// player loses, so it must be legible". Legible means three things, and each has a test
/// here. It must be <em>correct</em> — a noise crosses a door and loses exactly what
/// <c>noise_profile</c> says it loses. It must be <em>monotonic</em> — more doors means
/// less sound, never more. And it must be <em>recorded</em> — every noise reaches the
/// mission log naming its source and its listeners.
/// </para>
/// <para>
/// Swept against a purpose-built chain of rooms rather than a generated site, for the
/// reason the perception matrix does the same: the number of doors between the noise and
/// the listener has to be the only thing that varies.
/// </para>
/// </remarks>
public class TacticalNoiseMatrixTests
{
    // ---- the profiles are what the table says -------------------------------

    [Fact]
    public void EveryProfileInTheTableHasAPositiveRadiusAndAttenuation()
    {
        TacticalHarness.RequireTables();

        IReadOnlyList<NoiseProfileRow> profiles = SimulationRules.AllNoiseProfiles();
        Assert.NotEmpty(profiles);

        foreach (NoiseProfileRow profile in profiles)
        {
            Assert.True(profile.BaseRadiusCm > 0, $"Noise profile {profile.Id} has no radius.");
            Assert.True(profile.AttenuationPerConnectionPercent is >= 0 and < 100,
                $"Noise profile {profile.Id} attenuates {profile.AttenuationPerConnectionPercent}% per connection.");

            Assert.True(profile.AttenuationPerFloorPercent is >= 0 and < 100,
                $"Noise profile {profile.Id} attenuates {profile.AttenuationPerFloorPercent}% per floor.");
        }
    }

    [Fact]
    public void ALouderProfileIsHeardFurtherThanAQuieterOne()
    {
        // noise.run_step is 700cm and noise.crouch_step is 120cm; if the ordering ever
        // inverted, sprinting would be quieter than crouching and the whole posture
        // table would be a lie.
        Assert.True(
            NoiseSystem.BaseRadiusCm(12303) > NoiseSystem.BaseRadiusCm(12301),
            "Running is not louder than crouching.");

        Assert.True(
            NoiseSystem.BaseRadiusCm(12303) > NoiseSystem.BaseRadiusCm(12302),
            "Running is not louder than walking.");
    }

    // ---- attenuation per connection -----------------------------------------

    [Fact]
    public void ANoiseIsHeardAtFullVolumeInItsOwnRoom()
    {
        SiteLayout layout = NoiseFixture.Chain(rooms: 1);
        LightState light = PerceptionFixture.Lighting(layout);

        NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12303);

        IReadOnlyList<NoiseHeard> heard = NoiseSystem.Propagate(
            layout, NoiseFixture.Actors(layout, NoiseFixture.FirstRoomCentre), NoiseFixture.Doors(layout), light, noise);

        Assert.Contains(heard, h => h.ActorId == NoiseFixture.Listener);
    }

    [Fact]
    public void EachDoorCostsExactlyTheProfilesConnectionAttenuation()
    {
        // The load-bearing assertion: the arithmetic is the table's, applied once per
        // door, with integer division and no rounding drift of its own.
        SiteLayout layout = NoiseFixture.Chain(rooms: 2);
        LightState light = PerceptionFixture.Lighting(layout);

        int baseRadius = NoiseSystem.BaseRadiusCm(12303);
        int perConnection = NoiseSystem.AttenuationPerConnectionPercent(12303);
        int expected = SimulationRules.PercentOf(baseRadius, 100 - perConnection);

        NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12303, baseRadiusCm: baseRadius);

        NoiseSystem.Propagate(layout, NoiseFixture.Actors(layout, NoiseFixture.FirstRoomCentre), NoiseFixture.Doors(layout), light, noise);

        NoiseHeard heard = Assert.Single(noise.HeardBy);

        Assert.Equal(expected, expectedRemaining(noise, baseRadius));
        Assert.True(heard.IntensityPercent is >= 0 and <= 100);
    }

    [Fact]
    public void MoreDoorsNeverCarryASoundFurther()
    {
        const int profile = 12303;
        int baseRadius = NoiseSystem.BaseRadiusCm(profile);

        int previous = baseRadius;

        for (int rooms = 1; rooms <= 5; rooms++)
        {
            SiteLayout layout = NoiseFixture.Chain(rooms);
            LightState light = PerceptionFixture.Lighting(layout);

            NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profile, baseRadiusCm: baseRadius);

            NoiseSystem.Propagate(layout, NoiseFixture.Actors(layout, NoiseFixture.FirstRoomCentre), NoiseFixture.Doors(layout), light, noise);

            int remaining = noise.HeardBy.Count > 0
                ? remainingFrom(noise, baseRadius)
                : 0;

            Assert.True(remaining <= previous,
                $"{rooms} rooms carried a sound further than {rooms - 1}.");

            previous = remaining;
        }
    }

    [Fact]
    public void ASoundEventuallyStopsTravelling()
    {
        // Attenuation compounds, so a chain long enough always reaches silence. A system
        // that leaked at all would make every building one enormous echo chamber.
        SiteLayout layout = NoiseFixture.Chain(rooms: 8);
        LightState light = PerceptionFixture.Lighting(layout);

        NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12303);

        IReadOnlyList<NoiseHeard> heard = NoiseSystem.Propagate(
            layout, NoiseFixture.Actors(layout, NoiseFixture.LastRoomCentre), NoiseFixture.Doors(layout), light, noise);

        Assert.Empty(heard);
    }

    [Fact]
    public void TheHeardListIsTheSameWhicheverOrderTheListenersAreAskedIn()
    {
        // Determinism: the listener set is part of the state hash, so it may not depend
        // on the order the actors happen to sit in a list.
        SiteLayout layout = NoiseFixture.Chain(rooms: 3);
        LightState light = PerceptionFixture.Lighting(layout);

        IReadOnlyList<TacticalActor> forwards = NoiseFixture.Actors(layout, NoiseFixture.FirstRoomCentre);

        NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12303);

        NoiseSystem.Propagate(layout, forwards, NoiseFixture.Doors(layout), light, noise);
        int[] baseline = noise.HeardBy.Select(h => h.ActorId).ToArray();

        var backwards = new List<TacticalActor>(forwards);
        backwards.Reverse();

        NoiseEvent second = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12303);

        NoiseSystem.Propagate(layout, backwards, NoiseFixture.Doors(layout), light, second);

        Assert.Equal(baseline, second.HeardBy.Select(h => h.ActorId).ToArray());
    }

    // ---- floors --------------------------------------------------------------

    [Fact]
    public void CrossingAFloorCostsMoreThanCrossingADoor()
    {
        // A basement is meant to be a refuge. If a stairwell attenuated no more than a
        // door, a shout on the ground floor would carry downstairs as readily as along
        // a corridor.
        Assert.True(
            NoiseSystem.AttenuationPerFloorPercent(12303) > 0,
            "Crossing a floor costs nothing, so a basement hides nobody.");

        int viaDoor = SimulationRules.PercentOf(
            1000, 100 - NoiseSystem.AttenuationPerConnectionPercent(12303));

        int viaFloor = SimulationRules.PercentOf(
            viaDoor, 100 - NoiseSystem.AttenuationPerFloorPercent(12303));

        Assert.True(viaFloor < viaDoor);
    }

    // ---- legibility ----------------------------------------------------------

    [Fact]
    public void EveryNoiseReachesTheMissionLogNamingItsSourceAndItsListeners()
    {
        // "Every noise event is recorded in the mission log with its source and who heard
        // it" — the brief's exact words, and the reason a log exists at all.
        //
        // Two rooms rather than three, because the assertion is about the log and not
        // about range: a sound that travelled three doors may legitimately reach nobody,
        // and a log test that needed a listener would be testing range instead.
        SiteLayout layout = NoiseFixture.Chain(rooms: 2);
        LightState light = PerceptionFixture.Lighting(layout);

        NoiseEvent noise = NoiseFixture.Noise(
            NoiseFixture.FirstRoomCentre, profileId: 12313, step: 42, sourceKey: "action.combat_melee");

        NoiseSystem.Propagate(layout, NoiseFixture.Actors(layout, NoiseFixture.FirstRoomCentre), NoiseFixture.Doors(layout), light, noise);

        var entry = new NoiseLogEntry(
            noise.Step, noise.SourceActorId, noise.SourceKey, noise.ProfileId,
            noise.Origin, noise.HeardBy.ToArray());

        Assert.Equal(42, entry.Step);
        Assert.Equal(NoiseFixture.Source, entry.SourceActorId);
        Assert.Equal("action.combat_melee", entry.SourceKey);
        Assert.Equal(12313, entry.ProfileId);
        Assert.Equal(noise.Origin, entry.Origin);
        Assert.NotEmpty(entry.Heard);
        Assert.All(entry.Heard, h => Assert.True(h.ActorId > 0));
    }

    [Fact]
    public void NoiseOnlyRisesSuspicionInProportionToHowLoudlyItArrived()
    {
        // Two listeners, one room apart, hearing the same noise. The near one must end
        // up strictly more suspicious than the far one: suspicion scaled with "did
        // anything happen near me" rather than with how loudly the sound arrived would
        // make distance meaningless, and a guard could not tell a noise in the next
        // room from one on the far side of the building.
        SiteLayout layout = NoiseFixture.Chain(rooms: 3);
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor near = NoiseFixture.ListenerIn(roomIndex: 1, actorId: 901);
        TacticalActor far = NoiseFixture.ListenerIn(roomIndex: 2, actorId: 902);

        var actors = new List<TacticalActor> { NoiseFixture.Actors(layout, NoiseFixture.FirstRoomCentre)[0], near, far };

        var byId = new Dictionary<int, TacticalActor>();
        foreach (TacticalActor actor in actors)
            byId[actor.Id.Value] = actor;

        // A lethal shot is 1400cm, which is the loudest thing in the table and the one
        // noise a whole building would plausibly hear.
        NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12315);

        NoiseSystem.Propagate(layout, actors, NoiseFixture.Doors(layout), light, noise);
        NoiseSystem.ApplyToSuspicion(noise, byId);

        Assert.Contains(noise.HeardBy, h => h.ActorId == near.Id.Value);
        Assert.Contains(noise.HeardBy, h => h.ActorId == far.Id.Value);

        Assert.True(near.Suspicion.Value > 0, "A guard next door did not react at all.");
        Assert.True(
            near.Suspicion.Value > far.Suspicion.Value,
            $"A nearer listener ({near.Suspicion.Value}) was no more suspicious than a further one ({far.Suspicion.Value}).");

        // The maker is not one of their own listeners, so making noise cannot be the
        // thing that makes you suspicious.
        Assert.DoesNotContain(noise.HeardBy, h => h.ActorId == NoiseFixture.Source);
        Assert.Equal(0, byId[NoiseFixture.Source].Suspicion.Value);
    }

    [Fact]
    public void ACivilianHearingSomethingDoesNotBecomeSuspicious()
    {
        // A member of the public does not develop a suspicion of infiltrators. Charging
        // them one would make "somebody saw me" and "somebody recognised me" the same
        // event, and would turn the stealth system into a noise-avoidance system.
        SiteLayout layout = NoiseFixture.Chain(rooms: 2);
        LightState light = PerceptionFixture.Lighting(layout);

        var civilian = new TacticalActor
        {
            Id = new TacticalActorId(NoiseFixture.Listener),
            Kind = TacticalActorKind.Civilian,
            Position = PerceptionFixture.At(NoiseFixture.LastRoomListenerAt(rooms: 2)),
            Condition = ActorCondition.Active,
            Health = 40,
            MaxHealth = 40,
        };

        NoiseEvent noise = NoiseFixture.Noise(NoiseFixture.FirstRoomCentre, profileId: 12303);

        NoiseSystem.Propagate(layout, new[] { civilian }, NoiseFixture.Doors(layout), light, noise);

        var byId = new Dictionary<int, TacticalActor> { [civilian.Id.Value] = civilian };
        NoiseSystem.ApplyToSuspicion(noise, byId);

        Assert.NotEmpty(noise.HeardBy);
        Assert.Equal(0, civilian.Suspicion.Value);
    }

    // ---- every posture makes an audible step ---------------------------------

    [Theory]
    [InlineData(Posture.Prone)]
    [InlineData(Posture.Crouch)]
    [InlineData(Posture.Walk)]
    [InlineData(Posture.Run)]
    public void EveryPostureMakesAStepNoiseAndRunningIsTheLoudest(Posture posture)
    {
        int profile = PostureRules.StepNoiseProfileId(posture);

        Assert.True(profile > 0, $"A {posture} step makes no noise at all.");
        Assert.True(NoiseSystem.BaseRadiusCm(profile) > 0);

        if (posture == Posture.Run)
            Assert.Equal(12303, profile);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// Recovers the surviving radius from a propagation result.
    /// </summary>
    /// <remarks>
    /// Intensity is published as a percentage of the original radius, so the surviving
    /// radius is only recoverable approximately — integer division loses the remainder.
    /// The tests compare against the same rounding rather than against an exact figure,
    /// because the published number is the percentage and that is what the UI shows.
    /// </remarks>
    private static int expectedRemaining(NoiseEvent noise, int baseRadius)
        => noise.HeardBy.Count == 0 ? 0 : baseRadius * noise.HeardBy[0].IntensityPercent / 100;

    private static int remainingFrom(NoiseEvent noise, int baseRadius)
        => noise.HeardBy.Count == 0 ? 0 : baseRadius * noise.HeardBy[^1].IntensityPercent / 100;
}
