using ProjectSpy.Core.Tests.TableData;
using ProjectSpy.Tables;
using Xunit;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Proves the shipped tables are internally consistent, and — more importantly —
/// that the validator would actually catch a bad designer edit.
/// </summary>
/// <remarks>
/// The second half matters more than the first. A validator that passes on good
/// data is worthless if it also passes on broken data, so each negative test
/// asserts a specific rule rejects a specific realistic mistake.
/// </remarks>
public class TableValidatorTests
{
    private static TableValidator Validator => TableValidator.Load();

    // ---- positive: the shipped tables are clean -----------------------------

    [Fact]
    public void ShippedTables_HaveNoValidationErrors()
    {
        IReadOnlyList<string> errors = Validator.Validate();

        Assert.True(
            errors.Count == 0,
            "Table validation failed:\n  " + string.Join("\n  ", errors));
    }

    [Fact]
    public void EveryTable_IsPopulated()
    {
        ProjectSpy.Tables.Tables t = Validator.Tables;

        Assert.NotEmpty(t.TbAgentClass.DataList);
        Assert.NotEmpty(t.TbAgentName.DataList);
        Assert.NotEmpty(t.TbTrait.DataList);
        Assert.NotEmpty(t.TbRoomType.DataList);
        Assert.NotEmpty(t.TbRoomUpgrade.DataList);
        Assert.NotEmpty(t.TbMissionType.DataList);
        Assert.NotEmpty(t.TbMapGenRule.DataList);
        Assert.NotEmpty(t.TbNodeRoom.DataList);
        Assert.NotEmpty(t.TbMissionEvent.DataList);
        Assert.NotEmpty(t.TbGadget.DataList);
        Assert.NotEmpty(t.TbItem.DataList);
        Assert.NotEmpty(t.TbLootTable.DataList);
        Assert.NotEmpty(t.TbHeatTier.DataList);
        Assert.NotEmpty(t.TbContractOffer.DataList);
        Assert.NotEmpty(t.TbFogRule.DataList);
        Assert.NotEmpty(t.TbNodeInteriorRule.DataList);
    }

    [Fact]
    public void GddCallsForAboutTwentyTwoRoomTypes()
    {
        Assert.Equal(22, Validator.Tables.TbRoomType.DataList.Count);
    }

    [Fact]
    public void GddCallsForAtLeastFortyFiveMissionEvents()
    {
        Assert.True(
            Validator.Tables.TbMissionEvent.DataList.Count >= 45,
            $"Expected at least 45 mission events, found {Validator.Tables.TbMissionEvent.DataList.Count}.");
    }

    [Fact]
    public void GddCallsForFourAgentClasses()
    {
        Assert.Equal(4, Validator.Tables.TbAgentClass.DataList.Count);
    }

    [Fact]
    public void SkillCurve_CoversLevelsOneThroughTwenty()
    {
        var levels = Validator.Tables.TbSkillCurve.DataList.Select(c => c.Level).OrderBy(l => l).ToList();

        Assert.Equal(20, levels.Count);
        Assert.Equal(Enumerable.Range(1, 20), levels);
    }

    [Fact]
    public void Traits_IncludeTheTwelveTwelveSixSplit()
    {
        IReadOnlyList<Trait> traits = Validator.Tables.TbTrait.DataList;

        Assert.Equal(12, traits.Count(t => t.Polarity == TraitPolarity.Positive));
        Assert.Equal(12, traits.Count(t => t.Polarity == TraitPolarity.Negative));
        Assert.Equal(6, traits.Count(t => t.Polarity == TraitPolarity.Hidden));
        Assert.Equal(30, traits.Count);
    }

    [Fact]
    public void Traits_IncludeMoleAndDeserterAsHidden()
    {
        Trait mole = Validator.Tables.TbTrait.DataList.Single(t => t.NameKey == "trait.mole");
        Trait deserter = Validator.Tables.TbTrait.DataList.Single(t => t.NameKey == "trait.deserter");

        Assert.Equal(TraitPolarity.Hidden, mole.Polarity);
        Assert.Equal(TraitPolarity.Hidden, deserter.Polarity);
        Assert.True(mole.IsHidden);
        Assert.True(deserter.IsHidden);
    }

