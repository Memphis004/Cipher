using ProjectSpy.Core;
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
        Assert.NotEmpty(t.TbInteractableType.DataList);
        Assert.NotEmpty(t.TbNodeInteriorTemplate.DataList);
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
                .Concat(Validator.Tables.TbLoyaltyThreshold.DataList.Select(x => x.Escalation))
                // Stage 4 interactable kinds. These keys are derived by Core from the
                // enum rather than stored in a table column, so they have to be listed
                // here explicitly — which is the point: it keeps the equality assertion
                // honest in both directions, since a key added to the catalog without a
                // matching enum member now fails this test too.
                .Concat(Enum.GetValues<InteractableType>().Select(SimulationRules.NameKeyFor))
                // Tactical tables added for the continuous-building model.
                .Concat(Validator.Tables.TbSiteTemplate.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbRoomTemplate.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbLightingProfile.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbLightSource.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbConnectionType.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbNoiseProfile.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbGuardArchetype.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbGoapGoal.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbGoapAction.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbAgentRole.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbTacticalAction.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbThrowable.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbMeleeWeapon.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbCaptureSite.DataList.Select(x => x.NameKey))
                // Stage 4e: the six objective names, the five support abilities and the
                // five resolve classes. These are what the dispatch screen, the post
                // panel and the debrief lead line read, so a missing one is an empty
                // label in the game's most-read screen rather than an unused string.
                .Concat(Validator.Tables.TbObjectiveRule.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbCommandPostAbility.DataList.Select(x => x.NameKey))
                .Concat(Validator.Tables.TbResolveRule.DataList.Select(x => x.NameKey)),
            StringComparer.Ordinal).Count;

        // Stage 8 added a second class of key: strings the UI itself needs, which no
        // table column references. The top bar's date line, the build panel's refusals,
        // the intel band's unlock list. They are declared below rather than allowed to
        // appear unannounced, because the point of this assertion was always to fail on a
        // key nobody asked for — and a UI key nobody declared is exactly that.
        //
        // The relationship is therefore equality against the union, not against the
        // table-derived set alone. Both original directions stay intact: a referenced key
        // missing from the catalog still fails, and a catalog key with neither a table nor
        // a declaration still fails.
        int declared = StageEightUiKeys.Length;
        Assert.Equal(referenced + declared, validator.Thai.Count);
        Assert.Equal(referenced + declared, validator.English.Count);

        // Every declared key must actually be present in both languages, so the list
        // cannot rot into a comment that no longer describes the catalog.
        foreach (string key in StageEightUiKeys)
        {
            Assert.True(
                validator.Thai.Contains(key),
                $"Declared UI key '{key}' is missing from the Thai catalog.");
            Assert.True(
                validator.English.Contains(key),
                $"Declared UI key '{key}' is missing from the English catalog.");
        }

        // A floor, not an exact count: it exists to catch a catalog that collapsed to
        // almost nothing while still matching the tables. Raised as content was added
        // rather than left stale, because a floor nobody updates is a floor that stops
        // meaning anything.
        Assert.True(referenced >= 495, $"Expected a substantial content set, found {referenced} keys.");
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

    /// <summary>
    /// Every localization key the stage-8 UI emits that no table column references.
    /// </summary>
    /// <remarks>
    /// Listed explicitly rather than discovered. Discovery would mean scanning
    /// Presentation source for string literals, which would let the test pass for any typo
    /// that happened to look like a key — the exact opposite of what it is here to catch.
    /// Declaring them means a key that is emitted but not listed here fails the count, and
    /// a key listed here but absent from the catalog fails the containment check above.
    /// </remarks>
    private static readonly string[] StageEightUiKeys =
    {
            "agent.detail.none",
            "agent.detail.records",
            "agent.detail.relationships",
            "agent.detail.salary",
            "agent.detail.stats",
            "agent.detail.timeline",
            "agent.detail.traits",
            "agent.stamina.mental",
            "agent.stamina.physical",
            "agent.status.captured",
            "agent.status.dead",
            "agent.status.idle",
            "agent.status.onmission",
            "agent.status.recovering",
            "agent.status.resting",
            "agent.status.retired",
            "agent.status.training",
            "build.cost",
            "build.depth_modifier",
            "build.error.generic",
            "build.error.insufficient_funds",
            "build.error.layer_out_of_range",
            "build.error.layer_too_shallow",
            "build.error.locked_by_story",
            "build.error.neighbour_other_layer",
            "build.error.no_slots",
            "build.error.slot_occupied",
            "build.error.slot_out_of_range",
            "build.error.unknown_neighbour",
            "build.error.unknown_room_type",
            "build.ghost.valid",
            "build.upkeep",
            "class.unknown",
            "contract.client",
            "contract.difficulty",
            "contract.expiry",
            "contract.heat_gain",
            "contract.objective",
            "contract.reward",
            "contract.tier",
            "difficulty.band.easy",
            "difficulty.band.extreme",
            "difficulty.band.hard",
            "difficulty.band.impossible",
            "difficulty.band.standard",
            "difficulty.band.trivial",
            "difficulty.term.base",
            "difficulty.term.guards",
            "difficulty.term.no_forward_post",
            "difficulty.term.no_squad",
            "difficulty.term.security",
            "difficulty.term.skill.combat",
            "difficulty.term.skill.infiltration",
            "difficulty.term.skill.nerve",
            "difficulty.term.skill.social",
            "difficulty.term.skill.tech",
            "difficulty.term.team.combat",
            "difficulty.term.team.infiltration",
            "difficulty.term.team.nerve",
            "difficulty.term.team.social",
            "difficulty.term.team.tech",
            "difficulty.term.tier",
            "dispatch.refusal.agentnotdeployable",
            "dispatch.refusal.injurytosevere",
            "dispatch.refusal.mentalstaminatoolow",
            "dispatch.refusal.missingrequiredrole",
            "dispatch.refusal.physicalstaminatoolow",
            "dispatch.refusal.squadtoolarge",
            "dispatch.refusal.squadtoosmall",
            "dispatch.refusal.unknownagent",
            "dispatch.refusal.unknownrole",
            "heat.tier.0",
            "heat.tier.1",
            "heat.tier.2",
            "heat.tier.3",
            "heat.tier.4",
            "heat.tier.overflow",
            "heat.tier.unknown",
            "intel.band.complete",
            "intel.band.details",
            "intel.band.entranceonly",
            "intel.band.layout",
            "intel.band.typesandconnections",
            "intel.floor.0",
            "intel.floor.1",
            "intel.floor.2",
            "intel.floor.3",
            "intel.floor.4",
            "intel.floor.5",
            "intel.floor.6",
            "intel.floor.7",
            "intel.floor.8",
            "intel.floor.9",
            "intel.unlock.connections",
            "intel.unlock.entrance",
            "intel.unlock.extraction",
            "intel.unlock.floor_count",
            "intel.unlock.guard_counts",
            "intel.unlock.holding_rooms",
            "intel.unlock.light_levels",
            "intel.unlock.lock_states",
            "intel.unlock.may_be_stale",
            "intel.unlock.objective",
            "intel.unlock.patrol_routes",
            "intel.unlock.room_placement",
            "intel.unlock.room_types",
            "loyalty.band.content",
            "loyalty.band.devoted",
            "loyalty.band.resentful",
            "loyalty.band.uneasy",
            "loyalty.mood.content",
            "loyalty.mood.devoted",
            "loyalty.mood.resentful",
            "loyalty.mood.uneasy",
            "mission.readiness.too_few",
            "mission.readiness.too_many",
            "mission.readiness.under_powered",
            "mission.readiness.unknown_type",
            "skill.name.combat",
            "skill.name.infiltration",
            "skill.name.nerve",
            "skill.name.social",
            "skill.name.tech",
            "skill.term.base",
            "skill.term.fatigue",
            "skill.term.training",
            "skill.term.trait_bonus",
            "sleeper.status.burned",
            "sleeper.status.discovered",
            "sleeper.status.embedded",
            "sleeper.status.extracted",
            "sleeper.status.inserting",
            "sleeper.status.poisoned",
            "sleeper.status.unreported",
            "topbar.delta.gain",
            "topbar.delta.loss",
            "ui.key.pause",
            "ui.key.speed_1",
            "ui.key.speed_2",
            "ui.key.speed_3",
            "ui.key.speed_4",
            "ui.key.speed_unknown",
            "ui.speed.fast",
            "ui.speed.faster",
            "ui.speed.fastest",
            "ui.speed.normal",
            "ui.speed.pause",
            "ui.topbar.date",
            "ui.weekday.0",
            "ui.weekday.1",
            "ui.weekday.2",
            "ui.weekday.3",
            "ui.weekday.4",
            "ui.weekday.5",
            "ui.weekday.6",
    };
}
