using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Audio;
using ProjectSpy.Unity.Tactical;
using UnityEngine;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves the alarm bands are distinguishable and total, and that the audio catalogue
    /// cannot drift away from the cues the alarm asks for.
    /// </summary>
    /// <remarks>
    /// These are pure mappings, so they are tested directly. The counterweight is the
    /// placeholder-generator check at the bottom, which does touch the filesystem: a
    /// catalogue that is total in code but has no clips behind it would still be silence,
    /// and silence is the failure this whole subsystem exists to avoid.
    /// </remarks>
    public class AlarmLookTests
    {
        [Test]
        public void EveryBandCoreDefinesHasALook()
        {
            foreach (AlarmBand band in Enum.GetValues(typeof(AlarmBand)))
            {
                AlarmLook look = AlarmLooks.For(band);

                Assert.That(look.Band, Is.EqualTo(band), $"{band} has no look");
                Assert.That(look.MessageKey, Is.Not.Empty, $"{band} has no message");
                Assert.That(look.StingerKey, Is.Not.Empty, $"{band} has no stinger");
                Assert.That(look.AmbientLayerKey, Is.Not.Empty, $"{band} has no bed layer");
            }
        }

        [Test]
        public void AValueCoreDoesNotDefineFallsBackToCalm()
        {
            // Rendering as Calm rather than throwing: an unstyled band should look like an
            // ordinary quiet building, not like a broken one.
            AlarmLook look = AlarmLooks.For((AlarmBand)99);

            Assert.That(look.Band, Is.EqualTo(AlarmBand.Calm));
            Assert.That(look.VignetteStrength, Is.Zero);
        }

        [Test]
        public void CalmIsUnstyledAndTheEscalationIsMonotonic()
        {
            Assert.That(AlarmLooks.For(AlarmBand.Calm).VignetteStrength, Is.Zero,
                "A quiet mission must not be tinted.");
            Assert.That(AlarmLooks.For(AlarmBand.Calm).LightTintStrength, Is.Zero);

            AlarmBand[] order =
            {
                AlarmBand.Calm, AlarmBand.Suspicious, AlarmBand.Alert,
                AlarmBand.Lockdown, AlarmBand.Burned,
            };

            for (int i = 1; i < order.Length; i++)
            {
                float previous = AlarmLooks.For(order[i - 1]).VignetteStrength;
                float current = AlarmLooks.For(order[i]).VignetteStrength;

                Assert.That(current, Is.GreaterThan(previous),
                    $"{order[i]} should close the screen in more than {order[i - 1]}.");
            }
        }

        [Test]
        public void EveryBandReadsAsADifferentColour()
        {
            var seen = new Dictionary<AlarmBand, AlarmLook>();

            foreach (AlarmBand band in AlarmLooks.Bands)
                seen[band] = AlarmLooks.For(band);

            var bands = seen.Keys.ToList();

            for (int i = 0; i < bands.Count; i++)
            {
                for (int j = i + 1; j < bands.Count; j++)
                {
                    Color a = seen[bands[i]].VignetteColour;
                    Color b = seen[bands[j]].VignetteColour;

                    float distance = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

                    Assert.That(distance, Is.GreaterThan(0.05f),
                        $"{bands[i]} and {bands[j]} are too close to tell apart.");
                }
            }
        }

        [Test]
        public void EveryCueTheAlarmAsksForIsInTheCatalogue()
        {
            var keys = new HashSet<string>(AudioCues.AllIncludingBands(), StringComparer.Ordinal);

            foreach (AlarmBand band in Enum.GetValues(typeof(AlarmBand)))
            {
                AlarmLook look = AlarmLooks.For(band);

                Assert.That(keys.Contains(look.StingerKey), Is.True, $"{look.StingerKey} is not declared");
                Assert.That(keys.Contains(look.AmbientLayerKey), Is.True, $"{look.AmbientLayerKey} is not declared");
            }
        }

        [Test]
        public void EveryPostureAndPerceptionLevelHasACue()
        {
            var keys = new HashSet<string>(AudioCues.All, StringComparer.Ordinal);

            foreach (Posture posture in Enum.GetValues(typeof(Posture)))
                Assert.That(keys.Contains(AudioCues.Footstep(posture)), Is.True, $"no footstep for {posture}");

            foreach (PerceptionLevel level in Enum.GetValues(typeof(PerceptionLevel)))
                Assert.That(keys.Contains(AudioCues.Breath(level)), Is.True, $"no breathing for {level}");
        }

        [Test]
        public void AnUndefinedPostureIsQuietRatherThanLoud()
        {
            Assert.That(AudioCues.Footstep((Posture)99), Is.EqualTo(AudioCues.FootstepCrouch),
                "A posture the game has not heard of must not be the one that announces the player.");
        }

        [Test]
        public void TheBedAccumulatesLayersSoADropKeepsWhatIsUnderneath()
        {
            Assert.That(AudioCues.LayersUpTo(AlarmBand.Calm), Has.Count.EqualTo(1),
                "Calm still has a room tone; silence is not the same as quiet.");

            var alert = AudioCues.LayersUpTo(AlarmBand.Alert);
            var suspicious = AudioCues.LayersUpTo(AlarmBand.Suspicious);

            Assert.That(alert, Has.Count.EqualTo((int)AlarmBand.Alert + 1));
            Assert.That(suspicious, Is.SubsetOf(alert),
                "Dropping from Alert to Suspicious must lose the top layer and keep the rest.");
        }

        [Test]
        public void EveryDeclaredCueHasAPlaceholderOnDisk()
        {
            var missing = new List<string>();

            foreach (string key in AudioCues.AllIncludingBands())
            {
                string asset = $"Assets/ProjectSpy/Resources/{AudioCues.ResourcePathFor(key)}.wav";

                if (!UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(asset))
                    missing.Add(key);
            }

            Assert.That(missing, Is.Empty,
                "Run ProjectSpy > Audio > Generate Placeholder Clips. Missing: "
                + string.Join(", ", missing));
        }
    }

    /// <summary>
    /// Proves the after-action text is honest about what the report does and does not
    /// contain.
    /// </summary>
    public class AfterActionReportTests
    {
        private static MissionReport MinimalReport(
            bool includeRolls = false,
            IReadOnlyList<RouteStep> routes = null) => new()
        {
            MissionId = 7,
            SiteId = 11013,
            Class = ResolveClass.Disaster,
            ClassNameKey = "class.disaster",
            ObjectiveType = ProjectSpy.Tables.ObjectiveType.StealData,
            ObjectiveNameKey = "objective.steal_data",
            ObjectiveComplete = false,
            Steps = 137,
            PeakBand = AlarmBand.Burned,
            EndBand = AlarmBand.Burned,
            IncludeRolls = includeRolls,
            Routes = routes ?? Array.Empty<RouteStep>(),
        };

        [Test]
        public void AnEmptyReportIsStillAReport()
        {
            string text = AfterActionReport.Format(MinimalReport());

            Assert.That(text, Does.Contain("Mission 7"));
            Assert.That(text, Does.Contain("site 11013"));
            Assert.That(text, Does.Contain("137 steps"));
            Assert.That(text, Does.Contain("Burned"));
        }

        [Test]
        public void NullIsEmptyRatherThanThrowing()
        {
            Assert.That(AfterActionReport.Format(null), Is.Empty);
        }

        [Test]
        public void AbsentSectionsSaySoInsteadOfLookingEmpty()
        {
            string text = AfterActionReport.Format(MinimalReport());

            // The difference between "nothing happened" and "this was not collected" is
            // exactly what a pasted bug report turns on.
            Assert.That(text, Does.Contain("(no operative recorded)").Or.Contain("SQUAD"));
            Assert.That(text, Does.Contain("(no route recorded)"));
            Assert.That(text, Does.Contain("(nothing carried out)"));
            Assert.That(text, Does.Contain("(none)"));
        }

        [Test]
        public void RoutesAreListedOneLinePerOperativeInVisitOrder()
        {
            var routes = new List<RouteStep>
            {
                new(10, 1, new AgentId(1), new SiteRoomId(1)),
                new(20, 2, new AgentId(2), new SiteRoomId(1)),
                new(30, 1, new AgentId(1), new SiteRoomId(2)),
            };

            string text = AfterActionReport.Format(
                MinimalReport(routes: routes), null, id => $"room{id.Value}");

            Assert.That(text, Does.Contain("room1 > room2"),
                "One operative's route should read as a path, in the order they walked it.");
            Assert.That(text, Does.Contain("A0002"), "Every operative with a route should be listed.");
        }

        [Test]
        public void RollsNotCollectedIsDistinctFromNoRollsHappened()
        {
            Assert.That(AfterActionReport.RollsNote(MinimalReport(includeRolls: false)),
                Does.Contain("not recorded"));

            Assert.That(AfterActionReport.RollsNote(MinimalReport(includeRolls: true)), Is.Empty,
                "When rolls were collected the report must not claim they were missing.");
        }

        [Test]
        public void LocalizationKeysAreResolvedRatherThanShippedAsKeys()
        {
            string text = AfterActionReport.Format(
                MinimalReport(), key => key == "objective.steal_data" ? "Steal the data" : key);

            Assert.That(text, Does.Contain("Steal the data"));
            Assert.That(text, Does.Not.Contain("Objective objective.steal_data"));
        }

        [Test]
        public void TheExportIsLineOrientedSoItSurvivesBeingPasted()
        {
            string text = AfterActionReport.Format(MinimalReport());

            // Players paste these into chat and bug trackers; a single enormous line would
            // be mangled by every one of them.
            Assert.That(text, Does.Contain("\n"));
            foreach (string line in text.Split('\n'))
                Assert.That(line.Length, Is.LessThan(200), "A pasted line should stay readable.");
        }
    }
}