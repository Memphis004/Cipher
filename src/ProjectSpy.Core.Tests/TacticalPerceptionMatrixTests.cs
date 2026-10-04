using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The perception matrix: light, posture, distance, occlusion, cone and disguise, one
/// axis at a time.
/// </summary>
/// <remarks>
/// <para>
/// Perception is the part of this simulation a player argues with. "Why did that guard
/// see me" has to have an answer that is true, that the UI can show, and that the player
/// can act on.
/// </para>
/// <para>
/// <b>Everything is swept against <see cref="PerceptionFixture"/> rather than a generated
/// site.</b> A generated site changes its spans, lights, occluders and archetype
/// between seeds, so sweeping light levels there would be sweeping four other variables
/// at once and a failure would name none of them. The fixture holds all of them still.
/// </para>
/// <para>
/// <b>Monotonicity is the load-bearing assertion.</b> Checking that a range produces an
/// expected level on one layout proves almost nothing — a formula that ignored range
/// entirely would pass. Asserting that moving further away can only ever make
/// perception worse, and that going prone can only ever make it worse, is what catches
/// the formula with one term inverted.
/// </para>
/// </remarks>
public class TacticalPerceptionMatrixTests
{
    /// <summary>An observer with a range and a cone this test controls.</summary>
    private const int Range = 1_000;

    /// <summary>A narrow cone: the target must be in front.</summary>
    private const int NarrowCone = 90;

    /// <summary>A full circle: facing does not matter.</summary>
    private const int WideCone = 360;

    // ---- the breakdown is mandatory ------------------------------------------

