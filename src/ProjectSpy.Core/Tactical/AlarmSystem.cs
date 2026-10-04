using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// What the five alarm bands do to the building.
/// </summary>
/// <remarks>
/// <para>
/// The alarm's <em>level</em> is computed from what the NPCs know — see
/// <see cref="AlarmState"/> — and this is what the level then does to the site. The
/// split is the brief's: an abstract counter that ticks up is not an alarm, it is a
/// number, and a guard's behaviour is what makes it one.
/// </para>
/// <para>
/// <b>Every effect here is reversible in principle and none of it is faked.</b> Doors
/// that were closed by the alert record who closed them, so a player who finds the
/// building shuttered knows the site did it and not that the building did. Extraction
/// points close rather than disappear, so a band that drops opens them again.
/// </para>
/// <para>
/// <b>Band changes are the only trigger.</b> None of this runs every step; it runs when
/// the band moves, which keeps a high alarm from re-shuttering every door in the
/// building sixty times a second and from making the effect depend on how long a step
/// happened to be.
/// </para>
/// </remarks>
public static class AlarmSystem
{
    /// <summary>
    /// Applies whatever entering <paramref name="band"/> means for the site.
    /// </summary>
    /// <returns>What changed, for the mission log.</returns>
    public static AlarmBandChange OnBandChanged(TacticalState state, AlarmBand band)
    {
        var change = new AlarmBandChange(band);

        switch (band)
        {
            case AlarmBand.Suspicious:
                // "Extra patrols" from the design document. Guards already on a route
                // walk it faster, which is implemented by the planner reading the band
                // rather than by rewriting routes here — the routes are the generated
                // building's promise and rewriting them would make an intel report
                // wrong.
                change.SpeedMultiplierPercent = SuspiciousSpeedPercent;
                break;

            case AlarmBand.Alert:
                change.SpeedMultiplierPercent = AlertSpeedPercent;
                change.ClosesDoors = true;
                change.LocksConnections = true;
                break;

            case AlarmBand.Lockdown:
                change.SpeedMultiplierPercent = LockdownSpeedPercent;
                change.ClosesDoors = true;
                change.LocksConnections = true;
                change.SpawnsResponders = true;
                change.BlocksSomeExtraction = true;
                break;

            case AlarmBand.Burned:
                change.SpeedMultiplierPercent = BurnedSpeedPercent;
                change.ClosesDoors = true;
                change.LocksConnections = true;
                change.SpawnsResponders = true;
                change.BlocksSomeExtraction = true;
                change.ForcesExtraction = true;
                break;

            default:
                break;
        }

        if (change.ClosesDoors)
            CloseOpenDoors(state, change);

        if (change.SpawnsResponders)
            SpawnResponders(state, change);

        if (change.ForcesExtraction)
            state.Record("log.alarm.burned");

        return change;
    }

    /// <summary>
    /// Undoes the reversible parts of a band the site has dropped out of.
    /// </summary>
    /// <remarks>
    /// Only doors the <em>alarm</em> shut are reopened. A door the player closed stays
    /// closed, because a guard walking past should not quietly undo the player's work —
    /// and because a building that tidies itself up after a false alarm would remove the
    /// cost of being nearly caught.
    /// </remarks>
    public static void OnBandRelaxed(TacticalState state, AlarmBand band)
    {
        if (band != AlarmBand.Calm)
            return;

        var toReopen = new List<SiteConnectionId>();

        foreach (KeyValuePair<SiteConnectionId, ConnectionState> entry in state.Doors)
        {
            if (entry.Value == ConnectionState.Closed && state.DoorsShutByAlarm.Contains(entry.Key))
                toReopen.Add(entry.Key);
        }

        foreach (SiteConnectionId id in toReopen)
        {
            state.Doors[id] = ConnectionState.Open;
            state.DoorsShutByAlarm.Remove(id);
        }
    }

