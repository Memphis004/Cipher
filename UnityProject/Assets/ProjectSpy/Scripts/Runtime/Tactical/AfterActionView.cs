using System;
using System.Collections.Generic;
using System.Text;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Localisation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// The debrief: what happened, drawn once the mission is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It draws Core's report and nothing else.</b> Every number on this screen was
    /// decided by the simulation. The view's only judgement is layout — which is the whole
    /// reason a debrief can be trusted to disagree with the player's memory of a mission
    /// that went badly.
    /// </para>
    /// <para>
    /// <b>The floor diagram is honest about what it knows.</b> It draws room-to-room lines
    /// between room centres, because that is the precision <see cref="RouteStep"/> records.
    /// A smooth curve through the exact path an operative walked would look better and
    /// would be a drawing of something the simulation never stored.
    /// </para>
    /// <para>
    /// <b>Copyable by design.</b> Players paste these into chat and bug reporters paste
    /// them into issues, so the export button writes the same text the screen shows rather
    /// than a summary of it.
    /// </para>
    /// </remarks>
    public sealed class AfterActionView : MonoBehaviour
    {
        private const float DiagramWidth = 760f;
        private const float DiagramHeight = 470f;
        private const int MaxTimelineRows = 26;

        private LocalizationService _localization;
        private Canvas _canvas;
        private Text _header;
        private Text _squad;
        private Text _loot;
        private Text _summary;
        private Text _timeline;
        private Text _copyLabel;
        private RectTransform _diagram;

        private MissionReport _report;
        private SiteLayout _layout;

        /// <summary>True once a report has been shown.</summary>
        public bool IsVisible => _canvas != null && _canvas.gameObject.activeSelf;

        /// <summary>The text the copy button puts on the clipboard.</summary>
        public string ClipboardText { get; private set; } = string.Empty;

        /// <summary>
        /// Builds the screen. Hidden until <see cref="Show"/> is called.
        /// </summary>
        public void Build(LocalizationService localization)
        {
            _localization = localization;

            var go = new GameObject("AfterActionCanvas", typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);

            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            BuildBackdrop();
            BuildLeftColumn();
            BuildDiagram();
            BuildTimelineColumn();
            BuildCopyButton();

            _canvas.gameObject.SetActive(false);
        }

        private void BuildBackdrop()
        {
            var backdrop = new GameObject("Backdrop", typeof(Image));
            backdrop.transform.SetParent(_canvas.transform, false);

            var image = backdrop.GetComponent<Image>();
            image.color = new Color(0.02f, 0.025f, 0.035f, 0.94f);

            Stretch(image.rectTransform);
        }

        private void BuildLeftColumn()
        {
            Font font = TacticalStrings.LoadFont();

            _header = Column("Header", font, 26, new Vector2(60f, -50f), new Vector2(840f, 250f));
            _squad = Column("Squad", font, 19, new Vector2(60f, -320f), new Vector2(840f, 220f));
            _loot = Column("Loot", font, 19, new Vector2(60f, -560f), new Vector2(840f, 160f));
            _summary = Column("Summary", font, 19, new Vector2(60f, -740f), new Vector2(840f, 240f));
        }

        private void BuildDiagram()
        {
            var panel = new GameObject("Diagram", typeof(Image));
            panel.transform.SetParent(_canvas.transform, false);

            var image = panel.GetComponent<Image>();
            image.color = new Color(0.05f, 0.06f, 0.08f, 0.85f);
            image.raycastTarget = false;

            _diagram = image.rectTransform;
            _diagram.anchorMin = new Vector2(0f, 1f);
            _diagram.anchorMax = new Vector2(0f, 1f);
            _diagram.pivot = new Vector2(0f, 1f);
            _diagram.anchoredPosition = new Vector2(960f, -50f);
            _diagram.sizeDelta = new Vector2(DiagramWidth, DiagramHeight);
        }

        private void BuildTimelineColumn()
        {
            Font font = TacticalStrings.LoadFont();
            _timeline = Column("Timeline", font, 17, new Vector2(960f, -560f), new Vector2(880f, 460f));
        }

        private void BuildCopyButton()
        {
            Font font = TacticalStrings.LoadFont();

            var go = new GameObject("CopyButton", typeof(Image), typeof(Button));
            go.transform.SetParent(_canvas.transform, false);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.16f, 0.34f, 0.32f, 0.95f);

            var rect = image.rectTransform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-60f, 46f);
            rect.sizeDelta = new Vector2(300f, 54f);

            _copyLabel = Label("Label", font, 20, TextAnchor.MiddleCenter, go.transform);
            Stretch(_copyLabel.rectTransform);
            _copyLabel.text = Resolve(ReportStrings.Copy);

            var button = go.GetComponent<Button>();
            button.onClick.AddListener(CopyToClipboard);
        }

        private Text Column(string name, Font font, int size, Vector2 position, Vector2 extent)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(_canvas.transform, false);

            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = TextAnchor.UpperLeft;
            text.color = new Color(0.86f, 0.88f, 0.92f, 1f);
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = extent;

            return text;
        }

        private static Text Label(string name, Font font, int size, TextAnchor anchor, Transform parent)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.raycastTarget = false;

            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Shows a finished mission's debrief.
        /// </summary>
        /// <param name="report">Core's report.</param>
        /// <param name="layout">The site, for the floor diagram's room geometry.</param>
        public void Show(MissionReport report, SiteLayout layout)
        {
            if (_canvas == null || report is null)
                return;

            _report = report;
            _layout = layout;

            _header.text = HeaderText(report);
            _squad.text = SquadText(report);
            _loot.text = LootText(report);
            _summary.text = SummaryText(report);
            _timeline.text = TimelineText(report);

            ClearDiagram();
            DrawDiagram(report, layout);

            // Taken as a local because a null-conditional cannot be applied to a method group, and
// the formatter is happy with a null resolver — it falls back to keys.
Func<string, string> resolve = _localization is null ? null : _localization.Get;
            ClipboardText = AfterActionReport.Format(report, resolve, RoomName);

            _canvas.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_canvas != null)
                _canvas.gameObject.SetActive(false);
        }

        /// <summary>
        /// Puts the export on the clipboard and says so.
        /// </summary>
        /// <remarks>
        /// The confirmation is deliberate: a copy button that gives no feedback is
        /// indistinguishable from one that is broken, and players will press it again and
        /// then again.
        /// </remarks>
        public void CopyToClipboard()
        {
            if (string.IsNullOrEmpty(ClipboardText))
                return;

            GUIUtility.systemCopyBuffer = ClipboardText;
            _copyLabel.text = Resolve(ReportStrings.Copied);
        }

        private string Resolve(string key)
            => _localization is null ? key : _localization.Get(key);

        /// <summary>
        /// Names a room as "name (id)".
        /// </summary>
        /// <remarks>
        /// The id is not decoration. <c>NameKey</c> comes from the room's <em>type</em>, so
        /// a site with two detention rooms gives both the same name, and a route printed
        /// as "detention > detention > security" looks like the operative walked into one
        /// room twice — which is exactly the kind of thing a player pastes into a bug
        /// report. The id makes the route checkable against the diagram beside it.
        /// </remarks>
        private string RoomName(SiteRoomId id)
        {
            if (_layout is null)
                return id.ToString();

            foreach (SiteRoom room in _layout.Rooms)
            {
                if (room.Id == id)
                    return $"{room.NameKey} ({room.Id})";
            }

            return id.ToString();
        }

        // ------------------------------------------------------------------ text

        private string HeaderText(MissionReport report) =>
            $"MISSION {report.MissionId} - {report.ObjectiveType}\n" +
            $"site {report.SiteId}   {report.Steps} steps\n" +
            $"class {report.Class}   peak alarm {report.PeakBand}   ended {report.EndBand}\n" +
            $"objective {(report.ObjectiveComplete ? "COMPLETE" : "INCOMPLETE")}" +
            (report.CommandPostCompromised ? "   post compromised" : string.Empty) +
            (report.Aborted ? "   aborted" : string.Empty);

        private string SquadText(MissionReport report)
        {
            var text = new StringBuilder("SQUAD\n");
            text.AppendLine(new string('-', 60));

            if (report.Agents.Count == 0)
                return text.Append("(none recorded)").ToString();

            foreach (AgentReport agent in report.Agents)
            {
                text.Append(agent.AgentId).Append("  ").Append(agent.Condition)
                    .Append("  hp ").Append(agent.Health)
                    .Append("  +").Append(agent.InjuryAdded).Append(" injury")
                    .Append("  +").Append(agent.MentalDamage).Append(" mental")
                    .Append("  +").Append(agent.Exp).Append(" exp")
                    .AppendLine();
            }

            return text.ToString();
        }

        private string LootText(MissionReport report)
        {
            var text = new StringBuilder("LOOT\n");
            text.AppendLine(new string('-', 60));

            if (report.Loot.Count == 0)
                return text.Append("(nothing carried out)").ToString();

            foreach ((int table, int item, int count) in report.Loot)
                text.Append("table ").Append(table).Append("  item ").Append(item)
                    .Append("  x").AppendLine(count.ToString());

            return text.ToString();
        }

        private string SummaryText(MissionReport report)
        {
            var text = new StringBuilder("SUMMARY\n");
            text.AppendLine(new string('-', 60));

            if (report.Summary.Count == 0)
                return text.Append("(none)").ToString();

            foreach ((string key, IReadOnlyList<int> args) in report.Summary)
                text.Append("- ").AppendLine(Resolve(key));

            string note = AfterActionReport.RollsNote(report);
            if (!string.IsNullOrEmpty(note))
                text.AppendLine().Append(note);

            return text.ToString();
        }

        private string TimelineText(MissionReport report)
        {
            var text = new StringBuilder("TIMELINE\n");
            text.AppendLine(new string('-', 60));

            if (report.Perceptions.Count > 0)
            {
                text.AppendLine("perceptions:");
                foreach (PerceptionRecord record in report.Perceptions)
                    text.Append("  step ").Append(record.Step).Append("  actor ")
                        .Append(record.ObserverId).Append(" saw ").Append(record.SubjectId)
                        .Append("  ").AppendLine(record.Level.ToString());
                text.AppendLine();
            }

            if (report.Noises.Count > 0)
            {
                text.AppendLine("noise:");
                foreach (NoiseLogEntry entry in report.Noises)
                    text.Append("  step ").Append(entry.Step).Append("  actor ")
                        .Append(entry.SourceActorId).Append("  ").Append(entry.SourceKey)
                        .Append("  heard by ").AppendLine(entry.Heard.Count.ToString());
                text.AppendLine();
            }

            text.AppendLine("events:");
            if (report.Timeline.Count == 0)
            {
                text.AppendLine("  (none)");
            }
            else
            {
                foreach (MissionLogEntry entry in report.Timeline)
                    text.Append("  step ").Append(entry.Step).Append("  ")
                        .AppendLine(Resolve(entry.Key));
            }

            return text.ToString();
        }

        // ------------------------------------------------------------------ diagram

        private void ClearDiagram()
        {
            for (int i = _diagram.childCount - 1; i >= 0; i--)
                TacticalObject.Destroy(_diagram.GetChild(i).gameObject);
        }

        /// <summary>
        /// Draws the rooms and the route each operative took through them.
        /// </summary>
        /// <remarks>
        /// Rooms are drawn from the layout's own centimetres, scaled to fit the panel, so
        /// the diagram is the building rather than a schematic of it. Route lines go
        /// between room centres in visit order: that is exactly the information
        /// <see cref="RouteStep"/> carries, and connecting the dots is the most the
        /// debrief can honestly claim about where somebody went.
        /// </remarks>
        private void DrawDiagram(MissionReport report, SiteLayout layout)
        {
            if (layout is null)
                return;

            IReadOnlyList<SiteRoom> rooms = layout.Rooms;
            if (rooms.Count == 0)
                return;

            int minFloor = int.MaxValue;
            int maxFloor = int.MinValue;
            int minX = int.MaxValue;
            int maxX = int.MinValue;

            foreach (SiteRoom room in rooms)
            {
                minFloor = Mathf.Min(minFloor, room.FloorIndex);
                maxFloor = Mathf.Max(maxFloor, room.FloorIndex);
                minX = Mathf.Min(minX, room.StartX.Raw);
                maxX = Mathf.Max(maxX, room.EndX.Raw);
            }

            int floors = Mathf.Max(1, maxFloor - minFloor + 1);
            int spanX = Mathf.Max(1, maxX - minX);
            float scale = Mathf.Min(DiagramWidth / spanX, DiagramHeight / (floors * 400f));

            var centre = new Dictionary<SiteRoomId, Vector2>();

            foreach (SiteRoom room in rooms)
            {
                float w = (room.EndX.Raw - room.StartX.Raw) * scale;
                float h = 400f * scale;
                float x = (room.StartX.Raw - minX) * scale;
                float y = -(room.FloorIndex - minFloor) * h - h;

                var cell = new GameObject($"Room_{room.Id}", typeof(Image));
                cell.transform.SetParent(_diagram, false);

                var image = cell.GetComponent<Image>();
                image.color = new Color(0.20f, 0.24f, 0.30f, 0.75f);
                image.raycastTarget = false;

                var rect = image.rectTransform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, y);
                rect.sizeDelta = new Vector2(Mathf.Max(2f, w - 2f), Mathf.Max(2f, h - 2f));

                centre[room.Id] = new Vector2(x + w * 0.5f, y + h * 0.5f);
            }

            DrawRoutes(report, centre);
        }

        private void DrawRoutes(MissionReport report, Dictionary<SiteRoomId, Vector2> centre)
        {
            if (centre.Count == 0)
                return;

            var byAgent = new Dictionary<AgentId, List<SiteRoomId>>();
            var order = new List<AgentId>();

            foreach (RouteStep step in report.Routes)
            {
                if (!byAgent.TryGetValue(step.Agent, out List<SiteRoomId> rooms))
                {
                    rooms = new List<SiteRoomId>();
                    byAgent[step.Agent] = rooms;
                    order.Add(step.Agent);
                }

                rooms.Add(step.RoomId);
            }

            // Distinct enough to tell two operatives apart on one diagram, and ordered so
            // the first agent is always the same colour between two debriefs.
            Color[] palette =
            {
                new(1.00f, 0.78f, 0.30f, 0.95f),
                new(0.40f, 0.85f, 1.00f, 0.95f),
                new(0.60f, 1.00f, 0.55f, 0.95f),
                new(1.00f, 0.55f, 0.85f, 0.95f),
            };

            for (int i = 0; i < order.Count; i++)
            {
                List<SiteRoomId> rooms = byAgent[order[i]];
                Color colour = palette[i % palette.Length];

                for (int r = 1; r < rooms.Count; r++)
                {
                    if (!centre.TryGetValue(rooms[r - 1], out Vector2 from) ||
                        !centre.TryGetValue(rooms[r], out Vector2 to))
                        continue;

                    DrawSegment(from, to, colour, $"Route_{i}_{r}");
                }
            }
        }

        /// <summary>
        /// Draws one room-to-room leg as a thin rotated bar.
        /// </summary>
        /// <remarks>
        /// A rotated image rather than a line renderer: the debrief is built once, when the
        /// mission is already over, so the cost of a hundred extra components is nothing
        /// next to the clarity of not needing a second material or a second camera.
        /// </remarks>
        private void DrawSegment(Vector2 from, Vector2 to, Color colour, string name)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;

            if (length < 0.5f)
                return;

            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(_diagram, false);

            var image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;

            var rect = image.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = from;
            rect.sizeDelta = new Vector2(length, 3f);
            rect.localRotation = Quaternion.Euler(0f, 0f, -Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }
    }

    /// <summary>The debrief's own few strings.</summary>
    public static class ReportStrings
    {
        /// <summary>The clipboard button.</summary>
        public const string Copy = "report.copy";

        /// <summary>Shown for a moment after a successful copy.</summary>
        public const string Copied = "report.copied";

        private static readonly (string Key, string Thai, string English)[] Rows =
        {
            (Copy, "คัดลอกรายงาน", "Copy report"),
            (Copied, "คัดลอกแล้ว", "Copied"),
        };

        /// <summary>Adds every row to the localization service.</summary>
        public static void Register(LocalizationService localization)
        {
            if (localization is null)
                return;

            foreach (var row in Rows)
            {
                localization.Add(row.Key, UiLanguage.Thai, row.Thai);
                localization.Add(row.Key, UiLanguage.English, row.English);
            }
        }
    }
}