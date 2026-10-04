using System.Text;

using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Sim.Play;

/// <summary>
/// Draws the current floor of a mission as a lane of ASCII.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the game's entire visual layer, and it has to carry the game.</b> There is
/// no art, no lighting pass and no fog-of-war shader — a player looking at this is
/// looking at everything the design can possibly show them. Which is the point of the
/// stage: if the tension is not legible here, no amount of lighting will create it.
/// </para>
/// <para>
/// <b>It draws what the player knows, not what is true.</b> A room nobody has entered is
/// drawn as a sealed block. A guard nobody has seen is not drawn at all — only a guard
/// somebody has perceived appears, at the place they were last perceived, which is what
/// makes <see cref="PerceptionMemory"/>'s distinction between "there is a guard" and "a
/// guard was here" visible rather than merely implemented. Drawing the truth would make
/// the fog system look like it works while proving it does nothing.
/// </para>
/// <para>
/// <b>One row per room, not a grid.</b> Tactical space in this game is a line: rooms are
/// half-open intervals on a floor, and connections sit at a point between two of them.
/// A row per room with a column per actor position inside it is the honest picture, and
/// it is the picture the rules are written against.
/// </para>
/// </remarks>
public static class TacticalLane
{
    /// <summary>How many columns an interior metre is worth on screen.</summary>
    private const int ColumnsPerRoom = 24;

    /// <summary>
    /// Renders one floor.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="floorIndex">Which floor to draw.</param>
    public static string Render(TacticalState state, int floorIndex)
    {
        var sb = new StringBuilder(8 * 1024);

        SiteFloor? floor = FindFloor(state.Layout, floorIndex);

        if (floor is null)
        {
            sb.AppendLine($"(no floor {floorIndex} on this site)");
            return sb.ToString();
        }

        Header(sb, state, floor);

        foreach (SiteRoom room in floor.Rooms)
            Room(sb, state, room);

        sb.AppendLine();
        Legend(sb);

        return sb.ToString();
    }

    private static SiteFloor? FindFloor(SiteLayout layout, int floorIndex)
    {
        foreach (SiteFloor floor in layout.MainSite.Floors)
        {
            if (floor.Index == floorIndex)
                return floor;
        }

        if (layout.ForwardPost is not null)
        {
            foreach (SiteFloor floor in layout.ForwardPost.Floors)
            {
                if (floor.Index == floorIndex)
                    return floor;
            }
        }

        return null;
    }

    private static void Header(StringBuilder sb, TacticalState state, SiteFloor floor)
    {
        sb.AppendLine($"  floor {floor.Index,-2}  step {state.Step,-6} alarm {Band(state.Alarm.Band),-11}"
            + $"objective {state.ObjectiveOutcome.Percent,3}%  outcome {state.Outcome}");
        sb.AppendLine();
    }

    private static void Room(StringBuilder sb, TacticalState state, SiteRoom room)
    {
        bool observed = state.Layout.IsRoomObserved(room.Id);

        sb.Append("  ");
        sb.Append(RoomLabel(room));

        if (!observed)
        {
            // Sealed, not blank. A blank row reads as "nothing here"; a sealed row reads
            // as "something here you have not seen", which is the actual situation and the
            // reason the fog system exists.
            sb.AppendLine(new string('█', ColumnsPerRoom));
            return;
        }

        var cells = new char[ColumnsPerRoom];

        for (int i = 0; i < cells.Length; i++)
            cells[i] = LightChar(state, room, i);

        PlaceActors(state, room, cells);
        PlaceLights(state, room, cells);
        PlaceConnections(state, room, cells);

        sb.AppendLine(new string(cells));
    }

    /// <summary>The room's name and whatever roles it carries.</summary>
    private static string RoomLabel(SiteRoom room)
    {
        var tags = new List<string>();

        if (room.Role.HasFlag(SiteRoomRole.Entrance))
            tags.Add("EXIT");

        if (room.Role.HasFlag(SiteRoomRole.Objective))
            tags.Add("OBJECTIVE");

        if (room.Role.HasFlag(SiteRoomRole.ForwardPost))
            tags.Add("FORWARD POST");

        string name = room.NameKey.Length == 0 ? "-" : Shorten(room.NameKey);
        string suffix = tags.Count == 0 ? string.Empty : " " + string.Join(" ", tags);

        // Fixed width so the lanes line up. The labels come from localization keys, which
        // have wildly different lengths, and a ragged left edge makes the whole thing
        // much harder to read at a glance than the content deserves.
        return $"{name,-12}{suffix,-28}";
    }

    /// <summary>The floor character under a column: brighter under a light.</summary>
    private static char LightChar(TacticalState state, SiteRoom room, int column)
    {
        Fixed32 x = ColumnX(room, column);

        foreach (SiteLight light in state.Layout.Lights)
        {
            if (light.RoomId != room.Id)
                continue;

            if (Fixed32.Distance(light.X, x).Raw > light.RadiusCm)
                continue;

            return light.Level == SiteLightLevel.Lit ? '#' : light.Level == SiteLightLevel.Dim ? '+' : '.';
        }

        return room.DefaultLightLevel switch
        {
            SiteLightLevel.Lit => '#',
            SiteLightLevel.Dim => '+',
            _ => '.',
        };
    }

