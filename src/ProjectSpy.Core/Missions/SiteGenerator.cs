using ProjectSpy.Tables;

// The stage-2 tactical `room_template` bean, aliased for the same reason it is aliased
// everywhere else: Core's own `SiteRoom` would otherwise be the only other thing called
// a "room template" in scope, and the two mean completely different things.
using TacticalRoomTemplate = ProjectSpy.Tables.RoomTemplate;


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
/// Builds a mission site: a continuous, playable building, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// Two inputs and nothing else. Every decision — how many floors, how wide each one is,
/// which room goes where, which door is locked, who is standing in it — comes from a
/// private generator seeded from <see cref="MapSeed"/>. Not the world's Mission stream:
/// that advances with every roll any other system makes, so generating from it would mean
/// a save reloaded at a different moment in the run produced a different building. A
/// private stream means mission 7 of a world is the same building in the simulator, in a
/// replay and in the shipping game (rule 6).
/// </para>
/// <para>
/// <b>The generation order is fixed and every step is committed before the next begins</b>,
/// exactly as the stage-4a brief lays out. That is not tidiness: the order is what makes
/// the lazy-content rule work. Contents are generated later, keyed on room id, so as long
/// as room ids are handed out in a deterministic order nothing about "later" can reach
/// back and change what "earlier" decided.
/// </para>
/// <para>
/// <b>Every guarantee is structural, then asserted.</b> The generator makes the building
/// connected, looped and reachable by construction and then hands it to
/// <see cref="SiteLayout.Validate"/>, which re-checks all six invariants. A bad roll
/// cannot produce an unplayable site; a generator bug throws where it happened rather
/// than shipping a building the player cannot finish.
/// </para>
/// </remarks>
public static class SiteGenerator
{
    /// <summary>
    /// Derives the map seed for one mission of one world.
    /// </summary>
    /// <remarks>
    /// <c>WorldSeed + missionId</c>, mixed through the same routine that derives RNG
    /// streams, so there is exactly one documented way a seed is perturbed and two
    /// subsystems cannot drift into subtly different derivations. Mirrors
    /// <see cref="MissionMapGenerator.DeriveMapSeed"/> on purpose: a mission map and the
    /// building inside it are two views of one mission and must agree on its seed.
    /// </remarks>
    public static ulong DeriveMapSeed(ulong worldSeed, int missionId)
        => RngStreams.DeriveSeed(worldSeed, missionId);

