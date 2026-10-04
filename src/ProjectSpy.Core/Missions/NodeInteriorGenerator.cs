using ProjectSpy.Tables;

// The generated bean is named InteractableType, which collides with Core's enum of
// resolved values. Aliased so the distinction between "a row defining a kind" and "a
// kind" stays visible at every use site.
using InteractableTypeRow = ProjectSpy.Tables.InteractableType;
using NodeInteriorTemplateRow = ProjectSpy.Tables.NodeInteriorTemplate;
using TableInteractableKind = ProjectSpy.Tables.InteractableKind;

namespace ProjectSpy.Core;

/// <summary>
/// Builds a mission node's interior — its <see cref="RoomContents"/> — on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is internal on purpose.</b> It is the one place that can conjure a
/// node's contents, and making it public would hand the UI a fog-free path to every
/// interior on the map: <c>NodeInteriorGenerator.Generate(node, mapSeed)</c> takes no
/// visibility into account, so a caller could build the room the player has not entered
/// yet. Keeping it internal means <see cref="FogOfWar"/> is the only way out of Core
/// that can produce contents, and that path is gated by what the player has observed.
/// <c>FogOfWarLeakTests</c> asserts that over the compiled public surface.
/// </para>
/// <para>
/// <b>Determinism is the contract.</b> The generator is seeded from
/// <c>MapSeed + nodeId</c> rather than from a shared stream, so a node's interior is
/// identical whether it is generated on tick 1 or tick 200, and whether it is generated
/// before or after its neighbours. Generation order cannot change the result, which is
/// what makes lazy generation safe to use at all: if interiors came from the world's
/// Mission stream, the contents of the room you walk into first would depend on how
/// many rooms you happened to look at, and a save reloaded later would produce a
/// different building.
/// </para>
/// <para>
/// <b>Table-driven.</b> What a room contains comes from <c>room_template</c> (how many
/// objects, which kinds are mandatory, how many abstract slots) and
/// <c>interactable_type</c> (which kinds are legal in a room with these tags, and how
/// likely each is). No count, chance or tag is written here — knowledge.md rule 3.
/// The only thing this file decides is <em>placement</em>: which abstract slot each
/// object takes, and which states are legal for its kind.
/// </para>
/// <para>
/// <b>No coordinates.</b> Slots are abstract indices; Presentation decides what slot 4
/// looks like (knowledge.md rule 10).
/// </para>
/// </remarks>
internal static class NodeInteriorGenerator
{
    /// <summary>
    /// Kinds a room may hold at most one of, because two of them is a question with no
    /// defined answer rather than a harder room.
    /// </summary>
    /// <remarks>
    /// Enforced against the template, not left to chance: <see cref="RoomContents.Validate"/>
    /// rejects a room with two exits, and a generator that could roll one is a
    /// generator that will occasionally throw mid-mission.
    /// </remarks>
    private static readonly InteractableType[] UniqueKinds =
    {
        InteractableType.Exit,
        InteractableType.Objective,
    };

