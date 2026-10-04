using System;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Site;
using UnityEngine;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// The orthographic camera the cutaway is read through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope.</b> This is the floor-framing camera the site rendering, lighting and fog
    /// work needs in order to exist, and no more. Dead-zone follow, alarm-dependent
    /// framing, manual pan and zoom clamped to site bounds, and the tactical overview are
    /// the camera half of stage 9a and are not built here — a rig that half-implemented
    /// them would look finished and be wrong in the places that matter.
    /// </para>
    /// <para>
    /// It is side-on and orthographic because that is the only projection in which a
    /// room's interval on Core's lane reads as the interval it is. A perspective camera
    /// makes a room at the far end of a forty-room site narrower than the one the squad is
    /// standing in, and the player would be reading that as a difference in the building.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class CutawayCameraRig : MonoBehaviour
    {
        private Camera _camera;
        private float _siteMinX;
        private float _siteMaxX;
        private float _siteHeight;

        /// <summary>Metres of building shown either side of the focus point.</summary>
        [SerializeField, Tooltip("Orthographic half-height in metres. Higher frames more floors.")]
        private float _orthographicSize = 7.5f;

        /// <summary>Degrees of downward tilt. Small enough to keep the lanes parallel.</summary>
        [SerializeField]
        private float _pitchDegrees = 6f;

        /// <summary>Where the building's lane starts in world X.</summary>
        public float SiteMinX => _siteMinX;

        /// <summary>Where the building's lane ends in world X.</summary>
        public float SiteMaxX => _siteMaxX;

        /// <summary>Total building height, in metres.</summary>
        public float SiteHeight => _siteHeight;

        /// <summary>The camera this rig drives.</summary>
        public Camera Camera
        {
            get
            {
                if (_camera == null)
                    _camera = GetComponent<Camera>();
                return _camera;
            }
        }

        /// <summary>The orthographic half-height currently in use.</summary>
        public float OrthographicSize => _orthographicSize;

        private void Awake()
        {
            Camera.orthographic = true;
            Camera.orthographicSize = _orthographicSize;

            // Yawed 180 as well as pitched. The building extends towards negative Z and
            // this rig stands at +CameraDistanceMetres, so an unyawed camera looks away
            // from the site and renders the empty space behind it — which reads as a site
            // that generated no geometry rather than as a camera pointed the wrong way.
            Camera.transform.rotation = Quaternion.Euler(_pitchDegrees, 180f, 0f);
        }

        /// <summary>Frames a site, remembering the bounds the view may be clamped to.</summary>
        public void FrameSite(SiteAssembler.Result assembly, Vector3 origin)
        {
            _siteMinX = origin.x;
            _siteMaxX = origin.x + Mathf.Max(assembly.WidthMetres, 1f);
            _siteHeight = Mathf.Max(assembly.HeightMetres, LaneUnits.StoreyHeightMetres);

            // Frame the whole building's height when it is small enough, so a single-storey
            // site does not arrive zoomed in past its own roof.
            float wholeSite = _siteHeight * 0.62f;
            _orthographicSize = Mathf.Clamp(
                Mathf.Max(_orthographicSize, wholeSite), 3f, 40f);

            Camera.orthographicSize = _orthographicSize;
        }

        /// <summary>
        /// Frames the storey a position is on, following it in X and in Y.
        /// </summary>
        /// <remarks>
        /// Y is snapped to the floor's mid-height rather than eased toward the actor's own Y,
        /// because the actor's Y is always exactly the floor's Y and an eased follow would
        /// spend the whole mission a few centimetres behind.
        /// </remarks>
        public void Follow(TacticalPosition position, Vector3 origin)
        {
            float x = origin.x + LaneUnits.ToMetres(position.X.Raw);
            float y = LaneUnits.ToWorldY(position.FloorIndex, origin.y) + LaneUnits.RoomHeightMetres * 0.5f;

            // Clamped to the site so the camera cannot be dragged off the end of the
            // building into a void with no building in it.
            x = Mathf.Clamp(x, _siteMinX, _siteMaxX);

            var wanted = new Vector3(x, y, CameraDistanceMetres(origin));

            // Snapped rather than eased. The actor's position is only ever the room's lane
            // position, so an eased follow would spend the mission trailing by exactly the
            // smoothing constant, and the cutaway's whole reading depends on the camera and
            // the actor agreeing about which floor is in frame.
            transform.position = wanted;
        }

        /// <summary>
        /// Frames the whole building, for a tactical overview.
        /// </summary>
        /// <remarks>
        /// Only the framing is provided. Whether the overview is available, what it costs
        /// to enter and what it shows about the fog belong to the camera half of stage 9a;
        /// this is the one operation on it that site rendering genuinely needs in order to
        /// be able to show a building that is taller than one band.
        /// </remarks>
        public void FrameWholeSite(Vector3 origin)
        {
            float height = Mathf.Max(_siteHeight * 0.62f, 3f);
            float width = Mathf.Max((_siteMaxX - _siteMinX) * 0.58f, 4f);

            _orthographicSize = Mathf.Max(height, width);
            Camera.orthographicSize = _orthographicSize;

            transform.position = new Vector3(
                (_siteMinX + _siteMaxX) * 0.5f,
                _siteHeight * 0.5f,
                CameraDistanceMetres(origin));
        }

        private float CameraDistanceMetres(Vector3 origin)
            => origin.z + SiteAssembler.CameraDistanceMetres;
    }
}