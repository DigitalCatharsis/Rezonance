// REZONANCE VN EDITOR - importer orchestration.
// Reads the READABLE markdown, parses it, merges the hand authored layers (speaker map, stage overlay,
// condition bindings) and writes every generated artifact listed in ARCHITECTURE.md section 4.1.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>Menu entry points and the single import pass.</summary>
    public static class VNMarkdownImporter
    {
        const string LogArea = "import";

        /// <summary>Rezonance/Import Act I (READABLE) - parse, seed, write, report.</summary>
        [MenuItem("Rezonance/Import Act I (READABLE)")]
        public static void ImportAct1()
        {
            Import(VNImportSettings.LoadOrCreate(), false);
        }

        /// <summary>Rezonance/Reimport All - the same pass plus a forced regeneration of the placeholder art.</summary>
        [MenuItem("Rezonance/Reimport All")]
        public static void ReimportAll()
        {
            var settings = VNImportSettings.LoadOrCreate();
            var run = Import(settings, true);
            if (run == null) return;
            if (settings.generateFontAssets) VNFontGenerator.Generate(settings, run.FontNotes);
            if (settings.generatePlaceholderArt) VNPlaceholderArt.Generate(settings, run.ArtNotes, true);
        }

        /// <summary>
        /// Runs one import. Returns null when the source is unreadable; the caller of a failed import sees a
        /// single error in the console and nothing on disk is touched.
        /// </summary>
        public static VNImportRun Import(VNImportSettings settings, bool forcePlaceholderArt)
        {
            if (settings == null) settings = VNImportSettings.LoadOrCreate();
            var sourcePath = VNImportSettings.ToAbsolute(settings.sourcePath);
            if (!File.Exists(sourcePath))
            {
                VNLog.Error(LogArea, "Source markdown not found: " + settings.sourcePath);
                return null;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Rezonance VN import", "Reading " + Path.GetFileName(sourcePath), 0.05f);
                settings.EnsureFolders();

                var bytes = File.ReadAllBytes(sourcePath);
                var text = DecodeUtf8(bytes);
                var sha = Sha256Hex(bytes);

                var run = new VNImportRun
                {
                    Settings = settings,
                    SourceSha256 = sha,
                    SourceBytes = bytes.LongLength
                };

                run.Parse = VNMarkdownParser.Parse(text, settings.actId, settings.sourcePath);
                run.Parse.Data.sourceSha = sha;
                run.Parse.Data.importedAtUtc = DateTime.UtcNow;

                var map = VNSpeakerMapGenerator.LoadOrCreate(settings.SpeakerMapPath);
                run.SpeakerSeed = VNSpeakerMapGenerator.Seed(map, run.Parse);
                run.RolesStamped = VNSpeakerMapGenerator.ApplyRolesToNodes(run.Parse.Data, map);

                var overlay = VNStageOverlaySeeder.LoadOrCreate(settings.StageOverlayPath);
                run.OverlaySeed = VNStageOverlaySeeder.Seed(overlay, run.Parse.Data, settings);

                var bindings = VNConditionBinder.LoadOrCreate(settings.ConditionBindingsPath, settings.actId);
                run.ConditionApply = VNConditionBinder.ApplyBindings(run.Parse.Data, bindings, settings.unboundConditionalsDefaultTrue);
                if (bindings.bindings.Count != run.ConditionApply.Bound)
                    run.ContractTodos.Add("bindings file holds " + bindings.bindings.Count +
                                          " rows, " + run.ConditionApply.Bound + " markers are bound - stale or unauthored rows exist");

                CollectContractNotes(run);

                var json = JsonUtility.ToJson(run.Parse.Data, true);
                WriteJsonAndCheckDeterminism(settings, json, run);

                run.LocalizationKeys = VNLocalizationTables.WriteTables(run.Parse.Data, map.Rows,
                    settings.RuCsvPath, settings.EnCsvPath, settings.UiCsvPath);
                run.RuChecksum = VNLocalizationTables.RuTableChecksum.ToString(CultureInfo.InvariantCulture) +
                                 " (" + VNLocalizationTables.CanonicalUiKeyCount + " canonical ui.* keys)";

                if (forcePlaceholderArt) settings.overwritePlaceholderArt = true;
                if (settings.generatePlaceholderArt) VNPlaceholderArt.Generate(settings, run.ArtNotes, forcePlaceholderArt);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                VNValidationReport.Write(run);
                return run;
            }
            catch (Exception e)
            {
                VNLog.Error(LogArea, "Import failed: " + e);
                return null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>UTF-8 decode that tolerates a leading BOM.</summary>
        public static string DecodeUtf8(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
            return new UTF8Encoding(false, false).GetString(bytes, offset, bytes.Length - offset);
        }

        static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static string Sha256Text(string text)
        {
            return Sha256Hex(new UTF8Encoding(false).GetBytes(text ?? string.Empty));
        }

        /// <summary>
        /// Writes the JSON and compares it with the previous run. The importedAtUtc field is dropped from the
        /// fingerprint, because a timestamp is the only thing allowed to change between two identical imports.
        /// </summary>
        static void WriteJsonAndCheckDeterminism(VNImportSettings settings, string json, VNImportRun run)
        {
            var full = VNImportSettings.ToAbsolute(settings.DataJsonPath);
            var previous = File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : null;
            var previousFingerprintPath = VNImportSettings.ToAbsolute(settings.FingerprintPath);
            var previousFingerprint = File.Exists(previousFingerprintPath)
                ? File.ReadAllText(previousFingerprintPath, Encoding.UTF8).Trim()
                : null;

            var fingerprint = Sha256Text(CanonicalJson(json));

            File.WriteAllText(full, json, new UTF8Encoding(false));

            string byteState;
            if (previous == null) byteState = "first import, nothing to compare";
            else byteState = previous == json ? "JSON byte-identical to the previous import" : "JSON bytes differ from the previous import";
            var hashState = previousFingerprint == null
                ? "no stored fingerprint yet"
                : (previousFingerprint == fingerprint ? "timestamp-free fingerprint identical" : "fingerprint CHANGED");
            run.Determinism = byteState + "; " + hashState + " (fingerprint " + fingerprint.Substring(0, 16) + ")";

            if (previous != null && previous != json)
            {
                // The timestamp is inside the JSON only if Unity serializes DateTime at all; when it does not,
                // the two texts differ for a real reason and the report has to say so.
                if (CanonicalJson(previous) == CanonicalJson(json))
                    run.Determinism += " (difference confined to the importedAtUtc line)";
                else
                    run.Determinism += " (structural change: text or ids moved)";
            }

            var folder = Path.GetDirectoryName(previousFingerprintPath);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(previousFingerprintPath, fingerprint, new UTF8Encoding(false));
        }

        /// <summary>Removes the timestamp line and normalizes line ends before hashing.</summary>
        static string CanonicalJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;
            var lines = json.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder(json.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("importedAtUtc", StringComparison.Ordinal) >= 0) continue;
                sb.Append(lines[i]).Append('\n');
            }
            return sb.ToString();
        }

        static void CollectContractNotes(VNImportRun run)
        {
            run.ContractTodos.Add("ConditionEvaluator grammar is not written yet: the binder drafts flag(\"slug\") " +
                                  "and chosen(\"slug\") terms, the engine must accept exactly these names");
            run.ContractTodos.Add("VNGlossaryEntry has no term_en field, so EN term names go through the extra " +
                                  "key gl.{slug}.term in ui.csv");
            run.ContractTodos.Add("VNNode has no miniGameId field: the id is derived from the node id " +
                                  "(mg_{branch}_{ordinal}) and stored in VNStageOverlay rows");
            run.ContractTodos.Add("ARCHITECTURE 1 lists 77 conditional markers, V9 has a different number; the " +
                                  "source is treated as the truth");
            run.ContractTodos.Add("ARCHITECTURE 10 asks for UTF-8 with BOM in .cs files while the build brief says " +
                                  "no BOM; the Cyrillic literals are kept in data tables only and the report prints " +
                                  "RuTableChecksum so a mangling accident is visible");
        }
    }
}