    /// <summary>Generates a site.</summary>
    /// <param name="siteTemplateId">Foreign key into <c>site_template</c>.</param>
    /// <param name="tier">
    /// The tier. Read back from the template row, because the row is the authority and two
    /// callers passing different values should not silently produce different buildings.
    /// </param>
    /// <param name="missionId">Mission this site belongs to.</param>
    /// <param name="worldSeed">The world seed; appears in the result only.</param>
    /// <param name="mapSeed">
    /// Seed from <see cref="DeriveMapSeed"/>. Two calls with the same seed and template
    /// produce byte-identical sites.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The template is missing, or the tables cannot produce a playable building from it.
    /// That is a data error, not a runtime condition to recover from: a site that cannot
    /// be generated cannot be made playable by trying again.
    /// </exception>
    public static SiteLayout Generate(int siteTemplateId, int tier, int missionId, ulong worldSeed, ulong mapSeed)
    {
        SiteTemplateRow? template = SimulationRules.SiteTemplateFor(siteTemplateId);

        if (template is null)
        {
            throw new InvalidOperationException(
                $"No site_template row {siteTemplateId}. The site cannot be generated; this is a table data "
                + "error, not a runtime condition to recover from.");
        }

        var rng = new XorShift128Rng(mapSeed);
        var builder = new SiteBuilder(rng, template, siteTemplateId, missionId, worldSeed, mapSeed);

        // The brief's generation order, in order. Each step reads only what the previous
        // ones committed.
        var region = builder.BuildFloors();
        builder.PartitionFloors(region);
        builder.PlaceHorizontalConnections(region);
        builder.PlaceVerticalConnections(region);
        builder.MarkEntranceObjectiveAndExtraction(region);
        builder.EnsureAlternateRouteToObjective(region);
        // Guards and civilians are numbered across the whole site, not per region, so the
        // forward post cannot mint the same ids the main building already used.
        var ids = new SitePartIds();

        PlaceLightsAndOccluders(rng, region, template);
        PlaceInteractables(rng, region);
        EnsureObjectiveProp(rng, region, builder.ObjectiveRoomId);
        PlaceGuards(rng, region, template, ids, builder.EntranceRoomId);
        PlaceCivilians(rng, region, template, ids);

        SiteRegion? forwardPost = template.HasForwardPost
            ? BuildForwardPost(rng, template, builder, ids, region.Floors.Count)
            : null;

        var layout = new SiteLayout
        {
            SiteTemplateId = siteTemplateId,
            Tier = template.Tier,
            MissionId = missionId,
            WorldSeed = worldSeed,
            MapSeed = mapSeed,
            MainSite = region,
            ForwardPost = forwardPost,
            EntranceRoomId = builder.EntranceRoomId,
            ObjectiveRoomId = builder.ObjectiveRoomId,
            ExtractionRoomIds = builder.ExtractionRoomIds,
            ForwardPostRoomId = builder.ForwardPostRoomId,
        };

        if (!layout.Validate(out string problem))
        {
            // Unreachable by construction; if it ever fires the generator has a bug and a
            // site that violates its own guarantees must not reach a caller.
            throw new InvalidOperationException(
                $"Generated site {siteTemplateId} (tier {template.Tier}, mission {missionId}) failed "
                + $"validation: {problem}");
        }

        return layout;
    }

    // ---- rule access ---------------------------------------------------------

    /// <summary>Reads a <c>site_gen_rule</c> value with a documented fallback.</summary>
    private static int Rule(string key, int fallback) => SimulationRules.SiteGen(key, fallback);

    private const int FallbackMaxWeightTier = 3;
    private const int FallbackMinRoomsPerFloor = 2;
    private const int FallbackMaxRoomsPerFloor = 6;
    private const int FallbackLockedDoorPercent = 25;
    private const int FallbackLockedDoorPercentPerGrade = 3;
    private const int FallbackVerticalStairWeight = 60;
    private const int FallbackVerticalLadderWeight = 25;
    private const int FallbackVerticalVentWeight = 15;
    private const int FallbackVerticalExtraChancePercent = 30;
    private const int FallbackSecondRouteAttemptsMax = 8;
    private const int FallbackExtractionExtraChancePercent = 35;
    private const int FallbackLightSpanPerEmitterCm = 600;
    private const int FallbackOccluderSpanDivisor = 100;
    private const int FallbackLootContainerChancePercent = 55;
    private const int FallbackInteractableSpanDivisor = 500;
    private const int FallbackPatrolRoomMin = 2;
    private const int FallbackPatrolRoomMax = 5;
    private const int FallbackGuardRoomBonusPercent = 150;
    private const int FallbackForwardPostFloorCount = 1;
    private const int FallbackForwardPostRoomsMin = 2;
    private const int FallbackForwardPostRoomsMax = 3;
    private const int FallbackForwardPostTravelSteps = 900;
    private const int FallbackForwardPostGuardMax = 2;
    private const int FallbackEntranceEdgePercent = 50;

    // ---- forward post --------------------------------------------------------

