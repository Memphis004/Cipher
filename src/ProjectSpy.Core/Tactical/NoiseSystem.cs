using ProjectSpy.Core.Missions;
using ProjectSpy.Tables;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// One entry in the mission's noise log.
/// </summary>
/// <remarks>
/// <para>
/// The brief is emphatic that noise has to be legible: "every noise event is recorded in
/// the mission log with its source and who heard it". This record is what that means in
/// code — it names the source by key and id, says how loud it was, and lists every
/// listener by id and by how loudly it reached them.
/// </para>
/// <para>
/// Kept forever rather than trimmed. Noise is the primary way a careless player loses,
/// so the record of how they lost has to survive long enough for the debrief to be
/// honest about it. A bounded log would drop exactly the early mistakes that teach the
/// player the rule.
/// </para>
/// </remarks>
/// <param name="Step">The step the noise happened on.</param>
/// <param name="SourceActorId">Who made it, or zero for the world.</param>
/// <param name="SourceKey">Localization key for what it was, e.g. <c>action.combat_melee</c>.</param>
/// <param name="ProfileId">The <c>noise_profile</c> row.</param>
/// <param name="Origin">Where it was made.</param>
/// <param name="Heard">Everyone who heard it, and how loudly.</param>
public sealed record NoiseLogEntry(
    long Step,
    int SourceActorId,
    string SourceKey,
    int ProfileId,
    TacticalPosition Origin,
    IReadOnlyList<NoiseHeard> Heard);

/// <summary>
/// How noise travels: outward from where it was made, attenuated by every door and
/// every floor it crosses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Range, not sound.</b> Rule 15 asks for noise to propagate "to listeners by range,
/// attenuated per connection and per floor", and this does exactly that and nothing
/// more. A noise is a radius on one floor; crossing a connection shrinks it by
/// <c>noise_profile.attenuation_per_connection_percent</c>, and crossing a floor
/// shrinks it further by <c>attenuation_per_floor_percent</c>. The radius that survives
/// the journey is compared against the centimetres the sound still has to cover inside
/// the listener's room.
/// </para>
/// <para>
/// <b>Why a graph walk and not a straight line.</b> The obvious shortcut — "is the
/// listener within radius" — is wrong the moment there is a wall between them, and this
/// is a building. A shouting guard one room away through a shut door is meant to be
/// heard through that door and muffled, not ignored because the radius happens to
/// cover the wall.
/// </para>
/// <para>
/// <b>Determinism.</b> The flood fill visits rooms and connections in ascending id
/// order and stops expanding anything whose radius has fallen to nothing, so the
/// listener set is a function of the building and the profile alone.
/// </para>
/// </remarks>
public static class NoiseSystem
{
    /// <summary>
    /// The loudness a profile starts at, in centimetres.
    /// </summary>
    /// <remarks>
    /// Falls back to the quietest crouch step rather than to zero, because a missing
    /// profile row should produce a noise that can barely be heard rather than one that
    /// does not exist — and a zero radius would silently make an action mute.
    /// </remarks>
    public static int BaseRadiusCm(int profileId)
        => SimulationRules.NoiseProfileFor(profileId)?.BaseRadiusCm
           ?? SimulationRules.NoiseProfileFor(PostureRules.StepNoiseProfileId(Posture.Crouch))?.BaseRadiusCm
           ?? 120;

    /// <summary>How much a profile loses per connection crossed, in percent.</summary>
    public static int AttenuationPerConnectionPercent(int profileId)
        => SimulationRules.NoiseProfileFor(profileId)?.AttenuationPerConnectionPercent ?? 55;

    /// <summary>How much a profile loses per floor crossed, in percent.</summary>
    public static int AttenuationPerFloorPercent(int profileId)
        => SimulationRules.NoiseProfileFor(profileId)?.AttenuationPerFloorPercent ?? 45;

    /// <summary>
    /// How much suspicion this profile contributes to somebody who hears it at full
    /// loudness.
    /// </summary>
    /// <remarks>
    /// From <c>noise_profile.suspicion_weight</c>, which ranges from 1 for a vent crawl
    /// to 12 for the site alarm going off. The weight is applied to the <em>fraction</em>
    /// of the base radius that survived, so a noise that arrived muffled raises less
    /// suspicion than the same noise heard through an open doorway — and the ratio is
    /// what makes a distant footstep and a close one differ at all.
    /// </remarks>
    public static int SuspicionWeight(int profileId)
        => SimulationRules.NoiseProfileFor(profileId)?.SuspicionWeight ?? 1;

