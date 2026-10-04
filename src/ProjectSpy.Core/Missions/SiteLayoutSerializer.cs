using System.Globalization;
using System.Text;

namespace ProjectSpy.Core.Missions;

/// <summary>
/// A canonical, byte-stable serialization of a <see cref="SiteLayout"/>.
/// </summary>
/// <remarks>
/// <para>
/// Line-oriented text for the same two reasons as <c>WorldStateSerializer</c>: it stays
/// readable in a diff when a determinism test fails, which is the moment it matters most,
/// and it forces every field through one invariant number format so nothing
/// culture-sensitive can differ between machines.
/// </para>
/// <para>
/// <b>Every collection is written in sorted order.</b> A hash or a comparison over an
/// unordered enumeration would make the output depend on insertion history rather than on
/// state, and a canonical dump whose bytes move on their own is not canonical.
/// </para>
/// <para>
/// <b>This is a fingerprint, not the save format.</b> The thing a save actually stores is
/// <see cref="SiteLayoutSaveData"/> — five numbers and the observed-room set — because the
/// generator can rebuild the building from the seed exactly. This dump exists to make that
/// claim checkable: two layouts that regenerate identically produce identical bytes here,
/// and if they ever did not, the diff would say which field drifted.
/// </para>
/// </remarks>
public static class SiteLayoutSerializer
{
    /// <summary>Serializes a site to canonical text.</summary>
    public static string ToCanonicalText(SiteLayout layout)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine("site");
        Append(sb, "siteTemplateId", layout.SiteTemplateId);
        Append(sb, "tier", layout.Tier);
        Append(sb, "missionId", layout.MissionId);
        Append(sb, "worldSeed", layout.WorldSeed);
        Append(sb, "mapSeed", layout.MapSeed);
        Append(sb, "entrance", layout.EntranceRoomId.Value);
        Append(sb, "objective", layout.ObjectiveRoomId.Value);
        Append(sb, "forwardPostRoom", layout.ForwardPostRoomId.Value);
        Append(sb, "extraction", string.Join(",", layout.ExtractionRoomIds.Select(r => r.Value).OrderBy(v => v)));

        AppendRegion(sb, "mainSite", layout.MainSite);

        if (layout.ForwardPost is not null)
            AppendRegion(sb, "forwardPost", layout.ForwardPost);

        AppendLights(sb, layout);
        AppendOccluders(sb, layout);
        AppendInteractables(sb, layout);
        AppendGuards(sb, layout);
        AppendCivilians(sb, layout);
        AppendObserved(sb, layout);