    /// <summary>
    /// Builds the forward command post as its own small region.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal and separate. It gets one floor, a couple of rooms and a
    /// horizontal chain between them — the same partition machinery as the building, so
    /// there is one implementation of "lay rooms across a floor" rather than two that can
    /// disagree. It gets no lights' worth of tuning, no loot and no objective: a command
    /// post is a place to stand and plan, and a generator that dressed it up would be
    /// inventing content the design does not call for.
    /// </remarks>
    /// <param name="floorIndexOffset">
    /// The floor index the post's first floor takes. The building's top floor plus one,
    /// so a <see cref="TacticalPosition"/> in the post can never be mistaken for a
    /// position in the building.
    /// </param>
    private static SiteRegion? BuildForwardPost(
        IRng rng,
        SiteTemplateRow template,
        SiteBuilder builder,
        SitePartIds ids,
        int floorIndexOffset)
    {
        int floorCount = Math.Max(1, Rule("site_forward_post_floor_count", FallbackForwardPostFloorCount));
        int roomTarget = rng.NextInt(
            Math.Max(1, Rule("site_forward_post_rooms_min", FallbackForwardPostRoomsMin)),
            Math.Max(1, Rule("site_forward_post_rooms_max", FallbackForwardPostRoomsMax)) + 1);

        var region = new SiteRegion { Kind = SiteRegionKind.ForwardPost };

        for (int floorIndex = 0; floorIndex < floorCount; floorIndex++)
        {
            var floor = new SiteFloor
            {
                // Offset, because both regions draw from floor index zero and a position
                // carries nothing else to say which one it is in. `KindForIndex` still
                // takes the post's own ordinal: its first floor is the ground it stands
                // on, whatever number the building has reached.
                Index = floorIndexOffset + floorIndex,
                Kind = KindForIndex(floorIndex),
                Span = new Fixed32(ForwardPostSpanCm),
            };

            builder.PartitionFloor(region, floor, template, roomTarget, roomTarget, SingleFloor: false);
            region.Floors.Add(floor);
        }

        builder.LinkChainsHorizontally(region);

        // The post needs one room the entry connection can name, and it has to be
        // recorded on the builder too. Leaving this unset produced an edge pointing at
        // R000, which validation correctly rejected as a missing room.
        SiteRoom? arrival = null;
        foreach (SiteRoom room in region.AllRooms)
        {
            arrival = room;
            break;
        }

        if (arrival is not null)
        {
            arrival.Role |= SiteRoomRole.ForwardPost;
            builder.SetForwardPostRoom(arrival.Id);

            // The one connection that makes the post a place rather than a diagram. It
            // is a normal vertical connection from the departure room on the building's
            // top floor, so routing, door state, saving and the action system all work
            // on it without knowing anything about forward posts.
            builder.LinkForwardPost(
                region, arrival,
                Rule("site_forward_post_travel_steps", FallbackForwardPostTravelSteps));
        }

        PlaceForwardPostGuards(rng, region, template, ids);
        return region;
    }

    /// <summary>
    /// The forward post's span.
    /// </summary>
    /// <remarks>
    /// A schema constant rather than a tunable: it exists only so the post is a room
    /// rather than an empty interval, and nothing scales with it. Making it a rule row
    /// would invite a designer to widen the post until it needed its own lighting,
    /// lighting balance and room set — at which point it would be a second site
    /// template, and should be one.
    /// </remarks>
    private const int ForwardPostSpanCm = 2000;

    /// <summary>
    /// Stations a small guard detail in the post.
    /// </summary>
    /// <remarks>
    /// Capped well below the building's guard count, and every one of them stationary. A
    /// forward post that patrols its own two rooms is a post nobody would staff, and the
    /// cap is a rule row rather than a constant because "how many guards does a staging
    /// area need" is a balance question.
    /// </remarks>
    private static void PlaceForwardPostGuards(
        IRng rng,
        SiteRegion region,
        SiteTemplateRow template,
        SitePartIds ids)
    {
        int cap = Math.Min(Rule("site_forward_post_guard_max", FallbackForwardPostGuardMax), template.GuardCountMax);
        if (cap <= 0)
            return;

        var rooms = new List<SiteRoom>();
        foreach (SiteRoom room in region.AllRooms)
            rooms.Add(room);

        if (rooms.Count == 0)
            return;

        int count = rng.NextInt(0, cap + 1);
        for (int i = 0; i < count; i++)
        {
            SiteRoom room = rooms[rng.NextInt(0, rooms.Count)];

            region.Guards.Add(new SiteGuard
            {
                Id = ids.NextGuard(),
                ArchetypeId = PickArchetype(rng),
                NameKey = string.Empty,
                Role = SiteGuardRole.Sentry,
                HomeRoomId = room.Id,
                PatrolRoute = new[] { room.Id },
            });
        }
    }