    private static void PlaceActors(TacticalState state, SiteRoom room, char[] cells)
    {
        foreach (TacticalActor actor in state.SortedActors)
        {
            if (actor.Position.FloorIndex != room.FloorIndex)
                continue;

            if (!state.Layout.IsRoomObserved(room.Id))
                continue;

            int column = Column(state, room, actor.Position.X);

            if (column < 0 || column >= cells.Length)
                continue;

            // Only something the player could perceive is drawn. A guard nobody has seen
            // is genuinely not on the player's map, and drawing them would make the whole
            // fog-of-war layer cosmetic.
            if (actor.IsGuard && !Perceived(state, actor))
                continue;

            // Squad members are drawn over civilians, never the other way round. Actors
            // are visited in id order and the last writer wins, so without this a
            // civilian standing on the same centimetre as the operative the player is
            // holding would paint over the one thing the display exists to show them.
            char glyph = Glyph(state, actor);

            if (cells[column] == 'c' && glyph == 'c')
                continue;

            cells[column] = glyph;
        }
    }

    /// <summary>
    /// True when the squad has perceived this guard.
    /// </summary>
    /// <remarks>
    /// "The squad", not "the actor in this room", because the player is the whole squad
    /// and a guard somebody spotted two rooms ago is still a guard the player knows
    /// about. The position drawn is the last one they were perceived at, which is the
    /// honest one — it is where the player's information says they are.
    /// </remarks>
    private static bool Perceived(TacticalState state, TacticalActor guard)
    {
        foreach (TacticalActor member in state.Squad)
        {
            if (member.Memory.LastSeenActorId == guard.Id && member.Memory.HasContact)
                return true;
        }

        return false;
    }

    private static char Glyph(TacticalState state, TacticalActor actor)
    {
        if (actor.IsGuard)
            return 'G';

        if (actor.Condition == ActorCondition.Dead)
            return 'x';

        if (actor.Condition == ActorCondition.Captured)
            return 'X';

        if (actor.IsCarried)
            return 'c';

        // A civilian is somebody else's business, not the player's, and drawing it as
        // 'a' made the member of the public indistinguishable from the member of the
        // squad — which is the difference between "I am in a crowded lobby" and "four
        // of my own people are in a crowded lobby".
        if (!actor.IsAgent)
            return 'c';

        // The controlled agent is the one the player is holding. Marking it is the
        // cheapest possible way to answer "which one am I" without a HUD — and it has to
        // be the controlled one specifically, because marking every member of the squad
        // with the same glyph tells the player nothing at all.
        return state.Control.Controlled == actor.AgentId ? '@' : 'a';
    }

    private static void PlaceLights(TacticalState state, SiteRoom room, char[] cells)
    {
        foreach (SiteLight light in state.Layout.Lights)
        {
            if (light.RoomId != room.Id)
                continue;

            int column = Column(state, room, light.X);

            if (column < 0 || column >= cells.Length)
                continue;

            if (cells[column] == '.')
                cells[column] = light.Level == SiteLightLevel.Lit ? '#' : '+';
        }
    }

    /// <summary>
    /// Draws the connections at this room's edges.
    /// </summary>
    /// <remarks>
    /// Only on the edges, because that is where they are. A door is a point on the shared
    /// wall between two rooms, and drawing it inside a room would be drawing a second
    /// thing at a place it is not.
    /// </remarks>
    private static void PlaceConnections(TacticalState state, SiteRoom room, char[] cells)
    {
        foreach (SiteConnection connection in state.Layout.ConnectionsAt(room.Id))
        {
            char glyph = ConnectionGlyph(state, connection, room);

            if (glyph == ' ')
                continue;

            int column = connection.X.Raw <= room.StartX.Raw ? 0 : cells.Length - 1;
            cells[column] = glyph;
        }
    }

    private static char ConnectionGlyph(TacticalState state, SiteConnection connection, SiteRoom room)
    {
        if (connection.IsVertical)
            return connection.UsableByNpc ? '/' : '^';

        ConnectionState status = state.StateOf(connection.Id);

        return status switch
        {
            ConnectionState.Open => ' ',
            ConnectionState.Closed => '|',
            ConnectionState.Locked => 'L',
            ConnectionState.Barricaded => 'B',
            ConnectionState.Blocked => '#',
            _ => '?',
        };
    }

    private static int Column(TacticalState state, SiteRoom room, Fixed32 x)
    {
        int span = room.Span.Raw;

        if (span <= 0)
            return -1;

        int offset = x.Raw - room.StartX.Raw;

        return offset < 0 || offset >= span
            ? -1
            : (int)((long)offset * ColumnsPerRoom / span);
    }

    private static Fixed32 ColumnX(SiteRoom room, int column)
        => new((int)(room.StartX.Raw + ((long)column * room.Span.Raw / ColumnsPerRoom)));

    private static void Legend(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("  @ you   a squad   c civilian   G guard (seen)   x dead   X captured");
        sb.AppendLine("  # lit   + dim   . dark   | shut   L locked   B barricaded   / stair   ^ vent");
        sb.AppendLine("  rooms drawn sealed (█) have not been entered");
    }

    private static string Band(AlarmBand band) => band.ToString();

    /// <summary>
    /// The tail of a localization key, which is the part that says anything.
    /// </summary>
    /// <remarks>
    /// `site.room.office` becomes `office`. The keys are the only names a Core object has
    /// (rule 4), so stripping the namespace is what turns them into words without Core
    /// ever containing any.
    /// </remarks>
    private static string Shorten(string nameKey)
    {
        int dot = nameKey.LastIndexOf('.');

        return dot >= 0 && dot < nameKey.Length - 1 ? nameKey[(dot + 1)..] : nameKey;
    }
}