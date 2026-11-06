// REZONANCE VN EDITOR - TMP font asset generator.
// Builds the Cyrillic capable TMP font assets from the Windows Arial pair and stores them in
// Assets/Rezonance/Art/fonts. Only overloads that exist in the installed com.unity.ugui package are called:
// TMP_FontAsset.CreateFontAsset(string fontFilePath, int faceIndex, int samplingPointSize, int atlasPadding,
// GlyphRenderMode renderMode, int atlasWidth, int atlasHeight) and the system family/style overload.
// Every failure becomes a note in the import report instead of an exception - the project has to compile and
// run even when the fonts are missing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Rezonance.VN.Core;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;

namespace Rezonance.VN.Import
{
    /// <summary>Generates the two TMP font assets (regular + bold) of the project.</summary>
    public static class VNFontGenerator
    {
        const string LogArea = "fonts";
        const int AtlasPadding = 9;

        /// <summary>Where the generated assets live (ARCHITECTURE.md section 3 lists Art/fonts).</summary>
        public const string FontFolder = "Assets/Rezonance/Art/fonts";

        static readonly string[] SystemFontDirs =
        {
            "C:/Windows/Fonts"
        };

        /// <summary>Rezonance menu entry: force rebuild of both font assets.</summary>
        [MenuItem("Rezonance/Tools/Generate TMP Font Assets (Arial)")]
        public static void MenuGenerate()
        {
            var notes = new List<string>();
            Generate(VNImportSettings.LoadOrCreate(), notes, true);
            for (var i = 0; i < notes.Count; i++) VNLog.Info(LogArea, notes[i]);
        }

        /// <summary>
        /// Creates RezonanceArial.asset and RezonanceArial-Bold.asset. Existing assets are kept unless
        /// forceOverwrite is set, so a reimport never throws away hand tuned atlas settings.
        /// </summary>
        public static void Generate(VNImportSettings settings, List<string> notes, bool forceOverwrite = false)
        {
            if (settings == null) settings = VNImportSettings.LoadOrCreate();
            if (notes == null) notes = new List<string>();

            VNImportSettings.EnsureFolder(FontFolder);

            var pointSize = settings.fontSamplePointSize > 0 ? settings.fontSamplePointSize : 90;
            var atlas = RoundUpToPowerOfTwo(settings.fontAtlasSize > 0 ? settings.fontAtlasSize : 2048);

            GenerateOne(settings, notes, "RezonanceArial", "arial.ttf", "Arial", "Regular", pointSize, atlas, forceOverwrite);
            GenerateOne(settings, notes, "RezonanceArial-Bold", "arialbd.ttf", "Arial", "Bold", pointSize, atlas, forceOverwrite);

            if (notes.Count == 0) notes.Add("font assets already present, nothing rebuilt");
        }

        static void GenerateOne(VNImportSettings settings, List<string> notes, string assetName, string ttfName,
                                string familyName, string styleName, int pointSize, int atlasSize, bool forceOverwrite)
        {
            var assetPath = FontFolder + "/" + assetName + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (existing != null && !forceOverwrite)
            {
                notes.Add(assetName + ": kept the existing asset (" + assetPath + ")");
                return;
            }

            TMP_FontAsset asset = null;
            var route = new StringBuilder();

            var filePath = FindFontFile(ttfName);
            if (filePath != null)
            {
                try
                {
                    asset = TMP_FontAsset.CreateFontAsset(filePath, 0, pointSize, AtlasPadding,
                        GlyphRenderMode.SDFAA, atlasSize, atlasSize);
                    if (asset != null) route.Append("font file ").Append(ttfName);
                }
                catch (Exception e)
                {
                    notes.Add(assetName + ": CreateFontAsset(path) threw " + e.Message);
                }
            }
            else
            {
                notes.Add(assetName + ": " + ttfName + " was not found in the system font folders");
            }

            if (asset == null)
            {
                try
                {
                    asset = TMP_FontAsset.CreateFontAsset(familyName, styleName, pointSize);
                    if (asset != null) route.Append("system font ").Append(familyName).Append('/').Append(styleName);
                }
                catch (Exception e)
                {
                    notes.Add(assetName + ": CreateFontAsset(family, style) threw " + e.Message);
                }
            }

            if (asset == null)
            {
                notes.Add(assetName + ": NO FONT ASSET GENERATED - the UI falls back to the project default TMP font");
                VNLog.Warning(LogArea, "Could not build the TMP font asset " + assetName + " from " + ttfName);
                return;
            }

            try
            {
                asset.name = assetName;
                if (existing != null && forceOverwrite) AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.CreateAsset(asset, assetPath);
                EditorUtility.SetDirty(asset);

                notes.Add(assetName + ": " + route + ", " + pointSize.ToString(CultureInfo.InvariantCulture) +
                          " pt, atlas " + atlasSize.ToString(CultureInfo.InvariantCulture) + " px, SDFAA, " +
                          asset.atlasPopulationMode + " population");
            }
            catch (Exception e)
            {
                notes.Add(assetName + ": CreateAsset failed - " + e.Message);
                VNLog.Warning(LogArea, "Could not save the TMP font asset " + assetPath + ": " + e.Message);
            }
        }

        /// <summary>Finds a system truetype file, project copy first so a non Windows box can still ship one.</summary>
        public static string FindFontFile(string ttfName)
        {
            var local = FontFolder + "/" + ttfName;
            if (File.Exists(VNImportSettings.ToAbsolute(local))) return VNImportSettings.ToAbsolute(local);
            for (var i = 0; i < SystemFontDirs.Length; i++)
            {
                var candidate = Path.Combine(SystemFontDirs[i], ttfName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static int RoundUpToPowerOfTwo(int value)
        {
            var result = 64;
            while (result < value && result < 8192) result *= 2;
            return result;
        }
    }
}
