using System.Collections.Generic;
using ProjectSpy.Core.Missions;
using UnityEngine;

// ProjectSpy.Core has its own `Resources` (the strategic resource pool), which collides
// with UnityEngine.Resources on any file that also does a Resources.Load. `using
// ProjectSpy.Core;` is therefore absent from this file so that bare `Resources` means
// Unity's, and the one Core type actually needed here is aliased below. Reintroducing
// the namespace using is a compile error, which is the point: it fails loudly instead of
// silently resolving to the strategic resource pool.
using CoreInteractableType = ProjectSpy.Core.InteractableType;

namespace ProjectSpy.Unity.Site
{
    /// <summary>
    /// Turns a Core <see cref="SiteLayout"/> into a continuous 3D blockout building.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the piece the whole stage turns on. Every generated site has to produce a
    /// building that a player can read: rooms laid along X at the exact intervals Core
    /// declared, floors stacked by a real storey height, connections standing where Core
    /// put them. It has to do that for <em>any</em> layout <c>SiteGenerator</c> emits,
    /// without a human fixing geometry, because sites are generated per mission and there
    /// is no art pipeline that could keep up with bespoke fixes.
    /// </para>
    /// <para>
    /// <b>It reads Core; it never computes Core.</b> Room positions come from
    /// <see cref="SiteRoom.StartX"/>/<see cref="SiteRoom.EndX"/> and connection positions
    /// from <see cref="SiteConnection.X"/> and <see cref="SiteConnection.UpperX"/>. There
    /// is no tolerance, no snapping and no "close enough" here: if a wall appears in the
    /// wrong place, the bug is upstream in Core and papering over it in Presentation would
    /// make the simulation and the render quietly disagree about where a door is.
    /// </para>
    /// <para>
    /// <b>Roomless.</b> It is plain C# that instantiates GameObjects and is therefore
    /// callable from a runtime component and from an editor tool alike, which is what lets
    /// the same code render a mission and drive the fifty-building preview.
    /// </para>
    /// </remarks>
    public static class SiteAssembler
    {
        /// <summary>
        /// Where the camera stands relative to a building: behind the front, looking at the
        /// back wall, offset along Z.
        /// </summary>
        /// <remarks>
        /// The camera is on the +Z side and the back wall is at negative Z, so the front of
        /// each room has no wall at all. That is what makes the cutaway read.
        /// </remarks>
        public const float CameraDistanceMetres = 34f;

        /// <summary>Height of the cutaway camera above the ground floor.</summary>
        public const float CameraHeightMetres = 6f;

        /// <summary>
        /// The result of an assembly: the root, plus the lookup the renderer and the
        /// interpolation system need to get from a Core id to a Transform.
        /// </summary>
        public sealed class Result
        {
            /// <summary>Root of the whole building. Destroy this to remove it.</summary>
            public GameObject Root { get; init; }

            /// <summary>Each room's centre transform, keyed by Core room id.</summary>
            public IReadOnlyDictionary<SiteRoomId, Transform> RoomTransforms { get; init; } =
                new Dictionary<SiteRoomId, Transform>();

            /// <summary>Each connection's transform, keyed by Core connection id.</summary>
            public IReadOnlyDictionary<SiteConnectionId, Transform> ConnectionTransforms { get; init; } =
                new Dictionary<SiteConnectionId, Transform>();

            /// <summary>Total building extent along X, in metres.</summary>
            public float WidthMetres { get; init; }

            /// <summary>Total building extent along Y, in metres.</summary>
            public float HeightMetres { get; init; }

            /// <summary>
            /// Rooms skipped because their interval was degenerate.
            /// </summary>
            /// <remarks>
            /// Surfaced rather than thrown, and normally empty. A generator bug that
            /// produced a zero-width room should leave a visible hole and a warning, not
            /// abort the assembly and leave the player staring at an empty scene with no
            /// explanation.
            /// </remarks>
            public IReadOnlyList<string> Warnings { get; init; } = new List<string>();

