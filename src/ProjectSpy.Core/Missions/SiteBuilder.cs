using ProjectSpy.Tables;

// Aliased for the same reason as everywhere else in the site code: the tactical
// `room_template` bean, not the node-interior one.
using TacticalRoomTemplate = ProjectSpy.Tables.RoomTemplate;
using TableDoorRule = ProjectSpy.Tables.DoorRule;


// Table beans are aliased for the same reason they are aliased in SimulationRules:
// the generated names collide with Core's own (InteractableType is a Core enum, and
// "RoomTemplate" is overloaded between the node-interior and tactical tables), and a
// table row is a *declaration* where Core's type is a *resolved value*. Where a
// conversion is needed it is an explicit switch at the crossing point.
using SiteTemplateRow = ProjectSpy.Tables.SiteTemplate;
using ConnectionTypeRow = ProjectSpy.Tables.ConnectionType;
using GuardArchetypeRow = ProjectSpy.Tables.GuardArchetype;
using LightingProfileRow = ProjectSpy.Tables.LightingProfile;
using LightSourceRow = ProjectSpy.Tables.LightSource;
using InteractableTypeRow = ProjectSpy.Tables.InteractableType;
using TableInteractableKind = ProjectSpy.Tables.InteractableKind;

namespace ProjectSpy.Core.Missions;

/// <summary>
/// The mutable half of site generation: the state that exists only while a building is
/// being laid out.
/// </summary>
/// <remarks>
/// <para>
/// Split out from <see cref="SiteGenerator"/> because it carries state that must not
/// outlive the call — the cursor down the current floor, the next room id, the floor
/// spans already rolled. Keeping it in a separate type makes the boundary obvious: nothing
/// here is reachable once <see cref="SiteGenerator.Generate"/> returns, and everything in
/// here is either consumed immediately or handed to a <see cref="SiteLayout"/>.
/// </para>
/// <para>
/// Every guarantee it makes is made by construction and then checked by
/// <see cref="SiteLayout.Validate"/>. Where a step could in principle fail — a floor too
/// narrow to hold two rooms, a floor with no room that permits a vertical connection — the
/// choice made is the one that cannot strand anything, and the reasoning is on the method.
/// </para>
/// </remarks>
internal sealed class SiteBuilder
{
    private readonly IRng _rng;
    private readonly SiteTemplateRow _template;
    private readonly int _siteTemplateId;
    private readonly int _missionId;
    private readonly ulong _worldSeed;
    private readonly ulong _mapSeed;

    /// <summary>Rooms laid out so far, across every region, in generation order.</summary>
    private readonly List<SiteRoom> _rooms = new();

    /// <summary>Per-floor width already rolled, so a later pass cannot re-roll it.</summary>
    private readonly Dictionary<int, Fixed32> _spansByFloor = new();

    /// <summary>Room templates this site may build, already filtered to its id list.</summary>
    private List<TacticalRoomTemplate> _pool = new();

    /// <summary>Number of floors this site will have. Rolled once, in step one.</summary>
    private int _floorCount;

    public SiteBuilder(IRng rng, SiteTemplateRow template, int siteTemplateId, int missionId, ulong worldSeed, ulong mapSeed)
    {
        _rng = rng;
        _template = template;
        _siteTemplateId = siteTemplateId;
        _missionId = missionId;
        _worldSeed = worldSeed;
        _mapSeed = mapSeed;
    }

    /// <summary>Where the team lands. Set in step five.</summary>
    public SiteRoomId EntranceRoomId { get; private set; }

    /// <summary>What the mission is for. Set in step five.</summary>
    public SiteRoomId ObjectiveRoomId { get; private set; }

    /// <summary>Where the team can leave. Set in step five.</summary>
    public IReadOnlyList<SiteRoomId> ExtractionRoomIds { get; private set; } = Array.Empty<SiteRoomId>();

    /// <summary>The forward post's room, when the site has one.</summary>
    public SiteRoomId ForwardPostRoomId { get; private set; }

    /// <summary>Where in the building a journey to the post begins.</summary>
    public SiteRoomId ForwardPostDepartureRoomId { get; private set; }

    /// <summary>The room the hop up to the post leaves from, or null.</summary>
    internal SiteRoom? ForwardPostDeparture { get; private set; }

    // ---- step one: floor count and widths ------------------------------------

    /// <summary>
    /// Rolls how many floors the building has, and prepares the room pool.
    /// </summary>
    /// <remarks>
    /// Floor count first, because everything downstream is shaped by it: how many rooms
    /// have to fit, how many vertical links are needed, and which room templates are even
    /// legal (a ground-only room cannot go on floor four). Rolling it later would mean
    /// re-deciding floor membership after rooms had been placed.
    /// </remarks>
    public SiteRegion BuildFloors()
    {
        _pool = BuildRoomPool();

        if (_pool.Count == 0)
        {
            throw new InvalidOperationException(
                $"Site { _siteTemplateId } allows no room template that can be placed on any floor. "
                + "This is a table data error, not a runtime condition to recover from.");
        }

        int min = Math.Max(1, _template.FloorCountMin);
        int max = Math.Max(min, _template.FloorCountMax);
        _floorCount = _rng.NextInt(min, max + 1);

        var region = new SiteRegion { Kind = SiteRegionKind.Site };

        for (int index = 0; index < _floorCount; index++)
        {
            int floorIndex = index;

            // Width in lane units from the table, converted to centimetres once here.
            // Everything downstream of this line is centimetres.
            int widthLanes = _rng.NextInt(
                Math.Max(1, _template.WidthPerFloorMin),
                Math.Max(_template.WidthPerFloorMin, _template.WidthPerFloorMax) + 1);

            // Widen a floor too narrow to hold two rooms. Without this the generator can
            // roll a 27 m floor whose only legal room is a 20 m lobby, producing a
            // building with one room -- and a one-room building has no alternate route to
            // anything, because the entrance and the objective are the same room and there
            // is nothing to loop through. A real building is as big as its rooms need it
            // to be; this makes the generator say so rather than shipping a dead plan.
            Fixed32 span = Fixed32.FromMetres(widthLanes);
            int minimumUsable = 2 * NarrowestCm(_pool);

            if (span.Raw < minimumUsable)
                span = new Fixed32(minimumUsable);

            _spansByFloor[floorIndex] = span;

            region.Floors.Add(new SiteFloor
            {
                Index = floorIndex,
                Kind = SiteGenerator.KindForIndex(floorIndex),
                Span = span,
            });
        }

        return region;
    }

    /// <summary>
    /// The room templates this site may build, filtered to its <c>allowed_room_ids</c>.
    /// </summary>
    /// <remarks>
    /// The id list is a hard allow-list rather than a weighting, so a designer can make a
    /// room type impossible for a site by removing it. A missing id is skipped rather than
    /// throwing: the foreign-key check in <c>TableValidator</c> already reports it, and a
    //  runtime throw here would replace a clear data error with a crash in whichever
    // mission happened to load first.
    /// </remarks>
    private List<TacticalRoomTemplate> BuildRoomPool()
    {
        var pool = new List<TacticalRoomTemplate>();

        foreach (int roomId in SimulationRules.IntList(_template.AllowedRoomIds))
        {
            TacticalRoomTemplate? row = SimulationRules.TacticalRoomTemplateFor(roomId);
            if (row is not null)
                pool.Add(row);
        }

        return pool;
    }