    [Fact]
    public void EveryPerceptionCarriesAFullBreakdownEvenWhenNothingWasSeen()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(50), Range, NarrowCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(850), Posture.Run);

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.NotEmpty(perception.Terms);

        foreach (PerceptionFactor factor in new[]
                 {
                     PerceptionFactor.Distance, PerceptionFactor.BaseRange, PerceptionFactor.RangeBonus,
                     PerceptionFactor.Cone, PerceptionFactor.Occlusion, PerceptionFactor.Light,
                     PerceptionFactor.Posture,
                 })
        {
            Assert.Contains(perception.Terms, t => t.Factor == factor);
        }

        // Every term carries a localization key, because Core writes no prose.
        foreach (PerceptionTerm term in perception.Terms)
            Assert.False(string.IsNullOrWhiteSpace(term.Key));
    }

    [Fact]
    public void TheBreakdownIsPresentOnASuccessfulPerceptionToo()
    {
        // A breakdown that only appears on failure teaches nothing about how to avoid the
        // next one. The player needs to see how close it was.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, NarrowCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(300));

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.NotEqual(PerceptionLevel.None, perception.Level);
        Assert.NotEmpty(perception.Terms);
        Assert.Equal(PerceptionBlocker.None, perception.Blocker);
    }

    [Fact]
    public void TheBreakdownTermsAreTheNumbersTheFormulaActuallyUsed()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, NarrowCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(600), Posture.Crouch);

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.Equal(500, Term(perception, PerceptionFactor.Distance).Value);
        Assert.Equal(Range, Term(perception, PerceptionFactor.BaseRange).Value);
        Assert.Equal(
            (int)PerceptionSystem.LightRangePercent(SiteLightLevel.Lit),
            Term(perception, PerceptionFactor.Light).Value);
        Assert.Equal(
            PostureRules.DetectionPercent(Posture.Crouch),
            Term(perception, PerceptionFactor.Posture).Value);
    }

    [Fact]
    public void TheEffectiveRangeMatchesTheBreakdownCanBeReplayed()
    {
        // The UI rebuilds the arithmetic from the terms. If the published effective range
        // cannot be reconstructed from the published terms, the breakdown is decoration.
        SiteLayout layout = PerceptionFixture.Build(leftLight: SiteLightLevel.Dim);
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(400), Posture.Prone);

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        int expected = Range;
        expected = SimulationRules.PercentOf(expected, Term(perception, PerceptionFactor.Light).Value);
        expected = SimulationRules.PercentOf(expected, Term(perception, PerceptionFactor.Posture).Value);

        Assert.Equal(expected, perception.EffectiveRangeCm.Raw);
    }

    // ---- distance ------------------------------------------------------------

    [Fact]
    public void PerceptionOnlyGetsWorseAsTheTargetMovesAway()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        int best = -1;

        for (int offset = 50; offset <= 850; offset += 50)
        {
            TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(0), Range, WideCone);
            TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(offset), Posture.Walk);

            Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

            if (offset > 50)
            {
                Assert.True((int)perception.Level <= best,
                    $"Perception improved as the target moved out to {offset}cm.");
            }

            best = (int)perception.Level;
        }
    }

    [Fact]
    public void ExactlyAtTheEffectiveRangeIsSeenAndOneCentimetreBeyondIsNot()
    {
        // The boundary is the whole mechanic, so it is tested at the boundary rather than
        // at a comfortable distance inside it. The target crouches: a walking target
        // picks up the movement bonus and its effective range lands past the end of the
        // room, which would test the wall rather than the boundary.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor probe = PerceptionFixture.Observer(PerceptionFixture.At(0), Range, WideCone);
        TacticalActor partner = PerceptionFixture.Target(PerceptionFixture.At(100), Posture.Crouch);

        Perception at100 = PerceptionSystem.CanPerceive(layout, light, probe, partner);
        int effective = at100.EffectiveRangeCm.Raw;

        Assert.True(effective < PerceptionFixture.LeftEndCm,
            "The fixture's boundary case fell outside the room it was measured in.");

        TacticalActor atLimit = PerceptionFixture.Target(PerceptionFixture.At(effective), Posture.Crouch);
        TacticalActor beyondLimit = PerceptionFixture.Target(PerceptionFixture.At(effective + 1), Posture.Crouch);

        Assert.NotEqual(
            PerceptionLevel.None,
            PerceptionSystem.CanPerceive(layout, light, probe, atLimit).Level);

        Assert.Equal(
            PerceptionLevel.None,
            PerceptionSystem.CanPerceive(layout, light, probe, beyondLimit).Level);
    }

    [Fact]
    public void SomethingBeyondTheObserversRangeIsBlockedByRangeAndSaysSo()
    {
        // Both in the same room, so the answer is genuinely about distance rather than
        // about the wall between two rooms.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(50), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(880), Posture.Prone);

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.Equal(PerceptionLevel.None, perception.Level);
        Assert.Equal(PerceptionBlocker.OutOfRange, perception.Blocker);
    }

    [Fact]
    public void SomethingInAnotherRoomThroughABlockingDoorIsReportedAsOccluded()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.InLeft(50), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.InRight(50));

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.Equal(PerceptionLevel.None, perception.Level);
        Assert.Equal(PerceptionBlocker.Occluded, perception.Blocker);
    }

    [Fact]
    public void AnIdentificationNeedsTheTargetInsideTheNarrowerIdentifyBand()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(0), Range, WideCone);
        int identifyAt = SimulationRules.PercentOf(
            PerceptionSystem.CanPerceive(
                layout, light, observer,
                PerceptionFixture.Target(PerceptionFixture.At(1))).EffectiveRangeCm.Raw,
            PerceptionSystem.IdentifyRangePercent);

        // Standing exactly on the identify boundary is identified; a centimetre beyond it
        // is only noticed. Nothing else changes between the two.
        TacticalActor atLimit = PerceptionFixture.Target(PerceptionFixture.At(identifyAt));
        TacticalActor beyond = PerceptionFixture.Target(PerceptionFixture.At(identifyAt + 1));

        Assert.Equal(
            PerceptionLevel.Identified,
            PerceptionSystem.CanPerceive(layout, light, observer, atLimit).Level);

        Assert.Equal(
            PerceptionLevel.Noticed,
            PerceptionSystem.CanPerceive(layout, light, observer, beyond).Level);
    }

    // ---- posture -------------------------------------------------------------

    [Fact]
    public void EveryPostureIsAtLeastAsVisibleAsEveryFasterOne()
    {
        for (Posture slower = Posture.Prone; slower <= Posture.Run; slower++)
        {
            foreach (Posture quicker in Enum.GetValues<Posture>())
            {
                if (quicker <= slower)
                    continue;

                Assert.True(
                    PostureRules.DetectionPercent(slower) <= PostureRules.DetectionPercent(quicker),
                    $"{slower} is harder to see than the quicker {quicker}, which inverts the ladder.");

                Assert.True(
                    PostureRules.SpeedPerStep(slower).Raw <= PostureRules.SpeedPerStep(quicker).Raw,
                    $"{slower} covers more ground per step than the quicker {quicker}.");
            }
        }
    }

    [Fact]
    public void TheMoreConspicuousAPostureIsTheSoonerItIsSeen()
    {
        // Swept on the same building, same light, same cone, same distance: the only
        // variable is posture. The distance is inside even the most concealing posture's
        // range, so a failure here is about posture and not about where the target was
        // put.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(0), Range, WideCone);

        foreach (Posture posture in Enum.GetValues<Posture>())
        {
            TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(250), posture);
            Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

            Assert.True(
                perception.Level >= PerceptionLevel.Noticed,
                $"A {posture} target 250cm away in a lit room was not perceived at all.");
        }
    }

    [Fact]
    public void ProneIsHarderToSeeThanWalkingAtEveryDistance()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(0), Range, WideCone);

        for (int offset = 100; offset <= 800; offset += 100)
        {
            int prone = PerceptionSystem
                .CanPerceive(layout, light, observer, PerceptionFixture.Target(PerceptionFixture.At(offset), Posture.Prone))
                .EffectiveRangeCm.Raw;

            int walking = PerceptionSystem
                .CanPerceive(layout, light, observer, PerceptionFixture.Target(PerceptionFixture.At(offset), Posture.Walk))
                .EffectiveRangeCm.Raw;

            Assert.True(prone <= walking,
                $"At {offset}cm a prone target was seen from further away than a walking one.");
        }
    }

    // ---- light ---------------------------------------------------------------

    [Theory]
    [InlineData(SiteLightLevel.Lit)]
    [InlineData(SiteLightLevel.Dim)]
    public void AVisibleRoomIsSeenAndADarkOneIsNot(SiteLightLevel level)
    {
        SiteLayout layout = PerceptionFixture.Build(leftLight: level);
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(500), Posture.Crouch);

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.NotEqual(PerceptionLevel.None, perception.Level);
    }

    [Fact]
    public void ADarkRoomIsNotSeenAtAnyRange()
    {
        // Rule 12: lighting is gameplay and Core is the authority. If a dark room
        // produced a detection, "turn the lights out" would be a suggestion rather than
        // a plan, and the player would be right to distrust everything else the
        // perception system says.
        SiteLayout layout = PerceptionFixture.Build(leftLight: SiteLightLevel.Dark);
        LightState light = PerceptionFixture.Lighting(layout);

        for (int offset = 1; offset <= 800; offset += 50)
        {
            TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(0), Range, WideCone);
            TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(offset), Posture.Run);

            Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

            Assert.True(perception.SawNothing,
                $"A dark room produced a {perception.Level} at {offset}cm, blocked by {perception.Blocker}.");

            Assert.Equal(PerceptionBlocker.Unlit, perception.Blocker);
        }
    }

    [Fact]
    public void BrighterIsNeverHarderToSee()
    {
        int lit = PerceptionSystem.LightRangePercent(SiteLightLevel.Lit);
        int dim = PerceptionSystem.LightRangePercent(SiteLightLevel.Dim);
        int dark = PerceptionSystem.LightRangePercent(SiteLightLevel.Dark);

        Assert.True(lit > dim, "A dim room is seen further into than a lit one.");
        Assert.True(dim > dark, "A dark room is seen further into than a dim one.");
        Assert.Equal(0, dark);
    }

    [Fact]
    public void SwitchingOffTheLightDarkensTheRoom()
    {
        // "Destroying or switching a light changes them" — checked through perception,
        // not through the interval list, because a light system that recomputed its
        // intervals but not their effect would pass a test that only read the intervals.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(500), Posture.Crouch);

        int before = PerceptionSystem.CanPerceive(layout, light, observer, target).EffectiveRangeCm.Raw;

        Assert.True(light.SetSwitched(PerceptionFixture.LeftLight, true, 0));
        int after = PerceptionSystem.CanPerceive(layout, light, observer, target).EffectiveRangeCm.Raw;

        Assert.True(after < before, $"Switching the light off left the range at {after}cm, was {before}cm.");

        // And back on again, because the table says a ceiling strip is switchable.
        Assert.True(light.SetSwitched(PerceptionFixture.LeftLight, false, 0));
        Assert.Equal(before, PerceptionSystem.CanPerceive(layout, light, observer, target).EffectiveRangeCm.Raw);
    }

    [Fact]
    public void AEmitterDoesNotLightTheRoomNextDoor()
    {
        // A lit interval running through a wall would let a guard see into a dark
        // corridor, which is the exact mistake a cutaway building invites.
        SiteLayout layout = PerceptionFixture.Build(rightLight: SiteLightLevel.Dark);
        LightState light = PerceptionFixture.Lighting(layout);

        Assert.Equal(SiteLightLevel.Dark, light.LevelAt(layout, PerceptionFixture.InRight(200)));
        Assert.Equal(SiteLightLevel.Lit, light.LevelAt(layout, PerceptionFixture.InLeft(450)));
    }

    // ---- cone ----------------------------------------------------------------

    [Fact]
    public void ATargetBehindANarrowConeIsNotSeenAtAnyRange()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        // Observer looks right; the target stands to their left, however close.
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(500), Range, NarrowCone, Facing.Right);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(490));

        Assert.True(PerceptionSystem.IsBehind(observer, target));

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.Equal(PerceptionLevel.None, perception.Level);
        Assert.Equal(PerceptionBlocker.OutsideCone, perception.Blocker);
    }

    [Fact]
    public void ATargetBehindAFullCircleConeIsSeen()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(500), Range, WideCone, Facing.Right);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(490));

        Assert.True(PerceptionSystem.IsBehind(observer, target));
        Assert.NotEqual(PerceptionLevel.None, PerceptionSystem.CanPerceive(layout, light, observer, target).Level);
    }

    [Fact]
    public void TurningAroundBringsTheSameTargetIntoView()
    {
        // The cone axis is a real gate and not a formality: the identical pair of actors
        // is seen or not purely according to which way the observer is looking.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(300));

        TacticalActor lookingAway = PerceptionFixture.Observer(PerceptionFixture.At(400), Range, NarrowCone, Facing.Right);
        TacticalActor lookingBack = PerceptionFixture.Observer(PerceptionFixture.At(400), Range, NarrowCone, Facing.Left);

        Assert.Equal(PerceptionLevel.None, PerceptionSystem.CanPerceive(layout, light, lookingAway, target).Level);
        Assert.NotEqual(PerceptionLevel.None, PerceptionSystem.CanPerceive(layout, light, lookingBack, target).Level);
    }

    [Fact]
    public void CrossingAFloorIsNeverBlockedByACone()
    {
        // A guard would otherwise "see" the basement whenever they faced the wrong way
        // upstairs, which is nonsense rather than a difficulty.
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(400), Range, NarrowCone, Facing.Right);
        TacticalActor upstairs = PerceptionFixture.Target(PerceptionFixture.At(300));

        upstairs.Position = new TacticalPosition(1, upstairs.Position.X);

        Assert.False(PerceptionSystem.IsBehind(observer, upstairs));
    }

    // ---- occlusion -----------------------------------------------------------

    [Fact]
    public void OccludersBetweenThemAreCounted()
    {
        SiteLayout layout = PerceptionFixture.Build(leftOccluders: 3);
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(50), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(880));

        Assert.Equal(3, PerceptionSystem.CountOccluders(layout, observer, target));

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);
        Assert.Equal(3, Term(perception, PerceptionFactor.Occlusion).Value);
    }

    [Fact]
    public void AnOccluderTheObserverStandsBehindIsNotBetweenThem()
    {
        SiteLayout layout = PerceptionFixture.Build(leftOccluders: 1);
        LightState light = PerceptionFixture.Lighting(layout);

        // The occluder sits at 440cm. Standing at 450 puts it behind the observer.
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(450), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(880));

        Assert.Equal(0, PerceptionSystem.CountOccluders(layout, observer, target));

        // And it does not hide the target, because it is on the wrong side.
        Assert.NotEqual(
            PerceptionBlocker.Occluded,
            PerceptionSystem.CanPerceive(layout, light, observer, target).Blocker);
    }

    [Fact]
    public void SomethingBehindAnOccluderIsNotSeenAndSaysItWasOccluded()
    {
        // Occluders clip the sightline rather than merely being counted: a filing cabinet
        // does not make you invisible, it makes you invisible from past it.
        SiteLayout layout = PerceptionFixture.Build(leftOccluders: 3);
        LightState light = PerceptionFixture.Lighting(layout);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(50), Range, WideCone);
        TacticalActor near = PerceptionFixture.Target(PerceptionFixture.At(200));
        TacticalActor far = PerceptionFixture.Target(PerceptionFixture.At(880));

        Assert.NotEqual(
            PerceptionBlocker.Occluded,
            PerceptionSystem.CanPerceive(layout, light, observer, near).Blocker);

        Perception blocked = PerceptionSystem.CanPerceive(layout, light, observer, far);
        Assert.Equal(PerceptionLevel.None, blocked.Level);
        Assert.Equal(PerceptionBlocker.Occluded, blocked.Blocker);
    }

    [Fact]
    public void ASightBlockingDoorIsReportedAsOccludedRatherThanOutOfRange()
    {
        SiteLayout blocked = PerceptionFixture.Build(blocksVision: true);
        SiteLayout clear = PerceptionFixture.Build(blocksVision: false);

        LightState blockedLight = PerceptionFixture.Lighting(blocked);
        LightState clearLight = PerceptionFixture.Lighting(clear);

        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.InLeft(50), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.InRight(500));

        Perception through = PerceptionSystem.CanPerceive(blocked, blockedLight, observer, target);
        Assert.Equal(PerceptionLevel.None, through.Level);
        Assert.Equal(PerceptionBlocker.Occluded, through.Blocker);

        // Through an interior window the same pair can see each other.
        Assert.NotEqual(
            PerceptionBlocker.Occluded,
            PerceptionSystem.CanPerceive(clear, clearLight, observer, target).Blocker);
    }

    [Fact]
    public void MoreOccludersNeverMakeALineOfSightClearer()
    {
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(50), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(880));

        int previous = 0;

        foreach (int count in new[] { 0, 1, 2, 3, 4 })
        {
            SiteLayout layout = PerceptionFixture.Build(leftOccluders: count);
            int found = PerceptionSystem.CountOccluders(layout, observer, target);

            Assert.True(found >= previous, $"Adding an occluder reduced the count from {previous} to {found}.");
            Assert.True(found <= count, $"Counted {found} occluders where only {count} were placed.");

            previous = found;
        }
    }

    [Fact]
    public void ASightBlockingDoorCountsAsAnOccluderAndAnOpenPlanDoesNot()
    {
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.InLeft(50), Range, WideCone);
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.InRight(50));

        SiteLayout blocked = PerceptionFixture.Build(blocksVision: true);
        SiteLayout clear = PerceptionFixture.Build(blocksVision: false);

        Assert.Equal(1, PerceptionSystem.CountOccluders(blocked, observer, target));
        Assert.Equal(0, PerceptionSystem.CountOccluders(clear, observer, target));
    }

    // ---- disguise ------------------------------------------------------------

    [Fact]
    public void ADisguiseNeverMakesATargetEasierToSee()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, WideCone);

        for (int offset = 100; offset <= 800; offset += 100)
        {
            int bare = PerceptionSystem
                .CanPerceive(layout, light, observer, PerceptionFixture.Target(PerceptionFixture.At(offset)))
                .EffectiveRangeCm.Raw;

            int disguised = PerceptionSystem
                .CanPerceive(layout, light, observer,
                    PerceptionFixture.Target(PerceptionFixture.At(offset), disguiseId: 1))
                .EffectiveRangeCm.Raw;

            Assert.True(disguised <= bare,
                $"At {offset}cm a disguised target was seen from further away than a bare one.");
        }
    }

    [Fact]
    public void ADisguisedTargetStillCannotBeIdentifiedAtRange()
    {
        SiteLayout layout = PerceptionFixture.Build();
        LightState light = PerceptionFixture.Lighting(layout);
        TacticalActor observer = PerceptionFixture.Observer(PerceptionFixture.At(100), Range, WideCone);

        // Well inside the notice range but nowhere near the identify band.
        TacticalActor target = PerceptionFixture.Target(PerceptionFixture.At(400), disguiseId: 1);

        Perception perception = PerceptionSystem.CanPerceive(layout, light, observer, target);

        Assert.Equal(PerceptionLevel.Noticed, perception.Level);
    }

    // ---- guards actually get their archetype's eyes --------------------------

    [Fact]
    public void AGeneratedGuardInheritsItsArchetypesSightAndHearing()
    {
        // Caught by this matrix: guards were being built with the default vision stats,
        // which are a range of zero — so no guard on any site could see anything, and
        // every perception test that used a generated guard was asserting on a guard that
        // was effectively blind.
        TacticalState mission = TacticalHarness.Mission();

        foreach (TacticalActor guard in mission.Guards)
        {
            ProjectSpy.Tables.GuardArchetype? archetype = SimulationRules.GuardArchetypeFor(guard.GuardArchetypeId)
                ?? throw new InvalidOperationException($"Guard {guard.Id} has no archetype {guard.GuardArchetypeId}.");

            Assert.Equal(archetype.VisionRangeCm, guard.Vision.VisionRangeCm);
            Assert.Equal(archetype.VisionConeDegrees, guard.Vision.VisionConeDegrees);
            Assert.Equal(archetype.HearingRangeCm, guard.Vision.HearingRangeCm);
            Assert.True(guard.Vision.VisionRangeCm > 0, $"Guard {guard.Id} can see nothing at all.");
        }
    }

    [Fact]
    public void AMoreSkilledOperativeSeesFurtherThanALessSkilledOne()
    {
        // Rule 16: Infiltration governs perception range.
        var unskilled = new Agent { Skills = new SkillSet(Infiltration: 10, Combat: 0, Tech: 0, Social: 0, Nerve: 0) };
        var skilled = new Agent { Skills = new SkillSet(Infiltration: 90, Combat: 0, Tech: 0, Social: 0, Nerve: 0) };

        Assert.True(AgentSightOf(skilled) > AgentSightOf(unskilled));
    }

    private static int AgentSightOf(Agent agent) => TacticalMission.VisionFor(agent).VisionRangeCm;

    // ---- helpers -------------------------------------------------------------

    private static PerceptionTerm Term(Perception perception, PerceptionFactor factor)
    {
        foreach (PerceptionTerm term in perception.Terms)
        {
            if (term.Factor == factor)
                return term;
        }

        throw new InvalidOperationException($"The breakdown has no {factor} term.");
    }
}