    // ---- floors --------------------------------------------------------------

    /// <summary>
    /// What kind of floor an index is: ground at the bottom, upper above it.
    /// </summary>
    /// <remarks>
    /// A pure function of the index, so the floor a room lands on is decided before any
    /// room is chosen and every floor of a building is unambiguous. Basement is reachable
    /// as a declaration in <c>room_template.valid_floors</c> but not produced here yet;
    /// making one needs a below-ground entrance and a stairwell that descends, which is
    /// stage 4c's problem to define rather than this one's to fake.
    /// </remarks>
    internal static SiteFloorKind KindForIndex(int floorIndex)
        => floorIndex == 0 ? SiteFloorKind.Ground : SiteFloorKind.Upper;

    // ---- lights, occluders, interactables ------------------------------------

    /// <summary>
    /// Places emitters and sight blockers across every room of a region.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emitters come from the site's <c>lighting_profile</c>, one every
    /// <c>site_light_span_per_emitter_cm</c> of room span. The spacing is a rule row
    /// rather than a constant because "how brightly is a warehouse lit" is the single
    /// most important thing about whether an infiltration is tense, and it should be
    /// tunable without touching code.
    /// </para>
    /// <para>
    /// Both lights and occluders are committed rather than deferred, unlike room
    /// contents. Whether a room is lit and whether it is a straight sightline are things
    /// a player can judge from the doorway; generating them lazily would mean building
    /// contents to answer a question about a room nobody entered, which is the leak rule 11
    /// forbids.
    /// </para>
    /// </remarks>
    private static void PlaceLightsAndOccluders(IRng rng, SiteRegion region, SiteTemplateRow template)
    {
        LightingProfileRow? profile = SimulationRules.LightingProfileFor(template.LightingProfileId);
        var emitters = new List<LightSourceRow>();

        if (profile is not null)
        {
            foreach (int id in SimulationRules.IntList(profile.EmitterIds))
            {
                LightSourceRow? light = SimulationRules.LightSourceFor(id);
                if (light is not null)
                    emitters.Add(light);
            }
        }

        if (emitters.Count == 0)
        {
            // A site with no usable lighting profile would generate a building nothing
            // can be perceived in. That is a table error, and it is loud rather than a
            // dark building nobody can debug.
            throw new InvalidOperationException(
                $"Site {template.Id} names lighting_profile {template.LightingProfileId}, which resolves to no "
                + "light_source rows. The site would generate with no lights at all; this is a table data error.");
        }

        int spacingCm = Math.Max(1, Rule("site_light_span_per_emitter_cm", FallbackLightSpanPerEmitterCm));
        int occluderDivisor = Math.Max(1, Rule("site_occluder_span_divisor", FallbackOccluderSpanDivisor));

        foreach (SiteFloor floor in region.Floors)
        {
            foreach (SiteRoom room in floor.Rooms)
            {
                // One emitter per `spacingCm` of span, spread evenly. Even rather than
                // random because a room whose lights are clustered at one end is a room
                // with a dark end, and that reads as a bug in every presentation.
                int lightCount = Math.Max(1, room.Span.Raw / spacingCm);
                int lightStep = room.Span.Raw / lightCount;

                for (int i = 0; i < lightCount; i++)
                {
                    LightSourceRow emitter = emitters[rng.NextInt(0, emitters.Count)];

                    // Centre of each slot, so a two-light room has them at a quarter and
                    // three quarters rather than hard against the walls.
                    int x = room.StartX.Raw + (lightStep * i) + (lightStep / 2);

                    region.Lights.Add(new SiteLight
                    {
                        Id = region.Lights.Count + 1,
                        RoomId = room.Id,
                        LightSourceId = emitter.Id,
                        X = new Fixed32(x),
                        Level = SimulationRules.ToCoreLightLevel(emitter.IntensityLevel),
                        RadiusCm = emitter.RadiusCm,
                    });
                }

                TacticalRoomTemplate? roomTemplate = SimulationRules.TacticalRoomTemplateFor(room.RoomTemplateId);
                int density = roomTemplate?.OccluderDensity ?? FallbackOccluderDensity;
                int occluderCount = SimulationRules.PercentOf(room.Span.Raw, density) / occluderDivisor;

                if (occluderCount <= 0)
                    continue;

                int occluderStep = room.Span.Raw / occluderCount;

                for (int i = 0; i < occluderCount; i++)
                {
                    region.Occluders.Add(new SiteOccluder
                    {
                        Id = region.Occluders.Count + 1,
                        RoomId = room.Id,
                        X = new Fixed32(room.StartX.Raw + (occluderStep * i) + (occluderStep / 2)),
                    });
                }
            }
        }
    }

