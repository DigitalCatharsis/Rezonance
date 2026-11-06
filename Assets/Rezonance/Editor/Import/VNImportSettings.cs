// REZONANCE VN EDITOR - importer settings asset.
// Holds the source path and the switches of the markdown -> script data pipeline.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Rezonance.VN.Import
{
    /// <summary>Settings of the READABLE markdown importer (one asset, created on demand).</summary>
    public sealed class VNImportSettings : ScriptableObject
    {
        /// <summary>Where the default settings asset lives.</summary>
        public const string DefaultAssetPath = "Assets/Rezonance/Data/VNImportSettings.asset";

        /// <summary>Absolute path of the source markdown (the single text truth).</summary>
        public string sourcePath = "C:/Users/Alex/Desktop/РЕЗОНАНС/Resonance_ACT_1_Readable_V9.md";

        /// <summary>Act id used as the first id segment: "act1".</summary>
        public string actId = "act1";

        /// <summary>Resources root for runtime loadable artifacts.</summary>
        public string outputFolder = "Assets/Rezonance/Resources/Rezonance";

        /// <summary>Non-Resources folder for the hand-authored overlay / speaker map / bindings.</summary>
        public string dataFolder = "Assets/Rezonance/Data";

        /// <summary>Folder of the validation report.</summary>
        public string reportsFolder = "Assets/Rezonance/Reports";

        /// <summary>Generate flat colour placeholder backdrops and portraits.</summary>
        public bool generatePlaceholderArt = true;

        /// <summary>Regenerate placeholder PNGs that already exist.</summary>
        public bool overwritePlaceholderArt = false;

        /// <summary>Generate the TMP font assets from the system Arial pair.</summary>
        public bool generateFontAssets = true;

        /// <summary>
        /// Conditional nodes without a binding get "true" instead of an empty expression, so nothing is
        /// silently skipped before the bindings are authored. Turn off once every condition is bound.
        /// </summary>
        public bool unboundConditionalsDefaultTrue = true;

        /// <summary>Prefix of backdrop asset keys.</summary>
        public string backdropPrefix = "bg_";

        /// <summary>Prefix of portrait asset keys.</summary>
        public string portraitPrefix = "por_";

        /// <summary>Sampling size of generated TMP atlases.</summary>
        public int fontSamplePointSize = 90;

        /// <summary>Atlas edge of generated TMP fonts.</summary>
        public int fontAtlasSize = 2048;

        public string DataJsonPath => outputFolder + "/data/" + actId + ".json";
        public string RuCsvPath => outputFolder + "/l10n/ru.csv";
        public string EnCsvPath => outputFolder + "/l10n/en.csv";
        public string UiCsvPath => outputFolder + "/l10n/ui.csv";
        public string BackdropFolder => outputFolder + "/art/bg";
        public string PortraitFolder => outputFolder + "/art/por";
        public string StageOverlayPath => dataFolder + "/VNStageOverlay.asset";
        public string SpeakerMapPath => dataFolder + "/VNSpeakerMap.asset";
        public string ConditionBindingsPath => dataFolder + "/VNConditionBindings.json";
        public string ReportPath => reportsFolder + "/vn_import_report.md";
        public string FingerprintPath => reportsFolder + "/act1_json_fingerprint.txt";

        /// <summary>Finds the settings asset, creates a default one when missing.</summary>
        public static VNImportSettings LoadOrCreate()
        {
            var found = AssetDatabase.FindAssets("t:VNImportSettings");
            if (found != null && found.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(found[0]);
                var asset = AssetDatabase.LoadAssetAtPath<VNImportSettings>(path);
                if (asset != null) return asset;
            }

            EnsureFolder(StripAssetPrefix(DefaultAssetPath));
            var created = CreateInstance<VNImportSettings>();
            created.name = "VNImportSettings";
            AssetDatabase.CreateAsset(created, DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return created;
        }

        /// <summary>Creates the asset explicitly from the menu.</summary>
        [MenuItem("Rezonance/Create Import Settings")]
        public static void MenuCreateSettings()
        {
            var settings = LoadOrCreate();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        /// <summary>Directory.CreateDirectory for every artifact folder of this settings set.</summary>
        public void EnsureFolders()
        {
            EnsureFolder(StripAssetPrefix(outputFolder + "/data"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/l10n"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/art/bg"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/art/por"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/audio/music"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/audio/ambience"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/audio/sfx"));
            EnsureFolder(StripAssetPrefix(outputFolder + "/audio/voice"));
            EnsureFolder(StripAssetPrefix(dataFolder));
            EnsureFolder(StripAssetPrefix(reportsFolder));
        }

        /// <summary>Creates a project folder if it is not there yet.</summary>
        public static void EnsureFolder(string projectRelativeFolder)
        {
            if (string.IsNullOrEmpty(projectRelativeFolder)) return;
            var full = ToAbsolute(projectRelativeFolder);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
        }

        /// <summary>Turns "Assets/..." into a full file system path.</summary>
        public static string ToAbsolute(string projectRelative)
        {
            if (string.IsNullOrEmpty(projectRelative)) return projectRelative;
            if (Path.IsPathRooted(projectRelative)) return projectRelative;
            return Path.Combine(Directory.GetCurrentDirectory(), projectRelative.Replace('/', Path.DirectorySeparatorChar));
        }

        static string StripAssetPrefix(string folder)
        {
            return folder.Replace('\\', '/').TrimEnd('/');
        }
    }
}