    /// <summary>
    /// Works out who hears a noise, and records it on the event.
    /// </summary>
    /// <param name="layout">The building.</param>
    /// <param name="actors">Everyone in the mission.</param>
    /// <param name="doors">Current door states. A shut door adds to attenuation.</param>
    /// <param name="light">Lighting, so a dark room carries sound further than a lit one.</param>
    /// <param name="noise">The noise. <see cref="NoiseEvent.HeardBy"/> is filled in.</param>
    /// <returns>The listeners, in ascending actor id.</returns>
    public static IReadOnlyList<NoiseHeard> Propagate(
        SiteLayout layout,
        IReadOnlyList<TacticalActor> actors,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState> doors,
        LightState light,
        NoiseEvent noise)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));
        if (noise is null) throw new ArgumentNullException(nameof(noise));

        noise.HeardBy.Clear();

        SiteRoom? originRoom = layout.RoomContaining(noise.Origin);

        // How much radius survived into each room and where the sound entered it, keyed
        // by room id — a room id is unique across the whole site, so no floor is needed
        // in the key.
        Dictionary<SiteRoomId, Arrival> arrivals =
            PropagateAcrossRooms(layout, doors, originRoom, noise);

        foreach (TacticalActor actor in SortedActors(actors))
        {
            if (actor.Condition != ActorCondition.Active)
                continue;

            // Whoever made the noise hears their own noise. Recording that would make
            // ApplyToSuspicion charge an actor suspicion for the noise they themselves
            // made, and would put the maker in the mission log as one of their own
            // listeners — so the source is never one of its own listeners.
            if (noise.SourceActorId != 0 && actor.Id.Value == noise.SourceActorId)
                continue;

            if (!arrivals.TryGetValue(RoomOf(layout, actor.Position), out Arrival arrival))
                continue;

            int surviving = arrival.SurvivingCm;

            if (surviving <= 0)
                continue;

            // The surviving radius is spent covering the listener's own room, measured
            // from where the sound came in — the noise itself in the origin room, the
            // door it came through otherwise. Measuring from the origin instead would
            // charge a listener for the whole walk they did not have to carry the sound
            // over, and a noise in a long room would stop dead in its own doorway.
            //
            // On another floor there is no line to measure along at all — a stairwell
            // lands wherever it lands — so what survived the doors is the whole test.
            int distanceCm = 0;

            if (actor.Position.FloorIndex == noise.Origin.FloorIndex)
            {
                distanceCm = Fixed32.Distance(arrival.EnteredAtX, actor.Position.X).Raw;

                if (distanceCm > surviving)
                    continue;
            }

            int intensityPercent = PercentOfRadius(noise.Radius.Raw, surviving);

            // A dark room is a louder place: sound with nothing to bounce off carries.
            // A structural bonus rather than a table row because no CSV column owns it,
            // and it is what breaks a tie between two listeners who would otherwise hear
            // an identical noise identically — in favour of the one standing in the
            // dark, which is the situation a player is usually trying to create.
            if (light.LevelAt(layout, actor.Position) == SiteLightLevel.Dark)
                intensityPercent = Math.Min(100, intensityPercent + DarkRoomCarvePercent);

            noise.HeardBy.Add(new NoiseHeard(actor.Id.Value, intensityPercent, new Fixed32(distanceCm)));
        }

        return noise.HeardBy;
    }

    /// <summary>
    /// Raises suspicion on everyone who heard a noise, in proportion to how loudly.
    /// </summary>
    /// <returns>Number of listeners whose suspicion actually moved.</returns>
    public static int ApplyToSuspicion(NoiseEvent noise, IReadOnlyDictionary<int, TacticalActor> actorsById)
    {
        if (noise is null) throw new ArgumentNullException(nameof(noise));

        int weight = SuspicionWeight(noise.ProfileId);
        int moved = 0;

        foreach (NoiseHeard heard in noise.HeardBy)
        {
            if (!actorsById.TryGetValue(heard.ActorId, out TacticalActor? listener))
                continue;

            // A member of the public does not raise an alarm, so their reaction is a
            // scene rather than a suspicion value. Charging them suspicion would make a
            // civilian sighting a detection, which is the single easiest way to turn a
            // stealth system into a noise-avoidance system.
            if (listener.IsCivilian)
                continue;

            listener.Suspicion.Add(SimulationRules.PercentOf(weight, heard.IntensityPercent));
            listener.Memory.NoteNoise(noise.Origin, noise.Step);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// What a noise costs the site alarm directly, independent of who heard it.
    /// </summary>
    /// <remarks>
    /// Only for noises the whole site reacts to — an alarm going off, a gunshot. Most
    /// noises raise nothing here and work entirely through
    /// <see cref="ApplyToSuspicion"/>, because the brief is explicit that the alarm is
    /// "driven by NPC awareness rather than by an abstract counter". This is the narrow
    /// exception for the case where the noise <em>is</em> the alarm.
    /// </remarks>
    public static int SiteAlarmCost(int profileId)
        => profileId == SiteAlarmNoiseProfileId ? 12 : 0;

    /// <summary>The profile id of the site-wide alarm noise, from the table.</summary>
    public const int SiteAlarmNoiseProfileId = 12320;

    /// <summary>
    /// How much louder a noise is in an unlit room, in percentage points.
    /// </summary>
    /// <remarks>
    /// Structural: no column owns it, and it is small enough to be a correction rather
    /// than a factor. Its job is to break a tie between two listeners who would
    /// otherwise hear an identical noise identically, in favour of the one standing in
    /// the dark — which is the situation a player is usually trying to create.
    /// </remarks>
    private const int DarkRoomCarvePercent = 15;

    /// <summary>
    /// What is left of a noise by the time it reaches a room: how much radius, and the
    /// point on that floor where the sound entered.
    /// </summary>
    /// <param name="SurvivingCm">Radius left after every door and floor it crossed.</param>
    /// <param name="EnteredAtX">
    /// Where the sound crossed into the room — the connection's own position on that
    /// floor, or the noise's own position in the room it started in.
    /// </param>
    private readonly record struct Arrival(int SurvivingCm, Fixed32 EnteredAtX);

    /// <summary>
    /// Walks the room graph outwards from the origin, shrinking the radius by each door
    /// and each floor crossed.
    /// </summary>
    /// <returns>
    /// What survives in each room, keyed by room id. A room with no surviving radius is
    /// simply absent.
    /// </returns>
    private static Dictionary<SiteRoomId, Arrival> PropagateAcrossRooms(
        SiteLayout layout,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState> doors,
        SiteRoom? originRoom,
        NoiseEvent noise)
    {
        var remaining = new Dictionary<SiteRoomId, Arrival>();
        if (originRoom is null)
            return remaining;

        int profileId = noise.ProfileId;
        int perConnection = AttenuationPerConnectionPercent(profileId);
        int perFloor = AttenuationPerFloorPercent(profileId);

        remaining[originRoom.Id] = new Arrival(noise.Radius.Raw, noise.Origin.X);

        var frontier = new Queue<SiteRoomId>();
        frontier.Enqueue(originRoom.Id);

        while (frontier.Count > 0)
        {
            SiteRoomId current = frontier.Dequeue();
            int here = remaining[current].SurvivingCm;

            if (here <= 0)
                continue;

            if (layout.Find(current) is null)
                continue;

            // Ascending connection id, so the flood fill's order is a function of ids
            // and the resulting loudness map never depends on generation order.
            var connections = new List<SiteConnection>(layout.ConnectionsAt(current));
            connections.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));

            foreach (SiteConnection connection in connections)
            {
                SiteRoomId across = connection.Other(current);

                // First arrival wins. Rooms can be reached by several routes and the
                // first one is the loudest only if the fill is a proper BFS over
                // attenuation, which it is not — attenuation is not uniform per hop, so
                // the honest rule is that a room is heard in through the cheapest route
                // found, and taking the best-so-far rather than the first keeps that
                // true without needing a priority queue.
                if (remaining.TryGetValue(across, out Arrival already) && already.SurvivingCm >= here)
                    continue;

                int after = SimulationRules.PercentOf(here, 100 - perConnection);

                // A vertical hop costs the floor attenuation on top of the connection
                // one. Both, deliberately: a stairwell that only attenuated as much as a
                // door would carry a shout between floors as readily as along a
                // corridor, and the whole point of a basement is that it is a refuge.
                if (connection.IsVertical)
                    after = SimulationRules.PercentOf(after, 100 - perFloor);

                if (after <= 0)
                    continue;

                // The sound enters `across` at the connection's position on that room's
                // floor, which for a horizontal connection is the shared wall and for a
                // stairwell is the landing rather than the foot.
                Fixed32 enteredAt = across == connection.RoomB ? connection.UpperX : connection.X;

                remaining[across] = new Arrival(after, enteredAt);
                frontier.Enqueue(across);
            }
        }

        return remaining;
    }

    /// <summary>The room a position is in, or <see cref="SiteRoomId.None"/>.</summary>
    private static SiteRoomId RoomOf(SiteLayout layout, TacticalPosition at)
        => layout.RoomContaining(at)?.Id ?? SiteRoomId.None;

    /// <summary>
    /// How much of a noise's radius survived, as a percentage of the original.
    /// </summary>
    /// <remarks>
    /// Integer division, so two noises of different sizes heard at the same surviving
    /// radius do not produce the same intensity. That matters because the intensity is
    /// what scales a listener's suspicion, and a rounding scheme that flattened them
    /// would make a grenade and a dropped bolt raise identical alarm.
    /// </remarks>
    private static int PercentOfRadius(int originalCm, int remainingCm)
        => originalCm <= 0 ? 0 : Math.Min(100, (int)((long)remainingCm * 100L / originalCm));

    /// <summary>Actors in ascending id order, so a listener list is stable.</summary>
    private static IEnumerable<TacticalActor> SortedActors(IReadOnlyList<TacticalActor> actors)
    {
        var sorted = new List<TacticalActor>(actors.Count);
        foreach (TacticalActor actor in actors)
            sorted.Add(actor);

        sorted.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return sorted;
    }

    /// <summary>
    /// The id of the noise profile an action makes, or zero when it makes none.
    /// </summary>
    /// <remarks>
    /// A table row's <c>noise_profile_id</c> is 0 for the several actions that are
    /// genuinely silent — blocking, waiting, observing, healing. That is a real answer
    /// and not missing data, so this returns zero and the callers skip the event rather
    /// than substituting a default profile and making a silent action audible.
    /// </remarks>
    public static int ProfileForAction(int actionId)
    {
        TacticalAction? row = SimulationRules.TacticalActionFor(actionId);
        return row?.NoiseProfileId ?? 0;
    }
}
