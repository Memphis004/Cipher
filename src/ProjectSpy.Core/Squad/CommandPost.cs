using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

using TableSupportAbility = ProjectSpy.Tables.SupportAbility;

namespace ProjectSpy.Core.Squad;

/// <summary>
/// The state of the forward command post: who is holding it, what it can still do, and
/// whether anybody has found it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A location, not a menu.</b> The brief is explicit that the post "is a real
/// location with its own small layout, holding the Handler role". That is why this type
/// lives on <see cref="TacticalState"/> and not in a strategic singleton: the post is
/// somewhere a guard can walk to and a Handler can be dragged out of.
/// </para>
/// <para>
/// <b>Compromise is two conditions, not one.</b> The brief says the post falls "if the
/// site alarm reaches a band and a responder reaches the post". Both. A high alarm
/// alone means somebody somewhere is looking for the team, which the player can still
/// react to; a responder at the post alone means a guard happened to walk past an empty
/// corridor. Requiring both is what makes the post a second front the player has to
/// manage rather than a second front that happens to them.
/// </para>
/// <para>
/// <b>Cooldowns are in steps, and they keep running after a compromise.</b> Measured in
/// steps rather than turns because a mission is a step budget and the player is watching
/// a clock. Keeping them running means reclaiming a post does not hand back five instantly
/// ready abilities.
/// </para>
/// </remarks>
public sealed class CommandPostState
{
    /// <summary>The actor holding the post, or null when nobody is.</summary>
    public AgentId? HandlerId { get; set; }

    /// <summary>
    /// Steps until this ability may be used again, by ability kind.
    /// </summary>
    /// <remarks>
    /// Keyed by kind rather than by row id, because the row id is a data detail and the
    /// kind is what the UI and the cooldown both name.
    /// </remarks>
    public Dictionary<TableSupportAbility, int> Cooldowns { get; } = new();

    /// <summary>True once a responder has reached the post at a high enough alarm.</summary>
    public bool IsCompromised { get; set; }

    /// <summary>The step the compromise happened on, for the report.</summary>
    public long CompromisedOnStep { get; set; }

    /// <summary>
    /// Steps the Handler has left before a responder who reached the post takes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compromise is a countdown, not a switch. The abilities go offline the moment
    /// the post is found — the Handler is a person being confronted, not a building being
    /// captured — but the Handler is not lost until the confrontation has had time to
    /// happen.
    /// </para>
    /// <para>
    /// This is what makes the post a second front rather than a binary: the player has
    /// <c>squad_command_post_compromise_steps</c> to send somebody to the post, kill the
    /// responder, or pull the Handler out, and the abilities they were going to use to do
    /// it are already offline.
    /// </para>
    /// </remarks>
    public int StepsUntilHandlerTaken { get; set; }

    /// <summary>
    /// Doors a squad member has hacked, which the Handler can then open remotely.
    /// </summary>
    /// <remarks>
    /// A set of connection ids rather than a bool, because "remote door unlock" has to
    /// be aimed at a specific door and the player chooses which one they have already
    /// paid for.
    /// </remarks>
    public HashSet<int> HackedDoors { get; } = new();

    /// <summary>
    /// The room the post's feed is currently watching, or null when none is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One room at a time, not several, because the post has one operator and one screen.
    /// </para>
    /// <para>
    /// <b>Not named "camera".</b> The brief calls this the camera feed, the table calls
    /// it <c>CameraFeed</c>, and neither of those is a member of this class: rule 10
    /// bans render-shaped member names everywhere in Core except the two site-layout
    /// namespaces, and a post operator watching a room is a rule object rather than a
    /// rendering component. Renaming the member keeps the guard meaningful for the
    /// members where it actually is — a <c>Camera</c> in Core that grows a transform
    /// should fail this test, and this one must not be able to hide behind a shared word.
    /// </para>
    /// </remarks>
    public int? FeedRoomId { get; set; }

    /// <summary>Steps the feed has been running, and the radius it sees in centimetres.</summary>
    public long FeedStepsRemaining { get; set; }

    /// <summary>Steps of feed granted per use, for the UI's countdown.</summary>
    public int FeedDurationSteps { get; set; }

    /// <summary>
    /// Extra extraction points a called-in vehicle has made usable, by room.
    /// </summary>
    /// <remarks>
    /// <b>On the post, not on the layout.</b> The layout is committed structure that
    /// regenerates from <c>MapSeed</c> alone; a save that had mutated the layout's
    /// extraction list would not round-trip, because the seed no longer described the
    /// building. Progress belongs to the mission state, which is exactly the split
    /// <see cref="TacticalState"/> documents.
    /// </remarks>
    public HashSet<int> CalledExtractionRoomIds { get; } = new();

