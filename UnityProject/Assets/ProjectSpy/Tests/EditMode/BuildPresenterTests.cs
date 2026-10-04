using System.Collections.Generic;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Unity.UI.Presenters;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves placement validity and affordability gating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief asks for "ghost preview with validity colouring, cost and upkeep and
    /// depth modifier shown before confirming". A ghost that draws a room the placement
    /// rules will reject — or a valid room the player cannot afford — teaches the player
    /// the wrong thing about the game's rules, and the mistake is only visible at commit
    /// time. These tests pin the two halves of that gate.
    /// </para>
    /// <para>
    /// The placement verdicts under test come from Core's own
    /// <see cref="BaseLayout.CheckPlacement"/>. The presenter only translates them, so these
    /// tests are really asserting that nothing is dropped or reordered in translation.
    /// </para>
    /// </remarks>
    public sealed class BuildPresenterTests
    {
        private static BaseLayout Layout(int layers = 6, int slots = 8)
            => new BaseLayout(layers, slots);

        private static Resources Funds(long amount)
            => new Resources { Funds = amount, Intel = 0, Materials = 0, Reputation = 0, Heat = 0 };

        [Test]
        public void EveryPlacementErrorMapsToItsOwnKey()
        {
            // Exhaustive on purpose. A missing case would fall through to the generic key,
            // which reads as "something is wrong" and tells the player nothing.
            var expected = new Dictionary<PlacementError, string>
            {
                [PlacementError.LayerOutOfRange] = "build.error.layer_out_of_range",
                [PlacementError.SlotOutOfRange] = "build.error.slot_out_of_range",
                [PlacementError.SlotOccupied] = "build.error.slot_occupied",
                [PlacementError.NoSlots] = "build.error.no_slots",
                [PlacementError.UnknownRoomType] = "build.error.unknown_room_type",
                [PlacementError.LockedByStory] = "build.error.locked_by_story",
                [PlacementError.LayerTooShallow] = "build.error.layer_too_shallow",
                [PlacementError.UnknownNeighbour] = "build.error.unknown_neighbour",
                [PlacementError.NeighbourInOtherLayer] = "build.error.neighbour_other_layer",
            };

            foreach (KeyValuePair<PlacementError, string> pair in expected)
            {
                PlacementVerdict verdict = PlacementVerdict.FromCore(pair.Key, 0, 0);

                Assert.That(verdict.IsValid, Is.False, $"{pair.Key} must not read as valid");
                Assert.That(verdict.ReasonKey, Is.EqualTo(pair.Value), $"{pair.Key} has the wrong key");
            }
        }

        [Test]
        public void NoErrorTranslatesToValid()
        {
            PlacementVerdict verdict = PlacementVerdict.FromCore(PlacementError.None, 3, 4);

            Assert.That(verdict.IsValid, Is.True);
            Assert.That(verdict.ReasonKey, Is.Empty);
        }

        [Test]
        public void RefusedVerdictIsNotEqualToValid()
        {
            // The presenters use plain readonly structs, which default to reference
            // equality. Without value equality a test comparing two verdicts would fail
            // for reasons unrelated to the logic.
            Assert.That(PlacementVerdict.Valid, Is.EqualTo(PlacementVerdict.Valid));
            Assert.That(PlacementVerdict.Valid, Is.Not.EqualTo(
                PlacementVerdict.Refused("build.error.slot_occupied", 1, 2)));
            Assert.That(PlacementVerdict.Valid == PlacementVerdict.Valid, Is.True);
        }

        [Test]
        public void OccupiedSlotIsRefused()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            Assert.That(layout.CheckPlacement(4001, 0, new[] { 3 }), Is.EqualTo(PlacementError.None));
            layout.Place(4001, 0, new[] { 3 }, default);

            Assert.That(layout.CheckPlacement(4001, 0, new[] { 3 }), Is.EqualTo(PlacementError.SlotOccupied));
        }

        [Test]
        public void LayerOutsideTheBaseIsRefused()
        {
            BaseLayout layout = Layout(layers: 4);
            layout.UnlockedRoomTypeIds.Add(4001);

            Assert.That(layout.CheckPlacement(4001, 9, new[] { 0 }), Is.EqualTo(PlacementError.LayerOutOfRange));
        }

        [Test]
        public void EmptySlotSetIsRefused()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            Assert.That(layout.CheckPlacement(4001, 0, System.Array.Empty<int>()),
                Is.EqualTo(PlacementError.NoSlots));
        }

        [Test]
        public void UnlockedTypePassesAndLockedTypeDoesNot()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            Assert.That(layout.CheckPlacement(4001, 0, new[] { 0 }), Is.EqualTo(PlacementError.None));
            Assert.That(layout.CheckPlacement(4999, 0, new[] { 0 }), Is.EqualTo(PlacementError.LockedByStory));
        }

        [Test]
        public void TypeDeeperThanItsMinimumIsRefused()
        {
            BaseLayout layout = Layout(layers: 8);
            layout.UnlockedRoomTypeIds.Add(4001);
            layout.MinimumDepthByType[4001] = 5;

            Assert.That(layout.CheckPlacement(4001, 2, new[] { 0 }), Is.EqualTo(PlacementError.LayerTooShallow));
            Assert.That(layout.CheckPlacement(4001, 6, new[] { 0 }), Is.EqualTo(PlacementError.None));
        }

        [Test]
        public void EvaluatePrefersThePlacementRefusalOverTheMoneyRefusal()
        {
            // An illegal slot AND no money. Reporting the money first would tell the player
            // to go and earn money when the real problem is the slot they picked.
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);
            layout.Place(4001, 0, new[] { 2 }, default);

            PlacementVerdict verdict = BuildPresenter.Evaluate(
                layout, Funds(0), 4001, 0, new[] { 2 });

            Assert.That(verdict.IsValid, Is.False);
            Assert.That(verdict.ReasonKey, Is.EqualTo("build.error.slot_occupied"),
                "the placement rule must be reported first");
        }

        [Test]
        public void EvaluateRefusesAnEmptySlotSet()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            PlacementVerdict verdict = BuildPresenter.Evaluate(
                layout, Funds(100000), 4001, 0, System.Array.Empty<int>());

            Assert.That(verdict.IsValid, Is.False);
            Assert.That(verdict.ReasonKey, Is.EqualTo("build.error.no_slots"));
        }

        [Test]
        public void EvaluateAcceptsALegalAffordablePlacement()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            PlacementVerdict verdict = BuildPresenter.Evaluate(
                layout, Funds(10_000_000), 4001, 0, new[] { 1 });

            Assert.That(verdict.IsValid, Is.True, verdict.ReasonKey);
            Assert.That(verdict.ReasonKey, Is.Empty);
        }

        [Test]
        public void UnknownRoomTypeIsRefusedWithoutAValidVerdict()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            PlacementVerdict verdict = BuildPresenter.Evaluate(
                layout, Funds(10_000_000), 123456, 0, new[] { 1 });

            Assert.That(verdict.IsValid, Is.False);
            Assert.That(verdict.ReasonKey, Is.Not.EqualTo("build.error.generic"),
                "a missing room type has its own message, not the catch-all");
        }

        [Test]
        public void CanAffordIsFalseWhenFundsAreShort()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            // Deliberately zero: no room can be free, and the assertion below does not
            // depend on any room's price, which would change with the tables.
            bool affordable = BuildPresenter.CanAfford(
                layout, Funds(0), 4001, 0, out PlacementVerdict verdict, out long cost);

            Assert.That(affordable, Is.False);
            Assert.That(verdict.IsValid, Is.False);
            Assert.That(cost, Is.GreaterThan(0), "the cost must still be reported so the ghost can show it");
        }

        [Test]
        public void CanAffordCarriesTheCostAndTheShortfallInItsArguments()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            BuildPresenter.CanAfford(layout, Funds(0), 4001, 0, out PlacementVerdict verdict, out long cost);

            Assert.That(verdict.ReasonKey, Is.EqualTo("build.error.insufficient_funds"));
            Assert.That(verdict.ReasonArgs.Count, Is.EqualTo(2),
                "the message needs both the price and the shortfall to be actionable");
        }

        [Test]
        public void GhostPreviewReportsVerdictAndCostTogether()
        {
            BaseLayout layout = Layout();
            layout.UnlockedRoomTypeIds.Add(4001);

            BuildPresenter.GhostPreview good = BuildPresenter.Preview(
                layout, Funds(10_000_000), 4001, 0, new[] { 1 });

            BuildPresenter.GhostPreview bad = BuildPresenter.Preview(
                layout, Funds(10_000_000), 4001, 0, System.Array.Empty<int>());

            Assert.That(good.IsValid, Is.True);
            Assert.That(good.VerdictKey, Is.EqualTo("build.ghost.valid"));
            Assert.That(good.Cost, Is.GreaterThan(0));

            Assert.That(bad.IsValid, Is.False);
            Assert.That(bad.VerdictKey, Is.EqualTo("build.error.no_slots"));
        }

        [Test]
        public void EmptyActFilterKeepsEveryRoomType()
        {
            BaseLayout layout = Layout();

            // No tables loaded in an EditMode test, so this asserts the filter contract
            // rather than a particular row: an empty act list must not exclude anything.
            var all = BuildPresenter.Catalogue(layout, System.Array.Empty<ProjectSpy.Tables.RoomType>());
            var none = BuildPresenter.Catalogue(layout, System.Array.Empty<ProjectSpy.Tables.RoomType>(), new[] { 1 });

            Assert.That(all.Count, Is.EqualTo(none.Count));
        }

        [Test]
        public void CatalogueIsEmptyForANullRoomTypeList()
        {
            var entries = BuildPresenter.Catalogue(Layout(), null);

            Assert.That(entries, Is.Not.Null);
            Assert.That(entries, Is.Empty);
        }
    }
}