            /// <summary>
            /// A good cutaway camera for this building, looking at it side-on.
            /// </summary>
            /// <remarks>
            /// Framed from the total extent rather than a fixed distance, so a
            /// forty-room site and a four-room site are both legible without the caller
            /// passing a zoom.
            /// </remarks>
            public Camera CreateCamera()
            {
                var go = new GameObject("SiteCamera");
                var camera = go.AddComponent<Camera>();
                camera.orthographic = true;

                float span = Mathf.Max(WidthMetres, HeightMetres);
                camera.orthographicSize = Mathf.Max(4f, span * 0.62f);

                float cx = WidthMetres * 0.5f;
                float cy = HeightMetres * 0.5f;
                go.transform.position = new Vector3(cx, cy + CameraHeightMetres * 0.25f, CameraDistanceMetres);
                go.transform.rotation = Quaternion.Euler(6f, 0f, 0f);
                return camera;
            }
        }

        /// <summary>
        /// Builds the whole building under a new root GameObject.
        /// </summary>
        /// <param name="layout">The generated site.</param>
        /// <param name="origin">World offset of the building's floor-0, X=0 corner.</param>
        /// <param name="parent">Optional parent; the root is created as its child.</param>
        public static Result Assemble(SiteLayout layout, Vector3 origin = default, Transform parent = null)
        {
            if (layout is null)
                throw new System.ArgumentNullException(nameof(layout));

            var root = new GameObject($"Site_{layout.SiteTemplateId}_t{layout.Tier}_m{layout.MissionId}");
            root.transform.SetParent(parent, false);
            root.transform.position = origin;

            var warnings = new List<string>();
            var roomTransforms = new Dictionary<SiteRoomId, Transform>();
            var connectionTransforms = new Dictionary<SiteConnectionId, Transform>();

            // Walls between horizontally adjacent rooms are emitted once per boundary and
            // punched through wherever a door stands, rather than emitting a wall per room
            // and hoping the two agree. That is the difference between a building whose
            // walls line up and a building with a double-thick seam every few metres.
            var openings = CollectDoorOpenings(layout);

            foreach (SiteRegion region in layout.Regions)
            {
                var regionRoot = new GameObject(region.Kind.ToString());
                regionRoot.transform.SetParent(root.transform, false);

                foreach (SiteFloor floor in region.Floors)
                    BuildFloor(layout, floor, regionRoot.transform, origin, openings, roomTransforms, warnings);

                foreach (SiteConnection connection in region.Connections)
                    BuildConnection(layout, connection, regionRoot.transform, origin, connectionTransforms);
            }

            BuildContents(layout, root.transform, origin, roomTransforms, warnings);

            float width = 0f;
            foreach (SiteFloor floor in layout.Floors)
                width = Mathf.Max(width, LaneUnits.ToMetres(floor.Span.Raw));

            int floorCount = Mathf.Max(1, layout.Floors.Count);

            return new Result
            {
                Root = root,
                RoomTransforms = roomTransforms,
                ConnectionTransforms = connectionTransforms,
                WidthMetres = width,
                HeightMetres = floorCount * LaneUnits.StoreyHeightMetres,
                Warnings = warnings,
            };
        }

        /// <summary>
        /// One horizontal boundary that has an opening in it.
        /// </summary>
        private readonly struct Opening
        {
            public Opening(int floorIndex, int xcm)
            {
                FloorIndex = floorIndex;
                XCm = xcm;
            }

            public int FloorIndex { get; }
            public int XCm { get; }
        }

        /// <summary>
        /// Every horizontal connection, as a place a wall has a hole.
        /// </summary>
        /// <remarks>
        /// Grouped by floor so that building a wall can ask "is there an opening at this
        /// floor and this X" in constant time instead of rescanning the connection list
        /// once per wall segment. Fifty buildings in the preview window makes that the
        /// difference between instant and unusable.
        /// </remarks>
        private static Dictionary<int, List<int>> CollectDoorOpenings(SiteLayout layout)
        {
            var map = new Dictionary<int, List<int>>();
            foreach (SiteConnection c in layout.Connections)
            {
                if (c.IsVertical)
                    continue;

                if (!map.TryGetValue(c.FloorIndexA, out List<int> xs))
                {
                    xs = new List<int>();
                    map[c.FloorIndexA] = xs;
                }

                xs.Add(c.X.Raw);
            }

            return map;
        }

