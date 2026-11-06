// REZONANCE VN ENGINE - modal screen stack (UI module).
// Rules from the task brief: exactly one modal screen on top, Esc closes the top one, when the stack
// is empty Esc belongs to the orchestrator (it opens the pause menu). IVNInput.CaptureUiOnly follows
// the stack so dialogue commands are suppressed while a menu owns input.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>A screen the router can show, hide and re-localize.</summary>
    public interface IVNScreen
    {
        /// <summary>Stable id used by <see cref="UiRouter.Open"/>.</summary>
        string ScreenId { get; }

        /// <summary>Modal screens block the dialogue and own the Esc key.</summary>
        bool IsModal { get; }

        /// <summary>Screen became the top of the stack; payload may be null.</summary>
        void OnScreenShown(object payload);

        /// <summary>Screen was covered or popped; must stop polling input.</summary>
        void OnScreenHidden();
    }

    /// <summary>Stack of modal screens with Esc handling and input capture bookkeeping.</summary>
    [DisallowMultipleComponent]
    public sealed class UiRouter : MonoBehaviour
    {
        private const string Area = "router";

        /// <summary>Live instance; the canvas owns exactly one.</summary>
        public static UiRouter Instance { get; private set; }

        private readonly Dictionary<string, IVNScreen> screens = new Dictionary<string, IVNScreen>(StringComparer.Ordinal);
        private readonly List<IVNScreen> stack = new List<IVNScreen>();
        private RectTransform host;
        private IVNInput input;
        private IVNScreen top;

        /// <summary>Raised after a screen becomes visible.</summary>
        public event Action<IVNScreen> ScreenOpened;

        /// <summary>Raised after a screen is hidden.</summary>
        public event Action<IVNScreen> ScreenClosed;

        /// <summary>Current top screen or null.</summary>
        public IVNScreen Top => top;

        /// <summary>Screens currently on the stack, bottom first.</summary>
        public IReadOnlyList<IVNScreen> Stack => stack;

        /// <summary>Depth of the stack.</summary>
        public int Depth => stack.Count;

        /// <summary>True when a modal owns the screen - the orchestrator must not route dialogue keys.</summary>
        public bool IsModalOpen
        {
            get
            {
                for (var i = 0; i < stack.Count; i++)
                    if (stack[i] != null && stack[i].IsModal) return true;
                return false;
            }
        }

        /// <summary>Esc handling is polled by the router itself; set false to drive it externally.</summary>
        public bool PollEscapeKey = true;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ScreenOpened = null;
            ScreenClosed = null;
            stack.Clear();
            screens.Clear();
        }

        // ---------------------------------------------------------------- setup

        /// <summary>Binds the layer that hosts modal screens (ModalLayer by contract).</summary>
        public UiRouter AttachLayer(string layerName)
        {
            if (UIBuilder.TryGetLayer(layerName, out var found)) host = found;
            else host = UIBuilder.GetLayer(layerName);
            return this;
        }

        /// <summary>Binds the layer that hosts modal screens directly.</summary>
        public void AttachRect(RectTransform modalHost)
        {
            if (modalHost != null) host = modalHost;
        }

        /// <summary>Where new modal screens get parented; falls back to the canvas root.</summary>
        public RectTransform Host => host != null ? host : UIBuilder.Root;

        /// <summary>Input source whose CaptureUiOnly flag follows the stack.</summary>
        public void BindInput(IVNInput input)
        {
            this.input = input;
            SyncCapture();
        }

        /// <summary>Puts a screen in the registry so HUD buttons can open it by id.</summary>
        public void Register(IVNScreen screen)
        {
            if (screen == null || string.IsNullOrEmpty(screen.ScreenId)) return;
            screens[screen.ScreenId] = screen;
        }

        /// <summary>Removes a screen; if it is visible it is closed first.</summary>
        public bool Unregister(string screenId)
        {
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out var screen)) return false;
            PopScreen(screen);
            screens.Remove(screenId);
            return true;
        }

        /// <summary>Registered screen by id.</summary>
        public bool TryGet(string screenId, out IVNScreen screen)
        {
            screen = null;
            return !string.IsNullOrEmpty(screenId) && screens.TryGetValue(screenId, out screen);
        }

        // ---------------------------------------------------------------- navigation

        /// <summary>Opens a registered screen by id; false and a warning when nobody registered it.</summary>
        public bool Open(string screenId, object payload = null)
        {
            if (!TryGet(screenId, out var screen))
            {
                VNLog.Warn(Area, "Screen is not registered: " + screenId);
                return false;
            }
            Push(screen, payload);
            return true;
        }

        /// <summary>Pushes a screen on top of the stack and hides the previous one.</summary>
        public void Push(IVNScreen screen, object payload = null)
        {
            if (screen == null) return;
            if (stack.Count > 0 && ReferenceEquals(stack[stack.Count - 1], screen))
            {
                NotifyShown(screen, payload);
                return;
            }
            HideTop();
            stack.Add(screen);
            Register(screen);
            NotifyShown(screen, payload);
        }

        /// <summary>Closes the top screen and re-shows the one underneath.</summary>
        public bool Pop()
        {
            if (stack.Count == 0) return false;
            var screen = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            NotifyHidden(screen);
            if (stack.Count > 0) NotifyShown(stack[stack.Count - 1], null);
            if (ReferenceEquals(top, screen)) top = null;
            SyncCapture();
            return true;
        }

        /// <summary>Replaces the top of the stack (Save to Load and back).</summary>
        public void Replace(IVNScreen screen, object payload = null)
        {
            if (stack.Count > 0)
            {
                var old = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                NotifyHidden(old);
            }
            Push(screen, payload);
        }

        /// <summary>Closes every screen (returning to the main menu, quitting).</summary>
        public void CloseAll()
        {
            while (stack.Count > 0) Pop();
        }

        /// <summary>Closes a specific screen wherever it sits in the stack.</summary>
        public void PopScreen(IVNScreen screen)
        {
            if (screen == null) return;
            var index = stack.IndexOf(screen);
            if (index < 0)
            {
                NotifyHidden(screen);
                return;
            }
            while (stack.Count - 1 > index) Pop();
            Pop();
        }

        /// <summary>True when this screen id is the visible one.</summary>
        public bool IsOpen(string screenId)
        {
            return top != null && string.Equals(top.ScreenId, screenId, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- escape

        private void Update()
        {
            if (!PollEscapeKey || stack.Count == 0) return;
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (!keyboard.escapeKey.wasPressedThisFrame) return;
            UIBuilder.Safe("router.escape", HandleEscape);
        }

        /// <summary>
        /// Closes the top screen. The orchestrator may call it itself; when it returns false the
        /// Esc press is free and should escalate to the pause menu.
        /// </summary>
        public bool HandleEscape()
        {
            if (stack.Count == 0) return false;
            var screen = stack[stack.Count - 1];
            if (screen is IUiEscapeHandler handler)
            {
                if (handler.EscapePressed()) return true;
            }
            Pop();
            return true;
        }

        // ---------------------------------------------------------------- internals

        private void HideTop()
        {
            if (stack.Count == 0) return;
            NotifyHidden(stack[stack.Count - 1]);
        }

        private void NotifyShown(IVNScreen screen, object payload)
        {
            top = screen;
            SyncCapture();
            try { screen.OnScreenShown(payload); }
            catch (Exception e) { VNLog.Error(Area, "OnScreenShown(" + screen.ScreenId + ") failed: " + e.Message); }
            try { ScreenOpened?.Invoke(screen); }
            catch (Exception e) { VNLog.Error(Area, "ScreenOpened failed: " + e.Message); }
        }

        private void NotifyHidden(IVNScreen screen)
        {
            if (ReferenceEquals(top, screen)) top = null;
            try { screen.OnScreenHidden(); }
            catch (Exception e) { VNLog.Error(Area, "OnScreenHidden(" + screen.ScreenId + ") failed: " + e.Message); }
            try { ScreenClosed?.Invoke(screen); }
            catch (Exception e) { VNLog.Error(Area, "ScreenClosed failed: " + e.Message); }
            SyncCapture();
        }

        private void SyncCapture()
        {
            if (input == null) return;
            try { input.CaptureUiOnly = IsModalOpen; }
            catch (Exception e) { VNLog.Warn(Area, "CaptureUiOnly update failed: " + e.Message); }
        }
    }

    /// <summary>A screen that wants to intercept Esc before the router pops it (confirmation dialogs).</summary>
    public interface IUiEscapeHandler
    {
        /// <summary>Return true when the press was consumed inside the screen.</summary>
        bool EscapePressed();
    }
}
