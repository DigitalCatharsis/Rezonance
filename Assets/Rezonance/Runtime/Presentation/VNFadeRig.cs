// REZONANCE VN ENGINE - shared opacity ramp plus auto-hide timer for presentation elements.
// Ramps are ticked by PresentationHost with unscaled time, so a paused game never freezes a transition.
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Rezonance.VN.Core;
using UnityEngine;

namespace Rezonance.VN.Presentation
{
    /// <summary>Element whose opacity a VNFade rig drives.</summary>
    internal interface IFadeTarget
    {
        /// <summary>Current opacity of the element, 0..1.</summary>
        float FadeAlpha { get; set; }

        /// <summary>Called right before the element becomes visible - activate the node here.</summary>
        void OnFadeStarted();

        /// <summary>Called when a ramp finished; hidden tells whether the element ended transparent.</summary>
        void OnFadeFinished(bool hidden);
    }

    /// <summary>Lifecycle phase of one VNFade rig.</summary>
    internal enum VNFadePhase
    {
        /// <summary>Transparent, nothing scheduled.</summary>
        Hidden = 0,
        /// <summary>Opacity rising.</summary>
        FadeIn = 1,
        /// <summary>Fully visible with no timer attached.</summary>
        Shown = 2,
        /// <summary>Fully visible, auto-hide counting down.</summary>
        Hold = 3,
        /// <summary>Opacity falling.</summary>
        FadeOut = 4
    }

    /// <summary>Opacity ramp with an optional dwell window for a single screen element.</summary>
    internal sealed class VNFade
    {
        private const string LogArea = "fade";

        private readonly IFadeTarget target;
        private float rampSeconds;
        private PresentationHost host;
        private float defaultHold;
        private int tick;
        private float elapsed;
        private float from;
        private float to = 1f;
        private float hold;
        private VNFadePhase phase = VNFadePhase.Hidden;
        private CancellationTokenSource holdSource;
        private bool disposed;

        /// <summary>Builds the rig. A zero hold keeps the element visible until it is hidden explicitly.</summary>
        public VNFade(IFadeTarget fadeTarget, PresentationHost animationHost, float holdSeconds, float fadeSeconds)
        {
            target = fadeTarget;
            host = animationHost;
            defaultHold = Mathf.Max(0f, holdSeconds);
            rampSeconds = Mathf.Max(0f, fadeSeconds);
            hold = defaultHold;
        }

        /// <summary>Current lifecycle phase.</summary>
        public VNFadePhase Phase => disposed ? VNFadePhase.Hidden : phase;

        /// <summary>True when the element is visible or animating towards being visible.</summary>
        public bool IsVisible => !disposed && phase != VNFadePhase.Hidden;

        /// <summary>True while the dwell window counts down.</summary>
        public bool IsWaiting => !disposed && phase == VNFadePhase.Hold;

        /// <summary>True while a ramp or a dwell timer is live.</summary>
        public bool IsBusy => tick != 0 || holdSource != null;

        /// <summary>Current opacity of the element.</summary>
        public float Alpha => target != null ? target.FadeAlpha : 0f;

        /// <summary>Dwell length currently armed.</summary>
        public float HoldSeconds => hold;

        /// <summary>Swaps the ticker after a scene rebuild, resuming whatever phase was active.</summary>
        public void AttachHost(PresentationHost animationHost)
        {
            if (ReferenceEquals(host, animationHost)) return;
            CancelTimers();
            host = animationHost;
            if (phase == VNFadePhase.FadeIn || phase == VNFadePhase.FadeOut) StartRamp();
            else if (phase == VNFadePhase.Hold) StartHold();
        }

        /// <summary>Changes the dwell length used by later Show calls.</summary>
        public void SetDefaultHold(float seconds)
        {
            defaultHold = Mathf.Max(0f, seconds);
        }

        /// <summary>Fades in, then enters the dwell window. Negative hold keeps the configured default.</summary>
        public void Show(float holdOverride = -1f)
        {
            if (disposed || target == null) return;
            if (holdOverride >= 0f) hold = holdOverride;
            else hold = defaultHold;

            CancelHold();
            if (phase == VNFadePhase.FadeIn) return;
            if (phase == VNFadePhase.Shown || phase == VNFadePhase.Hold)
            {
                EnterDwell();
                return;
            }

            from = target.FadeAlpha;
            to = 1f;
            elapsed = 0f;
            target.OnFadeStarted();
            phase = VNFadePhase.FadeIn;
            StartRamp();
        }

