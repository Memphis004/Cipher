using ProjectSpy.Core.Missions;

// Core keeps its own resolved guard role; the table enum is the declaration side and the
// two cross only through SimulationRules.
using TableGuardRole = ProjectSpy.Tables.GuardRole;
using GuardArchetypeRow = ProjectSpy.Tables.GuardArchetype;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// The three derivations every consumer of a GOAP plan needs: what an NPC can see where
/// it stands, what its priority inputs are, and which archetype tags it plans under.
/// </summary>
/// <remarks>
/// <para>
/// These live in one place because the step pipeline, the debug surface and the director
/// all need all three, and because two implementations of any of them would be two
/// answers to a question the game is played against. The debug surface in particular has
/// to report the <em>same</em> world state the planner searched over, or it would explain
/// a decision the planner never made.
/// </para>
/// <para>
/// <b>What none of them may read.</b> No method here takes the player's position, and no
/// method reads another NPC's memory. The alarm is read, but only its band and level for
/// priority and one boolean for "there is an alarm" — never for anything an NPC would act
/// on. That boundary is what makes the no-omniscience test hold by construction rather
/// than by review.
/// </para>
/// </remarks>
public static class GoapObserver
{
    /// <summary>
    /// What an NPC can see of the room it is standing in, with no perception roll.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped to the actor's own room. A guard standing in a corridor learns about the
    /// corridor's doors; it does not learn about a door two rooms away that the generator
    /// happened to leave open. Widening this to "every door in the building" is the
    /// single change that would turn the whole system omniscient while still looking
    /// correct in every test that exists today.
    /// </para>
    /// <para>
    /// A door counts as this NPC's business only when its own patrol route runs through
    /// it. An open door behind a guard that never walks past it is not that guard's
    /// problem, and treating it as one would send every guard in the building to the same
    /// door.
    /// </para>
    /// </remarks>
    public static GoapLocalObservations ObserveLocally(TacticalState state, TacticalActor actor)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (actor is null) throw new ArgumentNullException(nameof(actor));

        SiteRoom? room = state.RoomOf(actor);

        if (room is null)
            return new GoapLocalObservations { AlarmRaised = state.Alarm.Level > 0 };

        int openDoors = 0;
        int responsibleDoors = 0;

        foreach (SiteConnection connection in state.Layout.ConnectionsAt(room.Id))
        {
            if (connection.Kind is not (SiteConnectionKind.Door or SiteConnectionKind.LockedDoor))
                continue;

            if (!GoapRouteRules.WalksRoute(state.Layout, actor.GuardId, actor.RouteIndex, connection))
                continue;

            responsibleDoors++;

            if (state.StateOf(connection.Id) == ConnectionState.Open)
                openDoors++;
        }

