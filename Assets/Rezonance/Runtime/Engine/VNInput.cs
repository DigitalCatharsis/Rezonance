// REZONANCE VN ENGINE - new Input System adapter (ARCHITECTURE.md 9.7).
// Only UnityEngine.InputSystem is used; legacy UnityEngine.Input is disabled in this project.
// Actions are built in code so the bindings match the table exactly and live in one readable place.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Rezonance.VN.Engine
{
    /// <summary>Keyboard/mouse/touch front-end that raises logical <see cref="VNCommand"/> values only.</summary>
    public sealed class VNInput : IVNInput
    {
        private const string Area = "Input";
        private const int MaxChoiceDigit = 9;

        private readonly List<InputAction> actions = new List<InputAction>(16);
        private readonly Dictionary<Key, VNCommand> keyCommands = new Dictionary<Key, VNCommand>(24);

        private InputAction advanceAction;
        private InputAction chooseAction;
        private InputAction navigateAction;
        private InputAction pointerAction;

        private bool enabled;
        private bool disposed;
        private bool captureUiOnly;
        private int choiceCount;
        private int lastNavigateIndex;

        /// <inheritdoc/>
        public event Action<VNCommand> Command;

        /// <inheritdoc/>
        public event Action<int> ChoiceSelected;

        /// <inheritdoc/>
        public event Action<Vector2> PointerDelta;

        /// <inheritdoc/>
        public bool CaptureUiOnly
        {
            get => captureUiOnly;
            set => captureUiOnly = value;
        }

        /// <inheritdoc/>
        public int ChoiceCount
        {
            get => choiceCount;
            set => choiceCount = value < 0 ? 0 : value;
        }

        /// <summary>Builds the bindings; nothing is polled until <see cref="Enable"/> is called.</summary>
        public VNInput()
        {
            // Advance: Space / Enter / left mouse button; tap on touch devices.
            advanceAction = Create("Advance", InputActionType.Button);
            advanceAction.AddBinding("<Keyboard>/space");
            advanceAction.AddBinding("<Keyboard>/enter");
            advanceAction.AddBinding("<Keyboard>/numpadEnter");
            advanceAction.AddBinding("<Mouse>/leftButton");
            advanceAction.AddBinding("<Touchscreen>/primaryTouch/press");
            advanceAction.performed += OnAdvance;

            // Choose: digits 1..9. Clicks on options are handled by the UI, which calls runtime.Choose itself.
            chooseAction = Create("Choose", InputActionType.Button);
            for (var d = 1; d <= MaxChoiceDigit; d++)
            {
                chooseAction.AddBinding("<Keyboard>/digit" + d);
                chooseAction.AddBinding("<Keyboard>/numpad" + d);
            }
            chooseAction.performed += OnChoose;

            navigateAction = Create("Navigate", InputActionType.PassThrough);
            navigateAction.AddBinding("<Keyboard>/upArrow");
            navigateAction.AddBinding("<Keyboard>/downArrow");
            navigateAction.AddBinding("<Keyboard>/leftArrow");
            navigateAction.AddBinding("<Keyboard>/rightArrow");
            navigateAction.performed += OnNavigate;

            pointerAction = Create("Pointer", InputActionType.PassThrough);
            pointerAction.AddBinding("<Mouse>/scroll");
            pointerAction.AddBinding("<Touchscreen>/primaryTouch/delta");
            pointerAction.performed += OnPointer;

            // One key per command; modifiers get both halves.
            Bind(VNCommand.ToggleAuto, Key.LeftCtrl, Key.RightCtrl);
            Bind(VNCommand.ToggleSkip, Key.LeftShift, Key.RightShift);
            Bind(VNCommand.ToggleFast, Key.F);
            Bind(VNCommand.OpenMenu, Key.Escape, Key.Tab);
            Bind(VNCommand.QuickSave, Key.F5);
            Bind(VNCommand.QuickLoad, Key.F9);
            Bind(VNCommand.OpenBacklog, Key.H);
            Bind(VNCommand.OpenGlossary, Key.J);
            Bind(VNCommand.OpenSettings, Key.F1);
            Bind(VNCommand.HideUi, Key.Period);
        }

        /// <inheritdoc/>
        public void Enable()
        {
            if (disposed || enabled) return;
            enabled = true;
            for (var i = 0; i < actions.Count; i++) actions[i].Enable();
            VNLog.Info(Area, "input enabled");
        }

        /// <inheritdoc/>
        public void Disable()
        {
            if (!enabled) return;
            enabled = false;
            for (var i = 0; i < actions.Count; i++) actions[i].Disable();
            VNLog.Info(Area, "input disabled");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Disable();
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                action.performed -= OnAdvance;
                action.performed -= OnChoose;
                action.performed -= OnNavigate;
                action.performed -= OnPointer;
                action.Dispose();
            }
            actions.Clear();
            keyCommands.Clear();
            Command = null;
            ChoiceSelected = null;
            PointerDelta = null;
        }

        // ------------------------------------------------------------------ callbacks

        private void OnAdvance(InputAction.CallbackContext ctx)
        {
            // A click that lands on a button, slider or any other UI graphic must not turn the page.
            if (IsPointerOverUi()) return;
            Emit(VNCommand.Advance);
        }

        private void OnChoose(InputAction.CallbackContext ctx)
        {
            var index = ChoiceIndexFromControl(ctx.control);
            if (index < 0) return;
            if (captureUiOnly) return;
            if (choiceCount > 0 && index >= choiceCount)
            {
                VNLog.Info(Area, "choice " + (index + 1) + " ignored, only " + choiceCount + " offered");
                return;
            }
            ChoiceSelected?.Invoke(index);
        }

        private void OnNavigate(InputAction.CallbackContext ctx)
        {
            if (captureUiOnly) return;
            var dir = ctx.ReadValue<Vector2>();
            if (Mathf.Abs(dir.y) >= Mathf.Abs(dir.x))
                lastNavigateIndex += dir.y < 0f ? 1 : -1;
            else
                lastNavigateIndex += dir.x < 0f ? -1 : 1;

            var span = choiceCount > 0 ? choiceCount : 1;
            lastNavigateIndex = ((lastNavigateIndex % span) + span) % span;
            Command?.Invoke(VNCommand.Navigate);
        }

        private void OnPointer(InputAction.CallbackContext ctx)
        {
            var value = ctx.ReadValue<Vector2>();
            if (value.sqrMagnitude <= 0.0001f) return;
            PointerDelta?.Invoke(value);
        }

        private void Emit(VNCommand command)
        {
            // While a modal owns input: dialogue commands are dropped and Escape/Tab means "close it".
            if (captureUiOnly)
            {
                if (command == VNCommand.OpenMenu) Command?.Invoke(VNCommand.CloseMenu);
                else if (IsDialogueCommand(command)) { }
                else Command?.Invoke(command);
                return;
            }
            Command?.Invoke(command);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Adds one action with the listed keys; the command is raised through <see cref="Emit"/>.</summary>
        private void Bind(VNCommand command, params Key[] keys)
        {
            var action = Create(command.ToString(), InputActionType.Button);
            for (var i = 0; i < keys.Length; i++)
            {
                action.AddBinding("<Keyboard>/" + keys[i]);
                keyCommands[keys[i]] = command;
            }
            action.performed += ctx => Emit(command);
        }

        private InputAction Create(string name, InputActionType type)
        {
            var action = new InputAction(name, type);
            actions.Add(action);
            return action;
        }

        /// <summary>Maps "digit4" / "numpad4" to the zero-based option index, -1 when the control is not a digit.</summary>
        private static int ChoiceIndexFromControl(InputControl control)
        {
            if (control == null) return -1;
            var name = control.name;
            if (name == null) return -1;
            for (var i = name.Length - 1; i >= 0 && i >= name.Length - 4; i--)
            {
                var c = name[i];
                if (c < '0' || c > '9') continue;
                var digit = c - '0';
                return digit >= 1 && digit <= MaxChoiceDigit ? digit - 1 : -1;
            }
            return -1;
        }

        private static bool IsDialogueCommand(VNCommand command)
        {
            switch (command)
            {
                case VNCommand.Advance:
                case VNCommand.Choose:
                case VNCommand.ToggleAuto:
                case VNCommand.ToggleSkip:
                case VNCommand.ToggleFast:
                case VNCommand.HideUi:
                case VNCommand.Navigate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True when the pointer currently hovers a raycastable UI element (button, slider, scroll rect).</summary>
        private static bool IsPointerOverUi()
        {
            var es = EventSystem.current;
            if (es == null) return false;
            try
            {
                var mouse = Mouse.current;
                if (mouse != null && mouse.leftButton.isPressed) return es.IsPointerOverGameObject();

                var touch = Touchscreen.current;
                if (touch != null && touch.primaryTouch.press.isPressed)
                    return es.IsPointerOverGameObject(touch.primaryTouch.touchId.ReadValue());
            }
            catch (Exception e)
            {
                // EventSystem can be mid-rebuild; treating it as "not over UI" keeps the story readable.
                VNLog.Warn(Area, "UI hover check failed: " + e.Message);
            }
            return false;
        }
    }
}
