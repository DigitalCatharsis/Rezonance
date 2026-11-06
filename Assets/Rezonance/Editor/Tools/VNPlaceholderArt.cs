// REZONANCE VN EDITOR - placeholder art generator.
// Flat colour placeholder PNGs for every backdrop key of VNStageOverlay and every portrait key reachable
// through VNSpeakerMap: backdrops 1920x1080 in Resources/Rezonance/art/bg, portraits 1024x1536 in
// Resources/Rezonance/art/por. The colour comes from a stable FNV-1a hash of the key, so the same key always
// produces the same picture on any machine (string.GetHashCode is randomized per process and is never used).
// Nothing is drawn as text: the art folders must stay replaceable by real artwork with identical file names.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Rezonance.VN.Core;
using UnityEditor;
using UnityEngine;

namespace Rezonance.VN.Import
{
    /// <summary>Writes the placeholder PNGs and configures their importers.</summary>
    public static class VNPlaceholderArt
    {
        const string LogArea = "art";

        public const int BackdropWidth = 1920;
        public const int BackdropHeight = 1080;
        public const int PortraitWidth = 1024;
        public const int PortraitHeight = 1536;

        /// <summary>Rezonance menu entry: rewrite every placeholder from the current key lists.</summary>
        [MenuItem("Rezonance/Tools/Generate Placeholder Art")]
        public static void MenuGenerate()
        {
            var settings = VNImportSettings.LoadOrCreate();
            var notes = new List<string>();
            Generate(settings, notes, true);
            for (var i = 0; i < notes.Count; i++) VNLog.Info(LogArea, notes[i]);
        }

        /// <summary>
        /// Generates the missing placeholders (or all of them when forceOverwrite is on). Notes are appended to
        /// the caller list so the import report can show what happened without a second pass over the files.
        /// </summary>
        public static void Generate(VNImportSettings settings, List<string> notes, bool forceOverwrite)
        {
            if (settings == null) settings = VNImportSettings.LoadOrCreate();
            if (notes == null) notes = new List<string>();

            var overwrite = forceOverwrite || settings.overwritePlaceholderArt;

            try
            {
                var overlay = AssetDatabase.LoadAssetAtPath<VNStageOverlay>(settings.StageOverlayPath);
                var map = AssetDatabase.LoadAssetAtPath<VNSpeakerMap>(settings.SpeakerMapPath);

                var backdropKeys = VNStageOverlaySeeder.BackdropKeys(overlay);
                var portraitKeys = VNSpeakerMapGenerator.PortraitKeysForPlaceholders(map, settings.portraitPrefix);

                if (overlay == null) notes.Add("backdrops: VNStageOverlay.asset is missing, nothing to generate");
                if (map == null) notes.Add("portraits: VNSpeakerMap.asset is missing, nothing to generate");

                var bg = WriteSet(settings.BackdropFolder, backdropKeys, BackdropWidth, BackdropHeight, false,
                    overwrite, notes, "backdrops");
                var por = WriteSet(settings.PortraitFolder, portraitKeys, PortraitWidth, PortraitHeight, true,
                    overwrite, notes, "portraits");

                notes.Add("placeholder summary: " +
                          bg.written.ToString(CultureInfo.InvariantCulture) + "/" +
                          backdropKeys.Count.ToString(CultureInfo.InvariantCulture) + " backdrops written (" +
                          bg.skipped + " kept), " +
                          por.written.ToString(CultureInfo.InvariantCulture) + "/" +
                          portraitKeys.Count.ToString(CultureInfo.InvariantCulture) + " portraits written (" +
                          por.skipped + " kept)");
                notes.Add("importer note: TextureImporter has no \"Full Color\" colour space in this Unity version; " +
                          "sRGBTexture=true, uncompressed platform default and mipmap off are used instead");
            }
            catch (Exception e)
            {
                notes.Add("placeholder generation failed: " + e.Message);
                VNLog.Warning(LogArea, "Placeholder art generation failed: " + e.Message);
            }
        }

        struct SetStats
        {
            public int written;
            public int skipped;
            public SetStats(int w, int s) { written = w; skipped = s; }
        }