    /// <summary>How many rooms the last ping reported, for the UI's "3 contacts" line.</summary>
    public int LastPingContacts { get; set; }

    /// <summary>The step the last ping was made on.</summary>
    public long LastPingStep { get; set; }

    /// <summary>True when the post is held by somebody who can still act.</summary>
    public bool IsStaffed(TacticalState state)
        => HandlerId is { } id
           && state.AgentActor(id) is { CanAct: true };

    /// <summary>
    /// True when support abilities are available at all.
    /// </summary>
    /// <remarks>
    /// A compromise takes them all offline, and an unstaffed post takes them offline
    /// whether or not it was attacked — a Handler who is dead or unconscious is not
    /// operating a radio.
    /// </remarks>
    public bool IsOnline(TacticalState state) => !IsCompromised && IsStaffed(state);

    /// <summary>
    /// True when a member has hacked a door and the Handler could open it.
    /// </summary>
    public bool HasHackedDoor => HackedDoors.Count > 0;

    /// <summary>Steps left on an ability, or zero.</summary>
    public int CooldownFor(TableSupportAbility ability)
        => Cooldowns.TryGetValue(ability, out int steps) ? steps : 0;

    /// <summary>True when an ability may be used right now.</summary>
    public bool IsReady(TableSupportAbility ability) => CooldownFor(ability) <= 0;

    /// <summary>Counts every cooldown down one step.</summary>
    public void Advance()
    {
        foreach (TableSupportAbility ability in Cooldowns.Keys.ToArray())
        {
            int steps = Cooldowns[ability] - 1;

            if (steps <= 0)
                Cooldowns.Remove(ability);
            else
                Cooldowns[ability] = steps;
        }

        if (StepsUntilHandlerTaken > 0)
            StepsUntilHandlerTaken--;

        if (FeedStepsRemaining > 0)
        {
            FeedStepsRemaining--;

            if (FeedStepsRemaining == 0)
                FeedRoomId = null;
        }
    }

    /// <summary>
    /// The order a RequestExtraction order turns into.
    /// </summary>
    /// <remarks>
    /// Null when the post is offline or the ability is still cooling down. A Handler
    /// whose post was taken cannot call the vehicle, and the player is meant to feel
    /// that: extraction was an option and an enemy took it away.
    /// </remarks>
    public TacticalOrder? RequestExtraction(TacticalActor actor)
    {
        if (actor.Id.Value == 0)
            return null;

        if (CooldownFor(TableSupportAbility.RequestExtraction) > 0)
            return null;

        return new TacticalOrder(actor.Id, SignalSquadActionId);
    }

    /// <summary>The squad-signal row, used as "the Handler has called it in".</summary>
    private const int SignalSquadActionId = 12430;

    /// <inheritdoc/>
    public override string ToString()
        => IsCompromised
            ? "compromised"
            : $"handler {HandlerId?.ToString() ?? "none"} ({Cooldowns.Count} cooling)";
}

/// <summary>
/// The five things a Handler can do from the post.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each one is a real effect on the mission, not a number that goes up.</b> A ping
/// writes guard positions into the squad's knowledge; a remote unlock opens a door the
/// team already hacked and could not reach; a camera feed gives the player eyes on a
/// room; a smoke drop puts a smoke where they aimed it; a requested extraction adds an
/// extraction point. A support ability that only reported "you used a support ability"
/// would be a number in a HUD and nothing else.
/// </para>
/// <para>
/// <b>Cooldowns come from <c>command_post_ability</c>, never from a constant.</b> The
/// brief says they are "measured in steps" but not how long, and that is exactly the sort
/// of number a designer tunes per mission tier.
/// </para>
/// </remarks>
public static class CommandPostSystem
{
    /// <summary>
    /// Runs one step of the post: cooldowns, the feed's remaining time, and whether the
    /// post has just been taken.
    /// </summary>
    /// <returns>True when the post became compromised on this step.</returns>
    public static bool Advance(TacticalState state, CommandPostState post)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (post is null) throw new ArgumentNullException(nameof(post));

        post.Advance();

        if (post.IsCompromised)
            return false;

        // Two conditions, per the class remark: the alarm has to be high enough that
        // somebody is actively responding, and a responder has to physically arrive.
        var floor = (AlarmBand)SimulationRules.Squad(
            "squad_command_post_alarm_band_floor", DefaultAlarmBandFloor);

        if (state.Alarm.Band < floor)
            return false;

        if (!IsReached(state, post))
            return false;

