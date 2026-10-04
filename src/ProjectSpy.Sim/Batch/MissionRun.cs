using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TacticalOutcome = ProjectSpy.Core.Tactical.MissionOutcome;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// One autonomous mission, run to its end with nobody playing, and everything worth
/// knowing about how it went.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the unit of evidence.</b> Every number in the balance report is a count of
/// these, so what is recorded here decides what the report is able to say. A field left
/// out is a question the report cannot answer, which is why the list is long and the
/// class is not a "did it work" bool.
/// </para>
/// <para>
/// <b>Deterministic.</b> Same seed, same policy, same mission — including the way it ended
/// and the step it ended on. The report quotes percentages across thousands of runs, and
/// a number that moved when the harness was re-run would be worthless.
/// </para>
/// </remarks>
public sealed class MissionRun
{
    public required int TemplateId { get; init; }

    public required int Tier { get; init; }

    public required TableObjectiveType Objective { get; init; }

    public required SquadPolicy Policy { get; init; }

    /// <summary>How the mission ended, in the tactical layer's own vocabulary.</summary>
    public required TacticalOutcome Outcome { get; init; }

    /// <summary>How the mission resolved, in the debrief's vocabulary.</summary>
    public required ResolveClass Class { get; init; }

    /// <summary>True when the objective was achieved, whatever else happened.</summary>
    public required bool ObjectiveComplete { get; init; }

    /// <summary>True when the site never got above suspicious.</summary>
    public required bool Clean { get; init; }

    /// <summary>True when the squad left with nobody dead, captured or downed.</summary>
    public required bool TeamExtracted { get; init; }

    /// <summary>Dead, captured and downed members.</summary>
    public required int Lost { get; init; }

    /// <summary>Steps the mission took.</summary>
    public required long Steps { get; init; }

    /// <summary>Steps from insert to the whole squad being out, or -1 if it never was.</summary>
    public required long TimeToExtraction { get; init; }

    /// <summary>The highest alarm band reached.</summary>
    public required AlarmBand PeakAlarmBand { get; init; }

    /// <summary>Heat the mission added.</summary>
    public required int Heat { get; init; }

    /// <summary>Evidence the mission left behind.</summary>
    public required int Evidence { get; init; }

    /// <summary>How many people the squad killed.</summary>
    public required int LethalActs { get; init; }

    /// <summary>Shots fired, as a proxy for how loud it was.</summary>
    public required int ShootActions { get; init; }

    /// <summary>True when a guard ever identified a squad member by name.</summary>
    public required bool WasIdentified { get; init; }

    /// <summary>How many times a guard got to <see cref="PerceptionLevel.Identified"/>.</summary>
    public required int Identifications { get; init; }

    /// <summary>How many times a guard merely noticed somebody.</summary>
    public required int Notices { get; init; }

    /// <summary>Orders the action system refused as illegal, which must always be zero.</summary>
    public required int IllegalOrders { get; init; }

    /// <summary>Orders refused because a door would not open.</summary>
    public required int BlockedSkips { get; init; }

    /// <summary>Orders skipped because the member was mid-action.</summary>
    public required int BusySkips { get; init; }

    /// <summary>The alarm band every <see cref="BandSampleInterval"/> steps, for the histogram.</summary>
    public required IReadOnlyList<AlarmBand> AlarmTrack { get; init; }

    /// <summary>The squad plan that was in force when it ended.</summary>
    public required string FinalPlanKey { get; init; }

    /// <summary>How many times the squad changed plan.</summary>
    public required int PlanChanges { get; init; }

    /// <summary>Steps between alarm samples.</summary>
    public const int BandSampleInterval = 100;

    /// <summary>How long a mission may run before the harness calls it unfinished.</summary>
    public const int StepCeiling = 4_000;

    /// <summary>
    /// Builds a world, dispatches a squad, plays it out, and reports.
    /// </summary>
    /// <param name="seed">The run's seed. Everything derives from it.</param>
    /// <param name="policy">How the squad plays.</param>
    /// <param name="templateId">The site template.</param>
    /// <param name="tier">The site's tier.</param>
    /// <param name="objective">What they were sent for.</param>
    /// <param name="missionId">The mission number, which also seeds the map.</param>
    /// <param name="roles">Role short names, in order.</param>
    public static MissionRun Execute(
        ulong seed,
        SquadPolicy policy,
        int templateId,
        int tier,
        TableObjectiveType objective,
        int missionId,
        IReadOnlyList<string> roles)
    {
        MissionFixture fixture = MissionFixture.Create(seed, templateId, tier, objective, missionId, roles);

        return Play(fixture, policy);
    }

