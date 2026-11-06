// REZONANCE VN ENGINE - input contract.
// Only the new Input System is enabled in this project (activeInputHandler = 1), so legacy
// UnityEngine.Input must not be used anywhere. The engine module owns the implementation, UI consumes events.
using System;

namespace Rezonance.VN.Core
{
    /// <summary>Logical gameplay commands, raised by whatever backend (keyboard, mouse, touch, gamepad).</summary>
    public enum VNCommand
    {
        Advance = 0,
        Choose = 1,
        ToggleAuto = 2,
        ToggleSkip = 3,
        ToggleFast = 4,
        OpenMenu = 5,
        CloseMenu = 6,
        QuickSave = 7,
        QuickLoad = 8,
        OpenBacklog = 9,
        OpenGlossary = 10,
        OpenSettings = 11,
        HideUi = 12,
        Navigate = 13
    }

    /// <summary>
    /// Input source for the VN. Implementations must respect <see cref="CaptureUiOnly"/>: while a menu
    /// or a modal screen owns input, dialogue commands have to be suppressed.
    /// </summary>
    public interface IVNInput : IDisposable
    {
        event Action<VNCommand> Command;
        /// <summary>Raised with the option index when the player picks a choice by keyboard, click or tap.</summary>
        event Action<int> ChoiceSelected;
        /// <summary>Normalized drag/position for backlog scrolling, or null when idle.</summary>
        event Action<UnityEngine.Vector2> PointerDelta;

        /// <summary>True when a UI screen owns input; dialogue commands are then suppressed.</summary>
        bool CaptureUiOnly { get; set; }
        /// <summary>Index count currently selectable - lets the input clamp keyboard choice navigation.</summary>
        int ChoiceCount { get; set; }
        void Enable();
        void Disable();
    }
}
