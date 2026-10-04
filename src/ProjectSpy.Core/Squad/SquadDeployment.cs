using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// Turns a composition the player has built into a mission that can be played.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where Dispatch becomes Insert.</b> Everything above this class is a
/// description — a composition, a validation result, a phase name. This is the point at
/// which a squad becomes five actors standing in a doorway, and it is deliberately the
/// only place that happens, so that "what does a mission start from" has one answer.
/// </para>
/// <para>
/// <b>Nothing is created until validation passes.</b> A refused dispatch does not
/// allocate a layout, does not allocate actor ids, and does not touch the RNG streams. A
/// player experimenting on the dispatch screen would otherwise burn mission seeds and
/// perturb every stream in the world, which is the kind of bug that only shows up as
/// "the same seed gives a different site after I looked at the screen".
/// </para>
/// </remarks>
public static class SquadDeployment
{
    /// <summary>
    /// Validates a composition and, if it is legal, builds the mission to play.
    /// </summary>
    /// <param name="world">The world the agents are drawn from.</param>
    /// <param name="composition">The squad as the player built it.</param>
    /// <param name="missionId">The mission being dispatched.</param>
    /// <param name="layout">The generated site.</param>
    /// <param name="controlledAgentId">
    /// Who the player takes direct control of. Null leaves the mission without a
    /// controlled agent, which the brief forbids — so the result reports
    /// <see cref="DispatchRefusalReason.UnknownAgent"/> rather than silently picking one.
    /// </param>
    /// <param name="recordRolls">Whether the debug roll log is kept.</param>
    /// <param name="mission">The mission, when the dispatch succeeded.</param>
    /// <param name="refusals">Every reason it failed, when it did.</param>
    /// <returns>True when the mission was built.</returns>
    public static bool TryDispatch(
        WorldState world,
        SquadComposition composition,
        int missionId,
        SiteLayout layout,
        AgentId? controlledAgentId,
        bool recordRolls,
        out TacticalState? mission,
        out IReadOnlyList<DispatchRefusal> refusals)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (composition is null) throw new ArgumentNullException(nameof(composition));
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        mission = null;

        bool siteHasPost = layout.ForwardPostRoomId.IsValid;
        refusals = composition.Validate(world, siteHasPost);

        AgentId? chosen = null;

        if (controlledAgentId is { } candidate)
            chosen = candidate;
        else
            refusals = Add(refusals, DispatchRefusal.Squad(
                DispatchRefusalReason.UnknownAgent));

        if (chosen is { } controlled && composition.MemberFor(controlled) is null)
            refusals = Add(refusals, DispatchRefusal.About(
                DispatchRefusalReason.UnknownAgent, controlled));

        // An advisory refusal — an unstaffed post on a site that has one — is reported
        // and then dispatched through. Forgoing the radio operator is a choice the
        // player is entitled to make, and a dispatch screen that would not let them
        // would be deciding their mission for them.
        if (refusals.Any(r => r.Reason != DispatchRefusalReason.UnstaffedCommandPost))
            return false;

        // Past this point the dispatch is committed, and the RNG streams start moving.
        // Everything above is pure, which is what makes the dispatch screen free to
        // re-validate on every change.
        var roster = new List<Agent>(composition.Members.Count);

        foreach (SquadMember member in composition.Members)
        {
            Agent? agent = world.Agents.TryGetValue(member.AgentId, out Agent? found)
                ? found
                : null;

            if (agent is not null)
                roster.Add(agent);
        }

        TacticalState state = TacticalMission.Create(layout, roster, missionId, world.Clock.Current);

        state.Composition = composition;
        state.ObjectiveOutcome = ObjectiveSystem.Create(composition.ObjectiveType);
        state.RecordRolls = recordRolls;

        state.Control.Begin(composition);
        state.Control.SwitchTo(chosen!.Value);

        StaffCommandPost(state, composition, layout);
        SeedObjective(state, layout);

