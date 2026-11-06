// REZONANCE VN ENGINE - save and load screen (UI module).
// One screen, two modes. Twelve manual slots plus autosave and quicksave, rendered as reusable cards.
// Corrupted and "from a newer build" rows stay visible so the player can delete them.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Which half of the save/load screen is showing.</summary>
    public enum SaveLoadMode
    {
        /// <summary>Write into a slot.</summary>
        Save = 0,
        /// <summary>Restore from a slot.</summary>
        Load = 1
    }

    /// <summary>Grid of save slots with delete confirmation and keyboard/touch navigation.</summary>
    [DisallowMultipleComponent]
    public sealed class SaveLoadScreen : MonoBehaviour, IVNScreen, IUiEscapeHandler
    {
        /// <summary>Router id.</summary>
        public const string Id = "save_load";

        private const int Columns = 3;

        private readonly List<Card> cards = new List<Card>();

        private RectTransform root;
        private RectTransform panel;
        private RectTransform content;
        private ScrollRect scroll;
        private TextMeshProUGUI titleLabel;
        private TextMeshProUGUI hintLabel;
        private GameObject confirmBlock;
        private Button closeButton;
        private Button deleteButton;
        private SaveLoadMode mode = SaveLoadMode.Load;
        private int focusIndex = -1;
        private int pendingDelete = -1;

        /// <summary>Save access.</summary>
        public ISaveService Saves { get; set; }

        /// <summary>Text source.</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Runtime used to capture the state when writing a save.</summary>
        public IVNRuntime Runtime { get; set; }

        /// <summary>Called with a loaded state; the orchestrator restores it into the runtime.</summary>
        public Action<VNGameState> OnStateChangedLoaded { get; set; }

        /// <summary>Optional override of the save action (slotId, captured state).</summary>
        public Action<string, VNGameState> OnSaveIntoSlot { get; set; }

        /// <summary>Optional override of the delete action (slotId).</summary>
        public Action<string> OnDeleteSlot { get; set; }

        /// <summary>Shown when the screen opens without a payload.</summary>
        public SaveLoadMode DefaultMode = SaveLoadMode.Load;

        public string ScreenId => Id;

        public bool IsModal => true;

        /// <summary>Currently highlighted card, -1 when the list is empty.</summary>
        public int FocusIndex => focusIndex;

        private sealed class Card
        {
            public GameObject gameObject;
            public RectTransform rect;
            public Button button;
            public Image image;
            public TextMeshProUGUI name;
            public TextMeshProUGUI timestamp;
            public TextMeshProUGUI playtime;
            public TextMeshProUGUI scene;
            public TextMeshProUGUI preview;
            public string slotId;
            public bool exists;
            public bool corrupted;
        }

        // ---------------------------------------------------------------- construction

        /// <summary>Creates the screen on ModalLayer, hidden.</summary>
        public static SaveLoadScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ModalLayer");
            var go = new GameObject("SaveLoadScreen", typeof(RectTransform));
            var screen = go.AddComponent<SaveLoadScreen>();
            UIBuilder.Safe("SaveLoadScreen.Build", () => screen.Build(layer));
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

            panel = UIBuilder.CreateNineSlicePanel("Panel", root, UiStyle.Panel).rectTransform;
            UIBuilder.Stretch(panel, 80f, 80f, 80f, 80f);

            titleLabel = UIBuilder.CreateLabel("ui.save.title", panel, UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(titleLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -16f), new Vector2(-48f, 72f));
            titleLabel.alignment = TextAlignmentOptions.MidlineLeft;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.raycastTarget = false;

            var area = UIBuilder.CreateRect("Area", panel);
            UIBuilder.SetRect(area, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(-48f, -200f));
            scroll = UIBuilder.CreateScrollList("Scroll", area, out content);
            var grid = UIBuilder.SetLayoutGrid(content, new Vector2(480f, 210f), new Vector2(16f, 16f), Columns);
            grid.padding = new RectOffset(4, 4, 4, 4);

            var footer = UIBuilder.CreateRect("Footer", panel);
            UIBuilder.SetRect(footer, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 60f), new Vector2(-48f, 96f));
            var hlg = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 16f;
            hlg.childAlignment = TextAnchor.MiddleRight;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;

            deleteButton = UIBuilder.CreateButton("ui.save.delete", footer, DeleteFocused);
            deleteButton.GetComponent<LayoutElement>().preferredWidth = 320f;
            deleteButton.GetComponent<LayoutElement>().preferredHeight = UiStyle.ButtonHeight;
            closeButton = UIBuilder.CreateButton("ui.common.close", footer, CloseRequested);
            closeButton.GetComponent<LayoutElement>().preferredWidth = 320f;
            closeButton.GetComponent<LayoutElement>().preferredHeight = UiStyle.ButtonHeight;

            hintLabel = UIBuilder.CreateLabel("ui.game.choices_hint", panel, UiStyle.CaptionFontSize, UiStyle.TextDim, true);
            UIBuilder.SetRect(hintLabel.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f),
                              new Vector2(24f, 60f), new Vector2(700f, 96f));
            hintLabel.alignment = TextAlignmentOptions.MidlineLeft;
            hintLabel.raycastTarget = false;
            hintLabel.gameObject.SetActive(false); // no canonical hint key for this screen

            BuildConfirmation();
            root.gameObject.SetActive(false);
        }

        private Card EnsureCard(int index)
        {
            if (index < cards.Count) return cards[index];

            var surface = UIBuilder.CreateNineSlicePanel("Slot_" + index, content, UiStyle.PanelRaised);
            var card = new Card
            {
                gameObject = surface.gameObject,
                rect = surface.rectTransform,
                image = surface
            };

            card.name = UIBuilder.CreateText(surface.transform, "SlotName", UiStyle.LabelFontSize, UiStyle.AccentWarm, false);
            UIBuilder.SetRect(card.name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -8f), new Vector2(-24f, 40f));
            card.name.alignment = TextAlignmentOptions.MidlineLeft;
            card.name.overflowMode = TextOverflowModes.Shrink;

            card.timestamp = UIBuilder.CreateText(surface.transform, "Timestamp", UiStyle.CaptionFontSize, UiStyle.TextDim, false);
            UIBuilder.SetRect(card.timestamp.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                              new Vector2(-12f, -8f), new Vector2(360f, 40f));
            card.timestamp.alignment = TextAlignmentOptions.MidlineRight;

            card.scene = UIBuilder.CreateText(surface.transform, "Scene", UiStyle.CaptionFontSize, UiStyle.Text, false);
            UIBuilder.SetRect(card.scene.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -52f), new Vector2(-24f, 34f));
            card.scene.alignment = TextAlignmentOptions.MidlineLeft;
            card.scene.overflowMode = TextOverflowModes.Ellipsis;

            card.playtime = UIBuilder.CreateText(surface.transform, "Playtime", UiStyle.CaptionFontSize, UiStyle.TextDim, false);
            UIBuilder.SetRect(card.playtime.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -84f), new Vector2(-24f, 30f));
            card.playtime.alignment = TextAlignmentOptions.MidlineLeft;

            card.preview = UIBuilder.CreateText(surface.transform, "Preview", UiStyle.CaptionFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(card.preview.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              new Vector2(0f, -44f), new Vector2(-28f, -140f));
            card.preview.alignment = TextAlignmentOptions.TopLeft;
            card.preview.overflowMode = TextOverflowModes.Ellipsis;

            foreach (var t in new[] { card.name, card.timestamp, card.scene, card.playtime, card.preview })
                t.raycastTarget = false;

            card.button = surface.gameObject.AddComponent<Button>();
            card.button.targetGraphic = surface;
            card.button.colors = UiStyle.ButtonColors(UiStyle.PanelRaised);
            var captured = index;
            card.button.onClick.AddListener(() => UIBuilder.Safe("slot." + captured, () => Activate(captured)));

            cards.Add(card);
            return card;
        }

        private void BuildConfirmation()
        {
            confirmBlock = UIBuilder.CreateNineSlicePanel("Confirm", root, UiStyle.PanelRaised).gameObject;
            var rt = confirmBlock.transform as RectTransform;
            UIBuilder.SetRect(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(760f, 320f));

            var text = UIBuilder.CreateLabel("ui.save.confirm_delete", rt.transform, UiStyle.LabelFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(text.rectTransform, new Vector2(0f, 0.45f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, Vector2.zero);
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;

            var yes = UIBuilder.CreateButton("ui.common.yes", rt.transform, ConfirmDelete);
            UIBuilder.SetRect(yes.GetComponent<RectTransform>(), new Vector2(0f, 0.05f), new Vector2(0.5f, 0.35f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-24f, 0f));
            var no = UIBuilder.CreateButton("ui.common.no", rt.transform, CancelConfirmation);
            UIBuilder.SetRect(no.GetComponent<RectTransform>(), new Vector2(0.5f, 0.05f), new Vector2(1f, 0.35f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-24f, 0f));
            confirmBlock.SetActive(false);
        }

        // ---------------------------------------------------------------- population

        /// <summary>Rebuilds every card from the save service.</summary>
        public void Refresh()
        {
            if (root == null) return;
            var service = Saves;
            if (service == null)
            {
                titleLabel.text = UIBuilder.T("ui.save.title");
                for (var i = 0; i < cards.Count; i++) cards[i].gameObject.SetActive(false);
                hintLabel.text = UIBuilder.T("ui.save.empty");
                return;
            }

            IReadOnlyList<SaveSlotInfo> slots;
            try { slots = service.ListSlots(); }
            catch (Exception e)
            {
                VNLog.Error("save", "ListSlots failed: " + e.Message);
                slots = new List<SaveSlotInfo>();
            }

            for (var i = 0; i < slots.Count; i++) Bind(EnsureCard(i), slots[i], i);
            for (var i = slots.Count; i < cards.Count; i++) cards[i].gameObject.SetActive(false);
            WireNavigation();

            UIBuilder.Rebuild(content);
            if (focusIndex < 0 || focusIndex >= slots.Count) SetFocus(0);
            else HighlightFocus();
        }

        // Cards are created lazily, so the grid links are wired in a second pass once every row exists.
        private void WireNavigation()
        {
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card == null || card.button == null) continue;
                var nav = card.button.navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnDown = i + Columns < cards.Count ? cards[i + Columns].button : null;
                nav.selectOnUp = i - Columns >= 0 ? cards[i - Columns].button : null;
                nav.selectOnRight = (i % Columns) < Columns - 1 && i + 1 < cards.Count ? cards[i + 1].button : null;
                nav.selectOnLeft = (i % Columns) > 0 ? cards[i - 1].button : null;
                card.button.navigation = nav;
            }
        }

        private void Bind(Card card, SaveSlotInfo info, int index)
        {
            card.gameObject.SetActive(true);
            card.slotId = info.slotId;
            card.exists = info.exists;
            card.corrupted = info.corrupted;

            card.name.text = string.IsNullOrEmpty(info.displayName) ? info.slotId : info.displayName;

            if (!info.exists)
            {
                card.timestamp.text = string.Empty;
                card.playtime.text = string.Empty;
                card.scene.text = string.Empty;
                card.preview.text = UIBuilder.T("ui.save.empty");
                card.preview.color = UiStyle.TextDim;
            }
            else
            {
                card.timestamp.text = FormatTimestamp(info.savedAtUtcTicks);
                card.playtime.text = UIBuilder.T("ui.save.playtime") + " " + FormatPlaytime(info.playtimeSeconds);
                card.scene.text = string.IsNullOrEmpty(info.sceneTitle) ? string.Empty : info.sceneTitle;
                card.preview.text = string.IsNullOrEmpty(info.previewText) ? info.slotId : info.previewText;
                card.preview.color = info.corrupted ? UiStyle.Danger : UiStyle.Text;
            }
        }

        /// <summary>Local, culture-neutral timestamp; a zero tick count renders nothing.</summary>
        public static string FormatTimestamp(long utcTicks)
        {
            if (utcTicks <= 0) return string.Empty;
            try
            {
                var local = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
                return local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                VNLog.Warn("save", "Timestamp format failed: " + e.Message);
                return string.Empty;
            }
        }

        /// <summary>h:mm:ss playtime.</summary>
        public static string FormatPlaytime(float seconds)
        {
            if (seconds <= 0f) return "0:00";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1f
                ? ((int)ts.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + ts.Minutes.ToString("00", CultureInfo.InvariantCulture)
                : ts.Minutes.ToString(CultureInfo.InvariantCulture) + ":" + ts.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- interaction

        /// <summary>Activates a card: saves into it, loads from it or refuses a broken row.</summary>
        public void Activate(int index)
        {
            if (index < 0 || index >= cards.Count) return;
            var card = cards[index];
            if (card == null || string.IsNullOrEmpty(card.slotId)) return;
            SetFocus(index);

            if (mode == SaveLoadMode.Save) { SaveInto(card.slotId); return; }
            LoadFrom(card.slotId, card.corrupted);
        }

        private void SaveInto(string slotId)
        {
            VNGameState state = null;
            try { state = Runtime != null ? Runtime.CaptureState() : null; }
            catch (Exception e) { VNLog.Error("save", "CaptureState failed: " + e.Message); }

            if (state == null)
            {
                VNLog.Warn("save", "Nothing to save - the runtime gave no state");
                ToastService.Notify("ui.toast.save_failed");
                return;
            }

            var overrideHandler = OnSaveIntoSlot;
            if (overrideHandler != null) { overrideHandler(slotId, state); Refresh(); return; }

            var ok = false;
            try { ok = Saves.Save(slotId, state); }
            catch (Exception e) { VNLog.Error("save", "Save failed: " + e.Message); }
            ToastService.Notify(ok ? "ui.toast.saved" : "ui.toast.save_failed");
            Refresh();
        }

        private void LoadFrom(string slotId, bool corrupted)
        {
            if (corrupted)
            {
                ToastService.Notify("ui.toast.load_failed");
                return;
            }
            VNGameState state;
            try
            {
                if (Saves == null || !Saves.TryLoad(slotId, out state))
                {
                    ToastService.Notify("ui.toast.load_failed");
                    return;
                }
            }
            catch (Exception e)
            {
                VNLog.Error("save", "TryLoad failed: " + e.Message);
                ToastService.Notify("ui.toast.load_failed");
                return;
            }

            var handler = OnStateChangedLoaded;
            if (handler == null)
            {
                VNLog.Warn("save", "No load handler bound - the state was read but not applied");
                ToastService.Notify("ui.toast.load_failed");
                return;
            }
            handler(state);
            ToastService.Notify("ui.toast.loaded");
            CloseRequested();
        }

        private void DeleteFocused()
        {
            if (focusIndex < 0 || focusIndex >= cards.Count) return;
            var card = cards[focusIndex];
            if (card == null || !card.exists) return;
            pendingDelete = focusIndex;
            AwaitingConfirmation = true;
            if (confirmBlock != null) confirmBlock.SetActive(true);
        }

        /// <summary>True while the delete confirmation is up.</summary>
        public bool AwaitingConfirmation { get; private set; }

        private void ConfirmDelete()
        {
            var index = pendingDelete;
            CancelConfirmation();
            if (index < 0 || index >= cards.Count) return;
            var slotId = cards[index] != null ? cards[index].slotId : null;
            if (string.IsNullOrEmpty(slotId)) return;

            var handler = OnDeleteSlot;
            if (handler != null) { handler(slotId); Refresh(); return; }
            try
            {
                if (Saves != null && Saves.Delete(slotId)) ToastService.Notify("ui.toast.deleted");
                else ToastService.Notify("ui.toast.load_failed");
            }
            catch (Exception e) { VNLog.Error("save", "Delete failed: " + e.Message); }
            Refresh();
        }

        private void CancelConfirmation()
        {
            pendingDelete = -1;
            AwaitingConfirmation = false;
            if (confirmBlock != null) confirmBlock.SetActive(false);
        }

        /// <summary>Moves the highlight by delta and scrolls it into view.</summary>
        public void MoveFocus(int delta)
        {
            if (cards.Count == 0) return;
            var next = Mathf.Clamp(focusIndex + delta, 0, cards.Count - 1);
            SetFocus(next);
        }

        /// <summary>Highlights one card.</summary>
        public void SetFocus(int index)
        {
            focusIndex = index;
            HighlightFocus();
            if (index >= 0 && index < cards.Count && cards[index] != null) UIBuilder.Focus(cards[index].button);
        }

        private void HighlightFocus()
        {
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card == null || card.image == null) continue;
                var on = i == focusIndex;
                card.image.color = on ? UiStyle.PanelActive : UiStyle.PanelRaised;
                if (card.name != null) card.name.color = on ? UiStyle.Accent : UiStyle.AccentWarm;
            }
        }

        private void CloseRequested()
        {
            var router = UiRouter.Instance;
            if (router != null && router.IsOpen(Id)) router.Pop();
            else Hide();
        }

        /// <summary>Esc cancels the confirmation first, then closes the screen.</summary>
        public bool EscapePressed()
        {
            if (!AwaitingConfirmation) return false;
            CancelConfirmation();
            return true;
        }

        // ---------------------------------------------------------------- IVNScreen

        void IVNScreen.OnScreenShown(object payload)
        {
            UIBuilder.Safe("save.show", () =>
            {
                mode = payload is SaveLoadMode m ? m : DefaultMode;
                titleLabel.text = UIBuilder.T(mode == SaveLoadMode.Save ? "ui.menu.save" : "ui.menu.load");
                if (deleteButton != null) deleteButton.gameObject.SetActive(true);
                Show();
            });
        }

        void IVNScreen.OnScreenHidden()
        {
            Hide();
        }

        /// <summary>Makes the screen visible in a mode and refreshes the rows.</summary>
        public void Show(object payload = null)
        {
            if (root == null) UIBuilder.Safe("SaveLoadScreen.Build", () => Build(UIBuilder.GetLayer("ModalLayer")));
            if (payload is SaveLoadMode m) mode = m;
            titleLabel.text = UIBuilder.T(mode == SaveLoadMode.Save ? "ui.menu.save" : "ui.menu.load");
            root.gameObject.SetActive(true);
            CancelConfirmation();
            Refresh();
        }

        /// <summary>Hides the screen.</summary>
        public void Hide()
        {
            if (root != null) root.gameObject.SetActive(false);
            CancelConfirmation();
        }

        private void OnEnable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged += OnLanguageChanged; } catch (Exception e) { VNLog.Warn("save", e.Message); }
            }
        }

        private void OnDisable()
        {
            var loc = Localizer != null ? Localizer : UIBuilder.Localizer;
            if (loc != null)
            {
                try { loc.LanguageChanged -= OnLanguageChanged; } catch (Exception e) { VNLog.Warn("save", e.Message); }
            }
        }

        private void OnLanguageChanged(VNLanguage language)
        {
            UIBuilder.Safe("save.relocalize", () =>
            {
                UIBuilder.Relocalize();
                Refresh();
                titleLabel.text = UIBuilder.T(mode == SaveLoadMode.Save ? "ui.menu.save" : "ui.menu.load");
            });
        }

        private void OnDestroy()
        {
            OnStateChangedLoaded = null;
            OnSaveIntoSlot = null;
            OnDeleteSlot = null;
            cards.Clear();
        }
    }
}
