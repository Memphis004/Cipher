using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ProjectSpy.Core.Tactical;
using ProjectSpy.Unity.Audio;
using ProjectSpy.Unity.Localisation;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Turns Core's alarm band into what the player sees and hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It observes, it does not decide.</b> The band comes from
    /// <c>Mission.Alarm.Band</c> every frame and Core's <see cref="AlarmSystem"/> already
    /// applied whatever the band means before this ever runs. Comparing the band to the
    /// last one seen is noticing a change, not deciding one — the same way
    /// <c>LightingDirector.Sync</c> reads <c>IsWorking</c> without writing it. A version
    /// that asked Core "did the band change" would need an event Core does not publish,
    /// and a version that recomputed the band itself would be a second copy of the rule
    /// that decides when the building is on fire.
    /// </para>
    /// <para>
    /// <b>The moment never blocks.</b> The banner is a non-raycast overlay that fades on a
    /// coroutine and never pauses, never takes input and never waits to be dismissed. The
    /// most dramatic thing in the game should not be the most annoying thing in it, and a
    /// player who is mid-sprint when a band trips has to be able to keep sprinting.
    /// </para>
    /// <para>
    /// <b>Calm is applied, not skipped.</b> Dropping back to Calm clears the tint, the
    /// vignette and the banner. A system that only reacted to escalation would leave a
    /// red screen over a building that has gone quiet, which is worse than never having
    /// tinted it at all.
    /// </para>
    /// </remarks>
    public sealed class AlarmDirector : MonoBehaviour
    {
        private const float BannerHoldSeconds = 1.1f;
        private const float BannerFadeSeconds = 0.45f;
        private const int VignetteSize = 256;

        private readonly System.Collections.Generic.Dictionary<string, AudioSource> _ambientLayers = new();

        private LocalizationService _localization;
        private LightingDirector _lights;
        private AudioService _audio;

        private AlarmBand _currentBand = AlarmBand.Calm;
        private bool _started;

        private Canvas _canvas;
        private Image _vignette;
        private Image _bannerPlate;
        private Text _bannerHeading;
        private Text _bannerMessage;
        private Coroutine _bannerRoutine;

        /// <summary>The band currently on screen.</summary>
        public AlarmBand CurrentBand => _currentBand;

        /// <summary>How many times the band has moved. Zero on a quiet mission.</summary>
        public int ChangeCount { get; private set; }

        /// <summary>
        /// Builds the overlay.
        /// </summary>
        /// <remarks>
        /// The vignette is a generated radial-falloff texture rather than an imported one,
        /// for the same reason the audio placeholders are generated: a missing art asset
        /// should not be able to remove the alarm from the game.
        /// </remarks>
        public void Build(LocalizationService localization, LightingDirector lights, AudioService audio)
        {
            _localization = localization;
            _lights = lights;
            _audio = audio;

            AlarmStrings.Register(localization);

            var canvasGo = new GameObject("AlarmOverlay", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.GetComponent<Canvas>();

            // Overlay so it is never clipped by the site camera, and drawn above the HUD
            // so a band change reads as an event rather than as another panel.
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            BuildVignette();
            BuildBanner();
        }

        private void BuildVignette()
        {
            var go = new GameObject("Vignette", typeof(Image));
            go.transform.SetParent(_canvas.transform, false);

            _vignette = go.GetComponent<Image>();
            _vignette.sprite = VignetteSprite();
            _vignette.raycastTarget = false;
            _vignette.color = Color.clear;

            var rect = _vignette.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void BuildBanner()
        {
            var plate = new GameObject("BannerPlate", typeof(Image));
            plate.transform.SetParent(_canvas.transform, false);

            _bannerPlate = plate.GetComponent<Image>();
            _bannerPlate.raycastTarget = false;
            _bannerPlate.color = new Color(0f, 0f, 0f, 0f);

            var plateRect = _bannerPlate.rectTransform;
            plateRect.anchorMin = new Vector2(0.5f, 0.5f);
            plateRect.anchorMax = new Vector2(0.5f, 0.5f);
            plateRect.pivot = new Vector2(0.5f, 0.5f);
            plateRect.anchoredPosition = new Vector2(0f, 90f);
            plateRect.sizeDelta = new Vector2(1180f, 190f);

            Font font = TacticalStrings.LoadFont();

            _bannerHeading = MakeLabel("Heading", font, 26, plate.transform);
            Place(_bannerHeading.rectTransform, new Vector2(0f, -22f), new Vector2(1140f, 34f));

            _bannerMessage = MakeLabel("Message", font, 44, plate.transform);
            Place(_bannerMessage.rectTransform, new Vector2(0f, -70f), new Vector2(1140f, 62f));

            _bannerHeading.color = Color.clear;
            _bannerMessage.color = Color.clear;
        }

        private static Text MakeLabel(string name, Font font, int size, Transform parent)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        private static void Place(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        /// <summary>
        /// Brings the presentation in line with Core's band, if it has moved.
        /// </summary>
        /// <remarks>
        /// Called every frame. The comparison is a single enum inequality and the common
        /// case allocates nothing, which is what keeps this out of the per-frame garbage
        /// budget the performance pass is going to measure.
        /// </remarks>
        public void Sync(AlarmBand band)
        {
            if (_vignette == null)
                return;

            if (!_started)
            {
                _started = true;
                _currentBand = band;
                Apply(AlarmLooks.For(band), playStinger: false);
                return;
            }

            if (band == _currentBand)
                return;

            _currentBand = band;
            ChangeCount++;

            Apply(AlarmLooks.For(band), playStinger: true);
            PlayBanner(AlarmLooks.For(band));
        }

        private void Apply(AlarmLook look, bool playStinger)
        {
            if (_lights != null)
                _lights.ApplyTint(look.LightTint, look.LightTintStrength);

            _vignette.color = new Color(
                look.VignetteColour.r,
                look.VignetteColour.g,
                look.VignetteColour.b,
                look.VignetteStrength);

            if (playStinger && _audio != null)
                _audio.Play(look.StingerKey);

            // The bed moves on every apply, not only on a rise: dropping back to Calm has
            // to be audible as the tension leaving, not only visible as the red draining.
            SyncAmbientBed(_currentBand);
        }

        /// <summary>
        /// Brings the ambient bed in line with the band: every layer up to this one plays,
        /// anything above it stops.
        /// </summary>
        /// <remarks>
        /// Deliberately not a crossfade between two beds. A drop from Lockdown to Alert has
        /// to lose the top layer and keep what is underneath it, and that is a question
        /// about set membership rather than about volume — see
        /// <see cref="AudioCues.LayersUpTo"/>.
        /// </remarks>
        private void SyncAmbientBed(AlarmBand band)
        {
            if (_audio == null)
                return;

            IReadOnlyList<string> wanted = AudioCues.LayersUpTo(band);

            var stale = new List<string>();

            foreach (KeyValuePair<string, AudioSource> playing in _ambientLayers)
            {
                if (wanted.Contains(playing.Key))
                    continue;

                _audio.StopLoop(playing.Value);
                stale.Add(playing.Key);
            }

            foreach (string key in stale)
                _ambientLayers.Remove(key);

            foreach (string key in wanted)
            {
                if (_ambientLayers.ContainsKey(key))
                    continue;

                AudioSource source = _audio.PlayLoop(key, AmbientLayerVolume(band, key));
                if (source != null)
                    _ambientLayers[key] = source;
            }
        }

        /// <summary>
        /// How loud one bed layer plays.
        /// </summary>
        /// <remarks>
        /// The top layer carries the band's weight and the ones beneath sit under it, so a
        /// rise is felt as something being added rather than as one track turning up.
        /// Giving every layer the same volume would make escalation sound like a knob.
        /// </remarks>
        private static float AmbientLayerVolume(AlarmBand band, string key)
        {
            int rank = int.TryParse(key.Substring(AudioCues.AmbientLayerPrefix.Length), out int parsed)
                ? parsed
                : 0;

            return rank >= (int)band ? 0.55f : 0.22f;
        }

        private void PlayBanner(AlarmLook look)
        {
            if (_bannerRoutine != null)
                StopCoroutine(_bannerRoutine);

            string heading = Resolve(AlarmStrings.BannerHeading);
            string message = Resolve(look.MessageKey);

            _bannerHeading.text = heading;
            _bannerMessage.text = message;

            Color tint = look.LightTint;
            _bannerMessage.color = tint;
            _bannerRoutine = StartCoroutine(BannerRoutine(tint));
        }

        private string Resolve(string key)
            => _localization is null ? key : _localization.Get(key);

        /// <summary>
        /// Fades the banner in, holds it, and fades it out. Never waits for input.
        /// </summary>
        private IEnumerator BannerRoutine(Color tint)
        {
            _bannerPlate.enabled = true;
            _bannerHeading.enabled = true;
            _bannerMessage.enabled = true;

            Color plateTarget = new Color(0f, 0f, 0f, 0.62f);
            Color textTarget = tint;

            yield return Fade(_bannerPlate, _bannerPlate.color, plateTarget, 0.18f);
            yield return Fade(_bannerHeading, _bannerHeading.color, textTarget, 0.18f);
            yield return Fade(_bannerMessage, _bannerMessage.color, textTarget, 0.18f);

            yield return new WaitForSeconds(BannerHoldSeconds);

            yield return Fade(_bannerMessage, _bannerMessage.color, Color.clear, BannerFadeSeconds);
            yield return Fade(_bannerHeading, _bannerHeading.color, Color.clear, BannerFadeSeconds);
            yield return Fade(_bannerPlate, _bannerPlate.color, Color.clear, BannerFadeSeconds);

            _bannerPlate.enabled = false;
            _bannerHeading.enabled = false;
            _bannerMessage.enabled = false;
            _bannerRoutine = null;
        }

        private static IEnumerator Fade(Graphic graphic, Color from, Color to, float seconds)
        {
            float elapsed = 0f;

            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                graphic.color = Color.Lerp(from, to, Mathf.Clamp01(elapsed / seconds));
                yield return null;
            }

            graphic.color = to;
        }

        /// <summary>
        /// A radial falloff sprite, generated once and cached.
        /// </summary>
        /// <remarks>
        /// Dark at the edges and transparent in the middle, which is what a vignette is.
        /// Generated rather than imported so that the alarm's most important visual cannot
        /// be missing from a build.
        /// </remarks>
        private static Sprite VignetteSprite()
        {
            if (_cachedVignette != null)
                return _cachedVignette;

            var texture = new Texture2D(VignetteSize, VignetteSize, TextureFormat.RGBA32, false)
            {
                name = "AlarmVignette",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            float half = VignetteSize * 0.5f;

            for (int y = 0; y < VignetteSize; y++)
            {
                for (int x = 0; x < VignetteSize; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    // Ramps from nothing at the centre to opaque at the corners. Squared so
                    // the middle of the screen stays genuinely clear — the player has to be
                    // able to read the room, not just feel that something is wrong.
                    float alpha = Mathf.Clamp01(Mathf.InverseLerp(0.35f, 1.05f, distance));
                    alpha *= alpha;

                    texture.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }

            texture.Apply();

            _cachedVignette = Sprite.Create(
                texture, new Rect(0f, 0f, VignetteSize, VignetteSize), new Vector2(0.5f, 0.5f));

            return _cachedVignette;
        }

        private static Sprite _cachedVignette;

        /// <summary>Drops the cached vignette so a domain reload rebuilds it.</summary>
        public static void ClearCache() => _cachedVignette = null;
    }
}