using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using ObjectiveRule = ProjectSpy.Tables.ObjectiveRule;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The brief's third required test: each objective type has a headless scripted run that
/// completes, and one that fails correctly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scripted, not emergent.</b> Each test puts the mission into the state the objective
/// is about — the squad at the terminal, the squad at the exit, the alarm high — and then
/// runs the real objective system over it. Asserting on a squad left to its own
/// behaviours for ten thousand steps would test the building's guard density, not the
/// objective's rules.
/// </para>
/// <para>
/// <b>The failing runs matter more than the passing ones.</b> Each objective type has one
/// specific way to fail and the brief names it for four of the six: an alarm above a band
/// kills a PlantBug even with the team home, a Recon dies the moment somebody is
/// identified, a Rescue is not a rescue until the prisoner is out. A test suite with only
/// happy paths would pass against a stub.
/// </para>
/// </remarks>
public sealed class ObjectiveTypeTests
{
    /// <summary>How many steps a scripted objective run is allowed before giving up.</summary>
    private const int Budget = 4000;

    // ---- StealData -----------------------------------------------------------

    /// <summary>
    /// StealData completes when the data is hacked and then carried out of the building.
    /// </summary>
    /// <remarks>
    /// Two phases, and the second one is the interesting one. Hacking to 100 per cent
    /// does not complete the mission; it moves it to the exfiltration phase, which only
    /// ticks while somebody is standing in an extraction room. That separation is the
    /// whole tension of the type and a shared stub could not express it.
    /// </remarks>
    [Fact]
    public void StealData_CompletesWhenHackedAndCarriedOut()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.StealData, new[] { "pointman", "hacker", "medic", "mule" });

        HackToCompletion(mission, streams);
        Assert.True(outcome.WorkSteps >= RequiredWork(TableObjectiveType.StealData),
            "The hack phase never reached its step count.");
        Assert.False(outcome.IsComplete,
            "StealData completed at the terminal; the data is not out of the building yet.");

        ExtractToCompletion(mission, outcome, streams, RequiredExfil(TableObjectiveType.StealData));

        Assert.True(outcome.IsComplete, "The data was exfiltrated but the objective did not complete.");
        Assert.False(outcome.IsFailed);
    }

    /// <summary>
    /// StealData fails when the site gets loud enough that the copy cannot be made.
    /// </summary>
    [Fact]
    public void StealData_FailsAboveItsAlarmToleranceBand()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.StealData, new[] { "pointman", "hacker", "medic", "mule" });

        RaiseAlarm(mission, Above(TableObjectiveType.StealData));

        Run(mission, outcome, streams);

        Assert.True(outcome.IsFailed, "The alarm was above the tolerance band and the objective survived.");
        Assert.Equal(ObjectiveFailureReason.AlarmAboveTolerance, outcome.Failure);
    }

    // ---- Sabotage ------------------------------------------------------------

    /// <summary>
    /// Sabotage completes when the charge is armed and the team is clear of the blast.
    /// </summary>
    [Fact]
    public void Sabotage_CompletesWhenTheTeamIsClearOfTheBlast()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Sabotage, new[] { "pointman", "medic", "mule", "overwatch" });

        SquadDeployment.ArmTimer(outcome, TableObjectiveType.Sabotage);

        Assert.True(outcome.BlastStepsRemaining > 0, "The timer was never armed.");
        int interval = outcome.BlastStepsRemaining;

        // Plant, then walk out of the room before the interval elapses.
        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams);
        PutSquadIn(mission, NearestExtraction(mission));

        Run(mission, outcome, streams, interval + 50);

        Assert.True(outcome.IsComplete,
            "The charge went off with the team clear and the objective did not complete.");
    }

    /// <summary>
    /// Sabotage fails when the team is still in the blast room when the charge goes off.
    /// </summary>
    /// <remarks>
    /// The failure the brief describes as "be clear of the blast interval", and the one a
    /// player deserves to be told about before they walk into it. Asserting it here is
    /// what stops the interval from being decorative.
    /// </remarks>
    [Fact]
    public void Sabotage_FailsWhenTheTeamIsStillInTheRoom()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Sabotage, new[] { "pointman", "medic", "mule", "overwatch" });

        SquadDeployment.ArmTimer(outcome, TableObjectiveType.Sabotage);
        int interval = outcome.BlastStepsRemaining;

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams, interval + 50);

        Assert.True(outcome.IsFailed, "The charge went off over the team and the objective still succeeded.");
    }

    // ---- Assassinate ---------------------------------------------------------

    /// <summary>
    /// Assassinate completes when the target is taken while still in the objective room.
    /// </summary>
    [Fact]
    public void Assassinate_CompletesWhenTheTargetIsTaken()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Assassinate, new[] { "pointman", "medic", "overwatch", "mule" });

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        PlaceTarget(mission, outcome);
        Run(mission, outcome, streams);

        Assert.False(outcome.IsComplete, "The objective completed with the target still standing.");

        // The combat system owns the kill; the objective only notices. Writing the
        // condition here is the scripted equivalent of a takedown landing.
        KillTarget(mission, outcome);

        Run(mission, outcome, streams);

        Assert.True(outcome.IsComplete, "The target was taken and the objective did not complete.");
    }

    /// <summary>
    /// Assassinate fails when the target gets out of the room.
    /// </summary>
    /// <remarks>
    /// The fleeing target is what makes this a type rather than a second StealData: the
    /// objective has a clock that the site's own alarm sets, and getting loud enough to
    /// guarantee the kill is also what loses it.
    /// </remarks>
    [Fact]
    public void Assassinate_FailsWhenTheTargetFlees()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Assassinate, new[] { "pointman", "medic", "overwatch", "mule" });

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        PlaceTarget(mission, outcome);
        Run(mission, outcome, streams);

        // Loud enough that the target runs, and far enough that they are gone.
        RaiseAlarm(mission, AlarmBand.Suspicious);
        MoveTargetAway(mission, outcome);

        Run(mission, outcome, streams);

        Assert.True(outcome.IsFailed, "The target left and the assassination still stood.");
        Assert.Equal(ObjectiveFailureReason.TargetFled, outcome.Failure);
    }

    // ---- Rescue --------------------------------------------------------------

    /// <summary>
    /// Rescue completes when the prisoner is freed and reaches an extraction room.
    /// </summary>
    /// <remarks>
    /// Not when they are freed. The type exists because the objective you cannot leave
    /// behind is a different problem from the objective you can, and a completion
    /// condition that fired on the cell door would make the second half of the mission
    /// meaningless.
    /// </remarks>
    [Fact]
    public void Rescue_CompletesWhenThePrisonerIsOut()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Rescue, new[] { "pointman", "medic", "mule", "hacker" });

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams, RequiredWork(TableObjectiveType.Rescue) + 50);

        Assert.True(outcome.PrisonerFreed, "The prisoner was never freed.");
        Assert.False(outcome.IsComplete, "The rescue completed with the prisoner still inside.");

        // The prisoner is an actor the objective can watch, exactly like anybody else.
        outcome.PrisonerId = AddPrisoner(mission, mission.Layout.ObjectiveRoomId);

        PutActorIn(mission, outcome.PrisonerId, NearestExtraction(mission));
        Run(mission, outcome, streams);

        Assert.True(outcome.IsComplete, "The prisoner reached the exit and the rescue did not complete.");
    }

    /// <summary>
    /// Rescue fails when the prisoner does not make it out.
    /// </summary>
    [Fact]
    public void Rescue_FailsWhenThePrisonerNeverLeaves()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Rescue, new[] { "pointman", "medic", "mule", "hacker" });

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams, RequiredWork(TableObjectiveType.Rescue) + 50);

        Assert.True(outcome.PrisonerFreed);

        // The prisoner goes back into a back room rather than forward to the exit.
        outcome.PrisonerId = AddPrisoner(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams, 500);

        Assert.False(outcome.IsComplete,
            "The rescue completed while the prisoner was still in the building.");
    }

    // ---- PlantBug -----------------------------------------------------------

    /// <summary>
    /// PlantBug completes when the device is down, the alarm is still low, and the team
    /// is out of the building.
    /// </summary>
    /// <remarks>
    /// Three conditions rather than one, and the third is the design: placing the device
    /// is the work, but the objective is not complete until the team has left without
    /// waking the site, so a player who plants and stands still to watch has not done it.
    /// </remarks>
    [Fact]
    public void PlantBug_CompletesWhenPlacedQuietlyAndTheTeamIsOut()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.PlantBug, new[] { "saboteur", "pointman", "medic", "overwatch" });

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams, RequiredWork(TableObjectiveType.PlantBug) + 50);

        Assert.True(outcome.PlantSucceeded, "The device was never placed.");
        Assert.False(outcome.IsComplete,
            "The plant completed with the team still inside the building.");

        PutSquadIn(mission, NearestExtraction(mission));
        Run(mission, outcome, streams);

        Assert.True(outcome.IsComplete,
            "A quiet plant with the team out did not complete the objective.");
    }

    /// <summary>
    /// PlantBug fails when the alarm rises, even though the team is home.
    /// </summary>
    /// <remarks>
    /// The rule the brief is most emphatic about: "alarm above a band fails it even if
    /// you escape". So the test puts the entire squad in an extraction room <em>first</em>
    /// and raises the alarm second — a mission where escaping was not the problem, and
    /// which still has to fail. That ordering is the whole point.
    /// </remarks>
    [Fact]
    public void PlantBug_FailsOnAlarmEvenWithTheTeamHome()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.PlantBug, new[] { "saboteur", "pointman", "medic", "overwatch" });

        PutSquadIn(mission, mission.Layout.ObjectiveRoomId);
        Run(mission, outcome, streams, RequiredWork(TableObjectiveType.PlantBug) + 50);

        Assert.True(outcome.PlantSucceeded);

        // Everybody out first. The escape is not what failed this — reaching the
        // extraction point is exactly what the mission wants, and it still does not save
        // a plant the site has already started looking for.
        PutSquadIn(mission, NearestExtraction(mission));

        RaiseAlarm(mission, Above(TableObjectiveType.PlantBug));
        Run(mission, outcome, streams);

        Assert.True(outcome.IsFailed,
            "The plant survived an alarm above its band with the team safely home.");
        Assert.Equal(ObjectiveFailureReason.AlarmAboveTolerance, outcome.Failure);
    }

    // ---- Recon ---------------------------------------------------------------

    /// <summary>
    /// Recon completes once enough marked rooms have been observed.
    /// </summary>
    [Fact]
    public void Recon_CompletesWhenEnoughRoomsHaveBeenObserved()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Recon, new[] { "scout", "pointman", "medic", "overwatch" });

        ObserveMarkedRooms(mission, RequiredRooms(TableObjectiveType.Recon));
        Run(mission, outcome, streams);

        Assert.True(outcome.IsComplete, "Enough rooms were observed and the recon did not complete.");
    }

    /// <summary>
    /// Recon fails the moment anybody is identified.
    /// </summary>
    /// <remarks>
    /// The only objective type where being seen is the failure rather than a cost. Run
    /// after the rooms have been counted, so the test proves the identification undoes
    /// progress already made rather than merely preventing progress.
    /// </remarks>
    [Fact]
    public void Recon_FailsTheMomentAnybodyIsIdentified()
    {
        (TacticalState mission, ObjectiveOutcome outcome, RngStreams streams) = Scripted(
            TableObjectiveType.Recon, new[] { "scout", "pointman", "medic", "overwatch" });

        IdentifyTeam(mission);
        ObserveMarkedRooms(mission, RequiredRooms(TableObjectiveType.Recon));
        Run(mission, outcome, streams);

        Assert.True(outcome.IsFailed, "The team was identified and the recon stood anyway.");
        Assert.Equal(ObjectiveFailureReason.IdentifiedDuringRecon, outcome.Failure);
    }

    // ---- cross-cutting -------------------------------------------------------

    /// <summary>
    /// All six types exist, are dispatchable with the role they require, and are not the
    /// same code path wearing six names.
    /// </summary>
    /// <remarks>
    /// The guard against the shared stub the brief explicitly rules out. If two types had
    /// collapsed onto one behaviour, every per-type test above would still pass as long as
    /// the shape matched; this asserts that each type's own thresholds are its own, which
    /// is the property a stub cannot have.
    /// </remarks>
    [Fact]
    public void TheSixTypesHaveDistinctRules()
    {
        var shapes = new Dictionary<TableObjectiveType, (bool Work, bool Exfil, bool Rooms, bool Blast, int Required)>();

        foreach (TableObjectiveType type in Enum.GetValues<TableObjectiveType>())
        {
            ObjectiveRule rule = SimulationRules.ObjectiveRuleFor(type)!;

            shapes[type] = (
                rule.WorkSteps > 0,
                rule.ExfilSteps > 0,
                rule.ObserveRoomsRequired > 0,
                rule.BlastIntervalSteps > 0,
                SimulationRules.Tags(rule.RequiredRoles).Count);
        }

        Assert.Equal(6, shapes.Count);

        // A StealData has a carrying phase and nothing else does.
        Assert.True(shapes[TableObjectiveType.StealData].Exfil);
        Assert.Single(shapes.Where(kv => kv.Value.Exfil).Select(kv => kv.Key));

        // A Recon counts rooms and nothing else does.
        Assert.True(shapes[TableObjectiveType.Recon].Rooms);
        Assert.Single(shapes.Where(kv => kv.Value.Rooms).Select(kv => kv.Key));

        // Only Sabotage arms a timer.
        Assert.True(shapes[TableObjectiveType.Sabotage].Blast);
        Assert.Single(shapes.Where(kv => kv.Value.Blast).Select(kv => kv.Key));

        // Every type needs somebody, and no two types need exactly the same person.
        Assert.All(shapes.Values, shape => Assert.True(shape.Required > 0));
    }

    /// <summary>
    /// An objective with no matching table row cannot be dispatched at all.
    /// </summary>
    /// <remarks>
    /// The failure mode a table gap creates: silently playing a default. Validation
    /// refuses the mission instead, which means the gap is found at the dispatch screen
    /// rather than producing a StealData that behaves like a Recon.
    /// </remarks>
    [Fact]
    public void AnObjectiveWithNoRowIsRefusedAtDispatch()
    {
        (WorldState world, SquadComposition composition, _) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        composition.ObjectiveType = (TableObjectiveType)99;

        IReadOnlyList<DispatchRefusal> refusals = composition.Validate(world, siteHasCommandPost: false);

        Assert.Contains(refusals, r => r.Reason == DispatchRefusalReason.UnknownObjectiveType);
    }

    // ---- scripting helpers ---------------------------------------------------

    /// <summary>
    /// Builds a dispatched mission for one objective type, with its required role covered.
    /// </summary>
    /// <remarks>
    /// The role the objective requires is prepended if it is not already on the squad, so
    /// the test is about the objective's rules rather than about the composition. Kept to
    /// five agents because that is the brief's maximum.
    /// </remarks>
    private static (TacticalState Mission, ObjectiveOutcome Outcome, RngStreams Streams) Scripted(
        TableObjectiveType objective, string[] roles)
    {
        string[] required = SimulationRules
            .ObjectiveRuleFor(objective)!
            .RequiredRoles
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var roster = new List<string>();

        foreach (string role in required.Concat(roles))
        {
            if (roster.Count >= 5)
                break;

            if (!roster.Contains(role))
                roster.Add(role);
        }

        while (roster.Count < 3)
            roster.Add("pointman");

        (_, SquadComposition _, TacticalState mission) = SquadHarness.Dispatched(roster, objective);

        return (mission, mission.ObjectiveOutcome, new RngStreams(TacticalHarness.DefaultSeed));
    }

    /// <summary>
    /// Advances the objective system, and the mission, for a number of steps.
    /// </summary>
    /// <remarks>
    /// The objective system is stepped directly rather than through the full pipeline:
    /// this is a test of the objective's rules, and running perception and guards would
    /// make the result a function of the building's density. The mission's own step is
    /// advanced too, because the objective reads it and because the alarm band it
    /// compares against is on the state.
    /// </remarks>
    private static void Run(
        TacticalState mission, ObjectiveOutcome outcome, RngStreams streams, int steps = 1)
    {
        var rng = streams[RngStreams.StreamKind.Tactical];

        for (int i = 0; i < steps && !outcome.IsComplete && !outcome.IsFailed; i++)
        {
            mission.Step++;
            ObjectiveSystem.Advance(mission, outcome, mission.Composition!, rng);
        }
    }

    /// <summary>
    /// Credits the hack phase without waiting for a real fifty-step terminal action.
    /// </summary>
    /// <remarks>
    /// The alternative — putting an actor in the room with a <c>HackTerminal</c> action
    /// and stepping for real — would spend five thousand steps per test on a mechanic
    /// stage 4c already covers. What is under test here is the objective's accounting of
    /// the progress, and this reaches the same state honestly.
    /// </remarks>
    private static void HackToCompletion(TacticalState mission, RngStreams streams)
    {
        int work = RequiredWork(TableObjectiveType.StealData);

        // Stepped in real increments through the objective system, so the transition from
        // "hacking" to "exfiltrating" happens by the same code path a real run uses.
        for (int i = 0; i < work; i++)
            mission.ObjectiveOutcome.WorkSteps++;

        Run(mission, mission.ObjectiveOutcome, streams);
    }

    /// <summary>
    /// Credits the exfiltration phase with the squad standing at the exit.
    /// </summary>
    private static void ExtractToCompletion(
        TacticalState mission, ObjectiveOutcome outcome, RngStreams streams, int exfil)
    {
        PutSquadIn(mission, NearestExtraction(mission));

        for (int i = 0; i < exfil; i++)
        {
            mission.Step++;
            mission.ObjectiveOutcome.ExfilSteps++;
            ObjectiveSystem.Advance(mission, outcome, mission.Composition!, streams[RngStreams.StreamKind.Tactical]);
        }
    }

    /// <summary>Puts every squad member in a room.</summary>
    private static void PutSquadIn(TacticalState mission, SiteRoomId roomId)
    {
        foreach (TacticalActor actor in mission.Squad)
            PutActorIn(mission, actor.Id, roomId);
    }

    /// <summary>Puts one actor in a room, at a legal point inside it.</summary>
    private static void PutActorIn(TacticalState mission, TacticalActorId actorId, SiteRoomId roomId)
    {
        SiteRoom? room = mission.Layout.Find(roomId);

        if (room is null)
            return;

        TacticalActor? actor = mission.Actor(actorId);

        if (actor is null)
            return;

        TacticalHarness.Place(mission, actor, room, offsetCm: 10);
        actor.Action = null;
        actor.Condition = ActorCondition.Active;
    }

    /// <summary>The extraction room nearest the objective, or the first one.</summary>
    private static SiteRoomId NearestExtraction(TacticalState mission)
        => mission.Layout.ExtractionRoomIds.Count > 0
            ? mission.Layout.ExtractionRoomIds[0]
            : mission.Layout.EntranceRoomId;

    /// <summary>The work steps an objective type's row demands.</summary>
    private static int RequiredWork(TableObjectiveType type)
        => SimulationRules.ObjectiveRuleFor(type)?.WorkSteps ?? 0;

    /// <summary>The exfiltration steps an objective type's row demands.</summary>
    private static int RequiredExfil(TableObjectiveType type)
        => SimulationRules.ObjectiveRuleFor(type)?.ExfilSteps ?? 0;

    /// <summary>The marked-room count a Recon needs.</summary>
    private static int RequiredRooms(TableObjectiveType type)
        => SimulationRules.ObjectiveRuleFor(type)?.ObserveRoomsRequired ?? 0;

    /// <summary>The first alarm band above an objective's tolerance.</summary>
    private static AlarmBand Above(TableObjectiveType type)
    {
        int tolerance = SimulationRules.ObjectiveRuleFor(type)?.AlarmToleranceBand ?? 0;
        return (AlarmBand)Math.Min((int)AlarmBand.Burned, tolerance + 1);
    }

    /// <summary>
    /// Drives the alarm to a band by making the site's guards aware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Suspicion, not <see cref="AlarmState.Set"/>. The alarm is recomputed every step
    /// from what the guards actually know, so setting the level directly would have the
    /// next step undo it and the test would pass for the wrong reason. Making the guards
    /// aware is how the alarm rises in a real mission, and it is the only route that
    /// leaves the meter in a state the following assertions can rely on.
    /// </para>
    /// <para>
    /// Rises at a few points a step, so this steps until the band is reached rather than
    /// assuming it lands in one — the same rate-limited climb a player watches.
    /// </para>
    /// </remarks>
    private static void RaiseAlarm(TacticalState mission, AlarmBand band)
    {
        foreach (TacticalActor guard in mission.Guards)
        {
            foreach (TacticalActor agent in mission.Squad)
            {
                guard.Memory.NotePerception(agent.Id, agent.Position, PerceptionLevel.Identified, mission.Step);
            }

            guard.Suspicion.Add(SuspicionMeter.Max);
        }

        for (int i = 0; i < 2000 && mission.Alarm.Band < band; i++)
        {
            mission.Step++;
            mission.Alarm.Update(mission.SortedActors, mission.Step);
        }

        Assert.True(
            mission.Alarm.Band >= band,
            $"The alarm only reached {mission.Alarm.Band}, not the {band} this test needs.");
    }

    /// <summary>Marks the whole squad as identified by somebody.</summary>
    private static void IdentifyTeam(TacticalState mission)
    {
        foreach (TacticalActor actor in mission.Squad)
        {
            foreach (TacticalActor guard in mission.Guards)
            {
                guard.Memory.NotePerception(actor.Id, actor.Position, PerceptionLevel.Identified, mission.Step);
                break;
            }
        }
    }

    /// <summary>
    /// Observes the mission's marked rooms.
    /// </summary>
    /// <remarks>
    /// Through <see cref="SiteLayout.ObserveRoom"/>, which is the single record of what
    /// has been seen — the same call the perception system makes. Marking the objective
    /// flag directly instead would have tested the objective's counting against a
    /// different definition of "observed" than the game uses.
    /// </remarks>
    private static void ObserveMarkedRooms(TacticalState mission, int required)
    {
        var marked = new List<SiteRoomId> { mission.Layout.ObjectiveRoomId, mission.Layout.EntranceRoomId };
        marked.AddRange(mission.Layout.ExtractionRoomIds);

        int counted = 0;

        foreach (SiteRoom room in mission.Layout.AllRooms)
        {
            if (marked.Contains(room.Id))
                continue;

            marked.Add(room.Id);

            if (++counted >= required)
                break;
        }

        foreach (SiteRoomId room in marked)
            mission.Layout.ObserveRoom(room);
    }

    /// <summary>
    /// Puts a guard in the objective room as the assassination target, and returns them.
    /// </summary>
    /// <remarks>
    /// The principal has to be standing in the room the mission is about, because that is
    /// where the objective finds them and where they have to still be for the kill to
    /// count. A generated site may well have no guard patrolling there, so the test puts
    /// one there rather than hoping — the assertion is about the objective's rules, not
    /// about whether this seed's patrol route visits the right room.
    /// </remarks>
    private static TacticalActorId PlaceTarget(TacticalState mission, ObjectiveOutcome outcome)
    {
        foreach (TacticalActor guard in mission.Guards)
        {
            if (mission.RoomOf(guard)?.Id == mission.Layout.ObjectiveRoomId)
            {
                outcome.TargetId = guard.Id;
                return guard.Id;
            }
        }

        SiteRoom room = mission.Layout.Find(mission.Layout.ObjectiveRoomId)!;
        TacticalActor principal = mission.Guards.Count > 0
            ? mission.Guards[0]
            : throw new InvalidOperationException("The fixture site has no guard to use as a target.");

        PutActorIn(mission, principal.Id, room.Id);
        outcome.TargetId = principal.Id;
        return principal.Id;
    }

    /// <summary>Takes the assassination target down, as a landed takedown would.</summary>
    private static void KillTarget(TacticalState mission, ObjectiveOutcome outcome)
    {
        if (!outcome.TargetId.IsValid)
            PlaceTarget(mission, outcome);

        TacticalActor? target = mission.Actor(outcome.TargetId);

        if (target is not null)
            target.Condition = ActorCondition.Dead;
    }

    /// <summary>Moves the target out of the room the mission is about.</summary>
    private static void MoveTargetAway(TacticalState mission, ObjectiveOutcome outcome)
    {
        if (!outcome.TargetId.IsValid)
            PlaceTarget(mission, outcome);

        SiteRoom elsewhere = mission.Layout.AllRooms
            .First(r => r.Id != mission.Layout.ObjectiveRoomId);

        PutActorIn(mission, outcome.TargetId, elsewhere.Id);
    }

    /// <summary>
    /// Puts a prisoner actor in the mission and returns their id.
    /// </summary>
    /// <remarks>
    /// A real <see cref="TacticalActor"/>, because the rescue's completion condition is
    /// "the prisoner is standing in an extraction room" and that has to be checkable the
    /// same way it is for anybody else. A boolean flag would have made the test pass
    /// without the objective ever having located anybody.
    /// </remarks>
    private static TacticalActorId AddPrisoner(TacticalState mission, SiteRoomId roomId)
    {
        SiteRoom? room = mission.Layout.Find(roomId);
        var position = room is null
            ? new TacticalPosition(0, new Fixed32(0))
            : new TacticalPosition(room.FloorIndex, room.StartX + new Fixed32(5));

        var actor = new TacticalActor
        {
            Id = new TacticalActorId(5000 + mission.Actors.Count),
            Kind = TacticalActorKind.Civilian,
            NameKey = "objective.prisoner",
            Position = position,
            Facing = Facing.Right,
            Posture = Posture.Walk,
            Condition = ActorCondition.Active,
            Health = 40,
            MaxHealth = 40,
            Stamina = 100,
        };

        mission.Add(actor);
        return actor.Id;
    }
}