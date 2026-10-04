using System.Collections.Generic;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Unity.UI.Presenters;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves the intel band preview is right, and that it says nothing about poisoned
    /// operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief's rule is exact: "Discovered operations show their status; poisoned ones
    /// do not." That is a security mechanic, not a display preference — a poisoned
    /// operation's real status is information the enemy does not want the player to have,
    /// and Core marks it precisely so the UI cannot leak it by rendering honestly.
    /// </para>
    /// <para>
    /// So the interesting assertion here is negative: the presenter must produce the
    /// unreported status for a poisoned operation even when handed Core's real status. A
    /// test that only checked the happy path would pass on a presenter that leaked.
    /// </para>
    /// </remarks>
    public sealed class IntelPresenterTests
    {
        /// <summary>
        /// Builds a Core view without needing a world or loaded tables.
        /// </summary>
        /// <remarks>
        /// <see cref="SleeperOperationView"/> is a positional record, so the presenter
        /// tests can construct exactly the shape they need. That is deliberate: these tests
        /// are about the UI's translation rule, and going through
        /// <see cref="SleeperOperationView.Build(WorldState, SleeperOperation, Tick)"/>
        /// would also be testing Core's estimate arithmetic, which Core's own suite covers.
        /// </remarks>
        private static SleeperOperationView View(
            SleeperStatus status = SleeperStatus.Embedded,
            int intelPercent = 40,
            IntelBand band = IntelBand.TypesAndConnections,
            int elapsed = 100,
            int remaining = 60,
            int risk = 3,
            bool canRecall = true,
            IntelBand? nextBand = IntelBand.Details,
            int percentToNext = 10,
            int ticksToNext = 20,
            bool nextRevealsAnything = true,
            int agentId = 1,
            int siteId = 11001,
            int tier = 2)
        {
            var contents = new IntelBandContents(
                RevealsEntrance: false,
                RevealsFloorCount: false,
                RevealsRoomPlacement: false,
                RevealsRoomTypes: nextRevealsAnything,
                RevealsConnections: false,
                RevealsLockStates: false,
                RevealsGuardCounts: false,
                RevealsLightLevels: false,
                RevealsPatrolRoutes: false,
                RevealsObjective: false,
                RevealsExtraction: false,
                RevealsHoldingRooms: false,
                MayBeStale: false);

            return new SleeperOperationView(
                AgentId: new AgentId(agentId),
                SiteId: siteId,
                SiteTier: tier,
                Status: status,
                IntelPercent: intelPercent,
                Band: band,
                BandContents: contents,
                ElapsedTicks: elapsed,
                EstimatedRemainingTicks: remaining,
                DiscoveryRiskPerTickPercent: risk,
                NextBand: nextBand,
                PercentToNextBand: percentToNext,
                EstimatedTicksToNextBand: ticksToNext,
                NextBandContents: nextRevealsAnything ? contents : null,
                CanRecall: canRecall,
                HeatIfDiscovered: 10,
                RecallKeepsIntelPercent: intelPercent);
        }

        [Test]
        public void DiscoveredOperationShowsItsRealStatus()
        {
            IntelPresenter.SleeperRow row = IntelPresenter.Row(
                View(status: SleeperStatus.Discovered), discovered: true);

            Assert.That(row.StatusKey, Is.EqualTo("sleeper.status.discovered"));
            Assert.That(row.IsDiscovered, Is.True);
            Assert.That(row.IsPoisoned, Is.False);
        }

        /// <summary>The rule the whole mechanic rests on.</summary>
        [Test]
        public void PoisonedOperationReportsNothingToThePlayer()
        {
            IntelPresenter.SleeperRow row = IntelPresenter.Row(
                View(status: SleeperStatus.Poisoned), discovered: false);

            Assert.That(row.StatusKey, Is.EqualTo("sleeper.status.unreported"));
            Assert.That(row.StatusKey, Does.Not.Contain("poisoned"),
                "leaking the poisoned status through the UI would defeat the mechanic");
        }

        [Test]
        public void UndiscoveredButHealthyOperationAlsoReportsNothing()
        {
            // The player has not found out yet, so the honest answer is "nothing to
            // report" regardless of whether the operation is in trouble.
            IntelPresenter.SleeperRow row = IntelPresenter.Row(
                View(status: SleeperStatus.Embedded), discovered: false);

            Assert.That(row.StatusKey, Is.EqualTo("sleeper.status.unreported"));
            Assert.That(row.IsPoisoned, Is.False);
        }

        [Test]
        public void RowCarriesCoreNumbersUnchanged()
        {
            IntelPresenter.SleeperRow row = IntelPresenter.Row(
                View(elapsed: 1234, remaining: 567, risk: 9, intelPercent: 42), discovered: true);

            Assert.That(row.ElapsedTicks, Is.EqualTo(1234));
            Assert.That(row.EstimatedRemainingTicks, Is.EqualTo(567));
            Assert.That(row.DiscoveryRiskPercent, Is.EqualTo(9));
            Assert.That(row.IntelPercent, Is.EqualTo(42));
            Assert.That(row.IsAtRisk, Is.True);
        }

        [Test]
        public void ZeroRiskMeansNotAtRisk()
        {
            IntelPresenter.SleeperRow row = IntelPresenter.Row(View(risk: 0), discovered: true);

            Assert.That(row.IsAtRisk, Is.False);
        }

        [Test]
        public void ProgressPercentIsClampedToTheBandScale()
        {
            Assert.That(IntelPresenter.Row(View(intelPercent: 150), true).ProgressPercent, Is.EqualTo(100));
            Assert.That(IntelPresenter.Row(View(intelPercent: -20), true).ProgressPercent, Is.EqualTo(0));
        }

        [Test]
        public void BandPreviewNamesTheNextBandAndWhatItReveals()
        {
            IntelPresenter.BandPreview preview = IntelPresenter.Preview(
                View(nextBand: IntelBand.Details, nextRevealsAnything: true));

            Assert.That(preview.NextBandKey, Is.EqualTo("intel.band.details"));
            Assert.That(preview.IsComplete, Is.False);
            Assert.That(preview.Unlocks, Is.Not.Empty);
        }

        /// <summary>
        /// At complete there is no next band, so the panel must not draw a progress bar.
        /// </summary>
        [Test]
        public void CompleteOperationHasNoNextBandToPreview()
        {
            IntelPresenter.BandPreview preview = IntelPresenter.Preview(
                View(band: IntelBand.Complete, nextBand: null, nextRevealsAnything: false));

            Assert.That(preview.NextBandKey, Is.Empty);
            Assert.That(preview.IsComplete, Is.True);
            Assert.That(preview.Unlocks, Is.Empty);
        }

        [Test]
        public void EverySetFlagInTheNextBandBecomesAnUnlockKey()
        {
            var contents = new IntelBandContents(
                RevealsEntrance: true,
                RevealsFloorCount: false,
                RevealsRoomPlacement: false,
                RevealsRoomTypes: false,
                RevealsConnections: false,
                RevealsLockStates: true,
                RevealsGuardCounts: false,
                RevealsLightLevels: false,
                RevealsPatrolRoutes: true,
                RevealsObjective: false,
                RevealsExtraction: true,
                RevealsHoldingRooms: false,
                MayBeStale: false);

            var view = View() with { NextBandContents = contents };

            IntelPresenter.SleeperRow row = IntelPresenter.Row(view, discovered: true);

            Assert.That(row.NextBandUnlocks, Is.EquivalentTo(new[]
            {
                "intel.unlock.entrance",
                "intel.unlock.lock_states",
                "intel.unlock.patrol_routes",
                "intel.unlock.extraction",
            }));
        }

        [Test]
        public void StaleCaveatIsReportedSeparatelyFromUnlocks()
        {
            // MayBeStale is a warning about the whole band, not one more thing it reveals.
            // Filing it as an unlock would tell the player they will learn something when
            // what they will learn may be wrong.
            var contents = new IntelBandContents(
                RevealsEntrance: false,
                RevealsFloorCount: false,
                RevealsRoomPlacement: false,
                RevealsRoomTypes: true,
                RevealsConnections: false,
                RevealsLockStates: false,
                RevealsGuardCounts: false,
                RevealsLightLevels: false,
                RevealsPatrolRoutes: false,
                RevealsObjective: false,
                RevealsExtraction: false,
                RevealsHoldingRooms: false,
                MayBeStale: true);

            var view = View() with { NextBandContents = contents };

            IntelPresenter.SleeperRow row = IntelPresenter.Row(view, discovered: true);

            Assert.That(row.NextBandUnlocks, Contains.Item("intel.unlock.may_be_stale"));
            Assert.That(row.NextBandUnlocks, Contains.Item("intel.unlock.room_types"));
        }

        [Test]
        public void RowsAppliesDiscoveryIndividually()
        {
            var views = new List<SleeperOperationView>
            {
                View(agentId: 1, status: SleeperStatus.Discovered),
                View(agentId: 2, status: SleeperStatus.Poisoned),
                View(agentId: 3, status: SleeperStatus.Embedded),
            };

            var rows = IntelPresenter.Rows(views, id => id.Value == 1);

            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[0].StatusKey, Is.EqualTo("sleeper.status.discovered"));
            Assert.That(rows[1].StatusKey, Is.EqualTo("sleeper.status.unreported"));
            Assert.That(rows[1].IsPoisoned, Is.True);
            Assert.That(rows[2].StatusKey, Is.EqualTo("sleeper.status.unreported"));
        }

        [Test]
        public void NullDiscoveryPredicateTreatsEveryOperationAsUndiscovered()
        {
            var views = new List<SleeperOperationView> { View(status: SleeperStatus.Discovered) };

            var rows = IntelPresenter.Rows(views, null);

            Assert.That(rows[0].StatusKey, Is.EqualTo("sleeper.status.unreported"));
        }

        [Test]
        public void RecallIsFlaggedCostlyOnceIntelIsComplete()
        {
            var view = View(band: IntelBand.Complete, nextBand: null, intelPercent: 100);

            Assert.That(IntelPresenter.RecallIsCostly(view, 20), Is.True,
                "recalling a finished operation wastes it, and the tooltip should say so");
        }

        [Test]
        public void RecallOfAFreshOperationIsNotFlaggedCostly()
        {
            var view = View(band: IntelBand.Layout, nextBand: IntelBand.TypesAndConnections, intelPercent: 5);

            Assert.That(IntelPresenter.RecallIsCostly(view, 20), Is.False);
        }

        [Test]
        public void EmptyViewListProducesEmptyRows()
        {
            Assert.That(IntelPresenter.Rows(null, _ => true), Is.Empty);
            Assert.That(IntelPresenter.Rows(new List<SleeperOperationView>(), _ => true), Is.Empty);
        }
    }
}
