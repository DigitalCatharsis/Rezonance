// REZONANCE VN ENGINE - runtime UI factory (UI module).
// There are no prefabs in the project: every element is built here in code, so the interface needs
// no art and no GUID references. See ARCHITECTURE.md 9.6 for the layer contract and 10 for logging.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Factory for the whole VN interface plus the canvas/EventSystem bootstrap.</summary>
    public static class UIBuilder
    {
        /// <summary>Log area tag.</summary>
        public const string Area = "ui";

        /// <summary>Minimum touch target in reference pixels; every control honours it.</summary>
        public const float MinTouchSize = 44f;

        /// <summary>Root canvas object name (ARCHITECTURE.md 9.6).</summary>
        public const string CanvasName = UiStyle.CanvasName;

        /// <summary>Layer names bottom to top (ARCHITECTURE.md 9.6).</summary>
        public static readonly string[] LayerNames = UiStyle.LayerNames;

        private static Canvas canvas;
        private static readonly Dictionary<string, RectTransform> layers =
            new Dictionary<string, RectTransform>(StringComparer.Ordinal);
        private static readonly Dictionary<TMP_Text, string> localized =
            new Dictionary<TMP_Text, string>(ReferenceComparer<TMP_Text>.Instance);
        private static readonly List<TMP_Text> allTexts = new List<TMP_Text>();
        private static TMP_FontAsset font;
        private static bool fontWarned;
        private static Sprite roundedSprite;
        private static Sprite chevronSprite;

        /// <summary>Text source every label goes through; null-tolerant, falls back to the raw key.</summary>
        public static ILocalizer Localizer { get; set; }

        /// <summary>Created canvas, or null before <see cref="EnsureCanvas"/>.</summary>
        public static Canvas Canvas => canvas;

        /// <summary>Root RectTransform of the canvas.</summary>
        public static RectTransform Root => canvas != null ? canvas.transform as RectTransform : null;

        // ---------------------------------------------------------------- font

        /// <summary>Font asset stamped onto every created text; resolved from TMP_Settings on demand.</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                EnsureFont();
                return font;
            }
            set
            {
                font = value;
                fontWarned = false;
                ApplyFontToAll();
            }
        }

        /// <summary>Reads TMP_Settings.defaultFontAsset once and warns if the project has none yet.</summary>
        public static void EnsureFont()
        {
            if (font != null) return;
            try
            {
                font = TMP_Settings.defaultFontAsset;
            }
            catch (Exception e)
            {
                VNLog.Warn(Area, "TMP_Settings.defaultFontAsset threw: " + e.Message);
                font = null;
            }
            if (font == null && !fontWarned)
            {
                fontWarned = true;
                VNLog.Warn(Area, "No default TMP font asset - text renders with the built-in fallback until the importer assigns one");
            }
        }

        /// <summary>Re-reads the default font (language switch with per-language font assets) and repaints live texts.</summary>
        public static void ReloadFont()
        {
            font = null;
            fontWarned = false;
            EnsureFont();
            ApplyFontToAll();
        }

        private static void ApplyFontToAll()
        {
            EnsureFont();
            if (font == null) return;
            for (var i = allTexts.Count - 1; i >= 0; i--)
            {
                var t = allTexts[i];
                if (t == null) { allTexts.RemoveAt(i); continue; }
                t.font = font;
                t.SetVerticesDirty();
            }
        }

        // ---------------------------------------------------------------- text tables

        /// <summary>Localized string for a key; returns the key itself when no localizer is bound.</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            var loc = Localizer;
            if (loc == null) return key;
            try { return loc.Get(key); }
            catch (Exception e)
            {
                VNLog.Warn(Area, "Localizer.Get(" + key + ") failed: " + e.Message);
                return key;
            }
        }

        /// <summary>Remember the key of a label so <see cref="Relocalize"/> can refresh it.</summary>
        public static void BindLocalized(TMP_Text text, string key)
        {
            if (text == null || string.IsNullOrEmpty(key)) return;
            localized[text] = key;
            text.text = T(key);
        }

        /// <summary>Stop tracking a label (call from OnDestroy of a screen that destroyed it).</summary>
        public static void UnbindLocalized(TMP_Text text)
        {
            if (text != null) localized.Remove(text);
        }

        /// <summary>Refreshes every bound label - subscribe the screens to ILocalizer.LanguageChanged.</summary>
        public static void Relocalize()
        {
            if (localized.Count == 0) return;
            foreach (var kv in localized)
            {
                var text = kv.Key;
                if (text == null) continue;
                text.text = T(kv.Value);
                text.SetVerticesDirty();
            }
            // Sweep the destroyed entries; Unity null-compares a dead component to null.
            RemoveDeadBindings();
        }

        private static void RemoveDeadBindings()
        {
            List<TMP_Text> dead = null;
            foreach (var kv in localized)
            {
                if (kv.Key != null) continue;
                (dead ?? (dead = new List<TMP_Text>())).Add(kv.Key);
            }
            if (dead == null) return;
            for (var i = 0; i < dead.Count; i++) localized.Remove(dead[i]);
        }

        // ---------------------------------------------------------------- canvas

        /// <summary>
        /// Creates (once) VNCanvas with the 8 fixed layers, CanvasScaler 1920x1080 match 0.5,
        /// a GraphicRaycaster, SafeArea, the shared EventSystem on InputSystemUIInputModule,
        /// the UiRouter and the ToastService. Idempotent.
        /// </summary>
        public static Canvas EnsureCanvas(int sortingOrder = 0)
        {
            if (canvas != null) return canvas;

            var existing = GameObject.Find(CanvasName);
            if (existing == null)
            {
                var go = new GameObject(CanvasName, typeof(RectTransform));
                var uiLayer = LayerMask.NameToLayer("UI");
                go.layer = uiLayer >= 0 ? uiLayer : 5;
                UnityEngine.Object.DontDestroyOnLoad(go);
                canvas = go.AddComponent<Canvas>();
                existing = go;
            }
            else
            {
                canvas = existing.GetComponent<Canvas>();
                if (canvas == null) canvas = existing.AddComponent<Canvas>();
                DontDestroyOnLoadKeep(existing);
            }

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            canvas.pixelPerfect = false;

            if (existing.GetComponent<CanvasScaler>() == null)
            {
                var scaler = existing.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(UiStyle.ReferenceWidth, UiStyle.ReferenceHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (existing.GetComponent<GraphicRaycaster>() == null)
            {
                var raycaster = existing.AddComponent<GraphicRaycaster>();
                raycaster.ignoreReversedGraphics = true;
                raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;
            }

            layers.Clear();
            for (var i = 0; i < LayerNames.Length; i++) EnsureLayer(LayerNames[i]);

            if (existing.GetComponent<SafeArea>() == null) existing.AddComponent<SafeArea>();
            existing.GetComponent<SafeArea>().ApplyToLayers();

            if (UiRouter.Instance == null) existing.AddComponent<UiRouter>();
            if (UiRouter.Instance != null) UiRouter.Instance.AttachLayer(UiStyle.LayerNames[5]);

            ToastService.Ensure(existing.transform);

            EnsureEventSystem();
            EnsureFont();
            return canvas;
        }

        private static void DontDestroyOnLoadKeep(GameObject go)
        {
            try { UnityEngine.Object.DontDestroyOnLoad(go); }
            catch (Exception e) { VNLog.Warn(Area, "DontDestroyOnLoad skipped: " + e.Message); }
        }

        /// <summary>Creates exactly one EventSystem driven by the new Input System.</summary>
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                var module = EventSystem.current.GetComponent<InputSystemUIInputModule>();
                if (module == null) module = EventSystem.current.gameObject.AddComponent<InputSystemUIInputModule>();
                if (module.actionsAsset == null) module.AssignDefaultActions();
                return;
            }

            var stale = GameObject.Find("EventSystem");
            if (stale != null && stale.GetComponent<EventSystem>() != null)
            {
                EventSystem.current = stale.GetComponent<EventSystem>();
            }
            else
            {
                var go = new GameObject("EventSystem");
                go.AddComponent<EventSystem>();
                var module = go.AddComponent<InputSystemUIInputModule>();
                if (module.actionsAsset == null) module.AssignDefaultActions();
            }
        }

        /// <summary>RectTransform of a fixed layer; builds the canvas on demand.</summary>
        public static RectTransform GetLayer(string name)
        {
            EnsureCanvas();
            return EnsureLayer(name);
        }

        /// <summary>Layer lookup that never creates anything; false when the canvas is absent.</summary>
        public static bool TryGetLayer(string name, out RectTransform layer)
        {
            layer = null;
            if (string.IsNullOrEmpty(name)) return false;
            if (layers.TryGetValue(name, out layer) && layer != null) return true;
            if (canvas == null) return false;
            for (var i = 0; i < canvas.transform.childCount; i++)
            {
                var child = canvas.transform.GetChild(i) as RectTransform;
                if (child == null || child.name != name) continue;
                layers[name] = child;
                layer = child;
                return true;
            }
            return false;
        }

        /// <summary>Creates a missing layer; unknown names are appended on top, fixed ones keep their slot.</summary>
        public static RectTransform EnsureLayer(string name)
        {
            if (TryGetLayer(name, out var found)) return found;
            if (canvas == null) EnsureCanvas();

            var rt = CreateRect(name, canvas.transform);
            var index = Array.IndexOf(LayerNames, name);
            if (index >= 0)
            {
                // Keep the child order equal to the contract order (bottom to top).
                var target = Mathf.Min(index, canvas.transform.childCount);
                rt.SetSiblingIndex(target);
            }
            layers[name] = rt;
            return rt;
        }

        /// <summary>Tears the whole canvas down (domain reload / integration tests).</summary>
        public static void DestroyCanvas()
        {
            if (canvas != null) UnityEngine.Object.Destroy(canvas.gameObject);
            canvas = null;
            layers.Clear();
            localized.Clear();
            allTexts.Clear();
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>Bare stretched RectTransform.</summary>
        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.transform as RectTransform;
            rt.SetParent(parent, false);
            Stretch(rt);
            return rt;
        }

        /// <summary>Full rectangle stretch with optional padding (left, right, top, bottom).</summary>
        public static void Stretch(RectTransform rt, float left = 0, float right = 0, float top = 0, float bottom = 0)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>Fixes a rect to an anchor with a pixel size.</summary>
        public static void SetRect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
                                   Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            if (rt == null) return;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;
        }

        /// <summary>Sets size in reference pixels and keeps the current anchors.</summary>
        public static void SetSize(RectTransform rt, float width, float height)
        {
            if (rt == null) return;
            rt.sizeDelta = new Vector2(width, height);
        }

        /// <summary>Solid colour panel (Image with no sprite renders a filled quad).</summary>
        public static Image CreatePanel(string name, Transform parent, Color color)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            return img;
        }

        /// <summary>Rounded nine-slice panel built from a procedural sprite - no art asset needed.</summary>
        public static Image CreateNineSlicePanel(string name, Transform parent, Color color, float border = 12f)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = RoundedPanel;
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = true;
            return img;
        }

        /// <summary>Procedural rounded rectangle used for panels, buttons, cards and handles.</summary>
        public static Sprite RoundedPanel
        {
            get
            {
                if (roundedSprite != null) return roundedSprite;
                var size = 64;
                var radius = 12;
                var tex = NewTexture(size, size, "vn_round_panel");
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var dx = 0f; var dy = 0f;
                        if (x < radius) dx = radius - x;
                        else if (x > size - 1 - radius) dx = x - (size - 1 - radius);
                        if (y < radius) dy = radius - y;
                        else if (y > size - 1 - radius) dy = y - (size - 1 - radius);
                        var dist = Mathf.Sqrt(dx * dx + dy * dy);
                        var a = dist <= radius - 1f ? 255 : (dist <= radius + 0.5f ? (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(radius + 0.5f - dist)) : (byte)0);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a / 255f));
                    }
                }
                tex.Apply();
                var b = (float)(size / 2 - radius);
                roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f,
                                              new Vector4(b, b, b, b), "vn_round_panel");
                roundedSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return roundedSprite;
            }
        }

        /// <summary>Small downward triangle for the "advance" chevron (glyph-free, font independent).</summary>
        public static Sprite Chevron
        {
            get
            {
                if (chevronSprite != null) return chevronSprite;
                var w = 32; var h = 20;
                var tex = NewTexture(w, h, "vn_chevron");
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var edge = h * 2f * Mathf.Abs(x - (w - 1) * 0.5f) / (w - 1);
                        var a = y <= edge && y > edge - 3f ? 255 : 0;
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a / 255f));
                    }
                }
                tex.Apply();
                chevronSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, Vector4.zero, "vn_chevron");
                chevronSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return chevronSprite;
            }
        }

        private static Texture2D NewTexture(int w, int h, string name)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            return tex;
        }

        // ---------------------------------------------------------------- text

        /// <summary>Unlocalized text element (numbers, source previews, node ids).</summary>
        public static TextMeshProUGUI CreateText(Transform parent, string name, float size, Color color, bool wordWrap)
        {
            var rt = CreateRect(name, parent);
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = size;
            tmp.color = color;
            tmp.enableWordWrapping = wordWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.richText = false;
            tmp.raycastTarget = false;
            tmp.margin = new Vector4(4f, 4f, 4f, 4f);
            var f = Font;
            if (f != null) tmp.font = f;
            allTexts.Add(tmp);
            return tmp;
        }

        /// <summary>Localized label created from a ui.* key; refreshed by <see cref="Relocalize"/>.</summary>
        public static TextMeshProUGUI CreateLabel(string key, Transform parent, float size, Color color, bool wordWrap = false)
        {
            var tmp = CreateText(parent, SanitizeName(key), size, color, wordWrap);
            BindLocalized(tmp, key);
            return tmp;
        }

        // ---------------------------------------------------------------- button

        /// <summary>Localized button; the click callback can never throw into the event system.</summary>
        public static Button CreateButton(string key, Transform parent, Action onClick)
        {
            return BuildButton(SanitizeName(key), parent, true, key, onClick);
        }

        /// <summary>Button whose caption is literal text (numbers, node ids, slot titles already localized elsewhere).</summary>
        public static Button CreateLiteralButton(string caption, Transform parent, Action onClick)
        {
            return BuildButton("Button", parent, false, caption, onClick);
        }

        private static Button BuildButton(string name, Transform parent, bool isKey, string text, Action onClick)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = RoundedPanel;
            img.type = Image.Type.Sliced;
            img.color = UiStyle.PanelRaised;

            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.colors = UiStyle.ButtonColors(UiStyle.PanelRaised);

            var layout = rt.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = Mathf.Max(MinTouchSize, UiStyle.ButtonHeight);

            var label = CreateText(rt, "Text", UiStyle.LabelFontSize, UiStyle.Text, true);
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.overflowMode = TextOverflowModes.Shrink;
            label.enableWordWrapping = false;
            label.margin = new Vector4(20f, 8f, 20f, 8f);
            if (isKey) BindLocalized(label, text); else label.text = text ?? string.Empty;

            btn.onClick.AddListener(() => Safe(name, onClick));
            return btn;
        }

        // ---------------------------------------------------------------- slider

        /// <summary>Row with a localized caption, a value read-out and a touch-sized slider.</summary>
        public static Slider CreateSlider(string key, Transform parent, Action<float> onChange)
        {
            return CreateSlider(key, parent, out _, onChange);
        }

        /// <summary>Row with a localized caption, a value read-out and a touch-sized slider.</summary>
        public static Slider CreateSlider(string key, Transform parent, out TextMeshProUGUI valueLabel, Action<float> onChange)
        {
            var row = CreateRect("SliderRow_" + SanitizeName(key), parent);
            var layout = row.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 104f;
            layout.preferredHeight = 104f;

            var caption = CreateLabel(key, row, UiStyle.LabelFontSize, UiStyle.Text, false);
            SetRect(caption.rectTransform, new Vector2(0, 0.55f), new Vector2(0.6f, 1f), new Vector2(0, 0.5f),
                    Vector2.zero, new Vector2(-8f, 0f));
            caption.alignment = TextAlignmentOptions.MidlineLeft;
            caption.overflowMode = TextOverflowModes.Shrink;

            valueLabel = CreateText(row, "Value", UiStyle.LabelFontSize, UiStyle.Accent, false);
            SetRect(valueLabel.rectTransform, new Vector2(0.6f, 0.55f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                    Vector2.zero, new Vector2(-8f, 0f));
            valueLabel.alignment = TextAlignmentOptions.MidlineRight;

            var sliderRt = CreateRect("Slider", row);
            SetRect(sliderRt, new Vector2(0, 0.05f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f),
                    Vector2.zero, new Vector2(-16f, -4f));

            var bg = sliderRt.gameObject.AddComponent<Image>();
            bg.sprite = RoundedPanel;
            bg.type = Image.Type.Sliced;
            bg.color = UiStyle.Border;
            bg.raycastTarget = true;

            var fillArea = CreateRect("Fill Area", sliderRt);
            SetRect(fillArea, new Vector2(0, 0.25f), new Vector2(1f, 0.75f), new Vector2(0.5f, 0.5f),
                    new Vector2(4f, 0f), new Vector2(-16f, 0f));
            var fill = fillArea.gameObject.AddComponent<Image>();
            fill.sprite = RoundedPanel;
            fill.type = Image.Type.Sliced;
            fill.color = UiStyle.Accent;
            fill.raycastTarget = false;

            var handleArea = CreateRect("Handle Slide Area", sliderRt);
            SetRect(handleArea, new Vector2(0, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                    Vector2.zero, new Vector2(-32f, 0f));
            var handleRt = CreateRect("Handle", handleArea);
            SetSize(handleRt, 28f, 0f);
            var handle = handleRt.gameObject.AddComponent<Image>();
            handle.sprite = RoundedPanel;
            handle.type = Image.Type.Sliced;
            handle.color = UiStyle.Text;
            handle.raycastTarget = false;

            var slider = sliderRt.gameObject.AddComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handleRt;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            slider.colors = UiStyle.ButtonColors(UiStyle.PanelRaised);
            slider.onValueChanged.AddListener(v => Safe(sliderRt.name, () => onChange?.Invoke(v)));
            return slider;
        }

        // ---------------------------------------------------------------- dropdown

        /// <summary>Caption + dropdown list built from the standard template structure TMP_Dropdown expects.</summary>
        public static TMP_Dropdown CreateDropdown(string key, Transform parent, IReadOnlyList<string> options, Action<int> onChange)
        {
            var root = CreateRect("Dropdown_" + SanitizeName(key), parent);
            var layout = root.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = Mathf.Max(MinTouchSize, UiStyle.ButtonHeight);

            var bg = root.gameObject.AddComponent<Image>();
            bg.sprite = RoundedPanel;
            bg.type = Image.Type.Sliced;
            bg.color = UiStyle.PanelRaised;
            bg.raycastTarget = true;

            var caption = CreateLabel(key, root, UiStyle.LabelFontSize, UiStyle.Text, false);
            caption.name = "Label";
            caption.alignment = TextAlignmentOptions.MidlineLeft;
            caption.overflowMode = TextOverflowModes.Shrink;
            caption.margin = new Vector4(20f, 4f, 40f, 4f);

            var arrow = CreateRect("Arrow", root);
            SetRect(arrow, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(-24f, 0f), new Vector2(18f, 12f));
            var arrowImg = arrow.gameObject.AddComponent<Image>();
            arrowImg.sprite = Chevron;
            arrowImg.color = UiStyle.TextDim;
            arrowImg.raycastTarget = false;

            // ---- template (hidden list)
            var template = CreateRect("Template", root);
            template.gameObject.SetActive(false);
            var tImg = template.gameObject.AddComponent<Image>();
            tImg.sprite = RoundedPanel;
            tImg.type = Image.Type.Sliced;
            tImg.color = UiStyle.Panel;
            SetRect(template, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1f),
                    new Vector2(0f, -8f), new Vector2(0f, 320f));

            var scroll = template.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = CreateRect("Viewport", template);
            Stretch(viewport, 6f, 6f, 6f, 6f);
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.color = Color.clear;
            vpImg.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;

            var content = CreateRect("Content", viewport);
            SetRect(content, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
            content.sizeDelta = new Vector2(0f, 0f);
            scroll.content = content;

            var item = CreateRect("Item", content);
            SetRect(item, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f), Vector2.zero,
                    new Vector2(0f, UiStyle.ButtonHeight));
            var itemImg = item.gameObject.AddComponent<Image>();
            itemImg.sprite = RoundedPanel;
            itemImg.type = Image.Type.Sliced;
            itemImg.color = Color.clear;
            itemImg.raycastTarget = true;

            var itemBg = CreateRect("Item Background", item);
            Stretch(itemBg, 2f, 2f, 1f, 1f);
            var itemBgImg = itemBg.gameObject.AddComponent<Image>();
            itemBgImg.sprite = RoundedPanel;
            itemBgImg.type = Image.Type.Sliced;
            itemBgImg.color = UiStyle.PanelRaised;
            itemBgImg.raycastTarget = false;

            var itemToggle = item.gameObject.AddComponent<Toggle>();
            itemToggle.transition = Selectable.Transition.ColorTint;
            itemToggle.targetGraphic = itemBgImg;
            itemToggle.colors = UiStyle.ButtonColors(Color.clear);

            var itemLabel = CreateText(item, "Item Text", UiStyle.LabelFontSize, UiStyle.Text, true);
            SetRect(itemLabel.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f),
                    Vector2.zero, Vector2.zero);
            itemLabel.alignment = TextAlignmentOptions.MidlineLeft;
            itemLabel.overflowMode = TextOverflowModes.Shrink;
            itemLabel.margin = new Vector4(20f, 6f, 20f, 6f);
            itemLabel.raycastTarget = false;

            var dropdown = root.gameObject.AddComponent<TMP_Dropdown>();
            dropdown.captionText = caption;
            dropdown.itemText = itemLabel;
            dropdown.itemImage = itemBgImg;
            dropdown.template = template;
            dropdown.SetValueWithoutNotify(0);
            if (options != null && options.Count > 0)
            {
                dropdown.ClearOptions();
                dropdown.AddOptions(new List<string>(options));
            }
            dropdown.onValueChanged.AddListener(i => Safe(root.name, () => onChange?.Invoke(i)));
            return dropdown;
        }

        // ---------------------------------------------------------------- toggle

        /// <summary>Touch-sized checkbox with a localized label.</summary>
        public static Toggle CreateToggle(string key, Transform parent, Action<bool> onChange)
        {
            var row = CreateRect("Toggle_" + SanitizeName(key), parent);
            var layout = row.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = Mathf.Max(MinTouchSize, UiStyle.ButtonHeight);

            var box = CreateRect("Background", row);
            SetRect(box, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                    new Vector2(8f, 0f), new Vector2(UiStyle.MinTouchSize, UiStyle.MinTouchSize));
            var boxImg = box.gameObject.AddComponent<Image>();
            boxImg.sprite = RoundedPanel;
            boxImg.type = Image.Type.Sliced;
            boxImg.color = UiStyle.Border;
            boxImg.raycastTarget = true;

            var check = CreateRect("Checkmark", box);
            Stretch(check, 12f, 12f, 12f, 12f);
            var checkImg = check.gameObject.AddComponent<Image>();
            checkImg.sprite = RoundedPanel;
            checkImg.type = Image.Type.Sliced;
            checkImg.color = UiStyle.Accent;
            checkImg.raycastTarget = false;

            var label = CreateLabel(key, row, UiStyle.LabelFontSize, UiStyle.Text, true);
            SetRect(label.rectTransform, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f),
                    Vector2.zero, new Vector2(-24f, 0f));
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.overflowMode = TextOverflowModes.Shrink;
            label.margin = new Vector4(72f, 6f, 12f, 6f);

            var toggle = row.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = boxImg;
            toggle.graphic = checkImg;
            toggle.isOn = false;
            toggle.colors = UiStyle.ButtonColors(UiStyle.Border);
            toggle.onValueChanged.AddListener(on => Safe(row.name, () => onChange?.Invoke(on)));
            return toggle;
        }

        // ---------------------------------------------------------------- lists

        /// <summary>ScrollRect with a masked viewport and a vertically fitted content root.</summary>
        public static ScrollRect CreateScrollList(string name, Transform parent, out RectTransform content)
        {
            var root = CreateRect(name, parent);
            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 40f;

            var viewport = CreateRect("Viewport", root);
            Stretch(viewport);
            var vpImg = viewport.gameObject.AddComponent<Image>();
            vpImg.color = Color.clear;
            vpImg.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;

            content = CreateRect("Content", viewport);
            SetRect(content, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = UiStyle.ItemSpacing;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            scroll.content = content;
            return scroll;
        }

        // ---------------------------------------------------------------- layout

        /// <summary>VerticalLayoutGroup + ContentSizeFitter with sane defaults for menus.</summary>
        public static VerticalLayoutGroup SetLayoutVertical(RectTransform target, float spacing = UiStyle.ItemSpacing,
                                                            float padding = UiStyle.PanelPadding, bool fit = true, bool reverse = false)
        {
            var vlg = target.GetComponent<VerticalLayoutGroup>();
            if (vlg == null) vlg = target.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = spacing;
            vlg.padding = new RectOffset((int)padding, (int)padding, (int)padding, (int)padding);
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.reverseArrangement = reverse;

            if (fit)
            {
                var fitter = target.GetComponent<ContentSizeFitter>();
                if (fitter == null) fitter = target.gameObject.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
            return vlg;
        }

        /// <summary>GridLayoutGroup for card walls (the save slot grid).</summary>
        public static GridLayoutGroup SetLayoutGrid(RectTransform target, Vector2 cell, Vector2 spacing, int columns)
        {
            var grid = target.GetComponent<GridLayoutGroup>();
            if (grid == null) grid = target.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = cell;
            grid.spacing = spacing;
            grid.padding = new RectOffset((int)spacing.x, (int)spacing.x, (int)spacing.y, (int)spacing.y);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Mathf.Max(1, columns);
            grid.childAlignment = TextAnchor.UpperCenter;
            return grid;
        }

        /// <summary>Forces an immediate rebuild so a freshly built screen lays out in one frame.</summary>
        public static void Rebuild(params RectTransform[] roots)
        {
            if (roots == null || roots.Length == 0) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(roots[0]);
        }

        // ---------------------------------------------------------------- input / focus

        /// <summary>Moves the uGUI keyboard focus to a control (gamepad/arrow navigation).</summary>
        public static void Focus(Selectable selectable)
        {
            if (selectable == null || EventSystem.current == null) return;
            try { EventSystem.current.SetSelectedGameObject(selectable.gameObject); }
            catch (Exception e) { VNLog.Warn(Area, "Focus failed: " + e.Message); }
        }

        /// <summary>Adds a navigation link so arrows walk a list of controls.</summary>
        public static void ChainNavigation(IList<Button> buttons)
        {
            if (buttons == null || buttons.Count == 0) return;
            for (var i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (b == null) continue;
                var nav = b.navigation;
                nav.wrapMode = true;
                nav.selectOnUp = buttons[(i - 1 + buttons.Count) % buttons.Count];
                nav.selectOnDown = buttons[(i + 1) % buttons.Count];
                nav.mode = Navigation.Mode.Explicit;
                b.navigation = nav;
            }
        }

        // ---------------------------------------------------------------- safety

        /// <summary>Runs a UI callback, converting any exception into a log line instead of a crash.</summary>
        public static void Safe(string what, Action action)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception e)
            {
                VNLog.Error(Area, "UI handler '" + what + "' threw: " + e);
            }
        }

        private static string SanitizeName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "Text";
            var clean = key.Replace('.', '_').Replace('*', '_');
            return clean.Length > 48 ? clean.Substring(0, 48) : clean;
        }

        /// <summary>Reference comparer so destroyed Unity objects do not hash by value equality.</summary>
        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
