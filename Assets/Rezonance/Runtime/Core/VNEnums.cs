// REZONANCE VN ENGINE - core enums and value types.
// Owner: contract layer. Do not add module-specific enums here without updating ARCHITECTURE.md.
using System;

namespace Rezonance.VN.Core
{
    /// <summary>Kind of a single script node. Flat model: one class carries payload for every kind.</summary>
    public enum VNNodeKind
    {
        Unknown = 0,
        /// <summary>"## Title" - scene header.</summary>
        SceneTitle = 1,
        /// <summary>"### Title" - branch header.</summary>
        BranchTitle = 2,
        /// <summary>NPC or player line: NAME: "text".</summary>
        Dialogue = 3,
        /// <summary>Player inner voice: "Vy (vnutrenniy golos): ...".</summary>
        InnerVoice = 4,
        /// <summary>Stage remark in square brackets.</summary>
        Remark = 5,
        /// <summary>On-screen diegetic text (backticks in source).</summary>
        ScreenText = 6,
        /// <summary>Choice menu block.</summary>
        Menu = 7,
        /// <summary>Conditional insertion "*(Yesli ...:)*".</summary>
        Conditional = 8,
        /// <summary>"[Mini-igra]" marker.</summary>
        MiniGame = 9,
        /// <summary>Glossary definition from the document tail.</summary>
        GlossaryEntry = 10,
        /// <summary>Plain narration paragraph without speaker.</summary>
        Narration = 11,
        /// <summary>Empty paragraph used for spacing / pacing.</summary>
        Blank = 12
    }

    /// <summary>Who is talking - drives name plate styling and portrait routing.</summary>
    public enum VNSpeakerRole
    {
        Unknown = 0,
        /// <summary>Named character with a portrait set (IRENA, GLORIA, AGATA...).</summary>
        Character = 1,
        /// <summary>The player ("Vy").</summary>
        Player = 2,
        /// <summary>Player inner voice.</summary>
        InnerVoice = 3,
        /// <summary>Anonymous ambient voice: "Golos za steklom", "Devushka"...</summary>
        Ambient = 4,
        /// <summary>Machine voice: "EKRAN", "TERMINAL".</summary>
        System = 5
    }

    /// <summary>Horizontal slots available for character portraits.</summary>
    public enum PortraitSlot
    {
        None = 0,
        FarLeft = 1,
        Left = 2,
        Center = 3,
        Right = 4,
        FarRight = 5
    }

    /// <summary>Playback mode of the runtime.</summary>
    public enum VNRunMode
    {
        /// <summary>Wait for player input on every node.</summary>
        Manual = 0,
        /// <summary>Advance automatically after typing speed + reading delay.</summary>
        Auto = 1,
        /// <summary>Instant text, still waits for input.</summary>
        Fast = 2,
        /// <summary>Skip everything already seen, stop on new content.</summary>
        SkipUnseen = 3
    }

    /// <summary>Supported UI/text languages. Extend by adding a CSV table + a row here.</summary>
    public enum VNLanguage
    {
        Ru = 0,
        En = 1
    }

    /// <summary>Audio buses exposed in settings.</summary>
    public enum VNAudioBus
    {
        Master = 0,
        Music = 1,
        Ambience = 2,
        Sfx = 3,
        Voice = 4
    }

    /// <summary>Transition style used by directors.</summary>
    public enum VNFadeKind
    {
        Cut = 0,
        Fade = 1,
        CrossFade = 2
    }

    /// <summary>Reference to one portrait: which character, which emotion, which slot.</summary>
    [Serializable]
    public struct PortraitRef
    {
        public string characterId;
        public string emotionId;
        public PortraitSlot slot;

        public PortraitRef(string character, string emotion, PortraitSlot slot)
        {
            this.characterId = character;
            this.emotionId = emotion;
            this.slot = slot;
        }

        public bool IsValid => !string.IsNullOrEmpty(characterId) && slot != PortraitSlot.None;

        public string AssetKey =>
            string.IsNullOrEmpty(emotionId)
                ? "por_" + characterId
                : "por_" + characterId + "_" + emotionId;
    }

    /// <summary>One audio instruction emitted by a stage cue.</summary>
    [Serializable]
    public struct AudioCue
    {
        public VNAudioBus bus;
        public string cueId;
        public float fadeSeconds;
        public bool stop;

        public AudioCue(VNAudioBus bus, string cueId, float fadeSeconds, bool stop = false)
        {
            this.bus = bus;
            this.cueId = cueId;
            this.fadeSeconds = fadeSeconds;
            this.stop = stop;
        }
    }

    /// <summary>Stable identity parts of a node, parsed from its id string.</summary>
    public readonly struct VNNodeId
    {
        public readonly string Full;
        public readonly string Act;
        public readonly string Scene;
        public readonly string Branch;
        public readonly string Local;

        public VNNodeId(string full)
        {
            Full = full ?? string.Empty;
            var parts = Full.Split('.');
            Act = parts.Length > 0 ? parts[0] : string.Empty;
            Scene = parts.Length > 1 ? parts[1] : string.Empty;
            Branch = parts.Length > 2 ? parts[2] : string.Empty;
            Local = parts.Length > 3 ? parts[3] : string.Empty;
        }

        public string BranchId => Act + "." + Scene + "." + Branch;
        public string SceneId => Act + "." + Scene;
        public override string ToString() => Full;
    }
}