    /// <summary>
    /// Plays an already-built fixture out to its end.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Execute"/> so a caller that wants to build twenty
    /// thousand fixtures up front and then replay them can, and so <c>play</c> can reuse
    /// the exact same fixture-building the batch run uses — a balance number measured on
    /// a different mission than the one a human plays would be a number about nothing.
    /// </remarks>
    public static MissionRun Play(MissionFixture fixture, SquadPolicy policy)
    {
        TacticalState state = fixture.Mission;
        var driver = new SquadPolicyDriver(policy);
        var track = new List<AlarmBand>();

        int identified = 0;
        int notices = 0;
        int seenIdentifications = 0;
        int seenNotices = 0;

        var runner = new TacticalMissionRunner(state, new RngStreams(fixture.Seed));

        long extractedAt = -1;
        long previousStep = state.Step;

        while (!state.IsOver && state.Step < StepCeiling)
        {
            driver.Think(state, fixture.Composition);

            // Perceptions are sampled before the step, so a run that is identified on its
            // very last step is counted rather than lost at the boundary.
            int beforeIdentified = CountIdentifications(state);
            int beforeNotices = CountNotices(state);

            runner.Step();

            int afterIdentified = CountIdentifications(state);
            int afterNotices = CountNotices(state);

            seenIdentifications = afterIdentified;
            seenNotices = afterNotices;

            identified += Math.Max(0, afterIdentified - beforeIdentified);
            notices += Math.Max(0, afterNotices - beforeNotices);

            if (state.Step - previousStep >= BandSampleInterval)
            {
                track.Add(state.Alarm.Band);
                previousStep = state.Step;
            }

            if (extractedAt < 0 && state.IsOver && EveryoneIsOut(state))
                extractedAt = state.Step;
        }

        track.Add(state.Alarm.Band);

        MissionResolution resolution = ResolveSystem.Classify(
            state, state.ObjectiveOutcome, aborted: state.AbortCalled);

        return new MissionRun
        {
            TemplateId = fixture.TemplateId,
            Tier = fixture.Tier,
            Objective = fixture.Objective,
            Policy = policy,
            Outcome = state.Outcome,
            Class = resolution.Class,
            ObjectiveComplete = state.ObjectiveOutcome.IsComplete,
            Clean = resolution.Class == ResolveClass.CleanSuccess,
            TeamExtracted = resolution.TeamExtracted,
            Lost = resolution.Lost,
            Steps = state.Step,
            TimeToExtraction = extractedAt,
            PeakAlarmBand = PeakBand(track),
            Heat = resolution.Heat,
            Evidence = resolution.Evidence,
            LethalActs = state.LethalActs,
            ShootActions = fixture.ShootActions,
            WasIdentified = seenIdentifications > 0,
            Identifications = identified,
            Notices = notices,
            IllegalOrders = driver.IllegalOrders,
            BlockedSkips = driver.BlockedSkips,
            BusySkips = driver.BusySkips,
            AlarmTrack = track,
            FinalPlanKey = driver.PlanKey,
            PlanChanges = driver.PlanChanges,
        };
    }

    /// <summary>
    /// True when every squad member is out of the building.
    /// </summary>
    /// <remarks>
    /// "Out" rather than "not captured": a member who was carried out unconscious is out
    /// as far as the clock is concerned, and the injury is applied afterwards in the
    /// debrief. Counting only the ones who walked would report a squad that dragged three
    /// people through a fire escape as never having got out at all.
    /// </remarks>
    private static bool EveryoneIsOut(TacticalState state)
    {
        foreach (TacticalActor actor in state.Squad)
        {
            if (actor.Condition is ActorCondition.Dead or ActorCondition.Captured)
                return false;
        }

        return true;
    }

    private static int CountIdentifications(TacticalState state)
    {
        int count = 0;

        foreach (TacticalActor guard in state.Guards)
        {
            if (guard.Memory.HasIdentification)
                count++;
        }

        return count;
    }

    private static int CountNotices(TacticalState state)
    {
        int count = 0;

        foreach (TacticalActor guard in state.Guards)
        {
            if (guard.Memory.PeakLevel >= PerceptionLevel.Noticed)
                count++;
        }

        return count;
    }

    /// <summary>The highest band in the track.</summary>
    private static AlarmBand PeakBand(IReadOnlyList<AlarmBand> track)
    {
        AlarmBand peak = AlarmBand.Calm;

        foreach (AlarmBand band in track)
        {
            if (band > peak)
                peak = band;
        }

        return peak;
    }
}