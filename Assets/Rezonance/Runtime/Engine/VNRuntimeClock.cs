// REZONANCE VN ENGINE - session playtime accumulator.
// Time-based rather than per-frame: the clock stores the running segment start and computes elapsed
// seconds on read, so nothing has to pump an Update and paused spans are never counted.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;
using UnityEngine;

namespace Rezonance.VN.Engine
{
    /// <summary>Wall clock of the play session, paused while menus, modals or mini-games own the screen.</summary>
    public sealed class VNRuntimeClock
    {
        /// <summary>Who can stop the clock; several sources may hold it at once.</summary>
        [Flags]
        public enum PauseSource
        {
            None = 0,
            /// <summary>A modal screen (settings, backlog, glossary, save/load) is open.</summary>
            Modal = 1,
            /// <summary>A choice menu is waiting for the player.</summary>
            Menu = 2,
            /// <summary>A mini-game owns the screen.</summary>
            MiniGame = 4
        }

        private readonly Dictionary<PauseSource, int> holds = new Dictionary<PauseSource, int>();
        private readonly VNRuntimeDependencies deps;
        private double segmentStart;
        private double accumulated;
        private bool running;
        private PauseSource mask = PauseSource.None;

        /// <summary>Creates the clock and starts it running.</summary>
        public VNRuntimeClock(VNRuntimeDependencies deps = null)
        {
            this.deps = deps ?? VNRuntimeDependencies.CreateEmpty();
            Start();
        }

        /// <summary>Seconds played, excluding paused spans. Written to VNGameState.playtimeSeconds.</summary>
        public float PlaytimeSeconds
        {
            get
            {
                var now = Now();
                return (float)(accumulated + (running ? Math.Max(0, now - segmentStart) : 0));
            }
        }

        /// <summary>True when at least one source holds the clock.</summary>
        public bool IsPaused => mask != PauseSource.None;

        /// <summary>Marks the start of a running segment; a no-op when already running.</summary>
        public void Start()
        {
            if (running) return;
            running = true;
            segmentStart = Now();
        }

        /// <summary>Folds the open segment into the accumulator; a no-op when already stopped.</summary>
        public void Stop()
        {
            if (!running) return;
            accumulated += Math.Max(0, Now() - segmentStart);
            running = false;
        }

        /// <summary>Adds one hold for a source and stops the clock.</summary>
        public void Pause(PauseSource source) => SetPaused(source, true);

        /// <summary>Removes one hold for a source; the clock resumes when nobody holds it.</summary>
        public void Resume(PauseSource source) => SetPaused(source, false);

        /// <summary>Counts holds per source so two screens may pause the same span without unlocking it early.</summary>
        public void SetPaused(PauseSource source, bool on)
        {
            if (source == PauseSource.None) return;
            holds.TryGetValue(source, out var depth);
            depth = on ? depth + 1 : depth - 1;
            if (depth > 0) holds[source] = depth;
            else holds.Remove(source);

            var next = PauseSource.None;
            foreach (var key in holds.Keys) next |= key;

            if (next == mask) return;
            var wasPaused = mask != PauseSource.None;
            var isPausedNow = next != PauseSource.None;
            mask = next;
            if (!wasPaused && isPausedNow) Stop();
            else if (wasPaused && !isPausedNow) Start();
        }

        /// <summary>Replaces the accumulated time; used when a save is restored.</summary>
        public void Set(float seconds)
        {
            var wasRunning = running;
            if (running) Stop();
            accumulated = seconds < 0f ? 0 : seconds;
            segmentStart = Now();
            if (wasRunning) running = true;
        }

        /// <summary>Drops the accumulated time.</summary>
        public void Reset()
        {
            accumulated = 0;
            Start();
        }

        /// <summary>Time source hook; replace in tests to step the clock deterministically.</summary>
        public Func<double> NowProvider;

        private double Now()
        {
            if (NowProvider != null) return NowProvider();
            return Time.unscaledTimeAsDouble;
        }
    }
}