        private static void BuildFloor(
            SiteLayout layout,
            SiteFloor floor,
            Transform parent,
            Vector3 origin,
            Dictionary<int, List<int>> openings,
            Dictionary<SiteRoomId, Transform> roomTransforms,
            List<string> warnings)
        {
            var floorRoot = new GameObject($"Floor_{floor.Index}_{floor.Kind}");
            floorRoot.transform.SetParent(parent, false);

            float baseY = LaneUnits.ToWorldY(floor.Index, origin.y);
            float depth = LaneUnits.RoomDepthMetres;

            foreach (SiteRoom room in floor.Rooms)
            {
                int startX = room.StartX.Raw;
                int endX = room.EndX.Raw;

                if (!LaneUnits.IsRenderableInterval(startX, endX))
                {
                    warnings.Add($"Room {room.Id} on floor {floor.Index} has interval " +
                                 $"[{startX},{endX}); skipped.");
                    continue;
                }

                float width = LaneUnits.RoomWidth(startX, endX);
                float centreX = origin.x + LaneUnits.ToMetres((startX + endX) / 2);

                // The room's own root sits at the room's centre in world space, so that
                // RoomTransforms[roomId] is a usable anchor. Leaving every room root at the
                // origin would make the dictionary look fine and quietly give stage 9's
                // perception and marker code fifty coincident positions to attach to.
                var roomRoot = new GameObject($"Room_{room.Id}_{room.NameKey}");
                roomRoot.transform.SetParent(floorRoot.transform, true);
                roomRoot.transform.position = new Vector3(centreX, baseY, 0f);
                roomTransforms[room.Id] = roomRoot.transform;

                // Floor slab.
                Place(KitPiece.Floor, roomRoot.transform,
                    new Vector3(centreX, baseY - LaneUnits.FloorThicknessMetres * 0.5f, -depth * 0.5f),
                    new Vector3(width, LaneUnits.FloorThicknessMetres, depth));

                // Ceiling slab, so an upper storey is visually separated from this one and
                // an unobserved floor can be hidden without also hiding the one below.
                Place(KitPiece.Ceiling, roomRoot.transform,
                    new Vector3(centreX, baseY + LaneUnits.RoomHeightMetres + LaneUnits.FloorThicknessMetres * 0.5f, -depth * 0.5f),
                    new Vector3(width, LaneUnits.FloorThicknessMetres, depth));

                // Back wall, behind the play area. The front has no wall: that is the cutaway.
                Place(KitPiece.BackWall, roomRoot.transform,
                    new Vector3(centreX, baseY + LaneUnits.RoomHeightMetres * 0.5f, -depth),
                    new Vector3(width, LaneUnits.RoomHeightMetres, LaneUnits.WallThicknessMetres));

                // Side walls, only on the building's real ends of each floor.
                bool isLeftmost = IsLeftmostOn(floor, room);
                bool isRightmost = IsRightmostOn(floor, room);

                if (isLeftmost)
                {
                    Place(KitPiece.SideWall, roomRoot.transform,
                        new Vector3(origin.x + LaneUnits.ToMetres(startX), baseY + LaneUnits.RoomHeightMetres * 0.5f, -depth * 0.5f),
                        new Vector3(LaneUnits.ExteriorWallThicknessMetres, LaneUnits.RoomHeightMetres, depth));
                }

                if (isRightmost)
                {
                    Place(KitPiece.SideWall, roomRoot.transform,
                        new Vector3(origin.x + LaneUnits.ToMetres(endX), baseY + LaneUnits.RoomHeightMetres * 0.5f, -depth * 0.5f),
                        new Vector3(LaneUnits.ExteriorWallThicknessMetres, LaneUnits.RoomHeightMetres, depth));
                }

                // Interior partition to the right, split around any door standing on it.
                if (!isRightmost)
                {
                    BuildPartition(
                        roomRoot.transform, floor.Index, origin.x + LaneUnits.ToMetres(endX),
                        baseY, depth, openings);
                }
            }
        }

        private static bool IsLeftmostOn(SiteFloor floor, SiteRoom room)
            => floor.Rooms.Count > 0 && floor.Rooms[0].Id == room.Id;