        mission = state;
        return true;
    }

    /// <summary>
    /// Puts a Handler in the post when the composition has one and the site has a post.
    /// </summary>
    /// <remarks>
    /// A composition whose Handler is valid but whose site has no post was already
    /// refused by validation, so this can assume the two agree. Writing the handler id
    /// here rather than letting the post find the actor itself is what makes the post's
    /// state a saveable fact rather than a search that could find a different answer
    /// after a load.
    /// </remarks>
    private static void StaffCommandPost(
        TacticalState state, SquadComposition composition, SiteLayout layout)
    {
        if (!layout.ForwardPostRoomId.IsValid)
        {
            state.CommandPost.HandlerId = null;
            return;
        }

        state.CommandPost.HandlerId = composition.CommandPostHandler;

        if (state.CommandPost.HandlerId is null)
        {
            foreach (SquadMember member in composition.Members)
            {
                if (!member.RequiresCommandPost)
                    continue;

                state.CommandPost.HandlerId = member.AgentId;
                break;
            }
        }

        MovePostRolesToThePost(state, composition, layout);
    }

    /// <summary>
    /// Puts every post-only member in the forward post instead of at the entrance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The radio operator is the member the mission needs most and the one the player can
    /// give the fewest orders to: <c>agent_role</c> gives <c>role.handler</c>
    /// <c>HoldPosition, RequestExtraction, UseGadget, Regroup, Abort</c> and no
    /// <c>MoveTo</c>, because it is a radio operator and not an infiltrator.
    /// </para>
    /// <para>
    /// That only works if the radio operator is at the radio. Deployed at the entrance
    /// like everybody else, the handler stood in the ground-floor lobby for the whole
    /// mission while the rest of the squad went upstairs — and because the mission ends
    /// when the <em>whole</em> squad is at an extraction point, and a handler can never
    /// be told to walk to one, <b>no mission with a handler could ever finish</b>. Not
    /// rarely: never, on every site, with every policy.
    /// </para>
    /// <para>
    /// A member of a post-only role who is left in the building is a bug, not a choice;
    /// forgoing the operator is expressed by dispatching a composition without one.
    /// </para>
    /// </remarks>
    private static void MovePostRolesToThePost(
        TacticalState state, SquadComposition composition, SiteLayout layout)
    {
        SiteRoom? post = layout.Find(layout.ForwardPostRoomId);

        if (post is null)
            return;

        foreach (SquadMember member in composition.Members)
        {
            if (!member.RequiresCommandPost)
                continue;

            TacticalActor? actor = state.AgentActor(member.AgentId);

            if (actor is not null)
                actor.Position = new TacticalPosition(post.FloorIndex, post.StartX);
        }
    }

    /// <summary>
    /// Points the objective at the interactable the mission is really about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <see cref="InteractableType.Objective"/> interactable in the objective room,
    /// and otherwise a terminal there.
    /// </para>
    /// <para>
    /// <b>The room is not a filter that was forgotten — it is the whole answer.</b> An
    /// earlier version took the first Objective-or-Terminal anywhere in the building,
    /// and on a two-floor site that usually found a terminal on the ground floor while
    /// the objective room was upstairs. The mission then handed the squad a hackable
    /// thing in the lobby, and the objective completed without anyone ever climbing the
    /// stairs — which made StealData a mission about walking in a circle.
    /// </para>
    /// <para>
    /// The off-site fallback is a real answer rather than a stub, and it is the last
    /// resort rather than the first: a template that generates no terminal in its
    /// objective room is still playable, because the alternative is a mission that
    /// refuses to start over a missing decoration. It is deliberately not a success
    /// though — <see cref="ObjectiveSystem"/> requires the work to happen in the room
    /// holding this interactable, so on such a site the team has to go and find it.
    /// </para>
    /// </remarks>
    private static void SeedObjective(TacticalState state, SiteLayout layout)
    {
        ObjectiveOutcome outcome = state.ObjectiveOutcome;
        outcome.WorkInteractableId = 0;

        SiteRoomId objectiveRoom = layout.ObjectiveRoomId;
        SiteInteractable? anywhere = null;

        foreach (SiteInteractable interactable in layout.Interactables)
        {
            bool usable = interactable.Kind is InteractableType.Objective
                or InteractableType.Terminal;

            if (!usable)
                continue;

            if (interactable.RoomId == objectiveRoom)
            {
                outcome.WorkInteractableId = interactable.Id;
                return;
            }

            // An Objective-kind interactable outranks a Terminal wherever both are found
            // on the fallback, because it is the one the site meant.
            if (anywhere is null || (interactable.Kind == InteractableType.Objective && anywhere.Kind != InteractableType.Objective))
                anywhere = interactable;
        }

        outcome.WorkInteractableId = anywhere?.Id ?? 0;
    }

    /// <summary>
    /// Arms a Sabotage charge's timer from the table.
    /// </summary>
    /// <remarks>
    /// Called once at insert rather than on the first objective step, so the blast
    /// interval a player is shown on the briefing is the interval they get. Starting it
    /// when the team happens to reach the objective room would mean the charge went off
    /// sooner every time the team was slow, which is not what "arm a timer" means.
    /// </remarks>
    public static void ArmTimer(ObjectiveOutcome outcome, TableObjectiveType type)
    {
        if (outcome is null) throw new ArgumentNullException(nameof(outcome));
        if (type != TableObjectiveType.Sabotage)
            return;

        outcome.BlastStepsRemaining = SimulationRules.ObjectiveRuleFor(type)?.BlastIntervalSteps ?? 0;
    }

    /// <summary>Appends a refusal to a list, returning a new list.</summary>
    private static IReadOnlyList<DispatchRefusal> Add(
        IReadOnlyList<DispatchRefusal> existing, DispatchRefusal refusal)
    {
        var combined = new List<DispatchRefusal>(existing.Count + 1);
        combined.AddRange(existing);
        combined.Add(refusal);
        return combined;
    }
}