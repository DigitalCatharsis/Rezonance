// REZONANCE VN ENGINE - in-game dictionary (UI module).
// Terms come from the importer tail of the act (VNScriptData.glossary). Rows are pooled, the search
// box filters term and definition without rebuilding the list.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Searchable glossary list.</summary>
    [DisallowMultipleComponent]
    public sealed class GlossaryScreen : MonoBehaviour, IVNScreen
    {
        /// <summary>Router id.</summary>
        public const string Id = "glossary";

        private const string Area = "glossary";

        private readonly List<Row> rows = new List<Row>();
        private readonly List<VNGlossaryEntry> filtered = new List<VNGlossaryEntry>();

        private RectTransform root;
        private RectTransform content;
        private ScrollRect scroll;
        private TextMeshProUGUI titleLabel;
        private TextMeshProUGUI emptyLabel;
        private TMP_InputField searchField;
        private Button closeButton;
        private string query = string.Empty;

        /// <summary>Text source.</summary>
        public ILocalizer Localizer { get; set; }

        /// <summary>Data registry the glossary is read from.</summary>
        public VNDataRegistry Registry { get; set; }

        /// <summary>Optional explicit source; wins over Registry.</summary>
        public Func<IReadOnlyList<VNGlossaryEntry>> Source { get; set; }

        /// <summary>Called when the player clicks a term (highlight in the scene, open a related node).</summary>
        public Action<VNGlossaryEntry> OnTermClicked { get; set; }

        public string ScreenId => Id;

        public bool IsModal => true;

        private sealed class Row
        {
            public GameObject gameObject;
            public TextMeshProUGUI term;
            public TextMeshProUGUI definition;
            public Button button;
            public VNGlossaryEntry entry;
        }

        // ---------------------------------------------------------------- construction

        /// <summary>Creates the screen on ModalLayer, hidden.</summary>
        public static GlossaryScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ModalLayer");
            var go = new GameObject("GlossaryScreen", typeof(RectTransform));
            var screen = go.AddComponent<GlossaryScreen>();
            UIBuilder.Safe("GlossaryScreen.Build", () => screen.Build(layer));
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
            UIBuilder.Stretch(panel.rectTransform, 240f, 240f, 60f, 60f);

            titleLabel = UIBuilder.CreateLabel("ui.glossary.title", panel.transform, UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(titleLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -8f), new Vector2(-380f, 72f));
            titleLabel.alignment = TextAlignmentOptions.MidlineLeft;
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.raycastTarget = false;

            closeButton = UIBuilder.CreateButton("ui.common.close", panel.transform, CloseRequested);
            UIBuilder.SetRect(closeButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f),
                              new Vector2(1f, 1f), new Vector2(-16f, -12f), new Vector2(300f, UiStyle.ButtonHeight));

            searchField = BuildSearch(panel.transform);

            var area = UIBuilder.CreateRect("Area", panel.transform);
            UIBuilder.SetRect(area, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              new Vector2(0f, -150f), new Vector2(-48f, -220f));
            scroll = UIBuilder.CreateScrollList("Scroll", area, out content);
            scroll.scrollSensitivity = 60f;

            emptyLabel = UIBuilder.CreateLabel("ui.glossary.empty", content, UiStyle.LabelFontSize, UiStyle.TextDim, true);
            emptyLabel.alignment = TextAlignmentOptions.Center;
            var le = emptyLabel.gameObject.AddComponent<LayoutElement>();
            le.minHeight = UiStyle.MinTouchSize;
            le.preferredHeight = 120f;

            root.gameObject.SetActive(false);
        }

        private TMP_InputField BuildSearch(Transform parent)
        {
            var rt = UIBuilder.CreateNineSlicePanel("Search", parent, UiStyle.PanelRaised);
            UIBuilder.SetRect(rt.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -88f), new Vector2(-32f, UiStyle.MinTouchSize + 8f));

            var label = UIBuilder.CreateLabel("ui.glossary.search", rt.transform, UiStyle.CaptionFontSize, UiStyle.TextDim, false);
            UIBuilder.SetRect(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -4f), new Vector2(-24f, 26f));
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;

            var textArea = UIBuilder.CreateRect("Text Area", rt.transform);
            UIBuilder.SetRect(textArea, new Vector2(0f, 0f), new Vector2(1f, 0.62f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(-24f, 0f));
            textArea.gameObject.AddComponent<RectMask2D>();

            var text = UIBuilder.CreateText(textArea.transform, "Text", UiStyle.LabelFontSize, UiStyle.Text, false);
            UIBuilder.Stretch(text.rectTransform, 8f, 8f, 4f, 4f);
            text.alignment = TextAlignmentOptions.MidlineLeft;

            var placeholder = UIBuilder.CreateLabel("ui.glossary.search", textArea.transform,
                                                    UiStyle.LabelFontSize, UiStyle.Disabled, false);
            UIBuilder.Stretch(placeholder.rectTransform, 8f, 8f, 4f, 4f);
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
            placeholder.raycastTarget = false;

            var field = rt.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = rt;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.caretColor = UiStyle.Accent;
            field.customCaretColor = true;
            field.characterLimit = 64;
            field.onValueChanged.AddListener(text2 => UIBuilder.Safe("glossary.search", () => ApplyFilter(text2)));
            return field;
        }

        private Row EnsureRow(int index)
        {
            if (index < rows.Count) return rows[index];

            var surface = UIBuilder.CreateNineSlicePanel("Term_" + index, content, UiStyle.PanelRaised);
            var row = new Row { gameObject = surface.gameObject };

            var rt = surface.rectTransform;
            var layout = UIBuilder.SetLayoutVertical(rt, 4f, 20f, true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            row.term = UIBuilder.CreateText(rt, "Term", UiStyle.LabelFontSize, UiStyle.AccentWarm, false);
            row.term.fontStyle = FontStyles.Bold;
            row.term.overflowMode = TextOverflowModes.Shrink;
            var termLe = row.term.gameObject.AddComponent<LayoutElement>();
            termLe.minHeight = 34f;
            termLe.preferredHeight = 38f;

            row.definition = UIBuilder.CreateText(rt, "Definition", UiStyle.CaptionFontSize + 2f, UiStyle.Text, true);
            row.definition.alignment = TextAlignmentOptions.TopLeft;
            var defLe = row.definition.gameObject.AddComponent<LayoutElement>();
            defLe.flexibleHeight = 1f;
            defLe.minHeight = 40f;

            row.button = rt.gameObject.AddComponent<Button>();
            row.button.targetGraphic = surface;
            row.button.colors = UiStyle.ButtonColors(UiStyle.PanelRaised);
            var captured = index;
            row.button.onClick.AddListener(() => UIBuilder.Safe("glossary." + captured, () => Activate(captured)));

            rows.Add(row);
            return row;
        }

        // ---------------------------------------------------------------- data

        /// <summary>Raw glossary list from the injected source or the registry.</summary>
        public IReadOnlyList<VNGlossaryEntry> Entries()
        {
            try
            {
                if (Source != null) return Source();
                var data = Registry != null ? Registry.Data : null;
                return data != null ? data.glossary : null;
            }
            catch (Exception e)
            {
                VNLog.Error(Area, "Glossary source failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Re-reads the source and re-applies the filter.</summary>
        public void Refresh()
        {
            if (root == null) return;
            filtered.Clear();
            var entries = Entries();
            if (entries != null)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;
                    if (Matches(e)) filtered.Add(e);
                }
            }

            emptyLabel.gameObject.SetActive(filtered.Count == 0);
            for (var i = 0; i < filtered.Count; i++)
            {
                var row = EnsureRow(i);
                row.gameObject.SetActive(true);
                row.entry = filtered[i];
                row.term.text = filtered[i].term ?? string.Empty;
                row.definition.text = filtered[i].definition ?? string.Empty;
            }
            for (var i = filtered.Count; i < rows.Count; i++) rows[i].gameObject.SetActive(false);

            UIBuilder.Rebuild(content);
            if (scroll != null) { Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 1f; }
        }

        private bool Matches(VNGlossaryEntry entry)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return Contains(entry.term, query) || Contains(entry.definition, query);
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyFilter(string value)
        {
            query = value ?? string.Empty;
            Refresh();
        }

        private void Activate(int index)
        {
            if (index < 0 || index >= rows.Count) return;
            var entry = rows[index] != null ? rows[index].entry : null;
            var handler = OnTermClicked;
            if (handler == null || entry == null) return;
            UIBuilder.Safe("glossary.activate", () => handler(entry));
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
            UIBuilder.Safe("glossary.show", Show);
        }

        void IVNScreen.OnScreenHidden()
        {
            Hide();
        }

        /// <summary>Shows the dictionary; a payload string prefills the search box.</summary>
        public void Show(object payload = null)
        {
            if (root == null) UIBuilder.Safe("GlossaryScreen.Build", () => Build(UIBuilder.GetLayer("ModalLayer")));
            root.gameObject.SetActive(true);
            var pre = payload as string;
            if (pre != null)
            {
                query = pre;
                if (searchField != null) searchField.SetTextWithoutNotify(pre);
            }
            else if (searchField != null && string.IsNullOrEmpty(searchField.text))
            {
                query = string.Empty;
            }
            Refresh();
            if (closeButton != null) UIBuilder.Focus(closeButton);
        }

        /// <summary>Hides the dictionary.</summary>
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
            UIBuilder.Safe("glossary.relocalize", () =>
            {
                UIBuilder.Relocalize();
                Refresh();
            });
        }

        private void OnDestroy()
        {
            Source = null;
            OnTermClicked = null;
            rows.Clear();
            filtered.Clear();
        }
    }
}
