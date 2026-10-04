using ProjectSpy.Tables;

// Aliased to keep the two "room template" beans apart in this file: the tactical one
// describes a walkable interval on a floor, the node-interior one a set of slot indices.
using TacticalRoomTemplate = ProjectSpy.Tables.RoomTemplate;

namespace ProjectSpy.Core.Missions;

/// <summary>
/// Builds one room's contents the first time the team observes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal on purpose.</b> This is the only code that can conjure a
/// <see cref="SiteRoomDetail"/>, and <c>Generate</c> takes no visibility into account, so
/// making it public would hand the UI a way to build the room the player has not entered
/// yet. Keeping it internal means <see cref="SiteLayout.ObserveRoom"/> is the only door,
/// and that door is gated by what the player has observed —
/// <c>SiteLazyContentTests</c> asserts that over the compiled public surface.
/// </para>
/// <para>
/// <b>Determinism is the contract.</b> The stream is derived from
/// <c>(MapSeed, roomId, "contents")</c> rather than drawn from a shared sequence, so a
/// room's contents are identical whether they are generated on the first step or the
/// thousandth, and whether it is generated before or after its neighbours. Generation
/// order cannot change the result — which is what makes lazy generation safe at all. If
/// contents came from the world's Tactical stream, the furniture in the room the player
/// walked into first would depend on how many rooms they happened to look at, and a save
/// reloaded later would describe a different building.
/// </para>
/// <para>
/// <b>Table-driven.</b> What a container yields comes from <c>loot_table</c>; how much
/// furniture a room has comes from its own occluder density and its span. No count,
/// chance or table id is written here (rule 3).
/// </para>
/// </remarks>
internal static class SiteRoomDetailGenerator
{
    /// <summary>
    /// The tag the contents stream is derived under.
    /// </summary>
    /// <remarks>
    /// Namespaces this stream away from every other stream derived from the same map
    /// seed, so a future per-room stream derives its own tag rather than colliding with
    /// this one and shifting every room's furniture.
    /// </remarks>
    private const int ContentsStreamTag = 0x5170;

    /// <summary>
    /// Fraction of the room's span occupied by furniture at the room template's occluder
    /// density, expressed as a divisor.
    /// </summary>
    /// <remarks>
    /// A prop every <c>divisor</c> centimetres of span, scaled by
    /// <c>room_template.occluder_density</c> as a percentage. At density 40 in a 2,000 cm
    /// room that is eight props, which reads as furnished; at density 5 in the same room
    /// it is one, which reads as a storage bay. The divisor is the tunable a designer
    /// turns when furniture is too sparse or too dense everywhere at once.
    /// </remarks>
    private const int PropSpanDivisor = 400;

    /// <summary>Rooms with no loot table still get furniture.</summary>
    private const int MinimumPropCount = 1;

    /// <summary>
    /// Never more than this many props in one room.
    /// </summary>
    /// <remarks>
    /// A cap rather than an unbounded roll because a prop is a thing an agent walks past
    /// and the stage-4c vision model clips against every one of them; a 3,000-prop room
    /// would be a performance problem long before it was a design one.
    /// </remarks>
    private const int MaxPropCount = 12;

    /// <summary>Generates a room's contents. Internal: see the type remarks.</summary>
    public static SiteRoomDetail Generate(SiteLayout layout, SiteRoomId roomId)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        SiteRoom? room = layout.Find(roomId);
        if (room is null)
        {
            throw new InvalidOperationException(
                $"Room {roomId} is not part of this site, so it has no contents to generate.");
        }

        // Keyed by the room, not drawn from a shared sequence: see the remarks.
        var rng = new XorShift128Rng(
            RngStreams.DeriveSeed(
                RngStreams.DeriveSeed(layout.MapSeed, roomId.Value),
                ContentsStreamTag));