    // ---- step two: partition each floor into rooms ---------------------------

    /// <summary>
    /// Lays rooms across every floor of the building.
    /// </summary>
    public void PartitionFloors(SiteRegion region)
    {
        int minRooms = Math.Max(1, Rule("site_min_rooms_per_floor", FallbackMinRooms));
        int maxRooms = Math.Max(minRooms, Rule("site_max_rooms_per_floor", FallbackMaxRooms));

        // A VerticalOnly room is reached from another floor, so a building that rolled a
        // single floor has no way in to one. The floor count is decided in step one, so
        // this is known here rather than guessed per floor.
        bool singleFloor = _floorCount <= 1;

        foreach (SiteFloor floor in region.Floors)
        {
            int target = _rng.NextInt(minRooms, maxRooms + 1);
            PartitionFloor(region, floor, _template, target, maxRooms, singleFloor);
        }
    }

    /// <summary>
    /// Lays rooms across one floor until the target is met or nothing more fits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A left-to-right greedy partition. At each step the remaining space is measured, a
    /// template is drawn from those legal on this floor and legal for this tier, its width
    /// is rolled inside its own range, and the room is clamped to what is left.
    /// </para>
    /// <para>
    /// <b>The trailing-remainder rule.</b> When what is left is too narrow for any
    /// candidate but is not empty, the last room is extended to the end of the floor
    /// rather than leaving a gap. A gap is legal geometry — a party wall has thickness —
    /// but a gap big enough to be un-walkable space is not a feature, and the extension
    /// keeps "rooms tile the floor" true. Extending only ever widens a room, so the
    /// "no room narrower than its template minimum" invariant is unaffected.
    /// </para>
    /// <para>
    /// <b>The VerticalOnly rule.</b> A room whose <c>door_positions_rule</c> is
    /// <c>VerticalOnly</c> refuses horizontal connections, which splits its floor into
    /// left and right halves. Those rooms are only admitted when the site has a second
    /// floor to reach them from, and they are forced to an edge so there is at most one
    /// such split per floor — which is what lets the vertical pass cover every piece with
    /// one link per floor pair.
    /// </para>
    /// </remarks>
    internal void PartitionFloor(
        SiteRegion region, SiteFloor floor, SiteTemplateRow template, int target, int maxRooms, bool SingleFloor)
    {
        List<TacticalRoomTemplate> candidates = CandidatesForFloor(floor.Kind, SingleFloor);
        if (candidates.Count == 0)
        {
            // No template is legal here. A floor with no rooms is legal as long as the
            // vertical pass can link the floors either side of it, which
            // SiteLayout.Validate re-checks.
            return;
        }

        int narrowest = NarrowestCm(candidates);
        Fixed32 cursor = Fixed32.Zero;
        int placed = 0;
        int ceiling = Math.Max(1, maxRooms);

        while (placed < target && placed < ceiling)
        {
            int remaining = floor.Span.Raw - cursor.Raw;

            if (remaining <= 0)
                break;

            if (remaining < narrowest)
                break; // nothing more fits; the trailing-remainder rule absorbs the rest

            List<TacticalRoomTemplate> fitting = Fitting(candidates, remaining);
            if (fitting.Count == 0)
                break;

            // While rooms are still owed, prefer a template narrow enough to leave space
            // for the next one. Without this a 27 m floor that rolls a 24 m lobby ends up
            // as a single room, which is the degenerate case the widening above is about.
            if (placed + 1 < target)
            {
                var splittable = new List<TacticalRoomTemplate>();
                foreach (TacticalRoomTemplate option in fitting)
                {
                    if (Fixed32.FromMetres(option.WidthMin).Raw <= remaining - narrowest)
                        splittable.Add(option);
                }

                if (splittable.Count > 0)
                    fitting = splittable;
            }

            TacticalRoomTemplate row = WeightedRoom(fitting, floor.Kind);

            int minCm = Fixed32.FromMetres(row.WidthMin).Raw;
            int maxCm = Math.Min(Fixed32.FromMetres(row.WidthMax).Raw, remaining);

            int width = _rng.NextInt(minCm, Math.Max(minCm, maxCm) + 1);

            // While rooms are still owed, leave room for at least one more. Clamping only
            // when the split is lopsided (the first version) let a 24 m room swallow a
            // 20 m floor and produce a single-room building, which then had no second
            // route to anything. Clamping unconditionally to "leave exactly the narrowest
            // candidate behind" is what actually hits the floor's target; the splittable
            // filter above guarantees minCm fits in that allowance.
            if (placed + 1 < target)
            {
                int allowance = remaining - narrowest;

                if (width > allowance)
                    width = Math.Max(minCm, allowance);
            }
            else if (remaining - width < narrowest)
            {
                // Last room we intend to place: take the remainder too, so the floor has
                // no un-walkable sliver at its right-hand end.
                width = remaining;
            }

            if (width < minCm)
                break;

            AddRoom(region, floor, row, cursor.Raw, width);
            cursor = new Fixed32(cursor.Raw + width);
            placed++;

            // A VerticalOnly room has no horizontal opening, so it severs the floor. One
            // placed mid-floor would leave three segments that a single vertical link
            // cannot all reach — which is how a building acquires a room nobody can get
            // to. Admitting it only as the final room of the floor (or the first, when
            // nothing has been placed yet) caps the floor at two segments, and the
            // vertical pass covers a segment each.
            if (row.DoorPositionsRule == TableDoorRule.VerticalOnly)
                break;
        }

        AbsorbRemainder(floor, cursor);
    }

    /// <summary>
    /// Extends the last room to the end of the floor, so no dead space is left behind.
    /// </summary>
    private static void AbsorbRemainder(SiteFloor floor, Fixed32 cursor)
    {
        if (floor.Rooms.Count == 0)
            return;

        int leftover = floor.Span.Raw - cursor.Raw;
        if (leftover <= 0)
            return;

        SiteRoom last = floor.Rooms[floor.Rooms.Count - 1];

        // Room is immutable-with-init by design, so the extension is expressed by
        // replacing the entry rather than mutating it.
        int index = floor.Rooms.Count - 1;
        floor.Rooms.RemoveAt(index);
        floor.Rooms.Add(new SiteRoom
        {
            Id = last.Id,
            FloorIndex = last.FloorIndex,
            StartX = last.StartX,
            EndX = new Fixed32(last.EndX.Raw + leftover),
            RoomTemplateId = last.RoomTemplateId,
            NameKey = last.NameKey,
            Tags = last.Tags,
            NpcTags = last.NpcTags,
            LootTableId = last.LootTableId,
            DefaultLightLevel = last.DefaultLightLevel,
            NoiseAbsorptionPercent = last.NoiseAbsorptionPercent,
            Role = last.Role,
        });
    }

