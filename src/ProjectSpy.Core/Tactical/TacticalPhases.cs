using ProjectSpy.Core.Missions;

// Only for the `using` above to be justified at all: the phases resolve rooms and
// extraction points through the state, and the one place a layout type is named
// directly is the objective check's "is the whole squad at the exit".

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// The ten step phases, implemented.
/// </summary>
/// <remarks>
/// Each phase is a separate type and each does exactly one thing, because the order is
/// a contract and a contract that lives inside one large method cannot be checked
/// phase-by-phase. The pairwise constraints that justify the order are asserted
/// individually by <c>TacticalStepOrderTests</c>, so reordering fails as a named
/// constraint rather than as an unexplained change in outcomes.
/// </remarks>
public static class TacticalPhases
{
    /// <summary>Builds a pipeline with all ten phases registered.</summary>
    public static TacticalStepPipeline CreateDefault()
    {
        var pipeline = new TacticalStepPipeline();

        pipeline.Register(new ApplyQueuedCommandsPhase());
        pipeline.Register(new MovementResolutionPhase());
        pipeline.Register(new ActionProgressPhase());
        pipeline.Register(new NoisePropagationPhase());
        pipeline.Register(new PerceptionPhase());
        pipeline.Register(new NpcPlanningPhase());
        pipeline.Register(new AlarmUpdatePhase());
        pipeline.Register(new DamageAndStatusPhase());
        pipeline.Register(new ObjectiveCheckPhase());
        pipeline.Register(new EventEmitPhase());

        return pipeline;
    }

    /// <summary>
    /// Starts every order issued since the last step, in the order it was issued.
    /// </summary>
    /// <remarks>
    /// Before movement, so that an order issued this step is reflected in this step's
    /// movement rather than a step later. Drain order matters: two orders issued in the
    /// same frame resolve the same way on every replay, which is what makes a recorded
    /// run reproducible.
    /// </remarks>
    public sealed class ApplyQueuedCommandsPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.ApplyQueuedCommands;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;

            // Drain before applying: an order started by this phase may queue more work,
            // and iterating a list that is growing underneath would either miss those or
            // throw.
            List<TacticalOrder> orders = new(state.PendingOrders);
            state.PendingOrders.Clear();

            int accepted = 0;

            foreach (TacticalOrder order in orders)
            {
                if (ActionSystem.TryBegin(state, order, context.Rng, state.Step, out TacticalOrderReason refusal))
                {
                    state.OrderLog.Add(new AcceptedOrder(state.Step, order));
                    accepted++;
                }
                else
                {
                    state.Record("log.order.rejected", (int)refusal);
                }
            }

