// REZONANCE VN ENGINE - key based asset access over Resources with deterministic placeholders.
// Paths follow ARCHITECTURE.md 4.1 and are given WITHOUT the "Resources/" prefix.
// A missing asset never returns null and never throws: callers get a stable placeholder plus one warning.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Rezonance.VN.Core;
using UnityEngine;

namespace Rezonance.VN.Presentation
{
    /// <summary>Caches and serves art and audio by asset key; generates placeholders for whatever is missing.</summary>
    public sealed class AssetLibrary
    {
        /// <summary>Background art folder inside Resources.</summary>
        public const string BackgroundFolder = "Rezonance/art/bg";

        /// <summary>Portrait art folder inside Resources.</summary>
        public const string PortraitFolder = "Rezonance/art/por";

        /// <summary>Generic interface art folder inside Resources.</summary>
        public const string UiFolder = "Rezonance/art/ui";

        /// <summary>Audio root inside Resources; one subfolder per bus.</summary>
        public const string AudioRoot = "Rezonance/audio";

        private const string LogArea = "assets";
        private const int SilentClipFrequency = 44100;

        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private readonly HashSet<string> placeholderTextures = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> placeholderSprites = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> placeholderClips = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> imageHints = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<VNAudioBus, HashSet<string>> audioHints = new Dictionary<VNAudioBus, HashSet<string>>();
        private readonly Dictionary<string, Texture2D> placeholderCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        /// <summary>Instance used by every director unless it is given another one.</summary>
        public static AssetLibrary Shared { get; } = new AssetLibrary();

        /// <summary>0..1 progress of the last PreloadAllAsync run.</summary>
        public float PreloadProgress { get; private set; }

        /// <summary>True while a preload sweep runs - a nested call returns immediately.</summary>
        public bool IsPreloading { get; private set; }

        /// <summary>Keys that resolved to a real asset since the last Clear.</summary>
        public int RealAssetCount { get; private set; }

        /// <summary>Keys that fell back to a placeholder since the last Clear.</summary>
        public int PlaceholderCount { get; private set; }

        // ------------------------------------------------------------------ paths

        /// <summary>Folder a keyed image lives in: por_* to art/por, bg_* to art/bg, everything else to art/ui.</summary>
        public static string FolderFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return UiFolder;
            if (key.StartsWith("por_", StringComparison.Ordinal)) return PortraitFolder;
            if (key.StartsWith("bg_", StringComparison.Ordinal)) return BackgroundFolder;
            return UiFolder;
        }

        /// <summary>Full Resources path (no "Resources/" prefix) of a keyed image.</summary>
        public static string PathFor(string key)
        {
            return FolderFor(key) + "/" + key;
        }

        /// <summary>Resources path of one cue: Rezonance/audio/{bus}/{cue}.</summary>
        public static string PathFor(VNAudioBus bus, string cue)
        {
            return AudioRoot + "/" + BusFolder(bus) + "/" + cue;
        }

