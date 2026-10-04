using System.Globalization;

using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

using ProjectSpy.Sim.Batch;

namespace ProjectSpy.Sim.Play;

/// <summary>
/// The tactical half of <c>play</c>: an infiltration, typed at a keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Commands are queued, never immediate.</b> Every input goes through
/// <see cref="TacticalState.PendingOrders"/> and takes effect at the next step boundary
/// (rule 14), which is the same channel the game's own input goes through. A terminal
/// client that acted the instant you pressed a key would be testing a control model the
/// shipped game does not have.
/// </para>
/// <para>
/// <b>Paused by default.</b> A player who has to hold down a key to watch a guard walk
/// past is not playing the game, and pausing costs nothing here — it is the difference
/// between a client that proves the tension and one that proves nothing.
/// </para>
/// </remarks>
public sealed class TacticalClient
{
    private readonly TextWriter _out;
    private readonly TextReader _in;

    private readonly GameSession _session;
    private readonly MissionFixture _fixture;

    private readonly SquadPolicyDriver _auto;

    private bool _running = true;
    private bool _autoPilot;

    /// <summary>Steps to run per "go" when unpaused.</summary>
    private int _burst = 1;

    /// <summary>Which floor the player is looking at.</summary>
    private int _floor;

    /// <summary>
    /// Where the player last told the squad to go, held until they arrive.
    /// </summary>
    /// <remarks>
    /// <c>SquadControl</c> treats a standing order as a single action to start, not a
    /// standing instruction: it pops the order off the queue as soon as the member
    /// begins the first hop. That is right for the rules engine — a queued order the
    /// player can no longer cancel is a queue of stale intentions — but it means "go to
    /// room 4" walks one doorway and then stops. The player meant the other thing, so the
    /// client keeps the destination and re-issues every step until the squad is there.
    /// Holding the intent in the presentation layer rather than in the rules is what lets
    /// the two keep disagreeing about it without either being wrong.
    /// </remarks>
    private SiteRoomId? _moveTarget;

    /// <summary>
    /// The strategy the autopilot uses, settable before the mission starts.
    /// </summary>
    /// <remarks>
    /// A property rather than a command because the most useful question about a policy is
    /// asked before you watch it, not after: pick the policy, then watch the same building
    /// play out four different ways.
    /// </remarks>
    public SquadPolicy Policy
    {
        get => _auto.Policy;
        set => _auto.SetPolicy(value);
    }

    public TacticalClient(
        TextWriter output,
        TextReader input,
        MissionFixture fixture,
        bool autoPilot = false)
    {
        _out = output;
        _in = input;
        _fixture = fixture;
        _autoPilot = autoPilot;
        _auto = new SquadPolicyDriver(SquadPolicy.Stealth);
        _session = new GameSession(fixture.World);
        _session.EnterTacticalMode(fixture.Mission);
        _floor = fixture.Mission.RoomOf(fixture.Mission.Control.ControlledActor(fixture.Mission)!)?.FloorIndex ?? 0;
    }

    /// <summary>Runs until the mission ends or the player quits.</summary>
    public void Run()
    {
        Banner();

        // --auto means unattended: the squad plays itself to the end, then debriefs.
        // Anything else is the interactive client, which waits for a person at every
        // prompt. Driving a scripted run through ReadLine would hang forever with no
        // terminal attached — and the point of --auto is a run with nobody attached.
        if (_autoPilot)
        {
            while (_running && !_fixture.Mission.IsOver && _fixture.Mission.Step < MissionRun.StepCeiling)
                Step(1);

            Debrief();
            return;
        }

        while (_running && !_fixture.Mission.IsOver)
        {
            Draw();
            string? line = _in.ReadLine();

            if (line is null)
                return;

            if (!Command(line.Trim()))
                return;
        }

        Debrief();
    }

    /// <summary>
    /// Executes one typed command.
    /// </summary>
    /// <returns>False to leave the mission.</returns>
    private bool Command(string line)
    {
        if (line.Length == 0)
            return true;

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0].ToLowerInvariant();