        /// <summary>Keeps opacity, restarts the dwell countdown.</summary>
        public void Refresh(float holdOverride = -1f)
        {
            if (disposed || target == null) return;
            if (holdOverride >= 0f) hold = holdOverride;
            else hold = defaultHold;

            if (phase == VNFadePhase.Hidden) { Show(holdOverride); return; }
            if (phase == VNFadePhase.FadeOut)
            {
                CancelHold();
                from = target.FadeAlpha;
                to = 1f;
                elapsed = 0f;
                target.OnFadeStarted();
                phase = VNFadePhase.FadeIn;
                StartRamp();
                return;
            }
            if (phase == VNFadePhase.FadeIn) return;
            EnterDwell();
        }

        /// <summary>Starts hiding: ramps down, or ends at once when the element was only dwelling.</summary>
        public void FadeOut()
        {
            if (disposed || target == null) return;
            CancelHold();
            if (phase == VNFadePhase.Hidden) return;
            from = target.FadeAlpha;
            to = 0f;
            elapsed = 0f;
            phase = VNFadePhase.FadeOut;
            StartRamp();
        }

        /// <summary>Removes every timer and leaves the element transparent.</summary>
        public void HideInstant()
        {
            if (target == null) return;
            CancelTimers();
            phase = VNFadePhase.Hidden;
            target.FadeAlpha = 0f;
            target.OnFadeFinished(true);
        }

        /// <summary>Cancels scheduled work without touching opacity.</summary>
        public void CancelTimers()
        {
            CancelTick();
            CancelHold();
        }

        /// <summary>Releases the rig; later calls do nothing.</summary>
        public void Dispose()
        {
            CancelTimers();
            disposed = true;
            phase = VNFadePhase.Hidden;
        }

        /// <summary>Completes when neither a ramp nor a dwell timer is live any more.</summary>
        public async UniTask WaitUntilIdle(CancellationToken token = default)
        {
            var linked = host != null ? host.Link(token) : token;
            while (IsBusy && !linked.IsCancellationRequested)
                await UniTask.Yield(PlayerLoopTiming.Update, linked);
        }

        private void StartRamp()
        {
            CancelTick();
            if (host == null || host.gameObject == null || rampSeconds <= 0f)
            {
                ApplyInstant(to);
                return;
            }
            if (Mathf.Abs(from - to) < 0.0005f)
            {
                FinishRamp();
                return;
            }
            tick = host.Schedule(TickRamp);
        }

        private void StartHold()
        {
            CancelHold();
            if (host == null || host.gameObject == null)
            {
                phase = VNFadePhase.Shown;
                return;
            }
            phase = VNFadePhase.Hold;
            holdSource = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
            HoldLoop(holdSource.Token).Forget();
        }

        private async UniTaskVoid HoldLoop(CancellationToken token)
        {
            var h = host;
            if (h == null) return;
            try
            {
                await h.WaitUnscaled(hold, token);
                if (disposed || phase != VNFadePhase.Hold) return;
                CancelHold();
                FadeOut();
            }
            catch (OperationCanceledException)
            {
                // Expected when the element is hidden early or the host dies.
            }
            catch (Exception ex)
            {
                VNLog.Warn(LogArea, "dwell timer failed: " + ex.Message);
            }
        }

        private void TickRamp(float deltaTime)
        {
            if (disposed || target == null)
            {
                CancelTick();
                return;
            }
            elapsed += deltaTime;
            var t = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, rampSeconds));
            target.FadeAlpha = Mathf.Clamp01(Mathf.Lerp(from, to, t));
            if (t < 1f) return;
            CancelTick();
            FinishRamp();
        }

        private void FinishRamp()
        {
            target.FadeAlpha = Mathf.Clamp01(to);
            if (to <= 0f)
            {
                phase = VNFadePhase.Hidden;
                target.OnFadeFinished(true);
                return;
            }
            target.OnFadeFinished(false);
            EnterDwell();
        }

        private void ApplyInstant(float destination)
        {
            CancelTick();
            if (destination > 0f) target.OnFadeStarted();
            to = destination;
            target.FadeAlpha = Mathf.Clamp01(destination);
            FinishRamp();
        }

        private void EnterDwell()
        {
            if (hold > 0f) StartHold();
            else
            {
                CancelHold();
                phase = VNFadePhase.Shown;
            }
        }

        private void CancelTick()
        {
            if (tick == 0) return;
            if (host != null) host.Cancel(tick);
            tick = 0;
        }

        private void CancelHold()
        {
            if (holdSource == null) return;
            try { holdSource.Cancel(); }
            catch (ObjectDisposedException) { }
            holdSource.Dispose();
            holdSource = null;
        }
    }
}
