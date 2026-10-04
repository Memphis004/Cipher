using System;
using System.Collections;
using System.Collections.Generic;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Localisation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// The tactical HUD: the controlled agent's light state, where they are, and the
    /// contradiction beat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The light-state indicator is the most important element on this screen.</b> The
    /// player must never be surprised by being seen while standing in what looked like
    /// shadow, and the only way to guarantee that is to state the current state in words as
    /// well as in shading. Shading alone fails for the reason shading always fails: at a
    /// glance, in a still frame, on a bad monitor, a silhouette and a well-lit figure at
    /// distance are both "a small shape".
    /// </para>
    /// <para>
    /// <b>Every string here is a localization key</b> (rule 4). Core does not write prose,
    /// and neither does this — the rows for the handful of keys the tactical view needs are
    /// registered by <see cref="TacticalStrings"/> in both shipped languages.
    /// </para>
    /// <para>
    /// Built in code rather than from prefabs, for the same reason the stage-7 windows are:
    /// the element set is small, entirely procedural, and has no art dependency, so a
    /// prefab per chip would be four assets describing four rectangles.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class TacticalHud : MonoBehaviour
    {
        private Canvas _canvas;
        private Image _lightChip;
        private Text _lightLabel;
        private Text _roomLabel;
        private Text _fogLabel;
        private Image _flash;
        private Text _flashLabel;
        private Text _contradictionCount;
        private Coroutine _beat;

        private UiLanguage _language = UiLanguage.English;
        private Func<string, string> _resolve = key => key;

        /// <summary>
        /// Builds the canvas.
        /// </summary>
        /// <param name="resolve">Resolves a localization key to text.</param>
        public void Build(Func<string, string> resolve)
        {
            _resolve = resolve ?? (_ => string.Empty);

            var canvasGo = new GameObject("TacticalHud", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.GetComponent<Canvas>();

            // Screen Space - Overlay so the HUD is never clipped by the site camera and is
            // captured by the Game View rather than by a scene camera render.
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            Font font = TacticalStrings.LoadFont();

            BuildLightPanel(font);
            BuildRoomPanel(font);
            BuildFogLegend(font);
            BuildFlash(font);

            // Everything above was created without a parent, because each panel names its
            // own parent. The flash is the exception: it must cover the entire screen, so
            // it stretches rather than sitting at a fixed reference resolution.
            Stretch(_flash.rectTransform);

            // Applied last, because building the legend is what registers it.
            ApplyBottomPivots();
        }

        /// <summary>Switches which language the HUD resolves keys in.</summary>
        public void SetLanguage(UiLanguage language) => _language = language;

        private void BuildLightPanel(Font font)
        {
            var panel = Panel("LightPanel", new Vector2(0f, 1f), new Vector2(0f, -18f),
                new Vector2(360f, 118f), new Color(0.04f, 0.05f, 0.07f, 0.82f));

            _lightChip = Panel("Chip", new Vector2(0f, 1f), new Vector2(16f, -16f),
                new Vector2(40f, 40f), Color.black, panel.transform);

            _lightLabel = Label("Level", font, 30, TextAnchor.MiddleLeft, panel.transform);
            SetRect(_lightLabel.rectTransform, new Vector2(0f, 1f),
                new Vector2(68f, -14f), new Vector2(276f, 44f));

            var hint = Label("Hint", font, 17, TextAnchor.MiddleLeft, panel.transform);
            SetRect(hint.rectTransform, new Vector2(0f, 1f),
                new Vector2(68f, -58f), new Vector2(276f, 44f));
            hint.text = _resolve(TacticalStrings.LightHint);
        }

        private void BuildRoomPanel(Font font)
        {
            var panel = Panel("RoomPanel", new Vector2(0f, 1f), new Vector2(0f, -144f),
                new Vector2(360f, 84f), new Color(0.04f, 0.05f, 0.07f, 0.72f));

            _roomLabel = Label("Room", font, 22, TextAnchor.UpperLeft, panel.transform);
            SetRect(_roomLabel.rectTransform, new Vector2(0f, 1f),
                new Vector2(16f, -10f), new Vector2(328f, 30f));

            _fogLabel = Label("Fog", font, 18, TextAnchor.UpperLeft, panel.transform);
            SetRect(_fogLabel.rectTransform, new Vector2(0f, 1f),
                new Vector2(16f, -42f), new Vector2(328f, 26f));
        }

        private void BuildFogLegend(Font font)
        {
            var panel = Panel("FogLegend", new Vector2(0f, 0f), new Vector2(18f, 18f),
                new Vector2(260f, 176f), new Color(0.04f, 0.05f, 0.07f, 0.72f));

            // Bottom-anchored, so this one pivots from its own bottom-left corner. With the
            // top-left pivot everything else uses, an eighteen-pixel offset from the bottom
            // edge puts the panel's bottom edge eighteen pixels up and its body off-screen.
            _bottomPivoted.Add(panel.rectTransform);

            var title = Label("Title", font, 17, TextAnchor.UpperLeft, panel.transform);
            SetRect(title.rectTransform, new Vector2(0f, 1f),
                new Vector2(14f, -10f), new Vector2(232f, 22f));
            title.text = _resolve(TacticalStrings.FogLegendTitle);

            FogLook[] looks =
            {
                FogLook.UnknownVoid, FogLook.Reported, FogLook.Scouted,
                FogLook.Observed, FogLook.Cleared,
            };

            for (int i = 0; i < looks.Length; i++)
            {
                float top = -38f - i * 27f;

                var swatch = Panel($"Swatch_{looks[i]}", new Vector2(0f, 1f),
                    new Vector2(14f, top), new Vector2(20f, 20f), FogLooks.TintFor(looks[i]), panel.transform);

                swatch.GetComponent<Image>().raycastTarget = false;

                var text = Label($"Text_{looks[i]}", font, 16, TextAnchor.UpperLeft, panel.transform);
                SetRect(text.rectTransform, new Vector2(0f, 1f),
                    new Vector2(42f, top), new Vector2(204f, 22f));
                text.text = _resolve(TacticalStrings.FogLookKey(looks[i]));
            }
        }

        private void BuildFlash(Font font)
        {
            _flash = Panel("ContradictionBeat", Vector2.zero, Vector2.zero,
                Vector2.zero, new Color(0.85f, 0.12f, 0.10f, 0f));
            _flash.raycastTarget = false;

            _flashLabel = Label("Beat", font, 46, TextAnchor.MiddleCenter, _flash.transform);
            Stretch(_flashLabel.rectTransform);
        }

        /// <summary>
        /// Shows the controlled agent's current light state.
        /// </summary>
        /// <param name="level">Core's answer for the controlled agent's position.</param>
        public void SetLightState(SiteLightLevel level)
        {
            _lightChip.color = ColourFor(level);
            _lightLabel.text = _resolve(TacticalStrings.LightStateKey(level));
            _lightLabel.color = ColourFor(level);
        }

        /// <summary>Shows which room the controlled agent is in and what is known about it.</summary>
        public void SetRoom(SiteRoom room, FogLook look)
        {
            if (room is null)
            {
                _roomLabel.text = _resolve(TacticalStrings.RoomNone);
                _fogLabel.text = string.Empty;
                return;
            }

            _roomLabel.text = _resolve(room.NameKey);
            _fogLabel.text = _resolve(TacticalStrings.FogLookKey(look));
            _fogLabel.color = FogLooks.TintFor(look);
        }

        /// <summary>
        /// Plays the contradiction beat: a hard red wash, a snapped label, and a hold long
        /// enough to be impossible to miss.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Short on purpose. This is a punctuation mark, not a window — the player is in the
        /// middle of a mission and the fact has already been recorded permanently in
        /// <c>MissionFog</c>. One and a half seconds is long enough to register in peripheral
        /// vision and short enough that it never becomes something to wait out.
        /// </para>
        /// <para>
        /// It never blocks input and never pauses. A beat that stopped the game would make
        /// the most dramatic moment in the game the most annoying one.
        /// </para>
        /// </remarks>
        public void PlayContradiction(IntelContradicted contradiction)
        {
            if (contradiction is null)
                return;

            if (_beat != null)
                StopCoroutine(_beat);

            _flashLabel.text = _resolve(TacticalStrings.Contradiction);
            _beat = StartCoroutine(ContradictionRoutine());
        }

        private IEnumerator ContradictionRoutine()
        {
            const float hold = 0.16f;
            const float fade = 0.55f;

            _flashLabel.enabled = true;
            _flash.enabled = true;

            // Snapped on rather than faded in. A contradiction the player can watch arrive
            // is a contradiction they can mistake for an effect they chose.
            _flash.color = new Color(0.85f, 0.12f, 0.10f, 0.46f);
            yield return new WaitForSecondsRealtime(hold);

            for (float t = 0f; t < fade; t += Time.unscaledDeltaTime)
            {
                float a = Mathf.Lerp(0.46f, 0f, t / fade);
                _flash.color = new Color(0.85f, 0.12f, 0.10f, a);
                yield return null;
            }

            _flash.enabled = false;
            _flashLabel.enabled = false;
            _beat = null;
        }

        /// <summary>
        /// The colour a light state is shown in, on both the chip and the label.
        /// </summary>
        /// <remarks>
        /// Three distinct hues, not one hue at three brightnesses. A player glancing at the
        /// corner of the screen in a dark room needs a hue difference to register; a
        /// brightness difference in a dark UI is a difference they will not see.
        /// </remarks>
        public static Color ColourFor(SiteLightLevel level) => level switch
        {
            SiteLightLevel.Lit => new Color(1.00f, 0.88f, 0.45f),
            SiteLightLevel.Dim => new Color(0.62f, 0.72f, 0.92f),
            _ => new Color(0.42f, 0.48f, 0.72f),
        };

        private Image Panel(
            string name, Vector2 anchor, Vector2 anchoredPosition,
            Vector2 size, Color colour, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));

            // Default to the canvas. A uGUI element with no Canvas ancestor does not
            // render at all, and it renders as nothing rather than as an error — which is
            // why every panel here is parented explicitly even when it looks redundant.
            go.transform.SetParent(parent != null ? parent : _canvas.transform, false);

            var image = go.GetComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;

            SetRect(image.rectTransform, anchor, anchoredPosition, size);
            return image;
        }

        private Text Label(string name, Font font, int size, TextAnchor anchor, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        /// <summary>
        /// Places an element by its top-left corner, in reference-resolution pixels.
        /// </summary>
        /// <remarks>
        /// One layout primitive rather than four anchor/pivot combinations passed around.
        /// Mixing them is how an element ends up anchored to both edges at once and
        /// stretches to fill its parent — which is what a chip is supposed to <em>not</em>
        /// do, and what made the light indicator render as a full-height bar.
        /// </remarks>
        private static void SetRect(
            RectTransform rect, Vector2 anchor, Vector2 anchoredPosition, Vector2 size,
            Vector2? pivot = null)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot ?? new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
        }

        /// <summary>Stretches an element across its parent, for a full-screen overlay.</summary>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private readonly System.Collections.Generic.List<RectTransform> _bottomPivoted = new();

        /// <summary>
        /// Re-pivots every bottom-anchored panel so its offset means what it looks like.
        /// </summary>
        private void ApplyBottomPivots()
        {
            foreach (RectTransform rect in _bottomPivoted)
                rect.pivot = new Vector2(0f, 0f);
        }
    }

    /// <summary>
    /// The localization rows the tactical view needs, in both shipped languages.
    /// </summary>
    /// <remarks>
    /// Registered from code rather than shipped as a table because they are a fixed,
    /// tiny, presentation-only set and the stage-8 table pipeline is the place this would
    /// move to if the set ever grew. They still go through <see cref="LocalizationService"/>
    /// like everything else, so a missing row renders as a key and logs rather than
    /// throwing — the property that matters is the same one the table-driven rows have.
    /// </remarks>
    public static class TacticalStrings
    {
        /// <summary>Shown under the light chip, explaining that it is the controlling factor.</summary>
        public const string LightHint = "tactical.light.hint";

        /// <summary>The fog legend heading.</summary>
        public const string FogLegendTitle = "tactical.fog.legend";

        /// <summary>Shown when the controlled agent is in a doorway between rooms.</summary>
        public const string RoomNone = "tactical.room.none";

        /// <summary>The contradiction beat's headline.</summary>
        public const string Contradiction = "tactical.intel.contradiction";

        /// <summary>Localization key for a light state.</summary>
        public static string LightStateKey(SiteLightLevel level) => level switch
        {
            SiteLightLevel.Lit => "tactical.light.lit",
            SiteLightLevel.Dim => "tactical.light.dim",
            _ => "tactical.light.dark",
        };

        /// <summary>Localization key for a fog look.</summary>
        public static string FogLookKey(FogLook look) => look switch
        {
            FogLook.Reported => "tactical.fog.reported",
            FogLook.Scouted => "tactical.fog.scouted",
            FogLook.Observed => "tactical.fog.observed",
            FogLook.Cleared => "tactical.fog.cleared",
            _ => "tactical.fog.unknown",
        };

        /// <summary>
        /// The font the HUD draws with.
        /// </summary>
        /// <remarks>
        /// The project's Thai face, because Thai is the default language and a missing
        /// glyph is a hard failure there. The builtin is the fallback so that a clone with
        /// no Resources font still produces a legible HUD rather than an exception in the
        /// middle of a mission.
        /// </remarks>
        public static Font LoadFont()
        {
            // `ProjectSpy.Core` declares its own Resources (the strategic resource pool), so the
            // bare name is ambiguous in this file. Qualifying it is the whole fix, and it
            // is also why the rest of the tactical view never loads an asset by name.
            var font = UnityEngine.Resources.Load<Font>("Fonts/THSarabunPSK");
            if (font != null)
                return font;

            try
            {
                return UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch (Exception)
            {
                // Unity has used both names for the builtin face; whichever one this
                // version ships, a null here only means uGUI falls back to Arial.
                return null;
            }
        }

        private static readonly (string Key, string Thai, string English)[] Rows =
        {
            (LightHint, "สถานะแสงของคุณคือสิ่งที่ทำให้เห็นตัว", "Your light state is what gets you seen"),
            (FogLegendTitle, "สิ่งที่รู้เกี่ยวกับห้อง", "What is known about a room"),
            (RoomNone, "ระหว่างห้อง", "Between rooms"),
            (Contradiction, "ข้อมูลขัดกับสิ่งที่ได้รับ", "The report was wrong"),
            ("tactical.light.lit", "สว่าง", "LIT"),
            ("tactical.light.dim", "มืดบางส่วน", "DIM"),
            ("tactical.light.dark", "มืด", "DARK"),
            ("tactical.fog.unknown", "ไม่รู้ — มืดสนิท", "Unknown — void"),
            ("tactical.fog.reported", "ได้รับรายงาน — อาจผิด", "Reported — may be wrong"),
            ("tactical.fog.scouted", "เฝ้าดูแล้ว", "Scouted"),
            ("tactical.fog.observed", "เห็นด้วยตนเอง", "Observed"),
            ("tactical.fog.cleared", "ตรวจแล้ว", "Cleared"),
        };

        /// <summary>Adds the tactical rows to a localization service, in both languages.</summary>
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