    /// <summary>Occluder density used when a room template row is unavailable.</summary>
    private const int FallbackOccluderDensity = 20;

    /// <summary>
    /// Places the interactables a room's tags permit.
    /// </summary>
    /// <remarks>
    /// The candidate list comes from <c>interactable_type.allowed_room_tags</c> filtered by
    /// the room's own tags — the same filter node interiors use, which is why a terminal
    /// turns up in a server room and not in a corridor.
    /// <para>
    /// Three kinds are excluded from the random fill: <see cref="InteractableType.Exit"/>
    /// and <see cref="InteractableType.Objective"/> because the generator places those
    /// deliberately at the entrance and the objective room, and
    /// <see cref="InteractableType.Container"/> because that is the loot container this
    /// method places under its own rule, with the room's loot table. Rolling a second
    /// container here would put one in a room whose template declares no loot table, and
    /// a sight that promised nothing would then hold something.
    /// </para>
    /// </remarks>
    private static void PlaceInteractables(IRng rng, SiteRegion region)
    {
        int spanDivisor = Math.Max(1, Rule("site_interactable_span_divisor", FallbackInteractableSpanDivisor));
        int lootChance = Rule("site_loot_container_chance_percent", FallbackLootContainerChancePercent);

        foreach (SiteRoom room in region.AllRooms)
        {
            List<InteractableTypeRow> candidates = CandidatesFor(room);
            int target = Math.Max(1, room.Span.Raw / spanDivisor);
            int step = room.Span.Raw / Math.Max(1, target);

            int placed = 0;

            for (int i = 0; i < target && placed < target; i++)
            {
                InteractableTypeRow? row = candidates.Count == 0
                    ? null
                    : rng.WeightedPick(candidates, WeightsOf(candidates));

                if (row is null)
                    break;

                int x = room.StartX.Raw + (step * i) + (step / 2);

                region.Interactables.Add(new SiteInteractable
                {
                    Id = region.Interactables.Count + 1,
                    RoomId = room.Id,
                    Kind = SimulationRules.ToCoreKind(row.Kind),
                    X = new Fixed32(x),
                    LootTableId = row.Kind == TableInteractableKind.Container ? room.LootTableId : 0,
                });

                placed++;
            }

            // The loot container is placed on the room's own promise rather than the
            // weighted draw, and only when the room has a loot table to roll from.
            if (room.LootTableId <= 0 || rng.NextInt(1, 101) > lootChance)
                continue;

            region.Interactables.Add(new SiteInteractable
            {
                Id = region.Interactables.Count + 1,
                RoomId = room.Id,
                Kind = InteractableType.Container,
                X = new Fixed32(room.StartX.Raw + (room.Span.Raw / 2)),
                LootTableId = room.LootTableId,
            });
        }
    }