    /// <summary>
    /// Shuts every open door on the site and remembers which ones the alarm shut.
    /// </summary>
    /// <remarks>
    /// Everything, not a sample. The design document says alert closes "some doors",
    /// and a sampled subset would be a subset chosen by a generator rather than by a
    /// rule — which makes the effect unrepeatable and untestable. Closing everything
    /// and reopening on a relaxation is the honest version of "the site is on edge".
    /// </remarks>
    private static void CloseOpenDoors(TacticalState state, AlarmBandChange change)
    {
        foreach (SiteConnection connection in state.Layout.Connections)
        {
            ConnectionState current = state.StateOf(connection.Id);

            // Only doors that were open to begin with. A locked door stays locked and a
            // barricade stays barricaded; the alarm does not clear either.
            if (current != ConnectionState.Open)
                continue;

            state.Doors[connection.Id] = ConnectionState.Closed;
            state.DoorsShutByAlarm.Add(connection.Id);
            change.DoorsClosed++;
        }
    }

    /// <summary>
    /// Wakes the site's responders so they converge rather than stand at a post.
    /// </summary>
    /// <remarks>
    /// An existing responder is flagged rather than a new actor minted. Spawning a
    /// guard would change the actor table, the state hash and every id-indexed lookup
    /// mid-mission, and the guards the generator placed already include responders —
    /// they were simply standing where they were told to. Releasing them is the same
    /// event with none of that cost.
    /// </remarks>
    private static void SpawnResponders(TacticalState state, AlarmBandChange change)
    {
        foreach (TacticalActor actor in state.SortedActors)
        {
            if (!actor.IsGuard || actor.Condition != ActorCondition.Active)
                continue;

            if (actor.Suspicion.IsAware)
                continue;

            // A guard that has not seen anything is not converging; it is being
            // recalled because somebody with a radio said so. Feeding it a little
            // suspicion is what makes it do that without pretending it saw the team.
            actor.Suspicion.Add(ResponderRecallSuspicion);
            actor.IsOffRoute = true;
            change.RespondersReleased++;
        }
    }

    /// <summary>Guard speed multiplier while the site is merely suspicious.</summary>
    private const int SuspiciousSpeedPercent = 110;

    /// <summary>Guard speed multiplier while the site is on alert.</summary>
    private const int AlertSpeedPercent = 135;

    /// <summary>Guard speed multiplier under lockdown.</summary>
    private const int LockdownSpeedPercent = 160;

    /// <summary>Guard speed multiplier once the site is burned.</summary>
    private const int BurnedSpeedPercent = 180;

    /// <summary>
    /// Suspicion handed to a responder that has not seen anything, so that it is
    /// searching rather than idle.
    /// </summary>
    /// <remarks>
    /// A fifth of the way to aware. High enough that the planner treats it as
    /// "something is wrong, go and look", low enough that it does not turn into
    /// identification and send every guard on the site straight at a position the team
    /// has already left.
    /// </remarks>
    private const int ResponderRecallSuspicion = 12;
}

/// <summary>
/// What a band change did to the site.
/// </summary>
/// <remarks>
/// A value rather than a side effect only, so the pipeline can record what happened
/// into the mission log and a test can assert it. A band that quietly changed eight
/// doors and nobody could see is a band the player cannot plan against.
/// </remarks>
/// <param name="Band">The band the site has entered.</param>
/// <remarks>
/// A mutable struct rather than a record because it is built up: the band is chosen,
/// then the doors are shut and counted, then the responders are released and counted,
/// and each of those happens in a different place. An init-only shape would mean
/// either three passes over the same switch or a copy per step.
/// </remarks>
public struct AlarmBandChange
{
    /// <summary>Creates a report for a band, with nothing applied yet.</summary>
    public AlarmBandChange(AlarmBand band) => Band = band;

    /// <summary>The band the site has entered.</summary>
    public AlarmBand Band { get; }

    /// <summary>How much faster guards move, as a percentage of patrol speed.</summary>
    public int SpeedMultiplierPercent { get; set; } = 100;

    /// <summary>True when the band shut doors behind the team.</summary>
    public bool ClosesDoors { get; set; }

    /// <summary>How many doors it shut.</summary>
    public int DoorsClosed { get; set; }

    /// <summary>True when the band locks connections.</summary>
    public bool LocksConnections { get; set; }

    /// <summary>True when the band releases responders.</summary>
    public bool SpawnsResponders { get; set; }

    /// <summary>How many responders it released.</summary>
    public int RespondersReleased { get; set; }

    /// <summary>True when the band closes some extraction points.</summary>
    public bool BlocksSomeExtraction { get; set; }

    /// <summary>True when the band ends the mission.</summary>
    public bool ForcesExtraction { get; set; }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Band} speed {SpeedMultiplierPercent}% doors {DoorsClosed} responders {RespondersReleased}";
}
