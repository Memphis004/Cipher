using System.Collections.Generic;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Audio;
using ProjectSpy.Unity.Localisation;
using ProjectSpy.Unity.Persistence;
using ProjectSpy.Unity.Simulation;
using ProjectSpy.Unity.Site;
using UnityEngine;
using UnityEngine.Rendering.Universal;

using AgentClass = ProjectSpy.Tables.AgentClass;
using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Builds and drives the tactical view: a mission, its building, its lights, its fog,
    /// and the HUD that reports all three.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It owns the view and nothing else.</b> The mission belongs to
    /// <see cref="GameSession"/> and is advanced by <see cref="SimulationRunner"/>; this
    /// creates one if the session has not entered tactical mode, renders it, and then does
    /// nothing but read.
    /// </para>
    /// <para>
    /// <b>The scene's cutaway is driven by observation, not by the camera.</b> Every frame
    /// the director asks Core which room the controlled agent is in and, if that room has
    /// not been observed yet, tells <c>MissionFog</c> so. Core decides what was learnt and
    /// whether the report was wrong about it; this only reports what the player can see.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class TacticalSceneDirector : MonoBehaviour
    {
        [Header("Mission")]
        [SerializeField, Tooltip("World seed for the demo mission.")]
        private ulong _seed = 20261005UL;

        [SerializeField, Tooltip("Site template id. 11001 is the warehouse district, 11013 the black site.")]
        private int _siteTemplateId = 11013;

        [SerializeField] private int _tier = 4;
        [SerializeField] private int _missionId = 1;

        [SerializeField, Tooltip("How much intel the sleeper operation has, 0-100.")]
        private int _intelPercent = 70;

        [SerializeField, Range(3, 5)] private int _squadSize = 4;

        /// <summary>
        /// agent_role row ids for the demo team, in dispatch order.
        /// </summary>
        /// <remarks>
        /// A team rather than one role for everyone, because <c>objective_rule</c> names the
        /// behaviours an objective needs — StealData requires a hacker, and a demo team with
        /// no hacker would be refused by Core for a reason that has nothing to do with what
        /// this scene is here to show. The ids are rows rather than literals in the code so
        /// that a retune of the demo team is a serialized edit; they resolve through
        /// <c>SquadRole</c>, which tolerates an id that no longer exists.
        /// </remarks>
        [SerializeField]
        private int[] _roleIds = { 12351, 12352, 12353, 12358 };

        [Header("Presentation")]
        [SerializeField] private UiLanguage _language = UiLanguage.English;
        [SerializeField] private Vector3 _siteOrigin = Vector3.zero;

        private GameSession _session;
        private SimulationRunner _runner;
        private LocalizationService _localization;
        private TacticalState _mission;
        private MissionFog _fog;
        private IntelSnapshot _snapshot;

        private SiteAssembler.Result _assembly;
        private SiteView _view;
        private LightingDirector _lights;
        private CutawayCameraRig _rig;
        private TacticalHud _hud;
        private AlarmDirector _alarm;
        private AudioService _audio;
        private AfterActionView _debrief;
        private SquadComposition _composition;
        private bool _debriefShown;

        private readonly Dictionary<TacticalActorId, AgentLightView> _actorViews = new();
        private readonly List<AgentLightView> _allViews = new();
        private readonly HashSet<SiteRoomId> _observedHere = new();

        private readonly LocalizationService _fallbackLocalization = new();

        /// <summary>The mission being presented, once it exists.</summary>
        public TacticalState Mission => _mission;

        /// <summary>The mission's fog.</summary>
        public MissionFog Fog => _fog;

        /// <summary>The assembled building.</summary>
        public SiteAssembler.Result Assembly => _assembly;

        /// <summary>The HUD, exposed so tests and tools can read what it is showing.</summary>
        public TacticalHud Hud => _hud;

        private void Start()
        {
            _runner = GetComponent<SimulationRunner>();
            if (_runner == null)
                _runner = FindFirstObjectByType<SimulationRunner>();

            if (_runner == null)
            {
                Debug.LogError("[ProjectSpy] TacticalSceneDirector needs a SimulationRunner to drive.");
                enabled = false;
                return;
            }

            _session = _runner.Session;
            EnsureMission();
            if (_mission is null)
            {
                enabled = false;
                return;
            }

            BuildView();
        }

        private void LateUpdate()
        {
            if (_mission is null)
                return;

            // The debrief is the first thing that happens when a mission ends, and it is
            // checked before the early-out below because that returns on IsOver and would
            // otherwise skip the debrief forever.
            if (_mission.IsOver)
            {
                ShowDebriefOnce();
                return;
            }

            ObserveControlledAgent();
            _lights.Sync();
            _alarm?.Sync(_mission.Alarm.Band);

            PlaceActors();
            foreach (AgentLightView view in _allViews)
                view.Sync();

            _rig.Follow(_mission.Control.ControlledActor(_mission)?.Position ?? default, _siteOrigin);
            _view.ApplyCamera(_rig.Camera, LaneUnits.StoreyHeightMetres);

            UpdateHud();
        }

        // ---------------------------------------------------------------- mission

        /// <summary>
        /// Makes sure the session is in a mission, creating one if it is not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mission is built through <c>SquadDeployment</c> — the same door the
        /// strategic screen's dispatch goes through — even though this scene invents its own
        /// composition to send. That is the point: the composition is the only thing that
        /// differs between the two callers, and everything Core does with it (validate,
        /// seed the objective, staff the post, set who the player controls) should be the
        /// same in both, or the standalone mission would be a second kind of mission.
        /// </para>
        /// <para>
        /// The squad is built with <c>WorldState.AddAgent</c> for the same reason
        /// <c>TacticalHarness</c> builds its fixtures that way: it is the only public door
        /// onto the roster, and inventing agents locally would produce actors Core has
        /// never heard of.
        /// </para>
        /// </remarks>
        private void EnsureMission()
        {
            EnsureTablesLoaded();

            if (_session.Mode == SessionMode.Tactical && _session.World.ActiveMission is { } existing)
            {
                _mission = existing;
                _snapshot = null;
                _fog = MissionFog.FromSnapshot(_mission.Layout, null);
                return;
            }

            if (_session.Mode == SessionMode.Tactical)
                _session.LeaveTacticalMode();

            List<Agent> squad = BuildSquad(_session.World, _squadSize);

            if (squad.Count == 0)
            {
                Debug.LogError("[ProjectSpy] Could not put a squad on the roster; no mission to present.");
                return;
            }

            ulong mapSeed = SiteGenerator.DeriveMapSeed(_seed, _missionId);
            SiteLayout layout = SiteGenerator.Generate(_siteTemplateId, _tier, _missionId, _seed, mapSeed);

            if (!layout.Validate(out string problem))
            {
                Debug.LogError($"[ProjectSpy] Generated site is not playable: {problem}");
                return;
            }

            SquadComposition composition = BuildComposition(squad);
            _composition = composition;

            if (!SquadDeployment.TryDispatch(
                    _session.World, composition, _missionId, layout, squad[0].Id, false,
                    out TacticalState? dispatched, out var refusals))
            {
                foreach (var refusal in refusals)
                    Debug.LogError($"[ProjectSpy] Dispatch refused: {refusal.Reason} {refusal.AgentId}");

                Debug.LogError("[ProjectSpy] Could not dispatch the demo mission; nothing to present.");
                return;
            }

            _mission = dispatched;
            _snapshot = BuildSnapshot(layout);
            _fog = MissionFog.FromSnapshot(layout, _snapshot);

            _session.EnterTacticalMode(_mission);
            _runner.SetTacticalSpeed(TacticalTimeScale.Normal);
        }

        /// <summary>
        /// The composition the standalone mission sends.
        /// </summary>
        /// <remarks>
        /// Roles cycle through the serialized list, and a member past the end of the list
        /// gets the last role rather than nothing: Core refuses a composition whose members
        /// have no role, and an empty role is not a state a demo team should be able to
        /// reach by asking for one more agent.
        /// </remarks>
        private SquadComposition BuildComposition(List<Agent> squad)
        {
            var composition = new SquadComposition
            {
                ObjectiveType = TableObjectiveType.StealData,
            };

            for (int i = 0; i < squad.Count; i++)
                composition.Add(squad[i].Id, RoleIdAt(i));

            return composition;
        }

        private int RoleIdAt(int index)
        {
            if (_roleIds == null || _roleIds.Length == 0)
                return 0;

            return _roleIds[Mathf.Min(index, _roleIds.Length - 1)];
        }

        /// <summary>
        /// Loads the compiled balance tables if nothing else has.
        /// </summary>
        /// <remarks>
        /// Normally <c>RootLifetimeScope</c> has already done this during boot. The
        /// tactical scene also has to work opened on its own — from the editor, from a
        /// screenshot, from a test — and there is no lifetime scope in that case, so the
        /// scene loads them itself rather than depending on an object that may not exist.
        /// </remarks>
        private static void EnsureTablesLoaded()
        {
            if (SimulationRules.AreTablesLoaded)
                return;

            try
            {
                new TableService().Load();
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    "[ProjectSpy] Could not load the compiled balance tables; the mission will " +
                    $"run on Core's fallback numbers. Run 'pwsh tools/sync-dlls.ps1'. ({ex.Message})");
            }
        }

        /// <summary>A squad of ordinary, mid-range agents on the session's roster.</summary>
        /// <remarks>
        /// The class id is read from the <c>agent_class</c> table rather than hard-coded,
        /// because Core has no opinion about which class an infiltrator is — that is what
        /// the table is for, and a literal here would be a balance number living in
        /// Presentation. Stamina and loyalty are initialised the way Core's own hire path
        /// does, so an agent that has never been hired does not start a mission exhausted.
        /// </remarks>
        private static List<Agent> BuildSquad(WorldState world, int count)
        {
            var squad = new List<Agent>(count);
            int classId = ResolveClassId();

            for (int i = 0; i < count; i++)
            {
                squad.Add(world.AddAgent(new Agent
                {
                    Name = $"Agent{i + 1}",
                    Codename = $"S{i + 1}",
                    ClassId = classId,
                    SalaryPerWeek = 200,
                    PhysicalStamina = Agent.MaxStamina,
                    MentalStamina = Agent.MaxStamina,
                    Loyalty = Agent.StartingLoyalty,
                    HiredOnTick = world.Clock.Current,
                    Skills = new SkillSet
                    {
                        Infiltration = 40,
                        Combat = 40,
                        Tech = 40,
                        Social = 40,
                        Nerve = 40,
                    },
                }));
            }

            return squad;
        }

        /// <summary>
        /// The <c>agent_class</c> id the demo squad is built with.
        /// </summary>
        /// <remarks>
        /// Resolved by name key through <c>SimulationRules</c> rather than through Core's
        /// <c>AgentClasses</c>, because those two do not read the same table instance.
        /// <c>AgentClasses</c> lazily loads through Core's own loader, which walks up from
        /// <c>AppContext.BaseDirectory</c> and therefore always fails inside Unity;
        /// <c>SimulationRules</c> is handed the tables explicitly at boot and is the one that
        /// actually has them. A literal id here would be a balance number in Presentation and
        /// would silently re-roster every agent if the table were renumbered.
        /// </remarks>
        private static int ResolveClassId()
        {
            AgentClass? row = SimulationRules.AgentClassFor(DemoClassNameKey);

            if (row != null)
                return row.Id;

            Debug.LogWarning(
                $"[ProjectSpy] No '{DemoClassNameKey}' row in agent_class; the demo squad has no class.");
            return 0;
        }

        /// <summary>The agent class a demo agent is hired into.</summary>
        private const string DemoClassNameKey = "class.infiltrator";

        /// <summary>
        /// The report the mission starts with.
        /// </summary>
        /// <remarks>
        /// Built from a synthetic sleeper operation at the serialized intel percentage
        /// rather than from a real one, because a real operation is the product of the
        /// strategic layer and this scene has to be playable without one. The snapshot it
        /// produces is a genuine <see cref="IntelSnapshot"/> from Core's own builder, so
        /// everything downstream — the fog, the contradictions, the room styles — is the
        /// real thing rather than a fixture shaped like it.
        /// </remarks>
        private IntelSnapshot BuildSnapshot(SiteLayout layout)
        {
            var operation = new SleeperOperation
            {
                AgentId = _mission.Squad.Count > 0 ? _mission.Squad[0].AgentId : default,
                SiteId = layout.SiteTemplateId,
                Status = SleeperStatus.Inserting,
                IntelPercent = Mathf.Clamp(_intelPercent, 0, 100),
            };

            return IntelSnapshotBuilder.Build(layout, operation, _session.World.Clock.Current);
        }

        // ------------------------------------------------------------------ view

        private void BuildView()
        {
            _localization = _fallbackLocalization;
            TacticalStrings.Register(_localization);
            _localization.SetLanguage(_language);

            var siteRoot = new GameObject("Site");
            siteRoot.transform.SetParent(transform, false);

            _assembly = SiteAssembler.Assemble(_mission.Layout, _siteOrigin, siteRoot.transform);
            foreach (string warning in _assembly.Warnings)
                Debug.LogWarning($"[ProjectSpy] Site assembly: {warning}");

            var cameraGo = new GameObject("CutawayCamera") { tag = "MainCamera" };
            cameraGo.transform.SetParent(transform, false);
            cameraGo.AddComponent<UniversalAdditionalCameraData>();
            _rig = cameraGo.AddComponent<CutawayCameraRig>();
            _rig.FrameSite(_assembly, _siteOrigin);

            // Solid colour rather than the skybox. A 2.5D cutaway has no sky, and the
            // default skybox behind a near-black unknown room reads as haze rather than
            // as "there is nothing known here".
            _rig.Camera.clearFlags = CameraClearFlags.SolidColor;
            _rig.Camera.backgroundColor = new Color(0.020f, 0.025f, 0.035f);

            _view = siteRoot.AddComponent<SiteView>();
            _view.Bind(_assembly, _mission.Layout, _fog);

            _lights = siteRoot.AddComponent<LightingDirector>();
            _lights.Build(_mission.Lights, _mission.Layout, _siteOrigin);

            var actorsRoot = new GameObject("Actors");
            actorsRoot.transform.SetParent(siteRoot.transform, false);
            BuildActors(actorsRoot.transform);

            var hudGo = new GameObject("Hud");
            hudGo.transform.SetParent(transform, false);
            _hud = hudGo.AddComponent<TacticalHud>();
            _hud.Build(_localization.Get);
            _hud.SetLanguage(_language);

            BuildAlarm();

            var debriefGo = new GameObject("AfterAction");
            debriefGo.transform.SetParent(transform, false);

            _debrief = debriefGo.AddComponent<AfterActionView>();
            ReportStrings.Register(_localization);
            _debrief.Build(_localization);

            // One first pass of the cutaway, so the very first rendered frame is already
            // correct rather than showing a building with every ceiling on.
            _view.ApplyCamera(_rig.Camera, LaneUnits.StoreyHeightMetres);
        }

        /// <summary>
        /// Builds the audio service and the alarm presentation on top of it.
        /// </summary>
        /// <remarks>
        /// The scene makes its own <c>AudioService</c> rather than resolving one from the
        /// root scope, for the same reason it builds its own localization fallback: this
        /// scene runs standalone, and taking a dependency on a container that may not have
        /// booted would make the tactical view the one part of the game that cannot be
        /// opened on its own. The catalogue is loaded up front so that no <c>Resources</c>
        /// read ever happens in the frame a guard first spots the player.
        /// </remarks>
        /// <summary>
        /// Shows the debrief, once, when the mission has ended.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Guarded rather than done inline in <c>LateUpdate</c> because IsOver stays true
        /// for every frame after the mission ends. Without the latch the report would be
        /// rebuilt — and the whole floor diagram re-instantiated — sixty times a second
        /// until the player closed the screen.
        /// </para>
        /// <para>
        /// The report is Core's, built by <c>MissionReportBuilder</c> from state the
        /// simulation already wrote. Nothing here is calculated for the debrief.
        /// </para>
        /// </remarks>
        private void ShowDebriefOnce()
        {
            if (_debriefShown || _debrief is null || _mission is null || _composition is null)
                return;

            _debriefShown = true;

            try
            {
                var report = ProjectSpy.Core.Squad.MissionReportBuilder.Build(
                    _mission,
                    _composition,
                    _mission.ObjectiveOutcome,
                    _mission.CommandPost);

                _debrief.Show(report, _mission.Layout);
            }
            catch (System.Exception problem)
            {
                // A debrief that throws would leave the player staring at the last frame of
                // a finished mission with no explanation. Losing the debrief is bad;
                // crashing the tactical scene over it is worse.
                Debug.LogError($"[ProjectSpy] Could not build the after-action report: {problem}");
            }
        }

        private void BuildAlarm()
        {
            _audio = new AudioService();
            AudioCueLibrary.LoadOrReport(_audio);

            var alarmGo = new GameObject("Alarm");
            alarmGo.transform.SetParent(transform, false);

            _alarm = alarmGo.AddComponent<AlarmDirector>();
            _alarm.Build(_localization, _lights, _audio);
        }

        /// <summary>
        /// Stands a capsule for every actor in the mission, squad and site alike.
        /// </summary>
        /// <remarks>
        /// Placement uses Core's own position, converted through <c>LaneUnits</c>. There is
        /// no interpolation here and none is needed yet: nothing moves until the control
        /// loop lands, and adding a second position source now would only give stage 9a's
        /// movement code something to disagree with.
        /// </remarks>
        private void BuildActors(Transform parent)
        {
            foreach (TacticalActor actor in _mission.SortedActors)
            {
                var go = new GameObject($"Actor_{actor.Id}");
                go.transform.SetParent(parent, false);

                var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                capsule.name = "Body";
                capsule.transform.SetParent(go.transform, false);
                capsule.transform.localScale = new Vector3(
                    LaneUnits.AgentRadiusMetres * 2f,
                    LaneUnits.AgentHeightMetres * 0.5f,
                    LaneUnits.AgentRadiusMetres * 2f);
                capsule.transform.localPosition = new Vector3(
                    0f, LaneUnits.AgentHeightMetres * 0.5f, 0f);

                var collider = capsule.GetComponent<Collider>();
                if (collider != null)
                    TacticalObject.Destroy(collider);

                var lightView = go.AddComponent<AgentLightView>();
                lightView.Bind(actor, _lights);

                _actorViews[actor.Id] = lightView;
                _allViews.Add(lightView);
            }
        }

        // ------------------------------------------------------------ observation

        /// <summary>
        /// Puts every actor's capsule where Core says it is.
        /// </summary>
        /// <remarks>
        /// Straight from Core's position with no interpolation. Nothing moves until the
        /// control loop lands, and when it does <c>SimulationRunner.RenderAlpha</c> becomes
        /// the second position source — adding a smoothed copy here now would give that
        /// code something to disagree with on the day it is written.
        /// </remarks>
        private void PlaceActors()
        {
            foreach (TacticalActor actor in _mission.SortedActors)
            {
                if (!_actorViews.TryGetValue(actor.Id, out AgentLightView view))
                    continue;

                view.transform.position = new Vector3(
                    _siteOrigin.x + LaneUnits.ToMetres(actor.Position.X.Raw),
                    LaneUnits.ToWorldY(actor.Position.FloorIndex, _siteOrigin.y),
                    -LaneUnits.RoomDepthMetres * 0.28f);
            }
        }

        /// <summary>
        /// Tells Core's fog about whatever the controlled agent can now see.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Core does the deciding: <c>ObserveRoom</c> promotes the room, compares the report
        /// against the room, and returns whatever contradicted. This does not compare
        /// anything — a Presentation-side comparison would be a second copy of the rule
        /// that decides when a poisoned report is caught, and the two would disagree about
        /// exactly the cases that matter.
        /// </para>
        /// <para>
        /// Rooms already handled here are remembered so that a squad standing still does not
        /// re-observe its own doorway every frame. Core's own guard against double-observation
        /// is the fog's monotone promotion, but calling it every frame would still walk the
        /// snapshot's entries every frame, and this is the frame the player is reading.
        /// </para>
        /// </remarks>
        private void ObserveControlledAgent()
        {
            TacticalActor? actor = _mission.Control.ControlledActor(_mission);
            if (actor is null)
                return;

            SiteRoom? room = _mission.RoomOf(actor);
            if (room is null)
                return;

            if (!_observedHere.Add(room.Id))
                return;

            IReadOnlyList<IntelContradicted> contradictions =
                _fog.ObserveRoom(room.Id, _mission.Layout, _snapshot, _mission.StartedOnTick);

            _view.RefreshFog();

            foreach (IntelContradicted contradiction in contradictions)
            {
                Debug.LogWarning(
                    $"[ProjectSpy] Intel contradicted: {contradiction.FactKind} " +
                    $"{contradiction.FactId} was claimed at {contradiction.ClaimedConfidence} confidence.");

                _hud.PlayContradiction(contradiction);
            }
        }

        // ------------------------------------------------------------------- HUD

        private void UpdateHud()
        {
            if (_hud is null)
                return;

            TacticalActor? actor = _mission.Control.ControlledActor(_mission);
            if (actor is null)
                return;

            _hud.SetLightState(_lights.LevelAt(actor.Position));

            SiteRoom? room = _mission.RoomOf(actor);
            _hud.SetRoom(room, room is null
                ? FogLook.UnknownVoid
                : FogLooks.LookFor(_fog.RoomState(room.Id)));
        }
    }
}