    /// <summary>
    /// Guarantees that the objective room holds something the objective can be done on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A guarantee, not a probability. The weighted draw in
    /// <see cref="PlaceInteractables"/> skips <see cref="TableInteractableKind.Objective"/>
    /// entirely and picks Terminals by room tag, so whether the objective room had
    /// anything hackable in it came down to whether its room template happened to carry
    /// <c>terminal</c>, <c>server</c>, <c>lab</c>, <c>tech</c>, <c>intel</c> or
    /// <c>office</c> — and how the roll went on top of that. Roughly half the sites came
    /// out with nothing, and a StealData on one of those had no terminal anywhere to aim
    /// at.
    /// </para>
    /// <para>
    /// <b>An objective room with nothing in it is not a variation, it is a hole.</b>
    /// There is no objective type that can be completed by standing in an empty room, so
    /// leaving the room bare made part of the mission's content — the thing the whole
    /// route is built around — a property of a CSV row the generator happened to draw.
    /// </para>
    /// <para>
    /// Placed after the weighted pass so it cannot be displaced, and without a
    /// <see cref="IRng"/> draw so that changing the loot rolls cannot change it: the same
    /// seed always produces the same objective room, whatever else moved.
    /// </para>
    /// </remarks>
    private static void EnsureObjectiveProp(IRng rng, SiteRegion region, SiteRoomId objectiveRoom)
    {
        SiteRoom? room = region.AllRooms.FirstOrDefault(r => r.Id == objectiveRoom);

        if (room is null)
            return;

        // Already something to work on: the weighted draw found a terminal here, and
        // adding an Objective prop as well would give the room two candidate targets
        // and make which one the mission wants a question the player has to guess at.
        foreach (SiteInteractable existing in region.Interactables)
        {
            if (existing.RoomId == objectiveRoom
                && existing.Kind is InteractableType.Terminal or InteractableType.Objective)
            {
                return;
            }
        }

        InteractableTypeRow? row = SimulationRules.InteractableTypesForRoom(room.Tags)
            .FirstOrDefault(r => r.Kind == TableInteractableKind.Objective);

        region.Interactables.Add(new SiteInteractable
        {
            Id = region.Interactables.Count + 1,
            RoomId = room.Id,
            Kind = row is null ? InteractableType.Objective : SimulationRules.ToCoreKind(row.Kind),
            X = new Fixed32(room.StartX.Raw + (room.Span.Raw / 2)),
            LootTableId = 0,
        });
    }

    /// <summary>
    /// The interactable kinds a room may hold, as table rows, in table order.
    /// </summary>
    private static List<InteractableTypeRow> CandidatesFor(SiteRoom room)
    {
        var candidates = new List<InteractableTypeRow>();

        foreach (InteractableTypeRow row in SimulationRules.InteractableTypesForRoom(room.Tags))
        {
            if (row.Weight <= 0)
                continue;

            switch (row.Kind)
            {
                case TableInteractableKind.Exit:
                case TableInteractableKind.Objective:
                case TableInteractableKind.Container:
                case TableInteractableKind.Guard:
                    continue;

                default:
                    candidates.Add(row);
                    break;
            }
        }

        return candidates;
    }

    private static IReadOnlyList<int> WeightsOf(IReadOnlyList<InteractableTypeRow> rows)
    {
        var weights = new List<int>(rows.Count);
        foreach (InteractableTypeRow row in rows)
            weights.Add(row.Weight);
        return weights;
    }

    // ---- guards and civilians ------------------------------------------------

