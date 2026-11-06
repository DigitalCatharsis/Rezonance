// REZONANCE VN ENGINE - background layer with crossfade.
// Creates its own two RawImages inside the BackdropLayer rect handed over by the UI module.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Rezonance.VN.Presentation
{
    /// <summary>Backdrop director implementing IBackdropDirector with cut, fade and crossfade transitions.</summary>
    public sealed class BackdropDirector : IBackdropDirector, IDisposable
    {
        /// <summary>Canvas layer this director expects (ARCHITECTURE.md 9.6).</summary>
        public const string LayerName = "BackdropLayer";

        /// <summary>Name prefix of the generated elements.</summary>
        public const string ElementName = "VN_Backdrop";

        /// <summary>Fade length used when the caller passes a non positive value.</summary>
        public const float DefaultFadeSeconds = 0.6f;

        private const string LogArea = "backdrop";

        private readonly AssetLibrary assets;
        private readonly RawImage[] images = new RawImage[2];
        private readonly CanvasGroup[] groups = new CanvasGroup[2];
        private readonly GameObject[] nodes = new GameObject[2];
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        private PresentationHost host;
        private RectTransform root;
        private int visibleIndex = -1;
        private int incomingIndex = -1;
        private int outgoingIndex = -1;
        private int tick;
        private float elapsed;
        private float duration = 1f;
        private VNFadeKind fadeKind;
        private string pendingKey;
        private string currentKey;
        private Color tint = Color.white;
        private bool ready;

        /// <summary>Creates the director; null arguments fall back to the shared library and host.</summary>
        public BackdropDirector(AssetLibrary assetLibrary = null, PresentationHost animationHost = null)
        {
            assets = assetLibrary ?? AssetLibrary.Shared;
            host = animationHost;
        }

        /// <summary>Backdrop key currently on screen, null when the layer is empty.</summary>
        public string CurrentKey => currentKey;

        /// <summary>True while a fade is running.</summary>
        public bool IsTransitioning => tick != 0;

        /// <summary>True once Init found its layer and built the elements.</summary>
        public bool IsReady => ready;

        /// <summary>Colour multiplied over every backdrop, kept across shows.</summary>
        public Color Tint
        {
            get => tint;
            set
            {
                tint = value;
                for (var i = 0; i < images.Length; i++)
                    if (images[i] != null) images[i].color = tint;
            }
        }

        /// <summary>Builds the two RawImages inside the given layer rect; warns and stays inert when it is null.</summary>
        public void Init(RectTransform layerRect)
        {
            if (layerRect == null)
            {
                WarnOnce("nolayer", "BackdropLayer rect was not provided, backdrop stays inert");
                ready = false;
                return;
            }

            if (ready && root == layerRect) return;
            root = layerRect;
            for (var i = 0; i < 2; i++)
            {
                if (nodes[i] != null && images[i] != null && groups[i] != null && images[i].transform.parent == root)
                    continue;

                PresentationUi.Destroy(nodes[i]);
                var rt = PresentationUi.CreateNode(ElementName + "_" + i, root, i);
                if (rt == null) continue;
                nodes[i] = rt.gameObject;
                images[i] = PresentationUi.AddRawImage(rt);
                images[i].color = tint;
                images[i].texture = null;
                groups[i] = PresentationUi.AddGroup(nodes[i], 0f, false);
            }
            ready = true;
            VNLog.Info(LogArea, "backdrop layer bound");
        }

        /// <summary>Shows a backdrop key. Repeating the visible key never restarts a finished fade.</summary>
        public void Show(string backdropKey, VNFadeKind fade, float seconds)
        {
            if (!ready) return;
            if (string.IsNullOrEmpty(backdropKey))
            {
                WarnOnce("emptykey", "Show was called with an empty backdrop key - ignored");
                return;
            }

            var texture = assets.GetTexture(backdropKey);

            if (string.Equals(backdropKey, currentKey, StringComparison.Ordinal) && !IsTransitioning)
            {
                if (visibleIndex >= 0 && groups[visibleIndex] != null && groups[visibleIndex].alpha >= 0.999f) return;
                ApplyInstant(texture, backdropKey);
                return;
            }

            var h = ResolveHost();
            if (h == null || seconds <= 0f || fade == VNFadeKind.Cut)
            {
                ApplyInstant(texture, backdropKey);
                return;
            }

            CancelTick();
            incomingIndex = visibleIndex < 0 ? 0 : 1 - visibleIndex;
            outgoingIndex = visibleIndex;

            if (images[incomingIndex] != null)
            {
                images[incomingIndex].texture = texture;
                images[incomingIndex].transform.SetAsLastSibling();
            }
            SetAlpha(incomingIndex, 0f);

            pendingKey = backdropKey;
            fadeKind = fade;
            duration = Mathf.Max(0.01f, seconds);
            elapsed = 0f;
            tick = h.Schedule(TickFade);
        }

        /// <summary>Shows a backdrop key with the default crossfade length.</summary>
        public void Show(string backdropKey) => Show(backdropKey, VNFadeKind.CrossFade, DefaultFadeSeconds);

        /// <summary>Shows the key instantly even when it is already on screen - used after a save restore.</summary>
        public void Force(string backdropKey)
        {
            if (string.IsNullOrEmpty(backdropKey)) return;
            ApplyInstant(assets.GetTexture(backdropKey), backdropKey);
        }

        /// <summary>Fades the backdrop out. Calling it on an empty layer is a no-op.</summary>
        public void Hide(float seconds)
        {
            if (!ready) return;
            if (visibleIndex < 0 && !IsTransitioning)
            {
                currentKey = null;
                return;
            }

            var h = ResolveHost();
            if (h == null || seconds <= 0f)
            {
                HideInstant();
                return;
            }

            CancelTick();
            incomingIndex = -1;
            outgoingIndex = visibleIndex;
            pendingKey = null;
            duration = Mathf.Max(0.01f, seconds);
            elapsed = 0f;
            tick = h.Schedule(TickHide);
        }

        /// <summary>Cuts the backdrop out.</summary>
        public void Hide() => Hide(0f);

        /// <summary>Stops every animation and leaves the picture as it is.</summary>
        public void Reset()
        {
            CancelTick();
            incomingIndex = -1;
            outgoingIndex = -1;
            pendingKey = null;
        }

        /// <summary>Forgets the binding and destroys the generated elements.</summary>
        public void Dispose()
        {
            CancelTick();
            for (var i = 0; i < nodes.Length; i++)
            {
                PresentationUi.Destroy(nodes[i]);
                nodes[i] = null;
                images[i] = null;
                groups[i] = null;
            }
            root = null;
            ready = false;
            currentKey = null;
            visibleIndex = -1;
        }

        /// <summary>Warms the texture of a key without touching the picture.</summary>
        public void Preload(string backdropKey)
        {
            if (string.IsNullOrEmpty(backdropKey)) return;
            assets.HintKey(backdropKey);
            assets.GetTexture(backdropKey);
        }

        private void TickFade(float deltaTime)
        {
            elapsed += deltaTime;
            var t = Mathf.Clamp01(elapsed / duration);
            ApplyFadeCurve(t);
            if (t < 1f) return;
            Complete();
        }

        private void TickHide(float deltaTime)
        {
            elapsed += deltaTime;
            var t = Mathf.Clamp01(elapsed / duration);
            SetAlpha(outgoingIndex, 1f - t);
            if (t < 1f) return;
            visibleIndex = -1;
            currentKey = null;
            SetAlpha(outgoingIndex, 0f);
            ClearTexture(outgoingIndex);
            CancelTick();
        }

        private void ApplyFadeCurve(float t)
        {
            // CrossFade mixes both layers at once, Fade drops the old one first and only then lifts the new one.
            if (fadeKind == VNFadeKind.CrossFade)
            {
                SetAlpha(incomingIndex, t);
                SetAlpha(outgoingIndex, 1f - t);
                return;
            }
            if (t < 0.5f)
            {
                SetAlpha(outgoingIndex, 1f - t * 2f);
                SetAlpha(incomingIndex, 0f);
            }
            else
            {
                SetAlpha(outgoingIndex, 0f);
                SetAlpha(incomingIndex, (t - 0.5f) * 2f);
            }
        }

        private void Complete()
        {
            SetAlpha(incomingIndex, 1f);
            SetAlpha(outgoingIndex, 0f);
            ClearTexture(outgoingIndex);
            if (incomingIndex >= 0) visibleIndex = incomingIndex;
            currentKey = pendingKey;
            incomingIndex = -1;
            outgoingIndex = -1;
            pendingKey = null;
            CancelTick();
        }

        private void ApplyInstant(Texture texture, string key)
        {
            CancelTick();
            var index = visibleIndex < 0 ? 0 : visibleIndex;
            if (images[index] != null) images[index].texture = texture;
            SetAlpha(index, 1f);
            var other = 1 - index;
            SetAlpha(other, 0f);
            ClearTexture(other);
            if (images[index] != null) images[index].transform.SetAsLastSibling();
            visibleIndex = index;
            incomingIndex = -1;
            outgoingIndex = -1;
            pendingKey = null;
            currentKey = key;
        }

        private void HideInstant()
        {
            CancelTick();
            SetAlpha(0, 0f);
            SetAlpha(1, 0f);
            ClearTexture(0);
            ClearTexture(1);
            visibleIndex = -1;
            incomingIndex = -1;
            outgoingIndex = -1;
            pendingKey = null;
            currentKey = null;
        }

        private void SetAlpha(int index, float value)
        {
            if (index < 0 || index >= groups.Length) return;
            if (groups[index] == null) return;
            groups[index].alpha = Mathf.Clamp01(value);
        }

        private void ClearTexture(int index)
        {
            if (index < 0 || index >= images.Length) return;
            if (images[index] == null) return;
            images[index].texture = null;
        }

        private void CancelTick()
        {
            if (tick == 0) return;
            var h = host != null && host.gameObject != null ? host : PresentationHost.Instance;
            if (h != null) h.Cancel(tick);
            tick = 0;
        }

        private PresentationHost ResolveHost()
        {
            if (host != null && host.gameObject != null) return host;
            host = PresentationHost.EnsureExists();
            if (host == null) WarnOnce("nohost", "no PresentationHost available, transitions apply instantly");
            return host;
        }

        private void WarnOnce(string identity, string message)
        {
            if (!warned.Add(identity)) return;
            VNLog.Warn(LogArea, message);
        }
    }
}
