using ProjectSpy.Tables;

// Core already defines SkillKind (the five skills an agent has). The generated table
// enum adds a None member meaning "this room trains no skill". They are genuinely
// different types and stage 3 converts between them explicitly, so the table side
// is aliased here to keep that conversion visible rather than implicit.
using TableSkillKind = ProjectSpy.Tables.SkillKind;

// Core owns the GOAP world-state key vocabulary (`GoapKeys`); the validator delegates
// its "is this key declared?" check to it rather than keeping a second list that can
// drift from the builder that assembles the world state.
using ProjectSpy.Core.Tactical;

// Core owns an `InteractableType` enum of resolved kinds and Luban generates a bean of
// the same name holding the per-kind row. Aliased rather than qualified throughout,
// so every use site in this file states which of the two it means.
using InteractableTypeRow = ProjectSpy.Tables.InteractableType;

// The generated manager class is named `Tables`, which collides with the
// ProjectSpy.Tables namespace. Because this file lives under ProjectSpy.*, the bare
// name binds to the namespace, so an alias is required rather than qualification.
using GameTables = ProjectSpy.Tables.Tables;

namespace ProjectSpy.Core.Tests.TableData;

/// <summary>
/// Validates the generated tables against the CSV sources.
/// </summary>
/// <remarks>
/// <para>
/// This is the guard that makes it safe for a designer to edit a CSV. It loads the
/// Luban-generated binary (what the game actually runs) and cross-checks it against
/// the localization sources, asserting:
/// </para>
/// <list type="bullet">
///   <item>no dangling foreign key,</item>
///   <item>no weight of zero or less,</item>
///   <item>every name_key and desc_key exists in <em>both</em> languages,</item>
///   <item>every skill a room trains is a real skill,</item>
///   <item>every mission tier has at least three usable node rooms.</item>
/// </list>
/// <para>
/// Foreign keys are read from the generated binary rather than re-parsed from CSV so
/// that the check reflects shipped content, not the editor's intent.
/// </para>
/// </remarks>
internal sealed class TableValidator
{
    /// <summary>
    /// The reputation tiers <c>recruit_rule</c> is keyed by. A field so the checks
    /// read as "for each tier" instead of repeating the bounds.
    /// </summary>
    private static readonly int[] ReputationTiers = { 0, 1, 2 };

    private readonly GameTables _tables;
    private readonly LocalizationCatalog _th;
    private readonly LocalizationCatalog _en;

    private TableValidator(GameTables tables, LocalizationCatalog th, LocalizationCatalog en)
    {
        _tables = tables;
        _th = th;
        _en = en;
    }

    /// <summary>The loaded tables.</summary>
    public GameTables Tables => _tables;

    /// <summary>Thai localization.</summary>
    public LocalizationCatalog Thai => _th;

    /// <summary>English localization.</summary>
    public LocalizationCatalog English => _en;

    /// <summary>
    /// Loads the tables and both localization catalogs.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// The table binaries have not been generated.
    /// </exception>
    public static TableValidator Load()
    {
        // Reset first so a developer re-running gen.ps1 mid-session sees new data.
        TableService.ResetCache();
        GameTables tables = TableService.Load();
        string root = LocalizationCatalog.FindRepositoryRoot();

        return new TableValidator(
            tables,
            LocalizationCatalog.Load("th", root),
            LocalizationCatalog.Load("en", root));
    }

    /// <summary>
    /// Collects every validation failure rather than throwing on the first.
    /// </summary>
    /// <remarks>
    /// Reporting all problems at once matters: a designer who has introduced six
    /// broken references should see all six, not fix them one build at a time.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        ValidateNoDanglingForeignKeys(errors);
        ValidateWeightsArePositive(errors);
        ValidateLocalizationCoverage(errors);
        ValidateSkillReferences(errors);
        ValidateNodeRoomCoveragePerTier(errors);
        ValidateIdSpacesAreNonOverlapping(errors);
        ValidateNumericRanges(errors);
        ValidateStageThreeTables(errors);
        ValidateStageFourTables(errors);
        ValidateTacticalTables(errors);