    /// <summary>
    /// Places guards with patrol routes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Count comes from <c>site_template.guard_count_min/max</c>, and is committed here
    /// rather than left to a later roll for the same reason it is on the abstract map's
    /// skeleton: fog of war has to be able to report a guard band for a room nobody has
    /// entered.
    /// </para>
    /// <para>
    /// Routes are grown from the guard's home room over connections an NPC can use, so a
    /// route is walkable by construction. A guard whose home can reach nothing walks a
    /// one-room route instead — a stationary post — which is honest: the alternative is
    /// inventing a patrol through a wall.
    /// </para>
    /// </remarks>
    private static void PlaceGuards(
        IRng rng,
        SiteRegion region,
        SiteTemplateRow template,
        SitePartIds ids,
        SiteRoomId entranceRoomId)
    {
        if (template.GuardCountMax <= 0)
            return;

        var allRooms = new List<SiteRoom>(region.AllRooms);
        if (allRooms.Count == 0)
            return;

        // The entrance is where the squad is inserted, so no guard is homed there.
        //
        // Without this a site could hand a guard the same room the team deploys into,
        // and the mission would be lost before the player had given an order: the guard
        // would start inside identification range of the whole squad, perceive it on
        // step one, and drive the site to Burned in half a minute with the player
        // watching. That is not difficulty, it is a mission that cannot be played.
        //
        // It is a structural property of the building rather than a balance number —
        // there is no column for it and no setting should produce one — and a site with
        // only one guard-capable room still places its guard there, because refusing to
        // place guards at all would be the worse failure.
        var guardRooms = GuardCapableRooms(allRooms).Where(room => room.Id != entranceRoomId).ToList();

        // The entrance has to come out of the candidate pool, not merely out of the
        // preferred one: PickRoom weights every room it is given, so a room excluded
        // only from `guardRooms` would still be picked at weight 1 and the guard would
        // still end up in the entrance often enough to matter.
        var guardCandidates = allRooms.Where(room => room.Id != entranceRoomId).ToList();

        if (guardCandidates.Count == 0)
            guardCandidates = allRooms;
        int patrolMin = Math.Max(1, Rule("site_patrol_room_min", FallbackPatrolRoomMin));
        int patrolMax = Math.Max(patrolMin, Rule("site_patrol_room_max", FallbackPatrolRoomMax));

        int count = rng.NextInt(template.GuardCountMin, template.GuardCountMax + 1);
        int bonusPercent = Rule("site_guard_room_bonus_percent", FallbackGuardRoomBonusPercent);

        for (int i = 0; i < count; i++)
        {
            // Guards prefer rooms whose template allows one. The preference is a weight
            // bonus rather than a hard filter so a site whose rooms are all guard-capable
            // still varies its patrols, and so a site with none does not fail to place
            // any — it just picks uniformly, which the validator separately flags as a
            // data error.
            SiteRoom home = PickRoom(rng, guardRooms, guardCandidates, bonusPercent);
            IReadOnlyList<SiteRoomId> route = BuildPatrolRoute(rng, region, home, patrolMin, patrolMax);
            GuardArchetypeRow? archetype = PickArchetypeRow(rng);

            region.Guards.Add(new SiteGuard
            {
                Id = ids.NextGuard(),
                ArchetypeId = archetype?.Id ?? FallbackArchetypeId,
                NameKey = archetype?.NameKey ?? string.Empty,
                Role = archetype is null ? SiteGuardRole.Patrol : SimulationRules.ToCoreGuardRole(archetype.Role),
                HomeRoomId = home.Id,
                PatrolRoute = route,
            });
        }
    }

    private static IReadOnlyList<SiteRoomId> BuildPatrolRoute(
        IRng rng, SiteRegion region, SiteRoom home, int minRooms, int maxRooms)
    {
        int target = rng.NextInt(minRooms, maxRooms + 1);
        var route = new List<SiteRoomId> { home.Id };

        // Grow outwards from the home over connections an NPC can use. Choosing among
        // *unused* neighbours keeps the route from ping-ponging between two rooms, which
        // is what a naive "pick any neighbour" walk does and what makes a patrol look
        // broken in the ascii dump.
        var visited = new HashSet<SiteRoomId> { home.Id };
        SiteRoomId cursor = home.Id;

        while (route.Count < target)
        {
            var open = new List<SiteConnection>();
            foreach (SiteConnection connection in ConnectionsAt(region, cursor))
            {
                if (!connection.UsableByNpc)
                    continue;

                SiteRoomId next = connection.Other(cursor);
                if (visited.Contains(next))
                    continue;

                open.Add(connection);
            }

            if (open.Count == 0)
                break;

            SiteConnection chosen = open[rng.NextInt(0, open.Count)];
            cursor = chosen.Other(cursor);
            visited.Add(cursor);
            route.Add(cursor);
        }

        return route;
    }

