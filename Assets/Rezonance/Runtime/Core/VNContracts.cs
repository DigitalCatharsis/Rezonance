// REZONANCE VN ENGINE - public contracts between engine, presentation, UI and services.
// Modules talk to each other ONLY through these types. Owner: contract layer.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rezonance.VN.Core
{
    /// <summary>Static log helper. Every module logs through it so dev builds can filter by area.</summary>
    public static class VNLog
    {
        /// <summary>Set true in development builds to get verbose traces.</summary>
        public static bool Dev = true;

        /// <summary>Verbose trace; dropped unless <see cref="Dev"/> is on.</summary>
        public static void Info(string area, string message)
        {
            if (Dev) Debug.Log("[VN:" + area + "] " + message);
        }

        public static void Warn(string area, string message) => Debug.LogWarning("[VN:" + area + "] " + message);

        public static void Error(string area, string message) => Debug.LogError("[VN:" + area + "] " + message);
    }

    /// <summary>Everything a scene node asks the presentation layer to do.</summary>
    public sealed class VNStageCues
    {
        public string backdropKey;
        public VNFadeKind fade = VNFadeKind.CrossFade;
        public float fadeSeconds = 0.6f;
        public PortraitRef[] portraits = Array.Empty<PortraitRef>();
        public bool hideAllPortraits;
        public AudioCue[] audio = Array.Empty<AudioCue>();
        public string screenText;
        /// <summary>Extra hold after typing finishes; 0 = use settings default.</summary>
        public float holdSeconds;
        /// <summary>Per-node typing speed override in characters per second; 0 = use settings.</summary>
        public float typingSpeed;
        public string miniGameId;

        public static readonly VNStageCues Empty = new VNStageCues();
    }

    /// <summary>Hand-authored staging layer keyed by node/branch/scene id. Overlay beats inline hints.</summary>
    public interface IStageOverlay
    {
        /// <summary>Returns cues bound to the node, or <see cref="VNStageCues.Empty"/>. Must never return null.</summary>
        VNStageCues ForNode(VNNode node);
        /// <summary>True when some cue exists for the node - used by the importer to report coverage.</summary>
        bool HasCues(VNNode node);
    }

    /// <summary>Pre-resolved payload for one frame of dialogue: what the UI must paint.</summary>
    public sealed class VNRenderContext
    {
        public VNNode node;
        /// <summary>Text already localized through ILocalizer.</summary>
        public string text;
        /// <summary>Speaker display name already localized; null when the node has no name plate.</summary>
        public string speakerName;
        public VNSpeakerRole role;
        public string delivery;
        public bool choicePending;
        public VNRunMode mode;
        public VNStageCues cues;
    }

    /// <summary>Backlog line stored for the history screen.</summary>
    public sealed class VNBacklogEntry
    {
        public string nodeId;
        public string speakerName;
        public string text;
        public VNNodeKind kind;
    }

    /// <summary>What the runtime asks the view (UI) to do. Implemented by the UI module.</summary>
    public interface IVNRuntimeView
    {
        void Render(VNRenderContext context);
        void ShowChoices(VNNode menuNode, IReadOnlyList<string> localizedLabels, IReadOnlyList<bool> enabledFlags);
        void HideChoices();
        void SetRunMode(VNRunMode mode);
        /// <summary>Called when typing animation completes (or is skipped) - runtime may use it for auto timing.</summary>
        void NotifyTypingFinished();
        void NotifyBusy(bool busy);
    }

    /// <summary>The dialogue machine. UI and bootstrap drive it; it drives view and directors.</summary>
    public interface IVNRuntime
    {
        VNRunMode Mode { get; }
        VNNode CurrentNode { get; }
        VNBranchDef CurrentBranch { get; }
        VNSceneDef CurrentScene { get; }
        bool ChoicePending { get; }
        bool IsTyping { get; }
        bool AtEndOfAct { get; }
        IReadOnlyList<VNBacklogEntry> Backlog { get; }

        event Action<VNRenderContext> NodeChanged;
        event Action<VNNode> MenuOpened;
        event Action MenuClosed;
        event Action<VNBranchDef> BranchChanged;
        event Action<VNSceneDef> SceneChanged;
        event Action Finished;
        event Action<string> MiniGameRequested;

        void StartAct(string sceneId = null);
        bool StartBranch(string branchId, string resumeBranchId = null);
        bool JumpToNode(string nodeId);
        /// <summary>Advance one node. Ignored while a choice is pending or text is still typing (unless skip requested).</summary>
        void Advance();
        void Choose(int optionIndex);
        void SetMode(VNRunMode mode);
        /// <summary>Finish typing instantly (input: tap / space during animation).</summary>
        void CompleteTyping();
        VNGameState CaptureState();
        bool RestoreState(VNGameState state);
    }

    /// <summary>Background image layer with crossfade. Implemented by presentation.</summary>
    public interface IBackdropDirector
    {
        string CurrentKey { get; }
        void Show(string backdropKey, VNFadeKind fade, float seconds);
        void Hide(float seconds);
    }

    /// <summary>Character portrait layer. Unknown characters resolve through the speaker map before this is called.</summary>
    public interface IPortraitDirector
    {
        void Show(PortraitRef portrait, float seconds);
        void Hide(PortraitSlot slot, float seconds);
        void HideAll(float seconds);
        /// <summary>Dims everyone who is not speaking. Value 1 = no dimming.</summary>
        void SetFocus(PortraitSlot speaking, float dimTo);
    }

    /// <summary>Four-channel audio with fades. Silent placeholder clips until real assets arrive.</summary>
    public interface IAudioDirector
    {
        void Play(VNAudioBus bus, string cueId, float fadeSeconds, bool loop);
        void Stop(VNAudioBus bus, float fadeSeconds);
        void SetBusVolume(VNAudioBus bus, float volume01);
        float GetBusVolume(VNAudioBus bus);
    }

    /// <summary>Diegetic on-screen text (the backtick lines in the script).</summary>
    public interface IScreenTextPresenter
    {
        void Show(string text, float seconds);
        void Hide();
    }

    /// <summary>Host for the "[Mini-igra]" markers. Prototype ships a stub; real games register later.</summary>
    public interface IVNMiniGameHost
    {
        bool Has(string miniGameId);
        void Launch(string miniGameId, Action<bool> onComplete);
    }

    /// <summary>Key based text tables. Keys equal node ids for script text and "ui.*" for interface.</summary>
    public interface ILocalizer
    {
        VNLanguage Language { get; }
        string Get(string key);
        bool TryGet(string key, out string text);
        /// <summary>Speaker label -> display name in the current language.</summary>
        string SpeakerDisplayName(string speakerLabel);
        void SetLanguage(VNLanguage language);
        event Action<VNLanguage> LanguageChanged;
        int MissingInCurrentLanguage { get; }
        int TotalKeys { get; }
    }

    /// <summary>Serializable flag row (bool/int/string kept as text to survive JsonUtility).</summary>
    [Serializable]
    public sealed class VNFlagEntry
    {
        public string name;
        public string value;
    }

    /// <summary>Serializable record of one taken decision.</summary>
    [Serializable]
    public sealed class VNChoiceEntry
    {
        public string menuId;
        public int optionIndex;
        public string optionId;
        public string targetBranchId;
    }

    /// <summary>Full restore point of a play session.</summary>
    [Serializable]
    public sealed class VNGameState
    {
        public int formatVersion = VNDataFormat.Version;
        public string actId;
        public string sceneId;
        public string branchId;
        public string nodeId;
        public int nodeIndexInBranch;
        /// <summary>Branch a forked path must return to; null when playing the mainline.</summary>
        public string resumeBranchId;
        public List<string> seenNodeIds = new List<string>();
        public List<VNFlagEntry> flags = new List<VNFlagEntry>();
        public List<VNChoiceEntry> choices = new List<VNChoiceEntry>();
        public List<string> backlogNodeIds = new List<string>();
        public VNLanguage language;
        public float playtimeSeconds;
        public long savedAtUtcTicks;
    }

    /// <summary>Row shown in the save/load screen.</summary>
    public sealed class SaveSlotInfo
    {
        public string slotId;
        public string displayName;
        public bool exists;
        public bool corrupted;
        public long savedAtUtcTicks;
        public float playtimeSeconds;
        public string sceneTitle;
        public string previewText;
        public string thumbnailPngPath;
    }

    /// <summary>Persistent save access. Implemented by services; JSON on Application.persistentDataPath.</summary>
    public interface ISaveService
    {
        void Initialize();
        IReadOnlyList<SaveSlotInfo> ListSlots();
        bool Save(string slotId, VNGameState state);
        bool TryLoad(string slotId, out VNGameState state);
        bool Exists(string slotId);
        bool Delete(string slotId);
        SaveSlotInfo Describe(string slotId);
        /// <summary>Autosave and quicksave slot ids, plus manual slots "slot_0".."slot_11".</summary>
        string AutosaveSlotId { get; }
        string QuicksaveSlotId { get; }
    }

    /// <summary>Player facing settings, persisted separately from game saves.</summary>
    [Serializable]
    public sealed class VNSettings
    {
        public VNLanguage language = VNLanguage.Ru;
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float musicVolume = 0.7f;
        [Range(0f, 1f)] public float ambienceVolume = 0.6f;
        [Range(0f, 1f)] public float sfxVolume = 0.8f;
        [Range(0f, 1f)] public float voiceVolume = 1f;
        /// <summary>Characters per second; 0 = instant text.</summary>
        public float textSpeedCps = 42f;
        public float autoNodeDelaySeconds = 1.2f;
        public float autoReadDelaySeconds = 0.35f;
        public int backlogMaxEntries = 400;
        public float portraitDimming = 0.65f;
        public bool fullscreen = true;
        /// <summary>Show localization keys instead of text - QA tool.</summary>
        public bool devShowKeys;
        public bool devVerboseLog;

        public VNSettings Clone() => (VNSettings)MemberwiseClone();
    }

    /// <summary>Settings persistence and broadcast. Implemented by services.</summary>
    public interface ISettingsStore
    {
        VNSettings Current { get; }
        void Apply(VNSettings settings);
        void Save();
        void Load();
        void ResetToDefaults();
        event Action<VNSettings> Changed;
    }
}