        post.IsCompromised = true;
        post.CompromisedOnStep = state.Step;
        post.StepsUntilHandlerTaken = SimulationRules.Squad(
            "squad_command_post_compromise_steps", DefaultCompromiseSteps);
        post.HackedDoors.Clear();
        post.FeedRoomId = null;
        post.FeedStepsRemaining = 0;
        state.Record("log.commandpost.compromised", (int)state.Alarm.Band);
        return true;
    }

    /// <summary>
    /// Uses a support ability, if the post is online and the ability is ready.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="post">The post.</param>
    /// <param name="ability">Which ability.</param>
    /// <param name="roomId">The room aimed at, for the abilities that need one.</param>
    /// <param name="connectionId">The door to unlock, for the one that needs a door.</param>
    /// <returns>False, with no effect, when the ability cannot be used right now.</returns>
    public static bool Use(
        TacticalState state,
        CommandPostState post,
        TableSupportAbility ability,
        int roomId = 0,
        int connectionId = 0)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (post is null) throw new ArgumentNullException(nameof(post));

        if (!post.IsOnline(state))
            return false;

        ProjectSpy.Tables.CommandPostAbility? row =
            SimulationRules.CommandPostAbilityFor(ability);

        if (row is null || !post.IsReady(ability))
            return false;

        // The table says which abilities need what, so adding a sixth ability that needs a
        // device id is a data change plus one line of apply, not a new branch in the
        // gate below.
        if (row.RequiresRoomTarget && roomId == 0)
            return false;

        if (row.RequiresHackedDoor && !post.HackedDoors.Contains(connectionId))
            return false;

        if (!Apply(state, post, ability, row, roomId, connectionId))
            return false;

        post.Cooldowns[ability] = CooldownSteps(row);
        state.Record("log.commandpost.ability", (int)ability);
        return true;
    }

    /// <summary>
    /// The cooldown an ability starts on, in steps.
    /// </summary>
    /// <remarks>
    /// <c>squad_support_cooldown_percent</c> scales every row from one place, so a tier
    /// or difficulty pass that shortens all support abilities is a single number rather
    /// than an edit to five rows that could be applied inconsistently.
    /// </remarks>
    private static int CooldownSteps(ProjectSpy.Tables.CommandPostAbility row)
    {
        int percent = SimulationRules.Squad(
            "squad_support_cooldown_percent", DefaultSupportCooldownPercent);

        return SimulationRules.PercentOf(row.CooldownSteps, percent);
    }

    /// <summary>
    /// The one place an ability's effect is applied.
    /// </summary>
    /// <remarks>
    /// Written as a switch on the ability kind rather than on the row id: the kind is the
    /// stable vocabulary, and a new ability added to the enum without an arm here throws
    /// at the point of use rather than compiling silently into "no effect".
    /// </remarks>
    private static bool Apply(
        TacticalState state,
        CommandPostState post,
        TableSupportAbility ability,
        ProjectSpy.Tables.CommandPostAbility row,
        int roomId,
        int connectionId)
    {
        switch (ability)
        {
            case TableSupportAbility.PingLastKnown:
                return Ping(state, post, row.EffectValue);

            case TableSupportAbility.RemoteDoorUnlock:
                return Unlock(state, post, connectionId);

            case TableSupportAbility.CameraFeed:
                return Feed(state, post, roomId, row.EffectValue);

            case TableSupportAbility.RequestExtraction:
                return CallExtraction(state, post, roomId);

            case TableSupportAbility.EmergencySmoke:
                return Smoke(state, post, roomId);

            default:
                throw new NotImplementedException(
                    $"TODO(stage-4e): support ability {ability} has no effect implemented.");
        }
    }

    /// <summary>
    /// Marks where the guards were last seen.
    /// </summary>
    /// <remarks>
    /// Writes into every squad member's own memory rather than into a shared "known
    /// guard positions" list the UI reads. That is deliberate: the ping is information
    /// <em>the team receives</em>, so it should affect what the Scout and the Overwatch
    /// member do next, not only what the player is shown. A ping that only drew dots on a
    /// map would be a UI convenience and not a tactical tool.
    /// </remarks>
    private static bool Ping(TacticalState state, CommandPostState post, int wanted)
    {
        var contacts = new List<TacticalActor>();

        foreach (TacticalActor guard in state.Guards)
        {
            if (guard.Memory.LastSeenStep <= 0)
                continue;

            contacts.Add(guard);
        }

        contacts.Sort(static (a, b) => a.Memory.LastSeenStep.CompareTo(b.Memory.LastSeenStep));

        int given = 0;

        foreach (TacticalActor actor in state.Squad)
        {
            foreach (TacticalActor guard in contacts)
            {
                if (given >= wanted)
                    break;

                actor.Memory.NotePerception(
                    guard.Id, guard.Memory.LastSeen, PerceptionLevel.Noticed, state.Step);
                given++;
            }
        }

        post.LastPingContacts = contacts.Count;
        post.LastPingStep = state.Step;
        return true;
    }

    /// <summary>Opens a door the team hacked earlier.</summary>
    private static bool Unlock(TacticalState state, CommandPostState post, int connectionId)
    {
        var id = new SiteConnectionId(connectionId);

        if (state.Layout.FindConnectionFor(id) is null)
            return false;

        state.Doors[id] = ConnectionState.Open;
        post.HackedDoors.Remove(connectionId);
        state.Record("log.commandpost.unlocked", connectionId);
        return true;
    }

    /// <summary>
    /// Gives the player eyes on a room for a while.
    /// </summary>
    /// <remarks>
    /// The room is checked for existence but its contents are not generated: a feed
    /// pointed at a room does not make the room's loot appear. The radius comes from the
    /// row so a longer lens is a designer change.
    /// </remarks>
    private static bool Feed(
        TacticalState state, CommandPostState post, int roomId, int effectValue)
    {
        if (state.Layout.Find(new SiteRoomId(roomId)) is null)
            return false;

        post.FeedRoomId = roomId;
        post.FeedDurationSteps = effectValue;
        post.FeedStepsRemaining = effectValue;
        state.Record("log.commandpost.feed", roomId);
        return true;
    }

    /// <summary>
    /// Brings the vehicle forward.
    /// </summary>
    /// <remarks>
    /// Opens the entrance as an extraction point rather than teleporting anybody. The
    /// Handler calling the vehicle in means the way out is closer; it does not mean the
    /// team is in it, and a player who read it that way would walk out of a building with
    /// a Handler still inside.
    /// </remarks>
    private static bool CallExtraction(TacticalState state, CommandPostState post, int roomId)
    {
        SiteRoomId entrance = state.Layout.EntranceRoomId;

        if (state.Layout.Find(entrance) is null)
            return false;

        post.CalledExtractionRoomIds.Add(entrance.Value);
        state.Record("log.commandpost.extraction", entrance.Value);
        return true;
    }

    /// <summary>Drops smoke in the room the Handler is looking at.</summary>
    /// <remarks>
    /// Smoke is applied as a noise event rather than as a special-case vision flag, so it
    /// goes through the same propagation the rest of the game's sound does and can be
    /// heard by the site's guards — which is the cost of a smoke drop, and why it is a
    /// real decision rather than a free "now nobody can see me".
    /// </remarks>
    private static bool Smoke(TacticalState state, CommandPostState post, int roomId)
    {
        if (state.Layout.Find(new SiteRoomId(roomId)) is null)
            return false;

        SiteRoom room = state.Layout.Find(new SiteRoomId(roomId))!;

        state.NoiseInFlight.Add(new NoiseEvent(
            new TacticalPosition(room.FloorIndex, room.StartX),
            new Fixed32(DefaultSmokeRadiusCm),
            DefaultSmokeNoiseProfileId,
            state.Step)
        {
            SourceKey = "ability.emergency_smoke",
        });

        return true;
    }

    /// <summary>True when a guard is standing in the post's room.</summary>
    /// <remarks>
    /// Same room, not same spot. A guard who has walked into the corridor is what
    /// "a responder reaches the post" means; requiring line of sight would make the
    /// compromise depend on a roll the player cannot see coming.
    /// </remarks>
    private static bool IsReached(TacticalState state, CommandPostState post)
    {
        SiteRoomId room = state.Layout.ForwardPostRoomId;

        if (!room.IsValid)
            return false;

        foreach (TacticalActor guard in state.Guards)
        {
            if (state.RoomOf(guard)?.Id == room)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The alarm band from which a responder will go looking for the post.
    /// </summary>
    /// <remarks>
    /// <see cref="AlarmBand"/>'s numeric values are structural — five bands, zero to four
    /// — so comparing against the raw number is comparing against the band scale, not
    /// against a tuning constant.
    /// </remarks>
    private const int DefaultAlarmBandFloor = 2;

    /// <summary>Cooldown scale, absent the table.</summary>
    private const int DefaultSupportCooldownPercent = 100;

    /// <summary>Steps before a compromised Handler is taken, absent the table.</summary>
    private const int DefaultCompromiseSteps = 300;

    /// <summary>How far a smoke drop is heard, absent a noise profile.</summary>
    private const int DefaultSmokeRadiusCm = 900;

    /// <summary>The noise profile a dropped smoke is heard through.</summary>
    private const int DefaultSmokeNoiseProfileId = 12321;
}