    /// <summary>Connections touching a room within one region.</summary>
    private static IReadOnlyList<SiteConnection> ConnectionsAt(SiteRegion region, SiteRoomId room)
    {
        var touching = new List<SiteConnection>();
        foreach (SiteConnection connection in region.Connections)
        {
            if (connection.RoomA == room || connection.RoomB == room)
                touching.Add(connection);
        }

        return touching;
    }

    /// <summary>Rooms whose template allows a static guard.</summary>
    private static List<SiteRoom> GuardCapableRooms(List<SiteRoom> rooms)
    {
        var capable = new List<SiteRoom>();
        foreach (SiteRoom room in rooms)
        {
            if (room.AllowsNpc(NpcTagGuard))
                capable.Add(room);
        }

        return capable;
    }

    /// <summary>
    /// Picks a room, preferring the guard-capable ones by a weight bonus.
    /// </summary>
    private static SiteRoom PickRoom(IRng rng, List<SiteRoom> preferred, List<SiteRoom> all, int bonusPercent)
    {
        if (preferred.Count == 0 || all.Count == 0)
            return all[rng.NextInt(0, all.Count)];

        var weights = new List<int>(all.Count);
        foreach (SiteRoom room in all)
            weights.Add(preferred.Contains(room) ? bonusPercent : 1);

        return rng.WeightedPick(all, weights);
    }

    /// <summary>
    /// Picks a guard archetype uniformly.
    /// </summary>
    /// <remarks>
    /// Uniform rather than weighted because <c>guard_archetype</c> has no weight column
    /// — the mix is currently decided by the archetype count and by the site's guard
    /// count, and inventing a weight column to tune something the data does not yet
    /// express would be a tuning knob with nothing behind it. A site that wants a heavier
    /// security detail does it with <c>guard_count_max</c>.
    /// </remarks>
    private static GuardArchetypeRow? PickArchetypeRow(IRng rng)
    {
        IReadOnlyList<GuardArchetypeRow> archetypes = SimulationRules.AllGuardArchetypes();
        if (archetypes.Count == 0)
            return null;

        return archetypes[rng.NextInt(0, archetypes.Count)];
    }

    /// <summary>Archetype id used when the table is unavailable.</summary>
    private const int FallbackArchetypeId = 0;

    /// <summary>
    /// Places civilians in rooms whose template allows them.
    /// </summary>
    /// <remarks>
    /// Count comes from <c>site_template.civilian_count</c> and is an exact count rather
    /// than a range, because the column is a single number — a site that promises 22
    /// civilians and delivers 18 has mislaid four people somewhere in the building, which
    /// is the kind of bug that is very hard to see in play.
    /// </remarks>
    private static void PlaceCivilians(IRng rng, SiteRegion region, SiteTemplateRow template, SitePartIds ids)
    {
        if (template.CivilianCount <= 0)
            return;

        var candidates = new List<SiteRoom>();
        foreach (SiteRoom room in region.AllRooms)
        {
            if (room.AllowsNpc(NpcTagCivilian))
                candidates.Add(room);
        }

        if (candidates.Count == 0)
            return;

        for (int i = 0; i < template.CivilianCount; i++)
        {
            region.Civilians.Add(new SiteCivilian
            {
                Id = ids.NextCivilian(),
                RoomId = candidates[rng.NextInt(0, candidates.Count)].Id,
            });
        }
    }

    /// <summary>The <c>civilian</c> npc_tag, as it appears in <c>room_template</c>.</summary>
    internal const string NpcTagCivilian = "civilian";

    /// <summary>The <c>guard</c> npc_tag, as it appears in <c>room_template</c>.</summary>
    internal const string NpcTagGuard = "guard";

    /// <summary>Picks an archetype id. Used by the builder's patrol code.</summary>
    private static int PickArchetype(IRng rng) => PickArchetypeRow(rng)?.Id ?? FallbackArchetypeId;
}