        return new SiteRoomDetail
        {
            RoomId = roomId,
            Loot = RollLoot(rng, room.LootTableId),
            Props = RollProps(rng, room),
        };
    }

    /// <summary>
    /// Rolls the room's container contents.
    /// </summary>
    /// <remarks>
    /// One roll per <c>loot_table</c> entry, so every entry is independently possible and
    /// the roll order is the table's entry order. That is deliberately not "roll once for
    /// the whole table": a table is a description of a container's contents, and a
    /// vault that could be half empty is a different object from one that is always full.
    /// </remarks>
    private static IReadOnlyList<SiteLootEntry> RollLoot(IRng rng, int lootTableId)
    {
        if (lootTableId <= 0)
            return Array.Empty<SiteLootEntry>();

        IReadOnlyList<LootTable> entries = SimulationRules.LootEntriesFor(lootTableId);
        if (entries.Count == 0)
            return Array.Empty<SiteLootEntry>();

        var weights = new List<int>(entries.Count);
        int total = 0;
        foreach (LootTable entry in entries)
        {
            weights.Add(entry.Weight);
            total += entry.Weight;
        }

        if (total <= 0)
            return Array.Empty<SiteLootEntry>();

        var loot = new List<SiteLootEntry>(entries.Count);

        foreach (LootTable entry in entries)
        {
            if (entry.Weight <= 0)
                continue;

            // A d(total) against the entry's own weight, so a weight of 50 out of 100 is
            // a coin flip and 15 is one in seven — with no integer division anywhere in
            // it. An earlier version scaled the weight to a d100 percentage, which
            // truncated to zero as soon as a table's weights exceeded 10,000 and quietly
            // emptied the container instead of skewing it.
            if (rng.NextInt(1, total + 1) > entry.Weight)
                continue;

            int min = Math.Max(1, entry.MinCount);
            int max = Math.Max(min, entry.MaxCount);
            int count = rng.NextInt(min, max + 1);

            if (count > 0)
                loot.Add(new SiteLootEntry(lootTableId, entry.ItemId, count));
        }

        return loot;
    }

    /// <summary>
    /// Places the room's furniture.
    /// </summary>
    /// <remarks>
    /// Spread evenly rather than clustered, and inset from the walls by a fraction of the
    /// room's span so nothing ends up embedded in a party wall. Even spacing is chosen
    /// because it is the one layout that reads correctly in every presentation: a
    /// presenter can turn these into meshes at these X values and the room looks
    /// deliberate, whereas a clustered roll usually does not.
    /// </remarks>
    private static IReadOnlyList<SiteProp> RollProps(IRng rng, SiteRoom room)
    {
        TacticalRoomTemplate? template = SimulationRules.TacticalRoomTemplateFor(room.RoomTemplateId);

        int densityPercent = template?.OccluderDensity ?? FallbackOccluderDensity;
        int count = SimulationRules.PercentOf(room.Span.Raw, densityPercent) / PropSpanDivisor;
        count = Math.Clamp(count, MinimumPropCount, MaxPropCount);

        // Props stand in the room, so they sit inside its half-open interval: never on
        // the last centimetre, which belongs to the next room.
        int usable = room.Span.Raw - InsetCm(room.Span.Raw);
        if (usable <= 0)
            return Array.Empty<SiteProp>();

        var props = new List<SiteProp>(count);
        int inset = InsetCm(room.Span.Raw);

        for (int i = 0; i < count; i++)
        {
            // Distribute across [0, count) so the spacing is even, then jitter by one
            // step to stop every room in the building furnishing itself identically.
            int slot = (usable * i) / count;
            int jitter = count > 1 ? rng.NextInt(0, usable / count + 1) : 0;

            int x = room.StartX.Raw + inset + Math.Min(slot + jitter, usable - 1);
            props.Add(new SiteProp(template?.PropSetId ?? FallbackPropSetId, new Fixed32(x)));
        }

        return props;
    }

    /// <summary>How far in from each wall furniture keeps, in centimetres.</summary>
    private static int InsetCm(int spanCm) => Math.Max(0, spanCm / 20);

    /// <summary>Used only when <c>room_template</c> is unavailable.</summary>
    private const int FallbackOccluderDensity = 20;

    /// <summary>Used only when <c>room_template</c> is unavailable.</summary>
    private const int FallbackPropSetId = 1;
}