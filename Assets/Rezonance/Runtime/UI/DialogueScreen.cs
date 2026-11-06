// REZONANCE VN ENGINE - dialogue view (UI module).
// Implements IVNRuntimeView: name plate, wrapped body text, mode indicator, blinking advance chevron
// and the HUD row. Everything is code-built, no prefabs. Layers follow ARCHITECTURE.md 9.6.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>The reading surface of the VN: dialogue box plus HUD, driven by the runtime.</summary>
    [DisallowMultipleComponent]
    public sealed class DialogueScreen : MonoBehaviour, IVNRuntimeView
    {
        private const string Area = "view";

        /// <summary>Command names the HUD buttons raise for the orchestrator.</summary>
        public const string KeyAuto = "ui.game.auto";
        public const string KeySkip = "ui.game.skip";
        public const string KeyFast = "ui.game.fast";
        public const string KeyLog = "ui.game.log";
        public const string KeySave = "ui.game.save";
        public const string KeyLoad = "ui.game.load";
        public const string KeyMenu = "ui.game.menu";
        public const string KeyHide = "ui.game.hide_ui";

        private Transform dialogueLayer;
        private Transform hudLayer;

        private CanvasGroup rootGroup;
        private Image panel;
        private GameObject speakerPlate;
        private Image speakerPlateImage;
        private TextMeshProUGUI speakerLabel;
        private TextMeshProUGUI bodyLabel;
        private Image chevron;
        private TextMeshProUGUI modeLabel;
        private TextMeshProUGUI hintLabel;
        private GameObject hintObject;
        private Button advanceButton;
        private RectTransform hudRow;

        private readonly Button[] modeButtons = new Button[3];

        /// <summary>Runtime the HUD and the advance click talk to; null is tolerated.</summary>
        public IVNRuntime Runtime { get; set; }

        /// <summary>Text source; null means labels keep their keys (integration fallback).</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Choice surface fed by <see cref="ShowChoices"/>.</summary>
        public ChoiceScreen Choices { get; set; }

        /// <summary>Every HUD button and the advance click funnel through this sink.</summary>
        public Action<VNCommand> CommandSink { get; set; }

        /// <summary>Raised when the runtime says typing has finished (bootstrap may schedule auto).</summary>
        public event Action TypingFinished;

        /// <summary>Raised whenever the busy state changes.</summary>
        public event Action<bool> BusyChanged;

        /// <summary>True while the typewriter is running.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>True once the current line is fully on screen.</summary>
        public bool TypingComplete { get; private set; } = true;

        /// <summary>True while a menu is waiting for a decision.</summary>
        public bool ChoicePending { get; private set; }

        /// <summary>Last context painted - handy for save previews and debugging.</summary>
        public VNRenderContext LastContext { get; private set; }

        /// <summary>Current playback mode mirrored from SetRunMode.</summary>
        public VNRunMode Mode { get; private set; } = VNRunMode.Manual;

        /// <summary>UI hidden by the HideUi command.</summary>
        public bool UiHidden { get; private set; }

        private float fontSize = UiStyle.DialogueFontSize;

        /// <summary>Body text size, clamped into the readable 36..44 band.</summary>
        public float TextFontSize
        {
            get => fontSize;
            set
            {
                fontSize = Mathf.Clamp(value, UiStyle.DialogueFontSizeMin, UiStyle.DialogueFontSizeMax);
                if (bodyLabel != null) bodyLabel.fontSize = fontSize;
            }
        }

        // ---------------------------------------------------------------- construction

        /// <summary>Builds the dialogue box on DialogueLayer and the HUD on HudLayer.</summary>
        public static DialogueScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("DialogueLayer");
            var go = new GameObject("DialogueScreen", typeof(RectTransform));
            var screen = go.AddComponent<DialogueScreen>();
            screen.dialogueLayer = layer;
            screen.hudLayer = UIBuilder.GetLayer("HudLayer");
            UIBuilder.Safe("DialogueScreen.Build", screen.Build);
            return screen;
        }

        private void Build()
        {
            if (dialogueLayer == null) dialogueLayer = UIBuilder.GetLayer("DialogueLayer");
            if (hudLayer == null) hudLayer = UIBuilder.GetLayer("HudLayer");

            var rt = transform as RectTransform;
            rt.SetParent(dialogueLayer, false);
            UIBuilder.Stretch(rt);
            rootGroup = gameObject.AddComponent<CanvasGroup>();

            panel = UIBuilder.CreateNineSlicePanel("DialoguePanel", rt, UiStyle.DialogueBackdrop);
            UIBuilder.SetRect(panel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 24f), new Vector2(1600f, 320f));
            panel.raycastTarget = false;

            bodyLabel = UIBuilder.CreateText(rt, "DialogueText", fontSize, UiStyle.Text, true);
            UIBuilder.SetRect(bodyLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 166f), new Vector2(1480f, 200f));
            bodyLabel.alignment = TextAlignmentOptions.TopLeft;
            bodyLabel.overflowMode = TextOverflowModes.Overflow;
            bodyLabel.enableWordWrapping = true;
            bodyLabel.margin = new Vector4(70f, 24f, 70f, 24f);
            bodyLabel.lineSpacing = 8f;
            bodyLabel.raycastTarget = false;

            chevron = UIBuilder.CreateRect("Chevron", rt).gameObject.AddComponent<Image>();
            chevron.sprite = UIBuilder.Chevron;
            chevron.color = UiStyle.AccentWarm;
            chevron.raycastTarget = false;
            UIBuilder.SetRect(chevron.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(700f, 46f), new Vector2(28f, 18f));

            hintLabel = UIBuilder.CreateText(rt, "ChoicesHint", UiStyle.CaptionFontSize, UiStyle.TextDim, true);
            UIBuilder.SetRect(hintLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 372f), new Vector2(1200f, 40f));
            hintLabel.alignment = TextAlignmentOptions.Midline;
            hintLabel.raycastTarget = false;
            UIBuilder.BindLocalized(hintLabel, "ui.game.choices_hint");
            hintObject = hintLabel.gameObject;
            hintObject.SetActive(false);

            modeLabel = UIBuilder.CreateText(rt, "ModeLabel", UiStyle.CaptionFontSize, UiStyle.Accent, false);
            UIBuilder.SetRect(modeLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(32f, 356f), new Vector2(420f, 36f));
            modeLabel.alignment = TextAlignmentOptions.MidlineLeft;
            modeLabel.raycastTarget = false;

            // Invisible click surface over the whole dialogue area: tap equals Advance.
            var advanceRt = UIBuilder.CreateRect("AdvanceArea", rt);
            UIBuilder.SetRect(advanceRt, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(-16f, -16f));
            var advanceImg = advanceRt.gameObject.AddComponent<Image>();
            advanceImg.color = new Color(1f, 1f, 1f, 0f);
            advanceImg.raycastTarget = true;
            advanceButton = advanceRt.gameObject.AddComponent<Button>();
            advanceButton.targetGraphic = advanceImg;
            advanceButton.transition = Selectable.Transition.None;
            advanceButton.onClick.AddListener(() => UIBuilder.Safe("advance", OnAdvanceClicked));

            BuildSpeakerPlate();
            BuildHud();
            BuildMiniGamePlaceholder();
            SetRunMode(Mode);
        }

        private void BuildSpeakerPlate()
        {
            var plateRt = UIBuilder.CreateNineSlicePanel("SpeakerPlate", panel.transform, UiStyle.PanelActive);
            UIBuilder.SetRect(plateRt.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(28f, 22f), new Vector2(420f, 68f));
            speakerPlateImage = plateRt;
            speakerPlate = plateRt.gameObject;

            speakerLabel = UIBuilder.CreateText(plateRt.transform, "SpeakerName", UiStyle.SpeakerFontSize, UiStyle.Text, false);
            UIBuilder.Stretch(speakerLabel.rectTransform, 24f, 24f, 4f, 4f);
            speakerLabel.alignment = TextAlignmentOptions.MidlineLeft;
            speakerLabel.overflowMode = TextOverflowModes.Shrink;
            speakerLabel.raycastTarget = false;
        }

        private void BuildHud()
        {
            hudRow = UIBuilder.CreateRect("DialogueHud", hudLayer);
            UIBuilder.SetRect(hudRow, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                              new Vector2(-24f, 360f), new Vector2(8f, 64f));
            var hlg = hudRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childAlignment = TextAnchor.UpperRight;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.reverseArrangement = true;

            modeButtons[0] = HudButton(KeyAuto, VNCommand.ToggleAuto);
            modeButtons[1] = HudButton(KeySkip, VNCommand.ToggleSkip);
            modeButtons[2] = HudButton(KeyFast, VNCommand.ToggleFast);
            HudButton(KeyLog, VNCommand.OpenBacklog);
            HudButton(KeySave, VNCommand.OpenSettings, () =>
            {
                if (SaveRequested != null) SaveRequested();
                else OpenRouted(SaveLoadScreen.Id, SaveLoadMode.Save);
            });
            HudButton(KeyLoad, VNCommand.OpenSettings, () =>
            {
                if (LoadRequested != null) LoadRequested();
                else OpenRouted(SaveLoadScreen.Id, SaveLoadMode.Load);
            });
            HudButton(KeyMenu, VNCommand.OpenMenu);
            HudButton(KeyHide, VNCommand.HideUi);
        }

        /// <summary>Opens a registered modal screen; a no-op with a warning when the router is absent.</summary>
        private static void OpenRouted(string screenId, object payload)
        {
            var router = UiRouter.Instance;
            if (router == null)
            {
                VNLog.Warn(Area, "UiRouter is missing, cannot open " + screenId);
                return;
            }
            router.Open(screenId, payload);
        }

        /// <summary>Optional overrides so the orchestrator can route Save/Load to its own screens.</summary>
        public Action SaveRequested { get; set; }

        /// <summary>Optional override for the Load HUD button.</summary>
        public Action LoadRequested { get; set; }

        private Button HudButton(string key, VNCommand command, Action overrideAction = null)
        {
            var rt = UIBuilder.CreateNineSlicePanel("Hud_" + key, hudRow, UiStyle.Panel);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minWidth = UiStyle.HudButtonSize;
            le.minHeight = UIBuilder.MinTouchSize;
            le.preferredWidth = UiStyle.HudButtonSize;
            le.preferredHeight = UiStyle.HudButtonSize;

            var label = UIBuilder.CreateLabel(key, rt.transform, 18f, UiStyle.Text, false);
            UIBuilder.Stretch(label.rectTransform, 4f, 4f, 4f, 4f);
            label.alignment = TextAlignmentOptions.Center;
            label.overflowMode = TextOverflowModes.Shrink;
            label.enableWordWrapping = true;
            label.raycastTarget = false;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = rt;
            button.colors = UiStyle.ButtonColors(UiStyle.Panel);
            button.onClick.AddListener(() => UIBuilder.Safe("hud." + key, () =>
            {
                if (overrideAction != null) overrideAction();
                else CommandSink?.Invoke(command);
            }));
            return button;
        }

        private GameObject miniGamePanel;
        private TextMeshProUGUI miniGameLabel;

        private void BuildMiniGamePlaceholder()
        {
            miniGamePanel = UIBuilder.CreateNineSlicePanel("MiniGameStub", transform, UiStyle.Panel).gameObject;
            var rt = miniGamePanel.transform as RectTransform;
            UIBuilder.SetRect(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(900f, 320f));

            miniGameLabel = UIBuilder.CreateLabel("ui.game.minigame_placeholder", miniGamePanel.transform,
                                                  UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(miniGameLabel.rectTransform, new Vector2(0f, 0.35f), new Vector2(1f, 1f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            miniGameLabel.alignment = TextAlignmentOptions.Center;

            var cont = UIBuilder.CreateButton("ui.game.minigame_continue", miniGamePanel.transform, () =>
            {
                HideMiniGame();
                CommandSink?.Invoke(VNCommand.Advance);
            });
            UIBuilder.SetRect(cont.GetComponent<RectTransform>(), new Vector2(0.5f, 0.08f), new Vector2(0.5f, 0.08f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, UiStyle.ButtonHeight));
            miniGamePanel.SetActive(false);
        }

        // ---------------------------------------------------------------- IVNRuntimeView

        /// <summary>Paints one frame of the script.</summary>
        public void Render(VNRenderContext context)
        {
            if (context == null)
            {
                VNLog.Warn(Area, "Render(null) - showing an empty frame");
                ShowPlaceholder(string.Empty);
                return;
            }

            LastContext = context;
            if (context.mode != Mode) SetRunMode(context.mode);

            var text = context.text;
            if (string.IsNullOrEmpty(text) && context.node != null) text = context.node.text;
            bodyLabel.text = text ?? string.Empty;

            var showPlate = ShouldShowPlate(context);
            speakerPlate.SetActive(showPlate);
            if (showPlate)
            {
                speakerLabel.text = ResolveSpeaker(context);
                speakerPlateImage.color = UiStyle.WithAlpha(UiStyle.AccentForRole(context.role), 210);
            }

            ChoicePending = context.choicePending;
            TypingComplete = false;
            SetBusy(true);
            UpdateChevron();
        }

        private static bool ShouldShowPlate(VNRenderContext context)
        {
            if (!string.IsNullOrEmpty(context.speakerName)) return true;
            if (string.IsNullOrEmpty(context.node == null ? null : context.node.speaker)) return false;
            switch (context.node.kind)
            {
                case VNNodeKind.Remark:
                case VNNodeKind.Narration:
                case VNNodeKind.Blank:
                case VNNodeKind.ScreenText:
                case VNNodeKind.MiniGame:
                    return false;
                default:
                    return true;
            }
        }

        private string ResolveSpeaker(VNRenderContext context)
        {
            if (!string.IsNullOrEmpty(context.speakerName)) return context.speakerName;

            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (!string.IsNullOrEmpty(context.node == null ? null : context.node.speaker) && loc != null)
            {
                try
                {
                    var name = loc.SpeakerDisplayName(context.node.speaker);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                catch (Exception e) { VNLog.Warn(Area, "SpeakerDisplayName failed: " + e.Message); }
            }

            switch (context.role)
            {
                case VNSpeakerRole.Player: return UIBuilder.T("ui.speaker.player");
                case VNSpeakerRole.InnerVoice: return UIBuilder.T("ui.speaker.inner_voice");
                case VNSpeakerRole.System: return UIBuilder.T("ui.speaker.system");
                default: return string.Empty;
            }
        }

        /// <summary>Shows the menu options; the actual list lives in ChoiceScreen on ChoiceLayer.</summary>
        public void ShowChoices(VNNode menuNode, IReadOnlyList<string> localizedLabels,
                                IReadOnlyList<bool> enabledFlags)
        {
            ChoicePending = true;
            SetChevronVisible(false);
            if (hintObject != null) hintObject.SetActive(localizedLabels != null && localizedLabels.Count > 0);
            if (Choices != null) Choices.Show(menuNode, localizedLabels, enabledFlags);
            else VNLog.Warn(Area, "No ChoiceScreen mounted - the menu is invisible");
        }

        /// <summary>Hides the option list.</summary>
        public void HideChoices()
        {
            ChoicePending = false;
            if (hintObject != null) hintObject.SetActive(false);
            if (Choices != null) Choices.Hide();
            UpdateChevron();
        }

        /// <summary>Mirrors the playback mode into the HUD chips and the mode label.</summary>
        public void SetRunMode(VNRunMode mode)
        {
            Mode = mode;
            for (var i = 0; i < modeButtons.Length; i++)
            {
                var b = modeButtons[i];
                if (b == null) continue;
                var active = (i == 0 && (mode == VNRunMode.Auto))
                             || (i == 1 && (mode == VNRunMode.SkipUnseen))
                             || (i == 2 && (mode == VNRunMode.Fast));
                var img = b.targetGraphic as Image;
                if (img != null) img.color = active ? UiStyle.Accent : UiStyle.Panel;
                var label = b.GetComponentInChildren<TMP_Text>();
                if (label != null) label.color = active ? UiStyle.TextOnAccent : UiStyle.Text;
            }

            string key;
            switch (mode)
            {
                case VNRunMode.Auto: key = KeyAuto; break;
                case VNRunMode.SkipUnseen: key = KeySkip; break;
                case VNRunMode.Fast: key = KeyFast; break;
                default: key = null; break;
            }
            if (modeLabel != null) modeLabel.text = key == null ? string.Empty : UIBuilder.T(key).ToUpperInvariant();
            if (mode == VNRunMode.Fast) { TypingComplete = true; SetChevronVisible(true); }
        }

        /// <summary>The runtime tells us the line finished appearing; Auto timing starts from here.</summary>
        public void NotifyTypingFinished()
        {
            TypingComplete = true;
            SetBusy(false);
            SetChevronVisible(true);
            try { TypingFinished?.Invoke(); }
            catch (Exception e) { VNLog.Error(Area, "TypingFinished handler failed: " + e.Message); }
        }

        /// <summary>Busy while the typewriter runs: the chevron sleeps and taps only finish the line.</summary>
        public void NotifyBusy(bool busy)
        {
            SetBusy(busy);
            UpdateChevron();
        }

        private void SetBusy(bool busy)
        {
            if (IsBusy == busy) return;
            IsBusy = busy;
            try { BusyChanged?.Invoke(busy); }
            catch (Exception e) { VNLog.Error(Area, "BusyChanged handler failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- behaviour

        private void OnAdvanceClicked()
        {
            if (ChoicePending) return;
            var runtime = Runtime;
            if (runtime == null)
            {
                VNLog.Warn(Area, "Advance tapped but no runtime is bound");
                return;
            }
            if (runtime.IsTyping) runtime.CompleteTyping();
            else runtime.Advance();
        }

        private void UpdateChevron()
        {
            SetChevronVisible(!IsBusy && !ChoicePending && !UiHidden);
        }

        private void SetChevronVisible(bool value)
        {
            if (chevron != null) chevron.enabled = value;
        }

        private float blink;

        private void Update()
        {
            if (chevron == null || !chevron.enabled) return;
            blink += Time.unscaledDeltaTime;
            var a = 0.35f + 0.65f * Mathf.PingPong(blink * 2.2f, 1f);
            chevron.color = UiStyle.WithAlpha(UiStyle.AccentWarm, (byte)(a * 255f));
        }

        /// <summary>Empty-state text used when the registry or the localizer produced nothing.</summary>
        public void ShowPlaceholder(string text)
        {
            if (bodyLabel == null) return;
            bodyLabel.text = string.IsNullOrEmpty(text)
                ? UIBuilder.T("ui.backlog.empty")
                : text;
            if (speakerPlate != null) speakerPlate.SetActive(false);
            TypingComplete = true;
            SetBusy(false);
            UpdateChevron();
        }

        /// <summary>Shows the "[Mini-igra]" stub panel; the real host replaces it later.</summary>
        public void ShowMiniGame(string miniGameId)
        {
            if (miniGamePanel == null) return;
            miniGamePanel.SetActive(true);
            VNLog.Info(Area, "Mini game placeholder: " + miniGameId);
        }

        /// <summary>Hides the mini game stub.</summary>
        public void HideMiniGame()
        {
            if (miniGamePanel != null) miniGamePanel.SetActive(false);
        }

        /// <summary>HideUi: the reading text stays, everything else disappears.</summary>
        public void SetUiHidden(bool hidden)
        {
            UiHidden = hidden;
            if (hudRow != null) hudRow.gameObject.SetActive(!hidden);
            if (panel != null) panel.enabled = !hidden;
            if (speakerPlate != null) speakerPlate.SetActive(!hidden && speakerPlate.activeSelf);
            if (modeLabel != null) modeLabel.gameObject.SetActive(!hidden);
            SetChevronVisible(!hidden && !IsBusy && !ChoicePending);
        }

        /// <summary>Switches the whole reading surface (used by the main menu).</summary>
        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }

        /// <summary>Re-reads every localized string on the HUD after a language switch.</summary>
        public void RefreshLocalized()
        {
            UIBuilder.Relocalize();
            SetRunMode(Mode);
        }

        private void OnEnable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged += OnLanguageChanged; } catch (Exception e) { VNLog.Warn(Area, e.Message); }
            }
        }

        private void OnDisable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged -= OnLanguageChanged; } catch (Exception e) { VNLog.Warn(Area, e.Message); }
            }
        }

        private void OnLanguageChanged(VNLanguage language)
        {
            UIBuilder.Safe("dialogue.relocalize", RefreshLocalized);
        }

        private void OnDestroy()
        {
            TypingFinished = null;
            BusyChanged = null;
            CommandSink = null;
            SaveRequested = null;
            LoadRequested = null;
        }
    }
}
