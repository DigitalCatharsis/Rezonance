// REZONANCE VN ENGINE - diegetic on-screen text plate (the backtick lines of the script).
// Builds its own plate inside the ScreenTextLayer rect handed over by the UI module.
using System;
using Rezonance.VN.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Rezonance.VN.Presentation
{
    /// <summary>Screen text presenter implementing IScreenTextPresenter with a short glitch reveal.</summary>
    public sealed class ScreenTextPresenter : IScreenTextPresenter, IFadeTarget, IDisposable
    {
        /// <summary>Canvas layer this presenter expects (ARCHITECTURE.md 9.6).</summary>
        public const string LayerName = "ScreenTextLayer";

        /// <summary>Name of the generated plate object.</summary>
        public const string ElementName = "VN_ScreenText";

        /// <summary>Plate centre as a fraction of the layer height, counted from the top edge.</summary>
        public const float TopThirdRatio = 0.22f;

        /// <summary>Dwell time used when the cue carries none.</summary>
        public const float DefaultSeconds = 2.4f;

        /// <summary>Length of the glitch reveal and of the fade out.</summary>
        public const float FadeSeconds = 0.3f;

        /// <summary>Width of the plate as a fraction of the layer height.</summary>
        public const float PlateWidthRatio = 0.92f;

        /// <summary>Height of the plate as a fraction of the layer height.</summary>
        public const float PlateHeightRatio = 0.085f;

        /// <summary>Letter spacing that gives the plate its terminal look.</summary>
        public const float MonoSpacingRatio = 0.08f;

        /// <summary>Resources path tried first for a monospaced TMP font; no GUID is used anywhere.</summary>
        public const string MonoFontResourcePath = "Rezonance/fonts/vn_mono";

        private const string LogArea = "screentext";
        private const int ReferenceFontSize = 44;

        private PresentationHost host;
        private RectTransform root;
        private GameObject plate;
        private RectTransform plateRect;
        private CanvasGroup group;
        private TextMeshProUGUI label;
        private VNFade fade;

        private TMP_FontAsset monoFont;
        private bool monoFontResolved;
        private string lastText;
        private float lastSeconds;
        private float jitterWidth;
        private float offsetY;
        private int glitchTick;
        private float glitchElapsed;
        private uint glitchSeed;
        private bool ready;
        private bool warnedNoLayer;

        /// <summary>Creates the presenter; the host may be null, it is resolved on Init.</summary>
        public ScreenTextPresenter(PresentationHost animationHost = null)
        {
            host = animationHost;
            fade = new VNFade(this, animationHost, DefaultSeconds, FadeSeconds);
        }

        /// <summary>The TMP label once Init succeeded, null otherwise.</summary>
        public TextMeshProUGUI Text => label;

        /// <summary>True when a line is on screen or still revealing.</summary>
        public bool IsVisible => fade != null && fade.IsVisible;

        /// <summary>True when the plate is revealed and its auto-hide window still runs.</summary>
        public bool IsWaiting => fade != null && fade.IsWaiting;

        /// <summary>Text of the visible line, null when nothing is on screen.</summary>
        public string CurrentText => IsVisible ? lastText : null;

        /// <summary>Dwell time of the last request.</summary>
        public float LastSeconds => lastSeconds;

        /// <summary>Binds to the ScreenTextLayer rect and builds the plate; warns and stays inert when it is missing.</summary>
        public void Init(RectTransform layerRect)
        {
            if (layerRect == null)
            {
                if (!warnedNoLayer)
                {
                    VNLog.Warn(LogArea, "ScreenTextLayer rect was not provided, screen text stays inert");
                    warnedNoLayer = true;
                }
                ready = false;
                return;
            }

            if (ready && root == layerRect && plate != null) return;
            DestroyPlate();

            root = layerRect;
            host = host != null && host.gameObject != null ? host : PresentationHost.EnsureExists();
            fade = new VNFade(this, host, DefaultSeconds, FadeSeconds);

            plateRect = PresentationUi.CreateNode(ElementName, root);
            if (plateRect == null)
            {
                VNLog.Warn(LogArea, "could not build the plate inside ScreenTextLayer");
                ready = false;
                return;
            }

            plate = plateRect.gameObject;
            PresentationUi.AddImage(plateRect, new Color(0.02f, 0.02f, 0.03f, 0.62f), false);
            label = PresentationUi.AddText(ElementName + "_Label", plateRect, LayerHeight(),
                                           TextAnchor.MiddleCenter, ReferenceFontSize, true);
            ApplyMonoFont();
            PresentationUi.SetMonoLook(label, MonoSpacingRatio);
            group = PresentationUi.AddGroup(plate, 0f, false);

            Layout();
            plate.SetActive(false);
            ready = true;
            VNLog.Info(LogArea, "screen text layer bound");
        }

        /// <summary>Shows a line. Repeating the visible text only restarts the dwell window, never the reveal.</summary>
        public void Show(string text, float seconds)
        {
            if (!ready) return;
            if (string.IsNullOrEmpty(text))
            {
                Hide();
                return;
            }

            var dwell = seconds > 0f ? seconds : DefaultSeconds;
            var sameLine = string.Equals(lastText, text, StringComparison.Ordinal) && fade.IsVisible;
            lastText = text;
            lastSeconds = dwell;
            label.text = text;
            ApplyMonoFont();
            PresentationUi.SetMonoLook(label, MonoSpacingRatio);
            Layout();

            if (sameLine)
            {
                fade.Refresh(dwell);
                return;
            }

            StartGlitch();
            fade.Show(dwell);
        }

        /// <summary>Shows a line for the default dwell time.</summary>
        public void Show(string text) => Show(text, DefaultSeconds);

        /// <summary>Shows a line and keeps it until Hide; the reveal glitch still plays.</summary>
        public void ShowPersistent(string text)
        {
            if (!ready || string.IsNullOrEmpty(text)) return;
            lastText = text;
            lastSeconds = 0f;
            label.text = text;
            Layout();
            StartGlitch();
            fade.Show(0f);
        }

        /// <summary>Hides the plate instantly, as the contract requires.</summary>
        public void Hide()
        {
            CancelGlitch();
            if (fade == null) return;
            fade.HideInstant();
        }

        /// <summary>Fades the plate out instead of cutting it.</summary>
        public void FadeOut()
        {
            CancelGlitch();
            if (fade != null) fade.FadeOut();
        }

        /// <summary>Restarts the dwell window, e.g. after the player changed the reading speed.</summary>
        public void Refresh(float seconds)
        {
            if (!ready || !fade.IsVisible) return;
            lastSeconds = seconds;
            fade.Refresh(seconds);
        }

        /// <summary>Overrides reveal and dwell lengths.</summary>
        public void SetTiming(float holdSeconds, float rampSeconds)
        {
            fade = new VNFade(this, host, holdSeconds, rampSeconds);
        }

        /// <summary>Assigns the monospaced TMP font; null re-reads the Resources fallback.</summary>
        public void SetMonospaceFont(TMP_FontAsset font)
        {
            monoFont = font;
            monoFontResolved = font != null;
            ApplyMonoFont();
            PresentationUi.SetMonoLook(label, MonoSpacingRatio);
        }

        /// <summary>Drops timers and the generated plate.</summary>
        public void Dispose()
        {
            CancelGlitch();
            if (fade != null) fade.Dispose();
            DestroyPlate();
            root = null;
            ready = false;
        }

        float IFadeTarget.FadeAlpha
        {
            get => group != null ? group.alpha : 0f;
            set { if (group != null) group.alpha = Mathf.Clamp01(value); }
        }

        void IFadeTarget.OnFadeStarted()
        {
            if (plate != null && !plate.activeSelf) plate.SetActive(true);
        }

        void IFadeTarget.OnFadeFinished(bool hidden)
        {
            if (hidden && plate != null && plate.activeSelf) plate.SetActive(false);
        }

        private void StartGlitch()
        {
            CancelGlitch();
            if (host == null || host.gameObject == null) return;
            glitchElapsed = 0f;
            glitchSeed = AssetLibrary.StableHash(lastText ?? string.Empty);
            glitchTick = host.Schedule(TickGlitch);
        }

        private void TickGlitch(float deltaTime)
        {
            glitchElapsed += deltaTime;
            var t = Mathf.Clamp01(glitchElapsed / Mathf.Max(0.01f, FadeSeconds));
            glitchSeed = glitchSeed * 1664525u + 1013904223u;
            var step = (int)(glitchSeed % 7u) - 3;

            if (plateRect != null)
                plateRect.anchoredPosition = new Vector2(step * jitterWidth * (1f - t), offsetY);
            if (label != null)
            {
                var flicker = t < 0.6f && glitchSeed % 3u == 0u ? 0.35f : 1f;
                label.color = new Color(1f, 1f, 1f, flicker);
            }

            if (t >= 1f) EndGlitch();
        }

        private void EndGlitch()
        {
            if (plateRect != null) plateRect.anchoredPosition = new Vector2(0f, offsetY);
            if (label != null) label.color = Color.white;
            CancelGlitch();
        }

        private void CancelGlitch()
        {
            if (glitchTick == 0) return;
            var h = host != null && host.gameObject != null ? host : PresentationHost.Instance;
            if (h != null) h.Cancel(glitchTick);
            glitchTick = 0;
        }

        private void Layout()
        {
            if (plateRect == null) return;
            var height = LayerHeight();
            var plateWidth = Mathf.Max(320f, height * PlateWidthRatio);
            var plateHeight = Mathf.Max(48f, height * PlateHeightRatio);
            offsetY = -height * TopThirdRatio;
            jitterWidth = Mathf.Max(1f, height * 0.006f);

            plateRect.anchorMin = new Vector2(0.5f, 1f);
            plateRect.anchorMax = new Vector2(0.5f, 1f);
            plateRect.pivot = new Vector2(0.5f, 1f);
            plateRect.anchoredPosition = new Vector2(0f, offsetY);
            plateRect.sizeDelta = new Vector2(plateWidth, plateHeight);

            if (label != null)
            {
                PresentationUi.Stretch(label.rectTransform);
                label.fontSize = PresentationUi.FontSize(plateHeight, ReferenceFontSize);
                PresentationUi.SetMonoLook(label, MonoSpacingRatio);
            }
        }

        private void ApplyMonoFont()
        {
            if (label == null) return;
            if (!monoFontResolved)
            {
                monoFontResolved = true;
                monoFont = Resources.Load<TMP_FontAsset>(MonoFontResourcePath);
                if (monoFont == null)
                    VNLog.Warn(LogArea, "no monospaced TMP font at Resources/" + MonoFontResourcePath
                                        + ", default font with wide letter spacing is used instead");
            }
            label.font = monoFont != null ? monoFont : TMP_Settings.defaultFontAsset;
        }

        private float LayerHeight()
        {
            if (root != null && root.rect.height > 1f) return root.rect.height;
            return PortraitDirector.ReferenceHeight;
        }

        private void DestroyPlate()
        {
            CancelGlitch();
            PresentationUi.Destroy(plate);
            plate = null;
            plateRect = null;
            group = null;
            label = null;
        }
    }
}
