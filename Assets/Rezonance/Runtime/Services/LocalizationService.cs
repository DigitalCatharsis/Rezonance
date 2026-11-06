// REZONANCE VN ENGINE - localization service (UI/services module).
// Reads CSV tables from Resources/Rezonance/l10n/{ru,en,ui}.csv and implements ILocalizer with a
// RU fallback (ARCHITECTURE.md section 7). Tolerant to BOM, CRLF and broken rows: nothing here throws.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Services
{
    /// <summary>
    /// CSV backed text tables. Node text lives in ru/en (key == node id), interface strings in ui
    /// (key == ui.*), speaker plates in ui (key == name.*).
    /// </summary>
    public sealed class LocalizationService : ILocalizer
    {
        private const string Area = "l10n";

        /// <summary>Resources folder the importer writes tables into (ARCHITECTURE.md 4.1).</summary>
        public const string ResourceFolder = "Rezonance/l10n";

        /// <summary>Shared interface table (ui.*, name.*).</summary>
        public const string UiTableResource = ResourceFolder + "/ui";

        private readonly Dictionary<VNLanguage, Dictionary<string, string>> nodeTables =
            new Dictionary<VNLanguage, Dictionary<string, string>>();
        private readonly Dictionary<VNLanguage, Dictionary<string, string>> interfaceTables =
            new Dictionary<VNLanguage, Dictionary<string, string>>();
        private readonly HashSet<string> allKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> reportedMissing = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> speakerCache = new Dictionary<string, string>(StringComparer.Ordinal);

        private Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.Ordinal);
        private int missingInCurrent;
        private bool loaded;

        /// <summary>When true Get() returns the key itself - QA aid, wired to VNSettings.devShowKeys.</summary>
        public Func<bool> DevShowKeysProvider;

        /// <summary>Language reported through <see cref="ILocalizer.Language"/>.</summary>
        public VNLanguage Language { get; private set; } = VNLanguage.Ru;

        public event Action<VNLanguage> LanguageChanged;

        /// <summary>Rows in the active language that the other language does not have.</summary>
        public int MissingInCurrentLanguage => missingInCurrent;

        /// <summary>Distinct keys across every loaded table.</summary>
        public int TotalKeys => allKeys.Count;

        /// <summary>Rows loaded per table, for the debug overlay and the import report.</summary>
        public string CoverageSummary =>
            "ru=" + Count(nodeTables, VNLanguage.Ru) + " en=" + Count(nodeTables, VNLanguage.En) +
            " ui=" + Count(interfaceTables, VNLanguage.Ru) + "/" + Count(interfaceTables, VNLanguage.En) +
            " total=" + TotalKeys;

        /// <summary>Builds the service and loads whatever tables exist in Resources.</summary>
        public LocalizationService() : this(VNLanguage.Ru) { }

        /// <summary>Builds the service and loads tables, starting in <paramref name="initial"/>.</summary>
        public LocalizationService(VNLanguage initial)
        {
            LoadAll();
            ApplyLanguage(initial, raiseEvent: false);
        }

        // ------------------------------------------------------------------ loading

        /// <summary>Rereads every table from Resources. Safe to call after a reimport.</summary>
        public void Reload()
        {
            var keep = Language;
            LoadAll();
            ApplyLanguage(keep, raiseEvent: false);
        }

        private void LoadAll()
        {
            nodeTables[VNLanguage.Ru] = ReadResource(ResourceFolder + "/ru");
            nodeTables[VNLanguage.En] = ReadResource(ResourceFolder + "/en");

            // ui.csv holds the interface strings; optional ui_ru/ui_en split them per language.
            var shared = ReadResource(UiTableResource);
            var ruUi = ReadResource(ResourceFolder + "/ui_ru");
            var enUi = ReadResource(ResourceFolder + "/ui_en");

            interfaceTables[VNLanguage.Ru] = Merge(shared, ruUi);
            interfaceTables[VNLanguage.En] = Merge(shared, enUi);

            loaded = true;
            RebuildCurrent();
            VNLog.Info(Area, "Tables loaded: " + CoverageSummary);
        }

        private static Dictionary<string, string> Merge(Dictionary<string, string> baseTable, Dictionary<string, string> overrides)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            CopyInto(result, baseTable);
            CopyInto(result, overrides);
            return result;
        }

        private static void CopyInto(Dictionary<string, string> target, Dictionary<string, string> source)
        {
            if (source == null) return;
            foreach (var kv in source) target[kv.Key] = kv.Value;
        }

        private static Dictionary<string, string> ReadResource(string path)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var asset = Resources.Load<TextAsset>(path);
                if (asset == null)
                {
                    VNLog.Warn(Area, "Localization table missing: Resources/" + path + ".csv");
                    return rows;
                }
                ParseCsv(asset.text, rows);
            }
            catch (Exception e)
            {
                VNLog.Error(Area, "Failed reading Resources/" + path + ": " + e.Message);
            }
            return rows;
        }

        /// <summary>Feeds a table from a raw CSV string (tests, editor tooling, hot reload).</summary>
        public void LoadNodeTableFromCsv(VNLanguage language, string csv)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            ParseCsv(csv, rows);
            nodeTables[language] = rows;
            loaded = true;
            RebuildCurrent();
        }

        /// <summary>Feeds the interface table from a raw CSV string (tests, editor tooling).</summary>
        public void LoadInterfaceTableFromCsv(VNLanguage language, string csv)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            ParseCsv(csv, rows);
            interfaceTables[language] = rows;
            loaded = true;
            RebuildCurrent();
        }

        /// <summary>RFC4180 parser tolerant to BOM, CR-only, LF-only and CRLF endings.</summary>
        public static void ParseCsv(string text, IDictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(text) || into == null) return;
            if (text[0] == (char)0xFEFF) text = text.Substring(1);

            var fields = new List<string>(4);
            var builder = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { builder.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else builder.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"':
                        if (builder.Length == 0) inQuotes = true;
                        else builder.Append('"');
                        break;
                    case ',':
                        fields.Add(builder.ToString());
                        builder.Clear();
                        break;
                    case '\r':
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        AddRow(fields, builder, into);
                        break;
                    case '\n':
                        AddRow(fields, builder, into);
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }
            AddRow(fields, builder, into);
        }

        private static void AddRow(List<string> fields, StringBuilder pending, IDictionary<string, string> into)
        {
            fields.Add(pending.ToString());
            pending.Clear();

            if (fields.Count < 2) { fields.Clear(); return; }
            var key = fields[0].Trim();
            if (key.Length == 0 || key[0] == '#') { fields.Clear(); return; }
            into[key] = fields[1];
            fields.Clear();
        }

        // ------------------------------------------------------------------ lookup

        private void RebuildCurrent()
        {
            var combined = new Dictionary<string, string>(StringComparer.Ordinal);
            // Source language first: it is the fallback for every untranslated node (section 7).
            CopyInto(combined, Get(nodeTables, VNLanguage.Ru));
            CopyInto(combined, Get(interfaceTables, Language));
            CopyInto(combined, Get(nodeTables, Language));

            current = combined;

            allKeys.Clear();
            AddKeys(allKeys, Get(nodeTables, VNLanguage.Ru));
            AddKeys(allKeys, Get(nodeTables, VNLanguage.En));
            AddKeys(allKeys, Get(interfaceTables, VNLanguage.Ru));
            AddKeys(allKeys, Get(interfaceTables, VNLanguage.En));

            missingInCurrent = 0;
            foreach (var k in allKeys)
                if (!current.ContainsKey(k)) missingInCurrent++;
        }

        private static void AddKeys(HashSet<string> target, Dictionary<string, string> table)
        {
            if (table == null) return;
            foreach (var k in table.Keys) target.Add(k);
        }

        private Dictionary<string, string> Get(Dictionary<VNLanguage, Dictionary<string, string>> map, VNLanguage lang)
        {
            return map.TryGetValue(lang, out var t) ? t : null;
        }

        private static int Count(Dictionary<VNLanguage, Dictionary<string, string>> map, VNLanguage lang)
        {
            return map.TryGetValue(lang, out var t) ? t.Count : 0;
        }

        public VNLanguage CurrentLanguageForTest => Language;

        /// <summary>True when the key resolves in any loaded table.</summary>
        public bool TryGet(string key, out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(key)) return false;
            if (!loaded) { LoadAll(); }

            if (current.TryGetValue(key, out text)) return true;

            if (!reportedMissing.Contains(key))
            {
                reportedMissing.Add(key);
                VNLog.Warn(Area, "Missing localization key: " + key);
            }
            return false;
        }

        /// <summary>Localized string; falls back to RU, then to the key itself (only shown in dev mode).</summary>
        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (IsDevKeys()) return key;
            return TryGet(key, out var text) ? text : key;
        }

        private bool IsDevKeys()
        {
            try { return DevShowKeysProvider != null && DevShowKeysProvider(); }
            catch (Exception e) { VNLog.Warn(Area, "devShowKeys provider failed: " + e.Message); return false; }
        }

        /// <summary>Speaker label to display name through the name.* key, RU label as last resort.</summary>
        public string SpeakerDisplayName(string speakerLabel)
        {
            if (string.IsNullOrEmpty(speakerLabel)) return string.Empty;
            if (IsDevKeys()) return "name." + VNSlug.Of(speakerLabel);
            if (speakerCache.TryGetValue(speakerLabel, out var cached)) return cached;

            var result = ResolveSpeaker(speakerLabel);
            speakerCache[speakerLabel] = result;
            return result;
        }

        private string ResolveSpeaker(string speakerLabel)
        {
            var slug = VNSlug.Of(speakerLabel);
            switch (slug)
            {
                case "vy":
                case "you":
                case "player":
                    return Get("ui.speaker.player");
                case "vnutrenniy-golos":
                case "inner-voice":
                case "inner":
                    return Get("ui.speaker.inner_voice");
                case "ekran":
                case "terminal":
                case "system":
                    return Get("ui.speaker.system");
            }

            if (TryGet("name." + slug, out var name)) return name;
            // Unknown one-off ambient voice: show the source label, never the key (section 7).
            return speakerLabel;
        }

        /// <summary>Drops resolved plate names; call after a table reload or a language switch.</summary>
        public void InvalidateSpeakerCache()
        {
            speakerCache.Clear();
            reportedMissing.Clear();
        }

        /// <summary>Switches the active language; a repeat call with the same value is free.</summary>
        public void SetLanguage(VNLanguage language)
        {
            if (language == Language && loaded) return;
            ApplyLanguage(language, raiseEvent: true);
        }

        private void ApplyLanguage(VNLanguage language, bool raiseEvent)
        {
            Language = language;
            RebuildCurrent();
            speakerCache.Clear();
            if (!raiseEvent) return;
            VNLog.Info(Area, "Language -> " + language + " (" + CoverageSummary + ")");
            try { LanguageChanged?.Invoke(language); }
            catch (Exception e) { VNLog.Error(Area, "LanguageChanged handler failed: " + e.Message); }
        }
    }
}
