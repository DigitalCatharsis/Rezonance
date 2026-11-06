// REZONANCE VN ENGINE - turns a delivery brief into a portrait emotion id.
// The engine hands the raw markdown brief ("shtopotom", "neglyako", "krikom") to presentation through
// the directors; this helper keeps that vocabulary in one place. Unknown text falls back to neutral.
using System;
using System.Collections.Generic;
using Rezonance.VN.Core;

namespace Rezonance.VN.Presentation
{
    /// <summary>Maps delivery hints and overlay hints onto emotion ids used in por_&lt;character&gt;_&lt;emotion&gt; keys.</summary>
    public sealed class PortraitEmotionResolver
    {
        /// <summary>Emotion used when nothing else matched.</summary>
        public const string DefaultEmotion = "neutral";

        private const string LogArea = "emotion";

        private static readonly string[] EmotionNames =
        {
            "neutral", "soft", "angry", "sad", "smile", "happy", "surprised", "serious",
            "thinking", "mocking", "worried", "tense", "cold", "calm", "tired", "blank"
        };

        // Keys are already ascii slugs; VNSlug transliterates the Cyrillic brief before lookup.
        private static readonly Dictionary<string, string> TokenMap = BuildTokenMap();

        private static readonly List<string> OrderedTokens = SortedKeys(TokenMap);

        private readonly Dictionary<string, string> custom = new Dictionary<string, string>(StringComparer.Ordinal);
        private VNSpeakerMap speakerMap;

        /// <summary>Creates the resolver. The speaker map may be null and assigned later.</summary>
        public PortraitEmotionResolver(VNSpeakerMap map = null)
        {
            speakerMap = map;
        }

        /// <summary>Speaker map consulted for per-character default emotions.</summary>
        public VNSpeakerMap SpeakerMap
        {
            get => speakerMap;
            set => speakerMap = value;
        }

        /// <summary>Emotion ids this resolver recognises.</summary>
        public static IEnumerable<string> KnownEmotions => EmotionNames;

        /// <summary>Adds or overrides a delivery token, e.g. "skvoz Zuby" to "angry".</summary>
        public void RegisterToken(string deliveryToken, string emotionId)
        {
            if (string.IsNullOrEmpty(deliveryToken) || string.IsNullOrEmpty(emotionId)) return;
            custom[VNSlug.Of(deliveryToken)] = NormalizeEmotion(emotionId);
        }

