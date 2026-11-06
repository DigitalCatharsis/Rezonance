// REZONANCE VN ENGINE - single source of UI design tokens (UI module).
// Every colour, size and spacing the interface uses lives here; other UI files must not hardcode
// palette constants. Direction: dark clinical bunker panel, SIGNALIS-flavoured cold accent.
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Design tokens for the whole VN interface.</summary>
    public static class UiStyle
    {
        // ---------------------------------------------------------------- palette

        /// <summary>Deep background behind everything (#0B0E11).</summary>
        public static Color Background => new Color32(0x0B, 0x0E, 0x11, 0xFF);

        /// <summary>Base panel colour (#141A20).</summary>
        public static Color Panel => new Color32(0x14, 0x1A, 0x20, 0xFF);

        /// <summary>Raised panel / card colour.</summary>
        public static Color PanelRaised => new Color32(0x1B, 0x23, 0x2B, 0xFF);

        /// <summary>Panel used for focused or hovered rows.</summary>
        public static Color PanelActive => new Color32(0x24, 0x2F, 0x39, 0xFF);

        /// <summary>Hairline and separator colour.</summary>
        public static Color Border => new Color32(0x2E, 0x3A, 0x45, 0xFF);

        /// <summary>Main body text (#E6E1D6, warm paper against cold panels).</summary>
        public static Color Text => new Color32(0xE6, 0xE1, 0xD6, 0xFF);

        /// <summary>Secondary text: hints, timestamps.</summary>
        public static Color TextDim => new Color32(0x9A, 0xA0, 0xA6, 0xFF);

        /// <summary>Text on a filled accent surface.</summary>
        public static Color TextOnAccent => new Color32(0x0B, 0x0E, 0x11, 0xFF);

        /// <summary>Cold signal accent (selection, focus frame).</summary>
        public static Color Accent => new Color32(0x4F, 0xB3, 0xAE, 0xFF);

        /// <summary>Warm signal accent: speaker plate, active mode chips.</summary>
        public static Color AccentWarm => new Color32(0xD8, 0xA6, 0x57, 0xFF);

        /// <summary>Destructive action colour.</summary>
        public static Color Danger => new Color32(0xC4, 0x54, 0x4A, 0xFF);

        /// <summary>Positive confirmation colour.</summary>
        public static Color Success => new Color32(0x7F, 0xB0, 0x69, 0xFF);

        /// <summary>Translucent dim behind the dialogue box so text stays readable over any backdrop.</summary>
        public static Color DialogueBackdrop => new Color32(0x0B, 0x0E, 0x11, 0xE6);

        /// <summary>Full screen dim used by modal screens.</summary>
        public static Color ModalDim => new Color32(0x05, 0x07, 0x0A, 0xD9);

        /// <summary>Colour of a disabled control.</summary>
        public static Color Disabled => new Color32(0x45, 0x4C, 0x53, 0xFF);

        /// <summary>Toast surface.</summary>
        public static Color Toast => new Color32(0x18, 0x21, 0x28, 0xF2);

        // ---------------------------------------------------------------- sizing

        /// <summary>Reference resolution width of the CanvasScaler.</summary>
        public const float ReferenceWidth = 1920f;

        /// <summary>Reference resolution height of the CanvasScaler.</summary>
        public const float ReferenceHeight = 1080f;

        /// <summary>Smallest allowed touch target in reference pixels (Android requirement).</summary>
        public const float MinTouchSize = 44f;

        /// <summary>Height of a standard button row.</summary>
        public const float ButtonHeight = 64f;

        /// <summary>Width of a standard button in menus.</summary>
        public const float ButtonWidth = 560f;

        /// <summary>HUD button square.</summary>
        public const float HudButtonSize = 56f;

        /// <summary>Gap between layout items.</summary>
        public const float ItemSpacing = 12f;

        /// <summary>Padding inside a panel.</summary>
        public const float PanelPadding = 24f;

        /// <summary>Dialogue body size; clamped to 36..44 by DialogueScreen.</summary>
        public const float DialogueFontSize = 38f;

        /// <summary>Smallest allowed dialogue size.</summary>
        public const float DialogueFontSizeMin = 36f;

        /// <summary>Largest allowed dialogue size.</summary>
        public const float DialogueFontSizeMax = 44f;

        /// <summary>Speaker plate font.</summary>
        public const float SpeakerFontSize = 30f;

        /// <summary>Menu and modal body font.</summary>
        public const float LabelFontSize = 26f;

        /// <summary>Small captions: timestamps, hints.</summary>
        public const float CaptionFontSize = 22f;

        /// <summary>Screen title font.</summary>
        public const float TitleFontSize = 64f;

        /// <summary>Section heading inside a modal.</summary>
        public const float HeadingFontSize = 40f;

        /// <summary>Corner radius of the procedural nine-slice.</summary>
        public const float PanelCornerRadius = 8f;

        // ---------------------------------------------------------------- naming

        /// <summary>Name of the root canvas object (fixed by ARCHITECTURE.md 9.6).</summary>
        public const string CanvasName = "VNCanvas";

        /// <summary>Layer names bottom to top (ARCHITECTURE.md 9.6).</summary>
        public static readonly string[] LayerNames =
        {
            "BackdropLayer", "PortraitLayer", "ScreenTextLayer", "DialogueLayer",
            "ChoiceLayer", "ModalLayer", "HudLayer", "DebugLayer"
        };

        // ---------------------------------------------------------------- helpers

        /// <summary>Same colour with a replaced alpha byte.</summary>
        public static Color WithAlpha(Color c, byte a)
        {
            c.a = a / 255f;
            return c;
        }

        /// <summary>UIMenu controller colours derived from a base tint.</summary>
        public static ColorBlock ButtonColors(Color tint)
        {
            var block = ColorBlock.defaultColorBlock;
            block.normalColor = tint;
            block.highlightedColor = Color.Lerp(tint, Color.white, 0.18f);
            block.pressedColor = Color.Lerp(tint, Color.black, 0.22f);
            block.selectedColor = block.highlightedColor;
            block.disabledColor = new Color(tint.r, tint.g, tint.b, 0.35f);
            block.fadeDuration = 0.08f;
            return block;
        }

        /// <summary>Accent colour of a speaker plate, keyed by the role the engine resolved.</summary>
        public static Color AccentForRole(VNSpeakerRole role)
        {
            switch (role)
            {
                case VNSpeakerRole.Player: return Accent;
                case VNSpeakerRole.InnerVoice: return WithAlpha(Accent, 200);
                case VNSpeakerRole.System: return Success;
                case VNSpeakerRole.Character: return AccentWarm;
                default: return TextDim;
            }
        }
    }
}
