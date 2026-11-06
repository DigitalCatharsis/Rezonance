// REZONANCE VN ENGINE - shared ticker for presentation animations.
// One hidden GameObject drives every fade in the presentation layer with UNSCALED time, so a pause
// menu or a frozen Time.timeScale never freezes a transition. Owned by the presentation module.
using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Rezonance.VN.Core;
using UnityEngine;

namespace Rezonance.VN.Presentation
{
    /// <summary>Singleton MonoBehaviour that ticks presentation animations and owns the shared cancellation token.</summary>
    public sealed class PresentationHost : MonoBehaviour
    {
        /// <summary>Name of the generated GameObject.</summary>
        public const string HostName = "VN_PresentationHost";

        private const string LogArea = "presentation";

        private readonly System.Collections.Generic.List<TickEntry> ticks =
            new System.Collections.Generic.List<TickEntry>(16);

        private CancellationTokenSource cancellation;
        private int nextHandle = 1;
        private bool inUpdate;

        /// <summary>Live instance, null after the host object is destroyed.</summary>
        public static PresentationHost Instance { get; private set; }

        /// <summary>Token cancelled when the host dies; every UniTask started by presentation must use it.</summary>
        public CancellationToken Token => cancellation != null ? cancellation.Token : CancellationToken.None;

        /// <summary>True when at least one animation is running.</summary>
        public bool IsTicking => ticks.Count > 0;

        /// <summary>Number of scheduled animations - debug helper.</summary>
        public int TickCount => ticks.Count;

        /// <summary>Returns the live host, creating the GameObject on first use.</summary>
        public static PresentationHost EnsureExists()
        {
            if (Instance != null && Instance.gameObject != null) return Instance;
            var go = new GameObject(HostName);
            var host = go.AddComponent<PresentationHost>();
            if (host == null)
            {
                VNLog.Error(LogArea, "could not create " + HostName);
                UnityEngine.Object.Destroy(go);
                return null;
            }
            return host;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this && Instance.gameObject != null)
            {
                VNLog.Warn(LogArea, "duplicate " + HostName + " destroyed");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            cancellation = new CancellationTokenSource();
        }

        /// <summary>
        /// Schedules a per-frame callback. It receives unscaled delta time and must call
        /// <see cref="Cancel"/> on its own handle when done. Returns a handle, or 0 when nothing was created.
        /// </summary>
        public int Schedule(Action<float> onTick, Action onCompleted = null)
        {
            if (onTick == null) return 0;
            var entry = new TickEntry
            {
                handle = nextHandle++,
                update = onTick,
                completed = onCompleted
            };
            ticks.Add(entry);
            return entry.handle;
        }

        /// <summary>Cancels a scheduled tick. Safe to call with a stale handle, never invokes onCompleted.</summary>
        public void Cancel(int handle)
        {
            if (handle == 0) return;
            for (var i = 0; i < ticks.Count; i++)
            {
                if (ticks[i].handle != handle) continue;
                ticks[i].done = true;
                if (!inUpdate) ticks.RemoveAt(i);
                return;
            }
        }

        /// <summary>Cancels every running animation without completing it.</summary>
        public void CancelAll()
        {
            for (var i = 0; i < ticks.Count; i++) ticks[i].done = true;
            ticks.Clear();
        }

        /// <summary>Starts a coroutine on the host; returns null when the routine cannot run.</summary>
        public Coroutine Start(IEnumerator routine)
        {
            if (routine == null) return null;
            try { return StartCoroutine(routine); }
            catch (Exception ex)
            {
                VNLog.Error(LogArea, "coroutine failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>One frame hop on the shared token; throws OperationCanceledException when the host dies.</summary>
        public UniTask YieldFrame(CancellationToken token = default)
        {
            var linked = Link(token);
            return UniTask.Yield(PlayerLoopTiming.Update, linked);
        }

        /// <summary>Waits real time regardless of Time.timeScale; completes instantly for non positive seconds.</summary>
        public UniTask WaitUnscaled(float seconds, CancellationToken token = default)
        {
            if (seconds <= 0f) return UniTask.CompletedTask;
            return UniTask.Delay(TimeSpan.FromSeconds(seconds), DelayType.UnscaledDeltaTime,
                                PlayerLoopTiming.Update, Link(token));
        }

        /// <summary>Combines the caller token with the host token so a destroyed host always cancels the work.</summary>
        public CancellationToken Link(CancellationToken token)
        {
            var hostToken = Token;
            if (!hostToken.CanBeCanceled) return token;
            if (!token.CanBeCanceled) return hostToken;
            return CancellationTokenSource.CreateLinkedTokenSource(hostToken, token).Token;
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;
            inUpdate = true;
            var count = ticks.Count;
            for (var i = 0; i < count; i++)
            {
                var entry = ticks[i];
                if (entry.done) continue;
                try
                {
                    entry.update(dt);
                }
                catch (Exception ex)
                {
                    entry.done = true;
                    VNLog.Error(LogArea, "tick " + entry.handle + " failed: " + ex.Message);
                }
            }

            inUpdate = false;
            for (var i = ticks.Count - 1; i >= 0; i--)
            {
                if (!ticks[i].done) continue;
                var done = ticks[i];
                ticks.RemoveAt(i);
                try { done.completed?.Invoke(); }
                catch (Exception ex)
                {
                    VNLog.Error(LogArea, "tick completion failed: " + ex.Message);
                }
            }
        }

        private void OnDestroy()
        {
            CancelAll();
            if (cancellation != null)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                cancellation.Dispose();
                cancellation = null;
            }
            if (Instance == this) Instance = null;
        }

        private sealed class TickEntry
        {
            public int handle;
            public Action<float> update;
            public Action completed;
            public bool done;
        }
    }
}
