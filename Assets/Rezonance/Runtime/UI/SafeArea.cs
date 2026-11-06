// REZONANCE VN ENGINE - safe area adapter (UI module).
// The canvas root rect is driven by Unity in Screen Space Overlay, so the insets are applied to the
// fixed layer roots of ARCHITECTURE.md 9.6. On desktop Screen.safeArea equals the display, which
// makes this a no-op; on a 20:9 phone with a notch it pulls every layer inside the visible area.
using System.Collections.Generic;
using UnityEngine;

namespace Rezonance.VN.UI
{
    /// <summary>Applies Screen.safeArea insets to the canvas layers it is told to guard.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class SafeArea : MonoBehaviour
    {
        [Tooltip("Skip the adaptation (editor experiments).")]
        public bool applySafeArea = true;

        private readonly List<RectTransform> targets = new List<RectTransform>();
        private readonly HashSet<RectTransform> seen = new HashSet<RectTransform>();
        private Canvas canvas;
        private CanvasScaler scaler;
        private RectTransform root;
        private Vector2 lastMin = new Vector2(-1f, -1f);
        private Vector2 lastMax = new Vector2(-1f, -1f);
        private int frame;

        /// <summary>Number of rects currently adapted.</summary>
        public int TargetCount => targets.Count;

        private void Awake()
        {
            Resolve();
        }

        private void Resolve()
        {
            root = transform as RectTransform;
            canvas = GetComponent<Canvas>();
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
            scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
        }

        /// <summary>Picks up the standard layer roots as children of this object (called by UIBuilder).</summary>
        public void ApplyToLayers()
        {
            Resolve();
            targets.Clear();
            seen.Clear();
            if (root == null) return;
            for (var i = 0; i < root.childCount; i++)
            {
                if (root.GetChild(i) is RectTransform rt && System.Array.IndexOf(UIBuilder.LayerNames, rt.name) >= 0)
                    AddTarget(rt);
            }
            Reapply(true);
        }

        /// <summary>Adds one rect to the guarded set (a layer created after the canvas). Safe to repeat.</summary>
        public void AddTarget(RectTransform target)
        {
            if (target == null || seen.Contains(target)) return;
            targets.Add(target);
            seen.Add(target);
        }

        private void Update()
        {
            frame++;
            if (frame % 15 != 0) return;
            if (targets.Count == 0) ApplyToLayers();
            else Reapply(false);
        }

        /// <summary>Recomputes the insets; cheap, so screens may call it after a rotation.</summary>
        public void ForceReapply() => Reapply(true);

        private void Reapply(bool force)
        {
            if (!applySafeArea || root == null) return;
            if (canvas == null || scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return;

            var screen = new Vector2(Screen.width, Screen.height);
            if (screen.x <= 1f || screen.y <= 1f) return;

            var safe = Screen.safeArea;
            if (safe.width <= 1f || safe.height <= 1f) return;

            var relMin = new Vector2(safe.x / screen.x, safe.y / screen.y);
            var relMax = new Vector2((safe.x + safe.width) / screen.x, (safe.y + safe.height) / screen.y);

            var full = relMin.x <= 0.0001f && relMin.y <= 0.0001f && relMax.x >= 0.9999f && relMax.y >= 0.9999f;

            var rect = root.rect;
            if (rect.width <= 1f || rect.height <= 1f) return;

            var min = full ? Vector2.zero : new Vector2(relMin.x * rect.width, relMin.y * rect.height);
            var max = full ? Vector2.zero : new Vector2((relMax.x - 1f) * rect.width, (relMax.y - 1f) * rect.height);

            if (!force && min == lastMin && max == lastMax) return;
            lastMin = min;
            lastMax = max;

            for (var i = targets.Count - 1; i >= 0; i--)
            {
                var t = targets[i];
                if (t == null) { targets.RemoveAt(i); seen.Remove(t); continue; }
                t.anchorMin = Vector2.zero;
                t.anchorMax = Vector2.one;
                t.offsetMin = min;
                t.offsetMax = max;
            }
        }
    }
}
