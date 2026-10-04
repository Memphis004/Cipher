using System.Globalization;
using System.Text;

namespace ProjectSpy.Core.Missions;

/// <summary>
/// Renders a generated site as text, floor by floor.
/// </summary>
/// <remarks>
/// <para>
/// A development tool that stage 6 leans on constantly, so it is designed to be read
/// rather than merely complete: every invariant has a visible tell, and a layout that is
/// wrong looks wrong here before anyone walks it.
/// </para>
/// <para>
/// <b>Widths are in metres, not centimetres.</b> Core stores centimetres because
/// simulation arithmetic needs them; a human reading a floor plan needs metres, and
/// dividing by <see cref="Fixed32.CmPerMetre"/> at the edge is what keeps the unit
/// convention from leaking into everything that reads a layout.
/// </para>
/// <para>
/// <b>It prints keys, not names.</b> Core never produces prose (rule 4), so rooms appear
/// as their localization keys. That is not a limitation of the renderer — it is the same
/// rule that stops a rule system from writing a sentence, and a debug dump that quietly
/// resolved keys would be a second, English-only surface inside Core.
/// </para>
/// </remarks>
public static class SiteAsciiRenderer
{
    /// <summary>Characters per metre of floor width.</summary>
    private const int ColumnsPerMetre = 2;

    /// <summary>Narrowest floor still worth drawing, in columns.</summary>
    private const int MinimumWidthColumns = 24;

    /// <summary>
    /// Renders a site as text.
    /// </summary>
    /// <param name="layout">The site to draw.</param>
    /// <param name="includeContents">
    /// When true, each room lists its observed contents. Unobserved rooms say so rather
    /// than staying silent, because "empty" and "not looked at yet" are different
    /// answers and a dump that blurred them would be a fog-of-war leak in a debug tool.
    /// </param>
    public static string Render(SiteLayout layout, bool includeContents = false)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var sb = new StringBuilder(8 * 1024);

        AppendHeader(sb, layout);
        AppendRegions(sb, layout, "SITE", layout.MainSite, includeContents);

        if (layout.ForwardPost is not null)
        {
            sb.AppendLine();
            AppendRegions(sb, layout, "FORWARD POST", layout.ForwardPost, includeContents);
        }