        /// <summary>True when the text is one of the known emotion ids.</summary>
        public static bool IsKnownEmotion(string emotion)
        {
            if (string.IsNullOrEmpty(emotion)) return false;
            var slug = VNSlug.Of(emotion);
            for (var i = 0; i < EmotionNames.Length; i++)
                if (string.Equals(EmotionNames[i], slug, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Lowercase ascii emotion id; unknown values return themselves so an authored set still works.</summary>
        public static string NormalizeEmotion(string emotion)
        {
            if (string.IsNullOrEmpty(emotion)) return DefaultEmotion;
            return VNSlug.Of(emotion);
        }

        /// <summary>Emotion implied by a delivery brief, or null when the brief says nothing.</summary>
        public string FromDelivery(string delivery)
        {
            if (string.IsNullOrEmpty(delivery)) return null;

            var slug = VNSlug.Of(delivery);
            if (string.IsNullOrEmpty(slug) || slug == "untitled") return null;

            var local = custom.Count > 0;
            if (local && custom.TryGetValue(slug, out var customHit)) return customHit;
            if (TokenMap.TryGetValue(slug, out var exact)) return exact;

            foreach (var token in OrderedTokens)
            {
                if (token.Length < 4) continue;
                if (slug.IndexOf(token, StringComparison.Ordinal) < 0) continue;
                return TokenMap[token];
            }

            if (IsKnownEmotion(slug)) return VNSlug.Of(slug);
            return null;
        }

        /// <summary>
        /// Resolution order: explicit overlay emotion, delivery brief, speaker map default, neutral.
        /// </summary>
        public string Resolve(string overlayEmotion, string delivery, string speakerLabel)
        {
            if (!string.IsNullOrEmpty(overlayEmotion)) return NormalizeEmotion(overlayEmotion);

            var fromDelivery = FromDelivery(delivery);
            if (!string.IsNullOrEmpty(fromDelivery)) return fromDelivery;

            var fromMap = DefaultEmotionFor(speakerLabel);
            if (!string.IsNullOrEmpty(fromMap)) return NormalizeEmotion(fromMap);

            return DefaultEmotion;
        }

        /// <summary>Convenience overload for a node: reads speaker and delivery straight from the data.</summary>
        public string ResolveForNode(VNNode node, string overlayEmotion = null)
        {
            if (node == null) return DefaultEmotion;
            return Resolve(overlayEmotion, node.delivery, node.speaker);
        }

        /// <summary>
        /// Builds a portrait reference for a speaker label. Returns null when the speaker map has no
        /// portrait for that label (ambient voices), so the caller can skip the slot entirely.
        /// </summary>
        public PortraitRef? ResolvePortrait(string speakerLabel, string delivery, string overlayEmotion = null,
                                            PortraitSlot? forcedSlot = null)
        {
            if (string.IsNullOrEmpty(speakerLabel)) return null;

            var emotion = Resolve(overlayEmotion, delivery, speakerLabel);

            if (speakerMap != null && speakerMap.TryGet(speakerLabel, out var row) && row != null)
            {
                if (string.IsNullOrEmpty(row.characterId)) return null;
                var slot = forcedSlot ?? row.defaultSlot;
                if (slot == PortraitSlot.None) return null;
                return new PortraitRef(row.characterId, emotion, slot);
            }

            if (forcedSlot == null || forcedSlot == PortraitSlot.None) return null;
            return new PortraitRef(VNSlug.Of(speakerLabel), emotion, forcedSlot.Value);
        }

        /// <summary>Default emotion authored for a speaker label.</summary>
        public string DefaultEmotionFor(string speakerLabel)
        {
            if (speakerMap == null || string.IsNullOrEmpty(speakerLabel)) return null;
            if (!speakerMap.TryGet(speakerLabel, out var row) || row == null) return null;
            return string.IsNullOrEmpty(row.defaultEmotion) ? null : row.defaultEmotion;
        }

        private static List<string> SortedKeys(Dictionary<string, string> map)
        {
            // Longest token first so "neglyako" wins over a short substring that appears inside it.
            var keys = new List<string>(map.Keys);
            keys.Sort((a, b) =>
            {
                var byLength = b.Length.CompareTo(a.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
            });
            return keys;
        }

        private static Dictionary<string, string> BuildTokenMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            Add(map, "soft", "shtopotom", "shopotom", "polushopotom", "polugolosom", "negromko",
                    "whisper", "whispering", "quietly", "softly", "underhisbreath", "inhalfearnest");
            Add(map, "angry", "krikom", "krichit", "krik", "rezko", "rykom", "grubo", "vysokimtomom",
                    "nesteryadno", "skvozzuby", "shout", "shouting", "shouts", "scream", "screaming",
                    "angrily", "harshly", "barked", "snarled", "furiously");
            Add(map, "sad", "plachet", "solezami", "pechalno", "tikhopechalno", "upalo", "vnachale",
                    "crying", "weeping", "sadly", "mournfully", "tearfully");
            Add(map, "smile", "uLybaetsya", "sulybkoy", "uLybka", "dobro", "myagko", "smiling",
                    "withasmile", "smiles", "warmly", "fondly", "gently");
            Add(map, "happy", "veselo", "radostno", "livko", "happy", "joyfully", "cheerfully", "delighted");
            Add(map, "surprised", "udivlyonno", "izumlyonno", "astronomicheski", "smtuleniem",
                    "surprised", "astonished", "startled", "inconvenience", "confused", "puzzled");
            Add(map, "serious", "serlezno", "seriozno", "strogo", "suho", "ofitsialno", "solemnly",
                    "gravely", "strictly", "sternly", "businesslike");
            Add(map, "thinking", "zadumchivo", "zadumalsya", "razmyshlyaem", "podoumluya",
                    "thoughtfully", "pondering", "musing", "considering", "reflective");
            Add(map, "mocking", "nasmeshlivo", "ilyonno", "irenichno", "sarkasticheski", "kogto", "mocking",
                    "sarcastic", "sarcastically", "teasing", "ironically", "smugly");
            Add(map, "worried", "vstrevozhno", "bespokoyno", "trevozhno", "nedoumenno", "anxious",
                    "worried", "uneasily", "apprehensive", "nervously", "troubled");
            Add(map, "tense", "napryazhenno", "rezko", "tensely", "taut", "strained", "tight");
            Add(map, "cold", "kholodno", "otstranenno", "bezzhalno", "coldly", "detached", "flatly",
                    "impersonal", "icily");
            Add(map, "calm", "spokoyno", "roavno", "medlenno", "calmly", "evenly", "composed", "placidly", "level");
            Add(map, "tired", "ustalo", "tomno", "iznurenno", "wearily", "tiredly", "exhausted", "weary");
            Add(map, "blank", "neglyako", "bezrazlichno", "avtomaticheski", "nonchalant", "blankly",
                    "indifferently", "flat", "neutral", "matteroffactly");
            return map;
        }

        private static void Add(Dictionary<string, string> map, string emotion, params string[] tokens)
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                var slug = VNSlug.Of(tokens[i]);
                if (string.IsNullOrEmpty(slug)) continue;
                map[slug] = emotion;
            }
        }
    }
}
