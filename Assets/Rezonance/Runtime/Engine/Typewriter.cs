// REZONANCE VN ENGINE - character reveal for TMP text.
// Driven as a UniTask loop: no per-character allocation (the visible window moves through one string
// assignment) and punctuation holds of 0.25 * autoReadDelaySeconds. A Mono coroutine fallback runs only
// when the caller passed a MonoBehaviour and UniTask is unavailable at runtime.
using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using Rezonance.VN.Core;
using TMPro;
using UnityEngine;

namespace Rezonance.VN.Engine
{
    /// <summary>Progressive text reveal helper. Static so the runtime needs no scene object of its own.</summary>
    public static class Typewriter
    {
        private const int PunctuationBudget = 4;

        /// <summary>Em dash, the long pause mark in the source.</summary>
        public const char Dash = '\u2014';

        /// <summary>Vertical ellipsis.</summary>
        public const char Ellipsis = '\u2026';

        /// <summary>True when the character makes the reader pause.</summary>
        public static bool IsPauseChar(char c)
        {
            return c == '.' || c == ',' || c == ':' || c == ';' || c == '!' || c == '?' || c == Dash || c == Ellipsis;
        }

        /// <summary>
        /// Reveals <paramref name="content"/> one character at a time inside <paramref name="target"/>.
        /// Completion (natural, cancelled or instant) resolves the returned task.
        /// </summary>
        /// <param name="content">Full localized line.</param>
        /// <param name="target">TMP component to reveal; null = timing only, nothing is painted.</param>
        /// <param name="cps">Characters per second; zero or negative means instant.</param>
        /// <param name="readDelay">Base reading delay, scaled by punctuation.</param>
        /// <param name="speedMultiplier">Fast mode multiplier from the runtime.</param>
        /// <param name="startVisible">How many characters start already shown (resuming a partial reveal).</param>
        /// <param name="ct">Cancellation from the owning node; the text stays where it was left.</param>
        /// <param name="onChar">Optional per-character hook for SFX; may be null.</param>
        public static async UniTask Play(
            string content,
            TMP_Text target,
            float cps,
            float readDelay,
            float speedMultiplier,
            int startVisible,
            System.Threading.CancellationToken ct,
            Action<int> onChar = null)
        {
            if (target != null) target.maxVisibleCharacters = 0;
            if (string.IsNullOrEmpty(content)) return;

            if (cps <= 0f || speedMultiplier <= 0f || ct.IsCancellationRequested)
            {
                ShowAll(target, content);
                return;
            }

            if (target != null) target.text = content;
            var length = content.Length;
            var revealed = Clamp(startVisible, 0, length);
            var lastApplied = -1;
            if (revealed > 0 && target != null)
            {
                target.maxVisibleCharacters = revealed;
                lastApplied = revealed;
            }

            var baseStep = 1f / cps;
            var punctuation = 0;
            var punctuationSeconds = 0f;
            var nextStep = baseStep;
            var elapsed = 0f;
            var lastTime = Time.unscaledTime;

            while (!ct.IsCancellationRequested && revealed < length)
            {
                var now = Time.unscaledTime;
                var frame = now - lastTime;
                // Tabbed-out frames are skipped by the player, not paid for by the text.
                if (frame > 0.25f) frame = baseStep;
                lastTime = now;
                elapsed += frame * speedMultiplier;

                while (elapsed >= nextStep && revealed < length && !ct.IsCancellationRequested)
                {
                    elapsed -= nextStep;
                    var c = content[revealed];
                    revealed++;
                    onChar?.Invoke(revealed);

                    nextStep = baseStep;
                    if (IsPauseChar(c) && punctuation < PunctuationBudget)
                    {
                        punctuation++;
                        var scaled = readDelay * VNDefaults.PunctuationHoldScale;
                        if (scaled > 0f) punctuationSeconds += scaled;
                        nextStep += scaled;
                    }
                }

                if (target != null && revealed != lastApplied)
                {
                    target.maxVisibleCharacters = revealed;
                    lastApplied = revealed;
                }

                if (revealed >= length) break;
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            // The reading hold is cancelled together with the node, so it only matters on the natural path.
            var tail = punctuationSeconds - elapsed;
            if (tail > 0f && !ct.IsCancellationRequested)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(tail), DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, ct);
            }
            ShowAll(target, content);
        }

        /// <summary>Instantly finishes a line already being revealed.</summary>
        public static void Finish(TMP_Text target, string content)
        {
            ShowAll(target, content);
        }

        /// <summary>
        /// Coroutine variant for hosts that drive presentation from a MonoBehaviour.
        /// Same pacing rules as <see cref="Play"/>; stop it by calling <see cref="Finish"/>.
        /// </summary>
        public static IEnumerator PlayCoroutine(
            string content,
            TMP_Text target,
            float cps,
            float readDelay,
            float speedMultiplier,
            System.Threading.CancellationToken ct)
        {
            if (target != null) target.maxVisibleCharacters = 0;
            if (string.IsNullOrEmpty(content) || cps <= 0f)
            {
                ShowAll(target, content);
                yield break;
            }

            target.text = content;
            var length = content.Length;
            var revealed = 0;
            var baseStep = 1f / cps;
            var punctuation = 0;
            var nextStep = baseStep;
            var elapsed = 0f;
            var lastTime = Time.unscaledTime;

            while (revealed < length && !ct.IsCancellationRequested)
            {
                var now = Time.unscaledTime;
                var frame = now - lastTime;
                if (frame > 0.25f) frame = baseStep;
                lastTime = now;
                elapsed += frame * speedMultiplier;

                while (elapsed >= nextStep && revealed < length)
                {
                    elapsed -= nextStep;
                    var c = content[revealed];
                    revealed++;
                    nextStep = baseStep;
                    if (IsPauseChar(c) && punctuation < PunctuationBudget)
                    {
                        punctuation++;
                        nextStep += readDelay * VNDefaults.PunctuationHoldScale;
                    }
                }

                if (target != null) target.maxVisibleCharacters = revealed;
                yield return null;
            }

            ShowAll(target, content);
        }

        private static void ShowAll(TMP_Text target, string content)
        {
            if (target == null) return;
            if (!string.IsNullOrEmpty(content)) target.text = content;
            target.maxVisibleCharacters = content != null ? content.Length : 0;
        }

        private static int Clamp(int value, int lo, int hi)
        {
            if (value < lo) return lo;
            return value > hi ? hi : value;
        }
    }
}