        /// <summary>Subfolder used for a bus. Master has no own cues and shares the music folder.</summary>
        public static string BusFolder(VNAudioBus bus)
        {
            switch (bus)
            {
                case VNAudioBus.Music: return "music";
                case VNAudioBus.Ambience: return "ambience";
                case VNAudioBus.Sfx: return "sfx";
                case VNAudioBus.Voice: return "voice";
                default: return "music";
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>True when an image asset with this key exists. A positive answer caches the asset.</summary>
        public bool Has(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            imageHints.Add(key);
            if (textures.TryGetValue(key, out var tex) && !placeholderTextures.Contains(key)) return true;
            if (sprites.TryGetValue(key, out var spr) && !placeholderSprites.Contains(key)) return true;
            if (Resources.Load<UnityEngine.Object>(PathFor(key)) == null) return false;
            GetTexture(key);
            GetSprite(key);
            return true;
        }

        /// <summary>True when an audio clip exists for the cue on the bus.</summary>
        public bool HasAudio(VNAudioBus bus, string cue)
        {
            if (string.IsNullOrEmpty(cue)) return false;
            RememberAudioHint(bus, cue);
            if (clips.TryGetValue(ClipKey(bus, cue), out var clip) && !placeholderClips.Contains(ClipKey(bus, cue)))
                return true;
            return Resources.Load<AudioClip>(PathFor(bus, cue)) != null;
        }

        /// <summary>True when the last lookup of this key produced a generated texture placeholder.</summary>
        public bool IsPlaceholderTexture(string key)
        {
            return !string.IsNullOrEmpty(key) && placeholderTextures.Contains(key);
        }

        /// <summary>True when the last lookup of this key produced a generated sprite placeholder.</summary>
        public bool IsPlaceholderSprite(string key)
        {
            return !string.IsNullOrEmpty(key) && placeholderSprites.Contains(key);
        }

        /// <summary>True when the cue is served by a generated silent clip.</summary>
        public bool IsPlaceholderAudio(VNAudioBus bus, string cue)
        {
            return !string.IsNullOrEmpty(cue) && placeholderClips.Contains(ClipKey(bus, cue));
        }

        // ------------------------------------------------------------------ images

        /// <summary>Texture for a background key, never null; a missing file yields a stable colored placeholder.</summary>
        public Texture2D GetTexture(string key)
        {
            if (string.IsNullOrEmpty(key)) return PlaceholderTexture("<empty>");
            imageHints.Add(key);
            if (textures.TryGetValue(key, out var cached)) return cached;

            var found = Resources.Load<Texture2D>(PathFor(key));
            if (found != null)
            {
                textures[key] = found;
                RealAssetCount++;
                return found;
            }

            var ph = PlaceholderTexture(key);
            textures[key] = ph;
            placeholderTextures.Add(key);
            PlaceholderCount++;
            WarnMissing("img:" + key, "texture missing at Resources/" + PathFor(key));
            return ph;
        }

        /// <summary>Sprite for a portrait or ui key, never null. Missing files yield a placeholder plate.</summary>
        public Sprite GetSprite(string key)
        {
            if (string.IsNullOrEmpty(key)) return SpriteFromTexture(PlaceholderTexture("<empty>"), key);
            imageHints.Add(key);
            if (sprites.TryGetValue(key, out var cached)) return cached;

            var found = Resources.Load<Sprite>(PathFor(key));
            if (found != null)
            {
                sprites[key] = found;
                RealAssetCount++;
                return found;
            }

            // A PNG imported with the Default texture type has no Sprite sub-asset: wrap the raw texture instead.
            var raw = Resources.Load<Texture2D>(PathFor(key));
            if (raw != null)
            {
                var wrapped = Sprite.Create(raw, new Rect(0f, 0f, raw.width, raw.height),
                                            new Vector2(0.5f, 0.5f), 100f);
                wrapped.name = key;
                sprites[key] = wrapped;
                textures[key] = raw;
                RealAssetCount++;
                return wrapped;
            }

            var sprite = SpriteFromTexture(PlaceholderTexture(key), key);
            sprites[key] = sprite;
            placeholderSprites.Add(key);
            PlaceholderCount++;
            WarnMissing("img:" + key, "sprite missing at Resources/" + PathFor(key));
            return sprite;
        }

        // ------------------------------------------------------------------ audio

        /// <summary>Cue for a bus, never null. A missing file gives a one sample silent clip so playback stays legal.</summary>
        public AudioClip GetAudioClip(VNAudioBus bus, string cue)
        {
            var cacheKey = ClipKey(bus, cue);
            if (string.IsNullOrEmpty(cue)) return SilentClip(cacheKey);
            RememberAudioHint(bus, cue);
            if (clips.TryGetValue(cacheKey, out var cached)) return cached;

            var found = Resources.Load<AudioClip>(PathFor(bus, cue));
            if (found != null)
            {
                clips[cacheKey] = found;
                RealAssetCount++;
                return found;
            }

            var silent = SilentClip(cacheKey);
            clips[cacheKey] = silent;
            placeholderClips.Add(cacheKey);
            PlaceholderCount++;
            WarnMissing("audio:" + cacheKey, "clip missing at Resources/" + PathFor(bus, cue));
            return silent;
        }

        // ------------------------------------------------------------------ hints

        /// <summary>Registers an image key so PreloadAllAsync warms it up front.</summary>
        public void HintKey(string key)
        {
            if (!string.IsNullOrEmpty(key)) imageHints.Add(key);
        }

        /// <summary>Registers a cue so PreloadAllAsync warms it up front.</summary>
        public void HintAudio(VNAudioBus bus, string cue) => RememberAudioHint(bus, cue);

        /// <summary>Collects backdrop, portrait and audio keys from the staging overlay rows.</summary>
        public void CollectHintsFrom(VNStageOverlay overlay)
        {
            if (overlay == null || overlay.Rows == null) return;
            foreach (var row in overlay.Rows)
            {
                if (row == null) continue;
                if (!string.IsNullOrEmpty(row.backdropKey)) imageHints.Add(row.backdropKey);
                if (row.portraits != null)
                {
                    foreach (var p in row.portraits)
                        if (p.IsValid) imageHints.Add(p.AssetKey);
                }
                if (row.audio != null)
                {
                    foreach (var a in row.audio)
                        if (a != null && !string.IsNullOrEmpty(a.cueId)) RememberAudioHint(a.bus, a.cueId);
                }
            }
        }

        /// <summary>Collects por_{character}_{emotion} keys from the speaker map defaults.</summary>
        public void CollectHintsFrom(VNSpeakerMap map)
        {
            if (map == null || map.Rows == null) return;
            foreach (var row in map.Rows)
            {
                if (row == null || string.IsNullOrEmpty(row.characterId)) continue;
                imageHints.Add(new PortraitRef(row.characterId, row.defaultEmotion, row.defaultSlot).AssetKey);
                imageHints.Add("por_" + row.characterId);
            }
        }

        /// <summary>Every image key the library knows about, sorted so a preload order is reproducible.</summary>
        public List<string> KnownKeys()
        {
            var set = new HashSet<string>(imageHints, StringComparer.Ordinal);
            set.UnionWith(textures.Keys);
            set.UnionWith(sprites.Keys);
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // ------------------------------------------------------------------ preload

        /// <summary>
        /// Warms every known image and cue across frames and reports 0..1 progress.
        /// Idempotent: a call while a sweep runs returns immediately, cancellation is swallowed.
        /// </summary>
        public UniTask PreloadAllAsync(IProgress<float> progress = null, CancellationToken token = default)
        {
            return PreloadAllImpl(progress, token);
        }

        /// <summary>Overload for callers that only hold a cancellation token.</summary>
        public UniTask PreloadAllAsync(CancellationToken token) => PreloadAllImpl(null, token);

        private async UniTask PreloadAllImpl(IProgress<float> progress, CancellationToken token)
        {
            if (IsPreloading) return;

            var keys = KnownKeys();
            var cues = new List<KeyValuePair<VNAudioBus, string>>();
            foreach (var entry in audioHints)
            {
                foreach (var cue in entry.Value)
                {
                    if (!string.IsNullOrEmpty(cue) && cue != "<empty>")
                        cues.Add(new KeyValuePair<VNAudioBus, string>(entry.Key, cue));
                }
            }
            cues.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));

            var total = keys.Count + cues.Count;
            if (total == 0)
            {
                PreloadProgress = 1f;
                progress?.Report(1f);
                return;
            }

            IsPreloading = true;
            var done = 0;
            try
            {
                foreach (var key in keys)
                {
                    var folder = FolderFor(key);
                    if (folder == PortraitFolder || folder == UiFolder) GetSprite(key);
                    else GetTexture(key);
                    Report(progress, ++done, total);
                    await UniTask.Yield(PlayerLoopTiming.Update, token).SuppressCancellationThrow();
                }

                foreach (var pair in cues)
                {
                    GetAudioClip(pair.Key, pair.Value);
                    Report(progress, ++done, total);
                    await UniTask.Yield(PlayerLoopTiming.Update, token).SuppressCancellationThrow();
                }
            }
            finally
            {
                IsPreloading = false;
            }
        }

        private void Report(IProgress<float> progress, int done, int total)
        {
            PreloadProgress = total <= 0 ? 1f : Mathf.Clamp01((float)done / total);
            progress?.Report(PreloadProgress);
        }

        // ------------------------------------------------------------------ cache control

        /// <summary>Drops cached references for one key so the next lookup reloads it.</summary>
        public void Forget(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            textures.Remove(key);
            sprites.Remove(key);
            placeholderTextures.Remove(key);
            placeholderSprites.Remove(key);
            warned.Remove("img:" + key);
        }

        /// <summary>Drops every cached reference. Resources assets are never destroyed; generated placeholders stay reusable.</summary>
        public void Clear()
        {
            textures.Clear();
            sprites.Clear();
            clips.Clear();
            placeholderTextures.Clear();
            placeholderSprites.Clear();
            placeholderClips.Clear();
            warned.Clear();
            RealAssetCount = 0;
            PlaceholderCount = 0;
            PreloadProgress = 0f;
        }

        // ------------------------------------------------------------------ placeholders

        private Texture2D PlaceholderTexture(string key)
        {
            if (placeholderCache.TryGetValue(key, out var existing) && existing != null) return existing;
            var folder = FolderFor(key);
            var width = folder == PortraitFolder ? 448 : folder == BackgroundFolder ? 512 : 256;
            var height = folder == PortraitFolder ? 768 : folder == BackgroundFolder ? 288 : 256;
            var tex = MakePlaceholder(key, width, height);
            placeholderCache[key] = tex;
            return tex;
        }

        private static Sprite SpriteFromTexture(Texture2D tex, string key)
        {
            var source = tex != null ? tex : MakePlaceholder("<null>", 8, 8);
            var sprite = Sprite.Create(source, new Rect(0f, 0f, source.width, source.height),
                                       new Vector2(0.5f, 0.5f), 100f);
            sprite.name = string.IsNullOrEmpty(key) ? "vn_placeholder_sprite" : key;
            return sprite;
        }

        private static AudioClip SilentClip(string cacheKey)
        {
            // "new AudioClip()" is illegal: a one sample clip made by AudioClip.Create is the documented stand-in.
            // Unity 6 has no AudioChannels enum in the runtime API, the channel count stays an int (1 = mono).
            var clip = AudioClip.Create("vn_silent_" + Sanitize(cacheKey), 1, 1, SilentClipFrequency, false);
            clip.SetData(new float[1], 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return clip;
        }

        /// <summary>Flat color plate with a light border and dark bands; identical for the same key on every run.</summary>
        private static Texture2D MakePlaceholder(string key, int width, int height)
        {
            width = System.Math.Max(2, width);
            height = System.Math.Max(2, height);
            var hash = StableHash(key);
            var baseColor = ToColor(hash, 0.32f, 0.55f);
            var dark = ToColor(ShiftRight(hash, 3), 0.45f, 0.30f);
            var light = ToColor(ShiftRight(hash, 7), 0.25f, 0.78f);

            var px = new Color32[width * height];
            var border = Mathf.Max(2, Mathf.Min(width, height) / 32);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var edge = x < border || y < border || x >= width - border || y >= height - border;
                    if (edge) px[y * width + x] = light;
                    else if (((x + y) / 24) % 3 == 0) px[y * width + x] = dark;
                    else px[y * width + x] = baseColor;
                }
            }

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "ph_" + Sanitize(key),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset
            };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>FNV-1a over the UTF8 bytes, so a placeholder color depends only on the key text.</summary>
        public static uint StableHash(string text)
        {
            unchecked
            {
                var h = 2166136261u;
                var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
                for (var i = 0; i < bytes.Length; i++)
                {
                    h ^= bytes[i];
                    h *= 16777619u;
                }
                return h;
            }
        }

        private static uint ShiftRight(uint value, int bits) => value >> bits;

        private static Color32 ToColor(uint hash, float saturation, float value)
        {
            var hue = (hash % 360u) / 360f;
            return Color.HSVToRGB(hue, saturation, value);
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "none";
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                else sb.Append('-');
            }
            return sb.ToString();
        }

        private static string ClipKey(VNAudioBus bus, string cue) => bus + ":" + cue;

        private void RememberAudioHint(VNAudioBus bus, string cue)
        {
            if (string.IsNullOrEmpty(cue)) return;
            if (!audioHints.TryGetValue(bus, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                audioHints[bus] = set;
            }
            set.Add(cue);
        }

        private void WarnMissing(string identity, string message)
        {
            if (warned.Contains(identity)) return;
            warned.Add(identity);
            VNLog.Warn(LogArea, message);
        }
    }
}
