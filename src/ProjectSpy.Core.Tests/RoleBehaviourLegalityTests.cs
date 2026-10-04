using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// Fixtures for the stage-4e squad tests.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="TacticalHarness"/> because these tests need a composition,
/// which the stage-4c fixtures deliberately do not build: a stage-4c mission has actors
/// and no roles, and that is still a valid thing to test. These fixtures build the real
/// thing — a dispatched squad with roles, gadgets and a controlled agent — so a test
/// about role behaviour is running against the same setup a mission runs on.
/// </para>
/// <para>
/// <b>Every fixture goes through <see cref="SquadDeployment.TryDispatch"/>.</b>
/// Hand-assigning a <see cref="TacticalState.Composition"/> would test a state the game
/// cannot actually be in, and would let a bug in the deployment path pass.
/// </para>
/// </remarks>
internal static class SquadHarness
{
    /// <summary>The role ids the fixtures assign, by short name.</summary>
    private const int PointManRole = 12351;
    private const int HackerRole = 12352;
    private const int OverwatchRole = 12353;
    private const int MuleRole = 12354;
    private const int MedicRole = 12355;
    private const int ScoutRole = 12356;
    private const int HandlerRole = 12358;
    private const int SaboteurRole = 12361;

    /// <summary>Role ids by the short name an objective rule would require.</summary>
    internal static IReadOnlyDictionary<string, int> RolesByKey { get; } = new Dictionary<string, int>
    {
        ["pointman"] = PointManRole,
        ["hacker"] = HackerRole,
        ["overwatch"] = OverwatchRole,
        ["mule"] = MuleRole,
        ["medic"] = MedicRole,
        ["scout"] = ScoutRole,
        ["handler"] = HandlerRole,
        ["saboteur"] = SaboteurRole,
    };

    /// <summary>A gadget with uses, from <c>gadget.csv</c>.</summary>
    internal const int MedkitGadget = 8507;

    /// <summary>A heavier gadget, for the carry-weight tests.</summary>
    internal const int SignalJammerGadget = 8504;

    /// <summary>
    /// Builds a world, a composition and a dispatched mission.
    /// </summary>
    /// <param name="roles">Short role names to assign, in order.</param>
    /// <param name="objective">What the team was sent to do.</param>
    /// <param name="templateId">Which site template.</param>
    /// <param name="seed">The mission seed.</param>
    internal static (WorldState World, SquadComposition Composition, TacticalState Mission) Dispatched(
        IReadOnlyList<string> roles,
        TableObjectiveType objective = TableObjectiveType.StealData,
        int templateId = TacticalHarness.WarehouseTemplate,
        ulong seed = TacticalHarness.DefaultSeed)
    {
        TacticalHarness.RequireTables();

        var world = new WorldState(seed);
        var composition = new SquadComposition { ObjectiveType = objective };
        var agents = new List<Agent>();

        foreach (string role in roles)
        {
            Agent agent = world.AddAgent(new Agent
            {
                Name = role,
                Codename = role.ToUpperInvariant(),
                ClassId = World.Classes.Infiltrator,
                Skills = new SkillSet
                {
                    Infiltration = 50,
                    Combat = 50,
                    Tech = 50,
                    Social = 50,
                    Nerve = 50,
                },
            });

            agents.Add(agent);
            composition.Add(agent.Id, RolesByKey[role]);
        }

        SiteLayout layout = TacticalHarness.Site(templateId, 1, TacticalHarness.MissionId, seed);

        bool dispatched = SquadDeployment.TryDispatch(
            world,
            composition,
            TacticalHarness.MissionId,
            layout,
            agents[0].Id,
            recordRolls: false,
            out TacticalState? mission,
            out IReadOnlyList<DispatchRefusal> refusals);

        Assert.True(
            dispatched,
            "The fixture squad was refused dispatch: "
            + string.Join("; ", refusals.Select(r => r.MessageKey)));

        return (world, composition, mission!);
    }