        private static bool IsRightmostOn(SiteFloor floor, SiteRoom room)
            => floor.Rooms.Count > 0 && floor.Rooms[floor.Rooms.Count - 1].Id == room.Id;

        /// <summary>
        /// A wall segment at one X, cut around any opening standing on it.
        /// </summary>
        /// <remarks>
        /// Two segments plus a lintel over each door, rather than a single wall with a
        /// boolean "has a hole". A door that leaves a gap in the wall to the ceiling is the
        /// single most common way a blockout building stops reading as a building, and it
        /// is invisible in a top-down view.
        /// </remarks>
        private static void BuildPartition(
            Transform parent, int floorIndex, float wallX, float baseY, float depth,
            Dictionary<int, List<int>> openings)
        {
            openings.TryGetValue(floorIndex, out List<int> xsOnFloor);

            // Collect the openings that fall inside this wall's thickness, then merge any
            // that overlap into one, because two doors 20cm apart would otherwise emit a
            // negative-width segment between them.
            var holeHalfWidthMetres = LaneUnits.DoorWidthMetres * 0.5f;
            var holes = new List<(float left, float right)>();

            if (xsOnFloor != null)
            {
                foreach (int xcm in xsOnFloor)
                {
                    float centre = LaneUnits.ToMetres(xcm);
                    // Only walls within a doorway's width of this boundary are relevant.
                    if (Mathf.Abs(centre - wallX) > holeHalfWidthMetres + LaneUnits.WallThicknessMetres)
                        continue;
                    holes.Add((centre - holeHalfWidthMetres, centre + holeHalfWidthMetres));
                }
            }

            holes.Sort((a, b) => a.left.CompareTo(b.left));

            var merged = new List<(float left, float right)>();
            foreach (var hole in holes)
            {
                if (merged.Count > 0 && hole.left <= merged[merged.Count - 1].right)
                {
                    var last = merged[merged.Count - 1];
                    merged[merged.Count - 1] = (last.left, Mathf.Max(last.right, hole.right));
                }
                else
                {
                    merged.Add(hole);
                }
            }

            float wallHeight = LaneUnits.RoomHeightMetres;
            float wallThickness = LaneUnits.WallThicknessMetres;
            float floorMin = wallX - wallThickness * 0.5f;
            float floorMax = wallX + wallThickness * 0.5f;

            // Walk the wall left to right: emit solid where there is wall, a lintel where
            // there is a door. `cursor` is where the last solid segment ended.
            float cursor = floorMin;

            foreach (var hole in merged)
            {
                if (hole.left > cursor)
                    EmitWall(parent, (cursor + hole.left) * 0.5f, baseY, wallHeight, wallThickness, depth);

                float lintelBottom = LaneUnits.DoorHeightMetres;
                float lintelHeight = wallHeight - lintelBottom;
                if (lintelHeight > 0.01f)
                {
                    EmitWall(parent, (hole.left + hole.right) * 0.5f,
                        baseY + lintelBottom + lintelHeight * 0.5f,
                        lintelHeight, wallThickness, depth);
                }

                cursor = hole.right;
            }

            if (floorMax > cursor)
                EmitWall(parent, (cursor + floorMax) * 0.5f, baseY, wallHeight, wallThickness, depth);
        }

        private static void EmitWall(
            Transform parent, float centreX, float baseY, float height, float thickness, float depth)
        {
            Place(KitPiece.BackWall, parent,
                new Vector3(centreX, baseY + height * 0.5f, -depth * 0.5f),
                new Vector3(thickness, height, depth));
        }