        return errors;
    }

    // ---- tactical layer ------------------------------------------------------

    /// <summary>
    /// The world-state keys a GOAP action may name in its preconditions or effects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Delegated to <see cref="GoapKeys"/> rather than listed here. The vocabulary is
    /// declared in exactly one place on purpose — the builder that assembles an NPC's
    /// world state reads the same declarations, and <c>GoapCatalog.Load</c> already
    /// throws on a table naming anything undeclared. A second list in the validator
    /// would be a third thing to update, and the failure mode of forgetting is the
    /// nasty one: the validator and the catalog disagreeing about what exists, with
    /// whichever side was edited last deciding whether a typo is an error.
    /// </para>
    /// <para>
    /// What this check still buys that the catalog's load-time throw does not: it
    /// reports the offending row and key by name, in the table-validation report a
    /// designer reads, instead of throwing from inside the simulation.
    /// </para>
    /// </remarks>
    private static bool IsKnownGoapKey(string name) => GoapKeys.IsDeclared(name);

    /// <summary>
    /// The archetype tags a GOAP action may require.
    /// </summary>
    /// <remarks>
    /// Kept in step with <c>GuardRole</c>. An action requiring a tag no archetype
    /// carries is an action that can never run, which is how a planner ends up with
    /// a guard who stands still because the only thing it knew how to do was for
    /// somebody else.
    /// </remarks>
    private static readonly HashSet<string> KnownArchetypeTags = new(StringComparer.Ordinal)
    {
        "any", "patrol", "sentry", "responder", "specialist", "combat", "civilian",
    };

    /// <summary>
    /// Tags that name a non-guard NPC class rather than a <c>guard_archetype</c> role.
    /// </summary>
    /// <remarks>
    /// Civilians are planned by the same GOAP as guards but come from
    /// <c>node_room</c> contents, not from the guard table. Without this the validator
    /// would correctly report that no archetype carries 'civilian' — and be right
    /// about the data while being wrong about the model.
    /// </remarks>
    private static readonly string[] NpcClassTags = { "civilian", "combat" };

    /// <summary>
    /// Checks every tactical table added for the continuous-building model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shared theme: each check below catches a combination that produces no
    /// error anywhere at runtime. A mission generator that cannot place an exit, a
    /// guard whose goal set does not exist, a GOAP action keyed on a world-state flag
    /// nobody sets — all of them run to completion and simply do less than the table
    /// says, which is the hardest kind of data bug to notice.
    /// </para>
    /// </remarks>
    private void ValidateTacticalTables(List<string> errors)
    {
        ValidateTacticalForeignKeys(errors);
        ValidateGoapContent(errors);
        ValidateTacticalActionContent(errors);
        ValidateRoomTemplateContent(errors);
        ValidateSiteGeneratability(errors);
        ValidateSiteGenRule(errors);
        ValidateIntelRule(errors);
        ValidateSleeperOps(errors);
        ValidateSquadTables(errors);
    }

    // ---- stage 4e: the squad -------------------------------------------------

    /// <summary>
    /// Every tunable the squad layer reads, listed rather than derived from Core.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="RequiredGoapRuleKeys"/>, and the same failure it
    /// catches. Every one of these falls back to a documented constant in Core, so a
    /// dropped row does not error — it makes the squads slightly lighter, slightly
    /// quicker to react, or able to carry more, and nobody notices until a balance
    /// review turns up a number nobody set.
    /// </remarks>
    private static readonly string[] RequiredSquadRuleKeys =
    {
        "squad_min_size",
        "squad_max_size",
        "squad_min_physical_stamina",
        "squad_min_mental_stamina",
        "squad_max_injury_severity",
        "squad_carry_weight_base",
        "squad_carry_weight_max",
        "squad_carry_weight_mule_bonus",
        "squad_gadget_slots_per_agent",
        "squad_order_follow_standoff_cm",
        "squad_order_stack_offset_cm",
        "squad_order_regroup_radius_cm",
        "squad_order_overwatch_report_cm",
        "squad_role_reaction_steps",
        "squad_abort_order_cooldown_steps",
        "squad_command_post_alarm_band_floor",
        "squad_command_post_compromise_steps",
        "squad_support_cooldown_percent",
    };

    /// <summary>The behaviours a role may name in <c>auto_behaviour_set</c>.</summary>
    private static readonly string[] RoleBehaviourTokens =
    {
        "none", "pointman", "overwatch", "mule", "hacker", "medic", "scout", "handler",
    };

    /// <summary>The orders a role may list in <c>allowed_orders</c>.</summary>
    /// <remarks>
    /// The CSV spells them the way the brief does — <c>HoldPosition</c> in one word —
    /// while Core's enum uses <c>Hold</c> so that rule 10's position-shaped member ban
    /// stays meaningful. <c>SquadRole.TryParseOrder</c> accepts both; this list is the
    /// CSV's half of that contract and is what catches a typo in the table.
    /// </remarks>
    private static readonly string[] RoleOrderTokens =
    {
        "MoveTo", "HoldPosition", "Follow", "Stack", "OverwatchDirection", "UseGadget",
        "HideBody", "CarryAlly", "Regroup", "Abort", "RequestExtraction",
    };

    /// <summary>
    /// Checks the four stage-4e tables against Core's vocabulary.
    /// </summary>
    /// <remarks>
    /// The failure this exists to catch is a row Core cannot act on. A role whose
    /// <c>auto_behaviour_set</c> names a behaviour nobody implemented produces an agent
    /// who stands still for a whole mission while the dispatch screen promised them a
    /// job, and a required-role token Core does not recognise makes an objective
    /// permanently unvalidatable — both of which are invisible until a player hits them.
    /// </remarks>
    private void ValidateSquadTables(List<string> errors)
    {
        ValidateSquadRuleKeys(errors);
        ValidateAgentRoleVocabulary(errors);

        // Derived from the table rather than listed, so a required-role token that names
        // no role is caught here instead of making the mission undispatchable at run
        // time. Listing the vocabulary instead would let the two drift apart in exactly
        // the direction that hides the mistake.
        var roleKeys = _tables.TbAgentRole.DataList
            .Select(r => (r.NameKey ?? string.Empty).Replace("role.", string.Empty))
            .ToHashSet(StringComparer.Ordinal);

        ValidateObjectiveRuleContent(errors, roleKeys);
        ValidateCommandPostAbilityContent(errors);
        ValidateResolveRuleContent(errors);
    }

    private void ValidateSquadRuleKeys(List<string> errors)
    {
        foreach (string key in RequiredSquadRuleKeys)
        {
            if (!_tables.TbSquadRule.DataList.Any(r => r.RuleKey == key))
                errors.Add(
                    $"squad_rule: missing required key '{key}'; the squad layer would silently use a fallback");
        }

        // Min must be at least three and max at least min. The brief fixes the floor at
        // three and Core throws below it, so a table saying two would produce a mission
        // the mission factory refuses to build — a data error that surfaces as an
        // exception in the middle of a dispatch rather than as a table error.
        int min = SquadRuleValue("squad_min_size");
        int max = SquadRuleValue("squad_max_size");

        if (min < 3)
        {
            errors.Add(
                $"squad_rule: 'squad_min_size' is {min}; TacticalMission refuses to deploy fewer than three agents");
        }

        if (max < min)
        {
            errors.Add(
                $"squad_rule: 'squad_max_size' is {max} but 'squad_min_size' is {min}; every squad would be rejected");
        }

        if (SquadRuleValue("squad_carry_weight_max") < SquadRuleValue("squad_carry_weight_base"))
        {
            errors.Add(
                "squad_rule: 'squad_carry_weight_max' is below 'squad_carry_weight_base'; "
                + "the cap would be applied before the base allowance");
        }

        if (SquadRuleValue("squad_support_cooldown_percent") <= 0)
        {
            errors.Add(
                "squad_rule: 'squad_support_cooldown_percent' must be positive; "
                + "every support ability would be permanently on cooldown");
        }
    }

    private void ValidateAgentRoleVocabulary(List<string> errors)
    {
        var behaviours = new HashSet<string>(RoleBehaviourTokens, StringComparer.Ordinal);
        var orders = new HashSet<string>(RoleOrderTokens, StringComparer.Ordinal);

        foreach (AgentRole role in _tables.TbAgentRole.DataList)
        {
            string behaviour = (role.AutoBehaviourSet ?? string.Empty).Trim();

            if (!behaviours.Contains(behaviour))
            {
                errors.Add(
                    $"agent_role {role.Id}: auto_behaviour_set '{role.AutoBehaviourSet}' is not one of "
                    + string.Join(", ", RoleBehaviourTokens) + "; the role would never act");
            }

            if ((role.AllowedOrders ?? string.Empty).Trim().Length == 0)
            {
                errors.Add(
                    $"agent_role {role.Id}: allowed_orders is empty; a role that cannot be given any order "
                    + "is a spectator, and the brief's control model has no place for one");
            }

            foreach (string token in SplitTokens(role.AllowedOrders))
            {
                if (!orders.Contains(token))
                {
                    errors.Add(
                        $"agent_role {role.Id}: allowed_orders token '{token}' is not one of "
                        + string.Join(", ", RoleOrderTokens) + "; the order would be silently unreachable");
                }
            }
        }

        // The post-only Handler is what makes the command post meaningful, and the
        // abilities are what the post grants. A table with no post-only role means the
        // forward command post can never be staffed, so every ability row is dead data
        // and the five support abilities the brief asks for do not exist.
        bool hasPostOnly = _tables.TbAgentRole.DataList.Any(r => r.RequiresCommandPost);

        if (!hasPostOnly)
        {
            errors.Add(
                "agent_role: no row has requires_command_post; the forward command post can never be staffed "
                + "and all five support abilities are unreachable");
        }
    }

    private void ValidateObjectiveRuleContent(List<string> errors, IReadOnlySet<string> roleKeys)
    {
        var required = Enum.GetValues<ObjectiveType>().ToHashSet();

        var present = new HashSet<ObjectiveType>();

        foreach (ObjectiveRule rule in _tables.TbObjectiveRule.DataList)
        {
            present.Add(rule.ObjectiveType);

            if (rule.WorkSteps <= 0)
            {
                errors.Add(
                    $"objective_rule {rule.Id} ({rule.ObjectiveType}): work_steps must be positive; "
                    + "the objective would complete on its first step");
            }

            if (rule.AlarmToleranceBand > (int)AlarmBand.Burned)
            {
                errors.Add(
                    $"objective_rule {rule.Id} ({rule.ObjectiveType}): alarm_tolerance_band "
                    + $"{rule.AlarmToleranceBand} is above the top alarm band; the objective could never fail "
                    + "on the alarm");
            }

            // StealData is the only type with a carrying phase. Exfil steps for anything
            // else would be a column reading a non-zero number that Core ignores, which
            // is exactly the "most of the table being zeros that mean nothing" encoding
            // the table's one-row-per-type shape was chosen to avoid.
            bool hasExfil = rule.ExfilSteps > 0;

            if (hasExfil && rule.ObjectiveType != ObjectiveType.StealData)
            {
                errors.Add(
                    $"objective_rule {rule.Id} ({rule.ObjectiveType}): exfil_steps is {rule.ExfilSteps} but only "
                    + "StealData has a carrying phase; the value would be ignored");
            }

            foreach (string role in SplitTokens(rule.RequiredRoles))
            {
                if (!roleKeys.Contains(role))
                {
                    errors.Add(
                        $"objective_rule {rule.Id} ({rule.ObjectiveType}): required_roles token '{role}' names "
                        + "no agent_role row; the requirement could never be satisfied and the mission "
                        + "would be undispatchable");
                }
            }

            bool needsRooms = rule.ObjectiveType == ObjectiveType.Recon;

            if (needsRooms != (rule.ObserveRoomsRequired > 0))
            {
                errors.Add(
                    $"objective_rule {rule.Id} ({rule.ObjectiveType}): observe_rooms_required is "
                    + $"{rule.ObserveRoomsRequired}; only Recon counts rooms and any other type "
                    + "setting it would be ignored");
            }

            bool needsBlast = rule.ObjectiveType == ObjectiveType.Sabotage;

            if (needsBlast != (rule.BlastIntervalSteps > 0))
            {
                errors.Add(
                    $"objective_rule {rule.Id} ({rule.ObjectiveType}): blast_interval_steps is "
                    + $"{rule.BlastIntervalSteps}; only Sabotage arms a timer and any other type "
                    + "setting it would be ignored");
            }
        }

        foreach (ObjectiveType missing in required.Except(present))
        {
            errors.Add(
                $"objective_rule: no row for objective type {missing}; Core would refuse to build that mission "
                + "rather than fall back to a default");
        }

        ValidateGadgetWeights(errors);
    }

    /// <summary>
    /// Every gadget must weigh something, and something an agent can actually carry.
    /// </summary>
    /// <remarks>
    /// <c>squad_rule.squad_carry_weight_base</c> is the cap, so a gadget heavier than the
    /// cap could never be issued: the dispatch screen would offer it, the player would
    /// pack it, and the mission would be refused for carrying too much — with no way to
    /// tell which of the three things went wrong. That is a data error, and this is where
    /// it belongs.
    /// </remarks>
    private void ValidateGadgetWeights(List<string> errors)
    {
        int cap = SquadRuleValue("squad_carry_weight_base");

        foreach (Gadget gadget in _tables.TbGadget.DataList)
        {
            if (gadget.Weight <= 0)
            {
                errors.Add(
                    $"gadget {gadget.Id} ({gadget.NameKey}): weight is {gadget.Weight}; "
                    + "carry weight is a dispatch gate and a free gadget makes that gate meaningless");
            }
            else if (gadget.Weight > cap)
            {
                errors.Add(
                    $"gadget {gadget.Id} ({gadget.NameKey}): weight is {gadget.Weight}, above the squad carry "
                    + $"cap of {cap}; it could never be issued");
            }

            if (gadget.Uses <= 0)
            {
                errors.Add(
                    $"gadget {gadget.Id} ({gadget.NameKey}): uses is {gadget.Uses}; "
                    + "a gadget with no uses could be assigned but never spent");
            }
        }
    }

    private void ValidateCommandPostAbilityContent(List<string> errors)
    {
        var required = Enum.GetValues<SupportAbility>().ToHashSet();
        var present = new HashSet<SupportAbility>();

        foreach (CommandPostAbility ability in _tables.TbCommandPostAbility.DataList)
        {
            present.Add(ability.Ability);

            if (ability.CooldownSteps <= 0)
            {
                errors.Add(
                    $"command_post_ability {ability.Id} ({ability.Ability}): cooldown_steps must be positive; "
                    + "the ability would be usable every step, which is what the cooldown exists to prevent");
            }

            // The remote unlock is aimed at a specific door and the smoke and feed at a
            // specific room. An ability that claims to need a target and names nothing
            // would be a row Core refuses at the point of use, so the designer finds out
            // in the middle of a mission rather than in the build.
            if (ability.RequiresHackedDoor && ability.RequiresRoomTarget)
            {
                errors.Add(
                    $"command_post_ability {ability.Id} ({ability.Ability}): requires both a hacked door and a "
                    + "room target; Core's Use gate accepts one or the other, not both");
            }
        }

        foreach (SupportAbility missing in required.Except(present))
        {
            errors.Add(
                $"command_post_ability: no row for ability {missing}; the forward command post would grant "
                + "nothing for it");
        }
    }

    private void ValidateResolveRuleContent(List<string> errors)
    {
        var required = Enum.GetValues<ResolveClass>().ToHashSet();
        var present = new HashSet<ResolveClass>();

        foreach (ResolveRule rule in _tables.TbResolveRule.DataList)
        {
            present.Add(rule.ResolveClass);

            // A CleanSuccess that costs heat would make the safest possible run the
            // loudest one, and a Disaster that paid funds would make losing the team
            // financially correct. Both are balance claims rather than crashes, so the
            // only check is that they were stated on purpose: percentages outside 0-100
            // are always a typo.
            foreach ((string column, int value) in new[]
                     {
                         ("funds_percent", rule.FundsPercent),
                         ("intel_percent", rule.IntelPercent),
                         ("heat_percent", rule.HeatPercent),
                         ("evidence_percent", rule.EvidencePercent),
                         ("injury_percent", rule.InjuryPercent),
                         ("mental_percent", rule.MentalPercent),
                         ("loot_percent", rule.LootPercent),
                     })
            {
                if (value is < 0 or > 400)
                {
                    errors.Add(
                        $"resolve_rule {rule.Id} ({rule.ResolveClass}): {column} is {value}; "
                        + "expected 0-100, or a deliberate multiple up to 400");
                }
            }

            if (rule.ResolveClass == ResolveClass.Disaster && rule.FundsPercent > 0)
            {
                errors.Add(
                    $"resolve_rule {rule.Id} (Disaster): funds_percent is {rule.FundsPercent}; "
                    + "a mission that loses the team must not pay for it");
            }
        }

        foreach (ResolveClass missing in required.Except(present))
        {
            errors.Add(
                $"resolve_rule: no row for resolve class {missing}; ResolveSystem would throw when a mission "
                + "ended that way");
        }
    }

    /// <summary>A <c>squad_rule</c> row's value, or zero when the row is absent.</summary>
    private int SquadRuleValue(string key)
        => _tables.TbSquadRule.DataList.FirstOrDefault(r => r.RuleKey == key)?.Value ?? 0;

    /// <summary>
    /// Splits a comma-separated table cell, trimming and dropping blanks.
    /// </summary>
    /// <remarks>
    /// Local rather than going through <c>SimulationRules.Tags</c> so that the validator
    /// does not silently inherit whatever the runtime parser decides a tag is. The
    /// validator's job is to check the raw cell.
    /// </remarks>
    private static IEnumerable<string> SplitTokens(string? cell)
        => string.IsNullOrWhiteSpace(cell)
            ? Array.Empty<string>()
            : cell.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);

    /// <summary>
    /// Every tunable the site generator reads, listed rather than derived from Core.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="RequiredFogKeys"/>: a key that goes missing falls
    /// back to a documented constant in Core, so the simulation keeps running with a
    /// number nobody approved. The list lives here precisely so it <em>can</em> fail —
    /// reading it out of the generator would make the check unable to detect the
    /// generator losing interest in a key.
    /// </remarks>
    private static readonly string[] RequiredSiteGenKeys =
    {
        "site_max_weight_tier",
        "site_min_rooms_per_floor",
        "site_max_rooms_per_floor",
        "site_locked_door_percent",
        "site_locked_door_percent_per_grade",
        "site_vertical_stair_weight",
        "site_vertical_ladder_weight",
        "site_vertical_vent_weight",
        "site_vertical_extra_chance_percent",
        "site_second_route_attempts_max",
        "site_extraction_extra_chance_percent",
        "site_light_span_per_emitter_cm",
        "site_occluder_span_divisor",
        "site_loot_container_chance_percent",
        "site_interactable_span_divisor",
        "site_patrol_room_min",
        "site_patrol_room_max",
        "site_guard_room_bonus_percent",
        "site_forward_post_floor_count",
        "site_forward_post_rooms_min",
        "site_forward_post_rooms_max",
        "site_forward_post_travel_steps",
        "site_forward_post_guard_max",
        "site_entrance_edge_percent",
    };

    /// <summary>Checks <c>site_gen_rule</c>, the stage-4a generator's tunables.</summary>
    private void ValidateSiteGenRule(List<string> errors)
    {
        foreach (string key in RequiredSiteGenKeys)
        {
            if (!_tables.TbSiteGenRule.DataList.Any(r => r.RuleKey == key))
            {
                errors.Add(
                    $"site_gen_rule: missing required key '{key}'; site generation would silently use a fallback");
            }
        }

        foreach (SiteGenRule rule in _tables.TbSiteGenRule.DataList)
        {
            if (rule.Value < 0)
                errors.Add($"site_gen_rule {rule.Id}: '{rule.RuleKey}' must be >= 0, was {rule.Value}");
        }

        // Weight tiers are columns, not rows, so the highest tier a site may reference
        // is a property of the schema. If the cap ever rises past the columns that
        // exist, every tier-4+ site silently reads weight 0 and generates empty floors.
        int maxWeightTier = SiteGenValue("site_max_weight_tier");
        if (maxWeightTier is < 1 or > 3)
        {
            errors.Add(
                $"site_gen_rule: site_max_weight_tier must be 1..3 (the weight_tier_1..3 columns that exist), "
                + $"was {maxWeightTier}");
        }

        // Every tier must have at least one room template that can actually be built,
        // or the weighted pick on some floor has nothing to return.
        if (_tables.TbRoomTemplate.DataList.Max(r => r.WeightTier1) <= 0)
            errors.Add("room_template: no template has a positive weight_tier_1, so tier 1 cannot build a floor");

        if (_tables.TbRoomTemplate.DataList.Max(r => r.WeightTier2) <= 0)
            errors.Add("room_template: no template has a positive weight_tier_2, so tier 2 cannot build a floor");

        if (_tables.TbRoomTemplate.DataList.Max(r => r.WeightTier3) <= 0)
            errors.Add("room_template: no template has a positive weight_tier_3, so tiers 3 and 4 cannot build a floor");

        // A floor with fewer rooms than the minimum is one the generator cannot honour,
        // and it would have to either loop forever or silently drop the invariant.
        int minRooms = SiteGenValue("site_min_rooms_per_floor");
        int maxRooms = SiteGenValue("site_max_rooms_per_floor");
        if (minRooms < 1)
            errors.Add($"site_gen_rule: site_min_rooms_per_floor must be >= 1, was {minRooms}");
        if (maxRooms < minRooms)
            errors.Add($"site_gen_rule: site_max_rooms_per_floor ({maxRooms}) is below site_min_rooms_per_floor ({minRooms})");

        // The whole "every floor has a vertical connection" guarantee rests on there
        // being at least one vertical connection kind to draw from.
        foreach (string key in new[] { "site_vertical_stair_weight", "site_vertical_ladder_weight", "site_vertical_vent_weight" })
        {
            if (SiteGenValue(key) < 0)
                errors.Add($"site_gen_rule: {key} must be >= 0");
        }

        if (SiteGenValue("site_vertical_stair_weight")
            + SiteGenValue("site_vertical_ladder_weight")
            + SiteGenValue("site_vertical_vent_weight") <= 0)
        {
            errors.Add("site_gen_rule: every vertical connection kind has weight 0, so no floor can ever be reached");
        }

        foreach (string key in new[]
                 {
                     "site_locked_door_percent",
                     "site_vertical_extra_chance_percent",
                     "site_extraction_extra_chance_percent",
                     "site_loot_container_chance_percent",
                     "site_entrance_edge_percent",
                 })
        {
            int value = SiteGenValue(key);
            if (value is < 0 or > 100)
                errors.Add($"site_gen_rule: {key} must be 0..100, was {value}");
        }

        // A floor width that fits no room is a floor the generator cannot populate,
        // and the site-level check below only looks at width_min, not at the room
        // count the partition actually has to hit.
        int spanPerEmitter = SiteGenValue("site_light_span_per_emitter_cm");
        if (spanPerEmitter < 1)
            errors.Add($"site_gen_rule: site_light_span_per_emitter_cm must be >= 1, was {spanPerEmitter}");

        foreach (string key in new[] { "site_occluder_span_divisor", "site_interactable_span_divisor" })
        {
            if (SiteGenValue(key) < 1)
                errors.Add($"site_gen_rule: {key} must be >= 1, was {SiteGenValue(key)}");
        }

        if (SiteGenValue("site_patrol_room_max") < SiteGenValue("site_patrol_room_min"))
            errors.Add("site_gen_rule: site_patrol_room_max is below site_patrol_room_min, so no patrol route is legal");

        // A forward post with no travel cost is a free extra region, which would make
        // the step economy meaningless for every site that has one.
        if (SiteGenValue("site_forward_post_travel_steps") < 1)
            errors.Add("site_gen_rule: site_forward_post_travel_steps must be >= 1");

        if (SiteGenValue("site_forward_post_floor_count") < 1)
            errors.Add("site_gen_rule: site_forward_post_floor_count must be >= 1");
    }

    /// <summary>Reads a <c>site_gen_rule</c> value, or zero when the row is missing.</summary>
    /// <summary>
    /// Every tunable the intel system reads, listed rather than derived from Core.
    /// </summary>
    /// <remarks>
    /// The band thresholds are the sharpest example of why this list exists. They are
    /// written out in <c>knowledge.md</c> rule 17 as "0-24 / 25-49 / 50-74 / 75-99 / 100",
    /// so it is tempting to hard-code them in Core as structure rather than as tuning.
    /// They are tuning: a designer retuning the reveal curve must be able to do it in a
    /// CSV, and rule 3 forbids Core holding a balance number nobody approved. If a
    /// threshold goes missing the fallback would quietly become the band's edge.
    /// </remarks>
    private static readonly string[] RequiredIntelKeys =
    {
        "intel_band_entrance_max",
        "intel_band_layout_max",
        "intel_band_types_max",
        "intel_band_details_max",
        "intel_band_full",
        "intel_embedded_at_percent",
        "intel_stale_after_ticks",
        "intel_stale_fact_percent",
        "intel_infiltration_points_per_reduction",
        "intel_social_points_per_reduction",
        "intel_security_grade_divisor",
        "intel_discovery_chance_min",
        "intel_discovery_chance_max",
        "intel_poison_patrol_percent",
        "intel_poison_door_percent",
        "intel_poison_max_locked_doors",
        "intel_poison_objective_percent",
        "intel_holding_room_percent",
        "intel_snapshot_min_percent",
    };

    /// <summary>Checks <c>intel_rule</c>, the stage-4b intel tunables.</summary>
    private void ValidateIntelRule(List<string> errors)
    {
        foreach (string key in RequiredIntelKeys)
        {
            if (!_tables.TbIntelRule.DataList.Any(r => r.RuleKey == key))
            {
                errors.Add(
                    $"intel_rule: missing required key '{key}'; intel would silently use a fallback");
            }
        }

        foreach (IntelRule rule in _tables.TbIntelRule.DataList)
        {
            if (rule.Value < 0)
                errors.Add($"intel_rule {rule.Id}: '{rule.RuleKey}' must be >= 0, was {rule.Value}");
        }

        // The five band edges must be strictly increasing and end at 100. Out of order
        // or overlapping, the band lookup would return the wrong band for a stretch of
        // percentages and the no-leak test would fail for a reason nobody could see.
        int[] edges =
        {
            IntelValue("intel_band_entrance_max"),
            IntelValue("intel_band_layout_max"),
            IntelValue("intel_band_types_max"),
            IntelValue("intel_band_details_max"),
            IntelValue("intel_band_full"),
        };

        for (int i = 1; i < edges.Length; i++)
        {
            if (edges[i] <= edges[i - 1])
            {
                errors.Add(
                    $"intel_rule: band edge {i} ({edges[i]}) must be greater than edge {i - 1} ({edges[i - 1]})");
            }
        }

        if (edges[^1] != 100)
            errors.Add($"intel_rule: intel_band_full must be 100 (the top of the intel scale), was {edges[^1]}");

        if (IntelValue("intel_band_entrance_max") >= 100)
        {
            errors.Add("intel_rule: intel_band_entrance_max must be < 100, or the top band is never reachable");
        }

        // A divisor of zero would divide by zero in the discovery roll. The reduced
        // formula is integer division, so this is a crash, not a wrong number.
        int securityDivisor = IntelValue("intel_security_grade_divisor");
        if (securityDivisor < 1)
            errors.Add($"intel_rule: intel_security_grade_divisor must be >= 1, was {securityDivisor}");

        foreach (string key in new[] { "intel_infiltration_points_per_reduction", "intel_social_points_per_reduction" })
        {
            if (IntelValue(key) < 1)
                errors.Add($"intel_rule: {key} must be >= 1, was {IntelValue(key)}");
        }

        int chanceMin = IntelValue("intel_discovery_chance_min");
        int chanceMax = IntelValue("intel_discovery_chance_max");
        if (chanceMax > 100)
            errors.Add($"intel_rule: intel_discovery_chance_max must be <= 100, was {chanceMax}");
        if (chanceMin > chanceMax)
            errors.Add($"intel_rule: intel_discovery_chance_min ({chanceMin}) is above the max ({chanceMax})");

        foreach (string key in new[]
                 {
                     "intel_stale_fact_percent",
                     "intel_poison_patrol_percent",
                     "intel_poison_door_percent",
                     "intel_poison_objective_percent",
                     "intel_holding_room_percent",
                     "intel_snapshot_min_percent",
                 })
        {
            int value = IntelValue(key);
            if (value is < 0 or > 100)
                errors.Add($"intel_rule: {key} must be 0..100, was {value}");
        }

        // Poisoning is bounded by construction in Core, but the bound is data: a value
        // above 100 would make "at most this many false locks" meaningless.
        if (IntelValue("intel_poison_max_locked_doors") < 1)
        {
            errors.Add(
                "intel_rule: intel_poison_max_locked_doors must be >= 1; "
                + "poisoning that may lie about no doors at all is not poisoning");
        }

        // The rescue reveal threshold has to be reachable. If it sits below the top band
        // the player would learn where a prisoner is held before learning everything
        // else about the site, which inverts the cost the rescue is supposed to carry.
        int holdingRoom = IntelValue("intel_holding_room_percent");
        if (holdingRoom < IntelValue("intel_band_details_max") + 1)
        {
            errors.Add(
                $"intel_rule: intel_holding_room_percent ({holdingRoom}) must sit above the details band "
                + $"({IntelValue("intel_band_details_max")}) so a holding room is never revealed before full intel");
        }
    }

    /// <summary>Checks <c>sleeper_op</c>, the per-tier accrual and discovery rates.</summary>
    private void ValidateSleeperOps(List<string> errors)
    {
        var tiers = new HashSet<int>();

        foreach (SleeperOp op in _tables.TbSleeperOp.DataList)
        {
            if (!tiers.Add(op.SiteTier))
                errors.Add($"sleeper_op: site tier {op.SiteTier} has more than one row");

            if (op.TicksPerIntelPercent < 1)
            {
                errors.Add(
                    $"sleeper_op {op.Id}: ticks_per_intel_percent must be >= 1, "
                    + $"was {op.TicksPerIntelPercent} (the operation would never accrue)");
            }

            if (op.BaseDiscoveryChancePerTick is < 0 or > 100)
                errors.Add($"sleeper_op {op.Id}: base_discovery_chance_per_tick must be 0..100");

            if (op.PoisonChanceOnDiscovery is < 0 or > 100)
                errors.Add($"sleeper_op {op.Id}: poison_chance_on_discovery must be 0..100");

            // Zero would mean an agent is found and nothing at all happens, which is a
            // dead row rather than a gentle site.
            if (op.HeatOnDiscovery < 1)
                errors.Add($"sleeper_op {op.Id}: heat_on_discovery must be >= 1");

            if (op.InfiltrationModifier < 0)
                errors.Add($"sleeper_op {op.Id}: infiltration_modifier must be >= 0");

            if (op.SocialModifier < 0)
                errors.Add($"sleeper_op {op.Id}: social_modifier must be >= 0");
        }

        // Every site tier must have a row, or a sleeper sent to that tier falls back to
        // a documented constant and the player is shown a risk nobody designed.
        var siteTiers = _tables.TbSiteTemplate.DataList.Select(s => s.Tier).Distinct().OrderBy(t => t);

        foreach (int tier in siteTiers)
        {
            if (!tiers.Contains(tier))
                errors.Add($"sleeper_op: no row for site tier {tier}, which site_template uses");
        }

        foreach (CaptureSite capture in _tables.TbCaptureSite.DataList)
        {
            if (!siteTiers.Contains(capture.SiteTier))
            {
                errors.Add(
                    $"capture_site {capture.Id}: site tier {capture.SiteTier} is not a tier any site uses");
            }

            if (capture.RescueTimeLimitDays < 1)
                errors.Add($"capture_site {capture.Id}: rescue_time_limit_days must be >= 1");

            if (LocalizationCatalog.ParseStringList(capture.HoldingRoomTags).Count == 0)
            {
                errors.Add(
                    $"capture_site {capture.Id}: holding_room_tags is empty, so no room could ever be "
                    + "chosen to hold a prisoner and the 100% intel reveal would have nothing to name");
            }
        }
    }

    private int IntelValue(string key)
    {
        foreach (IntelRule rule in _tables.TbIntelRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return 0;
    }

    private int SiteGenValue(string key)
    {
        foreach (SiteGenRule rule in _tables.TbSiteGenRule.DataList)
        {
            if (string.Equals(rule.RuleKey, key, StringComparison.Ordinal))
                return rule.Value;
        }

        return 0;
    }

    /// <summary>Cross-table references for the tactical layer.</summary>
    private void ValidateTacticalForeignKeys(List<string> errors)
    {
        var lightIds = _tables.TbLightSource.DataList.Select(x => x.Id).ToHashSet();
        var profileIds = _tables.TbLightingProfile.DataList.Select(x => x.Id).ToHashSet();
        var roomIds = _tables.TbRoomTemplate.DataList.Select(x => x.Id).ToHashSet();
        var noiseIds = _tables.TbNoiseProfile.DataList.Select(x => x.Id).ToHashSet();
        var goalIds = _tables.TbGoapGoal.DataList.Select(x => x.Id).ToHashSet();
        var lootIds = _tables.TbLootTable.DataList.Select(x => x.Id).ToHashSet();
        var gadgetIds = _tables.TbGadget.DataList.Select(x => x.Id).ToHashSet();

        foreach (SiteTemplate site in _tables.TbSiteTemplate.DataList)
        {
            if (!profileIds.Contains(site.LightingProfileId))
            {
                errors.Add(
                    $"site_template {site.Id}: lighting_profile_id {site.LightingProfileId} does not exist, "
                    + "so the site would generate with no lights at all");
            }

            foreach (int roomId in LocalizationCatalog.ParseIdList(site.AllowedRoomIds))
            {
                if (!roomIds.Contains(roomId))
                    errors.Add($"site_template {site.Id}: allowed_room_ids references unknown room_template {roomId}");
            }
        }

        foreach (LightingProfile profile in _tables.TbLightingProfile.DataList)
        {
            var emitters = LocalizationCatalog.ParseIdList(profile.EmitterIds);

            if (emitters.Count == 0)
            {
                errors.Add(
                    $"lighting_profile {profile.Id}: has no emitters, so every site using it is pitch dark "
                    + "and nothing is ever perceived");
            }

            foreach (int emitter in emitters)
            {
                if (!lightIds.Contains(emitter))
                    errors.Add($"lighting_profile {profile.Id}: references unknown light_source {emitter}");
            }

            if (profile.AmbientPercent is < 0 or > 100)
                errors.Add($"lighting_profile {profile.Id}: ambient_percent must be 0..100");

            if (profile.ShadowChancePercent is < 0 or > 100)
                errors.Add($"lighting_profile {profile.Id}: shadow_chance_percent must be 0..100");
        }

        foreach (LightSource light in _tables.TbLightSource.DataList)
        {
            if (light.RadiusCm < 1)
                errors.Add($"light_source {light.Id}: radius_cm must be >= 1, was {light.RadiusCm}");

            // A light that cannot be destroyed and cannot be switched off is a fixed
            // obstacle the player cannot solve by any means except walking past it.
            if (!light.CanBeDestroyed && !light.CanBeSwitched && light.IntensityLevel == LightLevel.Lit)
            {
                errors.Add(
                    $"light_source {light.Id}: a Lit emitter that can be neither destroyed nor switched off "
                    + "is unstealthable; darkening it is the whole point of the lighting system");
            }
        }

        foreach (ConnectionType connection in _tables.TbConnectionType.DataList)
        {
            if (connection.TraverseSteps < 1)
                errors.Add($"connection_type {connection.Id}: traverse_steps must be >= 1");

            if (connection.NoiseOnUse < 0)
                errors.Add($"connection_type {connection.Id}: noise_on_use must be >= 0");

            // A connection that requires a skill nobody has can never be used, which
            // would quietly disconnect part of the building.
            if (connection.SkillDc > 0 && connection.RequiresSkill == TableSkillKind.None)
            {
                errors.Add(
                    $"connection_type {connection.Id}: has a skill_dc of {connection.SkillDc} but requires "
                    + "no skill, so the difficulty check has nothing to resolve against");
            }
        }

        foreach (NoiseProfile noise in _tables.TbNoiseProfile.DataList)
        {
            if (noise.BaseRadiusCm < 0)
                errors.Add($"noise_profile {noise.Id}: base_radius_cm must be >= 0");

            // Attenuation above 100% would make a sound grow as it crosses more of
            // the building, which is the opposite of what the row describes.
            foreach ((string name, int value) in new[]
                     {
                         ("attenuation_per_connection_percent", noise.AttenuationPerConnectionPercent),
                         ("attenuation_per_floor_percent", noise.AttenuationPerFloorPercent),
                     })
            {
                if (value is < 0 or > 100)
                    errors.Add($"noise_profile {noise.Id}: {name} must be 0..100, was {value}");
            }

            if (noise.SuspicionWeight < 0)
                errors.Add($"noise_profile {noise.Id}: suspicion_weight must be >= 0");
        }

        foreach (GuardArchetype guard in _tables.TbGuardArchetype.DataList)
        {
            if (!goalIds.Contains(guard.GoalSetId))
            {
                errors.Add(
                    $"guard_archetype {guard.Id}: references unknown goap_goal {guard.GoalSetId}, so guards of "
                    + "this type would have nothing to plan");
            }

            if (guard.VisionRangeCm < 1)
                errors.Add($"guard_archetype {guard.Id}: vision_range_cm must be >= 1");

            // A cone of zero or more than 360 means the guard can never see anything
            // forward, or can see in every direction including behind itself.
            if (guard.VisionConeDegrees is < 1 or > 360)
                errors.Add($"guard_archetype {guard.Id}: vision_cone_degrees must be 1..360");

            if (guard.HearingRangeCm < 0)
                errors.Add($"guard_archetype {guard.Id}: hearing_range_cm must be >= 0");

            if (guard.PatrolSpeedCmPerStep < 0 || guard.AlertSpeedCmPerStep < 0)
                errors.Add($"guard_archetype {guard.Id}: movement speeds must be >= 0");

            if (guard.Health < 1)
                errors.Add($"guard_archetype {guard.Id}: health must be >= 1");

            if (guard.SuspicionGainRate <= 0)
                errors.Add($"guard_archetype {guard.Id}: suspicion_gain_rate must be > 0");

            if (guard.SuspicionDecayRate < 0)
                errors.Add($"guard_archetype {guard.Id}: suspicion_decay_rate must be >= 0");

            // A guard that gains suspicion faster than it decays can never settle
            // back down, so one glimpse permanently escalates the site.
            if (guard.SuspicionDecayRate > guard.SuspicionGainRate)
            {
                errors.Add(
                    $"guard_archetype {guard.Id}: suspicion decays ({guard.SuspicionDecayRate}) faster than it "
                    + $"is gained ({guard.SuspicionGainRate}), so a guard can never calm down");
            }

            if (guard.CarriesKeyId != 0 && guard.CarriesKeyId < 10000)
                errors.Add($"guard_archetype {guard.Id}: carries_key_id looks like a room_type id, not a connection id");
        }

        foreach (RoomTemplate room in _tables.TbRoomTemplate.DataList)
        {
            if (room.LootTableId != 0 && !lootIds.Contains(room.LootTableId))
                errors.Add($"room_template {room.Id}: references unknown loot_table {room.LootTableId}");
        }

        foreach (TacticalAction action in _tables.TbTacticalAction.DataList)
        {
            if (action.NoiseProfileId != 0 && !noiseIds.Contains(action.NoiseProfileId))
            {
                errors.Add(
                    $"tactical_action {action.Id}: references unknown noise_profile {action.NoiseProfileId}");
            }
        }

        foreach (Throwable throwable in _tables.TbThrowable.DataList)
        {
            if (!noiseIds.Contains(throwable.NoiseProfileId))
                errors.Add($"throwable {throwable.Id}: references unknown noise_profile {throwable.NoiseProfileId}");

            if (throwable.GadgetId != 0 && !gadgetIds.Contains(throwable.GadgetId))
                errors.Add($"throwable {throwable.Id}: references unknown gadget {throwable.GadgetId}");
        }

        foreach (MeleeWeapon weapon in _tables.TbMeleeWeapon.DataList)
        {
            if (!noiseIds.Contains(weapon.NoiseProfileId))
                errors.Add($"melee_weapon {weapon.Id}: references unknown noise_profile {weapon.NoiseProfileId}");

            if (weapon.GadgetId != 0 && !gadgetIds.Contains(weapon.GadgetId))
                errors.Add($"melee_weapon {weapon.Id}: references unknown gadget {weapon.GadgetId}");
        }
    }

    /// <summary>
    /// Every tunable the GOAP runtime reads, listed rather than derived from Core.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="RequiredFogKeys"/>: each of these falls back to a
    /// documented constant in Core when the row is absent, so the simulation keeps
    /// running with a number nobody approved. That is the failure this catches — a
    /// dropped row reads as "the AI got a bit slower", never as an error.
    /// </remarks>
    private static readonly string[] RequiredGoapRuleKeys =
    {
        "goap_max_plans_per_step",
        "goap_node_budget",
        "goap_plan_max_length",
        "goap_replan_cooldown_steps",
        "goap_radio_transmit_steps",
        "goap_radio_alarm_percent",
        "goap_body_hide_quality_steps",
        "goap_missing_colleague_steps",
        "goap_suspicion_threshold",
        "goap_priority_floor",
    };

    /// <summary>The value of a <c>goap_rule</c> row, or zero when the row is absent.</summary>
    /// <remarks>
    /// Zero rather than a sentinel, because a missing row is reported separately by
    /// <see cref="RequiredGoapRuleKeys"/> and a second, different error for the same
    /// mistake would only make the report harder to read.
    /// </remarks>
    private int GoapRuleValue(string key)
        => _tables.TbGoapRule.DataList.FirstOrDefault(r => r.RuleKey == key)?.Value ?? 0;

    /// <summary>GOAP actions and goals must name things that exist.</summary>
    private void ValidateGoapContent(List<string> errors)
    {
        foreach (string key in RequiredGoapRuleKeys)
        {
            if (!_tables.TbGoapRule.DataList.Any(r => r.RuleKey == key))
            {
                errors.Add(
                    $"goap_rule: missing required key '{key}'; the planner would silently use a fallback");
            }
        }

        // The per-step plan budget is the one GOAP tunable that can make the site look
        // broken rather than merely dull. At zero or one, one guard's twitchiness can
        // starve every other guard in the building — the fair queue guarantees fairness,
        // not liveness — and the symptom is a site where only one NPC ever reacts. The
        // simulation clamps it to at least one for that reason, which is exactly why it
        // has to be caught here instead.
        int planBudget = GoapRuleValue("goap_max_plans_per_step");

        if (planBudget < 2)
        {
            errors.Add(
                $"goap_rule: 'goap_max_plans_per_step' is {planBudget}; a single replan per step lets one "
                + "NPC's events consume the whole building's planning budget");
        }

        int nodeBudget = GoapRuleValue("goap_node_budget");

        if (nodeBudget < 1)
        {
            errors.Add(
                $"goap_rule: 'goap_node_budget' is {nodeBudget}; the planner cannot expand a single node and "
                + "no plan will ever be found");
        }

        int planLength = GoapRuleValue("goap_plan_max_length");

        if (planLength < 1)
        {
            errors.Add(
                $"goap_rule: 'goap_plan_max_length' is {planLength}; every plan would be empty");
        }

        // Radio escalation and body discovery are the two delays a player plans around,
        // so both have to be positive to exist at all. Zero for the transmit window
        // means no delay — the player gets no counter-play window — and zero for the
        // hiding delay means hiding a body does nothing, which is the opposite of what
        // the mechanic promises.
        foreach (string key in new[] { "goap_radio_transmit_steps", "goap_body_hide_quality_steps" })
        {
            int value = GoapRuleValue(key);

            if (value < 1)
                errors.Add($"goap_rule: '{key}' is {value}; it must be >= 1 for the mechanic to exist");
        }

        int radioPercent = GoapRuleValue("goap_radio_alarm_percent");

        if (radioPercent is < 1 or > 100)
        {
            errors.Add(
                $"goap_rule: 'goap_radio_alarm_percent' is {radioPercent}; it must be a percentage in 1..100");
        }

        int threshold = GoapRuleValue("goap_suspicion_threshold");

        if (threshold is < 1 or > 100)
        {
            errors.Add(
                $"goap_rule: 'goap_suspicion_threshold' is {threshold}; it must be a suspicion value in 1..100");
        }
        foreach (GoapAction action in _tables.TbGoapAction.DataList)
        {
            // An action with no precondition is always executable, so the planner
            // will always choose it — it becomes the only action in the game.
            var preconditions = LocalizationCatalog.ParseStringList(action.Preconditions);
            if (preconditions.Count == 0)
            {
                errors.Add(
                    $"goap_action {action.Id}: has no preconditions, so it is always applicable and the planner "
                    + "will always pick it");
            }

            var effects = LocalizationCatalog.ParseStringList(action.Effects);
            if (effects.Count == 0)
            {
                errors.Add(
                    $"goap_action {action.Id}: has no effects, so executing it changes nothing and re-planning "
                    + "picks it again forever");
            }

            foreach (string key in preconditions.Concat(effects))
            {
                // A leading '!' negates the key; the key itself is what must be
                // declared. Stripping it here rather than in the CSV means a designer
                // writes `!HasIntruder` and the validator still knows it is checking
                // HasIntruder.
                string name = key.StartsWith("!", StringComparison.Ordinal) ? key[1..] : key;

                if (!IsKnownGoapKey(name))
                {
                    errors.Add(
                        $"goap_action {action.Id}: '{key}' is not a declared world-state key, so the condition "
                        + "can never be true and the action can never run");
                }
            }

            foreach (string tag in LocalizationCatalog.ParseStringList(action.RequiredArchetypeTags))
            {
                if (!KnownArchetypeTags.Contains(tag))
                    errors.Add($"goap_action {action.Id}: requires unknown archetype tag '{tag}'");
            }

            if (action.BaseCost <= 0)
                errors.Add($"goap_action {action.Id}: base_cost must be > 0");

            if (action.DurationSteps < 1)
                errors.Add($"goap_action {action.Id}: duration_steps must be >= 1");

            if (action.InterruptPriority < 0)
                errors.Add($"goap_action {action.Id}: interrupt_priority must be >= 0");
        }

        foreach (GoapGoal goal in _tables.TbGoapGoal.DataList)
        {
            if (LocalizationCatalog.ParseStringList(goal.SatisfactionCondition).Count == 0)
                errors.Add($"goap_goal {goal.Id}: has no satisfaction condition, so it can never be completed");

            if (LocalizationCatalog.ParseStringList(goal.PriorityCurve).Count == 0)
                errors.Add($"goap_goal {goal.Id}: has an empty priority curve, so it can never be selected");

            foreach (string arch in LocalizationCatalog.ParseStringList(goal.ValidArchetypes))
            {
                if (!KnownArchetypeTags.Contains(arch))
                    errors.Add($"goap_goal {goal.Id}: valid_archetypes has unknown tag '{arch}'");
            }
        }

        // Every archetype tag that exists must be able to do something. Without this,
        // a civilian goal set can exist while every action requires a tag no
        // archetype carries, and the whole class of NPC just stands still.
        var requiredTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (GoapAction action in _tables.TbGoapAction.DataList)
        {
            foreach (string tag in LocalizationCatalog.ParseStringList(action.RequiredArchetypeTags))
                requiredTags.Add(tag);
        }

        var availableTags = new HashSet<string>(StringComparer.Ordinal) { "any" };
        foreach (GuardArchetype guard in _tables.TbGuardArchetype.DataList)
            availableTags.Add(ArchetypeTagFor(guard.Role));

        // Civilians are planned by the same GOAP but are not guard_archetype rows —
        // they are the site's own staff. Adding them here rather than inventing a
        // fake archetype is what keeps the tag vocabulary honest.
        foreach (string npcClass in NpcClassTags)
            availableTags.Add(npcClass);

        foreach (string tag in requiredTags)
        {
            if (!availableTags.Contains(tag))
            {
                errors.Add(
                    $"goap_action: requires archetype tag '{tag}', which no archetype carries, so those "
                    + "actions can never be planned");
            }
        }
    }

    /// <summary>The archetype tag a <see cref="GuardRole"/> corresponds to.</summary>
    private static string ArchetypeTagFor(GuardRole role) => role switch
    {
        GuardRole.Patrol => "patrol",
        GuardRole.Sentry => "sentry",
        GuardRole.Responder => "responder",
        GuardRole.Specialist => "specialist",
        _ => "any",
    };

    /// <summary>Tactical actions must name real skills and plausible costs.</summary>
    private void ValidateTacticalActionContent(List<string> errors)
    {
        var validSkills = Enum.GetValues<TableSkillKind>().ToHashSet();

        foreach (TacticalAction action in _tables.TbTacticalAction.DataList)
        {
            if (!validSkills.Contains(action.SkillUsed))
                errors.Add($"tactical_action {action.Id}: skill_used '{action.SkillUsed}' is not a known skill");

            if (action.StepsCost < 1)
                errors.Add($"tactical_action {action.Id}: steps_cost must be >= 1, was {action.StepsCost}");

            foreach ((string name, int value) in new[]
                     {
                         ("stamina_cost", action.StaminaCost),
                         ("mental_cost", action.MentalCost),
                         ("heat_cost", action.HeatCost),
                         ("evidence_level", action.EvidenceLevel),
                         ("mental_cost_on_witness", action.MentalCostOnWitness),
                     })
            {
                if (value < 0)
                    errors.Add($"tactical_action {action.Id}: {name} must be >= 0, was {value}");
            }

            // A d100 check with no skill behind it is a coin flip the player cannot
            // influence by choosing who to send.
            if (action.BaseDc > 0 && action.SkillUsed == TableSkillKind.None)
            {
                errors.Add(
                    $"tactical_action {action.Id}: has base_dc {action.BaseDc} but uses no skill, so the roll "
                    + "cannot be influenced by the agent sent to make it");
            }

            // knowledge.md rule 19: lethality is a tuning decision. An action marked
            // lethal that costs no Heat, no evidence and no witness penalty is a
            // moral stance hard-coded into the data, which is exactly what the rule
            // forbids — every lethal act must carry a data-driven cost.
            if (action.IsLethal && action.HeatCost == 0 && action.EvidenceLevel == 0
                && action.MentalCostOnWitness == 0)
            {
                errors.Add(
                    $"tactical_action {action.Id}: is lethal but costs no heat, evidence or witness penalty — "
                    + "lethality must be priced in the data, never assumed (knowledge.md rule 19)");
            }
        }

        // The player's squad must be able to open a locked door somehow, or a site
        // whose only exit is locked is unwinnable rather than difficult.
        bool anyForcible = _tables.TbTacticalAction.DataList.Any(a =>
            a.SkillUsed == TableSkillKind.Tech && a.BaseDc > 0);
        if (!anyForcible)
        {
            errors.Add(
                "tactical_action: no Tech-skillled action with a difficulty check exists, so a locked door "
                + "cannot be opened and a locked site is unwinnable rather than difficult");
        }

        foreach (SleeperOp op in _tables.TbSleeperOp.DataList)
        {
            if (op.SiteTier < 1)
                errors.Add($"sleeper_op {op.Id}: site_tier must be >= 1");

            if (op.TicksPerIntelPercent < 1)
                errors.Add($"sleeper_op {op.Id}: ticks_per_intel_percent must be >= 1");

            foreach ((string name, int value) in new[]
                     {
                         ("base_discovery_chance_per_tick", op.BaseDiscoveryChancePerTick),
                         ("poison_chance_on_discovery", op.PoisonChanceOnDiscovery),
                         ("heat_on_discovery", op.HeatOnDiscovery),
                     })
            {
                if (value < 0)
                    errors.Add($"sleeper_op {op.Id}: {name} must be >= 0, was {value}");
            }

            if (op.BaseDiscoveryChancePerTick > 100 || op.PoisonChanceOnDiscovery > 100)
                errors.Add($"sleeper_op {op.Id}: chance values must be 0..100");
        }

        foreach (CaptureSite site in _tables.TbCaptureSite.DataList)
        {
            if (LocalizationCatalog.ParseStringList(site.HoldingRoomTags).Count == 0)
                errors.Add($"capture_site {site.Id}: has no holding_room_tags, so a captured agent has nowhere to be held");

            if (site.RescueTimeLimitDays < 1)
                errors.Add($"capture_site {site.Id}: rescue_time_limit_days must be >= 1");

            if (site.IntelExtractedPerDay < 0 || site.HeatPerDay < 0)
                errors.Add($"capture_site {site.Id}: per-day values must be >= 0");
        }
    }

    /// <summary>Room templates must describe a space the generator can actually build.</summary>
    private void ValidateRoomTemplateContent(List<string> errors)
    {
        foreach (RoomTemplate room in _tables.TbRoomTemplate.DataList)
        {
            if (room.WidthMin < 1)
                errors.Add($"room_template {room.Id}: width_min must be >= 1, was {room.WidthMin}");

            // The one that matters: a maximum below the minimum makes every roll of
            // the width an invalid room, and the generator skips the template instead
            // of complaining.
            if (room.WidthMax < room.WidthMin)
            {
                errors.Add(
                    $"room_template {room.Id}: width_max ({room.WidthMax}) is below width_min ({room.WidthMin}), "
                    + "so no room of this type can ever be generated");
            }

            if (room.OccluderDensity is < 0 or > 100)
                errors.Add($"room_template {room.Id}: occluder_density must be 0..100");

            if (room.NoiseAbsorption is < 0 or > 100)
                errors.Add($"room_template {room.Id}: noise_absorption must be 0..100");

            foreach ((string name, int weight) in new[]
                     {
                         ("weight_tier_1", room.WeightTier1),
                         ("weight_tier_2", room.WeightTier2),
                         ("weight_tier_3", room.WeightTier3),
                     })
            {
                if (weight <= 0)
                {
                    errors.Add(
                        $"room_template {room.Id}: {name} must be > 0, was {weight} — a template that can never "
                        + "be chosen is dead data a designer believes they are using");
                }
            }

            // A room that can only be reached vertically and cannot itself hold a
            // vertical connection is a sealed box: the generator has to link floors,
            // and this template refuses to participate in any such link.
            if (room.ValidFloors == FloorKind.Any && room.DoorPositionsRule == DoorRule.VerticalOnly)
            {
                errors.Add(
                    $"room_template {room.Id}: VerticalOnly on Any floors makes every floor using it a sealed "
                    + "box — vertical connections live on the floor, not in the room's door rule");
            }

            // npc_tags is the only thing that decides whether a static inhabitant can
            // be placed here. An unknown tag is a typo that silently disables the
            // inhabitant, and a room with no tags at all is a deliberate "nobody here".
            foreach (string tag in LocalizationCatalog.ParseStringList(room.NpcTags))
            {
                if (!KnownNpcTags.Contains(tag))
                {
                    errors.Add(
                        $"room_template {room.Id}: npc_tags contains unknown tag '{tag}'; "
                        + $"known tags are {string.Join(", ", KnownNpcTags)}");
                }
            }

        // A guard is not restricted by DoorPositionsRule: a VerticalOnly room is reached
            // from above or below, which is exactly how the guard walks in too. The
            // generator gives every floor a vertical link, so the room is reachable
            // and so is the guard.
        }
    }

    /// <summary>
    /// The static inhabitant classes a <c>room_template.npc_tags</c> may name.
    /// </summary>
    /// <remarks>
    /// Listed rather than derived, for the same reason <c>RequiredSiteGenKeys</c> is:
    /// the check has to be able to fail against the data rather than against whatever
    /// the code happens to understand.
    /// </remarks>
    private static readonly HashSet<string> KnownNpcTags = new(StringComparer.Ordinal)
    {
        "civilian", "guard",
    };

    /// <summary>
    /// Every site template must be generable: no impossible constraint combination.
    /// </summary>
    /// <remarks>
    /// This is the check that catches a site which loads, generates, and then quietly
    /// produces a building with no objective or no route — the failure the stage-4
    /// brief calls "unwinnable rather than difficult", and the only place it can be
    /// caught without playing every site by hand.
    /// </remarks>
    private void ValidateSiteGeneratability(List<string> errors)
    {
        var rooms = _tables.TbRoomTemplate.DataList.ToDictionary(r => r.Id);

        foreach (SiteTemplate site in _tables.TbSiteTemplate.DataList)
        {
            if (site.Tier < 1)
                errors.Add($"site_template {site.Id}: tier must be >= 1");

            if (site.FloorCountMin < 1)
                errors.Add($"site_template {site.Id}: floor_count_min must be >= 1");

            if (site.FloorCountMax < site.FloorCountMin)
            {
                errors.Add(
                    $"site_template {site.Id}: floor_count_max ({site.FloorCountMax}) is below floor_count_min "
                    + $"({site.FloorCountMin}), so no floor count is legal");
            }

            if (site.WidthPerFloorMin < 1)
                errors.Add($"site_template {site.Id}: width_per_floor_min must be >= 1");

            if (site.WidthPerFloorMax < site.WidthPerFloorMin)
            {
                errors.Add(
                    $"site_template {site.Id}: width_per_floor_max ({site.WidthPerFloorMax}) is below "
                    + $"width_per_floor_min ({site.WidthPerFloorMin}), so no floor width is legal");
            }

            if (site.GuardCountMin < 0)
                errors.Add($"site_template {site.Id}: guard_count_min must be >= 0");

            if (site.GuardCountMax < site.GuardCountMin)
            {
                errors.Add(
                    $"site_template {site.Id}: guard_count_max ({site.GuardCountMax}) is below guard_count_min "
                    + $"({site.GuardCountMin}), so no guard count is legal");
            }

            var allowed = LocalizationCatalog.ParseIdList(site.AllowedRoomIds);

            if (allowed.Count == 0)
            {
                errors.Add($"site_template {site.Id}: allowed_room_ids is empty, so no rooms can be placed at all");
                continue;
            }

            // Every allowed room must be able to sit on the site's shortest floor and
            // fit inside its narrowest one. A room wider than the building is a room
            // the generator can never place.
            int narrowest = site.WidthPerFloorMin;
            int shortestFloors = site.FloorCountMin;

            var placeable = new List<RoomTemplate>();

            foreach (int roomId in allowed)
            {
                if (!rooms.TryGetValue(roomId, out RoomTemplate? room))
                    continue; // already reported as a dangling foreign key

                if (room.WidthMin > narrowest)
                {
                    errors.Add(
                        $"site_template {site.Id}: allows room_template {roomId}, whose width_min ({room.WidthMin}) "
                        + $"exceeds the site's narrowest floor ({narrowest}), so it can never be placed");
                }

                // A Ground-only room in a building whose every floor is Upper or
                // Basement is unreachable; Any is the only safe default.
                if (room.ValidFloors == FloorKind.Ground && site.FloorCountMin >= 1 && shortestFloors < 1)
                {
                    errors.Add($"site_template {site.Id}: allows room_template {roomId}, which has no valid floor");
                }

                placeable.Add(room);
            }

            // The objective room tags must be satisfiable by a room this site is
            // allowed to build. A site whose objective tag matches nothing produces a
            // mission with nothing to steal, rescue or observe.
            var objectiveTags = LocalizationCatalog.ParseStringList(site.ObjectiveRoomTags);
            var allowedTagSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (RoomTemplate room in placeable)
            {
                foreach (string tag in LocalizationCatalog.ParseStringList(room.Tags))
                    allowedTagSet.Add(tag);
            }

            foreach (string tag in objectiveTags)
            {
                if (!allowedTagSet.Contains(tag))
                {
                    errors.Add(
                        $"site_template {site.Id}: objective_room_tags requires '{tag}', which none of its allowed "
                        + "rooms carry, so every generated mission would have no objective");
                }
            }

            // At least two rooms must be placeable: one objective and one way in.
            if (placeable.Count < 2)
            {
                errors.Add(
                    $"site_template {site.Id}: allows only {placeable.Count} placeable room type(s); a site needs "
                    + "at least an entrance and an objective");
            }

            // A forward post is a second map region, so a tier-1 site promising one
            // is a promise the difficulty curve does not support.
            if (site.HasForwardPost && site.Tier < 2)
            {
                errors.Add(
                    $"site_template {site.Id}: offers a forward command post at tier {site.Tier}; forward posts "
                    + "are tier 2+ so the squad composition that needs one is not available that early");
            }

            // A single-floor site has no floor above or below to reach a VerticalOnly
            // room from, so allowing one produces a room nothing can enter. The
            // generator excludes such rooms rather than failing, which is the right
            // behaviour for a runtime and the wrong behaviour for a data bug.
            if (site.FloorCountMax <= 1)
            {
                foreach (int roomId in allowed)
                {
                    if (rooms.TryGetValue(roomId, out RoomTemplate? room) && room.DoorPositionsRule == DoorRule.VerticalOnly)
                    {
                        errors.Add(
                            $"site_template {site.Id}: is single-floor but allows room_template {roomId}, which is "
                            + "VerticalOnly — with no second floor there is no way in");
                    }
                }
            }

            // The site promises guards and civilians by count. Those counts can only be
            // placed in rooms whose npc_tags permit them, so a site whose allowed rooms
            // are all guard-free generates an empty guard roster.
            if (site.GuardCountMax > 0 && !placeable.Any(r => HasNpcTag(r, "guard")))
            {
                errors.Add(
                    $"site_template {site.Id}: promises up to {site.GuardCountMax} guards but none of its allowed "
                    + "rooms carries the 'guard' npc_tag, so no guard could ever be placed");
            }

            if (site.CivilianCount > 0 && !placeable.Any(r => HasNpcTag(r, "civilian")))
            {
                errors.Add(
                    $"site_template {site.Id}: promises {site.CivilianCount} civilians but none of its allowed "
                    + "rooms carries the 'civilian' npc_tag, so no civilian could ever be placed");
            }
        }

        // Tier 1 must exist and be playable, because it is the first thing the
        // player ever generates.
        if (!_tables.TbSiteTemplate.DataList.Any(s => s.Tier == 1))
            errors.Add("site_template: no tier-1 site exists, so the first mission has nothing to generate");
    }

    /// <summary>True when a room template lists the given static-inhabitant tag.</summary>
    private static bool HasNpcTag(RoomTemplate room, string tag)
    {
        foreach (string candidate in LocalizationCatalog.ParseStringList(room.NpcTags))
        {
            if (string.Equals(candidate, tag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // ---- stage 4 -------------------------------------------------------------

    // ---- stage 4 -------------------------------------------------------------

    /// <summary>
    /// The keys each stage-4 rule table must contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key that goes missing falls back to a documented constant in Core, which is
    /// exactly the failure this is here to prevent: the simulation keeps running,
    /// the number is quietly different from the one a designer approved, and nothing
    /// says so. <c>skill_cap</c> gets the same treatment for the same reason.
    /// </para>
    /// <para>
    /// Listed here rather than derived from Core, because the point is to catch a key
    /// the designer deleted — reading the list out of the code that reads it would
    /// make the check unable to fail.
    /// </para>
    /// </remarks>
    private static readonly string[] RequiredFogKeys =
    {
        "fog_base_reveal_radius",
        "fog_best_infiltration_weight_percent",
        "fog_team_avg_weight_pct",
        "fog_scout_radius_percent",
        "fog_gadget_scout_radius_bonus",
        "fog_terminal_scout_radius_bonus",
        "fog_silhouette_radius_percent",

        // The divisor pair stage 4 replaced the percentage radii with. Listed
        // explicitly so deleting one falls back to a documented constant rather than
        // silently changing what a player sees.
        "fog_silhouette_divisor",
        "fog_scouted_divisor",
        "fog_min_silhouette_radius",
        "fog_guard_band_many_min",
        "fog_gadget_reveal_radius_bonus",
        "fog_terminal_reveal_radius_bonus",
    };

    private static readonly string[] RequiredNodeInteriorKeys =
    {
        "interior_slot_count",
        "interior_container_count_max",
        "interior_guard_count_max",
        "interior_terminal_count_max",
        "interior_door_chance_percent",
        "interior_door_locked_percent",
        "interior_trap_chance_percent",
        "interior_trap_count_max",
        "interior_locked_container_percent",
    };

    /// <summary>Checks the stage-4 tables: fog of war and node interiors.</summary>
    private void ValidateStageFourTables(List<string> errors)
    {
        foreach (string key in RequiredFogKeys)
        {
            if (!_tables.TbFogRule.DataList.Any(r => r.RuleKey == key))
                errors.Add($"fog_rule: missing required key '{key}'; fog of war would silently use a fallback");
        }

        foreach (string key in RequiredNodeInteriorKeys)
        {
            if (!_tables.TbNodeInteriorRule.DataList.Any(r => r.RuleKey == key))
            {
                errors.Add(
                    $"node_interior_rule: missing required key '{key}'; node interiors would silently use a fallback");
            }
        }

        // The two infiltration weights are what make a specialist worth bringing, so
        // the average must never outweigh the specialist.
        int best = FogValue("fog_best_infiltration_weight_percent");
        int average = FogValue("fog_team_avg_weight_pct");

        if (average >= best)
        {
            errors.Add(
                $"fog_rule: average weight ({average}) must be lower than the best-infiltrator "
                + $"weight ({best}); otherwise team composition stops mattering");
        }

        // A divisor of zero is a division by zero inside a rule path, which is a crash
        // rather than a tuning mistake. Both are read through Math.Max(1, ...) in Core,
        // so the guard is belt-and-braces, but a designer who typed 0 wants to know.
        foreach (string key in new[] { "fog_silhouette_divisor", "fog_scouted_divisor" })
        {
            if (FogValue(key) < 1)
                errors.Add($"fog_rule: {key} must be >= 1, was {FogValue(key)}");
        }

        // Scouted is strictly more knowledge than Silhouette, so it must reach no
        // further. Equal divisors would collapse the two states into one, which is
        // worse than either being wrong because it silently deletes a state.
        int silhouetteDivisor = FogValue("fog_silhouette_divisor");
        int scoutedDivisor = FogValue("fog_scouted_divisor");

        if (scoutedDivisor <= silhouetteDivisor)
        {
            errors.Add(
                $"fog_rule: fog_scouted_divisor ({scoutedDivisor}) must exceed "
                + $"fog_silhouette_divisor ({silhouetteDivisor}); a larger divisor is a tighter "
                + "circle, and equal ones would collapse Scouted into Silhouette");
        }

        if (FogValue("fog_min_silhouette_radius") < 0)
            errors.Add("fog_rule: fog_min_silhouette_radius must be >= 0");

        // A scout radius wider than the reveal radius would mean every revealed node
        // is scouted and the distinction collapses.
        int scoutPercent = FogValue("fog_scout_radius_percent");
        if (scoutPercent is < 0 or > 100)
            errors.Add($"fog_rule: fog_scout_radius_percent must be 0..100, was {scoutPercent}");

        // A room with no slots cannot hold anything, and a container cap above the
        // slot count is unreachable weight the designer thinks they are using.
        int slotCount = InteriorValue("interior_slot_count");
        if (slotCount < 1)
            errors.Add($"node_interior_rule: interior_slot_count must be >= 1, was {slotCount}");

        int containerMax = InteriorValue("interior_container_count_max");
        int guardMax = InteriorValue("interior_guard_count_max");
        int terminalMax = InteriorValue("interior_terminal_count_max");

        foreach ((string name, int value) in new[]
                 {
                     ("interior_container_count_max", containerMax),
                     ("interior_guard_count_max", guardMax),
                     ("interior_terminal_count_max", terminalMax),
                 })
        {
            if (value < 0)
                errors.Add($"node_interior_rule: {name} must be >= 0, was {value}");
        }

        // A security node always gets at least one guard and a terminal node at least
        // one terminal, so a cap of zero would make the guarantee a lie rather than
        // a weak roll.
        if (guardMax < 1)
            errors.Add("node_interior_rule: interior_guard_count_max must be >= 1; a security node always holds a guard");

        if (terminalMax < 1)
            errors.Add("node_interior_rule: interior_terminal_count_max must be >= 1; a terminal node always holds a terminal");

        ValidateInteractableAndTemplateTables(errors);
    }

    /// <summary>
    /// Checks <c>interactable_type</c> and <c>room_template</c>, the two tables that
    /// decide what a mission room contains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this guards against is specific and nasty: a room whose template asks
    /// for more objects than its template allows slots, or whose required kinds are all
    /// barred by the room's tags. Neither throws at load. Both produce rooms that are
    /// silently emptier or fuller than a designer approved, and fog of war then reports
    /// a guard band the interior does not honour — a lie the simulation tells the
    /// player through its own read model.
    /// </para>
    /// </remarks>
    private void ValidateInteractableAndTemplateTables(List<string> errors)
    {
        var nodeRoomIds = new HashSet<int>();
        foreach (NodeRoom room in _tables.TbNodeRoom.DataList)
            nodeRoomIds.Add(room.Id);

        var lootTableIds = new HashSet<int>();
        foreach (LootTable loot in _tables.TbLootTable.DataList)
            lootTableIds.Add(loot.Id);

        // Every kind the table enum defines. There is deliberately no None member (unlike
// SkillKind), because every row in interactable_type names a kind that can actually
// appear in a room — there is no "this row does nothing" case to express.
var validKinds = Enum.GetValues<InteractableKind>().ToHashSet();

        // ---- interactable_type ----

        foreach (InteractableTypeRow type in _tables.TbInteractableType.DataList)
        {
            if (!validKinds.Contains(type.Kind))
                errors.Add($"interactable_type {type.Id}: '{type.Kind}' is not a kind Core knows");

            if (type.Weight <= 0)
            {
                errors.Add(
                    $"interactable_type {type.Id}: weight must be > 0, was {type.Weight} — "
                    + "a kind that can never be rolled is a kind that does not exist");
            }

            // A negative tick cost would let a command pay time back to the player.
            if (type.TickCost < 0)
                errors.Add($"interactable_type {type.Id}: tick_cost must be >= 0, was {type.TickCost}");

            if (type.Noise < 0)
                errors.Add($"interactable_type {type.Id}: noise must be >= 0, was {type.Noise}");

            if (type.LootTableId != 0 && !lootTableIds.Contains(type.LootTableId))
            {
                errors.Add($"interactable_type {type.Id}: references unknown loot_table {type.LootTableId}");
            }
        }

        // Every kind Core can place must have a row, or that kind is unreachable
        // however the tags fall. The reverse is deliberately not required: a designer
        // may retire a kind from the table while Core keeps the enum for old saves.
        foreach (InteractableKind kind in validKinds)
        {
            bool hasRow = _tables.TbInteractableType.DataList.Any(t => t.Kind == kind);
            if (!hasRow)
            {
                errors.Add(
                    $"interactable_type: no row for kind {kind}, so it can never appear in a room");
            }
        }

        // ---- room_template ----

        var templatesByRoom = new Dictionary<int, List<NodeInteriorTemplate>>();

        foreach (NodeInteriorTemplate template in _tables.TbNodeInteriorTemplate.DataList)
        {
            if (!nodeRoomIds.Contains(template.NodeRoomId))
            {
                errors.Add(
                    $"room_template {template.Id}: references unknown node_room {template.NodeRoomId}");
                continue;
            }

            if (!templatesByRoom.TryGetValue(template.NodeRoomId, out List<NodeInteriorTemplate>? list))
            {
                list = new List<NodeInteriorTemplate>();
                templatesByRoom[template.NodeRoomId] = list;
            }

            list.Add(template);

            if (template.MinInteractables < 0)
            {
                errors.Add(
                    $"room_template {template.Id}: min_interactables must be >= 0, was {template.MinInteractables}");
            }

            if (template.MaxInteractables < template.MinInteractables)
            {
                errors.Add(
                    $"room_template {template.Id}: max_interactables ({template.MaxInteractables}) is below "
                    + $"min_interactables ({template.MinInteractables}), so the roll range is empty");
            }

            // The one that actually bites. A room cannot hold more objects than it has
            // abstract slots, so a maximum above the slot count is unreachable weight a
            // designer believes they are using.
            if (template.MaxInteractables > template.SlotCount)
            {
                errors.Add(
                    $"room_template {template.Id}: max_interactables ({template.MaxInteractables}) exceeds "
                    + $"slot_count ({template.SlotCount}); the room cannot hold them all");
            }

            if (template.SlotCount < 1)
                errors.Add($"room_template {template.Id}: slot_count must be >= 1, was {template.SlotCount}");

            if (template.GuardMax < 0)
                errors.Add($"room_template {template.Id}: guard_max must be >= 0, was {template.GuardMax}");

            if (template.GuardMax > template.MaxInteractables)
            {
                errors.Add(
                    $"room_template {template.Id}: guard_max ({template.GuardMax}) exceeds "
                    + $"max_interactables ({template.MaxInteractables}); the promised guards could never all fit");
            }

            foreach (string kindName in LocalizationCatalog.ParseStringList(template.RequiredKinds))
            {
                if (!Enum.TryParse(kindName, ignoreCase: true, out InteractableKind kind))
                {
                    errors.Add(
                        $"room_template {template.Id}: required_kinds '{kindName}' is not a kind Core knows");
                    continue;
                }

                // A mandatory kind the room's own tags forbid is a contradiction the
                // generator resolves by silently dropping it.
                bool reachable = _tables.TbInteractableType.DataList.Any(t =>
                    t.Kind == kind && IsAllowedInSomeRoom(t, template.NodeRoomId));

                if (!reachable)
                {
                    errors.Add(
                        $"room_template {template.Id}: requires kind {kind}, but no room of this type is "
                        + "tagged to allow it, so the requirement can never be met");
                }
            }
        }

        // Every node room needs a template. Without one the generator falls back to a
        // default shape, which is legal but is not what the designer drew.
        foreach (NodeRoom room in _tables.TbNodeRoom.DataList)
        {
            if (!templatesByRoom.ContainsKey(room.Id))
            {
                errors.Add(
                    $"room_template: node_room {room.Id} has no template, so its interiors would fall back "
                    + "to hard-coded defaults");
            }
        }

        // A duplicate template per room room is ambiguous: the lookup takes the first
        // match, so which row wins would depend on table order.
        foreach ((int roomId, List<NodeInteriorTemplate> list) in templatesByRoom)
        {
            if (list.Count > 1)
            {
                errors.Add(
                    $"room_template: node_room {roomId} has {list.Count} templates; exactly one is used, "
                    + "so the others are dead data");
            }
        }
    }

    /// <summary>
    /// True when an <c>interactable_type</c> row is permitted in a given node room.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>SimulationRules.IsKindAllowedIn</c> — including the empty-list-means-
    /// anywhere rule. The duplication is deliberate: this is a check on the shipped
    /// bytes, and reading it from the code that consumes them would mean a bug in that
    /// code could not be caught here.
    /// </remarks>
    private bool IsAllowedInSomeRoom(InteractableTypeRow type, int nodeRoomId)
    {
        foreach (NodeRoom room in _tables.TbNodeRoom.DataList)
        {
            if (room.Id != nodeRoomId)
                continue;

            var roomTags = LocalizationCatalog.ParseStringList(room.Tags);

            if (LocalizationCatalog.ParseStringList(type.AllowedRoomTags).Count == 0)
                return true;

            foreach (string tag in LocalizationCatalog.ParseStringList(type.AllowedRoomTags))
            {
                if (roomTags.Contains(tag))
                    return true;
            }

            return false;
        }

        return false;
    }

    private int FogValue(string key)
    {
        foreach (FogRule rule in _tables.TbFogRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return 0;
    }

    private int InteriorValue(string key)
    {
        foreach (NodeInteriorRule rule in _tables.TbNodeInteriorRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return 0;
    }

    // ---- stage 3 -------------------------------------------------------------

    /// <summary>
    /// Checks the stage-3 tables. Kept together because they share one idea: every
    /// value the simulation reads at runtime must be reachable and sane, or a rule
    /// silently does the wrong thing instead of failing loudly.
    /// </summary>
    /// <summary>
    /// Reads an economy_rule value by key, falling back to the same default Core uses.
    /// </summary>
    /// <remarks>
    /// The fallback mirrors <c>SimulationRules.Economy</c> so the validator reasons
    /// about the ladder using the numbers Core would actually use, rather than
    /// disagreeing with it about what a missing key means.
    /// </remarks>
    private int SimulationFallback(string key)
    {
        foreach (EconomyRule rule in _tables.TbEconomyRule.DataList)
        {
            if (rule.RuleKey == key)
                return rule.Value;
        }

        return key switch
        {
            "settlement_day_of_week" => 6,
            "bankruptcy_grace_days" => 7,
            "bankruptcy_stage_loyalty_drain" => 2,
            "bankruptcy_stage_resign_day" => 3,
            "bankruptcy_stage_three_day" => 6,
            "bankruptcy_stage_four_day" => 10,
            "bankruptcy_max_stage" => 4,
            "upkeep_multiplier_percent" => 100,
            "loan_interest_compounds" => 1,
            _ => 0,
        };
    }

    private void ValidateStageThreeTables(List<string> errors)
    {
        var roomIds = new HashSet<int>();
        foreach (RoomType room in _tables.TbRoomType.DataList)
            roomIds.Add(room.Id);

        // Every stage-3 table row must point at a room type that exists. A recovery or
        // training rule about a room that is not in room_type is dead data.
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            if (!roomIds.Contains(rule.RoomTypeId))
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: references unknown room_type {rule.RoomTypeId}");
            }

            if (rule.RestorePhysicalPercentPerTick < 0 || rule.RestoreMentalPercentPerTick < 0)
            {
                errors.Add($"recovery_rule {rule.Id}: restore percentages must be >= 0");
            }

            if (rule.MentalRatioPercent is < 0 or > 100)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: mental_ratio_percent must be 0..100, " +
                    $"was {rule.MentalRatioPercent}");
            }

            // A recovery row that restores nothing and heals nothing is unreachable
            // data; a designer would reasonably assume the room does something.
            if (rule.RestorePhysicalPercentPerTick == 0
                && rule.RestoreMentalPercentPerTick == 0
                && rule.InjuryHealPercentPerTick == 0
                && rule.BurnoutReliefPercentPerTick == 0)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: has no effect at all, so the room is unreachable");
            }
        }

        foreach (TrainingRule rule in _tables.TbTrainingRule.DataList)
        {
            if (!roomIds.Contains(rule.RoomTypeId))
                errors.Add($"training_rule {rule.Id}: references unknown room_type {rule.RoomTypeId}");

            if (rule.StaminaCostPhysical < 0 || rule.StaminaCostMental < 0)
                errors.Add($"training_rule {rule.Id}: stamina costs must be >= 0");

            // A training room that costs no stamina makes overworking impossible, which
            // silently removes the entire "overworking is a mistake" design.
            if (rule.StaminaCostPhysical == 0 && rule.StaminaCostMental == 0)
                errors.Add($"training_rule {rule.Id}: must consume stamina, or overworking cannot happen");

            if (rule.OverworkThreshold is < 0 or > 100)
                errors.Add($"training_rule {rule.Id}: overwork_threshold must be 0..100");

            if (rule.OverworkGainPercent is < 0 or > 100)
            {
                errors.Add(
                    $"training_rule {rule.Id}: overwork_gain_percent must be 0..100, " +
                    $"was {rule.OverworkGainPercent}");
            }
        }

        foreach (SkillCap cap in _tables.TbSkillCap.DataList)
        {
            if (cap.SoftCap < 0)
                errors.Add($"skill_cap {cap.Id}: soft_cap must be >= 0");

            if (cap.HardCap <= cap.SoftCap)
            {
                errors.Add(
                    $"skill_cap {cap.Id}: hard_cap ({cap.HardCap}) must exceed soft_cap ({cap.SoftCap}); " +
                    "otherwise the diminishing-returns ramp has no width");
            }

            if (cap.FloorPercent is < 0 or > 100)
                errors.Add($"skill_cap {cap.Id}: floor_percent must be 0..100");
        }

        // All five skills need a cap row. A missing one leaves that skill uncapped,
        // which would let one agent grow indefinitely while others taper off.
        foreach (TableSkillKind skill in Enum.GetValues<TableSkillKind>())
        {
            if (skill == TableSkillKind.None)
                continue;

            bool hasCap = _tables.TbSkillCap.DataList.Any(c => c.SkillKind == skill);
            if (!hasCap)
                errors.Add($"skill_cap: no row for skill {skill}, so it can never be capped");
        }

        // morale_band must tile 0..100 exactly. A gap would make some loyalty values
        // fall through to Resentful and make the UI lie.
        var bandFloors = new List<int>();
        foreach (MoraleBand band in _tables.TbMoraleBand.DataList)
        {
            bandFloors.Add(band.LoyaltyFloor);

            if (band.LoyaltyFloor > band.LoyaltyCeiling)
                errors.Add($"morale_band {band.Id}: loyalty_floor exceeds loyalty_ceiling");

            if (band.MissChancePercent is < 0 or > 100)
                errors.Add($"morale_band {band.Id}: miss_chance_percent must be 0..100");
        }

        bandFloors.Sort();
        if (bandFloors.Count > 0 && bandFloors[0] != 0)
        {
            errors.Add(
                $"morale_band: lowest floor is {bandFloors[0]}, must be 0 so every loyalty value has a band");
        }

        foreach (MoraleBand band in _tables.TbMoraleBand.DataList)
        {
            // The next band up must start exactly where this one ends. Comparing against
            // the floor rather than the ceiling would let a band end at 74 and the next
            // begin at 60, silently re-labelling the overlap.
            bool continues = bandFloors.Any(f => f == band.LoyaltyCeiling + 1);

            if (!continues && band.LoyaltyCeiling < 100)
            {
                errors.Add(
                    $"morale_band {band.Id}: loyalty range {band.LoyaltyFloor}-{band.LoyaltyCeiling} " +
                    "is not followed by a band starting at " + (band.LoyaltyCeiling + 1));
            }
        }

        // loyalty_drift.condition must be a name Core knows about. Core maps the enum to
        // a string; a typo would return 0 and the penalty would silently vanish.
        var knownConditions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Overworked", "Working", "Idle", "MissionLoss", "MissionSuccess",
            "BadRoommate", "PoorFacility", "GoodFacility", "PayFair", "PayUnfair", "Burnout",
        };

        var seenConditions = new HashSet<string>(StringComparer.Ordinal);
        foreach (LoyaltyDrift drift in _tables.TbLoyaltyDrift.DataList)
        {
            if (!knownConditions.Contains(drift.Condition))
            {
                errors.Add(
                    $"loyalty_drift {drift.Id}: condition '{drift.Condition}' is not a condition Core knows");
            }

            if (!seenConditions.Add(drift.Condition))
                errors.Add($"loyalty_drift: condition '{drift.Condition}' is defined more than once");

            if (drift.Delta == 0)
                errors.Add($"loyalty_drift {drift.Id}: delta is 0, so this source can never affect anything");
        }

        foreach (LoyaltyThreshold threshold in _tables.TbLoyaltyThreshold.DataList)
        {
            if (threshold.Threshold is < 0 or > 100)
                errors.Add($"loyalty_threshold {threshold.Id}: threshold must be 0..100");

            if (threshold.ChancePercent is < 0 or > 100)
                errors.Add($"loyalty_threshold {threshold.Id}: chance_percent must be 0..100");

            if (threshold.CooldownDays < 0)
                errors.Add($"loyalty_threshold {threshold.Id}: cooldown_days must be >= 0");
        }

        foreach (LoanTier tier in _tables.TbLoanTier.DataList)
        {
            if (tier.Amount <= 0)
                errors.Add($"loan_tier {tier.Id}: amount must be > 0");

            if (tier.WeeklyInterestPercent < 0)
                errors.Add($"loan_tier {tier.Id}: weekly_interest_percent must be >= 0");

            if (tier.MaxActive < 1)
                errors.Add($"loan_tier {tier.Id}: max_active must be >= 1");
        }

        foreach (RecruitRule rule in _tables.TbRecruitRule.DataList)
        {
            if (rule.HrLevel < 1)
                errors.Add($"recruit_rule {rule.Id}: hr_level must be >= 1");

            if (rule.ReputationTier is < 0 or > 2)
                errors.Add($"recruit_rule {rule.Id}: reputation_tier must be 0..2");

            if (rule.PoolSize < 1)
                errors.Add($"recruit_rule {rule.Id}: pool_size must be >= 1");

            if (rule.SkillVariance < 0)
                errors.Add($"recruit_rule {rule.Id}: skill_variance must be >= 0");

            if (rule.TraitCountMin < 1)
                errors.Add($"recruit_rule {rule.Id}: trait_count_min must be >= 1");

            if (rule.TraitCountMax < rule.TraitCountMin)
                errors.Add($"recruit_rule {rule.Id}: trait_count_max is below trait_count_min");

            if (rule.HiddenTraitChance is < 0 or > 100)
                errors.Add($"recruit_rule {rule.Id}: hidden_trait_chance must be 0..100");
        }

        // Every (hr_level, reputation_tier) pair Core can ask about must resolve to a
        // row. The lookup degrades to a lower HR level on purpose, but only within the
        // same tier — a tier with no rows at all would stop recruiting entirely.
        foreach (int repTier in ReputationTiers)
        {
            bool any = _tables.TbRecruitRule.DataList.Any(r => r.ReputationTier == repTier);
            if (!any)
                errors.Add($"recruit_rule: no rows for reputation tier {repTier}, so recruiting stops at that tier");
        }

        // Keyed rule tables must not be missing a key Core reads. A missing key falls
        // back to a hard-coded default in SimulationRules, which is precisely the
        // silent-wrong-answer failure the facade exists to prevent.
        var requiredEconomyKeys = new[]
        {"settlement_day_of_week", "bankruptcy_grace_days", "bankruptcy_stage_loyalty_drain",
      "bankruptcy_stage_resign_day", "bankruptcy_stage_three_day", "bankruptcy_stage_four_day",
      "bankruptcy_forced_resign_count",
            "bankruptcy_forced_resign_loyalty_floor", "bankruptcy_forced_resign_chance",
            "bankruptcy_penalty_scale_percent", "bankruptcy_max_stage", "upkeep_multiplier_percent",
            "loan_interest_compounds",
        };

        foreach (string key in requiredEconomyKeys)
        {
            bool found = _tables.TbEconomyRule.DataList.Any(r => r.RuleKey == key);
            if (!found)
                errors.Add($"economy_rule: missing required key '{key}'");
        }

        var requiredBurnoutKeys = new[]
        {
            "burnout_mental_threshold", "burnout_daily_loyalty_drain",
            "burnout_resting_loyalty_drain",
            "burnout_recovery_mental_percent", "burnout_recovery_physical_percent",
            "burnout_exit_mental",
        };

        foreach (string key in requiredBurnoutKeys)
        {
            bool found = _tables.TbBurnoutRule.DataList.Any(r => r.RuleKey == key);
            if (!found)
                errors.Add($"burnout_rule: missing required key '{key}'");
        }

        var requiredIntelKeys = new[]
        {
            "investigation_ticks_per_step", "investigation_evidence_chance",
            "investigation_false_lead_chance", "mole_weekly_heat", "mole_leak_difficulty_bonus",
            "mole_leak_chance_percent", "wrong_accusation_loyalty_drain",
            "counter_intel_capacity_per_level",
            "investigation_evidence_gain", "investigation_false_lead_penalty",
            "investigation_max_steps", "expose_threshold",
        };

        foreach (string key in requiredIntelKeys)
        {
            bool found = _tables.TbCounterIntelRule.DataList.Any(r => r.RuleKey == key);
            if (!found)
                errors.Add($"counter_intel_rule: missing required key '{key}'");
        }

        ValidateCounterIntelBalance(errors);
    }

    /// <summary>
    /// Asserts that an investigation can actually conclude.
    /// </summary>
    /// <remarks>
    /// A case that gains <c>gain</c> evidence per good step, loses <c>penalty</c> per
    /// false lead, runs for <c>maxSteps</c> steps and must clear <c>threshold</c> to
    /// expose the mole is a system that silently does nothing if a designer raises the
    /// threshold or lowers the gain — the desk simply never produces a verdict and the
    /// mole becomes uncatchable. Nothing errors at runtime; the feature just quietly
    /// stops existing. So the relationship is checked here instead.
    /// </remarks>
    private void ValidateCounterIntelBalance(List<string> errors)
    {
        int Value(string key, int fallback)
            => _tables.TbCounterIntelRule.DataList.FirstOrDefault(r => r.RuleKey == key)?.Value ?? fallback;

        int gain = Value("investigation_evidence_gain", -1);
        int penalty = Value("investigation_false_lead_penalty", -1);
        int maxSteps = Value("investigation_max_steps", -1);
        int threshold = Value("expose_threshold", -1);

        if (gain < 0 || penalty < 0 || maxSteps <= 0 || threshold <= 0)
            return; // keys already reported as missing above

        if (gain <= 0)
        {
            errors.Add(
                "counter_intel_rule: investigation_evidence_gain must be positive or no case can ever conclude");
        }

        // The ceiling of the evidence scale is 100; the gain has to be able to reach it.
        if (gain > 100)
            errors.Add($"counter_intel_rule: investigation_evidence_gain {gain} exceeds the 0-100 evidence scale");

        // Best case: every step is a success.
        int bestCase = maxSteps * gain;
        if (bestCase <= threshold)
        {
            errors.Add(
                $"counter_intel_rule: a case cannot expose a mole — {maxSteps} steps at {gain} evidence "
                + $"reaches {bestCase}, below the expose threshold of {threshold}");
        }

        // Realistic case: the expected outcome of the configured branch probabilities.
        //
        // This one is deliberately expected to fall SHORT of the threshold. Evidence
        // accrues identically whoever you investigate, so if the average case cleared
        // the threshold the desk would expose agents at random and the player's own
        // work — correlating Heat spikes against the mission log — would be worth
        // nothing. The desk is a machine for converting a suspicion you already hold
        // into proof, and only good rolls on top of that will do it.
        int evidenceChance = Value("investigation_evidence_chance", 0);
        int falseLeadChance = Value("investigation_false_lead_chance", 0);
        int expectedPerStep = evidenceChance * gain / 100 - falseLeadChance * penalty / 100;

        if (expectedPerStep <= 0)
        {
            errors.Add(
                "counter_intel_rule: false leads cancel out evidence on average "
                + $"({evidenceChance}% x {gain} vs {falseLeadChance}% x {penalty}) — "
                + "investigations can only ever get worse, so a conclusive case is a "
                + "matter of luck rather than of narrowing the suspect list down");
        }
        else if (maxSteps * expectedPerStep >= threshold)
        {
            errors.Add(
                $"counter_intel_rule: the average investigation reaches "
                + $"{maxSteps * expectedPerStep}, at or above the expose threshold of {threshold} — "
                + "the desk would expose people at random and correlating Heat against "
                + "the mission log would be pointless");
        }

        // The settlement day must be a real day of the week.
        foreach (EconomyRule rule in _tables.TbEconomyRule.DataList)
        {
            if (rule.RuleKey == "settlement_day_of_week" && rule.Value is < 0 or > 6)
            {
                errors.Add(
                    $"economy_rule: settlement_day_of_week must be 0..6, was {rule.Value}");
            }

            if (rule.RuleKey == "bankruptcy_max_stage" && rule.Value < 1)
            {
                errors.Add(
                    "economy_rule: bankruptcy_max_stage must be >= 1, " +
                    "or the run can end before the grace period is even spent");
            }


        // The ladder's stage thresholds must ascend, or a later stage would never be
        // reachable and the designer would believe in a penalty that never fires.
        int stage2 = SimulationFallback("bankruptcy_stage_resign_day");
        int stage3 = SimulationFallback("bankruptcy_stage_three_day");
        int stage4 = SimulationFallback("bankruptcy_stage_four_day");

        if (!(stage2 < stage3 && stage3 < stage4))
        {
            errors.Add(
                $"economy_rule: bankruptcy stage thresholds must ascend, got {stage2}, {stage3}, {stage4}");
        }

        // "Never instantly lose": the grace period has to be a real period.
        if (SimulationFallback("bankruptcy_grace_days") < 1)
        {
            errors.Add("economy_rule: bankruptcy_grace_days must be >= 1");
        }
    }

        // The counter-intel room lookup in MoleSystem is derived from category, effect
        // and cost. If no room matches, investigations can never run.
        bool hasCounterIntel = _tables.TbRoomType.DataList.Any(
            r => r.Category == RoomCategory.Admin
                 && r.EffectType == EffectType.HeatReduction
                 && r.BuildCost > 2500);

        if (!hasCounterIntel)
            errors.Add("room_type: no Admin/HeatReduction room above 2500 cost, so counter-intel can never be identified");// The designed relationship: mental is a scarcer resource than physical. Enforced
        // as a band rather than an exact ratio because the per-room numbers are integers
        // and rounding would otherwise fail a correct table.
        long physicalTotal = 0;
        long mentalTotal = 0;
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            physicalTotal += rule.RestorePhysicalPercentPerTick;
            mentalTotal += rule.RestoreMentalPercentPerTick;
        }

        if (physicalTotal > 0)
        {
            // "Roughly a third" — wide enough to survive integer rounding and a designer
            // nudging a room, narrow enough to catch the ratio being quietly inverted.
            if (mentalTotal * 100L < physicalTotal * 20L || mentalTotal * 100L > physicalTotal * 60L)
            {
                errors.Add(
                    $"recovery_rule: total mental restore ({mentalTotal}) should be roughly a third " +
                    $"of total physical restore ({physicalTotal}); the designed ratio is " +
                    $"mental_ratio_percent");
            }
        }

        // A room that serves both pools must never restore mental faster than physical: that
        // would invert the scarcity the rest economy depends on. Specialist rooms are
        // exempt — the therapy office restoring almost no physical stamina is the whole
        // point of it being a specialist.
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            bool servesBoth = rule.RestorePhysicalPercentPerTick > 0
                              && rule.RestoreMentalPercentPerTick > 0;

            if (servesBoth && rule.RestoreMentalPercentPerTick > rule.RestorePhysicalPercentPerTick)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: a room that serves both pools restores mental faster " +
                    "than physical, which inverts the intended scarcity of mental stamina");
            }
        }

        // At least one room must be able to relieve burnout, or burnout is permanent —
        // which contradicts the design requirement that it be recoverable.
        bool anyBurnoutRelief = _tables.TbRecoveryRule.DataList.Any(r => r.BurnoutReliefPercentPerTick > 0);
        if (!anyBurnoutRelief)
        {
            errors.Add(
                "recovery_rule: no room provides burnout_relief_percent_per_tick, " +
                "which would make burnout permanent");
        }

        // The mental ratio must be a sane percentage, and it is the documented source
        // of the "mental is slower" design.
        foreach (RecoveryRule rule in _tables.TbRecoveryRule.DataList)
        {
            if (rule.MentalRatioPercent is < 10 or > 90)
            {
                errors.Add(
                    $"recovery_rule {rule.Id}: mental_ratio_percent ({rule.MentalRatioPercent}) " +
                    "is outside the plausible 10..90 band");
            }
        }

        // There must be a mole trait for the heat system to act on.
        bool hasMole = _tables.TbTrait.DataList.Any(t => t.EffectType == "HeatGain");
        if (!hasMole)
        {
            errors.Add(
                "trait: no trait with effect_type HeatGain exists, so the mole adds no Heat");
        }
    }

    // ---- foreign keys --------------------------------------------------------

    private void ValidateNoDanglingForeignKeys(List<string> errors)
    {
          var roomIds = new HashSet<int>();
        foreach (RoomType room in _tables.TbRoomType.DataList)
            roomIds.Add(room.Id);

        var itemIds = new HashSet<int>();
        foreach (Item item in _tables.TbItem.DataList)
            itemIds.Add(item.Id);

        var eventIds = new HashSet<int>();
        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
            eventIds.Add(e.Id);

        var lootTableIds = new HashSet<int>();
        foreach (LootTable loot in _tables.TbLootTable.DataList)
            lootTableIds.Add(loot.Id);

        // room_type.preferred_room_ids on agent_class
        foreach (AgentClass cls in _tables.TbAgentClass.DataList)
        {
            foreach (int roomId in LocalizationCatalog.ParseIdList(cls.PreferredRoomIds))
            {
                if (!roomIds.Contains(roomId))
                    errors.Add($"agent_class {cls.Id}: preferred_room_ids references unknown room {roomId}");
            }
        }

        // node_room.loot_table_id
        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            if (node.LootTableId != 0 && !lootTableIds.Contains(node.LootTableId))
                errors.Add($"node_room {node.Id}: loot_table_id {node.LootTableId} does not exist");
        }

        // node_room.possible_event_ids
        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            foreach (int eventId in LocalizationCatalog.ParseIdList(node.PossibleEventIds))
            {
                if (!eventIds.Contains(eventId))
                    errors.Add($"node_room {node.Id}: possible_event_ids references unknown mission_event {eventId}");
            }
        }

        // mission_event.loot_table_id
        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            if (e.LootTableId != 0 && !lootTableIds.Contains(e.LootTableId))
                errors.Add($"mission_event {e.Id}: loot_table_id {e.LootTableId} does not exist");
        }

        // loot_table.item_id
        foreach (LootTable loot in _tables.TbLootTable.DataList)
        {
            if (!itemIds.Contains(loot.ItemId))
                errors.Add($"loot_table {loot.Id}: entry {loot.EntryId} references unknown item {loot.ItemId}");
        }

        // room_upgrade.room_type_id
        var upgradeLevels = new Dictionary<int, HashSet<int>>();
        foreach (RoomUpgrade upgrade in _tables.TbRoomUpgrade.DataList)
        {
            if (!roomIds.Contains(upgrade.RoomTypeId))
            {
                errors.Add($"room_upgrade {upgrade.Id}: references unknown room_type {upgrade.RoomTypeId}");
                continue;
            }

            if (!upgradeLevels.TryGetValue(upgrade.RoomTypeId, out HashSet<int>? levels))
            {
                levels = new HashSet<int>();
                upgradeLevels[upgrade.RoomTypeId] = levels;
            }

            if (!levels.Add(upgrade.Level))
            {
                errors.Add(
                    $"room_upgrade: duplicate level {upgrade.Level} for room_type {upgrade.RoomTypeId}");
            }
        }

        // trait.conflicts_with must point at real traits and never at itself
        var traitIds = new HashSet<int>();
        foreach (Trait trait in _tables.TbTrait.DataList)
            traitIds.Add(trait.Id);

        foreach (Trait trait in _tables.TbTrait.DataList)
        {
            foreach (int other in LocalizationCatalog.ParseIdList(trait.ConflictsWith))
            {
                if (!traitIds.Contains(other))
                    errors.Add($"trait {trait.Id}: conflicts_with references unknown trait {other}");

                if (other == trait.Id)
                    errors.Add($"trait {trait.Id}: conflicts_with references itself");
            }
        }
    }

    // ---- weights -------------------------------------------------------------

    private void ValidateWeightsArePositive(List<string> errors)
    {
        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            if (node.Weight <= 0)
                errors.Add($"node_room {node.Id}: weight must be > 0, was {node.Weight}");
        }

        foreach (LootTable loot in _tables.TbLootTable.DataList)
        {
            if (loot.Weight <= 0)
                errors.Add($"loot_table {loot.Id} entry {loot.EntryId}: weight must be > 0, was {loot.Weight}");

            if (loot.MinCount < 0)
                errors.Add($"loot_table {loot.Id} entry {loot.EntryId}: min_count must be >= 0, was {loot.MinCount}");

            if (loot.MaxCount < loot.MinCount)
            {
                errors.Add(
                    $"loot_table {loot.Id} entry {loot.EntryId}: max_count ({loot.MaxCount}) " +
                    $"is below min_count ({loot.MinCount})");
            }
        }

        foreach (ContractOffer offer in _tables.TbContractOffer.DataList)
        {
            if (offer.Weight <= 0)
                errors.Add($"contract_offer {offer.Id}: weight must be > 0, was {offer.Weight}");
        }
    }

    // ---- localization --------------------------------------------------------

    /// <summary>
    /// Every key the tables reference must exist in both languages with real text.
    /// </summary>
    private void ValidateLocalizationCoverage(List<string> errors)
    {
        foreach ((string key, string source) in EnumerateTableKeys())
        {
            if (!_th.Contains(key))
                errors.Add($"localization th: missing key '{key}' (referenced by {source})");

            if (!_en.Contains(key))
                errors.Add($"localization en: missing key '{key}' (referenced by {source})");

            // A key that exists but is blank renders as an empty label in game.
            if (_th.Contains(key) && _th[key].Length == 0)
                errors.Add($"localization th: key '{key}' is empty");

            if (_en.Contains(key) && _en[key].Length == 0)
                errors.Add($"localization en: key '{key}' is empty");
        }
    }

    /// <summary>Yields every localization key the tables reference, with its origin.</summary>
    private IEnumerable<(string Key, string Source)> EnumerateTableKeys()
    {
        foreach (SiteTemplate s in _tables.TbSiteTemplate.DataList)
            yield return (s.NameKey, $"site_template {s.Id}");

        foreach (RoomTemplate r in _tables.TbRoomTemplate.DataList)
            yield return (r.NameKey, $"room_template {r.Id}");

        foreach (LightingProfile p in _tables.TbLightingProfile.DataList)
            yield return (p.NameKey, $"lighting_profile {p.Id}");

        foreach (LightSource l in _tables.TbLightSource.DataList)
            yield return (l.NameKey, $"light_source {l.Id}");

        foreach (ConnectionType c in _tables.TbConnectionType.DataList)
            yield return (c.NameKey, $"connection_type {c.Id}");

        foreach (NoiseProfile n in _tables.TbNoiseProfile.DataList)
            yield return (n.NameKey, $"noise_profile {n.Id}");

        foreach (GuardArchetype g in _tables.TbGuardArchetype.DataList)
            yield return (g.NameKey, $"guard_archetype {g.Id}");

        foreach (GoapGoal g in _tables.TbGoapGoal.DataList)
            yield return (g.NameKey, $"goap_goal {g.Id}");

        foreach (GoapAction a in _tables.TbGoapAction.DataList)
            yield return (a.NameKey, $"goap_action {a.Id}");

        foreach (AgentRole r in _tables.TbAgentRole.DataList)
            yield return (r.NameKey, $"agent_role {r.Id}");

        foreach (TacticalAction a in _tables.TbTacticalAction.DataList)
            yield return (a.NameKey, $"tactical_action {a.Id}");

        foreach (Throwable t in _tables.TbThrowable.DataList)
            yield return (t.NameKey, $"throwable {t.Id}");

        foreach (MeleeWeapon m in _tables.TbMeleeWeapon.DataList)
            yield return (m.NameKey, $"melee_weapon {m.Id}");

        foreach (CaptureSite c in _tables.TbCaptureSite.DataList)
            yield return (c.NameKey, $"capture_site {c.Id}");
        foreach (AgentClass c in _tables.TbAgentClass.DataList)
            yield return (c.NameKey, $"agent_class {c.Id}");

        foreach (RoomType r in _tables.TbRoomType.DataList)
            yield return (r.NameKey, $"room_type {r.Id}");

        foreach (Trait t in _tables.TbTrait.DataList)
        {
            yield return (t.NameKey, $"trait {t.Id}");
            yield return (t.DescKey, $"trait {t.Id}");
        }

        foreach (MissionType m in _tables.TbMissionType.DataList)
            yield return (m.NameKey, $"mission_type {m.Id}");

        foreach (NodeRoom n in _tables.TbNodeRoom.DataList)
            yield return (n.NameKey, $"node_room {n.Id}");

        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            yield return (e.NameKey, $"mission_event {e.Id}");
            yield return (e.NarrativeKey, $"mission_event {e.Id}");
        }

        foreach (Gadget g in _tables.TbGadget.DataList)
            yield return (g.NameKey, $"gadget {g.Id}");

        foreach (Item i in _tables.TbItem.DataList)
            yield return (i.NameKey, $"item {i.Id}");

        // Interactable kinds carry no name_key column — Core derives the key from the
        // enum (SimulationRules.NameKeyFor), because a column a designer can typo would
        // fail silently exactly where the label is needed. The keys are enumerated here
        // so a kind added to the enum without a matching row in both catalogs is caught.
        foreach (InteractableTypeRow type in _tables.TbInteractableType.DataList)
        {
            string key = Core.SimulationRules.NameKeyFor(Core.SimulationRules.ToCoreKind(type.Kind));
            yield return (key, $"interactable_type {type.Id}");
        }

        // Stage 4e. The objective, ability and resolve tables each carry a single
        // name_key column, so one row of enumeration each is enough — and enumerating
        // them is the only thing that makes "the debrief can name every resolve class"
        // a build-time failure rather than an empty label found by a player.
        foreach (ObjectiveRule o in _tables.TbObjectiveRule.DataList)
            yield return (o.NameKey, $"objective_rule {o.Id}");

        foreach (CommandPostAbility a in _tables.TbCommandPostAbility.DataList)
            yield return (a.NameKey, $"command_post_ability {a.Id}");

        foreach (ResolveRule r in _tables.TbResolveRule.DataList)
            yield return (r.NameKey, $"resolve_rule {r.Id}");
    }

    // ---- skills --------------------------------------------------------------

    /// <summary>
    /// Every skill a room trains must be a real, trainable skill.
    /// </summary>
    /// <remarks>
    /// A room whose <c>trains_skill</c> is <c>None</c> must also declare no training
    /// rate, otherwise it silently produces free exp for nobody.
    /// </remarks>
    private void ValidateSkillReferences(List<string> errors)
    {
        var valid = Enum.GetValues<TableSkillKind>().ToHashSet();

        foreach (RoomType room in _tables.TbRoomType.DataList)
        {
            if (!valid.Contains(room.TrainsSkill))
            {
                errors.Add(
                    $"room_type {room.Id}: trains_skill '{room.TrainsSkill}' is not a known skill");
                continue;
            }

            bool trains = room.TrainsSkill != TableSkillKind.None;

            if (trains && room.TrainRatePerTick <= 0)
            {
                errors.Add(
                    $"room_type {room.Id}: trains {room.TrainsSkill} but train_rate_per_tick " +
                    $"is {room.TrainRatePerTick}");
            }

            if (!trains && room.TrainRatePerTick != 0)
            {
                errors.Add(
                    $"room_type {room.Id}: trains_skill is None but train_rate_per_tick is " +
                    $"{room.TrainRatePerTick}");
            }
        }

        // The five core skills Core knows about must all be represented by some room,
        // otherwise that skill cannot be trained at all.
        var trainable = new HashSet<TableSkillKind>();
        foreach (RoomType room in _tables.TbRoomType.DataList)
        {
            if (room.TrainsSkill != TableSkillKind.None)
                trainable.Add(room.TrainsSkill);
        }

        foreach (TableSkillKind skill in valid)
        {
            if (skill == TableSkillKind.None)
                continue;

            if (!trainable.Contains(skill))
                errors.Add($"no room_type trains {skill}");
        }

        // mission_event.skill_checked must also be a real skill (None = pure Nerve/no check).
        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            if (!valid.Contains(e.SkillChecked))
                errors.Add($"mission_event {e.Id}: skill_checked '{e.SkillChecked}' is not a known skill");
        }

        // gadget effect_type strings must be ones Core understands (stage 3/4 effect code).
        var knownGadgetEffects = new HashSet<string>(StringComparer.Ordinal)
        {
            "InfiltrationBonus", "SocialBonus", "TechBonus", "CombatBonus", "NerveBonus",
            "AlarmReduction", "IntelBonus", "LootBonus", "RevealBonus", "HeatGainReduction",
            "StaminaRestore", "MentalRestore", "RecruitCostReduction",
        };

        foreach (Gadget gadget in _tables.TbGadget.DataList)
        {
            if (!knownGadgetEffects.Contains(gadget.EffectType))
                errors.Add($"gadget {gadget.Id}: unknown effect_type '{gadget.EffectType}'");
        }

        // trait effect_type strings likewise.
        foreach (Trait trait in _tables.TbTrait.DataList)
        {
            if (trait.EffectType.Length == 0)
                errors.Add($"trait {trait.Id}: effect_type is empty");
        }
    }

    // ---- mission tier coverage ----------------------------------------------

    /// <summary>
    /// Every mission tier must have at least three node rooms it can actually use.
    /// </summary>
    /// <remarks>
    /// The floor is three because a map needs an entry, an objective and an
    /// extraction; below that the generator cannot build a legal map and the failure
    /// surfaces in play rather than in a build.
    /// </remarks>
    private void ValidateNodeRoomCoveragePerTier(List<string> errors)
    {
        var tiers = _tables.TbMissionType.DataList
            .Select(m => m.Tier)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        foreach (int tier in tiers)
        {
            int usable = _tables.TbNodeRoom.DataList.Count(node =>
            {
                var allowed = LocalizationCatalog.ParseStringList(node.AllowedTiers);
                return allowed.Any(t => int.TryParse(t, out int tierValue) && tierValue == tier);
            });

            if (usable < 3)
            {
                errors.Add(
                    $"mission tier {tier} has only {usable} usable node_room(s); " +
                    "at least 3 are required to build a legal map");
            }
        }
    }

    // ---- id spaces -----------------------------------------------------------

    /// <summary>
    /// Every table owns a private id range, so a stray id cannot silently cross
    /// reference from one table into another.
    /// </summary>
    private void ValidateIdSpacesAreNonOverlapping(List<string> errors)
    {
        var spaces = new List<(string Table, IEnumerable<int> Ids)>
        {
            ("agent_class", _tables.TbAgentClass.DataList.Select(x => x.Id)),
            ("agent_name", _tables.TbAgentName.DataList.Select(x => x.Id)),
            ("trait", _tables.TbTrait.DataList.Select(x => x.Id)),
            ("room_type", _tables.TbRoomType.DataList.Select(x => x.Id)),
            ("room_upgrade", _tables.TbRoomUpgrade.DataList.Select(x => x.Id)),
            ("mission_type", _tables.TbMissionType.DataList.Select(x => x.Id)),
            ("node_room", _tables.TbNodeRoom.DataList.Select(x => x.Id)),
            ("mission_event", _tables.TbMissionEvent.DataList.Select(x => x.Id)),
            ("gadget", _tables.TbGadget.DataList.Select(x => x.Id)),
            ("item", _tables.TbItem.DataList.Select(x => x.Id)),
            ("contract_offer", _tables.TbContractOffer.DataList.Select(x => x.Id)),
            ("skill_cap", _tables.TbSkillCap.DataList.Select(x => x.Id)),
            ("recovery_rule", _tables.TbRecoveryRule.DataList.Select(x => x.Id)),
            ("training_rule", _tables.TbTrainingRule.DataList.Select(x => x.Id)),
            ("economy_rule", _tables.TbEconomyRule.DataList.Select(x => x.Id)),
            ("loan_tier", _tables.TbLoanTier.DataList.Select(x => x.Id)),
            ("morale_band", _tables.TbMoraleBand.DataList.Select(x => x.Id)),
            ("loyalty_drift", _tables.TbLoyaltyDrift.DataList.Select(x => x.Id)),
            ("loyalty_threshold", _tables.TbLoyaltyThreshold.DataList.Select(x => x.Id)),
            ("recruit_rule", _tables.TbRecruitRule.DataList.Select(x => x.Id)),
            ("counter_intel_rule", _tables.TbCounterIntelRule.DataList.Select(x => x.Id)),
            ("burnout_rule", _tables.TbBurnoutRule.DataList.Select(x => x.Id)),
            ("fog_rule", _tables.TbFogRule.DataList.Select(x => x.Id)),
            ("node_interior_rule", _tables.TbNodeInteriorRule.DataList.Select(x => x.Id)),
            ("site_gen_rule", _tables.TbSiteGenRule.DataList.Select(x => x.Id)),
            ("interactable_type", _tables.TbInteractableType.DataList.Select(x => x.Id)),
            ("node_interior_template", _tables.TbNodeInteriorTemplate.DataList.Select(x => x.Id)),
            ("site_template", _tables.TbSiteTemplate.DataList.Select(x => x.Id)),
            ("room_template", _tables.TbRoomTemplate.DataList.Select(x => x.Id)),
            ("lighting_profile", _tables.TbLightingProfile.DataList.Select(x => x.Id)),
            ("light_source", _tables.TbLightSource.DataList.Select(x => x.Id)),
            ("connection_type", _tables.TbConnectionType.DataList.Select(x => x.Id)),
            ("noise_profile", _tables.TbNoiseProfile.DataList.Select(x => x.Id)),
            ("guard_archetype", _tables.TbGuardArchetype.DataList.Select(x => x.Id)),
            ("goap_goal", _tables.TbGoapGoal.DataList.Select(x => x.Id)),
            ("goap_action", _tables.TbGoapAction.DataList.Select(x => x.Id)),
            ("agent_role", _tables.TbAgentRole.DataList.Select(x => x.Id)),
            ("tactical_action", _tables.TbTacticalAction.DataList.Select(x => x.Id)),
            ("throwable", _tables.TbThrowable.DataList.Select(x => x.Id)),
            ("melee_weapon", _tables.TbMeleeWeapon.DataList.Select(x => x.Id)),
            ("sleeper_op", _tables.TbSleeperOp.DataList.Select(x => x.Id)),
            ("capture_site", _tables.TbCaptureSite.DataList.Select(x => x.Id)),
            ("intel_rule", _tables.TbIntelRule.DataList.Select(x => x.Id)),
            ("squad_rule", _tables.TbSquadRule.DataList.Select(x => x.Id)),
            ("objective_rule", _tables.TbObjectiveRule.DataList.Select(x => x.Id)),
            ("command_post_ability", _tables.TbCommandPostAbility.DataList.Select(x => x.Id)),
            ("resolve_rule", _tables.TbResolveRule.DataList.Select(x => x.Id)),
        };

        var seen = new Dictionary<int, string>();

        foreach ((string table, IEnumerable<int> ids) in spaces)
        {
            foreach (int id in ids)
            {
                if (seen.TryGetValue(id, out string? owner))
                {
                    if (!string.Equals(owner, table, StringComparison.Ordinal))
                    {
                        errors.Add(
                            $"id {id} is used by both {owner} and {table}; " +
                            "id spaces must not overlap");
                    }
                }
                else
                {
                    seen[id] = table;
                }
            }
        }
    }

    // ---- numeric sanity ------------------------------------------------------

    private void ValidateNumericRanges(List<string> errors)
    {
        foreach (SkillCurve curve in _tables.TbSkillCurve.DataList)
        {
            if (curve.Level < 1)
                errors.Add($"skill_curve level {curve.Level}: must be >= 1");

            if (curve.ExpRequired <= 0)
                errors.Add($"skill_curve level {curve.Level}: exp_required must be > 0");
        }

        // Levels must run 1..N with no gaps: Core indexes this table by level.
        var levels = _tables.TbSkillCurve.DataList.Select(c => c.Level).OrderBy(l => l).ToList();
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] != i + 1)
            {
                errors.Add($"skill_curve: levels must be contiguous from 1; expected {i + 1}, found {levels[i]}");
                break;
            }
        }

        foreach (RoomType room in _tables.TbRoomType.DataList)
        {
            if (room.BuildCost <= 0)
                errors.Add($"room_type {room.Id}: build_cost must be > 0");

            if (room.Width <= 0)
                errors.Add($"room_type {room.Id}: width must be > 0");

            if (room.MaxLevel < 1)
                errors.Add($"room_type {room.Id}: max_level must be >= 1");

            if (room.WorkerSlots < 0)
                errors.Add($"room_type {room.Id}: worker_slots must be >= 0");

            if (room.MinDepth < 0)
                errors.Add($"room_type {room.Id}: min_depth must be >= 0");

            if (room.RequiredAct < 1)
                errors.Add($"room_type {room.Id}: required_act must be >= 1");

            if (room.MergeGroup.Length == 0)
                errors.Add($"room_type {room.Id}: merge_group is empty, so merging can never match");
        }

        foreach (MissionType m in _tables.TbMissionType.DataList)
        {
            if (m.NodeCountMin > m.NodeCountMax)
                errors.Add($"mission_type {m.Id}: node_count_min exceeds node_count_max");

            if (m.NodeCountMin <= 0)
                errors.Add($"mission_type {m.Id}: node_count_min must be > 0");

            if (m.RequiredAgentsMin > m.RequiredAgentsMax)
                errors.Add($"mission_type {m.Id}: required_agents_min exceeds required_agents_max");

            if (m.RequiredAgentsMin < 1)
                errors.Add($"mission_type {m.Id}: required_agents_min must be >= 1");

            if (m.BaseRewardFunds < 0 || m.BaseRewardIntel < 0)
                errors.Add($"mission_type {m.Id}: base rewards must be >= 0");

            if (m.DurationTicksPerNode <= 0)
                errors.Add($"mission_type {m.Id}: duration_ticks_per_node must be > 0");
        }

        foreach (NodeRoom node in _tables.TbNodeRoom.DataList)
        {
            if (node.BaseSecurity < 0 || node.BaseSecurity > 100)
                errors.Add($"node_room {node.Id}: base_security must be 0-100, was {node.BaseSecurity}");

            var tiers = LocalizationCatalog.ParseStringList(node.AllowedTiers);
            if (tiers.Count == 0)
                errors.Add($"node_room {node.Id}: allowed_tiers is empty, so the node is unreachable");

            if (LocalizationCatalog.ParseStringList(node.Tags).Count == 0)
                errors.Add($"node_room {node.Id}: tags is empty, so no event can ever match it");
        }

        foreach (MissionEvent e in _tables.TbMissionEvent.DataList)
        {
            if (e.AlarmOnFailure < e.AlarmOnPartial || e.AlarmOnPartial < e.AlarmOnSuccess)
            {
                errors.Add(
                    $"mission_event {e.Id}: alarm must be non-decreasing " +
                    "(success <= partial <= failure)");
            }

            if (e.AlarmOnFailure < 0 || e.AlarmOnFailure > 100)
                errors.Add($"mission_event {e.Id}: alarm_on_failure must be 0-100");

            if (e.PhysicalCost < 0 || e.MentalCost < 0)
                errors.Add($"mission_event {e.Id}: stamina costs must be >= 0");
        }

        foreach (Item item in _tables.TbItem.DataList)
        {
            if (item.SellValue < 0)
                errors.Add($"item {item.Id}: sell_value must be >= 0");

            if (item.StackMax < 1)
                errors.Add($"item {item.Id}: stack_max must be >= 1");
        }

        foreach (Gadget gadget in _tables.TbGadget.DataList)
        {
            if (gadget.CraftCost <= 0)
                errors.Add($"gadget {gadget.Id}: craft_cost must be > 0");

            if (gadget.Uses < 1)
                errors.Add($"gadget {gadget.Id}: uses must be >= 1");

            if (gadget.RequiredRoomLevel < 1)
                errors.Add($"gadget {gadget.Id}: required_room_level must be >= 1");
        }

        // Heat tiers must be strictly ascending and start at or below zero so that a
        // fresh agency resolves to the first tier.
        var heatTiers = _tables.TbHeatTier.DataList.OrderBy(t => t.Threshold).ToList();
        for (int i = 1; i < heatTiers.Count; i++)
        {
            if (heatTiers[i].Threshold <= heatTiers[i - 1].Threshold)
            {
                errors.Add(
                    $"heat_tier: thresholds must strictly ascend; {heatTiers[i].Threshold} " +
                    $"does not exceed {heatTiers[i - 1].Threshold}");
            }
        }

        if (heatTiers.Count > 0 && heatTiers[0].Threshold > 0)
        {
            errors.Add(
                $"heat_tier: the lowest threshold is {heatTiers[0].Threshold}; " +
                "a new agency at heat 0 must resolve to a tier");
        }
    }
}
