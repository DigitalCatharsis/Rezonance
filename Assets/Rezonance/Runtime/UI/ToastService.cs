// REZONANCE VN ENGINE - toast notifications (UI module).
// Short, queued, non-blocking messages ("saved", "loaded", "load failed"). The host CanvasGroup
// never intercepts raycasts, so a toast cannot eat an advance tap.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Queue of short messages rendered at the top of the HudLayer.</summary>
    [DisallowMultipleComponent]
    public sealed class ToastService : MonoBehaviour
    {
        private const string Area = "toast";
        private const int MaxVisible = 3;

        /// <summary>Live instance created together with the canvas.</summary>
        public static ToastService Instance { get; private set; }

        private readonly List<Row> visible = new List<Row>();
        private readonly Queue<Pending> pending = new Queue<Pending>();
        private readonly List<Row> pool = new List<Row>();
        private RectTransform host;
        private ILocalizer localizer;

        private struct Pending
        {
            public string key;
            public string raw;
            public float seconds;
        }

        private sealed class Row
        {
            public GameObject gameObject;
            public RectTransform rect;
            public CanvasGroup group;
            public TMP_Text text;
            public float remaining;
            public float fade;
        }

        /// <summary>Creates or returns the toast service attached to the canvas root.</summary>
        public static ToastService Ensure(Transform canvasRoot)
        {
            if (Instance != null) return Instance;
            if (canvasRoot == null)
            {
                VNLog.Warn(Area, "ToastService.Ensure called without a canvas");
                return null;
            }
            var service = canvasRoot.GetComponent<ToastService>();
            if (service == null) service = canvasRoot.gameObject.AddComponent<ToastService>();
            return service;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            BuildHost();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void BuildHost()
        {
            if (host != null) return;
            var layer = UIBuilder.GetLayer(UiStyle.LayerNames[6]); // HudLayer
            if (layer == null) return;

            host = UIBuilder.CreateRect("ToastHost", layer);
            UIBuilder.SetRect(host, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -120f), new Vector2(920f, 0f));
            host.sizeDelta = new Vector2(920f, 240f);

            var vlg = host.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8f;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;

            var group = host.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            group.alpha = 1f;
        }

        /// <summary>Text source for toast keys; defaults to the canvas-wide localizer.</summary>
        public ILocalizer Localizer
        {
            get => localizer;
            set => localizer = value;
        }

        // ---------------------------------------------------------------- public api

        /// <summary>Shows a localized toast for a ui.toast.* key.</summary>
        public static void Notify(string key, float seconds = 2.2f)
        {
            if (Instance == null)
            {
                VNLog.Info(Area, "Toast dropped (no canvas): " + key);
                return;
            }
            Instance.Show(key, seconds);
        }

        /// <summary>Shows a literal, already resolved string (engine messages, node previews).</summary>
        public static void NotifyRaw(string text, float seconds = 2.2f)
        {
            if (Instance == null) return;
            Instance.ShowRaw(text, seconds);
        }

        /// <summary>Instance level show of a localized key.</summary>
        public void Show(string key, float seconds = 2.2f)
        {
            if (string.IsNullOrEmpty(key)) return;
            Enqueue(new Pending { key = key, raw = Resolve(key), seconds = Mathf.Max(0.4f, seconds) });
        }

        /// <summary>Instance level show of raw text.</summary>
        public void ShowRaw(string text, float seconds = 2.2f)
        {
            if (string.IsNullOrEmpty(text)) return;
            Enqueue(new Pending { raw = text, seconds = Mathf.Max(0.4f, seconds) });
        }

        private string Resolve(string key)
        {
            var loc = localizer != null ? localizer : UIBuilder.Localizer;
            if (loc == null) return key;
            try { return loc.Get(key); }
            catch (Exception e)
            {
                VNLog.Warn(Area, "Toast localizer failed: " + e.Message);
                return key;
            }
        }

        private void Enqueue(Pending item)
        {
            if (host == null) BuildHost();
            if (host == null) return;
            // Collapse repeats of the same message so a held key cannot flood the queue.
            if (visible.Count > 0 && visible[visible.Count - 1].text != null &&
                visible[visible.Count - 1].text.text == item.raw && item.raw != null)
            {
                visible[visible.Count - 1].remaining = item.seconds;
                return;
            }
            if (visible.Count >= MaxVisible) pending.Enqueue(item);
            else ShowNow(item);
        }

        private void ShowNow(Pending item)
        {
            var row = Acquire();
            row.text.text = item.raw;
            row.remaining = item.seconds;
            row.fade = 0f;
            row.group.alpha = 0f;
            row.gameObject.SetActive(true);
            visible.Add(row);
        }

        private Row Acquire()
        {
            for (var i = pool.Count - 1; i >= 0; i--)
            {
                var reuse = pool[i];
                if (reuse == null || reuse.gameObject == null) { pool.RemoveAt(i); continue; }
                pool.RemoveAt(i);
                return reuse;
            }

            var rt = UIBuilder.CreateNineSlicePanel("Toast", host, UiStyle.Toast);
            var rowRt = rt.rectTransform;
            var le = rowRt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = UiStyle.MinTouchSize;
            le.preferredWidth = 880f;
            le.flexibleWidth = 0f;

            var row = new Row
            {
                gameObject = rowRt.gameObject,
                rect = rowRt,
                group = rowRt.gameObject.AddComponent<CanvasGroup>()
            };
            row.text = UIBuilder.CreateText(rowRt, "Text", UiStyle.CaptionFontSize + 2f, UiStyle.Text, true);
            UIBuilder.Stretch(row.text.rectTransform, 24f, 24f, 8f, 8f);
            row.text.alignment = TextAlignmentOptions.MidlineLeft;
            row.text.overflowMode = TextOverflowModes.Shrink;
            row.rect.localScale = Vector3.one;
            return row;
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;
            for (var i = visible.Count - 1; i >= 0; i--)
            {
                var row = visible[i];
                if (row == null || row.gameObject == null) { visible.RemoveAt(i); continue; }

                row.remaining -= dt;
                row.fade = Mathf.Clamp01(row.fade + dt * 6f);
                var outFade = Mathf.Clamp01(row.remaining * 3f);
                row.group.alpha = Mathf.Min(row.fade, outFade);

                if (row.remaining <= -0.2f)
                {
                    visible.RemoveAt(i);
                    row.gameObject.SetActive(false);
                    pool.Add(row);
                }
            }

            while (visible.Count < MaxVisible && pending.Count > 0)
            {
                ShowNow(pending.Dequeue());
            }
        }

        /// <summary>Drops everything on screen (used when returning to the main menu).</summary>
        public void Clear()
        {
            for (var i = visible.Count - 1; i >= 0; i--)
            {
                var row = visible[i];
                if (row == null || row.gameObject == null) continue;
                row.gameObject.SetActive(false);
                pool.Add(row);
            }
            visible.Clear();
            pending.Clear();
        }
    }
}