    /// <summary>Room templates legal on a floor of the given kind.</summary>
    private List<TacticalRoomTemplate> CandidatesForFloor(SiteFloorKind kind, bool singleFloor)
    {
        var legal = new List<TacticalRoomTemplate>();

        foreach (TacticalRoomTemplate row in _pool)
        {
            if (!SimulationRules.IsRoomAllowedOnFloor(row, kind))
                continue;

            // A VerticalOnly room is reached from another floor, so a one-floor building
            // has no way in and the room is simply not offered.
            if (singleFloor && row.DoorPositionsRule == TableDoorRule.VerticalOnly)
                continue;

            legal.Add(row);
        }

        return legal;
    }

    /// <summary>The narrowest minimum width among candidates, in centimetres.</summary>
    private static int NarrowestCm(List<TacticalRoomTemplate> candidates)
    {
        int narrowest = int.MaxValue;

        foreach (TacticalRoomTemplate row in candidates)
            narrowest = Math.Min(narrowest, Fixed32.FromMetres(row.WidthMin).Raw);

        return narrowest == int.MaxValue ? FallbackMinRoomCm : narrowest;
    }

    /// <summary>Candidates whose minimum width still fits in the remaining space.</summary>
    private static List<TacticalRoomTemplate> Fitting(List<TacticalRoomTemplate> candidates, int remainingCm)
    {
        var fitting = new List<TacticalRoomTemplate>();

        foreach (TacticalRoomTemplate row in candidates)
        {
            if (Fixed32.FromMetres(row.WidthMin).Raw <= remainingCm)
                fitting.Add(row);
        }

        return fitting;
    }

    /// <summary>
    /// Draws a room template weighted by this site's tier.
    /// </summary>
    /// <remarks>
    /// Weights are the <c>weight_tier_1/2/3</c> columns. Tiers above 3 reuse the tier-3
    /// column, because those columns are a schema rather than a lookup and inventing a
    /// fourth would mean every tier-4 site silently generating with no weights at all.
    /// <c>site_gen_rule.site_max_weight_tier</c> names the ceiling so the cap is data, and
    /// the validator fails if it is ever set above the columns that exist.
    /// </remarks>
    private TacticalRoomTemplate WeightedRoom(List<TacticalRoomTemplate> candidates, SiteFloorKind kind)
    {
        int cap = Math.Clamp(Rule("site_max_weight_tier", FallbackMaxWeightTier), 1, 3);

        var weights = new List<int>(candidates.Count);
        foreach (TacticalRoomTemplate row in candidates)
        {
            int weight = WeightForTier(row, _template.Tier, cap);

            // A room with no weight for this tier is not merely improbable, it is
            // unplaceable: a zero-weight candidate in the pool would make the draw
            // ill-defined, so it is filtered rather than weighed.
            if (weight > 0)
                weights.Add(weight);
        }

        if (weights.Count == 0 || weights.Count != candidates.Count)
        {
            var usable = new List<TacticalRoomTemplate>();
            foreach (TacticalRoomTemplate row in candidates)
            {
                if (WeightForTier(row, _template.Tier, cap) > 0)
                    usable.Add(row);
            }

            if (usable.Count == 0)
                return candidates[0];

            var usableWeights = new List<int>(usable.Count);
            foreach (TacticalRoomTemplate row in usable)
                usableWeights.Add(WeightForTier(row, _template.Tier, cap));

            return _rng.WeightedPick(usable, usableWeights);
        }

        _ = kind;
        return _rng.WeightedPick(candidates, weights);
    }

    /// <summary>A template's weight at a tier, clamped to the columns that exist.</summary>
    private static int WeightForTier(TacticalRoomTemplate row, int tier, int cap)
    {
        int column = Math.Clamp(tier, 1, cap);

        return column switch
        {
            1 => row.WeightTier1,
            2 => row.WeightTier2,
            _ => row.WeightTier3,
        };
    }

    private void AddRoom(SiteRegion region, SiteFloor floor, TacticalRoomTemplate row, int startCm, int widthCm)
    {
        var room = new SiteRoom
        {
            Id = new SiteRoomId(_rooms.Count + 1),
            FloorIndex = floor.Index,
            StartX = new Fixed32(startCm),
            EndX = new Fixed32(startCm + widthCm),
            RoomTemplateId = row.Id,
            NameKey = row.NameKey,
            Tags = SimulationRules.Tags(row.Tags),
            NpcTags = SimulationRules.Tags(row.NpcTags),
            LootTableId = row.LootTableId,
            DefaultLightLevel = SimulationRules.ToCoreLightLevel(row.DefaultLightLevel),
            NoiseAbsorptionPercent = row.NoiseAbsorption,
        };

        floor.Rooms.Add(room);
        _rooms.Add(room);
    }

    // ---- step three: horizontal connections ----------------------------------

    /// <summary>
    /// Joins each floor's rooms into one walkable chain, honouring
    /// <c>door_positions_rule</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shared wall between two adjacent rooms gets exactly one connection. This is
    /// what makes a floor a chain, which is in turn what makes every room on it reachable
    /// once the floors are linked vertically — the connectivity guarantee is structural
    /// rather than a repair pass.
    /// </para>
    /// <para>
    /// <b>What <c>door_positions_rule</c> actually decides.</b> The connection between two
    /// rooms sits on their shared wall, because that is where a door in a party wall is;
    /// there is nowhere else for it to be. So the rule cannot mean "where along the wall",
    /// and it is read as what is genuinely different between the four values: whether the
    /// wall is open at all, and what kind of opening it carries.
    /// <c>VerticalOnly</c> closes it — the room is reached from another floor instead, and
    /// the vertical pass guarantees that link. <c>Single</c> opens it as a plain door.
    /// <c>EvenlySpaced</c> and <c>ClusteredEnd</c> open it as well, and bias <em>this
    /// room's</em> vertical connection towards the low-X end and towards its centre
    /// respectively — which is where a stairwell or a vent actually goes in a building
    /// whose rooms are ordered left to right.
    /// </para>
    /// <para>
    /// <b>Why one VerticalOnly room per floor, at an edge.</b> Such a room has no
    /// horizontal opening, so it severs its floor. One at an edge leaves exactly two
    /// segments with a known boundary, which the vertical pass can cover with one link per
    /// floor. Two such rooms, or one in the middle, would need the vertical pass to know
    /// about segments at all, and the interaction is where unwinnable buildings come from.
    /// </para>
    /// </remarks>
    public void PlaceHorizontalConnections(SiteRegion region)
    {
        int baseLockedPercent = Rule("site_locked_door_percent", FallbackLockedDoorPercent);
        int perGrade = Rule("site_locked_door_percent_per_grade", FallbackLockedDoorPercentPerGrade);
        int lockedPercent = Math.Clamp(baseLockedPercent + (perGrade * _template.SecurityGrade), 0, 100);

        foreach (SiteFloor floor in region.Floors)
        {
            for (int i = 1; i < floor.Rooms.Count; i++)
            {
                SiteRoom left = floor.Rooms[i - 1];
                SiteRoom right = floor.Rooms[i];

                if (!WallIsOpen(left, right))
                    continue;

                SiteConnection? connection = MakeHorizontal(region, left, right, lockedPercent);
                _ = connection;
            }
        }
    }

