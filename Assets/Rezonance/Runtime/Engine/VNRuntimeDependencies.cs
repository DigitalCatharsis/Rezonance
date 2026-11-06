// REZONANCE VN ENGINE - the single injection point for the runtime.
// The composition root (GameBootstrap) fills these fields and passes the object to VNRuntime, so the
// engine never resolves services itself and every collaborator stays optional (null = feature off).
using Rezonance.VN.Core;

namespace Rezonance.VN.Engine
{
    /// <summary>Collaborators of <see cref="VNRuntime"/>. Assign the fields you have; the rest degrade with a warning.</summary>
    public sealed class VNRuntimeDependencies
    {
        /// <summary>Dialogue screen the runtime paints into. Null = no view, the runtime still advances state.</summary>
        public IVNRuntimeView View;

        /// <summary>Text tables; keys equal node ids for script text.</summary>
        public ILocalizer Localizer;

        /// <summary>Hand-authored staging cues keyed by node/branch/scene id.</summary>
        public IStageOverlay Overlay;

        /// <summary>Background layer.</summary>
        public IBackdropDirector Backdrop;

        /// <summary>Portrait layer.</summary>
        public IPortraitDirector Portraits;

        /// <summary>Audio buses.</summary>
        public IAudioDirector Audio;

        /// <summary>Diegetic on-screen text layer.</summary>
        public IScreenTextPresenter ScreenText;

        /// <summary>Host for "[Mini-igra]" markers; unknown ids are skipped with a warning.</summary>
        public IVNMiniGameHost MiniGames;

        /// <summary>Player settings source (speeds, backlog size, volumes).</summary>
        public ISettingsStore Settings;

        /// <summary>Save access used for autosave, quicksave and quickload.</summary>
        public ISaveService Save;

        /// <summary>Input source; the runtime keeps ChoiceCount and CaptureUiOnly in sync and handles commands itself.</summary>
        public IVNInput Input;

        /// <summary>Indexed script data; required for anything to happen.</summary>
        public VNDataRegistry Registry;

        /// <summary>Playtime accumulator; the runtime creates and owns one when null.</summary>
        public VNRuntimeClock Clock;

        /// <summary>Log area tag; "Engine" by default.</summary>
        public string LogArea;

        /// <summary>Dependency set with every field unset.</summary>
        public static VNRuntimeDependencies CreateEmpty() => new VNRuntimeDependencies();

        /// <summary>Lists the fields that are null. Optional services only warn; Registry is required.</summary>
        public string Missing()
        {
            var s = new System.Text.StringBuilder(96);
            Append(s, "View", View);
            Append(s, "Localizer", Localizer);
            Append(s, "Overlay", Overlay);
            Append(s, "Backdrop", Backdrop);
            Append(s, "Portraits", Portraits);
            Append(s, "Audio", Audio);
            Append(s, "ScreenText", ScreenText);
            Append(s, "MiniGames", MiniGames);
            Append(s, "Settings", Settings);
            Append(s, "Save", Save);
            Append(s, "Input", Input);
            if (Registry == null) s.Append("Registry, ");
            return s.ToString();
        }

        private static void Append(System.Text.StringBuilder s, string name, object value)
        {
            if (value != null) return;
            s.Append(name).Append(", ");
        }
    }
}