    /// <summary>
    /// Generates the interior for one node. Internal: see the type remarks.
    /// </summary>
    /// <param name="node">The node being observed.</param>
    /// <param name="mapSeed">The map's seed.</param>
    public static RoomContents Generate(MissionNode node, ulong mapSeed)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));

        // Keyed by the node, not drawn from a shared sequence: see the remarks. The
        // tag argument is part of the derivation so a future caller cannot
        // accidentally reuse this stream for a different purpose and have the two
        // interfere.
        var rng = new XorShift128Rng(
            RngStreams.DeriveSeed(RngStreams.DeriveSeed(mapSeed, node.Id.Value), ContentsStreamTag));

        NodeInteriorTemplateRow? template = SimulationRules.RoomTemplateFor(node.RoomTypeId);
        var room = new RoomContents { SlotCount = SlotCountFor(template) };

        var candidates = CandidatesFor(node, template);

        if (candidates.Count == 0)
        {
            // A room with no legal interactable type cannot be populated. Emptier than
            // the template asked for, but a valid room beats an exception in the middle
            // of a mission, and the validator is what keeps this unreachable.
            room.Validate(out _);
            return room;
        }

        Populate(room, rng, node, template, candidates);
        PlaceInitialStates(room, rng, node);

        // A generated interior that failed its own validator would mean a rule bug that
        // surfaces as a player standing in a room with two exits. Every generation is
        // checked rather than assumed.
        if (!room.Validate(out string problem))
        {
            throw new InvalidOperationException(
                $"Generated an invalid interior for node {node.Id} (room {node.RoomTypeId}): {problem}");
        }

        return room;
    }

    /// <summary>
    /// The tag the contents stream is derived under.
    /// </summary>
    /// <remarks>
    /// Exists so that the node-interior stream is namespaced away from every other
    /// stream derived from the same map seed. If a future system wanted a second
    /// per-node stream it would derive its own tag rather than colliding with this one.
    /// </remarks>
    private const int ContentsStreamTag = 0x0C07;

    /// <summary>Abstract slots the room has, from the template, floored at one.</summary>
    private static int SlotCountFor(NodeInteriorTemplateRow? template)
        => Math.Max(1, template?.SlotCount ?? InteriorFallbacks.SlotCount);

    /// <summary>
    /// The kinds this room may contain, as table rows, in table order.
    /// </summary>
    /// <remarks>
    /// Filtering by the node's own tags is what makes <c>allowed_room_tags</c> mean
    /// something: a terminal is worth putting in a server room and pointless in a
    /// corridor, and the table is where a designer changes that.
    /// </remarks>
    private static List<InteractableTypeRow> CandidatesFor(MissionNode node, NodeInteriorTemplateRow? template)
    {
        var allowed = new List<InteractableTypeRow>();

        foreach (InteractableTypeRow row in SimulationRules.InteractableTypesForRoom(node.Tags))
        {
            if (row.TickCost < 0)
                continue;

            // A unique kind the template did not ask for may still be placed, but only
            // once and never alongside another of its kind.
            allowed.Add(row);
        }

        return allowed;
    }

    /// <summary>
    /// Fills the room: mandatory kinds first, then a weighted fill up to the template's
    /// population range.
    /// </summary>
    /// <remarks>
    /// Order matters. Mandatory kinds go in before the random fill, so a template
    /// promising a guard always gets one even if the room is nearly full — a promise the
    /// generator could break by rolling badly would be a lie the map tells.
    /// </remarks>
    private static void Populate(
        RoomContents room,
        IRng rng,
        MissionNode node,
        NodeInteriorTemplateRow? template,
        List<InteractableTypeRow> candidates)
    {
        var placed = new Dictionary<InteractableType, int>();

        foreach (InteractableType kind in RequiredKinds(template))
        {
            PlaceOnce(room, rng, kind, placed, node.GuardCount);
        }

        // A node whose sight promises loot must hold something to find. The fog read
        // model shows `has_loot` from the skeleton, which the player can see while
        // merely scouted — so an interior that rolled no container would make the
        // sight a lie, and fog of war that lies is worse than fog of war that hides.
        //
        // Placed before the random fill for the same reason required kinds are: a
        // promise the generator could break by rolling badly is not a promise.
        //
        // Only when the room's own tags permit a container. Every node_room carries a
        // loot table, so this fires on almost every map, and forcing a container into a
        // room whose tags exclude one would quietly break the
        // allowed_room_tags rule the rest of the generator honours — a generator that
        // smuggles in a forbidden kind for its own convenience is worse than one that
        // honours the data and lets the sight stand.
        if (node.HasLoot && Permits(candidates, InteractableType.Container))
            PlaceOnce(room, rng, InteractableType.Container, placed, node.GuardCount);

        int min = Math.Max(0, template?.MinInteractables ?? 0);
        int max = Math.Max(min, template?.MaxInteractables ?? min);

        int target = rng.NextInt(min, max + 1);

        // The guards the node's skeleton promised are placed explicitly rather than
        // left to the weighted fill, because the fog read model has already told the
        // player how many to expect. A guard count the interior cannot honour would
        // make a Scouted sight a lie.
        PlaceGuards(room, rng, node.GuardCount, placed);

        while (room.Count < target)
        {
            InteractableTypeRow? row = PickAdmissible(rng, candidates, placed, node.GuardCount);
            if (row is null)
                break; // only unique kinds left, or nothing legal for this room's tags

            InteractableType kind = SimulationRules.ToCoreKind(row.Kind);

            Place(room, rng, kind);
            placed[kind] = placed.TryGetValue(kind, out int count) ? count + 1 : 1;
        }
    }

    /// <summary>The kinds a template insists on, in table order.</summary>
    private static IEnumerable<InteractableType> RequiredKinds(NodeInteriorTemplateRow? template)
    {
        if (template is null) yield break;

        foreach (string kindName in SimulationRules.Tags(template.RequiredKinds))
        {
            if (Enum.TryParse(kindName, ignoreCase: true, out InteractableType kind))
                yield return kind;

            // The template may also name a kind in the table's own vocabulary, which is
            // a different enum with different numeric values. Parsed explicitly rather
            // than cast: a cast would map "Guard" to the wrong kind and produce a room
            // with two exits where a guard should be.
            else if (Enum.TryParse(kindName, ignoreCase: true, out TableInteractableKind tableKind))
                yield return SimulationRules.ToCoreKind(tableKind);
        }
    }

    /// <summary>
    /// Places exactly <paramref name="count"/> guards, or fewer if the room will not
    /// hold them.
    /// </summary>
    /// <remarks>
    /// Fewer is possible and is why the fog tests assert the skeleton and the interior
    /// agree <em>where the room has room</em>: a two-slot room promised two guards will
    /// only ever fit one, and the alternative is throwing away a mission over a
    /// cosmetic count.
    /// </remarks>
    private static void PlaceGuards(
        RoomContents room,
        IRng rng,
        int count,
        Dictionary<InteractableType, int> placed)
    {
        if (count <= 0)
            return;

        for (int i = 0; i < count; i++)
        {
            if (PlaceOnce(room, rng, InteractableType.Guard, placed, count) is null)
                return;
        }
    }

    /// <summary>
    /// Picks a row weighted by its <c>weight</c>, skipping kinds already at their limit.
    /// </summary>
    /// <remarks>
    /// Filtering happens before the weighted draw rather than after, so the draw is over
    /// legal candidates only. Rejection-sampling instead would consume a different
    /// number of rolls depending on how many duplicates came up, which is exactly the
    /// kind of hidden coupling that makes a seeded result depend on an unrelated change.
    /// </remarks>
    private static InteractableTypeRow? PickAdmissible(
        IRng rng,
        List<InteractableTypeRow> candidates,
        Dictionary<InteractableType, int> placed,
        int guardLimit)
    {
        var admissible = new List<InteractableTypeRow>();
        var weights = new List<int>();

        foreach (InteractableTypeRow row in candidates)
        {
            InteractableType kind = SimulationRules.ToCoreKind(row.Kind);

            placed.TryGetValue(kind, out int count);

            if (IsUnique(kind) && count >= 1)
                continue;

            // Guards are capped by the count the skeleton already committed to. Without
            // this the random fill could roll a guard into a room the map promised was
            // unguarded, and the fog sight would then understate the threat — a leak in
            // the safe direction for the player and a broken promise either way.
            if (kind == InteractableType.Guard && count >= guardLimit)
                continue;

            if (row.Weight <= 0)
                continue;

            admissible.Add(row);
            weights.Add(row.Weight);
        }

        if (admissible.Count == 0)
            return null;

        return rng.WeightedPick(admissible, weights);
    }

    private static bool IsUnique(InteractableType kind)
    {
        foreach (InteractableType unique in UniqueKinds)
        {
            if (unique == kind)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when one of this room's permitted candidates is the given kind.
    /// </summary>
    /// <remarks>
    /// Checked before a kind is placed for a reason that is not its own. The loot
    /// container is placed on the node's promise rather than the table's permission, so
    /// it is the one placement that can be asked for something the room does not allow.
    /// </remarks>
    private static bool Permits(List<InteractableTypeRow> candidates, InteractableType kind)
    {
        foreach (InteractableTypeRow row in candidates)
        {
            if (SimulationRules.ToCoreKind(row.Kind) == kind)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Places one object of a named kind, or returns null when the room cannot hold it.
    /// </summary>
    private static Interactable? PlaceOnce(
        RoomContents room,
        IRng rng,
        InteractableType kind,
        Dictionary<InteractableType, int> placed,
        int guardLimit)
    {
        placed.TryGetValue(kind, out int existing);

        // A unique kind already present is a no-op rather than an error: the template
        // asked for it, it is there, and adding a second would be illegal.
        if (IsUnique(kind) && existing >= 1)
            return null;

        // Same guard cap as the random fill. A template requiring a Guard in a room the
        // generator rolled zero guards for must not smuggle one back in, or the
        // committed count stops being the truth.
        //
        // `existing` defaults to zero when the kind has not been placed yet, which is
        // what makes a limit of zero actually block a first placement rather than only
        // blocking the second.
        if (kind == InteractableType.Guard && existing >= guardLimit)
            return null;

        Interactable? result = Place(room, rng, kind);

        if (result is not null)
            placed[kind] = placed.TryGetValue(kind, out int count) ? count + 1 : 1;

        return result;
    }

    /// <summary>
    /// Gives each object a legal starting state for its kind.
    /// </summary>
    /// <remarks>
    /// Only doors and containers vary, and only between shut and open-ish. A guard
    /// starts watching and a trap starts armed because the alternative — a trap that
    /// begins triggered — is a room where the hazard has already fired.
    /// </remarks>
    private static void PlaceInitialStates(RoomContents room, IRng rng, MissionNode node)
    {
        int lockedPercent = SimulationRules.NodeInterior(
            "interior_locked_container_percent", InteriorFallbacks.LockedContainerPercent);

        foreach (Interactable interactable in room.Interactables)
        {
            switch (interactable.Type)
            {
                case InteractableType.Container:
                    interactable.State = rng.NextInt(1, 101) <= lockedPercent
                        ? InteractableState.Closed
                        : InteractableState.Idle;
                    break;

                case InteractableType.Door:
                    // A door blocks the slot behind it, which is the abstract form of
                    // "the doorway behind it is shut". Core names slots; Presentation
                    // draws the doorway (knowledge.md rule 10).
                    foreach (int slot in FreeSlots(room))
                    {
                        interactable.BlockedSlotIndices.Add(slot);
                        break;
                    }

                    interactable.State = InteractableState.Closed;
                    break;

                case InteractableType.Guard:
                    interactable.State = InteractableState.Active;
                    break;

                case InteractableType.Camera:
                    interactable.State = InteractableState.Active;
                    break;

                default:
                    interactable.State = InteractableState.Idle;
                    break;
            }
        }

        _ = node;
    }

    /// <summary>
    /// Adds an interactable of a type in a free slot, or returns null when the room is
    /// full.
    /// </summary>
    private static Interactable? Place(RoomContents room, IRng rng, InteractableType type)
    {
        var free = FreeSlots(room);
        if (free.Count == 0)
            return null;

        int slot = free[rng.NextInt(0, free.Count)];
        return room.Add(type, slot);
    }

    /// <summary>
    /// Slot indices in <c>[0, SlotCount)</c> that nothing occupies yet.
    /// </summary>
    private static List<int> FreeSlots(RoomContents room)
    {
        var taken = new HashSet<int>();
        foreach (Interactable interactable in room.Interactables)
            taken.Add(interactable.SlotIndex);

        var free = new List<int>(room.SlotCount);
        for (int slot = 0; slot < room.SlotCount; slot++)
        {
            if (!taken.Contains(slot))
                free.Add(slot);
        }

        return free;
    }

    /// <summary>
    /// Documented fallbacks used only when <c>room_template</c> is unavailable.
    /// </summary>
    /// <remarks>
    /// Mirrors the shipped table's shape. A missing table must still produce a valid
    /// room rather than an exception in the middle of a mission, which is why the
    /// fallbacks are permissive about population and only strict about slots.
    /// </remarks>
    internal static class InteriorFallbacks
    {
        internal const int SlotCount = 6;
        internal const int LockedContainerPercent = 20;
    }
}