            return $"accepted:{accepted}";
        }
    }

    /// <summary>Moves everybody in flight by one step.</summary>
    public sealed class MovementResolutionPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.MovementResolution;

        public string Run(StepContext context)
        {
            IReadOnlyList<NoiseEvent> made = MovementSystem.Advance(context.State);

            // The noises were already appended to NoiseInFlight by MovementSystem; this
            // only reports how many, because they are propagated in the next phase and
            // appending them here too would double them.
            return $"steps:{made.Count}";
        }
    }

    /// <summary>Spends one step on everybody's action in flight.</summary>
    public sealed class ActionProgressPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.ActionProgress;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;
            int completed = 0;
            int lethal = 0;

            foreach (TacticalActor actor in state.SortedActors)
            {
                if (actor.Action is null || actor.Action.Kind == TacticalActionKind.Move)
                    continue;

                ActionProgress progress = ActionSystem.Advance(state, actor, context.Rng);

                if (!progress.Completed)
                    continue;

                completed++;

                if (progress.Lethal)
                    lethal++;
            }

            return $"completed:{completed}:lethal:{lethal}";
        }
    }

    /// <summary>
    /// Works out who heard the noises made this step, and feeds them into suspicion.
    /// </summary>
    /// <remarks>
    /// After movement and actions, so that everything they made this step is in flight
    /// before any of it is heard. Before perception, so that a guard hears the noise and
    /// then looks — and the noise counts towards the alarm this step rather than the
    /// next one, which is what makes "get out before they hear it" a real deadline.
    /// </remarks>
    public sealed class NoisePropagationPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.NoisePropagation;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;

            List<NoiseEvent> inFlight = new(state.NoiseInFlight);
            state.NoiseInFlight.Clear();

            var byId = new Dictionary<int, TacticalActor>();
            foreach (TacticalActor actor in state.SortedActors)
                byId[actor.Id.Value] = actor;

            int listeners = 0;

            foreach (NoiseEvent noise in inFlight)
            {
                NoiseSystem.Propagate(state.Layout, state.SortedActors, state.Doors, state.Lights, noise);
                listeners += NoiseSystem.ApplyToSuspicion(noise, byId);

                // A noise the whole site reacts to — the alarm itself — bypasses the NPCs
                // entirely. That is the narrow exception the brief's "driven by NPC
                // awareness" allows, and it exists because an alarm nobody is aware of
                // would not be an alarm.
                state.Alarm.Raise(NoiseSystem.SiteAlarmCost(noise.ProfileId));

                state.NoiseLog.Add(new NoiseLogEntry(
                    noise.Step,
                    noise.SourceActorId,
                    noise.SourceKey,
                    noise.ProfileId,
                    noise.Origin,
                    noise.HeardBy.ToArray()));
            }

            return $"noises:{inFlight.Count}:listeners:{listeners}";
        }
    }

    /// <summary>Resolves what every observer can see of every other actor.</summary>
    /// <remarks>
    /// After noise, so a guard who heard something turns towards it before perception
    /// runs and can see along that new facing. Before planning, so the planner has this
    /// step's sightings rather than the last one's — otherwise a guard would always be
    /// one reaction behind, which reads as slow rather than as deliberate.
    /// </remarks>
    public sealed class PerceptionPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.Perception;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;
            IReadOnlyList<TacticalActor> actors = state.SortedActors;

            var byId = new Dictionary<int, TacticalActor>();
            foreach (TacticalActor actor in actors)
                byId[actor.Id.Value] = actor;

            int noticed = 0;
            int sighted = 0;
            int observed = ObserveOccupiedRooms(state);

            foreach (TacticalActor observer in actors)
            {
                if (!observer.CanAct)
                    continue;

                observer.Suspicion.BeginStep();

                int gainRate = SuspicionGainRate(observer);

                foreach (TacticalActor target in actors)
                {
                    if (target.Id == observer.Id || !target.CanAct)
                        continue;

                    // Only a guard watching an agent is an observation the site can act
                    // on. Guards do not look for other guards — every pair of guards
                    // seeing each other would fill every suspicion meter in the building
                    // within seconds — and the squad is not suspicious of the building
                    // it is standing in.
                    if (observer.IsGuard)
                    {
                        if (!target.IsAgent)
                            continue;

                        Perception perception = PerceptionSystem.CanPerceive(state.Layout, state.Lights, observer, target, state.Doors);

                        if (PerceptionSystem.Apply(perception, byId, gainRate, state.Step) > 0)
                            noticed++;
                    }
                    else if (observer.IsAgent && target.IsGuard)
                    {
                        // The other direction, and the one the player actually plays
                        // against. Same arithmetic, opposite bookkeeping: the squad
                        // notices the guard and the guard's suspicion is untouched.
                        // Without this the player's view of the building would be a blank
                        // set of rooms, because nothing ever wrote to a squad member's
                        // memory, and the fog-of-war layer would be untestable rather
                        // than merely unproven.
                        Perception seen = PerceptionSystem.CanPerceive(state.Layout, state.Lights, observer, target, state.Doors);

                        if (PerceptionSystem.RecordSighting(seen, byId, state.Step))
                            sighted++;
                    }
                }
            }

            // Decay runs after every observation in the step, never interleaved with
            // them, so a guard who is being fed perception this step does not also lose
            // it. That ordering is the whole reason BeginStep exists.
            foreach (TacticalActor observer in actors)
                observer.Suspicion.Decay(SuspicionDecayRate(observer));

            return $"noticed:{noticed}:sighted:{sighted}:rooms:{observed}";
        }

        /// <summary>
        /// Records every room the squad can see into, and returns how many were new.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the only thing that ever opens a room's contents.</b>
        /// <see cref="SiteLayout.ObserveRoom"/> generates a room's interior on first
        /// sight and refuses to produce one for a room that has not been observed, and
        /// nothing in the step loop called it. Every room stayed unobserved for the
        /// whole mission: the player could not see the floor they were standing on, and
        /// a Recon objective — which counts observed rooms — was arithmetically
        /// impossible.
        /// </para>
        /// <para>
        /// <b>Standing in a room observes it, and so does seeing through a door that
        /// is standing open.</b> Not through a shut, locked or barricaded one. That test
        /// is deliberately the same one
        /// (<see cref="PerceptionSystem.CountBlockingDoors"/>) the guards are judged by:
        /// if the two disagreed, the player would see a room on the map that a guard
        /// standing in it cannot see into, which is exactly the sort of lie a fog layer
        /// must never tell.
        /// </para>
        /// <para>
        /// Runs here, inside the perception phase, because "what the player can see" and
        /// "what the guards can see" are the same question asked in opposite directions,
        /// and answering them in different phases would let the two disagree within a
        /// single step.
        /// </para>
        /// </remarks>
        private static int ObserveOccupiedRooms(TacticalState state)
        {
            var pending = new List<SiteRoomId>();

            foreach (TacticalActor member in state.Squad)
            {
                SiteRoom? here = state.RoomOf(member);

                if (here is null)
                    continue;

                if (!state.Layout.IsRoomObserved(here.Id))
                    pending.Add(here.Id);

                foreach (SiteConnection connection in state.Layout.ConnectionsAt(here.Id))
                {
                    if (connection.IsVertical || BlocksSight(state, connection))
                        continue;

                    SiteRoomId beyond = connection.Other(here.Id);

                    if (!state.Layout.IsRoomObserved(beyond))
                        pending.Add(beyond);
                }
            }

            foreach (SiteRoomId room in pending.Distinct())
                state.Layout.ObserveRoom(room);

            return pending.Count;
        }

        /// <summary>
        /// Whether this door is standing in the way right now.
        /// </summary>
        /// <remarks>
        /// Open means open, whatever the door is made of; anything else falls back to the
        /// type's <c>blocks_vision</c>, which is what an interior window is judged by.
        /// </remarks>
        private static bool BlocksSight(TacticalState state, SiteConnection connection)
        {
            if (state.Doors.TryGetValue(connection.Id, out ConnectionState live) && live == ConnectionState.Open)
                return false;

            return connection.BlocksVision;
        }

        private static int SuspicionGainRate(TacticalActor actor)
            => SimulationRules.GuardArchetypeFor(actor.GuardArchetypeId)?.SuspicionGainRate
               ?? DefaultSuspicionGainRate;

        private static int SuspicionDecayRate(TacticalActor actor)
            => SimulationRules.GuardArchetypeFor(actor.GuardArchetypeId)?.SuspicionDecayRate
               ?? DefaultSuspicionDecayRate;
    }

    /// <summary>
    /// Decides what the NPCs want and starts their orders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stage 4d replaced the heuristic this phase used to run with the GOAP director.
    /// The old code picked an <see cref="NpcIntent"/> from each NPC's suspicion and the
    /// alarm band and issued an order from it; the new code builds a world state per NPC
    /// from what that NPC knows, selects a goal by priority, searches for a plan within a
    /// node budget, and executes the first action of it.
    /// </para>
    /// <para>
    /// <b>Replanning is event-driven.</b> Nothing here runs a search for every NPC every
    /// step. An NPC is added to the director's queue only when something happened to it
    /// — a perception change, a suspicion band crossing, a noise, an alarm band change —
    /// and the queue is drained under a per-step budget with a fair round-robin so no NPC
    /// is starved. That is the whole reason this stays inside the brief's 2 ms.
    /// </para>
    /// <para>
    /// <b>One order per NPC per step</b>, preserved from stage 4c: two orders issued in
    /// the same step would both be resolved by the next step's
    /// <c>ApplyQueuedCommands</c> and the second would silently overwrite the first.
    /// </para>
    /// </remarks>
    public sealed class NpcPlanningPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.NpcPlanning;

        /// <remarks>
        /// <para>
        /// The site's NPCs plan here, and so does the squad — in that order, and in the
        /// same phase, because "the guards decide, then the player's team reacts to what
        /// they decided" is a dependency and not an implementation detail. Splitting them
        /// across phases would let a guard's plan and a squad order issued in response to
        /// it resolve in a different relative order on a replay.
        /// </para>
        /// <para>
        /// The squad's half is skipped entirely when the mission has no composition,
        /// which is the case for every mission built before stage 4e. That is why the
        /// return string counts the two halves separately rather than reporting one
        /// number: a mission report saying "ordered:0" would be ambiguous between "the
        /// guards all stood still" and "there is no squad here".
        /// </para>
        /// </remarks>
        public string Run(StepContext context)
        {
            int ordered = GoapTick.Run(context.State, context.State.Step, context.PlannerRng);

            GoapDirector director = GoapRuntime.DirectorFor(context.State);

            int squad = 0;

            if (context.State.Composition is { } composition)
                squad = context.State.Control.Run(context.State, composition, context.Rng, context.State.CommandPost);

            return $"ordered:{ordered}:planned:{director.LastPlanCount}:noplan:{director.LastNoPlanCount}:squad:{squad}";
        }
    }

    /// <summary>
    /// Recomputes the site alarm from what the NPCs know, and applies its band.
    /// </summary>
    /// <remarks>
    /// After perception and planning so that everything the NPCs learned this step is
    /// counted, and before damage so that an alarm raised by a firefight reflects the
    /// firefight rather than the step before it.
    /// </remarks>
    public sealed class AlarmUpdatePhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.AlarmUpdate;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;
            AlarmBand before = state.Alarm.Band;

            state.Alarm.Update(state.SortedActors, state.Step);

            // The command post is checked in the same phase as the alarm it depends on,
            // after the update: a post can only be compromised by a responder sent to
            // find the team, and whether one was sent is a function of the band that was
            // just computed.
            bool postLost = ProjectSpy.Core.Squad.CommandPostSystem.Advance(state, state.CommandPost);

            AlarmBand after = state.Alarm.Band;
            if (after == before)
                return postLost ? $"steady:{after}:post-lost" : $"steady:{after}";

            if (after > before)
            {
                AlarmBandChange change = AlarmSystem.OnBandChanged(state, after);
                state.GuardSpeedMultiplierPercent = change.SpeedMultiplierPercent;

                state.Record("log.alarm.raised", (int)after, change.DoorsClosed, change.RespondersReleased);

                if (change.ForcesExtraction && !state.IsOver)
                {
                    state.Outcome = MissionOutcome.Burned;
                    state.Record("log.mission.burned");
                }

                return $"raised:{before}->{after}";
            }

            AlarmSystem.OnBandRelaxed(state, after);
            state.Record("log.alarm.calmed", (int)after);
            return $"calmed:{before}->{after}";
        }
    }

    /// <summary>Advances bleed-out timers and restores stamina.</summary>
    /// <remarks>
    /// After alarm, so that dying this step raises the alarm first — a guard who hears a
    /// body fall reacts to the noise, and only then finds out there is a body. Reversed,
    /// the site would be calm about a killing it heard nothing of.
    /// </remarks>
    public sealed class DamageAndStatusPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.DamageAndStatus;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;

            DamageSystem.AdvanceBleedOut(state);

            int rested = 0;

            foreach (TacticalActor actor in state.SortedActors)
            {
                if (!actor.CanAct || actor.Stamina >= MaxStamina)
                    continue;

                actor.Stamina = Math.Min(MaxStamina, actor.Stamina + StaminaPerStep);
                rested++;
            }

            return $"rested:{rested}";
        }
    }

    /// <summary>Progresses the objective and decides whether the mission is over.</summary>
    /// <remarks>
    /// Last of the changing phases, so that an objective completed this step is
    /// recognised before events are emitted — otherwise the step that completes the hack
    /// would report nothing and the next step would announce it as though it had happened
    /// then.
    /// </remarks>
    public sealed class ObjectiveCheckPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.ObjectiveCheck;

        public string Run(StepContext context)
        {
            TacticalState state = context.State;

            if (state.IsOver)
                return state.Outcome.ToString();

            // The stage-4e objective system runs first and owns the decision about
            // whether the job is done. It writes to `ObjectiveOutcome`, not to
            // `Objective`, because the legacy progress bar cannot express "failed" or
            // "complete only once you are out of the building" — which two of the six
            // objective types require. The legacy bar is then mirrored from the outcome
            // so that anything still reading it sees a coherent number.
            ProjectSpy.Core.Squad.ObjectiveOutcome outcome = state.ObjectiveOutcome;
            bool decided = state.Composition is not null
                && ProjectSpy.Core.Squad.ObjectiveSystem.Advance(
                    state, outcome, state.Composition, context.Rng);

            MirrorLegacy(state, outcome);

            // A failed objective ends the mission. There is no reading of "the alarm went
            // past this objective's tolerance" under which the right next action is to
            // keep playing: the job is already off, and every further step is a step the
            // player is spending on a mission whose debrief will say it failed. It also
            // used to leave the mission genuinely open — a StealData that failed on a
            // tier-2 site sat at "in progress" until the harness's step ceiling, four
            // thousand steps after everybody had stopped doing anything about it, and
            // was reported as a mission that never ended rather than as a failed one.
            if (outcome.IsFailed && !state.IsOver)
            {
                state.Outcome = state.AbortCalled
                    ? MissionOutcome.Aborted
                    : MissionOutcome.Burned;
                DamageSystem.ResolveMissionEnd(state, ExtractionRooms(state));
                state.Record("log.mission.burned", (int)outcome.Failure);
                return $"objective:failed:{(int)outcome.Failure}:{state.Outcome}";
            }

            bool complete = outcome.IsComplete || (!state.IsOver && state.Objective.IsComplete);

            if (!complete)
                return $"objective:{state.Objective.Percent}";

            // Complete and the whole team is at an extraction point: the mission ends
            // here, and DamageSystem decides each actor's fate from where they stand.
            if (AllSquadExtracted(state))
            {
                state.Outcome = state.AbortCalled
                    ? MissionOutcome.Aborted
                    : MissionOutcome.Exfiltrated;
                DamageSystem.ResolveMissionEnd(state, ExtractionRooms(state));
                state.Record(
                    state.AbortCalled ? "log.mission.aborted" : "log.mission.exfiltrated");
                return state.Outcome.ToString();
            }

            return $"objective:complete:extraction:pending{(decided ? ":decided" : string.Empty)}";
        }

        /// <summary>
        /// Copies the stage-4e outcome onto the legacy progress bar.
        /// </summary>
        /// <remarks>
        /// The bar predates the six objective types and is still what
        /// <see cref="EventEmitPhase"/> and the HUD read. Mirroring rather than
        /// replacing keeps one place that answers "how far along is this mission" for
        /// everything that has not been updated yet, and a failed objective shows as a
        /// full bar rather than as a stalled one — a stalled bar would read as "keep
        /// working on it", which is the wrong instruction for a PlantBug that is already
        /// dead.
        /// </remarks>
        private static void MirrorLegacy(TacticalState state, ProjectSpy.Core.Squad.ObjectiveOutcome outcome)
        {
            if (outcome.IsFailed || outcome.IsComplete)
            {
                state.Objective.SpentSteps = state.Objective.TotalSteps;
                state.Objective.IsComplete = true;
                return;
            }

            state.Objective.SpentSteps = outcome.WorkSteps;
        }

        /// <summary>
        /// Every room a member of the squad may leave by.
        /// </summary>
        /// <remarks>
        /// Delegated rather than reimplemented. This question — the layout's extraction
        /// rooms plus any the Handler's called-in vehicle opened — used to be answered
        /// here and again in <see cref="ProjectSpy.Core.Squad.ObjectiveSystem"/>, and the
        /// two answers disagreed: the ending counted the vehicle, the objective did not.
        /// Reading the post rather than the layout is also what leaves the layout alone:
        /// the building regenerates from its seed, and a save that mutated its extraction
        /// list would no longer match the structure it regenerates.
        /// </remarks>
        private static IReadOnlyList<SiteRoomId> ExtractionRooms(TacticalState state)
            => ProjectSpy.Core.Squad.ObjectiveSystem.ExtractionRooms(state);

        private static bool AllSquadExtracted(TacticalState state)
        {
            IReadOnlyList<SiteRoomId> rooms = ExtractionRooms(state);

            foreach (TacticalActor actor in state.Squad)
            {
                SiteRoom? here = state.RoomOf(actor);

                // See TacticalState.IsStillInside for why: somebody in the forward post,
                // or somebody whose role permits no move order at all, is not inside the
                // building and does not hold the mission open.
                if (!state.IsStillInside(actor))
                    continue;

                if (here is null)
                    return false;

                // Carried or downed members travel with whoever is carrying them, so
                // they are where their carrier is. Counting them as "not extracted"
                // because they are lying in a room nobody is standing in would make a
                // successful mule run read as a failure.
                if (!rooms.Contains(here.Id))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Publishes everything this step produced.
    /// </summary>
    /// <remarks>
    /// Last, always. Every earlier phase has already written into the mission log and
    /// the noise log by the time this runs, so the events a subscriber sees describe a
    /// finished step rather than one half-applied — which is the same reason the
    /// strategic pipeline flushes at the end of a tick.
    /// </remarks>
    public sealed class EventEmitPhase : ITacticalStepPhase
    {
        public TacticalStepPhase Phase => TacticalStepPhase.EventEmit;

        public string Run(StepContext context)
        {
            context.State.Record("step.completed", (int)context.State.Step);
            return $"step:{context.State.Step}";
        }
    }

    /// <summary>Top of the stamina scale.</summary>
    private const int MaxStamina = 100;

    /// <summary>
    /// Stamina restored per step while an actor is doing nothing strenuous.
    /// </summary>
    /// <remarks>
    /// Structural. Every 100 ms step of standing still gives back one stamina, so a
    /// minute of rest is a full bar — which is the timescale the whole design speaks in,
    /// since a mission is measured in minutes and in steps and nothing else.
    /// </remarks>
    private const int StaminaPerStep = 1;

    /// <summary>Suspicion gain used when a guard's archetype is unavailable.</summary>
    private const int DefaultSuspicionGainRate = 14;

    /// <summary>Suspicion decay used when a guard's archetype is unavailable.</summary>
    private const int DefaultSuspicionDecayRate = 6;
}
