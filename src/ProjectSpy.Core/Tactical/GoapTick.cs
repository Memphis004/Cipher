namespace ProjectSpy.Core.Tactical;

/// <summary>
/// One step of NPC planning, shared by the step pipeline and the headless entry point.
/// </summary>
/// <remarks>
/// <para>
/// Both callers run exactly this. The step pipeline needs it because
/// <c>NpcPlanning</c> is a documented phase, and <see cref="NpcPlanner.Plan"/> needs it
/// because tests and the console simulator drive NPCs without building a pipeline. Two
/// copies of the four steps below would be two answers to "what did the guards do", and
/// the console simulator is exactly where a divergence would hide — the shipped game
/// would look right and the headless balance runs would be measuring something else.
/// </para>
/// <para>
/// <b>The four steps, in order.</b> Notify events into replan requests; replan within the
/// per-step budget; advance the radio transmit timers; execute one action per idle NPC.
/// Notify before plan because a guard that heard something this step should plan around
/// it rather than one step late, and plan before execute because a plan that has just
/// been invalidated should not be executed on its way out.
/// </para>
/// </remarks>
public static class GoapTick
{
    /// <summary>
    /// Runs one planning step over a mission's NPCs.
    /// </summary>
    /// <returns>How many NPCs were given an order this step.</returns>
    public static int Run(TacticalState state, long step, IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        GoapDirector director = GoapRuntime.DirectorFor(state);

        // Collected once and passed down. See GoapStepFacts for why this matters: both of
        // these properties allocate and sort, and asking inside a per-NPC loop was the
        // single largest cost in the planning layer.
        GoapStepFacts facts = GoapStepFacts.Collect(state);

        NotifyEvents(state, director, step, facts);
        director.PlanStep(state, step);
        GoapRadioSystem.Advance(state, director, step);

        return ExecutePlans(state, director, step, rng, facts);
    }

    /// <summary>
    /// Turns this step's events into replan requests.
    /// </summary>
    /// <remarks>
    /// The bridge between the rest of the pipeline and the planner, and the reason
    /// replanning is event-driven. Each check compares against what this NPC already
    /// knew, so a step in which nothing happened costs three integer comparisons per
    /// NPC and no planning at all — which is what keeps thirty guards affordable.
    /// </remarks>
    private static void NotifyEvents(TacticalState state, GoapDirector director, long step, GoapStepFacts facts)
    {
        foreach (TacticalActor actor in facts.Actors)
        {
            if (actor.IsAgent || !actor.CanAct)
                continue;

            GoapAgent agent = director.AgentFor(actor);

            // Perception: edge-triggered on the high-water mark. A guard that has already
            // identified the team keeps knowing it, and re-requesting a plan every step
            // it stayed visible would let one guard spend the whole budget indefinitely.
            if (actor.Memory.PeakLevel > agent.LastNotifiedPerception)
            {
                agent.LastNotifiedPerception = actor.Memory.PeakLevel;
                agent.Findings.ContactIdentified = actor.Memory.HasIdentification;
                director.RequestPlan(agent, GoapInvalidationReason.PerceptionChanged, step);
            }

            // Noise: edge-triggered on the step it was first heard.
            if (actor.Memory.LastNoiseOrigin.IsValid && actor.Memory.LastNoiseStep > agent.LastNotifiedNoiseStep)
            {
                agent.LastNotifiedNoiseStep = actor.Memory.LastNoiseStep;
                agent.Findings.NoiseHeard = true;
                director.RequestPlan(agent, GoapInvalidationReason.NoiseHeard, step);
            }

            // A downed colleague, but only one this NPC could see. Learning about a
            // casualty in another part of the building would be a second omniscience
            // channel, and a particularly well-camouflaged one, because the guard's
            // subsequent behaviour would look correct in every other respect.
            bool allyDown = facts.CanSeeDownedAlly(actor);

            if (allyDown != agent.LastNotifiedAllyDown)
            {
                agent.LastNotifiedAllyDown = allyDown;
                agent.Findings.AllyDown = allyDown;
                director.RequestPlan(agent, GoapInvalidationReason.AllyDown, step);
            }

            // Suspicion band and alarm band. Compared, not polled: this raises a request
            // only when a band actually changes.
            agent.ObserveTriggers(GoapObserver.PriorityInputsFor(state, actor, step, facts), state.Alarm.Band, step);
        }
    }

    /// <summary>
    /// Issues each idle NPC's next planned action.
    /// </summary>
    /// <remarks>
    /// <b>One order per NPC per step</b>, which is the stage-4c invariant and still
    /// load-bearing: two orders issued in one step would both resolve in the next
    /// <c>ApplyQueuedCommands</c> and the second would overwrite the first.
    /// </remarks>
    private static int ExecutePlans(TacticalState state, GoapDirector director, long step, IRng rng, GoapStepFacts facts)
    {
        int ordered = 0;

        foreach (TacticalActor actor in facts.Actors)
        {
            if (actor.IsAgent || !actor.CanAct)
                continue;

            // Mid-action NPCs are left alone. Re-executing the first step of a new plan
            // every time the plan changed would mean no GOAP action ever completed.
            if (actor.Action is { IsComplete: false })
                continue;

            GoapAgent agent = director.AgentFor(actor);

            if (!agent.Plan.HasActions)
                continue;

            if (GoapExecutor.ExecuteNext(state, agent, step, rng))
                ordered++;
        }

        return ordered;
    }
}