        return sb.ToString();
    }

    /// <summary>Serializes a site to canonical UTF-8 bytes.</summary>
    public static byte[] ToCanonicalBytes(SiteLayout layout)
        => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ToCanonicalText(layout));

    /// <summary>
    /// Folds the canonical bytes into a 64-bit digest.
    /// </summary>
    /// <remarks>
    /// An independent second check alongside a byte-for-byte string comparison. Two
    /// different hashes agreeing by accident is far less likely than one.
    /// </remarks>
    public static ulong ComputeCanonicalDigest(SiteLayout layout)
    {
        byte[] bytes = ToCanonicalBytes(layout);

        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }

    /// <summary>
    /// The save data for a site: the seed, and which rooms had been observed.
    /// </summary>
    /// <remarks>
    /// Sorting the observed set here rather than trusting the caller's order is what makes
    /// a save byte-identical regardless of the order the team happened to walk the
    /// building in — which is the difference between "the same run" and "the same run,
    /// entered from the other side".
    /// </remarks>
    public static SiteLayoutSaveData ToSaveData(SiteLayout layout)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var observed = layout.ObservedRoomIds.Select(r => r.Value).ToList();
        observed.Sort();

        var ids = new List<SiteRoomId>(observed.Count);
        foreach (int value in observed)
            ids.Add(new SiteRoomId(value));

        return new SiteLayoutSaveData(
            layout.SiteTemplateId,
            layout.Tier,
            layout.MissionId,
            layout.WorldSeed,
            layout.MapSeed,
            ids);
    }

    /// <summary>Rebuilds a site from save data. Equivalent to <see cref="SiteLayout.Load"/>.</summary>
    public static SiteLayout FromSaveData(SiteLayoutSaveData data) => SiteLayout.Load(data);

    // ---- sections ------------------------------------------------------------

    private static void AppendRegion(StringBuilder sb, string key, SiteRegion region)
    {
        sb.AppendLine(key);
        Append(sb, "regionKind", (int)region.Kind);
        Append(sb, "floorCount", region.Floors.Count);

        foreach (SiteFloor floor in region.Floors)
        {
            Append(sb, "floor.index", floor.Index);
            Append(sb, "floor.kind", (int)floor.Kind);

            // Raw centimetres, not metres: this is a fingerprint, and rounding to metres
            // would make two layouts that differ by 40 cm serialize identically.
            Append(sb, "floor.spanCm", floor.Span.Raw);
            Append(sb, "floor.roomCount", floor.Rooms.Count);
        }

        foreach (SiteRoom room in region.AllRooms)
        {
            Append(sb, "room.id", room.Id.Value);
            Append(sb, "room.floor", room.FloorIndex);
            Append(sb, "room.startCm", room.StartX.Raw);
            Append(sb, "room.endCm", room.EndX.Raw);
            Append(sb, "room.template", room.RoomTemplateId);
            Append(sb, "room.nameKey", room.NameKey);
            Append(sb, "room.tags", Join(room.Tags));
            Append(sb, "room.npcTags", Join(room.NpcTags));
            Append(sb, "room.lootTable", room.LootTableId);
            Append(sb, "room.defaultLight", (int)room.DefaultLightLevel);
            Append(sb, "room.noiseAbsorption", room.NoiseAbsorptionPercent);

            // Cast to int so the value is the flag combination rather than a name, which
            // would make the dump depend on the enum's spelling.
            Append(sb, "room.role", (int)room.Role);
        }

        foreach (SiteConnection connection in Sorted(region.Connections))
        {
            Append(sb, "conn.id", connection.Id.Value);
            Append(sb, "conn.a", connection.RoomA.Value);
            Append(sb, "conn.b", connection.RoomB.Value);
            Append(sb, "conn.floorA", connection.FloorIndexA);
            Append(sb, "conn.floorB", connection.FloorIndexB);
            Append(sb, "conn.kind", (int)connection.Kind);
            Append(sb, "conn.type", connection.ConnectionTypeId);
            Append(sb, "conn.xCm", connection.X.Raw);
            Append(sb, "conn.upperXcm", connection.UpperX.Raw);
            Append(sb, "conn.steps", connection.TraverseSteps);
            Append(sb, "conn.locked", connection.IsLocked);
            Append(sb, "conn.blocksVision", connection.BlocksVision);
            Append(sb, "conn.usableByNpc", connection.UsableByNpc);
        }
    }

    private static void AppendLights(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine("lights");

        foreach (SiteLight light in layout.Lights.OrderBy(l => l.Id))
        {
            Append(sb, "light.id", light.Id);
            Append(sb, "light.room", light.RoomId.Value);
            Append(sb, "light.source", light.LightSourceId);
            Append(sb, "light.xCm", light.X.Raw);
            Append(sb, "light.level", (int)light.Level);
            Append(sb, "light.radiusCm", light.RadiusCm);
        }
    }

    private static void AppendOccluders(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine("occluders");

        foreach (SiteOccluder occluder in layout.Occluders.OrderBy(o => o.Id))
        {
            Append(sb, "occluder.id", occluder.Id);
            Append(sb, "occluder.room", occluder.RoomId.Value);
            Append(sb, "occluder.xCm", occluder.X.Raw);
        }
    }

    private static void AppendInteractables(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine("interactables");

        foreach (SiteInteractable interactable in layout.Interactables.OrderBy(i => i.Id))
        {
            Append(sb, "thing.id", interactable.Id);
            Append(sb, "thing.room", interactable.RoomId.Value);
            Append(sb, "thing.kind", (int)interactable.Kind);
            Append(sb, "thing.xCm", interactable.X.Raw);
            Append(sb, "thing.lootTable", interactable.LootTableId);
        }
    }

    private static void AppendGuards(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine("guards");

        foreach (SiteGuard guard in layout.Guards.OrderBy(g => g.Id))
        {
            Append(sb, "guard.id", guard.Id.Value);
            Append(sb, "guard.archetype", guard.ArchetypeId);
            Append(sb, "guard.nameKey", guard.NameKey);
            Append(sb, "guard.role", (int)guard.Role);
            Append(sb, "guard.home", guard.HomeRoomId.Value);
            Append(sb, "guard.route", string.Join(",", guard.PatrolRoute.Select(r => r.Value)));
        }
    }

    private static void AppendCivilians(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine("civilians");

        foreach (SiteCivilian civilian in layout.Civilians.OrderBy(c => c.Id))
        {
            Append(sb, "civilian.id", civilian.Id.Value);
            Append(sb, "civilian.room", civilian.RoomId.Value);
        }
    }

    private static void AppendObserved(StringBuilder sb, SiteLayout layout)
    {
        sb.AppendLine("observed");
        Append(sb, "observed.count", layout.ObservedRoomCount);

        // Sorted, because ObservedRoomIds is a set with no order of its own.
        Append(sb, "observed.rooms", string.Join(",", layout.ObservedRoomIds.Select(r => r.Value)));
    }

    // ---- helpers -------------------------------------------------------------

    private static IEnumerable<SiteConnection> Sorted(IEnumerable<SiteConnection> connections)
    {
        var ordered = new List<SiteConnection>(connections);
        ordered.Sort((a, b) => a.Id.CompareTo(b.Id));
        return ordered;
    }

    private static string Join(IReadOnlyList<string> values)
    {
        if (values is null || values.Count == 0)
            return string.Empty;

        var ordered = new List<string>(values);
        ordered.Sort(StringComparer.Ordinal);
        return string.Join("|", ordered);
    }

    /// <summary>
    /// Writes one key/value line.
    /// </summary>
    /// <remarks>
    /// Strings go through the invariant culture with newlines and backslashes escaped, so
    /// a name key containing a line break cannot forge an extra field and make two
    /// different layouts serialize identically.
    /// </remarks>
    private static void Append(StringBuilder sb, string key, object value)
    {
        string text = value switch
        {
            long l => l.ToString(CultureInfo.InvariantCulture),
            ulong u => u.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            _ => Escape(value.ToString() ?? string.Empty),
        };

        sb.Append(key).Append('=').Append(text).Append('\n');
    }

    private static string Escape(string raw)
    {
        var sb = new StringBuilder(raw.Length + 8);

        foreach (char c in raw)
        {
            if (c is '\n' or '\r' or '\\')
            {
                sb.Append('\\').Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}