        /// <summary>
        /// Stands a connection's geometry at the place Core put it.
        /// </summary>
        private static void BuildConnection(
            SiteLayout layout,
            SiteConnection connection,
            Transform parent,
            Vector3 origin,
            Dictionary<SiteConnectionId, Transform> connectionTransforms)
        {
            float depth = LaneUnits.RoomDepthMetres;

            if (!connection.IsVertical)
            {
                // Horizontal: a door frame standing in the partition at Core's X. The root
                // is anchored at the door's own world position and the frame is a child at
                // a local offset, so ConnectionTransforms[connId] is the door.
                float x = origin.x + LaneUnits.ToMetres(connection.X.Raw);
                float baseY = LaneUnits.ToWorldY(connection.FloorIndexA, origin.y);

                var go = new GameObject($"Conn_{connection.Id}_{connection.Kind}");
                go.transform.SetParent(parent, true);
                go.transform.position = new Vector3(x, baseY, -depth * 0.5f);
                connectionTransforms[connection.Id] = go.transform;

                var frame = PlaceLocal(KitPiece.DoorFrame, go.transform,
                    new Vector3(0f, LaneUnits.DoorHeightMetres * 0.5f, 0f),
                    new Vector3(LaneUnits.DoorWidthMetres, LaneUnits.DoorHeightMetres, depth * 0.4f));
                frame.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                return;
            }

            // Vertical: the run from the lower landing to the upper one, placed at the foot
            // position Core gave. The landing X is deliberately not used to position the
            // run — a stairwell's foot and its landing are not vertically aligned in a real
            // building, which is exactly why Core stores them as two numbers.
            float footX = origin.x + LaneUnits.ToMetres(connection.X.Raw);
            float lowerY = LaneUnits.ToWorldY(connection.FloorIndexA, origin.y);
            float rise = (connection.FloorIndexB - connection.FloorIndexA) * LaneUnits.StoreyHeightMetres;

            var root = new GameObject($"Conn_{connection.Id}_{connection.Kind}");
            root.transform.SetParent(parent, true);
            root.transform.position = new Vector3(footX, lowerY, 0f);
            connectionTransforms[connection.Id] = root.transform;

            switch (connection.Kind)
            {
                case SiteConnectionKind.Ladder:
                {
                    var ladder = PlaceLocal(KitPiece.Ladder, root.transform,
                        new Vector3(0f, rise * 0.5f, -depth * 0.35f),
                        new Vector3(0.6f, rise, 0.25f));
                    ladder.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    break;
                }

                case SiteConnectionKind.Vent:
                {
                    var vent = PlaceLocal(KitPiece.Vent, root.transform,
                        new Vector3(0f, LaneUnits.RoomHeightMetres * 0.75f, -depth * 0.9f),
                        new Vector3(0.7f, 0.7f, 0.3f));
                    vent.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    break;
                }

                default:
                {
                    // Door, LockedDoor, Window, Hole and Stair all render as a stair run: a
                    // vertical connection between storeys is a stair unless the table says
                    // otherwise, and the table already said "ladder" or "vent" above.
                    PlaceLocal(KitPiece.Stair, root.transform,
                        new Vector3(0f, rise * 0.5f, -depth * 0.5f),
                        new Vector3(1.2f, rise, 2.4f));
                    break;
                }
            }
        }

        /// <summary>
        /// Lays in guards, civilians, terminals, loot and lights.
        /// </summary>
        /// <remarks>
        /// Everything here is placed in its room's lane position, which for a non-patrol NPC
        /// is the middle of the room: Core commits <em>which room</em> they are in but not
        /// exactly where on the floor, and inventing an exact X here would be Presentation
        /// asserting a simulation fact it was never told.
        /// </remarks>
        private static void BuildContents(
            SiteLayout layout,
            Transform root,
            Vector3 origin,
            Dictionary<SiteRoomId, Transform> roomTransforms,
            List<string> warnings)
        {
            var contents = new GameObject("Contents");
            contents.transform.SetParent(root, false);

            foreach (SiteGuard guard in layout.Guards)
                PlaceMarker(contents.transform, MarkerKind.Guard, layout, guard.HomeRoomId,
                    null, origin, roomTransforms, warnings);

            foreach (SiteCivilian civilian in layout.Civilians)
                PlaceMarker(contents.transform, MarkerKind.Civilian, layout, civilian.RoomId,
                    null, origin, roomTransforms, warnings);

            foreach (SiteLight light in layout.Lights)
                PlaceMarker(contents.transform, MarkerKind.Light, layout, light.RoomId,
                    light.X.Raw, origin, roomTransforms, warnings);

            foreach (SiteInteractable interactable in layout.Interactables)
            {
                var kind = interactable.Kind == CoreInteractableType.Terminal
                    ? MarkerKind.Terminal
                    : MarkerKind.Loot;
                PlaceMarker(contents.transform, kind, layout, interactable.RoomId,
                    interactable.X.Raw, origin, roomTransforms, warnings);
            }
        }

