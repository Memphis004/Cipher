using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using SupportAbility = ProjectSpy.Tables.SupportAbility;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The brief's sixth requirement, and the third one: mid-mission abort, the resolve
/// classes, and the forward command post.
/// </summary>
/// <remarks>
/// <para>
/// <b>Abort is a button, not a teleport.</b> The brief insists aborting "always takes
/// real steps to reach extraction", and this is the test that holds the line: it asserts
/// the squad moves afterwards, not merely that a flag was set. An abort that ended the
/// mission on the spot would turn every firefight into a decision to reset.
/// </para>
/// <para>
/// <b>And aborting is worth considering.</b> The brief wants it to "feel legitimate,
/// sometimes correct". In a rule system that does not mean a class that pays — it means
/// a class that is <em>not</em> a failure to return. Walking out with nothing is a
/// different thing from being dragged out, and the resolve table has to say so.
/// </para>
/// </remarks>
public sealed class SquadAbortAndCommandPostTests
{
    // ---- abort ---------------------------------------------------------------

    /// <summary>
    /// Aborting sends the squad walking and does not end the mission.
    /// </summary>
    /// <remarks>
    /// The fixture walks the team deep into the building first, because the whole claim
    /// is about the walk back: a squad aborting from the doorway it came in through
    /// would satisfy "nobody is still inside" without ever costing a step.
    /// </remarks>
    [Fact]
    public void Abort_SendsTheSquadWalking_AndDoesNotEndTheMission()
    {
        (WorldState world, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(
                new[] { "pointman", "hacker", "medic", "mule" },
                TableObjectiveType.StealData,
                TacticalHarness.BlackSiteTemplate);

        DeployToTheObjectiveRoom(mission);

        var session = new GameSession(world);
        session.EnterTacticalMode(mission);

        var rng = new RngStreams(TacticalHarness.DefaultSeed)[RngStreams.StreamKind.Tactical];
        CommandPostState post = mission.CommandPost;

        Assert.True(
            mission.Control.AbortAll(mission, composition, post, rng),
            "The abort was refused on a mission that had never been aborted.");

        Assert.True(mission.AbortCalled, "The abort was accepted but never recorded.");
        Assert.False(mission.IsOver, "Aborting ended the mission outright instead of starting the walk out.");

        // Every member but the one under the player's hand is now headed for the exit.
        foreach (SquadMemberOrders orders in mission.Control.Members)
        {
            if (mission.Control.Controlled == orders.AgentId)
                continue;

            Assert.Equal(SquadOrderKind.Abort, orders.Current?.Kind);
        }

        // Still deep in the building: the abort did not move anybody.
        SiteRoomId entrance = mission.Layout.EntranceRoomId;

        foreach (TacticalActor actor in mission.Squad)
        {
            if (mission.Layout.ObjectiveRoomId == entrance)
                continue;

            Assert.False(
                (mission.RoomOf(actor)?.Id ?? SiteRoomId.None) == entrance,
                "A squad member was standing in the exit room before taking a single step.");
        }

        long before = mission.Step;

        for (int i = 0; i < 120 && !mission.IsOver; i++)
            session.AdvanceTacticalStep();

        Assert.True(mission.Step > before, "The mission stopped stepping after the abort.");
        Assert.False(
            mission.IsOver,
            "The squad covered the distance back to the exit in 120 steps on a four-floor site.");
    }

    /// <summary>
    /// Aborting clears whatever else the squad was told to do.
    /// </summary>
    /// <remarks>
    /// Otherwise an abort queues behind a standing "wait here", and a player who hits
    /// the button mid-firefight watches four people stand still — which reads as the
    /// button not working rather than as a queue they forgot about.
    /// </remarks>
    [Fact]
    public void Abort_ClearsOrdersThatWereAlreadyQueued()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        AgentId hacker = SquadHarness.AgentAt(composition, 1);

        mission.Control.Issue(
            composition,
            hacker,
            new SquadStandingOrder(SquadOrderKind.Hold, Facing: Facing.Left));

        Assert.Equal(SquadOrderKind.Hold, mission.Control.For(hacker)!.Current!.Kind);

        var rng = new RngStreams(TacticalHarness.DefaultSeed)[RngStreams.StreamKind.Tactical];
        mission.Control.AbortAll(mission, composition, mission.CommandPost, rng);

        Assert.Equal(
            SquadOrderKind.Abort,
            mission.Control.For(hacker)!.Current!.Kind);
    }

