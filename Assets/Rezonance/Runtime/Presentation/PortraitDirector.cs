// REZONANCE VN ENGINE - character portrait layer.
// Five normalized slots pinned to the bottom of the PortraitLayer rect the UI module hands over.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Rezonance.VN.Presentation
{
    /// <summary>Portrait director implementing IPortraitDirector with fades, focus dimming and reset.</summary>
    public sealed class PortraitDirector : IPortraitDirector, IDisposable
    {
        /// <summary>Canvas layer this director expects (ARCHITECTURE.md 9.6).</summary>
        public const string LayerName = "PortraitLayer";

        /// <summary>Name prefix of the generated slot objects.</summary>
        public const string ElementName = "VN_Portrait";

        /// <summary>Slot height as a fraction of the layer height.</summary>
        public const float HeightRatio = 0.85f;

        /// <summary>Fade length used when the caller passes a non positive value.</summary>
        public const float DefaultFadeSeconds = 0.35f;

        /// <summary>Fade length of a focus change.</summary>
        public const float FocusFadeSeconds = 0.25f;

        /// <summary>Reference resolution height used when the layer has not been laid out yet.</summary>
        public const float ReferenceHeight = 1080f;

        private const string LogArea = "portrait";

        private static readonly PortraitSlot[] SlotOrder =
        {
            PortraitSlot.FarLeft, PortraitSlot.Left, PortraitSlot.Center, PortraitSlot.Right, PortraitSlot.FarRight
        };

        private static readonly float[] DefaultPositions = { 0.12f, 0.31f, 0.5f, 0.69f, 0.88f };

        private readonly AssetLibrary assets;
        private readonly SlotView[] views = new SlotView[SlotOrder.Length];
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        private PresentationHost host;
        private RectTransform root;
        private PortraitSlot focusedSlot = PortraitSlot.None;
        private float dimTo = 1f;
        private float lastLayerHeight;
        private bool ready;
        private bool showPlates = true;

        /// <summary>Creates the director. Null arguments fall back to the shared library and host.</summary>
        public PortraitDirector(AssetLibrary assetLibrary = null, PresentationHost animationHost = null)
        {
            assets = assetLibrary ?? AssetLibrary.Shared;
            host = animationHost;
        }

        /// <summary>Draws a small key label over placeholder portraits so unreplaced art is obvious.</summary>
        public bool ShowPlaceholderPlates
        {
            get => showPlates;
            set
            {
                showPlates = value;
                foreach (var v in views)
                    if (v != null && v.plate != null) v.plate.gameObject.SetActive(value && v.IsPlaceholder);
            }
        }

        /// <summary>Slot currently considered the speaker, None when everybody is at full brightness.</summary>
        public PortraitSlot FocusedSlot => focusedSlot;

        /// <summary>Dim level applied to non speaking slots.</summary>
        public float DimLevel => dimTo;

        /// <summary>Binds to a PortraitLayer rect; warns and stays inert when the rect is missing.</summary>
        public void Init(RectTransform layerRect)
        {
            if (layerRect == null)
            {
                WarnOnce("nolayer", "PortraitLayer rect was not provided, portraits stay inert");
                ready = false;
                return;
            }

            root = layerRect;
            for (var i = 0; i < SlotOrder.Length; i++)
            {
                var slot = SlotOrder[i];
                var rt = PresentationUi.CreateBottomAnchored(ElementName + "_" + slot, root, DefaultPositions[i]);
                if (rt == null) continue;

                var view = new SlotView { slot = slot, rect = rt, xRatio = DefaultPositions[i] };
                view.image = PresentationUi.AddImage(rt, Color.white, false);
                view.group = PresentationUi.AddGroup(rt.gameObject, 0f, false);
                view.group.alpha = 0f;
                views[i] = view;
            }

            ready = true;
            Relayout();
            VNLog.Info(LogArea, "portrait layer bound with " + CountBound() + " slots");
        }

        /// <summary>Shows one portrait reference. Repeating the same key in the same slot does not restart the fade.</summary>
        public void Show(PortraitRef portrait, float seconds)
        {
            if (!ready) return;
            if (!portrait.IsValid)
            {
                WarnOnce("invalid-" + portrait.characterId, "portrait reference without a slot was ignored");
                return;
            }

            var view = ViewOf(portrait.slot);
            if (view == null || view.group == null) return;

            var key = portrait.AssetKey;
            assets.HintKey(key);
            var sprite = assets.GetSprite(key);
            var isPlaceholder = assets.IsPlaceholderSprite(key);

            if (string.Equals(view.key, key, StringComparison.Ordinal) && !isPlaceholder &&
                Mathf.Abs(view.group.alpha - TargetAlpha(view)) < 0.001f)
                return;

            view.key = key;
            view.IsPlaceholder = isPlaceholder;
            if (view.image != null) view.image.sprite = sprite;
            ApplyPlate(view, key, sprite);
            Relayout(view);

            FadeTo(view, TargetAlpha(view), seconds <= 0f ? 0f : Mathf.Max(0.01f, seconds));
        }

        /// <summary>Convenience overload using the default fade length.</summary>
        public void Show(PortraitRef portrait) => Show(portrait, DefaultFadeSeconds);

        /// <summary>Shows the portrait instantly, even when the same key is already up - used by save loading.</summary>
        public void Force(PortraitRef portrait)
        {
            if (!ready || !portrait.IsValid) return;
            var view = ViewOf(portrait.slot);
            if (view == null) return;
            CancelTick(view);
            Show(portrait, 0f);
        }

        /// <summary>Hides one slot.</summary>
        public void Hide(PortraitSlot slot, float seconds)
        {
            if (!ready) return;
            var view = ViewOf(slot);
            if (view == null || view.group == null) return;
            if (string.IsNullOrEmpty(view.key) && view.group.alpha <= 0.001f) return;
            FadeTo(view, 0f, seconds <= 0f ? 0f : Mathf.Max(0.01f, seconds), clearKey: true);
        }

        /// <summary>Hides every slot.</summary>
        public void HideAll(float seconds)
        {
            if (!ready) return;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || view.group == null) continue;
                if (string.IsNullOrEmpty(view.key) && view.group.alpha <= 0.001f) continue;
                FadeTo(view, 0f, seconds <= 0f ? 0f : Mathf.Max(0.01f, seconds), clearKey: true);
            }
            focusedSlot = PortraitSlot.None;
        }

        /// <summary>Dims everybody but the speaking slot. dimTo 1 disables dimming.</summary>
        public void SetFocus(PortraitSlot speaking, float dimTo)
        {
            if (!ready) return;
            focusedSlot = speaking;
            this.dimTo = Mathf.Clamp01(dimTo);
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || view.group == null) continue;
                if (string.IsNullOrEmpty(view.key)) continue;
                FadeTo(view, TargetAlpha(view), FocusFadeSeconds);
            }
        }

        /// <summary>Clears focus so all visible portraits return to full opacity.</summary>
        public void ClearFocus(float seconds = FocusFadeSeconds)
        {
            if (!ready) return;
            focusedSlot = PortraitSlot.None;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || string.IsNullOrEmpty(view.key)) continue;
                FadeTo(view, 1f, seconds);
            }
        }

        /// <summary>Cancels every animation and hides all portraits at once; used when a save is restored.</summary>
        public void Reset()
        {
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null) continue;
                CancelTick(view);
                if (view.group != null) view.group.alpha = 0f;
                if (view.image != null) view.image.sprite = null;
                if (view.plate != null) view.plate.gameObject.SetActive(false);
                view.key = null;
                view.IsPlaceholder = false;
            }
            focusedSlot = PortraitSlot.None;
        }

        /// <summary>Moves a slot to another normalized x position inside the layer.</summary>
        public void SetSlotPosition(PortraitSlot slot, float xRatio)
        {
            var view = ViewOf(slot);
            if (view == null || view.rect == null) return;
            view.xRatio = Mathf.Clamp01(xRatio);
            view.rect.anchorMin = new Vector2(view.xRatio, 0f);
            view.rect.anchorMax = new Vector2(view.xRatio, 0f);
        }

        /// <summary>Re-reads the layer rect and resizes every slot. Call after a resolution or orientation change.</summary>
        public void Relayout()
        {
            for (var i = 0; i < views.Length; i++) Relayout(views[i]);
        }

        /// <summary>Key currently shown in a slot, null when the slot is empty.</summary>
        public string KeyAt(PortraitSlot slot)
        {
            var view = ViewOf(slot);
            return view != null ? view.key : null;
        }

        /// <summary>True when the slot holds a portrait that is fully or partly visible.</summary>
        public bool IsSlotUsed(PortraitSlot slot)
        {
            var view = ViewOf(slot);
            return view != null && !string.IsNullOrEmpty(view.key) && view.group != null && view.group.alpha > 0.001f;
        }

        /// <summary>Releases the binding and destroys the generated nodes.</summary>
        public void Dispose()
        {
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null) continue;
                CancelTick(view);
                PresentationUi.Destroy(view.rect != null ? view.rect.gameObject : null);
                views[i] = null;
            }
            root = null;
            ready = false;
        }

        private void Relayout(SlotView view)
        {
            if (view == null || view.rect == null) return;
            var layerHeight = LayerHeight();
            var height = Mathf.Max(1f, layerHeight * HeightRatio);
            var width = height * 0.6f;
            if (view.image != null && view.image.sprite != null && view.image.sprite.rect.height > 0f)
            {
                var aspect = view.image.sprite.rect.width / view.image.sprite.rect.height;
                if (aspect > 0.01f && aspect < 10f) width = height * aspect;
            }
            view.rect.sizeDelta = new Vector2(width, height);
            lastLayerHeight = layerHeight;
        }

        private float LayerHeight()
        {
            if (root != null && root.rect.height > 1f) return root.rect.height;
            return ReferenceHeight;
        }

        private void ApplyPlate(SlotView view, string key, Sprite sprite)
        {
            if (view == null || view.rect == null) return;
            if (!showPlates || !view.IsPlaceholder)
            {
                if (view.plate != null) view.plate.gameObject.SetActive(false);
                return;
            }

            if (view.plate == null)
            {
                var height = view.rect.rect.height > 1f ? view.rect.rect.height : LayerHeight() * HeightRatio;
                var tmp = PresentationUi.AddText(ElementName + "_Plate", view.rect, height,
                                                 TextAnchor.LowerCenter, 26, true);
                if (tmp == null) return;
                tmp.rectTransform.anchorMin = new Vector2(0f, 0f);
                tmp.rectTransform.anchorMax = new Vector2(1f, 0.18f);
                tmp.rectTransform.offsetMin = Vector2.zero;
                tmp.rectTransform.offsetMax = Vector2.zero;
                PresentationUi.SetMonoLook(tmp, 0.06f);
                view.plate = tmp;
            }

            view.plate.gameObject.SetActive(true);
            // The plate prints the asset key (ascii) so the artist sees what is missing without a localizer.
            view.plate.text = SanitizeForPlate(key);
            var size = PresentationUi.FontSize(LayerHeight() * HeightRatio, 26);
            view.plate.fontSize = size;
        }

        private float TargetAlpha(SlotView view)
        {
            if (string.IsNullOrEmpty(view.key)) return 0f;
            if (focusedSlot == PortraitSlot.None || view.slot == focusedSlot) return 1f;
            return dimTo;
        }

        private void FadeTo(SlotView view, float to, float seconds, bool clearKey = false)
        {
            if (view == null || view.group == null) return;
            CancelTick(view);
            view.pendingClear = clearKey;

            var h = ResolveHost();
            if (h == null || seconds <= 0f || Mathf.Abs(view.group.alpha - to) < 0.0005f)
            {
                view.group.alpha = to;
                if (clearKey && to <= 0.001f) ClearView(view);
                return;
            }

            view.from = view.group.alpha;
            view.target = to;
            view.duration = seconds;
            view.elapsed = 0f;
            var captured = view;
            view.tick = h.Schedule(dt => TickSlot(captured, dt));
        }

        private void TickSlot(SlotView view, float deltaTime)
        {
            if (view == null || view.group == null) { CancelTick(view); return; }
            view.elapsed += deltaTime;
            var t = Mathf.Clamp01(view.duration <= 0f ? 1f : view.elapsed / view.duration);
            view.group.alpha = Mathf.Lerp(view.from, view.target, t);
            if (t < 1f) return;
            view.group.alpha = view.target;
            view.tick = 0;
            if (view.pendingClear && view.target <= 0.001f) ClearView(view);
            view.pendingClear = false;
        }

        private void ClearView(SlotView view)
        {
            if (view == null) return;
            view.key = null;
            view.IsPlaceholder = false;
            if (view.image != null) view.image.sprite = null;
            if (view.plate != null) view.plate.gameObject.SetActive(false);
        }

        private SlotView ViewOf(PortraitSlot slot)
        {
            for (var i = 0; i < SlotOrder.Length; i++)
                if (SlotOrder[i] == slot) return views[i];
            return null;
        }

        private int CountBound()
        {
            var n = 0;
            for (var i = 0; i < views.Length; i++) if (views[i] != null) n++;
            return n;
        }

        private void CancelTick(SlotView view)
        {
            if (view == null || view.tick == 0) return;
            var h = host != null && host.gameObject != null ? host : PresentationHost.Instance;
            if (h != null) h.Cancel(view.tick);
            view.tick = 0;
        }

        private PresentationHost ResolveHost()
        {
            if (host != null && host.gameObject != null) return host;
            host = PresentationHost.EnsureExists();
            if (host == null) WarnOnce("nohost", "no PresentationHost available, portrait fades apply instantly");
            return host;
        }

        private static string SanitizeForPlate(string key)
        {
            if (string.IsNullOrEmpty(key)) return "-";
            var upper = key.ToUpperInvariant();
            var length = Mathf.Min(upper.Length, 40);
            return upper.Substring(0, length);
        }

        private void WarnOnce(string identity, string message)
        {
            if (!warned.Add(identity)) return;
            VNLog.Warn(LogArea, message);
        }

        private sealed class SlotView
        {
            public PortraitSlot slot;
            public RectTransform rect;
            public Image image;
            public CanvasGroup group;
            public TextMeshProUGUI plate;
            public string key;
            public bool IsPlaceholder;
            public int tick;
            public float xRatio = 0.5f;
            public float from;
            public float target;
            public float duration = 1f;
            public float elapsed;
            public bool pendingClear;
        }
    }
}