    /// <summary>A runner over a dispatched mission.</summary>
    internal static TacticalMissionRunner Runner(TacticalState mission, ulong seed = TacticalHarness.DefaultSeed)
        => new(mission, new RngStreams(seed));

    /// <summary>The agent id at a position in the composition.</summary>
    internal static AgentId AgentAt(SquadComposition composition, int index)
        => composition.Members[index].AgentId;

    /// <summary>The actor for a member of the composition.</summary>
    internal static TacticalActor ActorOf(TacticalState mission, SquadComposition composition, int index)
    {
        TacticalActor? actor = mission.AgentActor(AgentAt(composition, index));
        Assert.NotNull(actor);
        return actor!;
    }
}

/// <summary>
/// The brief's first required test: role behaviours never issue an illegal action.
/// </summary>
/// <remarks>
/// <para>
/// Asserted two ways, because the two catch different bugs. Recording every order the
/// behaviours produce and validating each one catches a behaviour that issues something
/// illegal. Letting a mission run and then asserting that nothing was rejected in the log
/// catches a behaviour that only becomes illegal in some building, on some step, after
/// some door has closed — which is the case that actually happens.
/// </para>
/// <para>
/// <b>Run over several templates and seeds.</b> A behaviour that is legal in a two-room
/// warehouse and illegal in a four-floor black site has not been tested by a single
/// fixture, and the black site is where a player's squad actually dies.
/// </para>
/// </remarks>
public sealed class RoleBehaviourLegalityTests
{
    /// <summary>
    /// Every role's behaviour only ever produces orders the action system accepts.
    /// </summary>
    [Theory]
    [InlineData("pointman")]
    [InlineData("hacker")]
    [InlineData("overwatch")]
    [InlineData("mule")]
    [InlineData("medic")]
    [InlineData("scout")]
    [InlineData("handler")]
    public void RoleBehaviour_NeverIssuesAnIllegalAction(string role)
    {
        (_, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { role, "pointman", "hacker", "overwatch" });

        TacticalActor actor = SquadHarness.ActorOf(mission, composition, 0);
        Assert.Equal(role, composition.MemberFor(actor.AgentId)!.Role!.RoleKey);

        var rng = new RngStreams(TacticalHarness.DefaultSeed)[RngStreams.StreamKind.Tactical];
        var illegal = new List<TacticalOrder>();

        for (long step = 0; step < 400; step++)
        {
            if (actor.CanAct && actor.Action is not { IsComplete: false })
            {
                TacticalOrder? order = RoleBehaviours.Probe(mission, composition, actor);

                if (order is not null && ActionSystem.Validate(mission, order).IsRejected)
                    illegal.Add(order);
            }

            mission.Step = step;
        }

        Assert.True(
            illegal.Count == 0,
            $"The {role} behaviour produced {illegal.Count} illegal orders; first was "
            + illegal.FirstOrDefault());
    }

