// REZONANCE VN ENGINE - reading history (UI module).
// Rows are pooled and only one page (the newest PageSize entries) is materialised, so a 40k line act
// costs nothing to open. "Earlier" grows the window from the top.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Scrollable list of VNBacklogEntry with cell reuse and pagination.</summary>
    [DisallowMultipleComponent]
    public sealed class BacklogScreen : MonoBehaviour, IVNScreen
    {
        /// <summary>Router id.</summary>
        public const string Id = "backlog";

        /// <summary>How many entries are materialised per page.</summary>
        public const int PageSize = 50;

        private const string Area = "backlog";

        private readonly List<Row> rows = new List<Row>();

        private RectTransform root;
        private RectTransform content;
        private ScrollRect scroll;
        private TextMeshProUGUI titleLabel;
        private TextMeshProUGUI emptyLabel;
        private Button earlierButton;
        private Button closeButton;
        private int windowStart;
        private int windowCount;

        /// <summary>Text source.</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Runtime whose Backlog is listed; ignored when Source is set.</summary>
        public IVNRuntime Runtime { get; set; }

        /// <summary>Optional explicit history provider (used by the orchestrator or tests).</summary>
        public Func<IReadOnlyList<VNBacklogEntry>> Source { get; set; }

        /// <summary>Optional click handler for a row (jump to node).</summary>
        public Action<VNBacklogEntry> OnEntryClicked { get; set; }

        public string ScreenId => Id;

        public bool IsModal => true;

        private sealed class Row
        {
            public GameObject gameObject;
            public TextMeshProUGUI speaker;
            public TextMeshProUGUI text;
            public Button button;
            public VNBacklogEntry entry;
        }

        // ---------------------------------------------------------------- construction

        /// <summary>Creates the screen on ModalLayer, hidden.</summary>
        public static BacklogScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ModalLayer");
            var go = new GameObject("BacklogScreen", typeof(RectTransform));
            var screen = go.AddComponent<BacklogScreen>();
            UIBuilder.Safe("BacklogScreen.Build", () => screen.Build(layer));
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
            UIBuilder.Stretch(panel.rectTransform, 200f, 200f, 60f, 60f);

            titleLabel = UIBuilder.CreateLabel("ui.backlog.title", panel.transform, UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(titleLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -8f), new Vector2(-380f, 72f));
            titleLabel.alignment = TextAlignmentOptions.MidlineLeft;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.raycastTarget = false;

            closeButton = UIBuilder.CreateButton("ui.common.close", panel.transform, CloseRequested);
            UIBuilder.SetRect(closeButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f),
                              new Vector2(1f, 1f), new Vector2(-16f, -12f), new Vector2(300f, UiStyle.ButtonHeight));

            var area = UIBuilder.CreateRect("Area", panel.transform);
            UIBuilder.SetRect(area, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(-48f, -180f));
            scroll = UIBuilder.CreateScrollList("Scroll", area, out content);
            scroll.scrollSensitivity = 60f;

            emptyLabel = UIBuilder.CreateLabel("ui.backlog.empty", content, UiStyle.LabelFontSize, UiStyle.TextDim, true);
            emptyLabel.alignment = TextAlignmentOptions.Center;
            var emptyLe = emptyLabel.gameObject.AddComponent<LayoutElement>();
            emptyLe.minHeight = UiStyle.MinTouchSize;
            emptyLe.preferredHeight = 120f;

            earlierButton = UIBuilder.CreateButton("ui.backlog.load_more", content, LoadEarlier);
            var le = earlierButton.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = UiStyle.ButtonHeight;

            root.gameObject.SetActive(false);
        }

        private Row EnsureRow(int index)
        {
            if (index < rows.Count) return rows[index];

            var surface = UIBuilder.CreateNineSlicePanel("Entry_" + index, content, UiStyle.PanelRaised);
            var row = new Row { gameObject = surface.gameObject };

            var rt = surface.rectTransform;
            var rowLayout = UIBuilder.SetLayoutVertical(rt, 4f, 20f, true);
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandHeight = false;

            row.speaker = UIBuilder.CreateText(rt, "Speaker", UiStyle.CaptionFontSize, UiStyle.AccentWarm, false);
            row.speaker.overflowMode = TextOverflowModes.Shrink;
            var speakerLe = row.speaker.gameObject.AddComponent<LayoutElement>();
            speakerLe.minHeight = 32f;
            speakerLe.preferredHeight = 34f;
            speakerLe.flexibleHeight = 0f;

            row.text = UIBuilder.CreateText(rt, "Text", UiStyle.LabelFontSize, UiStyle.Text, true);
            row.text.alignment = TextAlignmentOptions.TopLeft;
            var textLe = row.text.gameObject.AddComponent<LayoutElement>();
            textLe.flexibleHeight = 1f;
            textLe.minHeight = 40f;

            row.button = rt.gameObject.AddComponent<Button>();
            row.button.targetGraphic = surface;
            row.button.colors = UiStyle.ButtonColors(UiStyle.PanelRaised);
            var captured = index;
            row.button.onClick.AddListener(() => UIBuilder.Safe("backlog." + captured, () => Activate(captured)));

            rows.Add(row);
            return row;
        }

        // ---------------------------------------------------------------- population

        /// <summary>Rebuilds the visible window from the history source.</summary>
        public void Refresh()
        {
            if (root == null) return;
            var history = History();
            var total = history != null ? history.Count : 0;

            if (total == 0)
            {
                emptyLabel.gameObject.SetActive(true);
                earlierButton.gameObject.SetActive(false);
                for (var i = 0; i < rows.Count; i++) rows[i].gameObject.SetActive(false);
                windowStart = 0;
                windowCount = 0;
                UIBuilder.Rebuild(content);
                return;
            }

            emptyLabel.gameObject.SetActive(false);
            if (windowCount <= 0)
            {
                windowCount = Mathf.Min(PageSize, total);
                windowStart = total - windowCount;
            }
            windowStart = Mathf.Clamp(windowStart, 0, Mathf.Max(0, total - 1));
            windowCount = Mathf.Clamp(windowCount, 1, total - windowStart);
            earlierButton.gameObject.SetActive(windowStart > 0);

            for (var i = 0; i < windowCount; i++)
            {
                var entry = history[windowStart + i];
                var row = EnsureRow(i);
                row.gameObject.SetActive(true);
                row.entry = entry;
                row.speaker.text = entry == null ? string.Empty : (entry.speakerName ?? string.Empty);
                row.text.text = entry == null ? string.Empty : (entry.text ?? string.Empty);
                row.speaker.gameObject.SetActive(!string.IsNullOrEmpty(row.speaker.text));
            }
            for (var i = windowCount; i < rows.Count; i++) rows[i].gameObject.SetActive(false);

            UIBuilder.Rebuild(content);
            ScrollToBottom();
        }

        private IReadOnlyList<VNBacklogEntry> History()
        {
            try
            {
                if (Source != null) return Source();
                return Runtime != null ? Runtime.Backlog : null;
            }
            catch (Exception e)
            {
                VNLog.Error(Area, "Backlog source failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Extends the window by one page towards the beginning of the history.</summary>
        public void LoadEarlier()
        {
            var total = History()?.Count ?? 0;
            if (windowStart <= 0) return;
            var take = Mathf.Min(PageSize, windowStart);
            windowStart -= take;
            windowCount += take;
            var keepBottom = normalizedPosition();
            Refresh();
            RestoreBottom(keepBottom);
        }

        private float normalizedPosition()
        {
            return scroll != null ? scroll.verticalNormalizedPosition : 0f;
        }

        private void RestoreBottom(float previous)
        {
            if (scroll == null) return;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = Mathf.Clamp01(previous);
        }

        private void ScrollToBottom()
        {
            if (scroll == null) return;
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0f;
        }

        private void Activate(int index)
        {
            if (index < 0 || index >= rows.Count) return;
            var entry = rows[index] != null ? rows[index].entry : null;
            var handler = OnEntryClicked;
            if (handler == null || entry == null) return;
            UIBuilder.Safe("backlog.activate", () => handler(entry));
        }

        private void CloseRequested()
        {
            var router = UiRouter.Instance;
            if (router != null && router.IsOpen(Id)) router.Pop();
            else Hide();
        }

        // ---------------------------------------------------------------- IVNScreen

        void IVNScreen.OnScreenShown(object payload)
        {
            UIBuilder.Safe("backlog.show", Show);
        }

        void IVNScreen.OnScreenHidden()
        {
            Hide();
        }

        /// <summary>Shows the history and scrolls to the newest line.</summary>
        public void Show()
        {
            if (root == null) UIBuilder.Safe("BacklogScreen.Build", () => Build(UIBuilder.GetLayer("ModalLayer")));
            root.gameObject.SetActive(true);
            windowCount = 0;
            Refresh();
            if (closeButton != null) UIBuilder.Focus(closeButton);
        }

        /// <summary>Hides the history.</summary>
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
            UIBuilder.Safe("backlog.relocalize", () =>
            {
                UIBuilder.Relocalize();
                Refresh();
            });
        }

        private void OnDestroy()
        {
            Source = null;
            OnEntryClicked = null;
            rows.Clear();
        }
    }
}