        static SetStats WriteSet(string projectFolder, List<string> keys, int width, int height, bool sprite,
                                bool overwrite, List<string> notes, string label)
        {
            var stats = new SetStats(0, 0);
            VNImportSettings.EnsureFolder(projectFolder);
            var absoluteFolder = VNImportSettings.ToAbsolute(projectFolder);
            if (!Directory.Exists(absoluteFolder)) Directory.CreateDirectory(absoluteFolder);

            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (string.IsNullOrEmpty(key)) continue;
                var assetPath = projectFolder + "/" + key + ".png";
                var fullPath = Path.Combine(absoluteFolder, key + ".png");

                if (File.Exists(fullPath) && !overwrite)
                {
                    stats.skipped++;
                    continue;
                }

                try
                {
                    var bytes = RenderPng(key, width, height, sprite);
                    File.WriteAllBytes(fullPath, bytes);
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    ConfigureImporter(assetPath, sprite, notes);
                    stats.written++;
                }
                catch (Exception e)
                {
                    notes.Add(label + " " + key + ": " + e.Message);
                }
            }
            return stats;
        }

        /// <summary>Renders one flat placeholder and returns its PNG bytes.</summary>
        public static byte[] RenderPng(string key, int width, int height, bool portrait)
        {
            var baseColor = ColorFor(key, portrait);
            var plate = Lighten(baseColor, portrait ? 0.45f : 0.55f);

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = key;
            var pixels = new Color32[width * height];

            // Texture rows start at the bottom, so the plate sits in the low index range.
            var plateTop = portrait ? (int)(height * 0.14f) : (int)(height * 0.18f);
            var rampTop = plateTop + (int)(height * 0.05f);

            for (var y = 0; y < height; y++)
            {
                Color32 row;
                if (y < plateTop)
                {
                    row = plate;
                }
                else if (y < rampTop)
                {
                    var t = (float)(y - plateTop) / Mathf.Max(1, rampTop - plateTop);
                    row = Lerp(plate, baseColor, t);
                }
                else
                {
                    // Subtle vertical gradient so a flat colour still reads as a stage picture.
                    var shade = Mathf.Lerp(0.82f, 1.0f, (float)(height - y) / (height - rampTop));
                    row = Scale(baseColor, shade);
                }

                for (var x = 0; x < width; x++) pixels[y * width + x] = row;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);

            byte[] png;
            try
            {
                png = texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            if (png == null || png.Length == 0)
                throw new InvalidOperationException("EncodeToPNG returned nothing (imageconversion module missing?)");
            return png;
        }

        static void ConfigureImporter(string assetPath, bool sprite, List<string> notes)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                notes.Add(assetPath + ": no TextureImporter after ImportAsset");
                return;
            }

            importer.textureType = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            if (sprite)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaSource = TextureImporterAlphaSource.None;
            }
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureWrapMode = TextureWrapMode.Clamp;
            importer.ignoreMasterTextureLimit = true;

            try
            {
                importer.SaveAndReimport();
            }
            catch (Exception e)
            {
                notes.Add(assetPath + ": importer settings failed - " + e.Message);
            }
        }

        /// <summary>Stable hue from the key: same file name always the same colour.</summary>
        public static Color32 ColorFor(string key, bool portrait)
        {
            var hash = Fnv1a(key);
            var hue = (hash % 360u) / 360f;
            var saturation = portrait ? 0.30f : 0.22f;
            var value = portrait ? 0.62f : 0.44f;
            // A little brightness spread keeps neighbouring scenes apart without becoming noisy.
            value *= 0.85f + (int)((hash >> 8) % 100u) / 100f * 0.30f;
            var color = Color.HSVToRGB(hue, saturation, Mathf.Clamp01(value));
            return new Color32((byte)Mathf.RoundToInt(color.r * 255f),
                               (byte)Mathf.RoundToInt(color.g * 255f),
                               (byte)Mathf.RoundToInt(color.b * 255f), 255);
        }

        /// <summary>FNV-1a over the UTF-16 code units of the key; deterministic across processes.</summary>
        public static uint Fnv1a(string text)
        {
            var hash = 2166136261u;
            if (text == null) return hash;
            for (var i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= 16777619u;
            }
            return hash;
        }

        static Color32 Lighten(Color32 color, float amount)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(color.r / 255f, 0.93f, amount) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Lerp(color.g / 255f, 0.93f, amount) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Lerp(color.b / 255f, 0.93f, amount) * 255f), 255);
        }

        static Color32 Scale(Color32 color, float factor)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * factor), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * factor), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * factor), 0, 255), 255);
        }

        static Color32 Lerp(Color32 a, Color32 b, float t)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(a.r, b.r, t)), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(a.g, b.g, t)), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(a.b, b.b, t)), 0, 255), 255);
        }
    }
}
