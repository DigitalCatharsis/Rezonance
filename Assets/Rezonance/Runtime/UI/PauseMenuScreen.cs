// REZONANCE VN ENGINE - in-game pause menu (UI module).
// Modal panel on ModalLayer with a two-step quit confirmation. Opens and closes own the input capture
// flag of IVNInput so dialogue commands (space, ctrl, tab) cannot leak through the menu.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Escape menu shown over the running scene.</summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenuScreen : MonoBehaviour, IVNScreen, IUiEscapeHandler
    {
        /// <summary>Router id.</summary>
        public const string Id = "pause_menu";

        private readonly List<Button> buttons = new List<Button>();

        private RectTransform root;
        private RectTransform panel;
        private GameObject confirmBlock;
        private TextMeshProUGUI titleLabel;

        /// <summary>Text source.</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Input source whose CaptureUiOnly flag follows this menu.</summary>
        public IVNInput Input { get; set; }

        /// <summary>Close the menu and return to the scene.</summary>
        public Action OnResume { get; set; }

        /// <summary>Open the save screen.</summary>
        public Action OnSaveRequested { get; set; }

        /// <summary>Open the load screen.</summary>
        public Action OnLoadRequested { get; set; }

        /// <summary>Open the backlog.</summary>
        public Action OnBacklog { get; set; }

        /// <summary>Open the glossary.</summary>
        public Action OnGlossary { get; set; }

        /// <summary>Open settings.</summary>
        public Action OnSettings { get; set; }

        /// <summary>Return to the title screen.</summary>
        public Action OnQuitToMenu { get; set; }

        /// <summary>Leave the application.</summary>
        public Action OnQuitGame { get; set; }

        public string ScreenId => Id;

        public bool IsModal => true;

        /// <summary>True while the quit confirmation is up.</summary>
        public bool AwaitingConfirmation { get; private set; }

        // ---------------------------------------------------------------- construction

        /// <summary>Creates the pause menu on ModalLayer, hidden.</summary>
        public static PauseMenuScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ModalLayer");
            var go = new GameObject("PauseMenuScreen", typeof(RectTransform));
            var menu = go.AddComponent<PauseMenuScreen>();
            UIBuilder.Safe("PauseMenuScreen.Build", () => menu.Build(layer));
            return menu;
        }

        private void Build(Transform parent)
        {
            root = transform as RectTransform;
            root.SetParent(parent, false);
            UIBuilder.Stretch(root);

            var dim = root.gameObject.AddComponent<Image>();
            dim.color = UiStyle.ModalDim;
            dim.raycastTarget = true;

            panel = UIBuilder.CreateNineSlicePanel("Panel", root, UiStyle.Panel).rectTransform;
            UIBuilder.SetRect(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(620f, 720f));

            titleLabel = UIBuilder.CreateLabel("ui.menu.title", panel, UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(titleLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              Vector2.zero, new Vector2(0f, 96f));
            titleLabel.alignment = TextAlignmentOptions.Midline;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.raycastTarget = false;

            var column = UIBuilder.CreateRect("Column", panel);
            UIBuilder.SetRect(column, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(-48f, -140f));
            UIBuilder.SetLayoutVertical(column, 12f, 0f, false);

            buttons.Add(AddRow(column, "ui.menu.resume", Resume));
            buttons.Add(AddRow(column, "ui.menu.save", () => Route(OnSaveRequested, SaveLoadScreen.Id, SaveLoadMode.Save)));
            buttons.Add(AddRow(column, "ui.menu.load", () => Route(OnLoadRequested, SaveLoadScreen.Id, SaveLoadMode.Load)));
            buttons.Add(AddRow(column, "ui.menu.backlog", () => Route(OnBacklog, BacklogScreen.Id, null)));
            buttons.Add(AddRow(column, "ui.menu.glossary", () => Route(OnGlossary, GlossaryScreen.Id, null)));
            buttons.Add(AddRow(column, "ui.menu.settings", () => Route(OnSettings, SettingsScreen.Id, null)));
            buttons.Add(AddRow(column, "ui.menu.quit_to_menu", () => AskConfirmation(OnQuitToMenu)));
            buttons.Add(AddRow(column, "ui.menu.quit_game", () => AskConfirmation(QuitApplication)));

            BuildConfirmation();
            UIBuilder.ChainNavigation(buttons);
            root.gameObject.SetActive(false);
        }

        private Button AddRow(Transform parent, string key, Action onClick)
        {
            var button = UIBuilder.CreateButton(key, parent, () => UIBuilder.Safe("pause." + key, onClick));
            var le = button.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = UiStyle.ButtonHeight;
            return button;
        }

        private void BuildConfirmation()
        {
            confirmBlock = UIBuilder.CreateNineSlicePanel("Confirm", panel, UiStyle.PanelRaised).gameObject;
            var rt = confirmBlock.transform as RectTransform;
            UIBuilder.SetRect(rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 24f), new Vector2(560f, 240f));

            var text = UIBuilder.CreateLabel("ui.menu.confirm_quit", rt.transform, UiStyle.LabelFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(text.rectTransform, new Vector2(0f, 0.45f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, Vector2.zero);
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;

            var yes = UIBuilder.CreateButton("ui.common.yes", rt.transform, () => RunPending());
            UIBuilder.SetRect(yes.GetComponent<RectTransform>(), new Vector2(0f, 0.05f), new Vector2(0.5f, 0.35f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-16f, 0f));
            var no = UIBuilder.CreateButton("ui.common.no", rt.transform, () => CancelConfirmation());
            UIBuilder.SetRect(no.GetComponent<RectTransform>(), new Vector2(0.5f, 0.05f), new Vector2(1f, 0.35f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-16f, 0f));
            foreach (var b in new[] { yes, no })
            {
                var img = b.targetGraphic as Image;
                if (img != null) img.color = UiStyle.PanelActive;
            }
            yes.GetComponentInChildren<TMP_Text>().color = UiStyle.Text;
            confirmBlock.SetActive(false);
        }

        // ---------------------------------------------------------------- behaviour

        /// <summary>Shows the menu over the scene.</summary>
        public void Show(object payload = null)
        {
            if (root == null) UIBuilder.Safe("PauseMenuScreen.Build", () => Build(UIBuilder.GetLayer("ModalLayer")));
            CancelConfirmation();
            root.gameObject.SetActive(true);
            SetCapture(true);
            if (buttons.Count > 0) UIBuilder.Focus(buttons[0]);
        }

        /// <summary>Hides the menu.</summary>
        public void Hide()
        {
            if (root != null) root.gameObject.SetActive(false);
            CancelConfirmation();
            SetCapture(false);
        }

        private void SetCapture(bool on)
        {
            var input = Input;
            if (input == null) return;
            try
            {
                // When another modal sits below, capture stays on.
                var router = UiRouter.Instance;
                input.CaptureUiOnly = on || (router != null && router.IsModalOpen);
            }
            catch (Exception e) { VNLog.Warn("menu", "CaptureUiOnly failed: " + e.Message); }
        }

        private void Resume()
        {
            var handler = OnResume;
            var router = UiRouter.Instance;
            if (router != null && router.IsOpen(Id)) router.Pop();
            else Hide();
            handler?.Invoke();
        }

        private void Route(Action target, string screenId, object payload)
        {
            if (target != null) { target(); return; }
            var router = UiRouter.Instance;
            if (router != null && router.Open(screenId, payload)) return;
            VNLog.Warn("menu", "Screen unavailable: " + screenId);
        }

        private Action pendingAction;

        private void AskConfirmation(Action action)
        {
            pendingAction = action;
            AwaitingConfirmation = true;
            if (confirmBlock != null) confirmBlock.SetActive(true);
        }

        private void CancelConfirmation()
        {
            pendingAction = null;
            AwaitingConfirmation = false;
            if (confirmBlock != null) confirmBlock.SetActive(false);
        }

        private void RunPending()
        {
            var action = pendingAction;
            CancelConfirmation();
            if (action == null) return;
            UIBuilder.Safe("pause.confirm", action);
        }

        private void QuitApplication()
        {
            var handler = OnQuitGame;
            if (handler != null) { handler(); return; }
            try { Application.Quit(); }
            catch (Exception e) { VNLog.Warn("menu", "Application.Quit failed: " + e.Message); }
        }

        /// <summary>Esc closes the confirmation first, then lets the router pop the menu.</summary>
        public bool EscapePressed()
        {
            if (!AwaitingConfirmation) return false;
            CancelConfirmation();
            return true;
        }

        // ---------------------------------------------------------------- IVNScreen

        void IVNScreen.OnScreenShown(object payload)
        {
            UIBuilder.Safe("pause.show", () => Show(payload));
        }

        void IVNScreen.OnScreenHidden()
        {
            Hide();
        }

        private void OnEnable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged += OnLanguageChanged; } catch (Exception e) { VNLog.Warn("menu", e.Message); }
            }
        }

        private void OnDisable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged -= OnLanguageChanged; } catch (Exception e) { VNLog.Warn("menu", e.Message); }
            }
        }

        private void OnLanguageChanged(VNLanguage language)
        {
            UIBuilder.Safe("pause.relocalize", UIBuilder.Relocalize);
        }

        private void OnDestroy()
        {
            OnResume = null;
            OnSaveRequested = null;
            OnLoadRequested = null;
            OnBacklog = null;
            OnGlossary = null;
            OnSettings = null;
            OnQuitToMenu = null;
            OnQuitGame = null;
            pendingAction = null;
            buttons.Clear();
        }
    }
}
