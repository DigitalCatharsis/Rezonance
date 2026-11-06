// REZONANCE VN ENGINE - title screen (UI module).
// New Game / Continue / Load / Settings / Language / Quit plus the build version. Every string comes
// from the ui.main.* keys of ARCHITECTURE.md 9.8.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Start screen of the VN.</summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuScreen : MonoBehaviour, IVNScreen
    {
        /// <summary>Router id.</summary>
        public const string Id = "main_menu";

        private readonly List<Button> buttons = new List<Button>();

        private RectTransform root;
        private TextMeshProUGUI titleLabel;
        private TextMeshProUGUI versionLabel;
        private Button continueButton;
        private Button languageButton;
        private TextMeshProUGUI languageValue;

        /// <summary>Save access used to enable Continue.</summary>
        public ISaveService Saves { get; set; }

        /// <summary>Settings used to persist the chosen language.</summary>
        public ISettingsStore Settings { get; set; }

        /// <summary>Text source.</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Player asked to start a new run.</summary>
        public Action OnNewGame { get; set; }

        /// <summary>Player asked to resume the newest save.</summary>
        public Action<VNGameState> OnContinue { get; set; }

        /// <summary>Player asked for the load screen.</summary>
        public Action OnLoadRequested { get; set; }

        /// <summary>Player asked for settings.</summary>
        public Action OnSettingsRequested { get; set; }

        /// <summary>Player asked to quit the application.</summary>
        public Action OnQuit { get; set; }

        public string ScreenId => Id;

        public bool IsModal => false;

        // ---------------------------------------------------------------- construction

        /// <summary>Creates the menu on ModalLayer (or a custom parent) and keeps it hidden.</summary>
        public static MainMenuScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ModalLayer");
            var go = new GameObject("MainMenuScreen", typeof(RectTransform));
            var menu = go.AddComponent<MainMenuScreen>();
            UIBuilder.Safe("MainMenuScreen.Build", () => menu.Build(layer));
            return menu;
        }

        private void Build(Transform parent)
        {
            root = transform as RectTransform;
            root.SetParent(parent, false);
            UIBuilder.Stretch(root);

            var bg = root.gameObject.AddComponent<Image>();
            bg.color = UiStyle.Background;
            bg.raycastTarget = true;

            titleLabel = UIBuilder.CreateLabel("ui.main.title", root, UiStyle.TitleFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(titleLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -140f), new Vector2(1400f, 120f));
            titleLabel.alignment = TextAlignmentOptions.Center;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.raycastTarget = false;

            var column = UIBuilder.CreateRect("Column", root);
            UIBuilder.SetRect(column, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              new Vector2(0f, -60f), new Vector2(UiStyle.ButtonWidth, 520f));
            UIBuilder.SetLayoutVertical(column, 14f, 0f, false);

            buttons.Add(AddRow(column, "ui.main.new_game", () => OnNewGame?.Invoke()));
            continueButton = AddRow(column, "ui.main.continue", OnContinueClicked);
            buttons.Add(continueButton);
            buttons.Add(AddRow(column, "ui.main.load", () =>
            {
                if (OnLoadRequested != null) OnLoadRequested();
                else UiRouter.Instance?.Open(SaveLoadScreen.Id, SaveLoadMode.Load);
            }));
            buttons.Add(AddRow(column, "ui.main.settings", () =>
            {
                if (OnSettingsRequested != null) OnSettingsRequested();
                else UiRouter.Instance?.Open(SettingsScreen.Id);
            }));
            languageButton = AddRow(column, "ui.main.language", ToggleLanguage);
            languageValue = UIBuilder.CreateText(languageButton.transform, "Value", UiStyle.LabelFontSize, UiStyle.Accent, false);
            UIBuilder.SetRect(languageValue.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                              new Vector2(-24f, 0f), new Vector2(120f, 0f));
            languageValue.alignment = TextAlignmentOptions.MidlineRight;
            languageValue.raycastTarget = false;
            buttons.Add(languageButton);
            buttons.Add(AddRow(column, "ui.main.quit", Quit));

            versionLabel = UIBuilder.CreateLabel("ui.main.version", root, UiStyle.CaptionFontSize, UiStyle.TextDim, false);
            UIBuilder.SetRect(versionLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 48f), new Vector2(900f, 40f));
            versionLabel.alignment = TextAlignmentOptions.Midline;
            versionLabel.raycastTarget = false;

            if (UIBuilder.Localizer == null) UIBuilder.Localizer = Localizer;
            root.gameObject.SetActive(false);
        }

        private Button AddRow(Transform parent, string key, Action onClick)
        {
            var button = UIBuilder.CreateButton(key, parent, () => UIBuilder.Safe("menu." + key, onClick));
            var le = button.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = UiStyle.ButtonHeight;
            var text = button.GetComponentInChildren<TMP_Text>();
            if (text != null) text.alignment = TextAlignmentOptions.MidlineLeft;
            return button;
        }

        // ---------------------------------------------------------------- state

        /// <summary>Shows the menu and refreshes Continue, the language row and the version.</summary>
        public void Show(object payload = null)
        {
            EnsureBuilt();
            root.gameObject.SetActive(true);
            Refresh();
            if (buttons.Count > 0) UIBuilder.Focus(buttons[0]);
        }

        /// <summary>Hides the menu.</summary>
        public void Hide()
        {
            if (root != null) root.gameObject.SetActive(false);
        }

        private void EnsureBuilt()
        {
            if (root != null) return;
            UIBuilder.Safe("MainMenuScreen.Build", () => Build(UIBuilder.GetLayer("ModalLayer")));
        }

        /// <summary>Recomputes the enabled state of Continue and the language caption.</summary>
        public void Refresh()
        {
            var hasSave = HasAnySave();
            if (continueButton != null) continueButton.interactable = hasSave;

            if (languageValue != null)
                languageValue.text = CurrentLanguage() == VNLanguage.En ? "EN" : "RU";

            if (versionLabel != null)
            {
                UIBuilder.UnbindLocalized(versionLabel);
                versionLabel.text = UIBuilder.T("ui.main.version") + ": " + SafeVersion();
            }
        }

        /// <summary>Build version, defensive against a null string on exotic platforms.</summary>
        public static string SafeVersion()
        {
            try
            {
                var v = Application.version;
                return string.IsNullOrEmpty(v) ? "dev" : v;
            }
            catch (Exception e)
            {
                VNLog.Warn("menu", "Application.version failed: " + e.Message);
                return "dev";
            }
        }

        private VNLanguage CurrentLanguage()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null) return loc.Language;
            return Settings != null ? Settings.Current.language : VNLanguage.Ru;
        }

        private void ToggleLanguage()
        {
            var next = CurrentLanguage() == VNLanguage.En ? VNLanguage.Ru : VNLanguage.En;
            ApplyLanguage(next);
        }

        /// <summary>Switches the language everywhere and stores it in settings.</summary>
        public void ApplyLanguage(VNLanguage language)
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            try { loc?.SetLanguage(language); }
            catch (Exception e) { VNLog.Error("menu", "SetLanguage failed: " + e.Message); }

            try
            {
                var store = Settings as Services.SettingsStore;
                if (store != null) store.Update(s => s.language = language);
                else VNLog.Info("menu", "Settings store is not a SettingsStore - language not persisted");
            }
            catch (Exception e) { VNLog.Error("menu", "Language persistence failed: " + e.Message); }

            UIBuilder.Relocalize();
            Refresh();
        }

        private void OnContinueClicked()
        {
            var handler = OnContinue;
            if (handler == null || Saves == null)
            {
                VNLog.Info("menu", "Continue has no handler bound");
                return;
            }
            if (!Saves.TryLoad(Saves.AutosaveSlotId, out var state))
                if (!Saves.TryLoad(Saves.QuicksaveSlotId, out state))
                {
                    var newest = NewestManualSlot();
                    if (newest == null || !Saves.TryLoad(newest, out state))
                    {
                        ToastService.Notify("ui.toast.load_failed");
                        return;
                    }
                }
            handler(state);
        }

        /// <summary>True when at least one save file exists - drives the Continue button.</summary>
        public bool HasAnySave()
        {
            if (Saves == null) return false;
            try
            {
                var slots = Saves.ListSlots();
                if (slots == null) return false;
                for (var i = 0; i < slots.Count; i++)
                    if (slots[i] != null && slots[i].exists) return true;
            }
            catch (Exception e) { VNLog.Warn("menu", "ListSlots failed: " + e.Message); }
            return false;
        }

        private string NewestManualSlot()
        {
            if (Saves == null) return null;
            string best = null;
            var bestTicks = 0L;
            var slots = Saves.ListSlots();
            for (var i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null || !s.exists || s.corrupted) continue;
                if (!s.slotId.StartsWith("slot_", StringComparison.Ordinal)) continue;
                if (s.savedAtUtcTicks < bestTicks) continue;
                bestTicks = s.savedAtUtcTicks;
                best = s.slotId;
            }
            return best;
        }

        private void Quit()
        {
            var handler = OnQuit;
            if (handler != null) { handler(); return; }
            try
            {
                Application.Quit();
            }
            catch (Exception e) { VNLog.Warn("menu", "Application.Quit failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- IVNScreen

        void IVNScreen.OnScreenShown(object payload)
        {
            UIBuilder.Safe("menu.show", () => Show(payload));
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
            Refresh();
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
            UIBuilder.Safe("menu.relocalize", Refresh);
        }

        private void OnDestroy()
        {
            OnNewGame = null;
            OnContinue = null;
            OnLoadRequested = null;
            OnSettingsRequested = null;
            OnQuit = null;
            buttons.Clear();
        }
    }
}
