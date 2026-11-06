// REZONANCE VN ENGINE - modal stub host for the "[Mini-igra]" markers.
// The prototype ships no real games: Launch shows a modal panel and waits for its button.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Rezonance.VN.Presentation
{
    /// <summary>Mini-game host that renders a placeholder panel until real games register themselves.</summary>
    public sealed class MiniGameStubHost : IVNMiniGameHost, IDisposable
    {
        /// <summary>Canvas layer this host expects (ARCHITECTURE.md 9.6).</summary>
        public const string LayerName = "ModalLayer";

        /// <summary>Name of the generated modal object.</summary>
        public const string ElementName = "VN_MiniGameStub";

        /// <summary>Localization key of the explanatory line, owned by the importer (ARCHITECTURE.md 9.8).</summary>
        public const string PlaceholderKey = "ui.game.minigame_placeholder";

        /// <summary>Localization key of the continue button, owned by the importer (ARCHITECTURE.md 9.8).</summary>
        public const string ContinueKey = "ui.game.minigame_continue";

        /// <summary>Latin text used when no localizer was attached.</summary>
        public const string FallbackTitle = "MINI-GAME";

        /// <summary>Latin button text used when no localizer was attached.</summary>
        public const string FallbackButton = "OK";

        private const string LogArea = "minigame";
        private const int TitleFontSize = 40;
        private const int NoteFontSize = 26;

        private readonly Dictionary<string, Action<string, Action<bool>>> games =
            new Dictionary<string, Action<string, Action<bool>>>(StringComparer.Ordinal);

        private readonly AssetLibrary assets;
        private Func<string, string> localize;
        private PresentationHost host;
        private RectTransform root;
        private RectTransform panel;
        private CanvasGroup group;
        private TextMeshProUGUI title;
        private TextMeshProUGUI note;
        private Button continueButton;
        private VNFade fade;
        private string activeId;
        private Action<bool> pendingComplete;
        private bool bound;
        private bool warnedNoLayer;

        /// <summary>Creates the host. localize may be null, then the panel prints latin placeholders.</summary>
        public MiniGameStubHost(Func<string, string> localize = null, AssetLibrary assetLibrary = null,
                               PresentationHost animationHost = null)
        {
            this.localize = localize;
            assets = assetLibrary ?? AssetLibrary.Shared;
            host = animationHost;
        }

        /// <summary>True when the stub panel is on screen.</summary>
        public bool IsActive => panel != null && panel.gameObject.activeSelf;

        /// <summary>Id of the mini-game currently on screen, null when nothing was launched.</summary>
        public string ActiveId => activeId;

        /// <summary>Attaches or replaces the localizer supplied by the orchestrator.</summary>
        public void SetLocalizer(Func<string, string> localizer)
        {
            localize = localizer;
            if (IsActive) Paint();
        }

        /// <summary>Binds to the ModalLayer rect and builds the panel; warns and stays inert when it is missing.</summary>
        public void Init(RectTransform layerRect)
        {
            if (layerRect == null)
            {
                if (!warnedNoLayer)
                {
                    VNLog.Warn(LogArea, "ModalLayer rect was not provided, mini-game stubs cannot be shown");
                    warnedNoLayer = true;
                }
                bound = false;
                return;
            }

            if (bound && root == layerRect && panel != null) return;
            DestroyPanel();

            root = layerRect;
            host = host != null && host.gameObject != null ? host : PresentationHost.EnsureExists();
            fade = new VNFade(new PanelTarget(this), host, 0f, 0.2f);

            var shell = PresentationUi.CreateNode(ElementName, root);
            if (shell == null)
            {
                VNLog.Warn(LogArea, "could not build the stub panel inside ModalLayer");
                bound = false;
                return;
            }

            // A full screen blocker so the dialogue layer cannot swallow the click.
            PresentationUi.AddImage(shell, new Color(0f, 0f, 0f, 0.72f), true);
            group = PresentationUi.AddGroup(shell.gameObject, 0f, true);

            var card = PresentationUi.CreateNode(ElementName + "_Card", shell);
            card.anchorMin = new Vector2(0.5f, 0.5f);
            card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(920f, 420f);
            PresentationUi.AddImage(card, new Color(0.06f, 0.07f, 0.09f, 0.96f), false);

            var height = LayerHeight();
            title = PresentationUi.AddText(ElementName + "_Title", card, height, TextAnchor.UpperCenter,
                                            TitleFontSize, true);
            if (title != null)
            {
                title.rectTransform.anchorMin = new Vector2(0.06f, 0.62f);
                title.rectTransform.anchorMax = new Vector2(0.94f, 0.96f);
                title.rectTransform.offsetMin = Vector2.zero;
                title.rectTransform.offsetMax = Vector2.zero;
            }

            note = PresentationUi.AddText(ElementName + "_Note", card, height, TextAnchor.MiddleCenter,
                                          NoteFontSize, false);
            if (note != null)
            {
                note.rectTransform.anchorMin = new Vector2(0.06f, 0.24f);
                note.rectTransform.anchorMax = new Vector2(0.94f, 0.60f);
                note.rectTransform.offsetMin = Vector2.zero;
                note.rectTransform.offsetMax = Vector2.zero;
            }

            continueButton = PresentationUi.CreateButton(ElementName + "_Button", card, height * 0.07f,
                                                        FallbackButton, OnContinueClicked);
            if (continueButton != null)
            {
                var buttonRect = continueButton.transform as RectTransform;
                if (buttonRect != null)
                {
                    buttonRect.anchorMin = new Vector2(0.5f, 0.05f);
                    buttonRect.anchorMax = new Vector2(0.5f, 0.05f);
                    buttonRect.anchoredPosition = Vector2.zero;
                }
            }

            panel = shell;
            shell.gameObject.SetActive(false);
            bound = true;
            Paint();
            VNLog.Info(LogArea, "mini-game stub host bound");
        }

        /// <summary>Registers a real mini-game launcher; from then on Has reports the id as available.</summary>
        public void Register(string miniGameId, Action<string, Action<bool>> launcher)
        {
            if (string.IsNullOrEmpty(miniGameId) || launcher == null) return;
            games[miniGameId] = launcher;
        }

        /// <summary>Drops a registration so the id falls back to the stub panel again.</summary>
        public void Unregister(string miniGameId)
        {
            if (!string.IsNullOrEmpty(miniGameId)) games.Remove(miniGameId);
        }

        /// <summary>False for every id until a real game registers itself.</summary>
        public bool Has(string miniGameId)
        {
            return !string.IsNullOrEmpty(miniGameId) && games.ContainsKey(miniGameId);
        }

        /// <summary>
        /// Shows the stub panel and completes with true when the player presses continue.
        /// A registered game is delegated to instead. Re-entrant calls are ignored while one is open.
        /// </summary>
        public void Launch(string miniGameId, Action<bool> onComplete)
        {
            if (string.IsNullOrEmpty(miniGameId))
            {
                VNLog.Warn(LogArea, "Launch called without an id");
                onComplete?.Invoke(false);
                return;
            }

            if (games.TryGetValue(miniGameId, out var launcher))
            {
                launcher(miniGameId, onComplete);
                return;
            }

            if (IsActive || pendingComplete != null)
            {
                VNLog.Warn(LogArea, "mini-game " + miniGameId + " was requested while another stub is open");
                onComplete?.Invoke(false);
                return;
            }

            if (!bound)
            {
                VNLog.Warn(LogArea, "no ModalLayer, mini-game " + miniGameId + " completes immediately");
                onComplete?.Invoke(true);
                return;
            }

            activeId = miniGameId;
            pendingComplete = onComplete;
            Paint();
            fade.Show();
        }

        /// <summary>Closes the panel. Without a click the caller is completed with false so the runtime unsticks.</summary>
        public void Cancel()
        {
            Finish(false);
        }

        /// <summary>Forgets the panel and cancels the animations.</summary>
        public void Reset()
        {
            Finish(false);
            if (fade != null) fade.HideInstant();
        }

        /// <summary>Destroys the generated nodes.</summary>
        public void Dispose()
        {
            Finish(false);
            if (fade != null) fade.Dispose();
            DestroyPanel();
            root = null;
            bound = false;
        }

        private void OnContinueClicked()
        {
            Finish(true);
        }

        private void Finish(bool result)
        {
            var callback = pendingComplete;
            pendingComplete = null;
            activeId = null;
            if (fade != null && panel != null && panel.gameObject.activeSelf) fade.FadeOut();
            callback?.Invoke(result);
        }

        private void Paint()
        {
            if (title == null || note == null) return;
            title.text = Translate(PlaceholderKey, FallbackTitle);
            var text = Translate(ContinueKey, FallbackButton);
            if (continueButton != null)
            {
                var label = PresentationUi.FindText(continueButton.GetComponent<RectTransform>(),
                                                    ElementName + "_Button_Label");
                if (label != null) label.text = text;
            }
            // The id is data, not prose: printing it helps the author see which marker fired.
            note.text = string.IsNullOrEmpty(activeId) ? string.Empty : activeId;
        }

        private string Translate(string key, string fallback)
        {
            var handler = localize;
            if (handler == null) return fallback;
            string value;
            try
            {
                value = handler(key);
            }
            catch (Exception ex)
            {
                VNLog.Warn(LogArea, "localizer failed for " + key + ": " + ex.Message);
                return fallback;
            }
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private float LayerHeight()
        {
            if (root != null && root.rect.height > 1f) return root.rect.height;
            return PortraitDirector.ReferenceHeight;
        }

        private void DestroyPanel()
        {
            if (panel != null) PresentationUi.Destroy(panel.gameObject);
            panel = null;
            group = null;
            title = null;
            note = null;
            continueButton = null;
        }

        private sealed class PanelTarget : IFadeTarget
        {
            private readonly MiniGameStubHost owner;

            public PanelTarget(MiniGameStubHost host_)
            {
                owner = host_;
            }

            public float FadeAlpha
            {
                get => owner.GroupAlpha;
                set => owner.GroupAlpha = value;
            }

            public void OnFadeStarted() => owner.ActivatePanel();

            public void OnFadeFinished(bool hidden)
            {
                if (hidden) owner.DeactivatePanel();
            }
        }

        internal float GroupAlpha
        {
            get => group != null ? group.alpha : 0f;
            set
            {
                if (group != null) group.alpha = Mathf.Clamp01(value);
            }
        }

        internal void ActivatePanel()
        {
            if (panel != null && !panel.gameObject.activeSelf) panel.gameObject.SetActive(true);
        }

        internal void DeactivatePanel()
        {
            if (panel != null && panel.gameObject.activeSelf) panel.gameObject.SetActive(false);
        }
    }
}
