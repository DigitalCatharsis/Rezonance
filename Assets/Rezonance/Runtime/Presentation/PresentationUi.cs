// REZONANCE VN ENGINE - small uGUI/TMP builders shared by the presentation directors.
// They only create objects inside a layer RectTransform handed over by the UI module, never a Canvas.
using System;
using Rezonance.VN.Core;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Rezonance.VN.Presentation
{
    /// <summary>Factory helpers for the screen elements the directors build at runtime.</summary>
    internal static class PresentationUi
    {
        /// <summary>Layer id used for every generated node; falls back to the built-in UI layer.</summary>
        public static int UiLayer
        {
            get
            {
                var layer = LayerMask.NameToLayer("UI");
                return layer >= 0 ? layer : 5;
            }
        }

        /// <summary>Creates a stretched child node under parent and returns its rect; null when parent is missing.</summary>
        public static RectTransform CreateNode(string name, RectTransform parent, int siblingIndex = -1)
        {
            if (parent == null) return null;
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.transform as RectTransform;
            rt.SetParent(parent, false);
            Stretch(rt);
            go.layer = UiLayer;
            if (siblingIndex >= 0) rt.SetSiblingIndex(Mathf.Min(siblingIndex, parent.childCount));
            return rt;
        }

        /// <summary>Creates a child pinned to a normalized x position with its pivot on the bottom edge.</summary>
        public static RectTransform CreateBottomAnchored(string name, RectTransform parent, float anchorX)
        {
            if (parent == null) return null;
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.transform as RectTransform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(anchorX, 0f);
            rt.anchorMax = new Vector2(anchorX, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            go.layer = UiLayer;
            return rt;
        }

        /// <summary>Makes a rect cover its parent exactly.</summary>
        public static void Stretch(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Adds a RawImage that does not intercept pointer input.</summary>
        public static RawImage AddRawImage(RectTransform rt)
        {
            var img = rt.gameObject.AddComponent<RawImage>();
            img.color = Color.white;
            img.raycastTarget = false;
            img.texture = null;
            return img;
        }

        /// <summary>Adds a plain colored Image; optionally clickable so a modal can swallow taps.</summary>
        public static Image AddImage(RectTransform rt, Color color, bool raycastTarget)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycastTarget;
            img.type = Image.Type.Simple;
            return img;
        }

        /// <summary>Adds or reuses the CanvasGroup that acts as the opacity knob of one element.</summary>
        public static CanvasGroup AddGroup(GameObject go, float alpha, bool blocksRaycasts)
        {
            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            group.alpha = Mathf.Clamp01(alpha);
            group.interactable = blocksRaycasts;
            group.blocksRaycasts = blocksRaycasts;
            group.ignoreParentGroups = false;
            return group;
        }

        /// <summary>Creates a TMP label sized from the hosting layer height so it scales with the canvas.</summary>
        public static TextMeshProUGUI AddText(string name, RectTransform parent, float layerHeight,
                                              TextAnchor anchor, int referenceFontSize, bool bold)
        {
            var rt = CreateNode(name, parent);
            if (rt == null) return null;
            var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.font = TMP_Settings.defaultFontAsset;
            tmp.alignment = ToOptions(anchor);
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            tmp.color = Color.white;
            tmp.text = string.Empty;
            tmp.fontSize = FontSize(layerHeight, referenceFontSize);
            tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            if (tmp.font == null)
                VNLog.Warn("presentation", "TMP default font asset is not assigned, built-in fallback is used");
            return tmp;
        }

        /// <summary>System-terminal look: bold plus wide letter spacing, close enough to monospaced plates.</summary>
        public static void SetMonoLook(TextMeshProUGUI tmp, float spacingRatio)
        {
            if (tmp == null) return;
            tmp.fontStyle = FontStyles.Bold;
            tmp.characterSpacing = Mathf.Clamp(spacingRatio * Mathf.Max(1f, tmp.fontSize), 0f, 24f);
        }

        /// <summary>Font size scaled from the layer height against the 1080 reference resolution.</summary>
        public static float FontSize(float layerHeight, int referenceFontSize)
        {
            if (layerHeight <= 1f || float.IsNaN(layerHeight) || float.IsInfinity(layerHeight))
                return Mathf.Max(8, referenceFontSize);
            return Mathf.Clamp(layerHeight * (referenceFontSize / 1080f), 8f, 220f);
        }

        /// <summary>Builds a clickable button (image plus centered label) and wires its callback.</summary>
        public static Button CreateButton(string name, RectTransform parent, float height, string label,
                                          UnityAction onClick)
        {
            var rt = CreateNode(name, parent);
            if (rt == null) return null;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(Mathf.Max(240f, height * 3f), height);

            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(0.30f, 0.34f, 0.42f, 1f);
            bg.raycastTarget = true;
            bg.type = Image.Type.Simple;

            var text = AddText(name + "_Label", rt, height, TextAnchor.MiddleCenter, 30, true);
            if (text != null) text.text = label;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.interactable = true;
            if (onClick != null) button.onClick.AddListener(onClick);
            return button;
        }

        /// <summary>Finds a label by child name so a rebuild never duplicates it.</summary>
        public static TextMeshProUGUI FindText(RectTransform parent, string name)
        {
            if (parent == null) return null;
            var child = parent.Find(name);
            return child != null ? child.GetComponent<TextMeshProUGUI>() : null;
        }

        /// <summary>Destroys a node when the reference is still valid.</summary>
        public static void Destroy(GameObject go)
        {
            if (go != null) UnityEngine.Object.Destroy(go);
        }

        /// <summary>Destroys a component's GameObject when the reference is still valid.</summary>
        public static void Destroy(Component component)
        {
            if (component != null && component.gameObject != null) UnityEngine.Object.Destroy(component.gameObject);
        }

        private static TextAlignmentOptions ToOptions(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.Center;
            }
        }
    }
}
