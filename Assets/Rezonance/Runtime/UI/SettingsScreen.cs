// REZONANCE VN ENGINE - settings screen (UI module).
// Every control writes through ISettingsStore (Apply + Save), so the engine and the persistence layer
// always agree. Keys are the ui.settings.* canon of ARCHITECTURE.md 9.8.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Audio, text timing, language and developer switches.</summary>
    [DisallowMultipleComponent]
    public sealed class SettingsScreen : MonoBehaviour, IVNScreen
    {
        /// <summary>Router id.</summary>
        public const string Id = "settings";

        private const string Area = "settings";

        private readonly List<Button> buttons = new List<Button>();
        private readonly List<Slider> sliders = new List<Slider>();

        private RectTransform root;
        private TextMeshProUGUI titleLabel;
        private SettingsRow masterRow;
        private SettingsRow musicRow;
        private SettingsRow ambienceRow;
        private SettingsRow sfxRow;
        private SettingsRow voiceRow;
        private SettingsRow textSpeedRow;
        private SettingsRow autoDelayRow;
        private SettingsRow readDelayRow;
        private TMP_Dropdown languageDropdown;
        private Toggle fullscreenToggle;
        private Toggle devKeysToggle;
        private bool suppressEvents;

        /// <summary>Settings source; the concrete store is preferred but any implementation works.</summary>
        public ISettingsStore Store { get; set; }

        /// <summary>Text source.</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Optional override of the close action; the router pops the screen by default.</summary>
        public Action OnClose { get; set; }

        public string ScreenId => Id;

        public bool IsModal => true;

        private sealed class SettingsRow
        {
            public Slider slider;
            public TMP_Text value;
            public float min;
            public float max;
            public bool percent;
        }

        // ---------------------------------------------------------------- construction

        /// <summary>Creates the screen on ModalLayer, hidden.</summary>
        public static SettingsScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ModalLayer");
            var go = new GameObject("SettingsScreen", typeof(RectTransform));
            var screen = go.AddComponent<SettingsScreen>();
            UIBuilder.Safe("SettingsScreen.Build", () => screen.Build(layer));
            return screen;
        }

        private void Build(Transform parent)
        {
            root = transform as RectTransform;
            root.SetParent(parent, false);
            UIBuilder.Stretch(root);

            var dim = root.gameObject.AddComponent<Image>();
            dim.color = UiStyle.ModalDim;
            dim.raycastTarget = true;

            var panel = UIBuilder.CreateNineSlicePanel("Panel", root, UiStyle.Panel);
            UIBuilder.Stretch(panel.rectTransform, 240f, 240f, 80f, 80f);

            titleLabel = UIBuilder.CreateLabel("ui.settings.title", panel.transform, UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(titleLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -12f), new Vector2(-48f, 72f));
            titleLabel.alignment = TextAlignmentOptions.MidlineLeft;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.raycastTarget = false;

            var area = UIBuilder.CreateRect("Area", panel.transform);
            UIBuilder.SetRect(area, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(-48f, -220f));
            RectTransform content;
            var scroll = UIBuilder.CreateScrollList("Scroll", area, out content);
            scroll.scrollSensitivity = 60f;

            masterRow = AddSlider(content, "ui.settings.master", 0f, 1f, true, v => s => s.masterVolume = v);
            musicRow = AddSlider(content, "ui.settings.music", 0f, 1f, true, v => s => s.musicVolume = v);
            ambienceRow = AddSlider(content, "ui.settings.ambience", 0f, 1f, true, v => s => s.ambienceVolume = v);
            sfxRow = AddSlider(content, "ui.settings.sfx", 0f, 1f, true, v => s => s.sfxVolume = v);
            voiceRow = AddSlider(content, "ui.settings.voice", 0f, 1f, true, v => s => s.voiceVolume = v);
            textSpeedRow = AddSlider(content, "ui.settings.text_speed", 0f, 200f, false, v => s => s.textSpeedCps = v);
            autoDelayRow = AddSlider(content, "ui.settings.auto_delay", 0.2f, 8f, false, v => s => s.autoNodeDelaySeconds = v);
            readDelayRow = AddSlider(content, "ui.settings.read_delay", 0f, 8f, false, v => s => s.autoReadDelaySeconds = v);

            languageDropdown = UIBuilder.CreateDropdown("ui.settings.language", content,
                new List<string> { "RU", "EN" }, index =>
                {
                    if (suppressEvents) return;
                    ApplyLanguage(index == 1 ? VNLanguage.En : VNLanguage.Ru);
                });

            fullscreenToggle = UIBuilder.CreateToggle("ui.settings.fullscreen", content, on =>
            {
                if (suppressEvents) return;
                Mutate(s => s.fullscreen = on);
            });

            devKeysToggle = UIBuilder.CreateToggle("ui.settings.show_keys", content, on =>
            {
                if (suppressEvents) return;
                Mutate(s => s.devShowKeys = on);
                UIBuilder.Relocalize();
            });

            var reset = UIBuilder.CreateButton("ui.settings.reset", content, ResetDefaults);
            var close = UIBuilder.CreateButton("ui.settings.close", content, CloseRequested);
            buttons.Add(reset);
            buttons.Add(close);
            foreach (var b in buttons)
            {
                var le = b.GetComponent<LayoutElement>();
                if (le != null) le.preferredHeight = UiStyle.ButtonHeight;
            }
            UIBuilder.ChainNavigation(buttons);

            root.gameObject.SetActive(false);
        }

        private SettingsRow AddSlider(Transform parent, string key, float min, float max, bool percent,
                                      Func<float, Action<VNSettings>> bind)
        {
            var row = UIBuilder.CreateSlider(key, parent, value =>
            {
                if (suppressEvents) return;
                Mutate(bind(value));
            });
            row.minValue = min;
            row.maxValue = max;
            row.wholeNumbers = false;

            var rowRt = row.transform as RectTransform;
            // The first text child of a row is the caption, the second one is the read-out.
            var valueLabel = rowRt != null ? rowRt.GetComponentInChildren<TMP_Text>(true) : null;
            var texts = row.GetComponentsInChildren<TMP_Text>(true);
            if (texts != null && texts.Length > 1) valueLabel = texts[1];

            var settingsRow = new SettingsRow
            {
                slider = row,
                value = valueLabel,
                min = min,
                max = max,
                percent = percent
            };
            sliders.Add(row);
            return settingsRow;
        }

        // ---------------------------------------------------------------- state

        /// <summary>Pushes the store values into the widgets without echoing back to the store.</summary>
        public void Refresh()
        {
            var store = Store;
            if (store == null || store.Current == null) return;
            var s = store.Current;
            suppressEvents = true;
            try
            {
                Set(masterRow, s.masterVolume);
                Set(musicRow, s.musicVolume);
                Set(ambienceRow, s.ambienceVolume);
                Set(sfxRow, s.sfxVolume);
                Set(voiceRow, s.voiceVolume);
                Set(textSpeedRow, s.textSpeedCps);
                Set(autoDelayRow, s.autoNodeDelaySeconds);
                Set(readDelayRow, s.autoReadDelaySeconds);

                if (languageDropdown != null) languageDropdown.SetValueWithoutNotify(s.language == VNLanguage.En ? 1 : 0);
                if (fullscreenToggle != null) fullscreenToggle.isOn = s.fullscreen;
                if (devKeysToggle != null) devKeysToggle.isOn = s.devShowKeys;
            }
            finally
            {
                suppressEvents = false;
            }
        }

        private static void Set(SettingsRow row, float value)
        {
            if (row == null || row.slider == null) return;
            row.slider.SetValueWithoutNotify(Mathf.Clamp(value, row.min, row.max));
            if (row.value == null) return;
            row.value.text = row.percent
                ? Mathf.RoundToInt(Mathf.Clamp01(value) * 100f).ToString() + "%"
                : value.ToString("0.##");
        }

        private void Mutate(Action<VNSettings> change)
        {
            var store = Store;
            if (store == null)
            {
                VNLog.Warn(Area, "No settings store bound");
                return;
            }
            try
            {
                var copy = store.Current != null ? store.Current.Clone() : new VNSettings();
                change(copy);
                store.Apply(copy);
                store.Save();
            }
            catch (Exception e)
            {
                VNLog.Error(Area, "Settings update failed: " + e.Message);
            }
            Refresh();
        }

        private void ApplyLanguage(VNLanguage language)
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            try { loc?.SetLanguage(language); }
            catch (Exception e) { VNLog.Error(Area, "SetLanguage failed: " + e.Message); }
            Mutate(s => s.language = language);
        }

        private void ResetDefaults()
        {
            var store = Store;
            if (store == null) return;
            try
            {
                store.ResetToDefaults();
                var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
                if (loc != null) loc.SetLanguage(store.Current.language);
            }
            catch (Exception e) { VNLog.Error(Area, "Reset failed: " + e.Message); }
            Refresh();
            UIBuilder.Relocalize();
            ToastService.Notify("ui.toast.settings_saved");
        }

        private void CloseRequested()
        {
            var handler = OnClose;
            if (handler != null) { handler(); return; }
            var router = UiRouter.Instance;
            if (router != null && router.IsOpen(Id)) router.Pop();
            else Hide();
        }

        // ---------------------------------------------------------------- IVNScreen

        void IVNScreen.OnScreenShown(object payload)
        {
            UIBuilder.Safe("settings.show", () => Show());
        }

        void IVNScreen.OnScreenHidden()
        {
            Hide();
        }

        /// <summary>Shows the screen and syncs widgets with the store.</summary>
        public void Show()
        {
            if (root == null) UIBuilder.Safe("SettingsScreen.Build", () => Build(UIBuilder.GetLayer("ModalLayer")));
            root.gameObject.SetActive(true);
            Refresh();
            if (buttons.Count > 0) UIBuilder.Focus(buttons[0]);
        }

        /// <summary>Hides the screen.</summary>
        public void Hide()
        {
            if (root != null) root.gameObject.SetActive(false);
        }

        private void OnEnable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged += OnLanguageChanged; } catch (Exception e) { VNLog.Warn(Area, e.Message); }
            }
            var store = Store;
            if (store != null)
            {
                try { store.Changed += OnStoreChanged; } catch (Exception e) { VNLog.Warn(Area, e.Message); }
            }
        }

        private void OnDisable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged -= OnLanguageChanged; } catch (Exception e) { VNLog.Warn(Area, e.Message); }
            }
            var store = Store;
            if (store != null)
            {
                try { store.Changed -= OnStoreChanged; } catch (Exception e) { VNLog.Warn(Area, e.Message); }
            }
        }

        private void OnLanguageChanged(VNLanguage language)
        {
            UIBuilder.Safe("settings.relocalize", () =>
            {
                UIBuilder.Relocalize();
                Refresh();
            });
        }

        private void OnStoreChanged(VNSettings settings)
        {
            if (gameObject.activeInHierarchy) UIBuilder.Safe("settings.refresh", Refresh);
        }

        private void OnDestroy()
        {
            OnClose = null;
            buttons.Clear();
            sliders.Clear();
        }
    }
}