    /// <summary>
    /// Aborting twice inside the cooldown is refused.
    /// </summary>
    /// <remarks>
    /// The reason an abort can be a decision at all. A button with no cost would be
    /// pressed on every noise, and the firefight would stop being a thing you survive.
    /// </remarks>
    [Fact]
    public void Abort_IsOnCooldown()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        var rng = new RngStreams(TacticalHarness.DefaultSeed)[RngStreams.StreamKind.Tactical];

        Assert.True(mission.Control.AbortAll(mission, composition, mission.CommandPost, rng));

        mission.Step += 3;

        Assert.False(
            mission.Control.AbortAll(mission, composition, mission.CommandPost, rng),
            "The abort was accepted again three steps after the first one.");

        mission.Step += SimulationRules.Squad("squad_abort_order_cooldown_steps", 100);

        Assert.True(
            mission.Control.AbortAll(mission, composition, mission.CommandPost, rng),
            "The abort was still on cooldown a whole cooldown later.");
    }

    /// <summary>
    /// Aborting with the objective incomplete classifies as Aborted, and the report says
    /// so.
    /// </summary>
    [Fact]
    public void AnAbortedMission_ResolvesAsAborted()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        // The objective is untouched and the team walks out: the canonical "we got out
        // and we got nothing".
        mission.ObjectiveOutcome.WorkSteps = 20;
        mission.Outcome = TacticalOutcome.Aborted;

        MissionReport report = MissionReportBuilder.Build(
            mission, composition, mission.ObjectiveOutcome, mission.CommandPost);

        Assert.Equal(ResolveClass.Aborted, report.Class);
        Assert.False(report.ObjectiveComplete);
        Assert.True(report.Aborted);
        Assert.Contains(report.Summary, line => line.Key == "report.summary.aborted");
    }

    /// <summary>
    /// An abort is not a failure to return: the team is home, and the debrief says so.
    /// </summary>
    /// <remarks>
    /// The "sometimes correct" half of the brief. A class that reported the team as lost
    /// would make aborting strictly worse than trying, which is the opposite of what the
    /// brief asks for — the player should be able to judge "this is too hot, I am
    /// leaving" as a real option rather than a surrender.
    /// </remarks>
    [Fact]
    public void AnAbortedMissionStillBringsTheTeamHome()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        mission.ObjectiveOutcome.WorkSteps = 20;
        mission.Outcome = TacticalOutcome.Aborted;
        mission.HeatGained = 40;

        MissionResolution resolution =
            ResolveSystem.Classify(mission, mission.ObjectiveOutcome, aborted: true);

        Assert.Equal(ResolveClass.Aborted, resolution.Class);
        Assert.True(resolution.TeamExtracted, "An abort reported the team as lost when nobody was hurt.");
        Assert.Equal(0, resolution.Lost);

        // Walking out is not free: the row charges heat for it, at a premium over a
        // clean run. That is the cost the player's decision is actually weighing.
        Assert.True(resolution.Heat > mission.HeatGained,
            "Aborting cost no more heat than doing nothing, so there is no decision to weigh.");

        ProjectSpy.Tables.ResolveRule rule =
            SimulationRules.ResolveRuleFor(ResolveSystem.Table(ResolveClass.Aborted))!;

        Assert.True(rule.HeatPercent > 100, "The abort row does not charge a heat premium.");

        // ...but you keep half of whatever you were carrying out, which is what makes
        // "we leave now" a judgement rather than a reset.
        Assert.True(rule.LootPercent > 0, "Aborting forfeits everything already carried out.");
    }

    /// <summary>
    /// A mission that is aborted is never a success class, whatever the objective says.
    /// </summary>
    [Fact]
    public void AnAbortedMissionIsNeverClassifiedAsASuccess()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        // The objective was, against all odds, completed — and the player still left.
        mission.ObjectiveOutcome.IsComplete = true;
        mission.Outcome = TacticalOutcome.Aborted;

        MissionResolution resolution =
            ResolveSystem.Classify(mission, mission.ObjectiveOutcome, aborted: true);

        Assert.Equal(ResolveClass.Aborted, resolution.Class);
    }

    /// <summary>
    /// A team that did not come home is a Disaster however well the objective went.
    /// </summary>
    /// <remarks>
    /// Asserted here because the abort tests are what establish the other half of the
    /// classification: if a walkout is legitimate, then not walking out has to be worse
    /// than it, or "legitimate" just means "the game never says no".
    /// </remarks>
    [Fact]
    public void ACasualtyOutranksASuccessfulObjective()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        mission.ObjectiveOutcome.IsComplete = true;
        SquadHarness.ActorOf(mission, composition, 2).Condition = ActorCondition.Dead;

        MissionResolution resolution =
            ResolveSystem.Classify(mission, mission.ObjectiveOutcome, aborted: false);

        Assert.Equal(ResolveClass.Disaster, resolution.Class);
        Assert.False(resolution.TeamExtracted);
    }

    // ---- command post --------------------------------------------------------

    /// <summary>
    /// Every ability has a cooldown in steps, and using one starts it.
    /// </summary>
    [Fact]
    public void SupportAbilities_RunOnStepCooldowns()
    {
        (WorldState _, TacticalState mission) = PostMission(out _);

        var post = mission.CommandPost;
        int roomId = mission.Layout.ForwardPostRoomId.Value;
        int doorId = FirstConnectionId(mission);

        foreach (SupportAbility ability in Enum.GetValues<SupportAbility>())
        {
            ProjectSpy.Tables.CommandPostAbility row =
                SimulationRules.CommandPostAbilityFor(ability)!;

            if (row.CooldownSteps <= 0)
                continue;

            // The unlock is the one ability that has to be aimed at a door the team
            // already paid for, so the fixture pays for it before each attempt.
            post.HackedDoors.Add(doorId);

            bool used = CommandPostSystem.Use(mission, post, ability, roomId, doorId);

            Assert.True(used, $"The {ability} ability could not be used with a post staffed and a target named.");

            Assert.Equal(row.CooldownSteps, post.CooldownFor(ability));

            Assert.False(
                CommandPostSystem.Use(mission, post, ability, roomId, doorId),
                $"The {ability} ability was usable again on the step it was used.");
        }
    }

    /// <summary>
    /// A remote unlock only opens a door somebody actually hacked.
    /// </summary>
    /// <remarks>
    /// The rule the row declares as <c>requires_hacked_door</c>. Opening any door on the
    /// building would make the hack the team did elsewhere pointless, and the ability is
    /// meant to be the reward for it.
    /// </remarks>
    [Fact]
    public void RemoteUnlock_OnlyOpensAHackedDoor()
    {
        (WorldState _, TacticalState mission) = PostMission(out _);

        var post = mission.CommandPost;
        int someDoor = FirstConnectionId(mission);

        Assert.False(
            CommandPostSystem.Use(mission, post, SupportAbility.RemoteDoorUnlock, 0, someDoor),
            "A door was unlocked that nobody had hacked.");

        post.HackedDoors.Add(someDoor);
        mission.Doors[new SiteConnectionId(someDoor)] = ConnectionState.Locked;

        Assert.True(
            CommandPostSystem.Use(mission, post, SupportAbility.RemoteDoorUnlock, 0, someDoor),
            "A hacked door could not be unlocked.");

        Assert.Equal(
            ConnectionState.Open,
            mission.StateOf(new SiteConnectionId(someDoor)));
    }

    /// <summary>
    /// The post falls when the alarm is high and a responder reaches it — and not before.
    /// </summary>
    /// <remarks>
    /// Both halves of the brief's condition, asserted separately, because the failure
    /// this catches is a post that falls on either one alone: a site at lockdown with the
    /// post untouched, or a guard wandering through the post's corridor while the site is
    /// calm.
    /// </remarks>
    [Fact]
    public void ThePost_FallsOnAlarmAndAResponderTogether()
    {
        (WorldState _, TacticalState mission) = PostMission(out _);

        var post = mission.CommandPost;
        SiteRoom postRoom = mission.Layout.Find(mission.Layout.ForwardPostRoomId)!;

        // The post is manned by the site's own guards, so "nobody has reached it" has to
        // be arranged rather than assumed. Clear it first, then test each half of the
        // condition with exactly one half present.
        ClearPost(mission);

        // A responder reaches it, but the site is calm.
        PutGuardIn(mission, postRoom);
        Assert.False(
            CommandPostSystem.Advance(mission, post),
            "The post fell in a quiet building because a guard walked past it.");

        // The site is loud, but nobody is standing in the post.
        ClearPost(mission);
        mission.Alarm.Raise(AlarmState.Max);

        Assert.False(
            CommandPostSystem.Advance(mission, post),
            "The post fell on the alarm alone, with nobody standing in it.");

        PutGuardIn(mission, postRoom);
        Assert.True(
            CommandPostSystem.Advance(mission, post),
            "A responder reached the post at a high alarm and the post held.");
    }

    /// <summary>
    /// A compromised post takes its abilities offline and then its Handler.
    /// </summary>
    /// <remarks>
    /// In that order, and the order is the design. The abilities go first because the
    /// Handler is a person being confronted; the Handler goes later because the player
    /// gets <c>squad_command_post_compromise_steps</c> to get somebody there or pull
    /// them out.
    /// </remarks>
    [Fact]
    public void ACompromisedPostGoesOfflineAndThenLosesItsHandler()
    {
        (WorldState _, TacticalState mission) = PostMission(out AgentId handler);

        var post = mission.CommandPost;
        SiteRoom postRoom = mission.Layout.Find(mission.Layout.ForwardPostRoomId)!;

        ClearPost(mission);
        mission.Alarm.Raise(AlarmState.Max);
        PutGuardIn(mission, postRoom);

        int compromise = SimulationRules.Squad("squad_command_post_compromise_steps", 300);

        Assert.True(CommandPostSystem.Advance(mission, post));
        Assert.False(post.IsOnline(mission), "A compromised post is still online.");

        Assert.False(
            CommandPostSystem.Use(mission, post, SupportAbility.PingLastKnown),
            "A compromised post granted an ability.");

        Assert.Equal(compromise, post.StepsUntilHandlerTaken);

        // Still staffed, for now: the confrontation has not had time to happen.
        Assert.True(post.IsStaffed(mission));

        for (int i = 0; i < compromise; i++)
            CommandPostSystem.Advance(mission, post);

        Assert.Equal(0, post.StepsUntilHandlerTaken);

        // The Handler is taken by being no longer able to stand.
        mission.AgentActor(handler)!.Condition = ActorCondition.Captured;

        Assert.False(post.IsStaffed(mission));
        Assert.False(post.IsOnline(mission));
    }

    /// <summary>
    /// A site with no forward post has no abilities to grant.
    /// </summary>
    [Fact]
    public void ASiteWithNoForwardPostHasNoSupport()
    {
        (WorldState _, _, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        Assert.False(mission.Layout.ForwardPostRoomId.IsValid);
        Assert.Null(mission.CommandPost.HandlerId);
        Assert.False(mission.CommandPost.IsOnline(mission));

        Assert.False(
            CommandPostSystem.Use(mission, mission.CommandPost, SupportAbility.PingLastKnown),
            "A site with no post granted a support ability.");
    }

    /// <summary>
    /// A Handler on a site with no post is not an error, and nobody is pretending the
    /// post is staffed.
    /// </summary>
    /// <remarks>
    /// The deployment writes a null HandlerId rather than pointing the post at whichever
    /// squad member happens to be standing nearest. That is deliberate: the post is a
    /// place, and a post staffed by whoever was left-handed about it would be a post
    /// whose state a reload could answer differently.
    /// </remarks>
    [Fact]
    public void AHandlerOnAPostlessSiteDispatchesAndIsSimplyUseless()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "handler" });

        // The Handler is on the roster and is still a Handler — it is the post that is
        // missing, not the role.
        SquadMember handler = composition.Members[3];
        Assert.Equal("handler", handler.Role!.RoleKey);
        Assert.True(handler.RequiresCommandPost);

        Assert.False(mission.Layout.ForwardPostRoomId.IsValid);
        Assert.Null(mission.CommandPost.HandlerId);
        Assert.False(mission.CommandPost.IsStaffed(mission));
        Assert.False(mission.CommandPost.IsOnline(mission));

        // And the validator does not raise the unstaffed-post advisory, because there is
        // no post to leave unstaffed.
        Assert.False(composition.UnstaffedPost(siteHasCommandPost: false));
    }

    /// <summary>
    /// The post can be walked to, and walking there costs steps.
    /// </summary>
    /// <remarks>
    /// The gap this holds shut: for most of stage 4e the post was a region the pathfinder
    /// had no edge into, so every query about it answered correctly about somewhere the
    /// squad could not reach. A Handler ordered to hold it stood in the building, the
    /// compromise rule could never see a responder arrive, and none of the state
    /// assertions in this file failed — they were all answering about the right place.
    /// The only thing that catches it is asking whether a route exists.
    /// </remarks>
    [Fact]
    public void TheForwardPostIsRoutableFromTheBuilding()
    {
        (WorldState _, TacticalState mission) = PostMission(out _);

        SiteLayout layout = mission.Layout;
        SiteRoom postRoom = layout.Find(layout.ForwardPostRoomId)!;

        // From the building's ground floor, which is as far away from the top-floor
        // departure room as a squad ever starts.
        SiteRoom start = layout.Find(layout.EntranceRoomId)!;

        TacticalPath path = Pathfinder.FindRoute(layout, mission.Doors, start.Id, postRoom.Id);

        Assert.True(path.ReachedTarget, "No route from the building reaches the forward post.");

        SiteConnection? hop = layout.FindConnectionFor(path.Connections[^1]);

        Assert.NotNull(hop);
        Assert.True(
            hop!.RoomA == postRoom.Id || hop.RoomB == postRoom.Id,
            $"The last hop ({hop}) does not arrive at the post.");

        // ...and it comes from the building, not from another room inside the post.
        SiteRoom? from = layout.Find(hop.Other(postRoom.Id));

        Assert.NotNull(from);
        Assert.True(
            from!.FloorIndex < layout.ForwardPost!.Floors[0].Index,
            "The last hop into the post started somewhere else inside the post.");

        Assert.True(
            hop.TraverseSteps > 0,
            "The post is one step away, so travelling to it costs nothing.");

        // And it is a real cost, not a rounding error on a stair.
        Assert.True(
            path.TotalSteps > hop.TraverseSteps,
            "The hop cost is the whole journey, so the post is adjacent to the extraction.");
    }

    /// <summary>
    /// The post's floors are numbered past the building's, so a position says which
    /// region it is in.
    /// </summary>
    /// <remarks>
    /// Without this, floor 0 x 500 is a room in both regions and
    /// <see cref="TacticalState.RoomOf"/> cannot answer for anybody standing in the post
    /// — which is the single reason the compromise rule never fired.
    /// </remarks>
    [Fact]
    public void TheForwardPostsFloorsDoNotCollideWithTheBuildings()
    {
        (WorldState _, TacticalState mission) = PostMission(out _);

        SiteLayout layout = mission.Layout;
        var buildingFloors = new HashSet<int>();

        foreach (SiteFloor floor in layout.MainSite.Floors)
            buildingFloors.Add(floor.Index);

        foreach (SiteFloor floor in layout.ForwardPost!.Floors)
        {
            Assert.False(
                buildingFloors.Contains(floor.Index),
                $"The post claims floor {floor.Index}, which the building also claims.");
        }

        // And the consequence that actually matters: a guard standing in the post is in
        // the post, not in a building room that happens to share the interval.
        SiteRoom postRoom = layout.Find(layout.ForwardPostRoomId)!;
        TacticalActor guard = PutGuardIn(mission, postRoom);

        Assert.Equal(
            postRoom.Id,
            mission.RoomOf(guard)?.Id ?? SiteRoomId.None);
    }

    // ---- fixtures -------------------------------------------------------------

    /// <summary>
    /// A dispatched mission with a staffed forward command post.
    /// </summary>
    /// <remarks>
    /// The black site, because it is one of the templates in the shipped set with
    /// <c>has_forward_post</c> set — a fixture on the two-room warehouse would have no
    /// post to staff and would silently be testing the postless case.
    /// </remarks>
    private static (WorldState World, TacticalState Mission) PostMission(out AgentId handler)
    {
        (WorldState world, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(
                new[] { "pointman", "hacker", "medic", "handler" },
                TableObjectiveType.StealData,
                TacticalHarness.BlackSiteTemplate);

        Assert.True(
            mission.Layout.ForwardPostRoomId.IsValid,
            "The black site template has no forward command post, so the fixture cannot test one.");

        // Stand the Handler in the post so the abilities have somebody operating them.
        handler = composition.Members[3].AgentId;
        mission.CommandPost.HandlerId = handler;

        SiteRoom postRoom = mission.Layout.Find(mission.Layout.ForwardPostRoomId)!;
        TacticalActor? actor = mission.AgentActor(handler);

        Assert.NotNull(actor);
        TacticalHarness.Place(mission, actor!, postRoom, offsetCm: 5);

        return (world, mission);
    }

    /// <summary>Walks the whole squad into the objective room, away from the exit.</summary>
    private static void DeployToTheObjectiveRoom(TacticalState mission)
    {
        SiteRoom? room = mission.Layout.Find(mission.Layout.ObjectiveRoomId);

        if (room is null)
            return;

        int offset = 5;

        foreach (TacticalActor actor in mission.Squad)
            TacticalHarness.Place(mission, actor, room, offset);
    }

    /// <summary>
    /// Walks every guard out of the forward post.
    /// </summary>
    /// <remarks>
    /// The site template that grows a post also staffs it —
    /// <c>site_forward_post_guard_max</c> guards who live there — so a fixture cannot
    /// treat "a responder reached the post" as something it arranges by putting one guard
    /// somewhere. It has to start from nobody being there at all.
    /// </remarks>
    private static void ClearPost(TacticalState mission)
    {
        SiteRoomId postRoom = mission.Layout.ForwardPostRoomId;
        SiteRoom? elsewhere = mission.Layout.Find(mission.Layout.ObjectiveRoomId);

        foreach (TacticalActor guard in mission.Guards)
        {
            if (mission.RoomOf(guard)?.Id != postRoom)
                continue;

            TacticalHarness.Place(mission, guard, elsewhere!, offsetCm: 5);
        }
    }

    /// <summary>The first connection in the building, as an id.</summary>
    private static int FirstConnectionId(TacticalState mission)
        => mission.Layout.Connections[0].Id.Value;

    /// <summary>Walks a guard into a room, and hands back the guard.</summary>
    private static TacticalActor PutGuardIn(TacticalState mission, SiteRoom room)
    {
        Assert.NotEmpty(mission.Guards);
        TacticalActor guard = mission.Guards[0];
        TacticalHarness.Place(mission, guard, room, offsetCm: 5);
        return guard;
    }
}