        private static void PlaceMarker(
            Transform parent,
            MarkerKind kind,
            SiteLayout layout,
            SiteRoomId roomId,
            int? exactXcm,
            Vector3 origin,
            Dictionary<SiteRoomId, Transform> roomTransforms,
            List<string> warnings)
        {
            if (!roomTransforms.TryGetValue(roomId, out Transform roomTransform))
            {
                // A content item in a room the assembler skipped. Recorded, not thrown: the
                // degenerate room is the real problem and it already warned.
                return;
            }

            SiteRoom room = layout.Find(roomId);
            if (room is null)
                return;

            int xcm = exactXcm ?? (room.StartX.Raw + room.EndX.Raw) / 2;
            float x = origin.x + LaneUnits.ToMetres(xcm);
            float y = LaneUnits.ToWorldY(room.FloorIndex, origin.y);

            var prefab = Resources.Load<GameObject>(BlockoutKit.ResourcePathForMarker(kind));
            GameObject go;

            if (prefab is null)
            {
                go = new GameObject($"M_{kind}");
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.name = "Body";
                body.transform.SetParent(go.transform, false);
                body.transform.localScale = new Vector3(0.5f, MarkerStyle.HeightFor(kind), MarkerStyle.DepthFor(kind));
                body.transform.localPosition = new Vector3(0f, MarkerStyle.HeightFor(kind) * 0.5f, 0f);
                var collider = body.GetComponent<Collider>();
                if (collider != null)
                    Object.DestroyImmediate(collider);

                var renderer = body.GetComponent<Renderer>();
                if (renderer != null)
                    renderer.sharedMaterial = BlockoutMaterials.ForMarker(kind);
            }
            else
            {
                go = Object.Instantiate(prefab, parent);
                go.name = $"M_{kind}_{roomId}";
            }

            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(x, y, -LaneUnits.RoomDepthMetres * 0.5f);
        }

        /// <summary>
        /// Creates a kit piece, sized, at a world position.
        /// </summary>
        /// <remarks>
        /// A prefab is authored at unit scale and scaled per instance, because a room's
        /// width varies per layout while the kit must stay one asset. Only non-kit content
        /// — the agent capsule and markers — is authored at final size.
        /// </remarks>
        private static GameObject Place(
            KitPiece piece, Transform parent, Vector3 worldPosition, Vector3 sizeMetres)
        {
            var prefab = Resources.Load<GameObject>(BlockoutKit.ResourcePathFor(piece));

            if (prefab is null)
            {
                var fallback = BlockoutKit.BuildFallback(piece, parent, Vector3.one);
                fallback.transform.position = worldPosition;
                fallback.transform.localScale = sizeMetres;
                return fallback;
            }

            var instance = Object.Instantiate(prefab, parent);
            instance.name = $"SM_{piece}";
            instance.transform.position = worldPosition;
            instance.transform.localScale = sizeMetres;
            return instance;
        }

        /// <summary>
        /// Places a kit piece relative to its parent's origin.
        /// </summary>
        /// <remarks>
        /// Used by the pieces that hang off an anchored root — a connection's own root, or
        /// a room's. The distinction from <see cref="Place"/> is not cosmetic: assigning
        /// <c>localPosition</c> on a parent that itself was positioned in world space is
        /// how a door ends up at the building's origin instead of in its wall, which is
        /// invisible in a screenshot of one building and obvious across fifty.
        /// </remarks>
        private static GameObject PlaceLocal(
            KitPiece piece, Transform parent, Vector3 localPosition, Vector3 sizeMetres)
        {
            var prefab = Resources.Load<GameObject>(BlockoutKit.ResourcePathFor(piece));

            if (prefab is null)
            {
                var fallback = BlockoutKit.BuildFallback(piece, parent, Vector3.one);
                fallback.transform.localPosition = localPosition;
                fallback.transform.localScale = sizeMetres;
                return fallback;
            }

            var instance = Object.Instantiate(prefab, parent);
            instance.name = $"SM_{piece}";
            instance.transform.localPosition = localPosition;
            instance.transform.localScale = sizeMetres;
            return instance;
        }
    }
}