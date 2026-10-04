using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Health, injury, going down, bleeding out, dying, and being taken alive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Downed is not dead, and the distinction is the point of the whole model.</b> At
/// zero health an actor becomes <see cref="ActorCondition.Downed"/> with a bleed-out
/// timer, and an ally can stabilise them. Only the timer reaching zero is death. That
/// makes "leave them here" a choice with a deadline rather than an instant consequence,
/// which is what the brief asks for when it says a downed agent left behind is
/// captured rather than killed.
/// </para>
/// <para>
/// <b>Capture is not death.</b> An agent left behind when the mission ends goes into
/// enemy hands, goes to a holding site, and is the subject of a rescue mission with a
/// countdown that starts ticking in the base. A dead agent is a name that comes off the
/// roster. Modelling both with one flag would make the game's most recoverable failure
/// and its most permanent one indistinguishable in the debrief.
/// </para>
/// <para>
/// <b>Death is rare.</b> Bleed-out is measured in steps and only runs on an actor who is
/// down and unattended, so lethality is a function of how long the player spends looking
/// the other way — not of how much damage a fight deals.
/// </para>
/// </remarks>
public static class DamageSystem
{
    /// <summary>
    /// Steps an unconscious actor has before they die.
    /// </summary>
    /// <remarks>
    /// Ninety seconds of simulated time. Long enough that an ally can cross a building
    /// and carry them out, short enough that hesitating has a cost. It is structural
    /// rather than tuned because there is no column for it: it is the shape of the
    /// decision, not its balance.
    /// </remarks>
    public const long BleedOutSteps = 900;

    /// <summary>Health restored when an actor is stabilised.</summary>
    public const int StabiliseAmount = 10;

    /// <summary>
    /// Applies the outcome of taking damage: go down, bleed out, or die outright.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="actor">Who was hit.</param>
    /// <param name="wasLethal">
    /// True when the act that caused this was marked lethal in the table. A lethal act
    /// can kill outright; a non-lethal one can only knock somebody down.
    /// </param>
    public static void ApplyOutcome(TacticalState state, TacticalActor actor, bool wasLethal)
    {
        if (actor.Condition is ActorCondition.Dead or ActorCondition.Captured)
            return;

        if (actor.Health > 0)
        {
            actor.Stamina = Math.Max(0, actor.Stamina - InjuryStaminaCost);
            return;
        }

        actor.Health = 0;

        // A lethal act on an actor already at the edge finishes them. A non-lethal one
        // only ever puts them on the floor, because the table said so — which is the
        // whole point of the is_lethal column existing.
        if (wasLethal && actor.IsGuard)
        {
            actor.Condition = ActorCondition.Dead;
            actor.BleedOutStepsRemaining = 0;
            state.Record("log.actor.dead", actor.Id.Value);
            return;
        }

        actor.Condition = ActorCondition.Downed;
        actor.BleedOutStepsRemaining = BleedOutSteps;
        state.Record("log.actor.downed", actor.Id.Value);
    }

    /// <summary>
    /// Advances every bleed-out timer by one step.
    /// </summary>
    /// <remarks>
    /// The only place an actor dies from bleeding. Keeping it in one method means the
    /// timer can be reasoned about as a single countdown and there is no second path
    /// that could kill an actor without one having been set.
    /// </remarks>
    public static void AdvanceBleedOut(TacticalState state)
    {
        foreach (TacticalActor actor in state.SortedActors)
        {
            if (actor.Condition != ActorCondition.Downed)
                continue;

            actor.BleedOutStepsRemaining--;

            if (actor.BleedOutStepsRemaining > 0)
                continue;

            actor.Condition = ActorCondition.Dead;
            actor.BleedOutStepsRemaining = 0;
            state.Record("log.actor.bled_out", actor.Id.Value);
        }
    }

    /// <summary>
    /// Brings a downed actor back far enough to be carried, without waking them.
    /// </summary>
    /// <remarks>
    /// Distinct from a revive: stabilising stops the clock and buys time, reviving puts
    /// somebody back on their feet. Collapsing the two would mean a medic standing over
    /// a bleeding agent had to choose between saving them and waking them, and the
    /// correct play would always be to revive — which removes the decision.
    /// </remarks>
    public static bool Stabilise(TacticalState state, TacticalActor actor)
    {
        if (actor.Condition != ActorCondition.Downed)
            return false;

        actor.Health = Math.Min(actor.MaxHealth, actor.Health + StabiliseAmount);
        actor.BleedOutStepsRemaining = BleedOutSteps;
        state.Record("log.actor.stabilised", actor.Id.Value);
        return true;
    }

    /// <summary>
    /// Applies the end-of-mission fate of every actor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rule the brief states most explicitly: a downed agent left behind is
    /// <b>captured</b>, not killed, unless the damage that brought them down was lethal.
    /// Dead stays dead; anything alive who is not at an extraction point goes into
    /// enemy hands; everybody else walks out.
    /// </para>
    /// <para>
    /// Standing in an extraction room is what counts, not being next to the door: a
    /// team that has dragged somebody to the room they have to leave from has done the
    /// hard part, and making them walk an extra step to be considered safe would make
    /// carrying somebody out strictly worse than leaving them.
    /// </para>
    /// </remarks>
    public static void ResolveMissionEnd(TacticalState state, IReadOnlyList<SiteRoomId>? extractionRoomIds)
    {
        foreach (TacticalActor actor in state.SortedActors)
        {
            switch (actor.Condition)
            {
                case ActorCondition.Dead:
                    continue;

                case ActorCondition.Captured:
                    continue;

                default:
                    break;
            }

            // The same predicate the ending uses. Deciding "extracted" two different ways
            // in two different files is how a clean exfiltration gets reported as a
            // capture: the mission ended because the radio operator was not counted as
            // inside, and then the resolution counted them as not at an exit and handed
            // them to the enemy anyway.
            bool extracted = !state.IsStillInside(actor)
                || (extractionRoomIds is not null && IsAtExtraction(state, actor, extractionRoomIds));

            if (extracted)
            {
                actor.Condition = ActorCondition.Active;
                actor.BleedOutStepsRemaining = 0;
                state.Record("log.actor.exfiltrated", actor.Id.Value);
                continue;
            }

            if (!actor.IsAgent)
                continue;

            // The whole point of the distinction: still breathing, in enemy hands, and
            // the subject of a rescue mission rather than a eulogy.
            actor.Condition = ActorCondition.Captured;
            actor.BleedOutStepsRemaining = 0;
            actor.IsCarried = false;
            state.Record("log.actor.captured", actor.Id.Value);
        }
    }

    /// <summary>True when an actor is standing in one of the extraction rooms.</summary>
    private static bool IsAtExtraction(TacticalState state, TacticalActor actor, IReadOnlyList<SiteRoomId> rooms)
    {
        SiteRoom? here = state.RoomOf(actor);
        if (here is null)
            return false;

        foreach (SiteRoomId id in rooms)
        {
            if (id == here.Id)
                return true;
        }

        return false;
    }

    /// <summary>Stamina an actor loses for every hit they take.</summary>
    private const int InjuryStaminaCost = 5;
}