        switch (verb)
        {
            case "q" or "quit":
                return false;

            case "?" or "h" or "help":
                Help();
                return true;

            case "go" or "g":
                Step(Burst(parts, _burst));
                return true;

            case "step" or "s":
                Step(1);
                return true;

            case "pause":
                _burst = 0;
                _out.WriteLine("paused.");
                return true;

            case "auto":
                _autoPilot = !_autoPilot;
                _out.WriteLine(_autoPilot
                    ? "autopilot on — the squad is playing itself."
                    : "autopilot off — you are driving again.");
                return true;

            case "burst":
                _burst = Burst(parts, 1);
                _out.WriteLine($"burst {_burst}");
                return true;

            case "floor" or "f":
                Floor(Burst(parts, _floor));
                return true;

            case "next":
                NextFloor(1);
                return true;

            case "prev":
                NextFloor(-1);
                return true;

            case "switch" or "w":
                Switch(parts);
                return true;

            case "squad":
                DrawSquad();
                return true;

            case "rooms":
                DrawRooms();
                return true;

            case "doors" or "conns":
                DrawDoors();
                return true;

            case "actors" or "who":
                DrawActors();
                return true;

            case "obj" or "objective":
                DrawObjective();
                return true;

            case "why":
                Why();
                return true;

            case "log":
                DrawLog();
                return true;

            case "order" or "o":
                Order(parts);
                return true;

            case "act":
                Act(parts);
                return true;

            case "policy":
                SetPolicy(parts);
                return true;

            default:
                _out.WriteLine($"'{verb}' is not a command. ? for the list.");
                return true;
        }
    }

    /// <summary>Runs the mission forward, drawing as it goes.</summary>
    private void Step(int steps)
    {
        for (int i = 0; i < steps && !_fixture.Mission.IsOver; i++)
        {
            if (_autoPilot)
                _auto.Think(_fixture.Mission, _fixture.Composition);
            else
                SustainMove();

            _session.AdvanceTacticalStep();

            if (i % 50 == 49)
                _out.WriteLine($"  ... step {_fixture.Mission.Step}");
        }
    }

    /// <summary>
    /// Re-issues the remembered destination to anybody who has not arrived yet.
    /// </summary>
    /// <remarks>
    /// Deliberately silent. Re-issuing four times a second and telling the player about
    /// it would bury the screen in lines that all say the same thing; the destination is
    /// on screen as a room label and the arrival is visible, so neither needs a message.
    /// </remarks>
    private void SustainMove()
    {
        if (_moveTarget is not { } target)
            return;

        var pending = new List<AgentId>();

        foreach (ProjectSpy.Core.Squad.SquadMember member in _fixture.Composition.Members)
        {
            if (_fixture.Mission.Control.Controlled == member.AgentId)
                continue;

            TacticalActor? actor = _fixture.Mission.AgentActor(member.AgentId);

            if (actor is null || !actor.CanAct)
                continue;

            if (_fixture.Mission.RoomOf(actor)?.Id == target)
                continue;

            pending.Add(member.AgentId);
        }

        if (pending.Count == 0)
        {
            _moveTarget = null;
            return;
        }

        SiteRoom? destination = _fixture.Mission.Layout.Find(target);

        if (destination is null)
        {
            _moveTarget = null;
            return;
        }

        var order = new SquadStandingOrder(
            SquadOrderKind.MoveTo,
            new TacticalPosition(destination.FloorIndex, destination.StartX));

        foreach (AgentId agentId in pending)
            _fixture.Mission.Control.Issue(_fixture.Composition, agentId, order);
    }

    private int Burst(string[] parts, int fallback)
    {
        if (parts.Length < 2)
            return fallback;

        return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Math.Max(0, n)
            : fallback;
    }

    private void Floor(int floor)
    {
        _floor = Math.Max(0, floor);
        _out.WriteLine($"showing floor {_floor}");
    }

    private void NextFloor(int delta)
    {
        SiteLayout layout = _fixture.Mission.Layout;
        int top = layout.MainSite.Floors.Count > 0
            ? layout.MainSite.Floors[^1].Index
            : 0;

        _floor = Math.Clamp(_floor + delta, 0, top);
    }

    /// <summary>Hands the player's hand to another squad member.</summary>
    private void Switch(string[] parts)
    {
        if (parts.Length < 2)
        {
            _out.WriteLine("switch <n> — 1-based index of a squad member, or 'list'.");
            return;
        }

        IReadOnlyList<ProjectSpy.Core.Squad.SquadMember> members = _fixture.Composition.Members;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted)
            || wanted < 1
            || wanted > members.Count)
        {
            _out.WriteLine($"no such squad member. there are {members.Count}.");
            return;
        }

        AgentId target = members[wanted - 1].AgentId;

        if (!_fixture.Mission.Control.SwitchTo(target))
        {
            _out.WriteLine("cannot switch to that member.");
            return;
        }

        TacticalActor? actor = _fixture.Mission.AgentActor(target);

        if (actor is not null)
            _floor = actor.Position.FloorIndex;

        _out.WriteLine($"now driving {RoleName(target)}");
    }

    private string RoleName(AgentId agentId)
    {
        SquadMember? member = _fixture.Composition.MemberFor(agentId);

        return member?.Role?.RoleKey ?? agentId.ToString();
    }

    /// <summary>
    /// Issues a standing order to the squad.
    /// </summary>
    /// <remarks>
    /// `order move &lt;room&gt;`, `order hold`, `order regroup`, `order abort`, `order overwatch
    /// &lt;l|r&gt;`, `order extract`. The member is optional and defaults to every member who
    /// is not the one under the player's hand — which is the common case, and the reason
    /// the brief calls for a squad rather than one character.
    /// </remarks>
    private void Order(string[] parts)
    {
        if (parts.Length < 2)
        {
            _out.WriteLine("order <what> [member] — move <room> | hold | regroup | abort | extract | overwatch <l|r>");
            return;
        }

        SquadStandingOrder order;
        string what = parts[1].ToLowerInvariant();

        switch (what)
        {
            case "move":
            {
                if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int room))
                {
                    _out.WriteLine("order move <roomId>");
                    return;
                }

                SiteRoom? target = _fixture.Mission.Layout.Find(new SiteRoomId(room));

                if (target is null)
                {
                    _out.WriteLine($"no room {room} on this site.");
                    return;
                }

                // A standing MoveTo names a point, not a room, so it is resolved to the
                // room's near edge — the honest interpretation of "go there" without
                // inventing a destination inside it.
                _moveTarget = target.Id;
                order = new SquadStandingOrder(
                    SquadOrderKind.MoveTo,
                    new TacticalPosition(target.FloorIndex, target.StartX));
                break;
            }

            case "hold":
                _moveTarget = null;
                order = new SquadStandingOrder(SquadOrderKind.Hold);
                break;

            case "regroup":
                _moveTarget = null;
                order = new SquadStandingOrder(SquadOrderKind.Regroup);
                break;

            case "overwatch":
                order = new SquadStandingOrder(
                    SquadOrderKind.OverwatchDirection,
                    Facing: parts.Length > 2 && parts[2].StartsWith("l", StringComparison.OrdinalIgnoreCase)
                        ? Facing.Left
                        : Facing.Right);
                break;

            case "extract" or "requestextraction":
                order = new SquadStandingOrder(SquadOrderKind.RequestExtraction);
                break;

            case "force" or "open":
            {
                // Opening a door is an individual act, not a posture the squad can hold:
                // there is no "everyone be quiet at that door" in the order set, and
                // pretending otherwise would mean the client silently queued something
                // the rules cannot express. Routed to the direct channel instead.
                if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int door))
                {
                    _out.WriteLine("order force <connectionId> — see 'doors'");
                    return;
                }

                Door(door, what == "force" ? "action.interact_force_door" : "action.interact_open_door");
                return;
            }

            case "abort":
                IssueAbort();
                return;

            default:
                _out.WriteLine($"'{what}' is not an order.");
                return;
        }

        int issued = 0;
        List<string> refusals = new();

        foreach (ProjectSpy.Core.Squad.SquadMember member in _fixture.Composition.Members)
        {
            if (_fixture.Mission.Control.Controlled == member.AgentId)
                continue;

            SquadOrderResult result = _fixture.Mission.Control.Issue(_fixture.Composition, member.AgentId, order);

            if (result.Ok)
                issued++;
            else
                refusals.Add($"{member.Role?.RoleKey ?? "?"}: {result.Reason}");
        }

        // The reasons come out of the issue loop itself. Asking the squad afterwards
        // "would that order have worked?" by sending it a different order to provoke the
        // answer would be a second, unrequested command smuggled in — the player asked for
        // one move and would get a hold they did not type.
        _out.WriteLine(refusals.Count == 0
            ? $"ordered {issued} members"
            : $"ordered {issued}, {refusals.Count} refused"
                + $" ({string.Join("; ", refusals.Take(3))})");
    }

    private void IssueAbort()
    {
        var rng = new RngStreams(_fixture.Seed)[RngStreams.StreamKind.Tactical];

        bool accepted = _fixture.Mission.Control.AbortAll(
            _fixture.Mission, _fixture.Composition, _fixture.Mission.CommandPost, rng);

        _out.WriteLine(accepted
            ? "abort called. the squad is walking out; it will take the steps it takes."
            : "abort is still on cooldown.");
    }

    /// <summary>
    /// Issues a direct action to the member under the player's hand.
    /// </summary>
    /// <remarks>
    /// The direct channel, for the things a standing order cannot express: opening a
    /// door, hacking the objective, shooting somebody. `act hack`, `act work`, `act shoot
    /// &lt;n&gt;`, `act door &lt;connectionId&gt;`, `act stop`.
    /// </remarks>
    private void Act(string[] parts)
    {
        TacticalActor? actor = _fixture.Mission.Control.ControlledActor(_fixture.Mission);

        if (actor is null)
        {
            _out.WriteLine("nobody is under your hand.");
            return;
        }

        if (parts.Length < 2)
        {
            _out.WriteLine("act <hack|work|observe|door|shoot|stop> [id]");
            return;
        }

        TacticalOrder order;

        switch (parts[1].ToLowerInvariant())
        {
            case "hack":
                order = new TacticalOrder(
                    actor.Id, Action("action.interact_hack_terminal"),
                    InteractableId: _fixture.Mission.ObjectiveOutcome.WorkInteractableId);
                break;

            case "work":
            {
                string? nameKey = SimulationRules.ObjectiveRuleFor(_fixture.Mission.ObjectiveOutcome.Type)?.WorkAction;
                order = new TacticalOrder(
                    actor.Id, Action(nameKey ?? "action.interact_hack_terminal"),
                    InteractableId: _fixture.Mission.ObjectiveOutcome.WorkInteractableId);
                break;
            }

            case "observe":
                order = new TacticalOrder(actor.Id, Action("action.support_observe"));
                break;

            case "door" or "open" or "force" or "shut" or "lock":
            {
                string doorAction = parts[1].ToLowerInvariant() switch
                {
                    "open" => "action.interact_open_door",
                    "force" => "action.interact_force_door",
                    "shut" => "action.interact_close_door",
                    _ => "action.traverse_door",
                };

                if (parts.Length < 3 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int door))
                {
                    _out.WriteLine($"act {parts[1]} <connectionId> — see 'doors'");
                    return;
                }

                order = new TacticalOrder(actor.Id, Action(doorAction), ConnectionId: new SiteConnectionId(door));
                break;
            }

            case "shoot":
            {
                TacticalActor? target = parts.Length > 2 && int.TryParse(parts[2], out int who)
                    ? _fixture.Mission.Actor(new TacticalActorId(who))
                    : null;

                order = new TacticalOrder(actor.Id, Action("action.combat_shoot"), TargetActorId: target?.Id ?? default);
                break;
            }

            case "stop":
                order = new TacticalOrder(actor.Id, TacticalOrder.StopActionId);
                break;

            default:
                _out.WriteLine($"'act {parts[1]}' is not an action.");
                return;
        }

        if (order.ActionId == 0 && !order.IsStop)
        {
            _out.WriteLine("no such action in the tables.");
            return;
        }

        TacticalOrderResult check = ActionSystem.Validate(_fixture.Mission, order);

        if (check.IsRejected)
        {
            _out.WriteLine($"refused: {check.Reason}");
            return;
        }

        _fixture.Mission.PendingOrders.Add(order);
        _out.WriteLine("queued for the next step.");
    }

    /// <summary>Switches the autopilot between the four strategies.</summary>
    private void SetPolicy(string[] parts)
    {
        if (parts.Length < 2
            || !Enum.TryParse(parts[1], ignoreCase: true, out SquadPolicy policy)
            || !Enum.IsDefined(policy))
        {
            _out.WriteLine("policy <stealth|aggressive|speedrun|cautious>");
            return;
        }

        _auto.SetPolicy(policy);
        _out.WriteLine($"autopilot policy: {policy}");
    }

    private static int Action(string nameKey) => SimulationRules.TacticalActionIdFor(nameKey);

    private void Draw()
    {
        _out.Write(TacticalLane.Render(_fixture.Mission, _floor));
        _out.WriteLine();
        DrawSquad();
    }

    private void DrawSquad()
    {
        _out.WriteLine("  squad:");

        int index = 1;

        foreach (ProjectSpy.Core.Squad.SquadMember member in _fixture.Composition.Members)
        {
            TacticalActor? actor = _fixture.Mission.AgentActor(member.AgentId);

            if (actor is null)
                continue;

            bool mine = _fixture.Mission.Control.Controlled == member.AgentId;
            string marker = mine ? "*" : " ";

            SiteRoom? room = _fixture.Mission.RoomOf(actor);
            string where = room is null ? "off-map" : Shorten(room.NameKey);

            _out.WriteLine(
                $"   {marker}{index}. {member.Role!.RoleKey,-11} "
                + $"hp {actor.Health,3}/{actor.MaxHealth,-3} stam {actor.Stamina,3} "
                + $"f{actor.Position.FloorIndex} {where,-12} {actor.Condition}"
                + $"  doing {Doing(actor)}"
                + (mine ? "   <- you" : string.Empty));

            index++;
        }
    }

    /// <summary>
    /// Lists the building's rooms, so room ids are something the player can read rather
    /// than something they have to guess.
    /// </summary>
    /// <remarks>
    /// <c>order move &lt;room&gt;</c> takes a room id, and a command that takes an id the
    /// screen never shows is a command the player can only discover by trying numbers.
    /// Unobserved rooms are listed but marked, because their existence is on the plan —
    /// what is not known is what is in them.
    /// </remarks>
    private void DrawRooms()
    {
        SiteLayout layout = _fixture.Mission.Layout;

        _out.WriteLine("  rooms:");

        foreach (SiteRoom room in layout.AllRooms.OrderBy(r => r.FloorIndex).ThenBy(r => r.Id.Value))
        {
            SiteRoom? occupantHere = layout.RoomContaining(
                _fixture.Mission.Control.ControlledActor(_fixture.Mission)?.Position ?? TacticalPosition.None);

            string marks = room.Role.HasFlag(SiteRoomRole.Objective) ? " OBJECTIVE" : string.Empty;

            marks += room.Role.HasFlag(SiteRoomRole.Extraction) ? " EXIT" : string.Empty;

            marks += room.Role.HasFlag(SiteRoomRole.ForwardPost) ? " FORWARD-POST" : string.Empty;

            _out.WriteLine(
                $"   {room.Id.Value,-4} f{room.FloorIndex} {Shorten(room.NameKey),-14}"
                + $" x {room.StartX.Raw,6}..{room.EndX.Raw,-6} {room.Span.Raw,6}cm {marks}"
                + (layout.IsRoomObserved(room.Id) ? string.Empty : $"   (unobserved{(occupantHere?.Id == room.Id ? ", you are here" : string.Empty)})"));
        }
    }

    private void DrawDoors()
    {
        SiteLayout layout = _fixture.Mission.Layout;
        TacticalState state = _fixture.Mission;

        _out.WriteLine("  doors:");

        foreach (SiteConnection connection in layout.Connections
            .OrderBy(c => c.RoomA.Value).ThenBy(c => c.RoomB.Value))
        {
            SiteRoom? a = layout.Find(connection.RoomA);
            SiteRoom? b = layout.Find(connection.RoomB);

            string where = connection.IsVertical
                ? $"f{a?.FloorIndex}->f{b?.FloorIndex} {connection.Kind}"
                : $"{Shorten(a?.NameKey ?? "?")} <-> {Shorten(b?.NameKey ?? "?")} {connection.Kind}";

            _out.WriteLine(
                $"   {connection.Id.Value,-4} {state.StateOf(connection.Id),-11} "
                + $"rooms {connection.RoomA.Value}/{connection.RoomB.Value}  "
                + $"{(connection.IsVertical ? "vertical" : connection.TraverseSteps.ToString())}  {where}"
                + (connection.IsLocked && state.StateOf(connection.Id) != ConnectionState.Locked
                    ? "  (locks: none)"
                    : string.Empty));
        }
    }

    /// <summary>Every actor the player knows about, with where they were last perceived.</summary>
    private void DrawActors()
    {
        _out.WriteLine("  actors:");

        foreach (TacticalActor actor in _fixture.Mission.SortedActors)
        {
            SiteRoom? room = _fixture.Mission.RoomOf(actor);
            bool mine = _fixture.Mission.Control.Controlled == actor.AgentId;

            _out.WriteLine(
                $"   {actor.Id.Value,-4} {Kind(actor),-8} "
                + $"f{actor.Position.FloorIndex} x{actor.Position.X.Raw,-7} "
                + $"hp {actor.Health,3}/{actor.MaxHealth,-3} stam {actor.Stamina,3} "
                + $"{actor.Posture,-7} {actor.Condition,-9} {Shorten(room?.NameKey ?? "?"),-12}"
                + (mine ? "  <- you" : string.Empty));
        }

        _out.WriteLine("  guard memory (last perceived position, which may be stale):");

        foreach (TacticalActor member in _fixture.Mission.Squad)
        {
            if (!member.Memory.HasContact)
                continue;

            _out.WriteLine(
                $"   {Shorten(member.NameKey ?? member.AgentId.ToString()),-12} last saw actor "
                + $"{member.Memory.LastSeenActorId.Value} at {member.Memory.LastSeen.X.Raw} "
                + $"on step {member.Memory.LastSeenStep}"
                + (member.Memory.HasIdentification ? "  IDENTIFIED YOU" : string.Empty));
        }
    }

    /// <summary>Acts on a door as whoever is under the player's hand.</summary>
    private void Door(int connectionId, string actionNameKey)
    {
        TacticalActor? actor = _fixture.Mission.Control.ControlledActor(_fixture.Mission);

        if (actor is null)
        {
            _out.WriteLine("nobody is under your hand.");
            return;
        }

        var order = new TacticalOrder(
            actor.Id, Action(actionNameKey), ConnectionId: new SiteConnectionId(connectionId));

        TacticalOrderResult check = ActionSystem.Validate(_fixture.Mission, order);

        if (check.IsRejected)
        {
            _out.WriteLine($"refused: {check.Reason}");
            return;
        }

        _fixture.Mission.PendingOrders.Add(order);
        _out.WriteLine($"queued {Shorten(actionNameKey)} on connection {connectionId}.");
    }

    /// <summary>
    /// What the objective is actually asking for, and where.
    /// </summary>
    /// <remarks>
    /// The single most useful thing to be able to check and the easiest thing to leave
    /// invisible. A bar reading "40%" tells the player nothing about whether the 40%
    /// happened in the right room; this tells them both, and when the two disagree the
    /// player is the one who finds out first.
    /// </remarks>
    private void DrawObjective()
    {
        TacticalState state = _fixture.Mission;
        ObjectiveOutcome outcome = state.ObjectiveOutcome;
        var rule = SimulationRules.ObjectiveRuleFor(outcome.Type);

        _out.WriteLine("  objective:");
        _out.WriteLine($"   type        {outcome.Type}  ({Shorten(outcome.NameKey)})");
        _out.WriteLine($"   state       {(outcome.IsComplete ? "COMPLETE" : outcome.IsFailed ? $"FAILED {outcome.Failure}" : $"in progress, {outcome.Percent}%")}");
        _out.WriteLine($"   work        {outcome.WorkSteps}/{rule?.WorkSteps ?? 0} steps   exfil {rule?.ExfilSteps ?? 0}");
        _out.WriteLine($"   room        {Shorten(state.Layout.Find(state.Layout.ObjectiveRoomId)?.NameKey ?? "?")} ({state.Layout.ObjectiveRoomId})");
        _out.WriteLine($"   alarm cap   {Shorten(((AlarmBand)(rule?.AlarmToleranceBand ?? 0)).ToString())}   now {state.Alarm.Band}");

        if (outcome.WorkInteractableId == 0)
        {
            _out.WriteLine("   interactable  none — this site generated no terminal or objective prop,");
            _out.WriteLine("                  so the work has to be done in the objective room itself.");
            return;
        }

        foreach (SiteInteractable interactable in state.Layout.Interactables)
        {
            if (interactable.Id != outcome.WorkInteractableId)
                continue;

            SiteRoom? room = state.Layout.Find(interactable.RoomId);

            _out.WriteLine(
                $"   interactable {interactable.Id} {interactable.Kind} in "
                + $"{Shorten(room?.NameKey ?? "?")} ({interactable.RoomId})"
                + (interactable.RoomId == state.Layout.ObjectiveRoomId
                    ? string.Empty
                    : "   <- NOT the objective room"));
            return;
        }

        _out.WriteLine($"   interactable {outcome.WorkInteractableId} — no such prop on this site");
    }

    /// <summary>
    /// What a member is doing right now, in words.
    /// </summary>
    /// <remarks>
    /// The single most load-bearing line on the screen. Without it a member who is
    /// standing still is indistinguishable from a member who is obeying an order that
    /// cannot be carried out, and the player has no way to tell a bug from a decision.
    /// </remarks>
    private static string Doing(TacticalActor actor)
    {
        if (!actor.CanAct)
            return actor.Condition.ToString().ToLowerInvariant();

        if (actor.Action is not { } action)
            return "nothing";

        string what = action.ActionId == TacticalOrder.StopActionId ? "stop" : ActionName(action.ActionId);

        if (action.IsComplete)
            return $"{what} (finishing)";

        return $"{what} {action.StepsSpent}/{action.TotalSteps}";
    }

    /// <summary>
    /// Explains why the squad is not doing what it was told.
    /// </summary>
    /// <remarks>
    /// The question every player asks within thirty seconds and that no other screen
    /// answers. A member standing still looks identical whether the order was dropped,
    /// refused, refused every step for a reason nobody can see, or is simply waiting on
    /// the ten-step reaction delay — and those four need four different answers.
    /// </remarks>
    private void Why()
    {
        TacticalState state = _fixture.Mission;

        _out.WriteLine("  why:");

        foreach (ProjectSpy.Core.Squad.SquadMember member in _fixture.Composition.Members)
        {
            TacticalActor? actor = state.AgentActor(member.AgentId);

            if (actor is null)
                continue;

            SquadMemberOrders? orders = state.Control.For(member.AgentId);
            bool mine = state.Control.Controlled == member.AgentId;

            string because;

            if (mine)
                because = "under your hand — orders are yours to give";
            else if (!actor.CanAct)
                because = $"{actor.Condition}, cannot act";
            else if (orders is null)
                because = "no orders object";
            else if (orders.Held)
                because = "held";
            else if (orders.Queue.Count > 0)
                because = $"{orders.Queue.Count} queued; reacting in "
                    + $"{Math.Max(0, SimulationRules.Squad("squad_role_reaction_steps", 10) - (state.Step - orders.LastOrderStep))} steps";
            else if (actor.Action is { } action)
                because = $"mid-action: {Doing(actor)}";
            else
                because = "no order; acting on role behaviour";

            _out.WriteLine($"   {member.Role!.RoleKey,-11} {Shorten(state.RoomOf(actor)?.NameKey ?? "?"),-12} {because}");
        }

        if (_moveTarget is { } target)
        {
            SiteRoom? destination = state.Layout.Find(target);
            _out.WriteLine($"  destination: {Shorten(destination?.NameKey ?? "?")} ({target})");

            foreach (TacticalActor actor in state.Squad)
            {
                SiteRoom? here = state.RoomOf(actor);

                if (here is null || here.Id == target)
                    continue;

                TacticalPath path = Pathfinder.FindRoute(state.Layout, state.Doors, here.Id, target);

                if (!path.ReachedTarget || path.Connections.Count == 0)
                {
                    _out.WriteLine($"   {Shorten(actor.NameKey ?? "?")}: no route");
                    continue;
                }

                SiteConnection? first = state.Layout.FindConnectionFor(path.Connections[0]);

                if (first is null)
                    continue;

                _out.WriteLine(
                    $"   {Shorten(actor.NameKey ?? "?"),-10} at {Shorten(here.NameKey),-10} -> first hop: connection "
                    + $"{first.Id.Value} ({first.Kind}) state {state.StateOf(first.Id)}, "
                    + $"{path.Connections.Count} hops, {path.TotalSteps} steps");

                if (state.Control.Controlled == actor.AgentId)
                    continue;

                var probe = new SquadStandingOrder(
                    SquadOrderKind.MoveTo,
                    new TacticalPosition(destination!.FloorIndex, destination.StartX));

                TacticalOrder? action = RoleBehaviours.TacticalOrderFor(
                    state, _fixture.Composition, actor, probe, state.CommandPost);

                if (action is null)
                {
                    _out.WriteLine($"      the order layer produced nothing for this move");
                    continue;
                }

                TacticalOrderResult verdict = ActionSystem.Validate(state, action);

                _out.WriteLine(
                    $"      the order layer wants {ActionName(action.ActionId)}"
                    + (action.ConnectionId.IsValid ? $" through connection {action.ConnectionId.Value}" : string.Empty)
                    + (action.Target.IsValid ? $" to x{action.Target.X.Raw}" : string.Empty)
                    + (verdict.IsRejected ? $"  -- REFUSED: {verdict.Reason}" : "  -- legal"));
            }
        }
    }

    /// <summary>The localization key of an action, looked up through the table.</summary>
    private static string ActionName(int actionId)
    {
        foreach (var row in SimulationRules.AllTacticalActions())
        {
            if (row.Id == actionId)
                return Shorten(row.NameKey);
        }

        return $"action {actionId}";
    }

    /// <summary>What an actor is, for the listing.</summary>
    /// <remarks>
    /// Three labels, not two. Calling a civilian an "agent" would put the public on the
    /// same line as the player's own squad and make the roster unreadable — and civilians
    /// are the one thing in the building whose presence is not the player's business.
    /// </remarks>
    private static string Kind(TacticalActor actor)
        => actor.IsGuard ? "guard" : actor.IsAgent ? "squad" : "civilian";

    private void DrawLog()
    {
        IReadOnlyList<MissionLogEntry> log = _fixture.Mission.Log;

        if (log.Count == 0)
        {
            _out.WriteLine("  (nothing has happened yet)");
            return;
        }

        // Every step emits one, and twelve steps of that would bury the twelve events a
        // player actually wants to read. The heartbeat is in the header.
        var interesting = new List<MissionLogEntry>(log.Count);

        foreach (MissionLogEntry entry in log)
        {
            if (entry.Key != "step.completed")
                interesting.Add(entry);
        }

        if (interesting.Count == 0)
        {
            _out.WriteLine("  (nothing but steps so far)");
            return;
        }

        int from = Math.Max(0, interesting.Count - 12);

        for (int i = from; i < interesting.Count; i++)
        {
            MissionLogEntry entry = interesting[i];

            _out.WriteLine(
                $"   step {entry.Step,-6} {entry.Key}"
                + (entry.Args.Count == 0 ? string.Empty : "  " + string.Join(",", entry.Args)));
        }
    }

    private void Debrief()
    {
        TacticalState state = _fixture.Mission;

        MissionResolution resolution = ResolveSystem.Classify(
            state, state.ObjectiveOutcome, aborted: state.AbortCalled);

        MissionReport report = MissionReportBuilder.Build(
            state, _fixture.Composition, state.ObjectiveOutcome, state.CommandPost);

        _out.WriteLine();
        _out.WriteLine("  ================= debrief =================");
        _out.WriteLine($"  outcome        {state.Outcome}");
        _out.WriteLine($"  resolved as    {resolution.Class}  ({report.ClassNameKey})");
        _out.WriteLine($"  objective      {(state.ObjectiveOutcome.IsComplete ? "complete" : "incomplete")}"
            + $"  {state.ObjectiveOutcome.Percent}%");
        _out.WriteLine($"  steps          {state.Step}");
        _out.WriteLine($"  lost           {resolution.Lost}");
        _out.WriteLine($"  team extracted {resolution.TeamExtracted}");
        _out.WriteLine($"  peak alarm     {resolution.EndBand}");
        _out.WriteLine($"  heat           {resolution.Heat}");
        _out.WriteLine($"  evidence       {resolution.Evidence}");
        _out.WriteLine($"  lethal acts    {state.LethalActs}");
        _out.WriteLine();
        _out.WriteLine("  summary:");

        foreach ((string Key, IReadOnlyList<int> Args) in report.Summary)
            _out.WriteLine($"   {Key}");

        _out.WriteLine();
        _out.WriteLine("  the locale for those keys is data/localization/. keys first, prose later.");
    }

    private void Banner()
    {
        SiteLayout layout = _fixture.Mission.Layout;

        _out.WriteLine("  ================= tactical =================");
        _out.WriteLine($"  template {_fixture.TemplateId}   tier {_fixture.Tier}   objective {_fixture.Objective}");
        _out.WriteLine($"  seed {_fixture.Seed}   rooms {layout.AllRooms.Count}   guards {_fixture.Mission.Guards.Count}"
            + $"   squad {_fixture.Composition.Members.Count}");
        _out.WriteLine($"  objective room {layout.ObjectiveRoomId}   extraction {string.Join(",", layout.ExtractionRoomIds)}");
        _out.WriteLine("  paused. type ? for commands, g to advance, o to order the squad.");
        _out.WriteLine();
    }

    private void Help()
    {
        _out.WriteLine();
        _out.WriteLine("    g [n]      advance n steps (default burst)");
        _out.WriteLine("    s          advance one step");
        _out.WriteLine("    pause      stop advancing");
        _out.WriteLine("    burst <n>  how many steps g runs");
        _out.WriteLine("    f <n>      show floor n      next / prev   change floor");
        _out.WriteLine("    w <n>      take control of squad member n");
        _out.WriteLine("    o <what>   order the squad:  move <room> | hold | regroup | overwatch l|r | extract");
        _out.WriteLine("    o force <conn>  or open <conn> — get through a door (noisy, and only one agent)");
        _out.WriteLine("    o abort    call the abort");
        _out.WriteLine("    act <what> act yourself:    hack | work | observe | door|open|force|shut <conn> | shoot <actor> | stop");
        _out.WriteLine("    auto       toggle autopilot     policy <name>  set the autopilot's strategy");
        _out.WriteLine("    squad      the roster          rooms      the building's rooms and their ids");
        _out.WriteLine("    doors      the connections     actors     everybody, and what the squad has seen");
        _out.WriteLine("    obj        what the objective is and where it has to be done");
        _out.WriteLine("    log        recent events");
        _out.WriteLine("    q          leave the mission");
        _out.WriteLine();
    }

    private static string Shorten(string nameKey)
    {
        int dot = nameKey.LastIndexOf('.');

        return dot >= 0 && dot < nameKey.Length - 1 ? nameKey[(dot + 1)..] : nameKey;
    }
}