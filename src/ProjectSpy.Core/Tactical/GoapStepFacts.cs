using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// The facts about a mission that every NPC needs this step, materialised once.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is a performance fix with a correctness reason behind it.</b> Both
/// <see cref="TacticalState.SortedActors"/> and <see cref="SiteLayout.Guards"/> build a
/// fresh list on every access — the layout's because it has to combine the main site
/// with a forward post, and the state's because it must be sorted. Asking for either
/// inside a loop over thirty NPCs therefore allocated and sorted thirty times per step,
/// which measured as the planning layer tripling the cost of a tactical step even though
/// the search itself was under a microsecond.
/// </para>
/// <para>
/// Resolving it by hoisting the list into a local would have fixed the loop but left the
/// trap armed for the next caller. Holding the per-step facts in a named type makes the
/// cost visible at the call site instead of hiding behind a property that looks free.
/// </para>
/// <para>
/// <b>Built once per step, from the mission, before any planning happens.</b> Nothing
/// here is cached across steps, so it cannot go stale — the common way a "just an
/// optimisation" turns into a bug where a spawned responder is invisible to the planner.
/// </para>
/// </remarks>
public sealed class GoapStepFacts
{
    /// <summary>Every actor, in ascending id order.</summary>
    public IReadOnlyList<TacticalActor> Actors { get; }

    /// <summary>Every guard on the site, in generated order.</summary>
    public IReadOnlyList<SiteGuard> Guards { get; }

    /// <summary>Each guard by its id.</summary>
    public IReadOnlyDictionary<SiteGuardId, SiteGuard> GuardsById { get; }

    /// <summary>Guards that are down, by floor index.</summary>
    /// <remarks>
    /// Keyed by floor because that is the limit of what a guard can see: a casualty one
    /// floor away is not this guard's business, and a casualty on its own floor is. Held
    /// as a set so the "can this guard see a downed colleague" test is a lookup rather
    /// than a scan of every guard for every guard.
    /// </remarks>
    public IReadOnlyDictionary<int, HashSet<TacticalActorId>> DownedGuardsByFloor { get; }

    private GoapStepFacts(
        IReadOnlyList<TacticalActor> actors,
        IReadOnlyList<SiteGuard> guards,
        IReadOnlyDictionary<SiteGuardId, SiteGuard> guardsById,
        IReadOnlyDictionary<int, HashSet<TacticalActorId>> downedByFloor)
    {
        Actors = actors;
        Guards = guards;
        GuardsById = guardsById;
        DownedGuardsByFloor = downedByFloor;
    }

    /// <summary>Collects the facts for one step.</summary>
    /// <param name="state">The mission being resolved.</param>
    public static GoapStepFacts Collect(TacticalState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        IReadOnlyList<TacticalActor> actors = state.SortedActors;
        IReadOnlyList<SiteGuard> guards = state.Layout.Guards;

        var byId = new Dictionary<SiteGuardId, SiteGuard>(guards.Count);

        foreach (SiteGuard guard in guards)
            byId[guard.Id] = guard;

        var downed = new Dictionary<int, HashSet<TacticalActorId>>();

        foreach (TacticalActor actor in actors)
        {
            if (!actor.IsGuard || actor.Condition != ActorCondition.Downed)
                continue;

            if (!downed.TryGetValue(actor.Position.FloorIndex, out HashSet<TacticalActorId>? onFloor))
            {
                onFloor = new HashSet<TacticalActorId>();
                downed[actor.Position.FloorIndex] = onFloor;
            }

            onFloor.Add(actor.Id);
        }

        return new GoapStepFacts(actors, guards, byId, downed);
    }

    /// <summary>
    /// True when this guard has a downed colleague on its own floor.
    /// </summary>
    /// <remarks>
    /// The floor limit is the whole no-omniscience property for this event. Without it a
    /// guard learns about a casualty anywhere in the building, and every guard in the
    /// site reacts to a single death at once — which is both wrong and extremely hard to
    /// spot, because everything each guard then does looks like a reasonable response to
    /// something it should not know about.
    /// </remarks>
    public bool CanSeeDownedAlly(TacticalActor actor)
    {
        if (!actor.IsGuard)
            return false;

        return DownedGuardsByFloor.TryGetValue(actor.Position.FloorIndex, out HashSet<TacticalActorId>? onFloor)
               && !onFloor.Contains(actor.Id);
    }

    /// <summary>True when this guard walks a route rather than holding one room.</summary>
    public bool WalksAPatrol(TacticalActor actor)
    {
        if (!actor.IsGuard)
            return false;

        return GuardsById.TryGetValue(actor.GuardId, out SiteGuard? guard) && !guard.IsStationary;
    }
}