    /// <summary>Joins a region's room chains. Used by the forward post.</summary>
    internal void LinkChainsHorizontally(SiteRegion region)
    {
        int lockedPercent = Rule("site_locked_door_percent", FallbackLockedDoorPercent);

        foreach (SiteFloor floor in region.Floors)
        {
            for (int i = 1; i < floor.Rooms.Count; i++)
            {
                SiteRoom left = floor.Rooms[i - 1];
                SiteRoom right = floor.Rooms[i];

                if (!WallIsOpen(left, right))
                    continue;

                _ = MakeHorizontal(region, left, right, lockedPercent);
            }
        }
    }

    /// <summary>
    /// True when the wall between two adjacent rooms carries an opening.
    /// </summary>
    /// <remarks>
    /// A <c>VerticalOnly</c> room on either side closes the wall. That is the whole
    /// meaning of the rule at this layer.
    /// </remarks>
    private static bool WallIsOpen(SiteRoom left, SiteRoom right)
        => RuleOf(left) != TableDoorRule.VerticalOnly && RuleOf(right) != TableDoorRule.VerticalOnly;

    private static TableDoorRule RuleOf(SiteRoom room)
        => SimulationRules.TacticalRoomTemplateFor(room.RoomTemplateId)?.DoorPositionsRule
           ?? TableDoorRule.EvenlySpaced;

    /// <summary>
    /// Builds an opening in the wall two adjacent rooms share.
    /// </summary>
    /// <param name="kind">
    /// The kind to build. Null means "a door, locked or not, per the security roll". A
    /// repair pass passes <see cref="SiteConnectionKind.Window"/> explicitly, because a
    /// second <em>door</em> between the same two rooms is the same connection, and
    /// <c>Validate</c> rightly rejects duplicate kinds for a pair.
    /// </param>
    private SiteConnection? MakeHorizontal(
        SiteRegion region, SiteRoom left, SiteRoom right, int lockedPercent, SiteConnectionKind? kind = null)
    {
        ConnectionTypeRow? type = null;
        bool locked = false;

        if (kind is null)
        {
            ConnectionTypeRow? doorType = SimulationRules.DefaultConnectionTypeFor(SiteConnectionKind.Door);
            if (doorType is null)
                return null;

            locked = lockedPercent > 0 && _rng.NextInt(1, 101) <= lockedPercent;

            type = locked
                ? SimulationRules.DefaultConnectionTypeFor(SiteConnectionKind.LockedDoor) ?? doorType
                : doorType;
        }
        else
        {
            type = SimulationRules.DefaultConnectionTypeFor(kind.Value);
            if (type is null)
                return null;
        }

        // Refuse to build a connection that already exists. The back-connection repairs
        // ask for a Window and the chain already put a Door there, which is fine; asking
        // for a Window where a Window already is, is not.
        if (HasConnection(region, left.Id, right.Id, SimulationRules.ToCoreConnectionKind(type.Kind)))
            return null;

        var connection = new SiteConnection
        {
            Id = new SiteConnectionId(_nextConnectionId++),
            RoomA = left.Id,
            FloorIndexA = left.FloorIndex,
            RoomB = right.Id,
            FloorIndexB = right.FloorIndex,
            Kind = SimulationRules.ToCoreConnectionKind(type.Kind),
            ConnectionTypeId = type.Id,
            // The shared wall: the right edge of the left-hand room, which is also the
            // left edge of the right-hand one.
            X = left.EndX,
            UpperX = left.EndX,
            TraverseSteps = type.TraverseSteps,
            IsLocked = locked,
            BlocksVision = type.BlocksVision,
            UsableByNpc = type.UsableByNpc,
        };

        region.Connections.Add(connection);
        return connection;
    }

    // ---- step four: vertical connections -------------------------------------

    /// <summary>
    /// Links every floor to the one above it, then adds optional extras.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hard requirement is that every <em>segment</em> of every floor ends up with at
    /// least one vertical connection, and "segment" is the whole difficulty. A
    /// <c>VerticalOnly</c> room has no horizontal opening, so it severs its floor; the
    /// partition confines such a room to a floor's edge, which caps a floor at two
    /// segments. One link per floor pair is therefore not enough — the link that lands in
    /// the odd segment leaves the other one with no way off the floor at all.
    /// </para>
    /// <para>
    /// So links are placed per segment: one from each segment of the lower floor, and one
    /// into each segment of the upper floor that the lower ones did not already cover. That
    /// is the minimum that satisfies both floors, and it is what makes "every floor with a
    /// room has at least one vertical connection" true by construction rather than by a
    /// repair pass that would have to notice.
    /// </para>
    /// <para>
    /// Extras come after, chance-gated, and are the building's character rather than its
    /// correctness: a second stairwell, a service ladder, a vent nobody is supposed to find.
    /// </para>
    /// </remarks>
    public void PlaceVerticalConnections(SiteRegion region)
    {
        var kinds = new List<SiteConnectionKind>
        {
            SiteConnectionKind.Stair,
            SiteConnectionKind.Ladder,
            SiteConnectionKind.Vent,
        };

        var weights = new List<int>
        {
            Math.Max(0, Rule("site_vertical_stair_weight", FallbackStairWeight)),
            Math.Max(0, Rule("site_vertical_ladder_weight", FallbackLadderWeight)),
            Math.Max(0, Rule("site_vertical_vent_weight", FallbackVentWeight)),
        };

        int extraChance = Rule("site_vertical_extra_chance_percent", FallbackVerticalExtraChance);

        for (int pair = 0; pair + 1 < region.Floors.Count; pair++)
        {
            SiteFloor lower = region.Floors[pair];
            SiteFloor upper = region.Floors[pair + 1];

            IReadOnlyList<List<SiteRoom>> lowerSegments = SegmentsOf(lower);
            IReadOnlyList<List<SiteRoom>> upperSegments = SegmentsOf(upper);

            if (lowerSegments.Count == 0 || upperSegments.Count == 0)
                continue;

            // One link out of every lower segment, each landing in a different upper
            // segment so the pairing is a spread rather than a stack.
            for (int i = 0; i < lowerSegments.Count; i++)
            {
                SiteRoom origin = PickFromSegment(_rng, lowerSegments[i]);
                List<SiteRoom> landingSegment = upperSegments[Math.Min(i, upperSegments.Count - 1)];
                SiteRoom landing = PickFromSegment(_rng, landingSegment);

                _ = MakeVertical(region, origin, landing, WeightedKind(kinds, weights));
            }

            // Then one into each upper segment the lower pass did not already reach.
            for (int j = lowerSegments.Count; j < upperSegments.Count; j++)
            {
                SiteRoom origin = PickFromSegment(_rng, lowerSegments[0]);
                SiteRoom landing = PickFromSegment(_rng, upperSegments[j]);

                _ = MakeVertical(region, origin, landing, WeightedKind(kinds, weights));
            }

            // Extras, from the first segment on each side, so they land somewhere that
            // is already reachable and cannot orphan anything.
            if (_rng.NextInt(1, 101) > extraChance)
                continue;

            SiteRoom extraOrigin = PickFromSegment(_rng, lowerSegments[0]);
            SiteRoom extraLanding = PickFromSegment(_rng, upperSegments[0], except: extraOrigin);
            SiteConnectionKind extraKind = WeightedKind(kinds, weights);

            // May legitimately be refused when the roll lands on a kind this pair already
            // has. That costs the building a second stairwell, not its correctness.
            _ = MakeVertical(region, extraOrigin, extraLanding, extraKind);
        }
    }

