using ProjectSpy.Core.Missions;
using Xunit;
using Xunit.Abstractions;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Smoke test: every site template, at every tier it claims, generates and validates.
/// </summary>
/// <remarks>
/// Deliberately separate from the twenty-thousand site sweep. When generation throws,
/// this test says <em>which template</em> failed, because the generator's exception text
/// is the only thing that tells you whether a floor width, a room weight or a floor kind
/// is at fault — and "one of twenty thousand failed" is not a diagnosis.
/// </remarks>
public class SiteGeneratorSmokeTest
{
    private readonly ITestOutputHelper _output;

    public SiteGeneratorSmokeTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EverySiteTemplateGeneratesAValidSiteAtEveryTier()
    {
        Assert.True(SimulationRules.AreTablesLoaded,
            "Tables did not load, so site generation is untestable (knowledge.md rule 3).");

        IReadOnlyList<ProjectSpy.Tables.SiteTemplate> templates = SimulationRules.AllSiteTemplates();
        Assert.NotEmpty(templates);

        foreach (ProjectSpy.Tables.SiteTemplate template in templates)
        {
            foreach (int tier in new[] { template.Tier, 1, 2, 3, 4 })
            {
                ulong mapSeed = SiteGenerator.DeriveMapSeed(0xC1F4E7UL, 7);
                SiteLayout layout = BuildOrExplain(template.Id, tier, mapSeed);

                Assert.True(
                    layout.Validate(out string problem),
                    $"site {template.Id} tier {tier} failed validation: {problem}");
            }
        }

        _output.WriteLine($"{templates.Count} templates x 5 tiers generated and validated.");
    }

    private static SiteLayout BuildOrExplain(int siteTemplateId, int tier, ulong mapSeed)
    {
        try
        {
            return SiteGenerator.Generate(siteTemplateId, tier, 7, 0xC1F4E7UL, mapSeed);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Generating site {siteTemplateId} at tier {tier} threw: {ex.Message}", ex);
        }
    }
}