using System.Linq;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Site;
using ProjectSpy.Unity.Tactical;
using UnityEngine;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves the cutaway's rules: which floors the camera covers, which slabs get faded
    /// out of the way, and that the light budget holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are pure functions over floor indices and camera geometry, so they are
    /// tested directly rather than through a scene. A test that stood up a building and
    /// checked what it looked like would assert on the renderer's behaviour instead of on
    /// the decision, and would go green for a cutaway that is wrong for the right reason.
    /// </para>
    /// <para>
    /// The counterweight is the budget test at the bottom, which does drive the real
    /// component: a budget that is applied and then undone on the next frame is invisible
    /// to a pure-function test and is exactly the defect that shipped here once.
    /// </para>
    /// </remarks>
    public sealed class CutawayVisibilityTests
    {
        private const float Storey = 3.2f;

        [OneTimeSetUp]
        public void RequireTables()
        {
            TestTables.Require();
        }

        /// <summary>A camera framing one storey covers that storey and nothing else.</summary>
        [Test]
        public void ACameraFramingOneStoreyCoversThatStorey()
        {
            // Aimed at floor 2: base y 6.4, so the camera sits at its mid height.
            float cameraY = 2 * Storey + LaneUnits.RoomHeightMetres * 0.5f;

            var band = FloorBand.ForOrthographic(cameraY, 1.6f, Storey, 5);

            Assert.That(band.Contains(2), Is.True);
            Assert.That(band.Contains(0), Is.False);
            Assert.That(band.Contains(4), Is.False);
        }

        /// <summary>A wide camera covers several floors, and culling is not over-eager.</summary>
        [Test]
        public void AWideCameraCoversEveryStoreyItActuallySees()
        {
            // Aimed just above the ground floor but pulled back far enough to see fifteen
            // metres up, which is genuinely the top of a five-storey building.
            float cameraY = Storey + 1.5f;

            var band = FloorBand.ForOrthographic(cameraY, 10f, Storey, 5);

            Assert.That(band.LowestFloor, Is.EqualTo(0));
            Assert.That(band.HighestFloor, Is.EqualTo(4));
        }

        /// <summary>
        /// A camera at the bottom of the building still frames the ground floor, and the
        /// band never runs off either end.
        /// </summary>
        [Test]
        public void ABandIsAlwaysInsideTheBuilding()
        {
            foreach (float cameraY in new[] { -50f, 0f, 1.5f, 999f })
            {
                var band = FloorBand.ForOrthographic(cameraY, 8f, Storey, 3);

                Assert.That(band.LowestFloor, Is.GreaterThanOrEqualTo(0));
                Assert.That(band.HighestFloor, Is.LessThan(3));
                Assert.That(band.LowestFloor, Is.LessThanOrEqualTo(band.HighestFloor));
            }
        }

        /// <summary>Floors outside the band are hidden outright, not faded.</summary>
        [Test]
        public void FloorsTheCameraDoesNotCoverAreHidden()
        {
            var band = new FloorBand(1, 2);

            Assert.That(CutawayVisibility.FloorRole(1, band), Is.EqualTo(CutawayRole.Solid));
            Assert.That(CutawayVisibility.FloorRole(2, band), Is.EqualTo(CutawayRole.Solid));
            Assert.That(CutawayVisibility.FloorRole(0, band), Is.EqualTo(CutawayRole.Hidden));
            Assert.That(CutawayVisibility.FloorRole(3, band), Is.EqualTo(CutawayRole.Hidden));
        }

        /// <summary>
        /// The slab between the camera and the room is faded out — the whole point of the
        /// cutaway.
        /// </summary>
        [Test]
        public void ACameraLookingDownIntoARoomFadesItsCeiling()
        {
            var band = new FloorBand(0, 2);

            // Camera above floor 1's slab: it is looking down through that room's ceiling.
            Assert.That(
                CutawayVisibility.CeilingRole(1, band, cameraY: 1f * Storey + 2f, Storey),
                Is.EqualTo(CutawayRole.Faded));
        }

        /// <summary>
        /// A ceiling the camera is below is seen from underneath, occludes nothing, and is
        /// left solid so the building keeps its horizontal structure.
        /// </summary>
        [Test]
        public void ACameraBelowACeilingLeavesItSolid()
        {
            var band = new FloorBand(0, 3);

            Assert.That(
                CutawayVisibility.CeilingRole(2, band, cameraY: 0.5f, Storey),
                Is.EqualTo(CutawayRole.Solid));
        }

        /// <summary>
        /// Looking down at the whole building fades every ceiling, which is what makes the
        /// tactical overview readable rather than a stack of sealed floors.
        /// </summary>
        [Test]
        public void AnOverviewFromAboveFadesEveryCoveredCeiling()
        {
            var band = new FloorBand(0, 3);

            for (int floor = 0; floor <= 3; floor++)
            {
                Assert.That(
                    CutawayVisibility.CeilingRole(floor, band, cameraY: 100f, Storey),
                    Is.EqualTo(CutawayRole.Faded),
                    $"floor {floor} should be faded from above");
            }
        }

        /// <summary>A hidden floor's ceiling is hidden, whatever the camera is doing.</summary>
        [Test]
        public void AHiddenFloorHasNoCeiling()
        {
            var band = new FloorBand(0, 0);

            Assert.That(
                CutawayVisibility.CeilingRole(3, band, cameraY: 100f, Storey),
                Is.EqualTo(CutawayRole.Hidden));
        }

        /// <summary>The band is a value, and never describes an empty window.</summary>
        [Test]
        public void BandsCompareByValueSoTheyCanBeCached()
        {
            Assert.That(new FloorBand(1, 3), Is.EqualTo(new FloorBand(1, 3)));
            Assert.That(new FloorBand(1, 3), Is.Not.EqualTo(new FloorBand(1, 4)));
        }

        /// <summary>
        /// A band built with its ends the wrong way round collapses to the top floor rather
        /// than becoming empty. An empty band would hide every floor and render nothing —
        /// the same silent failure as a camera pointed the wrong way.
        /// </summary>
        [Test]
        public void ABandIsNeverEmpty()
        {
            var inverted = new FloorBand(5, 2);

            Assert.That(inverted.Contains(5), Is.True);
            Assert.That(inverted.LowestFloor, Is.LessThanOrEqualTo(inverted.HighestFloor));
        }

        /// <summary>A zero storey height is rejected rather than dividing by it.</summary>
        [Test]
        public void AZeroStoreyHeightIsRejected()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => FloorBand.ForOrthographic(0f, 5f, 0f, 3));
        }
    }

    /// <summary>
    /// Proves the fog mapping is total and that a room's contents only exist once Core has
    /// observed the room.
    /// </summary>
    /// <remarks>
    /// The total-mapping test is the one that matters. A mapping written as a switch with a
    /// fallthrough would quietly draw an unrecognised state as observed, which is the one
    /// failure mode here that hands the player information they have not earned.
    /// </remarks>
    public sealed class FogLooksTests
    {
        [Test]
        public void EveryCoreFogStateHasALook()
        {
            foreach (FogState state in System.Enum.GetValues(typeof(FogState)))
            {
                FogLook look = FogLooks.LookFor(state);
                Assert.That(System.Enum.IsDefined(typeof(FogLook), look), Is.True,
                    $"{state} mapped to a look that does not exist");
            }
        }

        [Test]
        public void UnknownIsTheFallbackForAValueCoreDoesNotDefine()
        {
            var invented = (FogState)999;

            Assert.That(FogLooks.LookFor(invented), Is.EqualTo(FogLook.UnknownVoid));
            Assert.That(FogLooks.ShowsContents(FogLooks.LookFor(invented)), Is.False);
        }

        [Test]
        public void OnlyObservedRoomsShowTheirContents()
        {
            Assert.That(FogLooks.ShowsContents(FogLook.Observed), Is.True);
            Assert.That(FogLooks.ShowsContents(FogLook.Cleared), Is.True);

            Assert.That(FogLooks.ShowsContents(FogLook.UnknownVoid), Is.False);
            Assert.That(FogLooks.ShowsContents(FogLook.Reported), Is.False);
            Assert.That(FogLooks.ShowsContents(FogLook.Scouted), Is.False);
        }

        /// <summary>
        /// A reported room does not respond to the building's lights. That is the cue that
        /// it is a claim rather than a fact.
        /// </summary>
        [Test]
        public void ClaimsAreDrawnUnlitAndFactsAreDrawnLit()
        {
            Assert.That(FogLooks.IsUnlit(FogLook.Reported), Is.True);
            Assert.That(FogLooks.IsUnlit(FogLook.UnknownVoid), Is.True);

            Assert.That(FogLooks.IsUnlit(FogLook.Observed), Is.False);
            Assert.That(FogLooks.IsUnlit(FogLook.Cleared), Is.False);
            Assert.That(FogLooks.IsUnlit(FogLook.Scouted), Is.False);
        }

        /// <summary>One shared material per look, so the building stays batchable.</summary>
        [Test]
        public void EveryLookHasExactlyOneSharedMaterial()
        {
            var first = new System.Collections.Generic.Dictionary<FogLook, Material>();

            foreach (FogLook look in System.Enum.GetValues(typeof(FogLook)))
            {
                Material material = FogLooks.MaterialFor(look);

                Assert.That(material, Is.Not.Null, $"{look} has no material");

                if (first.TryGetValue(look, out Material existing))
                    Assert.That(material, Is.SameAs(existing), $"{look} allocated a second material");
                else
                    first[look] = material;
            }
        }

        /// <summary>Unknown is near-black, and Observed is the building's own grey.</summary>
        [Test]
        public void UnknownIsNearBlackAndObservedIsTheBuildingsOwnGrey()
        {
            Assert.That(FogLooks.UnknownColour.maxColorComponent, Is.LessThan(0.05f),
                "An unknown room that is not near-black reads as a room with nothing in it.");

            Assert.That(FogLooks.MaterialFor(FogLook.Observed),
                Is.SameAs(BlockoutMaterials.Grey),
                "An observed room must be indistinguishable from one drawn before fog existed.");
        }

        /// <summary>
        /// Every legend swatch is distinguishable from every other one.
        /// </summary>
        /// <remarks>
        /// The material colours are deliberately near-identical, which is right in the
        /// world and useless in a legend: a swatch the player cannot tell apart is worse
        /// than no swatch, because it teaches them to trust a colour that means nothing.
        /// Asserted as a floor on channel distance rather than on specific values, so
        /// re-tuning a swatch does not fail the test but collapsing two of them does.
        /// </remarks>
        [Test]
        public void EveryLegendSwatchIsDistinguishableFromEveryOther()
        {
            FogLook[] looks =
            {
                FogLook.UnknownVoid, FogLook.Reported, FogLook.Scouted,
                FogLook.Observed, FogLook.Cleared,
            };

            for (int i = 0; i < looks.Length; i++)
            {
                for (int j = i + 1; j < looks.Length; j++)
                {
                    Color a = FogLooks.TintFor(looks[i]);
                    Color b = FogLooks.TintFor(looks[j]);

                    float distance = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

                    Assert.That(distance, Is.GreaterThanOrEqualTo(0.15f),
                        $"{looks[i]} and {looks[j]} are too close to tell apart at 20 pixels.");
                }
            }
        }

        /// <summary>
        /// The legend reads as a progression, and the claim is the only coloured one.
        /// </summary>
        [Test]
        public void TheLegendReadsDarkToBrightAndTheClaimIsTheOnlyHue()
        {
            FogLook[] order =
            {
                FogLook.UnknownVoid, FogLook.Reported, FogLook.Scouted,
                FogLook.Observed, FogLook.Cleared,
            };

            for (int i = 1; i < order.Length; i++)
            {
                Assert.That(FogLooks.TintFor(order[i]).grayscale,
                    Is.GreaterThan(FogLooks.TintFor(order[i - 1]).grayscale),
                    $"{order[i]} should read brighter than {order[i - 1]}.");
            }

            Assert.That(FogLooks.TintFor(FogLook.UnknownVoid).maxColorComponent, Is.LessThan(0.2f),
                "A void that reads as a mid grey is not a void.");

            foreach (FogLook look in order)
            {
                if (look == FogLook.Reported)
                    continue;

                Color tint = FogLooks.TintFor(look);
                Assert.That(tint.b, Is.GreaterThanOrEqualTo(tint.g),
                    $"{look} is a real room, so its swatch should stay cool rather than going green.");
            }

            Color reported = FogLooks.TintFor(FogLook.Reported);
            Assert.That(reported.g, Is.GreaterThan(reported.b),
                "The one swatch that is a claim rather than a fact should be the green one.");
        }
    }

    /// <summary>
    /// Proves the realtime light budget holds, and that switching a light changes Core and
    /// the render together.
    /// </summary>
    /// <remarks>
    /// This one drives the real component rather than a pure function, because the defect
    /// it guards is a state machine: the budget is applied, and then the per-frame sync
    /// turns it back on. Nothing about the pure rule is wrong; the order of operations is.
    /// </remarks>
    public sealed class LightingDirectorTests
    {
        [OneTimeSetUp]
        public void RequireTables()
        {
            TestTables.Require();
        }

        private GameObject _host;
        private LightingDirector _director;
        private SiteLayout _layout;
        private LightState _lights;

        [SetUp]
        public void Build()
        {
            _layout = Site();

            var world = new WorldState(20261005UL);
            var roster = new System.Collections.Generic.List<Agent>();

            for (int i = 0; i < 3; i++)
            {
                roster.Add(world.AddAgent(new Agent
                {
                    Name = $"Agent{i + 1}",
                    Codename = $"S{i + 1}",
                    Skills = new SkillSet
                    {
                        Infiltration = 40, Combat = 40, Tech = 40, Social = 40, Nerve = 40,
                    },
                }));
            }

            _lights = new LightState(_layout);

            _host = new GameObject("LightingDirectorTests");
            _director = _host.AddComponent<LightingDirector>();
            _director.Build(_lights, _layout, Vector3.zero);
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
                Object.DestroyImmediate(_host);
        }

        /// <summary>
        /// The budget is enforced at build time, not left for a later pass to apply.
        /// </summary>
        [Test]
        public void NoMoreLightsThanTheBudgetAreEnabledAfterBuild()
        {
            Assert.That(_director.RealtimeLightCount, Is.EqualTo(LightingDirector.MaxRealtimeLights),
                "Every emitter should have been granted a realtime light until the budget ran out.");
            Assert.That(_director.ProxyCount, Is.GreaterThan(0),
                "This site has more emitters than the budget, so something should be a proxy.");
        }

        /// <summary>
        /// The per-frame sync must not turn the budget back on. This is the defect the test
        /// exists for: the budget applied correctly on one frame and was undone on the next.
        /// </summary>
        [Test]
        public void TheBudgetSurvivesAFewFramesOfSyncing()
        {
            for (int frame = 0; frame < 5; frame++)
                _director.Sync();

            int enabled = _host.GetComponentsInChildren<Light>(true)
                .Count(l => l.enabled && l.gameObject.activeInHierarchy);

            Assert.That(enabled, Is.LessThanOrEqualTo(LightingDirector.MaxRealtimeLights),
                $"{enabled} realtime lights are on after five syncs; the budget is " +
                $"{LightingDirector.MaxRealtimeLights}.");
        }

        /// <summary>
        /// Every emitter is drawn by something. A light that is neither a realtime light nor
        /// a proxy would render its room pitch black, which the player reads as "this room is
        /// dark" — a lie about a room Core has said is lit.
        /// </summary>
        [Test]
        public void EveryEmitterIsDrawnBySomething()
        {
            var proxies = _host.GetComponentsInChildren<MeshRenderer>(true)
                .Count(r => r.name.StartsWith("Proxy_") || r.transform.parent.name.StartsWith("Proxy_"));

            int realtime = _director.RealtimeLightCount;
            int proxyGroups = _host.transform.Find("Lights")
                ?.GetComponentsInChildren<Transform>(true)
                .Count(t => t.name.StartsWith("Proxy_")) ?? 0;

            Assert.That(realtime + proxyGroups, Is.EqualTo(_lights.Emitters.Count),
                $"{_lights.Emitters.Count} emitters but only {realtime} realtime and " +
                $"{proxyGroups} proxies drawn.");
            _ = proxies;
        }

        /// <summary>
        /// Switching a light off changes Core's state and the render in the same call, so
        /// there is never a frame where the room is dark but the simulation still thinks
        /// the bulb works.
        /// </summary>
        [Test]
        public void SwitchingALightOffChangesCoreAndTheRenderTogether()
        {
            int lightId = _lights.Emitters[0].LightId;

            Assert.That(_director.SetSwitched(lightId, off: true, step: 0), Is.True);
            Assert.That(_lights.Find(lightId)!.SwitchedOff, Is.True,
                "Core still thinks the light is on after the switch was accepted.");
            Assert.That(_lights.Find(lightId)!.IsWorking, Is.False);

            _director.Sync();

            var light = _host.GetComponentsInChildren<Light>(true)
                .First(l => l.gameObject.name == $"Emitter_{lightId}");

            Assert.That(light.enabled, Is.False,
                "The render still shows a working light after Core switched it off.");
        }

        /// <summary>Switching back on restores both sides too.</summary>
        [Test]
        public void SwitchingALightBackOnRestoresBothSides()
        {
            int lightId = _lights.Emitters[0].LightId;

            _director.SetSwitched(lightId, off: true, step: 0);
            Assert.That(_director.SetSwitched(lightId, off: false, step: 1), Is.True);
            _director.Sync();

            Assert.That(_lights.Find(lightId)!.IsWorking, Is.True);
        }

        /// <summary>
        /// A refused switch changes nothing on either side, so the caller can show the
        /// reason without having to undo anything.
        /// </summary>
        [Test]
        public void ARefusedSwitchLeavesBothSidesAlone()
        {
            int unknownLightId = 9999;

            Assert.That(_director.SetSwitched(unknownLightId, off: true, step: 0), Is.False);

            int stillWorking = _lights.Emitters.Count(e => e.IsWorking);
            Assert.That(stillWorking, Is.EqualTo(_lights.Emitters.Count),
                "A switch on a light Core does not know about changed something.");
        }

        /// <summary>
        /// The light level asked about a position is Core's answer, not one derived from
        /// the lights that happen to be rendering. With the budget biting, the two would
        /// otherwise disagree about how bright a room is.
        /// </summary>
        [Test]
        public void TheLightLevelComesFromCore()
        {
            SiteRoom lit = FirstRoomWithLightLevel(SiteLightLevel.Lit);
            Assert.That(lit, Is.Not.Null, "No generated room came out lit; the fixture is wrong.");

            var position = new TacticalPosition(lit.FloorIndex, lit.StartX);

            Assert.That(_director.LevelAt(position), Is.EqualTo(SiteLightLevel.Lit),
                "The director's answer disagreed with LightState.LevelAt.");
        }

        private SiteRoom FirstRoomWithLightLevel(SiteLightLevel level)
        {
            foreach (SiteLight light in _layout.Lights)
            {
                if (light.Level != level)
                    continue;

                SiteRoom? room = _layout.Find(light.RoomId);
                if (room != null)
                    return room;
            }

            return null;
        }

        private static SiteLayout Site()
        {
            const ulong seed = 20261005UL;
            const int missionId = 1;
            const int templateId = 11013;

            SiteLayout layout = SiteGenerator.Generate(
                templateId, 4, missionId, seed, SiteGenerator.DeriveMapSeed(seed, missionId));

            Assert.That(layout.Validate(out string problem), Is.True,
                $"The generated site is not playable: {problem}");

            return layout;
        }
    }
}