        return new GoapLocalObservations
        {
            DoorOpenAtPost = responsibleDoors > 0 && openDoors > 0,
            SuspiciousDoorsClosed = responsibleDoors > 0 && openDoors == 0,
            InCover = HasCover(state, room, actor),
            RoomIsDark = IsDark(state, room),
            InDangerZone = state.Alarm.Band >= AlarmBand.Alert,
            AlarmRaised = state.Alarm.Level > 0,
        };
    }

    /// <summary>True when there is no working light where this actor stands.</summary>
    private static bool IsDark(TacticalState state, SiteRoom room)
    {
        if (state.Lights is null)
            return false;

        var where = new TacticalPosition(room.FloorIndex, (room.StartX + room.EndX) / 2);
        return state.Lights.LevelAt(state.Layout, where) == SiteLightLevel.Dark;
    }

    /// <summary>
    /// Whether there is something to break line of sight with, from where this actor is.
    /// </summary>
    /// <remarks>
    /// Only the actor's own room's occluders count. A guard in an empty corridor is
    /// exposed whatever else the building contains, which is what stops "is this guard in
    /// cover" from becoming a question about the whole site.
    /// </remarks>
    private static bool HasCover(TacticalState state, SiteRoom room, TacticalActor actor)
    {
        foreach (SiteOccluder occluder in state.Layout.Occluders)
        {
            if (occluder.RoomId != room.Id)
                continue;

            int distance = (occluder.X - actor.Position.X).Abs().Raw;

            if (distance < CoverRangeCm)
                return true;
        }

        return false;
    }

    /// <summary>How close an occluder has to be, in centimetres, to count as cover.</summary>
    /// <remarks>
    /// Structural rather than tuned: it is the width of the thing a guard ducks behind,
    /// not a balance decision, so it has no row in any table to sit in.
    /// </remarks>
    private const int CoverRangeCm = 150;

    /// <summary>
    /// The priority inputs for one NPC, from that NPC and the public alarm.
    /// </summary>
    /// <remarks>
    /// Note what is absent: no aggregate of other guards' suspicion, no count of agents
    /// seen site-wide, no distance to the nearest player. Each of those would let one
    /// guard's reaction change another's priorities, which is precisely how a stealth
    /// game stops being stealthy — one guard glimpsing the team would silently re-rank
    /// the goals of every guard in the building.
    /// </remarks>
    public static GoapPriorityInputs PriorityInputsFor(TacticalState state, TacticalActor actor, long step)
        => PriorityInputsFor(state, actor, step, null);

    /// <summary>
    /// The priority inputs for one NPC, reusing this step's already-collected facts.
    /// </summary>
    /// <remarks>
    /// The overload every hot path uses. The guard lookup it needs goes through
    /// <paramref name="facts"/>, because <see cref="SiteLayout.Guards"/> rebuilds a list
    /// on every read and calling it once per NPC per step was the largest single cost in
    /// the planning layer. Passing null falls back to reading the layout directly, which
    /// is correct and is what the debug surface and tests use, where it happens once.
    /// </remarks>
    public static GoapPriorityInputs PriorityInputsFor(
        TacticalState state,
        TacticalActor actor,
        long step,
        GoapStepFacts? facts)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (actor is null) throw new ArgumentNullException(nameof(actor));

        int healthPercent = actor.MaxHealth > 0
            ? Math.Clamp(actor.Health * 100 / actor.MaxHealth, 0, 100)
            : 100;

        // Self-preservation rises as this NPC's own health falls and as the building
        // gets louder. Both are facts about itself, plus the one public fact.
        int selfPreservation = state.Alarm.Band switch
        {
            AlarmBand.Burned => 100,
            AlarmBand.Lockdown => 85,
            AlarmBand.Alert => 65,
            AlarmBand.Suspicious => 40,
            _ => 100 - healthPercent,
        };

        // Composure falls as suspicion rises: a guard that has nearly identified the
        // team is not a steady one. From its own meter, never from the site's.
        int composure = Math.Clamp(100 - actor.Suspicion.Value, 0, 100);

        // A guard with a route has an unfinished standing order, which is what
        // `goal.close_open_door` and `goal.patrol` are scaled by.
        bool patrols = facts is not null ? facts.WalksAPatrol(actor) : WalksAPatrol(state.Layout, actor);
        int order = patrols ? 100 : 0;

        int compliance = actor.IsCivilian ? 100 : 0;

        return new GoapPriorityInputs
        {
            Alarm = state.Alarm.Level,
            Curiosity = actor.Suspicion.Value,
            SelfPreservation = selfPreservation,
            Composure = composure,
            Ally = 0,
            Order = order,
            Compliance = compliance,
        };
    }

    /// <summary>The priority inputs for an agent, including what its findings add.</summary>
    public static GoapPriorityInputs PriorityInputsFor(TacticalState state, GoapAgent agent, long step)
        => PriorityInputsFor(state, agent, step, null);

    /// <summary>The priority inputs for an agent, reusing this step's facts.</summary>
    public static GoapPriorityInputs PriorityInputsFor(
        TacticalState state,
        GoapAgent agent,
        long step,
        GoapStepFacts? facts)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        GoapPriorityInputs baseInputs = PriorityInputsFor(state, agent.Actor, step, facts);

        return baseInputs with
        {
            // A colleague being down only counts for a guard who can see it. This is the
            // one place findings feed priority, and it reads a boolean the perception
            // bridge set — not the site's state.
            Ally = agent.Findings.AllyDown ? 100 : 0,
        };
    }

    /// <summary>True when this guard's route has more than one room.</summary>
    /// <remarks>
    /// The direct-from-layout fallback for callers outside the per-step loop. Inside the
    /// loop, <see cref="GoapStepFacts"/> answers this from an index it already built.
    /// </remarks>
    private static bool WalksAPatrol(SiteLayout layout, TacticalActor actor)
    {
        if (!actor.IsGuard)
            return false;

        foreach (SiteGuard guard in layout.Guards)
        {
            if (guard.Id == actor.GuardId)
                return !guard.IsStationary;
        }

        return false;
    }

    /// <summary>
    /// The archetype tags an NPC plans under.
    /// </summary>
    /// <remarks>
    /// A guard gets its role's tag plus <c>any</c>; a civilian gets <c>civilian</c> plus
    /// <c>any</c>. An empty list means "no archetype row found", which is why such an NPC
    /// still plans rather than standing still — it simply gets the actions nobody
    /// claimed.
    /// </remarks>
    public static IReadOnlyList<string> TagsFor(TacticalActor actor)
    {
        if (actor.IsCivilian)
            return CivilianTags;

        GuardArchetypeRow? archetype = SimulationRules.GuardArchetypeFor(actor.GuardArchetypeId);

        if (archetype is null)
            return Array.Empty<string>();

        return new[] { RoleTag(archetype.Role), AnyTag };
    }

    /// <summary>The tag a guard role plans under, matching the CSV vocabulary.</summary>
    private static string RoleTag(TableGuardRole role) => role switch
    {
        TableGuardRole.Patrol => "patrol",
        TableGuardRole.Sentry => "sentry",
        TableGuardRole.Responder => "responder",
        TableGuardRole.Specialist => "specialist",
        _ => AnyTag,
    };

    /// <summary>The "any archetype" tag, which every NPC carries.</summary>
    public const string AnyTag = "any";

    /// <summary>Tags a civilian plans under.</summary>
    private static readonly string[] CivilianTags = { "civilian", AnyTag };
}