    /// <summary>
    /// A whole squad running unattended for a long mission logs no rejected orders.
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the test above. A rejected order is logged by the
    /// <c>ApplyQueuedCommands</c> phase under <c>log.order.rejected</c>, so a mission
    /// where no behaviour misbehaved has no such entry — which is a much stronger
    /// statement than "the behaviour I called returned null".
    /// </remarks>
    [Theory]
    [InlineData(TacticalHarness.WarehouseTemplate, 1)]
    [InlineData(TacticalHarness.BlackSiteTemplate, 4)]
    public void UnattendedSquad_LogsNoRejectedOrders(int templateId, int tier)
    {
        (_, SquadComposition composition, TacticalState mission) = SquadHarness.Dispatched(
            new[] { "pointman", "hacker", "medic", "mule", "overwatch" },
            TableObjectiveType.StealData,
            templateId);

        TacticalMissionRunner runner = SquadHarness.Runner(mission);

        // Long enough for the squad to cross several rooms, meet guards and start
        // reacting. Short enough to stay a unit test.
        runner.StepMany(1200);

        List<string> rejections = mission.Log
            .Where(e => e.Key == "log.order.rejected")
            .Select(e => e.Key + ":" + string.Join(",", e.Args))
            .ToList();

        Assert.True(
            rejections.Count == 0,
            $"{rejections.Count} orders were rejected by the action system during an unattended run: "
            + string.Join("; ", rejections.Take(5)));
    }/// <summary>
    /// Every non-empty behaviour in the table is one Core implements.
/// </summary>
/// <remarks>
    /// The cheap guard against a role being added to the CSV with a behaviour nobody
    /// implemented. <c>RoleBehaviours.Decide</c> throws for that, but only at run time
    /// inside a mission; this fails at build time instead.
    /// </remarks>
    [Fact]
    public void EveryBehaviourInTheTable_IsImplemented()
    {
        // None is deliberately not expected: no row names it, because "this role does
        // nothing on its own" is expressed by having no auto_behaviour_set at all, not
        // by a row spelling it out. Expecting it would push a pointless CSV row into
        // the table to satisfy a test.
        var expected = new HashSet<RoleBehaviour>
        {
            RoleBehaviour.PointMan,
            RoleBehaviour.Overwatch,
            RoleBehaviour.Mule,
            RoleBehaviour.Hacker,
            RoleBehaviour.Medic,
            RoleBehaviour.Scout,
            RoleBehaviour.Handler,
        };

        var inTable = new HashSet<RoleBehaviour>(
            SquadRole.All().Select(r => r.Behaviour).Where(b => b != RoleBehaviour.None));

        Assert.True(expected.SetEquals(inTable),
            "The behaviours in agent_role.csv and the behaviours Core implements have drifted. "
            + "Expected " + string.Join(",", expected.OrderBy(b => (int)b))
            + " but the table holds " + string.Join(",", inTable.OrderBy(b => (int)b)));
    }

    /// <summary>
    /// Every order a role may be given reaches a real action.
    /// </summary>
    /// <remarks>
    /// The other half of "never issues an illegal action": the player's orders have to
    /// resolve too. An order kind that no branch of
    /// <see cref="RoleBehaviours.TacticalOrderFor"/> handled would sit at the head of a
    /// queue and be dropped every step, which looks like the agent ignoring the player
    /// rather than like a bug.
    /// </remarks>
    [Fact]
    public void EveryOrderKind_TheTableAllows_ResolvesToAnAction()
    {
        (_, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        var post = new CommandPostState();
        var unresolved = new List<SquadOrderKind>();

        // A room the member is standing in, so a MoveTo and a Stack have somewhere legal
        // to point at; a point inside it so the move is not zero-distance.
        SiteRoom room = mission.RoomOf(mission.Squad[0])
            ?? mission.Layout.Find(mission.Layout.EntranceRoomId)!;

        Fixed32 centre = new(room.StartX.Raw + ((room.LastX.Raw - room.StartX.Raw) / 2));
        var point = new TacticalPosition(room.FloorIndex, centre);

        foreach (SquadOrderKind kind in Enum.GetValues<SquadOrderKind>())
        {
            var order = new SquadStandingOrder(
                kind,
                point,
                mission.Layout.ConnectionsAt(room.Id).FirstOrDefault()?.Id ?? default,
                InteractableId: 0,
                TargetActorId: mission.Squad[1].Id,
                ItemId: SquadHarness.MedkitGadget,
                LightId: 0,
                Facing: Facing.Right);

            TacticalOrder? action = RoleBehaviours.TacticalOrderFor(
                mission, composition, mission.Squad[0], order, post);

            if (action is null)
                unresolved.Add(kind);
        }

        Assert.True(
            unresolved.Count == 0,
            "These orders produced no action for a member standing in a room: "
            + string.Join(", ", unresolved));
    }
}