using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// A world, a generated site, a squad dispatched into it, and nothing played yet.
/// </summary>
/// <remarks>
/// <para>
/// Built once and played many times. The batch harness needs this because a policy
/// comparison is only fair if every policy was handed the <em>same</em> building with the
/// same guards standing in the same places — four separately-generated sites would differ
/// by far more than the policies do, and the report's columns would be measuring luck.
/// </para>
/// <para>
/// Shared with <c>play</c> for the same reason in the other direction: the mission a
/// human plays in the terminal has to be built by the same code as the one the batch
/// numbers come from, or the balance report is describing a game nobody is playing.
/// </para>
/// </remarks>
public sealed class MissionFixture
{
    public required ulong Seed { get; init; }

    public required int TemplateId { get; init; }

    public required int Tier { get; init; }

    public required TableObjectiveType Objective { get; init; }

    public required WorldState World { get; init; }

    public required SquadComposition Composition { get; init; }

    public required TacticalState Mission { get; init; }

    /// <summary>Shots fired, counted by watching the action log rather than the policy.</summary>
    public required int ShootActions { get; init; }

    /// <summary>
    /// The id of the Infiltrator class, from <c>agent_class.csv</c>.
    /// </summary>
    /// <remarks>
    /// Resolved from the table on first use rather than written as a literal, because a
    /// literal here would be a balance number living in a harness: renumber the table and
    /// every mission in the report would silently be run by agents of a class nobody
    /// chose. The lookup is cached because it is on the hot path of a sweep that builds
    /// thousands of fixtures.
    /// </remarks>
    private static int InfiltratorClassId { get; } = ResolveInfiltratorClassId();

    private static int ResolveInfiltratorClassId()
    {
        foreach (ProjectSpy.Tables.AgentClass row in SimulationRules.AllAgentClasses())
        {
            if (string.Equals(row.NameKey, "class.infiltrator", StringComparison.Ordinal))
                return row.Id;
        }

        throw new InvalidOperationException(
            "agent_class.csv has no row named class.infiltrator, so there is no squad to field.");
    }

    /// <summary>
    /// The squad to field for an objective.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from <c>objective_rule.required_roles</c> rather than fixed, because the six
    /// objectives each name a different specialist — a Hacker for StealData, a Saboteur
    /// for PlantBug, a Scout for Recon — and a single hard-coded roster cannot cover
    /// them all. A sweep that used one squad for every objective would be unable to
    /// dispatch most of its own runs.
    /// </para>
    /// <para>
    /// The three non-specialists are the same in every case: a Pointman to open doors and
    /// take the hit, a Medic for the ones who get hit, and a Handler so the sites that
    /// grow a forward post actually staff it. Which of those four is the specialist
    /// depends on the contract, and that is exactly the decision a player makes at the
    /// dispatch screen — the harness just always makes the same one.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RolesFor(TableObjectiveType objective)
    {
        IReadOnlyList<string> required = new SquadComposition { ObjectiveType = objective }
            .RequiredRoles();

        var roles = new List<string> { "pointman", "medic", "handler" };

        foreach (string role in required)
        {
            if (!roles.Contains(role, StringComparer.Ordinal))
                roles.Add(role);
        }

        return roles;
    }

    /// <summary>
    /// Builds a fixture, or throws when the composition the caller asked for is not one
    /// the dispatch screen would accept.
    /// </summary>
    /// <remarks>
    /// It throws rather than returning null because every caller of this in the harness is
    /// a test loop or a report: a silently-skipped mission would show up as a denominator
    /// quietly one smaller, and a percentage that is wrong because a mission failed to
    /// start is worse than a crash.
    /// </remarks>
    public static MissionFixture Create(
        ulong seed,
        int templateId,
        int tier,
        TableObjectiveType objective,
        int missionId,
        IReadOnlyList<string> roles)
    {
        if (!SimulationRules.AreTablesLoaded)
            throw new InvalidOperationException(
                "Tables did not load, so there is nothing to simulate (knowledge.md rule 3). "
                + "Run tools/gen.ps1.");

        var world = new WorldState(seed);
        var composition = new SquadComposition { ObjectiveType = objective };
        var roster = new System.Collections.Generic.List<Agent>();

        foreach (string role in roles)
        {
            Agent agent = world.AddAgent(new Agent
            {
                Name = role,
                Codename = role.ToUpperInvariant(),
                ClassId = InfiltratorClassId,
                Skills = new SkillSet
                {
                    Infiltration = 50,
                    Combat = 50,
                    Tech = 50,
                    Social = 50,
                    Nerve = 50,
                },
            });

            roster.Add(agent);
            composition.Add(agent.Id, RoleIdFor(role));
        }

        ulong mapSeed = SiteGenerator.DeriveMapSeed(seed, missionId);
        SiteLayout layout = SiteGenerator.Generate(templateId, tier, missionId, seed, mapSeed);

        if (!layout.Validate(out string problem))
            throw new InvalidOperationException($"Generated site {templateId} is unplayable: {problem}");

        bool dispatched = SquadDeployment.TryDispatch(
            world, composition, missionId, layout, roster[0].Id, recordRolls: false,
            out TacticalState? mission, out IReadOnlyList<DispatchRefusal> refusals);

        if (!dispatched || mission is null)
        {
            throw new InvalidOperationException(
                "The harness squad was refused dispatch: "
                + string.Join("; ", System.Linq.Enumerable.Select(refusals, r => r.MessageKey)));
        }

        return new MissionFixture
        {
            Seed = seed,
            TemplateId = templateId,
            Tier = tier,
            Objective = objective,
            World = world,
            Composition = composition,
            Mission = mission,
            ShootActions = 0,
        };
    }

    /// <summary>
    /// The <c>agent_role</c> row id for a role's short name.
    /// </summary>
    /// <remarks>
    /// Looked up through the table rather than held as a constant, so adding a role to
    /// <c>agent_role.csv</c> makes it available here without touching the harness, and so
    /// a typo fails loudly instead of quietly producing a squad of role-zero members that
    /// the dispatch validator would refuse for an unrelated reason.
    /// </remarks>
    public static int RoleIdFor(string role)
    {
        foreach (ProjectSpy.Tables.AgentRole row in SimulationRules.AllAgentRoles())
        {
            if (!string.Equals(row.NameKey, $"role.{role}", StringComparison.Ordinal))
                continue;

            return row.Id;
        }

        throw new InvalidOperationException(
            $"No agent_role row is named role.{role}; the harness cannot field a squad with one.");
    }
}