    /// <summary>
    /// Splits a floor into the runs of rooms that are horizontally connected to each other.
    /// </summary>
    /// <remarks>
    /// A boundary with no opening is a cut, so a floor is one segment unless it holds a
    /// <c>VerticalOnly</c> room, in which case the odd room is a segment of its own. The
    /// partition keeps such a room at an edge, so this returns at most two lists — and the
    /// vertical pass can rely on that without re-deriving it.
    /// </remarks>
    private static IReadOnlyList<List<SiteRoom>> SegmentsOf(SiteFloor floor)
    {
        var segments = new List<List<SiteRoom>>();
        List<SiteRoom>? current = null;

        for (int i = 0; i < floor.Rooms.Count; i++)
        {
            SiteRoom room = floor.Rooms[i];

            if (current is null || (i > 0 && !WallIsOpen(floor.Rooms[i - 1], room)))
            {
                current = new List<SiteRoom>();
                segments.Add(current);
            }

            current.Add(room);
        }

        return segments;
    }

    /// <summary>Picks a room from a segment.</summary>
    private static SiteRoom PickFromSegment(IRng rng, List<SiteRoom> segment, SiteRoom? except = null)
    {
        if (except is null)
            return segment[rng.NextInt(0, segment.Count)];

        SiteRoom chosen = segment[rng.NextInt(0, segment.Count)];

        // Retry rather than throw: the exclusion exists to avoid stacking two links on
        // the same stairwell, not because one segment of one room cannot be handled.
        for (int attempt = 0; attempt < segment.Count && chosen.Id == except.Id; attempt++)
            chosen = segment[rng.NextInt(0, segment.Count)];

        return chosen;
    }