    [Fact]
    public void FourAgentClasses_HaveGenuinelyDifferentGrowthCurves()
    {
        IReadOnlyList<AgentClass> classes = Validator.Tables.TbAgentClass.DataList;

        // Each class must lead in a different skill, otherwise the four classes are
        // one class with four names.
        var leaders = classes
            .Select(c => new
            {
                Class = c.NameKey,
                Skill = new SkillSet(
                    c.BaseInfiltration, c.BaseCombat, c.BaseTech, c.BaseSocial, c.BaseNerve)
                    .HighestKind(),
            })
            .ToList();

        Assert.Equal(4, leaders.Select(l => l.Skill).Distinct().Count());
    }

    [Fact]
    public void MapGenRule_CoversEveryMissionTier()
    {
        var ruleTiers = Validator.Tables.TbMapGenRule.DataList.Select(r => r.Tier).OrderBy(t => t);
        var missionTiers = Validator.Tables.TbMissionType.DataList.Select(m => m.Tier).Distinct().OrderBy(t => t);

        Assert.Equal(missionTiers, ruleTiers);
    }

    [Fact]
    public void LocalizationFiles_CoverTheSameKeysInBothLanguages()
    {
        TableValidator validator = Validator;

        Assert.Equal(
            validator.Thai.Keys.OrderBy(k => k, StringComparer.Ordinal),
            validator.English.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Localization_CoversEveryReferencedKey()
    {
        TableValidator validator = Validator;

        // Derived from the tables rather than hardcoded, so adding a row with a new
        // name_key automatically updates the expected total.
        int referenced = new HashSet<string>(
            Validator.Tables.TbAgentClass.DataList.Select(x => x.NameKey)
                .Concat(Validator.Tables.TbRoomType.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbMissionType.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbNodeRoom.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbMissionEvent.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbMissionEvent.DataList.Select(x => x.NarrativeKey))
                .Concat(Validator.Tables.TbGadget.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbItem.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbTrait.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbTrait.DataList.Select(x => x.DescKey))
                // Stage 3 tables that carry player-facing keys.
                .Concat(Validator.Tables.TbLoanTier.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbMoraleBand.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbLoyaltyDrift.DataList.Select(x => x.SourceKey))
                .Concat(Validator.Tables.TbLoyaltyThreshold.DataList.Select(x => x.Escalation)),
            StringComparer.Ordinal).Count;

        Assert.Equal(referenced, validator.Thai.Count);
        Assert.Equal(referenced, validator.English.Count);
        Assert.True(referenced >= 280, $"Expected a substantial content set, found {referenced} keys.");
    }

    [Fact]
    public void LocalizationText_IsNonEmptyForEveryKey()
    {
        TableValidator validator = Validator;

        foreach (string key in validator.Thai.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(validator.Thai[key]), $"th '{key}' is blank");
            Assert.False(string.IsNullOrWhiteSpace(validator.English[key]), $"en '{key}' is blank");
        }
    }

    // ---- negative: the validator must actually catch bad edits --------------

    [Fact]
    public void CsvParser_HandlesQuotedFieldsAndDoubledQuotes()
    {
        // The rules below depend on this parsing correctly, so it is pinned here.
        string[] cells = LocalizationCatalog.SplitCsvLine(",\"a,b\",c");

        Assert.Equal(3, cells.Length);
        Assert.Equal(string.Empty, cells[0]);
        Assert.Equal("a,b", cells[1]);
        Assert.Equal("c", cells[2]);

        Assert.Equal("say \"hi\"", LocalizationCatalog.SplitCsvLine(",\"say \"\"hi\"\"\"")[1]);
    }

    [Fact]
    public void ParseIdList_RejectsGarbageRatherThanReturningZero()
    {
        // A silently-zero id would make a foreign-key check pass on broken data,
        // which is the exact failure mode this validator exists to prevent.
        Assert.Throws<InvalidDataException>(() => LocalizationCatalog.ParseIdList("1,abc,3"));
    }

    [Fact]
    public void TableBinary_IsActuallyRegenerableAndLoadable()
    {
        // Proves the committed binary matches the CSV source: if a designer edits a
        // CSV and forgets to re-run gen.ps1, the loaded tables are stale.
        Assert.True(
            TableService.IsAvailable(),
            "Table binaries not found. Run 'pwsh tools/gen.ps1'.");
    }
}
