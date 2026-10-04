using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Unity.Site;
using UnityEngine;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves the site assembler renders a generated layout faithfully.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief's requirement is that the assembler "must handle any generated layout
    /// without manual fixing". That is a claim about every layout the generator can produce,
    /// not about one, so these tests sweep the whole template and tier space rather than
    /// pinning a seed. A generator change that starts producing a narrower room, a
    /// connection on the wrong floor or an interval the assembler cannot render fails here
    /// instead of as a screenshot somebody has to notice.
    /// </para>
    /// <para>
    /// The assertions are deliberately about agreement between Core and Unity, not about
    /// the assembler computing anything. If a wall appeared in the wrong place, the defect
    /// would be in Core or in the conversion, and a test that reimplemented the arithmetic
    /// to check itself would pass while the player looked at a broken building.
    /// </para>
    /// </remarks>
    public sealed class SiteAssemblerTests
    {
        private const int SeedsPerCell = 4;

        /// <summary>
        /// Core must have its tables, or every assertion below would compare a fallback
        /// default against a fallback default and pass vacuously.
        /// </summary>
        [OneTimeSetUp]
        public void RequireTables()
        {
            Assert.That(SimulationRules.AreTablesLoaded, Is.True,
                "Core tables are not loaded. Run 'pwsh tools/gen.ps1'. Without them these " +
                "tests would assert that two identical fallbacks agree.");
        }

        /// <summary>
        /// Every room and connection lands where Core put it, at the right scale.
        /// </summary>
        [Test]
        public void EveryGeneratedLayoutRendersAtCoresPositions()
        {
            var failures = new List<string>();
            int layouts = 0;

            foreach (int templateId in TemplateIds())
            {
                foreach (int tier in new[] { 1, 2, 3, 4 })
                {
                    for (int seed = 0; seed < SeedsPerCell; seed++)
                    {
                        var layout = Generate(templateId, tier, seed);
                        var result = SiteAssembler.Assemble(layout);

                        try
                        {
                            layouts++;
                            AssertRooms(layout, result, templateId, tier, seed, failures);
                            AssertConnections(layout, result, templateId, tier, seed, failures);
                            AssertStoreys(layout, result, templateId, tier, seed, failures);
                        }
                        finally
                        {
                            Object.DestroyImmediate(result.Root);
                        }
                    }
                }
            }

            Assert.That(failures, Is.Empty,
                $"{failures.Count} mismatch(es) across {layouts} layout(s):\n" +
                string.Join("\n", failures.Take(20)));
        }

        /// <summary>
        /// The building is continuous: rooms on a floor tile along X without overlapping.
        /// </summary>
        [Test]
        public void RoomsOnAFloorAreLaidInOrderWithoutOverlapping()
        {
            var failures = new List<string>();

            foreach (int templateId in TemplateIds())
            {
                foreach (SiteLayout layout in SampleLayouts(templateId))
                {
                    foreach (SiteFloor floor in layout.Floors)
                    {
                        float previousEnd = float.MinValue;
                        bool first = true;

                        foreach (SiteRoom room in floor.Rooms)
                        {
                            if (!LaneUnits.IsRenderableInterval(room.StartX.Raw, room.EndX.Raw))
                                continue;

                            float start = LaneUnits.ToMetres(room.StartX.Raw);

                            if (!first && start < previousEnd - 0.001f)
                            {
                                failures.Add($"t{templateId} f{floor.Index} {room.Id} starts at " +
                                             $"{start:0.###}m but the previous room ends at {previousEnd:0.###}m");
                            }

                            previousEnd = LaneUnits.ToMetres(room.EndX.Raw);
                            first = false;
                        }
                    }
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
        }

        /// <summary>
        /// One lane unit is one metre, exactly.
        /// </summary>
        /// <remarks>
        /// This is the conversion the whole stage rests on. An off-by-a-factor-of-a-hundred
        /// would render a building a hundred times too large, which is obvious; a rounding
        /// drift would be a few millimetres and is not, so it is pinned exactly.
        /// </remarks>
        [TestCase(0, 0f)]
        [TestCase(100, 1f)]
        [TestCase(3300, 33f)]
        [TestCase(1809, 18.09f)]
        public void CentimetresConvertToMetresExactly(int centimetres, float expectedMetres)
        {
            Assert.That(LaneUnits.ToMetres(centimetres), Is.EqualTo(expectedMetres).Within(1e-4f));
            Assert.That(LaneUnits.ToCentimetres(expectedMetres), Is.EqualTo(centimetres));
        }

        /// <summary>A storey is a storey.</summary>
        [Test]
        public void ConsecutiveFloorsAreExactlyOneStoreyApart()
        {
            for (int floor = 0; floor < 10; floor++)
            {
                float y = LaneUnits.ToWorldY(floor);
                Assert.That(y, Is.EqualTo(floor * LaneUnits.StoreyHeightMetres).Within(1e-4f));
            }
        }

        /// <summary>A degenerate interval is reported rather than rendered inside-out.</summary>
        [Test]
        public void DegenerateIntervalsAreNotRenderable()
        {
            Assert.That(LaneUnits.IsRenderableInterval(0, 100), Is.True);
            Assert.That(LaneUnits.IsRenderableInterval(100, 100), Is.False);
            Assert.That(LaneUnits.IsRenderableInterval(200, 100), Is.False);
        }

        private static void AssertRooms(
            SiteLayout layout, SiteAssembler.Result result,
            int templateId, int tier, int seed, List<string> failures)
        {
            foreach (SiteRoom room in layout.Rooms)
            {
                if (!LaneUnits.IsRenderableInterval(room.StartX.Raw, room.EndX.Raw))
                    continue;

                if (!result.RoomTransforms.TryGetValue(room.Id, out Transform t))
                {
                    failures.Add($"t{templateId}/tier{tier}/seed{seed}: room {room.Id} has no transform");
                    continue;
                }

                float wantX = LaneUnits.ToMetres((room.StartX.Raw + room.EndX.Raw) / 2);
                float wantY = LaneUnits.ToWorldY(room.FloorIndex);

                if (Mathf.Abs(t.position.x - wantX) > 0.001f || Mathf.Abs(t.position.y - wantY) > 0.001f)
                {
                    failures.Add($"t{templateId}/tier{tier}/seed{seed}: room {room.Id} at " +
                                 $"({t.position.x:0.###},{t.position.y:0.###}) want ({wantX:0.###},{wantY:0.###})");
                }
            }
        }

        private static void AssertConnections(
            SiteLayout layout, SiteAssembler.Result result,
            int templateId, int tier, int seed, List<string> failures)
        {
            foreach (SiteConnection connection in layout.Connections)
            {
                if (!result.ConnectionTransforms.TryGetValue(connection.Id, out Transform t))
                {
                    failures.Add($"t{templateId}/tier{tier}/seed{seed}: connection {connection.Id} has no transform");
                    continue;
                }

                float wantX = LaneUnits.ToMetres(connection.X.Raw);
                float wantY = LaneUnits.ToWorldY(connection.FloorIndexA);

                if (Mathf.Abs(t.position.x - wantX) > 0.001f || Mathf.Abs(t.position.y - wantY) > 0.001f)
                {
                    failures.Add($"t{templateId}/tier{tier}/seed{seed}: connection {connection.Id} at " +
                                 $"({t.position.x:0.###},{t.position.y:0.###}) want ({wantX:0.###},{wantY:0.###})");
                }
            }
        }

        private static void AssertStoreys(
            SiteLayout layout, SiteAssembler.Result result,
            int templateId, int tier, int seed, List<string> failures)
        {
            for (int i = 1; i < layout.Floors.Count; i++)
            {
                float gap = LaneUnits.ToWorldY(layout.Floors[i].Index) - LaneUnits.ToWorldY(layout.Floors[i - 1].Index);
                if (Mathf.Abs(gap - LaneUnits.StoreyHeightMetres) > 0.0001f)
                {
                    failures.Add($"t{templateId}/tier{tier}/seed{seed}: storey gap {gap:0.###}m");
                }
            }

            float expectedWidth = layout.Floors.Count == 0
                ? 0f
                : layout.Floors.Max(f => LaneUnits.ToMetres(f.Span.Raw));

            if (Mathf.Abs(result.WidthMetres - expectedWidth) > 0.001f)
            {
                failures.Add($"t{templateId}/tier{tier}/seed{seed}: width {result.WidthMetres:0.###}m " +
                             $"want {expectedWidth:0.###}m");
            }
        }

        private static IEnumerable<int> TemplateIds()
            => ProjectSpy.Tables.TableService.Load().TbSiteTemplate.DataList.Select(r => r.Id);

        private static IEnumerable<SiteLayout> SampleLayouts(int templateId)
        {
            for (int seed = 0; seed < SeedsPerCell; seed++)
                yield return Generate(templateId, 1 + (seed % 4), seed);
        }

        private static SiteLayout Generate(int templateId, int tier, int seed)
        {
            ulong worldSeed = (ulong)(templateId * 1000 + tier * 100 + seed);
            return SiteGenerator.Generate(
                templateId, tier, seed + 1, worldSeed, SiteGenerator.DeriveMapSeed(worldSeed, seed + 1));
        }
    }
}