    /// <summary>
    /// True when this region already joins two rooms by a connection of this kind.
    /// </summary>
    /// <remarks>
    /// Checked by <see cref="MakeHorizontal"/> and <see cref="MakeVertical"/> themselves
    /// rather than by each caller. Two Doors between the same two rooms are one
    /// connection, and the vertical "extra" pass could roll a second Stair onto a pair
    /// that already had one — which is a duplicate, and validation says so. Guarding in
    /// one place means no caller can forget.
    /// </remarks>
    private static bool HasConnection(SiteRegion region, SiteRoomId a, SiteRoomId b, SiteConnectionKind kind)
    {
        foreach (SiteConnection connection in region.Connections)
        {
            bool touches =
                (connection.RoomA == a && connection.RoomB == b)
                || (connection.RoomA == b && connection.RoomB == a);

            if (touches && connection.Kind == kind)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Vertical connection kinds different from the given one, in preference order.
    /// </summary>
    /// <remarks>
    /// A list rather than a single kind, because "a spare kind" is not always free: the
    /// vertical extras pass may already have used the Ladder on this pair, in which case
    /// the caller has to try the next one rather than give up.
    /// </remarks>
    private static List<SiteConnectionKind> SpareVerticalKinds(SiteConnectionKind existing)
    {
        SiteConnectionKind[] options =
        {
            SiteConnectionKind.Stair,
            SiteConnectionKind.Ladder,
            SiteConnectionKind.Vent,
        };

        var spare = new List<SiteConnectionKind>();
        foreach (SiteConnectionKind option in options)
        {
            if (option != existing)
                spare.Add(option);
        }

        return spare;
    }

    private SiteConnectionKind WeightedKind(List<SiteConnectionKind> kinds, List<int> weights)
    {
        int total = 0;
        foreach (int weight in weights)
            total += weight;

        if (total <= 0)
            return SiteConnectionKind.Stair;

        int roll = _rng.NextInt(1, total + 1);
        for (int i = 0; i < kinds.Count; i++)
        {
            if (roll <= weights[i])
                return kinds[i];
            roll -= weights[i];
        }

        return kinds[^1];
    }

    private SiteConnection? MakeVertical(SiteRegion region, SiteRoom origin, SiteRoom landing, SiteConnectionKind kind)
    {
        ConnectionTypeRow? type = SimulationRules.DefaultConnectionTypeFor(kind);
        if (type is null)
            return null;

        if (HasConnection(region, origin.Id, landing.Id, kind))
            return null;

        // Foot and landing are separate points inside their own rooms. A stairwell's
        // landing is not directly above its foot, and letting the two differ is what
        // frees the generator from having to line up the rooms either side of the floor
        // boundary — the case that would otherwise make a narrow upper floor over a wide
        // ground floor impossible.
        int footInset = origin.Span.Raw / 4;
        int landingInset = landing.Span.Raw / 4;

        var connection = new SiteConnection
        {
            Id = new SiteConnectionId(_nextConnectionId++),
            RoomA = origin.Id,
            FloorIndexA = origin.FloorIndex,
            RoomB = landing.Id,
            FloorIndexB = landing.FloorIndex,
            Kind = SimulationRules.ToCoreConnectionKind(type.Kind),
            ConnectionTypeId = type.Id,
            X = new Fixed32(origin.StartX.Raw + footInset),
            UpperX = new Fixed32(landing.StartX.Raw + landingInset),
            TraverseSteps = type.TraverseSteps,
            IsLocked = false,
            BlocksVision = type.BlocksVision,
            UsableByNpc = type.UsableByNpc,
        };

        region.Connections.Add(connection);
        return connection;
    }

    /// <summary>
    /// The next connection id, across every region of the site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counting per region looked harmless while the forward post had no connections of
    /// its own, and stopped being harmless the moment it had some: the post's first door
    /// became id 1, which the building had already used, and
    /// <see cref="SiteLayout.EnsureIndex"/>'s id map — a dictionary, so last writer wins —
    /// silently answered every query about connection 1 with the post's door. Door state
    /// is keyed by connection id, so that is not a rendering artefact: it is a save file
    /// recording the wrong door's state.
    /// </para>
    /// <para>
    /// The same class of bug as duplicate guard ids, and fixed the same way: numbered once
    /// for the whole site rather than once per container.
    /// </para>
    /// </remarks>
    private int _nextConnectionId = 1;

    // ---- step five: entrance, objective, extraction --------------------------

    /// <summary>
    /// Places the entrance, the objective room and the extraction points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Entrance.</b> On the ground floor, at one of the two ends of it — a building is
    /// entered from outside, and outside is an end. A roll on
    /// <c>site_entrance_edge_percent</c> picks which end.
    /// </para>
    /// <para>
    /// <b>Objective.</b> The furthest room that carries one of
    /// <c>site_template.objective_room_tags</c>, ranked by floor then by distance along the
    /// floor. Ranking by depth rather than picking uniformly is what stops an objective
    /// landing beside the front door. Where no room matches — which the validator reports
    /// as a data error — the deepest room is used, so the mission is at least well-formed
    /// enough to fail visibly rather than mysteriously.
    /// </para>
    /// <para>
    /// <b>Extraction.</b> At least one room reachable from the objective, and deliberately
    /// not the entrance: a site where the only way out is the way in is a site with no
    /// escape problem, which removes one of the two things the mission is about.
    /// </para>
    /// </remarks>
    public void MarkEntranceObjectiveAndExtraction(SiteRegion region)
    {
        if (region.Floors.Count == 0 || region.Floors[0].Rooms.Count == 0)
        {
            throw new InvalidOperationException(
                $"Site {_siteTemplateId} generated no rooms on its ground floor, so it has nowhere to enter. "
                + "This is a table data error: no allowed room template is narrow enough for the narrowest floor.");
        }

        SiteFloor ground = region.Floors[0];
        int edgePercent = Rule("site_entrance_edge_percent", FallbackEntranceEdgePercent);
        SiteRoom entrance = _rng.NextInt(1, 101) <= edgePercent ? ground.Rooms[0] : ground.Rooms[^1];

        entrance.Role |= SiteRoomRole.Entrance;
        EntranceRoomId = entrance.Id;

        SiteRoom objective = PickObjective(region, entrance);
        objective.Role |= SiteRoomRole.Objective;
        ObjectiveRoomId = objective.Id;

        var extractions = new List<SiteRoomId>();

        // Prefer a room that is neither the entrance nor the objective. A site with only
        // two rooms has no such room, so the fallback is the entrance — a shabby exit, but
        // a valid one, and better than failing generation over it.
        var spare = new List<SiteRoom>();
        foreach (SiteRoom room in region.AllRooms)
        {
            if (room.Id != entrance.Id && room.Id != objective.Id)
                spare.Add(room);
        }

        if (spare.Count > 0)
        {
            int extraChance = Rule("site_extraction_extra_chance_percent", FallbackExtractionExtraChance);

            for (int i = 0; i < spare.Count && extractions.Count < ExtractionCap; i++)
            {
                if (i > 0 && _rng.NextInt(1, 101) > extraChance)
                    continue;

                spare[i].Role |= SiteRoomRole.Extraction;
                extractions.Add(spare[i].Id);
            }
        }

        if (extractions.Count == 0)
        {
            // Or-ing rather than assigning: this room is still the entrance, and losing
            // that role here is what made every two-room site fail validation with
            // "SiteEntranceCount:0".
            entrance.Role |= SiteRoomRole.Extraction;
            extractions.Add(entrance.Id);
        }

        ExtractionRoomIds = extractions;
        ForwardPostDeparture = PickForwardPostDeparture(extractions, region);
        ForwardPostDepartureRoomId = ForwardPostDeparture?.Id ?? SiteRoomId.None;
    }

    /// <summary>
    /// How many extraction points a site may offer.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a target. More than two and the mission stops being a
    /// question — the team simply picks the nearest one that suits — so the generator
    /// places one or two and no more.
    /// </remarks>
    private const int ExtractionCap = 2;

    /// <param name="entrance">
    /// The room the team lands in, which is not a candidate while any other room exists.
    /// A mission whose objective is the room you walk in from the street has no building
    /// to cross -- and because both roles were being assigned to the same field, picking
    /// the entrance silently erased the entrance.
    /// </param>
    private SiteRoom PickObjective(SiteRegion region, SiteRoom entrance)
    {
        var required = SimulationRules.Tags(_template.ObjectiveRoomTags);
        SiteRoom? best = null;

        foreach (SiteFloor floor in region.Floors)
        {
            foreach (SiteRoom room in floor.Rooms)
            {
                if (room.Id == entrance.Id && region.RoomCount > 1)
                    continue;

                if (required.Count > 0 && !HasAnyTag(room, required))
                    continue;

                if (best is null || IsDeeperThan(room, best))
                    best = room;
            }
        }

        if (best is not null)
            return best;

        // No room carries a required tag. Documented fallback: the deepest room, so the
        // site is still well-formed and the data error the validator reports is visible
        // in play rather than swallowed here.
        //
        // The entrance exclusion applies here too. Leaving it off meant a warehouse whose
        // allowed rooms happen not to include anything tagged "office" put its objective
        // in the room the team walks in from the street -- and an objective that is also
        // the entrance has no second route to anything, so the site failed validation for
        // a reason that had nothing to do with the fault the fallback was papering over.
        SiteRoom? deepest = null;

        foreach (SiteFloor floor in region.Floors)
        {
            foreach (SiteRoom room in floor.Rooms)
            {
                if (room.Id == entrance.Id && region.RoomCount > 1)
                    continue;

                if (deepest is null || IsDeeperThan(room, deepest))
                    deepest = room;
            }
        }

        // Only reachable on a one-room building, where the entrance is all there is and
        // the room correctly carries both flags.
        return deepest ?? entrance;
    }

    /// <summary>True when a room carries at least one of the required tags.</summary>
    private static bool HasAnyTag(SiteRoom room, IReadOnlyList<string> required)
    {
        foreach (string tag in required)
        {
            if (room.Tags.Contains(tag))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a room is further from the entrance than another: deeper floor first, then
    /// further along it.
    /// </summary>
    private static bool IsDeeperThan(SiteRoom candidate, SiteRoom current)
    {
        if (candidate.FloorIndex != current.FloorIndex)
            return candidate.FloorIndex > current.FloorIndex;

        return candidate.StartX > current.StartX;
    }

    /// <summary>
    /// Picks which extraction point a journey to the forward post leaves from.
    /// </summary>
    /// <remarks>
    /// The team travels out from somewhere they were already going to be — an extraction
    /// point — rather than from the objective, because arriving at the post is a retreat
    /// and a retreat starts where you intended to leave.
    /// </remarks>
    /// <summary>
    /// Where the hop up to the forward post leaves from: a room on the building's top
    /// floor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floor is not a preference, it is a requirement. The post's floors are indexed
    /// one past the building's, so the hop is only a stair — one floor, which
    /// <see cref="SiteLayout.Validate"/> accepts and the pathfinder and the action system
    /// already know how to handle — when the departure room is directly below it. An
    /// earlier version preferred a top-floor extraction and otherwise fell back to any
    /// extraction at all, which on a building whose extractions are all on the ground
    /// floor produced a "stair" from floor 0 to floor 6: legal to generate, unreachable
    /// in practice, and passing validation the whole way.
    /// </para>
    /// <para>
    /// Among the top-floor rooms, an extraction is preferred over any other room because
    /// it is already a place a team might want to be; failing that, any room on the floor
    /// works, because the building's own stairs will carry the team to it. There is no
    /// fallback to a lower floor: if the top floor has no rooms the generator has a
    /// problem that a guessed departure room would only hide.
    /// </para>
    /// </remarks>
    private SiteRoom? PickForwardPostDeparture(IReadOnlyList<SiteRoomId> extractions, SiteRegion region)
    {
        int topFloor = Math.Max(0, region.Floors.Count - 1);
        var onTop = new List<SiteRoom>();
        var extractionsOnTop = new List<SiteRoom>();

        foreach (SiteRoom room in region.AllRooms)
        {
            if (room.FloorIndex != topFloor)
                continue;

            onTop.Add(room);

            if (extractions.Contains(room.Id))
                extractionsOnTop.Add(room);
        }

        if (extractionsOnTop.Count > 0)
            return extractionsOnTop[_rng.NextInt(0, extractionsOnTop.Count)];

        if (onTop.Count > 0)
            return onTop[_rng.NextInt(0, onTop.Count)];

        return null;
    }

    /// <summary>
    /// Attaches the forward post's room to this builder so the entry connection can name it.
    /// </summary>
    internal void SetForwardPostRoom(SiteRoomId roomId) => ForwardPostRoomId = roomId;

    /// <summary>
    /// Builds the one connection that joins the building to the forward post.
    /// </summary>
    /// <param name="post">The post's region, which the connection joins.</param>
    /// <param name="arrival">The room inside the post the team comes out into.</param>
    /// <param name="travelSteps">
    /// What the journey costs, from <c>site_gen_rule.site_forward_post_travel_steps</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// An ordinary vertical connection, which is the whole point. The journey reads as a
    /// stair up from the departure room's floor into the post's, costs
    /// <paramref name="travelSteps"/> rather than the handful a real stair costs, and is
    /// then routed, locked, saved and validated by machinery that has no idea there is
    /// anything unusual about it.
    /// </para>
    /// <para>
    /// A stair rather than a hole or a vent because the post is a place the team walks to
    /// deliberately and a guard may be posted in. Its
    /// <see cref="SiteConnection.UsableByNpc"/> is the connection type's, not a
    /// hard-coded true: a post a guard cannot walk into is a post that cannot be
    /// compromised, and the compromise rule needs somebody who can reach it.
    /// </para>
    /// </remarks>
    internal SiteConnection? LinkForwardPost(
        SiteRegion post, SiteRoom arrival, int travelSteps)
    {
        SiteRoom? departure = ForwardPostDeparture;

        if (departure is null)
            return null;

        if (arrival.FloorIndex != departure.FloorIndex + 1)
        {
            // Not a stair-shaped hop. A connection spanning more than one floor is not a
            // stair, `SiteLayout.Validate` rejects it, and letting one through would take
            // the whole site generation down with it — so the hop is refused and
            // `ValidateTravel` reports a post nobody can reach, which is a reportable bug
            // rather than a silently broken building.
            return null;
        }

        ConnectionTypeRow? type = SimulationRules.DefaultConnectionTypeFor(SiteConnectionKind.Stair);

        if (type is null)
            return null;

        var connection = new SiteConnection
        {
            Id = new SiteConnectionId(_nextConnectionId++),
            RoomA = departure.Id,
            FloorIndexA = departure.FloorIndex,
            RoomB = arrival.Id,
            FloorIndexB = arrival.FloorIndex,
            Kind = SimulationRules.ToCoreConnectionKind(type.Kind),
            ConnectionTypeId = type.Id,
            X = new Fixed32(departure.StartX.Raw + (departure.Span.Raw / 4)),
            UpperX = new Fixed32(arrival.StartX.Raw + (arrival.Span.Raw / 4)),
            TraverseSteps = travelSteps,
            IsLocked = false,
            BlocksVision = type.BlocksVision,
            UsableByNpc = type.UsableByNpc,
        };

        post.Connections.Add(connection);
        return connection;
    }

    // ---- step six: the guaranteed alternate route ----------------------------

    /// <summary>
    /// Adds a connection that closes a loop on the route to the objective, if the building
    /// does not already have one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief's "at least one alternate route" is the one invariant a chain-shaped
    /// building cannot satisfy by construction: rooms in a line, linked by doors, form a
    /// tree, and a tree has exactly one route between any two rooms. Something has to give.
    /// </para>
    /// <para>
    /// The three attempts below, in order, are the ways a real building creates a loop,
    /// and they are tried cheapest-first:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>A second vertical link.</b> Two stairs serving the same pair of floors, plus both
    /// floors' chains, is a loop — and it is what most buildings actually look like.
    /// </item>
    /// <item>
    /// <b>A back connection along the objective's floor.</b> Two rooms that share a wall
    /// get a second opening. An interior window between an office and the corridor is the
    /// least remarkable loop in existence.
    /// </item>
    /// <item>
    /// <b>A back connection between two rooms a chain runs between.</b> The last resort,
    /// and the only one that can fire on a one-room-per-floor building.
    /// </item>
    /// </list>
    /// <para>
    /// Every attempt is bounded and every one is checked, because "the generator usually
    /// makes a loop" is not a guarantee and the invariant is asserted.
    /// </para>
    /// </remarks>
    public void EnsureAlternateRouteToObjective(SiteRegion region)
    {
        if (region.Floors.Count == 0)
            return;

        int attempts = Math.Max(1, Rule("site_second_route_attempts_max", FallbackSecondRouteAttempts));
        int lockedPercent = Rule("site_locked_door_percent", FallbackLockedDoorPercent);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (AlreadyLooped(region))
                return;

            if (TrySecondVerticalLink(region, lockedPercent))
                return;

            if (TryBackConnectionOnObjectiveFloor(region, lockedPercent))
                return;

            if (TryBackConnectionAlongChain(region, lockedPercent))
                return;
        }
    }

    /// <summary>
    /// True when the region's rooms already contain a cycle, so a second route exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A graph has a cycle exactly when it has at least as many edges as vertices — but
    /// that is only true for a <em>connected</em> graph, and a disconnected one can easily
    /// clear the edge count while having no cycle at all. Skipping the connectivity test
    /// would mean a building whose rooms had drifted apart looked looped, the repair would
    /// be skipped, and the site would then fail <see cref="SiteLayout.Validate"/> and throw
    /// on a layout that a flood fill would have recognised as disconnected.
    /// </para>
    /// <para>
    /// So connectivity is checked too: one flood fill from the first room, and the edge
    /// count compared against the vertex count only if it reached everything. Both are
    /// O(V+E) and the whole point is to skip work when there is nothing to do.
    /// </para>
    /// </remarks>
    private static bool AlreadyLooped(SiteRegion region)
    {
        int vertices = region.RoomCount;
        if (vertices < 2)
            return false;

        SiteRoom? seed = null;
        var seen = new Dictionary<int, SiteRoomId>();
        var neighbours = new Dictionary<int, List<int>>();
        int edges = 0;

        foreach (SiteConnection connection in region.Connections)
        {
            edges++;

            if (!neighbours.TryGetValue(connection.RoomA.Value, out List<int>? to))
            {
                to = new List<int>();
                neighbours[connection.RoomA.Value] = to;
            }

            to.Add(connection.RoomB.Value);

            if (!neighbours.TryGetValue(connection.RoomB.Value, out List<int>? back))
            {
                back = new List<int>();
                neighbours[connection.RoomB.Value] = back;
            }

            back.Add(connection.RoomA.Value);
        }

        foreach (SiteFloor floor in region.Floors)
        {
            foreach (SiteRoom room in floor.Rooms)
            {
                seen[room.Id.Value] = room.Id;
                seed ??= room;
            }
        }

        if (seed is null || edges < vertices)
            return false;

        var reached = new HashSet<int> { seed.Id.Value };
        var frontier = new Queue<int>();
        frontier.Enqueue(seed.Id.Value);

        while (frontier.Count > 0)
        {
            int current = frontier.Dequeue();

            if (!neighbours.TryGetValue(current, out List<int>? adjacent))
                continue;

            foreach (int next in adjacent)
            {
                if (reached.Add(next))
                    frontier.Enqueue(next);
            }
        }

        return reached.Count == vertices && edges >= vertices;
    }

    /// <summary>
    /// Adds a second vertical link between two floors that already have one.
    /// </summary>
    private bool TrySecondVerticalLink(SiteRegion region, int lockedPercent)
    {
        _ = lockedPercent;

        for (int pair = 0; pair + 1 < region.Floors.Count; pair++)
        {
            SiteFloor lower = region.Floors[pair];
            SiteFloor upper = region.Floors[pair + 1];

            SiteConnection? existing = null;
            foreach (SiteConnection connection in region.Connections)
            {
                if (connection.FloorIndexA == pair && connection.FloorIndexB == pair + 1)
                {
                    existing = connection;
                    break;
                }
            }

            if (existing is null)
                continue;

            List<SiteConnectionKind> spares = SpareVerticalKinds(existing.Kind);
            if (spares.Count == 0)
                return false;

            // Prefer a different room at each end, so the loop closes through both floors'
            // chains rather than doubling up on one stairwell, and try every spare kind
            // because the extras pass may already have used one of them on this pair.
            foreach (SiteConnectionKind spare in spares)
            {
                foreach (SiteRoom origin in lower.Rooms)
                {
                    foreach (SiteRoom landing in upper.Rooms)
                    {
                        if (origin.Id == existing.RoomA && landing.Id == existing.RoomB)
                            continue;

                        if (MakeVertical(region, origin, landing, spare) is not null)
                            return true;
                    }
                }
            }

            // Then the same room pair, which is the only option left on a
            // one-room-per-floor building -- and is a real thing: a service ladder beside
            // the main stair.
            foreach (SiteConnectionKind spare in spares)
            {
                SiteRoom? fallbackOrigin = lower.Rooms.Count > 0 ? lower.Rooms[0] : null;
                SiteRoom? fallbackLanding = upper.Rooms.Count > 0 ? upper.Rooms[0] : null;

                if (fallbackOrigin is null || fallbackLanding is null)
                    continue;

                if (MakeVertical(region, fallbackOrigin, fallbackLanding, spare) is not null)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gives two rooms on the objective's floor a second opening.
    /// </summary>
    /// <remarks>
    /// Only rooms that already share a wall qualify, so the loop is an interior window
    /// rather than a hole in a load-bearing wall somewhere else in the building.
    /// </remarks>
    private bool TryBackConnectionOnObjectiveFloor(SiteRegion region, int lockedPercent)
    {
        SiteRoom? objective = Find(region, ObjectiveRoomId);
        if (objective is null)
            return false;

        SiteFloor? floor = FloorOf(region, objective.FloorIndex);
        if (floor is null || floor.Rooms.Count < 2)
            return false;

        for (int i = 1; i < floor.Rooms.Count; i++)
        {
            SiteRoom left = floor.Rooms[i - 1];
            SiteRoom right = floor.Rooms[i];

            if (!WallIsOpen(left, right))
                continue;

            // Already linked by the chain's own door; this adds the second opening, as a
            // window. An interior window between an office and a corridor is the least
            // remarkable loop in existence, and being a different kind it does not collide
            // with the door the chain already put there.
            _ = MakeHorizontal(region, left, right, lockedPercent, SiteConnectionKind.Window);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Links two rooms that a floor's chain already runs between.
    /// </summary>
    /// <remarks>
    /// The last resort, used when a floor holds exactly two rooms: it re-opens a wall the
    /// chain already crosses, which is what a building with a single loop door looks like.
    /// </remarks>
    private bool TryBackConnectionAlongChain(SiteRegion region, int lockedPercent)
    {
        foreach (SiteFloor floor in region.Floors)
        {
            if (floor.Rooms.Count < 2)
                continue;

            SiteRoom left = floor.Rooms[0];
            SiteRoom right = floor.Rooms[1];

            if (!WallIsOpen(left, right))
                continue;

            _ = MakeHorizontal(region, left, right, lockedPercent, SiteConnectionKind.Window);
            return true;
        }

        return false;
    }

    // ---- small lookups -------------------------------------------------------

    private static SiteRoom? Find(SiteRegion region, SiteRoomId id)
    {
        foreach (SiteRoom room in region.AllRooms)
        {
            if (room.Id == id)
                return room;
        }

        return null;
    }

    private static SiteFloor? FloorOf(SiteRegion region, int floorIndex)
    {
        foreach (SiteFloor floor in region.Floors)
        {
            if (floor.Index == floorIndex)
                return floor;
        }

        return null;
    }

    private int Rule(string key, int fallback) => SimulationRules.SiteGen(key, fallback);

    private const int FallbackMaxWeightTier = 3;
    private const int FallbackMinRooms = 2;
    private const int FallbackMaxRooms = 6;
    private const int FallbackLockedDoorPercent = 25;
    private const int FallbackLockedDoorPercentPerGrade = 3;
    private const int FallbackStairWeight = 60;
    private const int FallbackLadderWeight = 25;
    private const int FallbackVentWeight = 15;
    private const int FallbackVerticalExtraChance = 30;
    private const int FallbackSecondRouteAttempts = 8;
    private const int FallbackExtractionExtraChance = 35;
    private const int FallbackGuardRoomBonusPercent = 150;
    private const int FallbackEntranceEdgePercent = 50;

    /// <summary>
    /// The narrowest room the partition will ever place, in centimetres.
    /// </summary>
    /// <remarks>
    /// Only used when the candidate list is somehow empty, which
    /// <see cref="BuildRoomPool"/> and <see cref="CandidatesForFloor"/> already make
    /// impossible. A floor narrower than this cannot be partitioned, and one room that runs
    /// off the end of the floor would be caught by validation rather than here.
    /// </remarks>
    private const int FallbackMinRoomCm = 100;
}