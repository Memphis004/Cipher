using System.Collections.Generic;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Site;
using UnityEngine;

using CoreFogState = ProjectSpy.Core.FogState;
using MissionFog = ProjectSpy.Core.MissionFog;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Draws an assembled site the way the cutaway is supposed to look: only the floors
    /// the camera covers, ceilings faded where they would hide a floor, and every room
    /// drawn according to what the player actually knows about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It reads Core and never computes Core.</b> Floor indices come from
    /// <see cref="SiteFloor.Index"/>, fog states from <c>MissionFog.RoomState</c>, and the
    /// geometry from <c>SiteAssembler</c>. Nothing here works out where a wall is or what a
    /// room contains.
    /// </para>
    /// <para>
    /// <b>Per-frame work is proportional to what changed, not to the building.</b> Every
    /// floor and every room remembers what it was last drawn as, and a frame that changes
    /// nothing touches nothing. A five-floor site and a fifty-room site both cost one band
    /// computation per frame; only a camera move pays.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SiteView : MonoBehaviour
    {
        private SiteAssembler.Result _assembly;
        private SiteLayout _layout;
        private MissionFog _fog;

        private readonly Dictionary<int, CutawayRole> _floorRole = new();
        private readonly Dictionary<SiteRoomId, FogLook> _roomLook = new();

        /// <summary>The assembly being drawn. Null until <see cref="Bind"/> runs.</summary>
        public SiteAssembler.Result Assembly => _assembly;

        /// <summary>The site being drawn.</summary>
        public SiteLayout Layout => _layout;

        /// <summary>The fog the drawing is following.</summary>
        public MissionFog Fog => _fog;

        /// <summary>
        /// Binds a freshly assembled building.
        /// </summary>
        /// <param name="assembly">What <c>SiteAssembler</c> produced.</param>
        /// <param name="layout">The Core layout it was built from.</param>
        /// <param name="fog">The mission's fog, or null for an all-unknown site.</param>
        public void Bind(SiteAssembler.Result assembly, SiteLayout layout, MissionFog fog)
        {
            _assembly = assembly;
            _layout = layout;
            _fog = fog;

            _floorRole.Clear();
            _roomLook.Clear();

            RefreshFog();
        }

        /// <summary>
        /// Re-reads every room's fog state and redraws the rooms whose state changed.
        /// </summary>
        /// <remarks>
        /// Called on bind and whenever <c>MissionFog</c> promotes something. Cheap enough to
        /// call on a promotion event — a promotion happens a handful of times per mission —
        /// and doing it wholesale means there is no way for one room's rendering to be left
        /// behind after its state moved.
        /// </remarks>
        public void RefreshFog()
        {
            if (_assembly is null || _layout is null)
                return;

            foreach (KeyValuePair<SiteRoomId, Transform> entry in _assembly.RoomTransforms)
            {
                CoreFogState state = _fog?.RoomState(entry.Key) ?? CoreFogState.Unknown;
                FogLook look = FogLooks.LookFor(state);

                if (_roomLook.TryGetValue(entry.Key, out FogLook previous) && previous == look)
                    continue;

                _roomLook[entry.Key] = look;
                ApplyRoomLook(entry.Key, look);
            }
        }

        /// <summary>
        /// Updates floor culling and ceiling fading for this frame's camera.
        /// </summary>
        /// <remarks>
        /// Called from <c>LateUpdate</c> by the camera rig rather than from here, because
        /// whether the camera has finished moving is not something this component can know.
        /// </remarks>
        public void ApplyCamera(Camera camera, float storeyHeightMetres)
        {
            if (_assembly is null || camera == null)
                return;

            float halfHeight = camera.orthographic
                ? camera.orthographicSize
                : camera.fieldOfView * 0.5f * camera.transform.position.z;

            int floorCount = Mathf.Max(1, _assembly.FloorRoots.Count);
            var band = FloorBand.ForOrthographic(
                camera.transform.position.y, halfHeight, storeyHeightMetres, floorCount);

            ApplyBand(band, camera.transform.position.y, storeyHeightMetres);
        }

        /// <summary>
        /// Applies a band directly, for an overview camera that is not a real camera.
        /// </summary>
        /// <remarks>
        /// The tactical overview frames a whole building that may not fit on screen, so it
        /// cannot be described by what a camera happens to cover. It states its band.
        /// </remarks>
        public void ApplyBand(FloorBand band, float cameraY, float storeyHeightMetres)
        {
            foreach (KeyValuePair<int, Transform> entry in _assembly.FloorRoots)
            {
                int floorIndex = entry.Key;
                CutawayRole role = CutawayVisibility.FloorRole(floorIndex, band);

                if (_floorRole.TryGetValue(floorIndex, out CutawayRole previous) && previous == role)
                    continue;

                _floorRole[floorIndex] = role;
                entry.Value.gameObject.SetActive(role != CutawayRole.Hidden);
            }

            foreach (KeyValuePair<SiteRoomId, Transform> entry in _assembly.RoomTransforms)
            {
                SiteRoom room = _layout.Find(entry.Key);
                if (room is null)
                    continue;

                int floorIndex = room.FloorIndex;
                if (_floorRole.TryGetValue(floorIndex, out CutawayRole floorRole) &&
                    floorRole == CutawayRole.Hidden)
                    continue;

                ApplyCeiling(entry.Key, CutawayVisibility.CeilingRole(
                    floorIndex, band, cameraY, storeyHeightMetres));
            }
        }

        /// <summary>Applies a room's fog look to its shell and to whether its contents exist.</summary>
        private void ApplyRoomLook(SiteRoomId roomId, FogLook look)
        {
            if (!_assembly.RoomParts.TryGetValue(roomId, out SiteAssembler.Result.RoomRenderers parts))
                return;

            Material shellMaterial = FogLooks.MaterialFor(look);

            foreach (Renderer renderer in parts.Structure)
            {
                if (renderer != null)
                    renderer.sharedMaterial = shellMaterial;
            }

            bool showContents = FogLooks.ShowsContents(look);

            foreach (Renderer renderer in parts.Contents)
            {
                if (renderer == null)
                    continue;

                // Switching the renderer rather than the object, because a marker object
                // may in future hold more than its mesh and only the mesh is a fact about
                // the room's contents.
                renderer.enabled = showContents;
            }
        }

        private void ApplyCeiling(SiteRoomId roomId, CutawayRole role)
        {
            if (!_assembly.RoomParts.TryGetValue(roomId, out SiteAssembler.Result.RoomRenderers parts))
                return;

            foreach (Renderer renderer in parts.Ceilings)
            {
                if (renderer == null)
                    continue;

                renderer.sharedMaterial = role == CutawayRole.Faded
                    ? FogLooks.Ghost
                    : FogLooks.MaterialFor(FogLook.Observed);
            }
        }
    }
}