        AppendTravel(sb, layout);
        AppendSummary(sb, layout);
        AppendLegend(sb);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine($"Site {layout.SiteTemplateId}  tier {layout.Tier}  mission {layout.MissionId}");
        sb.AppendLine($"worldSeed {layout.WorldSeed}  mapSeed {layout.MapSeed}");
        sb.AppendLine(
            $"entrance {layout.EntranceRoomId}   objective {layout.ObjectiveRoomId}   "
            + $"extraction [{string.Join(", ", layout.ExtractionRoomIds)}]");
        sb.AppendLine(new string('=', 100));
    }

    private static void AppendRegions(
        StringBuilder sb, SiteLayout layout, string title, SiteRegion region, bool includeContents)
    {
        sb.AppendLine();
        sb.AppendLine($"[{title}] {region.Floors.Count} floor(s), {region.RoomCount} room(s)");

        foreach (SiteFloor floor in region.Floors)
            AppendFloor(sb, layout, floor, includeContents);
    }

    private static void AppendFloor(StringBuilder sb, SiteLayout layout, SiteFloor floor, bool includeContents)
    {
        int columns = WidthInColumns(floor.Span.Raw);

        sb.AppendLine();
        sb.AppendLine(
            $"-- floor {floor.Index} ({floor.Kind}) {Metres(floor.Span.Raw)}m across, {floor.Rooms.Count} room(s)");

        // A ruler, so a reader can tell how far along a floor a room or a connection sits
        // without doing arithmetic against the label.
        sb.AppendLine("     " + Ruler(floor.Span.Raw, columns));
        AppendPlan(sb, layout, floor, columns);

        foreach (SiteRoom room in floor.Rooms)
        {
            AppendRoomDetail(sb, layout, room, includeContents);
        }

        AppendVerticalNotes(sb, layout, floor);
    }

    /// <summary>
    /// The plan row: rooms as spans, with a marker on each boundary that has an opening.
    /// </summary>
    private static void AppendPlan(StringBuilder sb, SiteLayout layout, SiteFloor floor, int columns)
    {
        var row = new char[columns];
        for (int i = 0; i < columns; i++)
            row[i] = '.';

        foreach (SiteRoom room in floor.Rooms)
        {
            int from = ColumnOf(room.StartX.Raw, floor.Span.Raw, columns);
            int to = Math.Max(from + 1, ColumnOf(room.EndX.Raw, floor.Span.Raw, columns));

            for (int i = from; i < to && i < columns; i++)
                row[i] = '=';
        }

        // Connections on this floor: a marker at the wall they sit in. Vertical ones are
        // marked on the floor they leave from, and annotated underneath, because a stair
        // drawn as a dot on two floors would be ambiguous about where it lands.
        foreach (SiteConnection connection in layout.Connections)
        {
            if (connection.FloorIndexA != floor.Index)
                continue;

            int column = ColumnOf(connection.X.Raw, floor.Span.Raw, columns);

            if (!connection.IsVertical && column < columns)
                row[column] = GateOf(connection);
        }

        sb.AppendLine("     |" + new string(row) + "|");
    }

    /// <summary>The glyph for an opening in a wall.</summary>
    private static char GateOf(SiteConnection connection) => connection.Kind switch
    {
        SiteConnectionKind.LockedDoor => 'L',
        SiteConnectionKind.Window => 'w',
        SiteConnectionKind.Hole => 'h',
        SiteConnectionKind.Door => '+',
        _ => '+',
    };

    private static void AppendRoomDetail(StringBuilder sb, SiteLayout layout, SiteRoom room, bool includeContents)
    {
        var parts = new List<string>
        {
            room.Id.ToString(),
            room.NameKey,
            $"[{Metres(room.StartX.Raw)}-{Metres(room.EndX.Raw)}m",
            $"{Metres(room.Span.Raw)}m]",
            $"tpl {room.RoomTemplateId}",
        };

        if (room.Role != SiteRoomRole.None)
            parts.Add(room.Role.ToString());

        int lights = 0;
        foreach (SiteLight light in layout.Lights)
        {
            if (light.RoomId == room.Id)
                lights++;
        }
        if (lights > 0)
            parts.Add($"{lights} light");

        int occluders = CountOccluders(layout, room.Id);
        if (occluders > 0)
            parts.Add($"{occluders} occl");

        int guards = CountGuards(layout, room.Id);
        if (guards > 0)
            parts.Add($"{guards} guard");

        int civilians = CountCivilians(layout, room.Id);
        if (civilians > 0)
            parts.Add($"{civilians} civ");

        sb.AppendLine("       " + string.Join("  ", parts));

        var interactables = new List<string>();
        foreach (SiteInteractable interactable in layout.Interactables)
        {
            if (interactable.RoomId == room.Id)
                interactables.Add($"{SimulationRules.NameKeyFor(interactable.Kind)}@{Metres(interactable.X.Raw)}m");
        }

        if (interactables.Count > 0)
            sb.AppendLine("          things: " + string.Join(" ", interactables));

        if (room.LootTableId > 0)
            sb.AppendLine($"          loot table {room.LootTableId}");

        if (includeContents)
            AppendContents(sb, layout, room);
    }

    /// <summary>
    /// Appends a room's observed contents, or says plainly that there are none yet.
    /// </summary>
    /// <remarks>
    /// "Not observed" is spelled out rather than left blank, because a blank line and an
    /// empty one look identical and the difference between them is exactly what fog of
    /// war is. A dump that blurred the two would be a leak in the one tool people reach
    /// for when checking whether fog works.
    /// </remarks>
    private static void AppendContents(StringBuilder sb, SiteLayout layout, SiteRoom room)
    {
        SiteRoomDetail? detail = layout.ObservedRoom(room.Id);

        if (detail is null)
        {
            sb.AppendLine(layout.IsRoomObserved(room.Id)
                ? "          contents: (observed, not re-read)"
                : "          contents: <unobserved>");
            return;
        }

        var loot = new List<string>();
        foreach (SiteLootEntry entry in detail.Loot)
            loot.Add($"item {entry.ItemId} x{entry.Count}");

        var props = new List<string>();
        foreach (SiteProp prop in detail.Props)
            props.Add($"{prop.PropId}@{Metres(prop.X.Raw - room.StartX.Raw)}m");

        sb.AppendLine(
            "          contents: loot=[" + string.Join(", ", loot) + "] props=[" + string.Join(", ", props) + "]");
    }

    private static void AppendVerticalNotes(StringBuilder sb, SiteLayout layout, SiteFloor floor)
    {
        foreach (SiteConnection connection in layout.Connections)
        {
            if (connection.FloorIndexA != floor.Index || !connection.IsVertical)
                continue;

            sb.AppendLine(
                $"       ^ {connection.Id} {connection.Kind} up from {connection.RoomA} "
                + $"({Metres(connection.X.Raw)}m) to {connection.RoomB} (f{connection.FloorIndexB}, "
                + $"{Metres(connection.UpperX.Raw)}m), {connection.TraverseSteps} steps"
                + (connection.UsableByNpc ? ", npc ok" : ", not for npc"));
        }
    }

    /// <summary>
    /// Names the hop between the building and the post, and the post's floor numbers.
    /// </summary>
    /// <remarks>
    /// Worth calling out on its own because it is the one thing about the two regions
    /// that is not visible in either region's own block: the post's floors are numbered
    /// past the building's, and the connection that joins them is an ordinary vertical
    /// one whose cost comes from <c>site_forward_post_travel_steps</c> rather than from a
    /// stair row. Both facts are load-bearing for reading a dump and easy to miss.
    /// </remarks>
    private static void AppendTravel(StringBuilder sb, SiteLayout layout)
    {
        if (layout.ForwardPost is null)
            return;

        sb.AppendLine();
        sb.AppendLine("[forward post]");

        foreach (SiteConnection connection in layout.ForwardPost.Connections)
        {
            if (layout.Find(connection.RoomA) is not { } from)
                continue;

            if (from.Role.HasFlag(SiteRoomRole.ForwardPost))
                continue;

            sb.AppendLine(
                $"       F{from.FloorIndex} {from.Id} -> F{connection.FloorIndexB} "
                + $"{connection.RoomB} ({connection.TraverseSteps} steps, "
                + $"{connection.Kind})");
        }

        foreach (SiteFloor floor in layout.ForwardPost.Floors)
            sb.AppendLine($"       post floor        F{floor.Index} ({floor.Rooms.Count} rooms)");
    }

    private static void AppendSummary(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine();
        sb.AppendLine("[summary]");
        sb.AppendLine($"       floors            {layout.MainSite.Floors.Count}");
        sb.AppendLine($"       rooms             {layout.MainSite.RoomCount}");
        sb.AppendLine($"       connections       {layout.Connections.Count}");
        sb.AppendLine($"       lights            {layout.Lights.Count}");
        sb.AppendLine($"       occluders         {layout.Occluders.Count}");
        sb.AppendLine($"       interactables     {layout.Interactables.Count}");
        sb.AppendLine($"       guards            {layout.Guards.Count}");
        sb.AppendLine($"       civilians         {layout.Civilians.Count}");
        sb.AppendLine($"       routes to object  {layout.CountRoutesToObjective(1000)}");
        sb.AppendLine($"       observed rooms    {layout.ObservedRoomCount}");
        sb.AppendLine($"       valid             {layout.Validate(out string problem)} {problem}");
    }

    private static void AppendLegend(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("[legend]");
        sb.AppendLine("       = room span      + door     L locked door   w window   h hole");
        sb.AppendLine("       ^ vertical       . empty floor");
        sb.AppendLine("       lengths in metres; Core stores centimetres and converts here only");
    }

    // ---- measurement ---------------------------------------------------------

    /// <summary>Centimetres as a whole-metre string, for a human reader.</summary>
    private static string Metres(int centimetres) =>
        (centimetres / Fixed32.CmPerMetre).ToString(CultureInfo.InvariantCulture);

    private static int WidthInColumns(int spanCm)
    {
        int columns = (spanCm / Fixed32.CmPerMetre) * ColumnsPerMetre;
        return columns < MinimumWidthColumns ? MinimumWidthColumns : columns;
    }

    private static int ColumnOf(int centimetres, int spanCm, int columns)
    {
        if (spanCm <= 0)
            return 0;

        int column = (int)((long)centimetres * columns / spanCm);
        return column < 0 ? 0 : (column >= columns ? columns - 1 : column);
    }

    private static string Ruler(int spanCm, int columns)
    {
        var sb = new StringBuilder(columns);
        for (int i = 0; i < columns; i++)
        {
            if (i % 10 == 0)
                sb.Append('|');
            else
                sb.Append('-');
        }

        return sb.ToString();
    }

    // ---- counting ------------------------------------------------------------
    //
    // Straight loops rather than prebuilt indices: a dump is called once, by a human
    // looking at one site, and building four dictionaries to count four things would be
    // a worse trade than the few hundred comparisons it saves.

    private static int CountOccluders(SiteLayout layout, SiteRoomId roomId)
    {
        int total = 0;
        foreach (SiteOccluder occluder in layout.Occluders)
        {
            if (occluder.RoomId == roomId)
                total++;
        }

        return total;
    }

    private static int CountGuards(SiteLayout layout, SiteRoomId roomId)
    {
        int total = 0;
        foreach (SiteGuard guard in layout.Guards)
        {
            if (guard.HomeRoomId == roomId)
                total++;
        }

        return total;
    }

    private static int CountCivilians(SiteLayout layout, SiteRoomId roomId)
    {
        int total = 0;
        foreach (SiteCivilian civilian in layout.Civilians)
        {
            if (civilian.RoomId == roomId)
                total++;
        }

        return total;
    }
}