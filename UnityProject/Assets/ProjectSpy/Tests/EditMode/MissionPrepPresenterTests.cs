using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Missions;
using ProjectSpy.Unity.UI.Presenters;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves the readiness maths on the mission preparation screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief names readiness maths as a thing worth testing, and it is: this is the
    /// number the player reads before committing three agents to a mission they might lose.
    /// A readiness ring that reads 100% on a squad Core refused to dispatch is how a
    /// player loses an agent, and it is a pure-function bug rather than a tuning one.
    /// </para>
    /// <para>
    /// Core's <c>MissionDifficultyEstimate</c> is a positional record, so these tests build
    /// one directly instead of going through the estimator. The estimator's own arithmetic
    /// is Core's to test; what is being checked here is that the UI reports Core's verdict
    /// without softening, merging or dropping it.
    /// </para>
    /// </remarks>
    public sealed class MissionPrepPresenterTests
    {
        private static MissionDifficultyEstimate Estimate(
            int teamPower = 100,
            int requiredPower = 100,
            IReadOnlyList<DispatchRefusal>? refusals = null)
        {
            var terms = new List<DifficultyTerm>
            {
                new("difficulty.term.base", requiredPower),
            };

            var squadTerms = new List<DifficultyTerm>
            {
                new("difficulty.term.team.infiltration", teamPower),
            };

            return new MissionDifficultyEstimate(
                Total: Math.Max(0, requiredPower - teamPower),
                Terms: terms,
                SquadTerms: squadTerms,
                Band: requiredPower <= teamPower
                    ? ProjectSpy.Tables.DifficultyClass.Easy
                    : ProjectSpy.Tables.DifficultyClass.Hard,
                Refusals: refusals ?? new List<DispatchRefusal>(),
                TeamPower: teamPower,
                RequiredPower: requiredPower);
        }

        [Test]
        public void ASquadExactlyAsStrongAsTheSiteIsReady()
        {
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 100, requiredPower: 100), selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.IsReady, Is.True);
            Assert.That(readiness.Blockers, Is.Empty);
            Assert.That(MissionPrepPresenter.CanDispatch(readiness), Is.True);
        }

        [Test]
        public void TooFewAgentsBlocksEvenForAStrongSquad()
        {
            // The squad's power is irrelevant: Core will not dispatch two agents for a
            // three-agent job, and the readout must say so rather than showing a full ring.
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 500, requiredPower: 100), selected: 2, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.IsReady, Is.False);
            Assert.That(readiness.Blockers.Select_Keys(), Contains.Item("mission.readiness.too_few"));
        }

        [Test]
        public void TooManyAgentsBlocksWhenCoreSetsAMaximum()
        {
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(), selected: 6, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.Blockers.Select_Keys(), Contains.Item("mission.readiness.too_many"));
        }

        [Test]
        public void ZeroMaximumMeansUnbounded()
        {
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(), selected: 9, minimumAgents: 3, maximumAgents: 0);

            Assert.That(readiness.IsReady, Is.True, "a maximum of zero means Core sets no ceiling");
        }

        /// <summary>
        /// The bug the brief's phrasing warns about, stated as a test.
        /// </summary>
        [Test]
        public void ASquadTooWeakForTheSiteIsBlockedNotMerelyWarned()
        {
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 40, requiredPower: 100), selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.IsReady, Is.False);
            Assert.That(readiness.PowerMargin, Is.LessThan(0));
            Assert.That(readiness.Blockers.Select_Keys(), Contains.Item("mission.readiness.under_powered"));
        }

        [Test]
        public void CoreRefusalsBecomeBlockers()
        {
            // Core's own refusal reasons, verbatim. A missing role must reach the player
            // in Core's words rather than a UI summary that might drop the agent's name.
            var refusals = new List<DispatchRefusal>
            {
                DispatchRefusal.About(DispatchRefusalReason.SquadTooSmall, new AgentId(7), 4),
            };

            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(refusals: refusals), selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.IsReady, Is.False);
            Assert.That(readiness.Blockers.Select_Keys(), Contains.Item("dispatch.refusal.squadtoosmall"));

            ReadinessBlocker blocker = readiness.Blockers[0];
            Assert.That(blocker.AgentId, Is.EqualTo(new AgentId(7)),
                "the refusal names an agent, and the UI must be able to point at them");
        }

        [Test]
        public void UnknownMissionTypeBlocksRatherThanReportingZero()
        {
            // A null estimate means the mission type row is missing. Reporting "difficulty 0,
            // ready" would tell the player the safest thing possible about a mission nobody
            // can describe.
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                null, selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.IsReady, Is.False);
            Assert.That(readiness.Blockers.Select_Keys(), Contains.Item("mission.readiness.unknown_type"));
        }

        [Test]
        public void PercentIsCappedAtOneHundred()
        {
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 400, requiredPower: 100), selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.Percent, Is.EqualTo(100),
                "a bar that overflows its track reads as a bug to the player");
        }

        [Test]
        public void PercentScalesBelowTheCap()
        {
            SquadReadiness half = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 50, requiredPower: 100), selected: 3, minimumAgents: 3, maximumAgents: 5);
            SquadReadiness threeQuarters = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 75, requiredPower: 100), selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(half.Percent, Is.EqualTo(50));
            Assert.That(threeQuarters.Percent, Is.EqualTo(75));
        }

        [Test]
        public void ZeroRequiredPowerIsTreatedAsFullyReady()
        {
            SquadReadiness readiness = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 0, requiredPower: 0), selected: 3, minimumAgents: 3, maximumAgents: 5);

            Assert.That(readiness.Percent, Is.EqualTo(100),
                "a division by zero would otherwise produce an undefined ring");
        }

        [Test]
        public void BandKeyIsCoreBand()
        {
            SquadReadiness strong = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 200, requiredPower: 100), 3, 3, 5);
            SquadReadiness weak = MissionPrepPresenter.Readiness(
                Estimate(teamPower: 20, requiredPower: 100), 3, 3, 5);

            Assert.That(strong.BandKey, Is.EqualTo("difficulty.band.easy"));
            Assert.That(weak.BandKey, Is.EqualTo("difficulty.band.hard"));
        }

        [Test]
        public void BreakdownCarriesCoreTermsUnmodifiedAndBothSides()
        {
            var estimate = Estimate(teamPower: 90, requiredPower: 100);

            IReadOnlyList<MissionPrepPresenter.DifficultyRow> rows = MissionPrepPresenter.Breakdown(estimate);

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(Enumerable.Count(rows, r => r.Side == MissionPrepPresenter.DifficultyRowSide.Demand), Is.EqualTo(1));
            Assert.That(Enumerable.Count(rows, r => r.Side == MissionPrepPresenter.DifficultyRowSide.Supply), Is.EqualTo(1));

            foreach (MissionPrepPresenter.DifficultyRow row in rows)
            {
                Assert.That(row.Delta, Is.Not.EqualTo(0), "Core drops zero terms, so a zero here means we invented one");
            }
        }

        [Test]
        public void BreakdownOfANullEstimateIsEmptyRatherThanZeroFilled()
        {
            Assert.That(MissionPrepPresenter.Breakdown(null), Is.Empty);
        }

        [Test]
        public void BreakdownIsNotPaddedToLookThorough()
        {
            // The brief: an honest number "never a fudge". A two-term estimate must render
            // as two lines, not six with four invented to make the panel look fuller.
            var estimate = Estimate();

            Assert.That(MissionPrepPresenter.Breakdown(estimate).Count, Is.EqualTo(2));
        }

        [Test]
        public void IntelPreviewOfNullIsEmpty()
        {
            Assert.That(MissionPrepPresenter.IntelPreview(null), Is.Empty);
        }

        [Test]
        public void IntelPreviewGroupsRoomEntriesByFloorInOrder()
        {
            var snapshot = new IntelSnapshot
            {
                SiteId = 11001,
                IntelPercent = 80,
                Band = IntelBand.Details,
                Entries = new[]
                {
                    new IntelRoomEntry { RoomId = new SiteRoomId(2), FloorIndex = 1, NameKey = "room.b", Confidence = IntelConfidence.Reported },
                    new IntelRoomEntry { RoomId = new SiteRoomId(1), FloorIndex = 0, NameKey = "room.a", Confidence = IntelConfidence.Reported },
                    new IntelRoomEntry { RoomId = new SiteRoomId(3), FloorIndex = 1, NameKey = "room.c", Confidence = IntelConfidence.Reported },
                },
            };

            IReadOnlyList<MissionPrepPresenter.IntelFloorPreview> floors =
                MissionPrepPresenter.IntelPreview(snapshot);

            Assert.That(floors.Count, Is.EqualTo(2));
            Assert.That(floors[0].FloorIndex, Is.EqualTo(0));
            Assert.That(floors[0].Facts.Count, Is.EqualTo(1));
            Assert.That(floors[1].FloorIndex, Is.EqualTo(1));
            Assert.That(floors[1].Facts.Count, Is.EqualTo(2));
        }

        [Test]
        public void EveryIntelFactCarriesItsConfidence()
        {
            var snapshot = new IntelSnapshot
            {
                Entries = new[]
                {
                    new IntelRoomEntry { FloorIndex = 0, NameKey = "room.a", Confidence = IntelConfidence.Reported },
                    new IntelRoomEntry { FloorIndex = 0, NameKey = "room.b", Confidence = IntelConfidence.Stale },
                },
            };

            MissionPrepPresenter.IntelFloorPreview floor = MissionPrepPresenter.IntelPreview(snapshot)[0];

            // The brief forbids hiding the maths, and confidence is part of the maths: a
            // stale fact and a fresh one must be distinguishable on screen.
            Assert.That(floor.ConfirmedCount, Is.EqualTo(1));
            Assert.That(floor.Facts[0].Confidence, Is.EqualTo(IntelConfidence.Reported));
            Assert.That(floor.Facts[1].Confidence, Is.EqualTo(IntelConfidence.Stale));
        }
    }

    /// <summary>
    /// Small readability helper for the blocker assertions.
    /// </summary>
    /// <remarks>
    /// NUnit's <c>Has.Exactly(...).Members</c> would work but reads worse at the call site
    /// than <c>Contains.Item</c> on a projected sequence, and every assertion here is about
    /// one key being present among several.
    /// </remarks>
    internal static class BlockerExtensions
    {
        /// <summary>Projects blockers to their localization keys.</summary>
        internal static IEnumerable<string> Select_Keys(this IReadOnlyList<ReadinessBlocker> blockers)
        {
            foreach (ReadinessBlocker blocker in blockers)
                yield return blocker.Key;
        }
    }
}
