// REZONANCE VN EDITOR - validation report writer.
// Produces Assets/Rezonance/Reports/vn_import_report.md plus a one line console summary. Everything the
// editor brief lists as "must be visible" has its own section here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>Everything one import run produced; filled by the importer, read by the report.</summary>
    public sealed class VNImportRun
    {
        public VNImportSettings Settings;
        public VNParseResult Parse;
        public VNSpeakerSeedResult SpeakerSeed;
        public VNOverlaySeedResult OverlaySeed;
        public VNConditionApplyResult ConditionApply;
        public string SourceSha256 = string.Empty;
        public long SourceBytes;
        public int LocalizationKeys;
        public int RolesStamped;
        public string Determinism = "not measured";
        public string RuChecksum;
        public readonly List<string> ArtNotes = new List<string>();
        public readonly List<string> FontNotes = new List<string>();
        public readonly List<string> ContractTodos = new List<string>();
    }

    /// <summary>Counters of the reference V9 source, used as the sanity gate.</summary>
    public static class VNExpectedCounts
    {
        public const int Scenes = 63;
        public const int Branches = 457;
        public const int Menus = 111;
        public const int Conditionals = 79;
        public const int MiniGames = 12;
        public const int BacktickLines = 54;
    }

    /// <summary>Writes the markdown report.</summary>
    public static class VNValidationReport
    {
        public static void Write(VNImportRun run)
        {
            if (run == null || run.Parse == null || run.Settings == null) return;
            var settings = run.Settings;
            var res = run.Parse;
            int scenes, branches, nodes, menus, conditionals, miniGames, screenTexts;
            var registry = res.Registry ?? VNDataRegistry.Build(res.Data);
            registry.GetCounts(out scenes, out branches, out nodes, out menus, out conditionals, out miniGames, out screenTexts);

            var sb = new StringBuilder();
            Line(sb, "# REZONANCE - VN import report");
            Line(sb, string.Empty);
            Line(sb, "- source: `" + (res.Data.sourcePath ?? string.Empty) + "`");
            Line(sb, "- bytes: " + Num(run.SourceBytes) + ", lines: " + Num(res.SourceLines));
            Line(sb, "- sha256: `" + run.SourceSha256 + "`");
            Line(sb, "- generated json: `" + settings.DataJsonPath + "` (formatVersion " + Num(VNDataFormat.Version) + ")");
            Line(sb, "- imported at (UTC): " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Line(sb, "- note: the timestamp is NOT inside the JSON; see the determinism section below.");
            Line(sb, string.Empty);

            Line(sb, "## 1. Counters against the reference");
            Line(sb, string.Empty);
            Line(sb, "| artifact | actual | expected (V9) |");
            Line(sb, "| --- | ---: | ---: |");
            Counter(sb, "scenes (`## `)", scenes, VNExpectedCounts.Scenes);
            Counter(sb, "branches (`### `)", branches, VNExpectedCounts.Branches);
            Counter(sb, "menu blocks", menus, VNExpectedCounts.Menus);
            Counter(sb, "conditional markers", conditionals, VNExpectedCounts.Conditionals);
            Counter(sb, "mini-game markers", miniGames, VNExpectedCounts.MiniGames);
            Counter(sb, "source lines with backticks", res.BacktickLines, VNExpectedCounts.BacktickLines);
            Line(sb, "| nodes total | " + Num(nodes) + " | - |");
            Line(sb, "| screen text nodes | " + Num(screenTexts) + " | - |");
            Line(sb, "| menu options | " + Num(res.MenuOptionLines) + " | - |");
            Line(sb, "| glossary entries | " + Num(res.Data.glossary != null ? res.Data.glossary.Count : 0) + " | - |");
            Line(sb, "| localization keys | " + Num(run.LocalizationKeys) + " | - |");
            Line(sb, "| implicit scene-intro branches | " + Num(res.SceneIntroBranches) + " | - |");
            Line(sb, "| fuzzy matched options | " + Num(res.FuzzyMatchedOptions) + " | - |");
            Line(sb, string.Empty);
            Line(sb, "> ARCHITECTURE.md section 1 quotes 77 conditional markers while V9 has " + Num(conditionals) +
                      "; the source wins, the document line is stale.");
            Line(sb, string.Empty);

            Line(sb, "## 2. Determinism");
            Line(sb, string.Empty);
            Line(sb, "- " + run.Determinism);
            Line(sb, "- RU/EN table self-check (RuTableChecksum): " + run.RuChecksum);
            Line(sb, "- node ids are derived from slugs and per-branch ordinals only, so editing a neighbour");
            Line(sb, "  never shifts another id; all iterations walk lists in document order, never dictionaries.");
            Line(sb, string.Empty);

            WriteDuplicates(sb, res);
            WriteEmptyText(sb, res);
            WriteUnresolved(sb, res);
            WriteResume(sb, res);
            WriteIssues(sb, "## 7. Other parser findings", res, new HashSet<string>(StringComparer.Ordinal)
            {
                "DUP_ID", "EMPTY_TEXT", "MENU_UNRESOLVED", "RESUME_FALLBACK", "RESUME_NONE"
            });
            WriteSpeakers(sb, run);
            WriteConditionals(sb, run);
            WriteTools(sb, run);
            WriteTodos(sb, run);

            var full = VNImportSettings.ToAbsolute(settings.ReportPath);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));

            var summary = "VN import: " + Num(scenes) + " scenes / " + Num(branches) + " branches / " +
                          Num(nodes) + " nodes / " + Num(menus) + " menus / " + Num(conditionals) + " conditionals / " +
                          Num(miniGames) + " mini games / " + Num(screenTexts) + " screen texts / " +
                          Num(res.Data.glossary != null ? res.Data.glossary.Count : 0) + " glossary. " +
                          "sha " + Shorten(run.SourceSha256, 12) + ". " + run.Determinism + " Report: " + settings.ReportPath;
            Rezonance.VN.Core.VNLog.Info("import", summary);
        }

        static void Counter(StringBuilder sb, string name, int actual, int expected)
        {
            var mark = actual == expected ? "ok" : "**diff " + Num(actual - expected) + "**";
            Line(sb, "| " + name + " | " + Num(actual) + " | " + Num(expected) + " " + mark + " |");
        }

        static void WriteDuplicates(StringBuilder sb, VNParseResult res)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dups = new List<string>();
            for (var i = 0; i < res.AllNodes.Count; i++)
            {
                var id = res.AllNodes[i].id;
                if (string.IsNullOrEmpty(id)) continue;
                if (seen.Add(id)) continue;
                dups.Add(id);
            }
            Line(sb, "## 3. Duplicate node ids");
            Line(sb, string.Empty);
            if (dups.Count == 0) Line(sb, "- none");
            else for (var i = 0; i < dups.Count && i < 200; i++) Line(sb, "- " + dups[i]);
            Line(sb, string.Empty);
        }

        static void WriteEmptyText(StringBuilder sb, VNParseResult res)
        {
            var found = new List<string>();
            for (var i = 0; i < res.AllNodes.Count; i++)
            {
                var node = res.AllNodes[i];
                if (!NeedsText(node.kind)) continue;
                var text = node.kind == VNNodeKind.Menu ? node.menuPrompt : node.text;
                if (!string.IsNullOrEmpty(text)) continue;
                found.Add("L" + Num(node.sourceLine) + " " + node.id);
            }
            Line(sb, "## 4. Nodes with empty text");
            Line(sb, string.Empty);
            if (found.Count == 0) Line(sb, "- none");
            else for (var i = 0; i < found.Count && i < 200; i++) Line(sb, "- " + found[i]);
            Line(sb, string.Empty);
        }

        static bool NeedsText(VNNodeKind kind)
        {
            return kind == VNNodeKind.Dialogue || kind == VNNodeKind.InnerVoice || kind == VNNodeKind.Remark ||
                   kind == VNNodeKind.Narration || kind == VNNodeKind.ScreenText || kind == VNNodeKind.Menu;
        }

        static void WriteUnresolved(StringBuilder sb, VNParseResult res)
        {
            var found = Collect(res, "MENU_UNRESOLVED");
            Line(sb, "## 5. Menu options without a branch");
            Line(sb, string.Empty);
            if (found.Count == 0) Line(sb, "- none: every option resolves to a branch");
            else for (var i = 0; i < found.Count; i++) Line(sb, found[i]);
            Line(sb, string.Empty);
        }

        static void WriteResume(StringBuilder sb, VNParseResult res)
        {
            var fallback = Collect(res, "RESUME_FALLBACK");
            var none = Collect(res, "RESUME_NONE");
            Line(sb, "## 6. Resume rule (branch after the last option)");
            Line(sb, string.Empty);
            if (fallback.Count == 0 && none.Count == 0)
                Line(sb, "- the rule worked for every menu: no fallbacks");
            for (var i = 0; i < fallback.Count; i++) Line(sb, "- fallback (L" + Num(fallback[i].Line) + ") " + fallback[i].Text);
            for (var i = 0; i < none.Count; i++) Line(sb, "- no resume at all (L" + Num(none[i].Line) + ") " + none[i].Text);
            Line(sb, string.Empty);
        }

        struct Row
        {
            public int Line;
            public string Text;
        }

        static List<Row> Collect(VNParseResult res, string code)
        {
            var list = new List<Row>();
            for (var i = 0; i < res.Issues.Count; i++)
            {
                var issue = res.Issues[i];
                if (issue.Code != code) continue;
                list.Add(new Row { Line = issue.Line, Text = (issue.NodeId ?? string.Empty) + " " + issue.Message });
            }
            return list;
        }

        static void WriteIssues(StringBuilder sb, string title, VNParseResult res, HashSet<string> skip)
        {
            Line(sb, title);
            Line(sb, string.Empty);
            var shown = 0;
            for (var i = 0; i < res.Issues.Count; i++)
            {
                var issue = res.Issues[i];
                if (skip.Contains(issue.Code)) continue;
                if (issue.Code == "IMPLICIT_BRANCH" || issue.Code == "COND_EMPTY_INSERTION") continue;
                Line(sb, "- [" + issue.Code + "] L" + Num(issue.Line) + " " + (issue.NodeId ?? string.Empty) + " " + issue.Message);
                shown++;
                if (shown >= 200) { Line(sb, "- ... truncated, see the full list in the parser log"); break; }
            }
            if (shown == 0) Line(sb, "- none");
            Line(sb, string.Empty);
        }

        static void WriteSpeakers(StringBuilder sb, VNImportRun run)
        {
            var res = run.Parse;
            var map = run.SpeakerSeed;
            Line(sb, "## 8. Speaker map coverage");
            Line(sb, string.Empty);
            Line(sb, "- labels found in the script: " + Num(res.SpeakerOrder.Count));
            if (map != null)
            {
                Line(sb, "- new rows seeded: " + Num(map.NewRows) + ", existing rows refreshed: " + Num(map.RefreshedRows));
                if (map.NewLabels.Count > 0)
                {
                    Line(sb, "- seeded:");
                    for (var i = 0; i < map.NewLabels.Count; i++) Line(sb, "  - " + map.NewLabels[i]);
                }
                if (map.OrphanLabels.Count > 0)
                {
                    Line(sb, "- rows the script no longer uses (kept, hand authored):");
                    for (var i = 0; i < map.OrphanLabels.Count; i++) Line(sb, "  - " + map.OrphanLabels[i]);
                }
            }
            Line(sb, "- histogram (label, lines):");
            var sorted = new List<string>(res.SpeakerOrder);
            sorted.Sort(delegate(string a, string b)
            {
                var byCount = res.SpeakerCounts[b].CompareTo(res.SpeakerCounts[a]);
                return byCount != 0 ? byCount : string.CompareOrdinal(a, b);
            });
            for (var i = 0; i < sorted.Count; i++)
                Line(sb, "  - " + sorted[i] + " = " + Num(res.SpeakerCounts[sorted[i]]));
            Line(sb, string.Empty);
            Line(sb, "> Missing rows are impossible by construction (the seeder creates one per label); a label");
            Line(sb, "> is only \"missing\" when the map file was edited away between the seed and the write.");
            Line(sb, string.Empty);
        }

        static void WriteConditionals(StringBuilder sb, VNImportRun run)
        {
            var res = run.Parse;
            var apply = run.ConditionApply;
            Line(sb, "## 9. Conditional bindings");
            Line(sb, string.Empty);
            Line(sb, "- markers: " + Num(res.AllNodes.Count == 0 ? 0 : CountKind(res, VNNodeKind.Conditional)));
            if (apply != null)
            {
                Line(sb, "- bound by the bindings file: " + Num(apply.Bound));
                Line(sb, "- defaulted to \"true\": " + Num(apply.DefaultedTrue));
                if (apply.UnboundIds.Count > 0)
                {
                    Line(sb, "- without a binding:");
                    for (var i = 0; i < apply.UnboundIds.Count; i++) Line(sb, "  - " + apply.UnboundIds[i]);
                }
            }
            var empty = Collect(res, "COND_EMPTY_INSERTION");
            if (empty.Count > 0)
            {
                Line(sb, "- markers whose insertion block is empty (nothing to gate):");
                for (var i = 0; i < empty.Count; i++) Line(sb, "  - L" + Num(empty[i].Line) + " " + empty[i].Text);
            }
            Line(sb, string.Empty);
        }

        static int CountKind(VNParseResult res, VNNodeKind kind)
        {
            var n = 0;
            for (var i = 0; i < res.AllNodes.Count; i++)
                if (res.AllNodes[i].kind == kind) n++;
            return n;
        }

        static void WriteTools(StringBuilder sb, VNImportRun run)
        {
            Line(sb, "## 10. Asset tools");
            Line(sb, string.Empty);
            if (run.OverlaySeed != null)
            {
                Line(sb, "- overlay rows: " + Num(run.OverlaySeed.NewSceneRows) + " scene rows added, " +
                          Num(run.OverlaySeed.TouchedSceneRows) + " filled in, " +
                          Num(run.OverlaySeed.NewMiniGameRows) + " mini-game rows added");
                for (var i = 0; i < run.OverlaySeed.MiniGameIds.Count; i++)
                    Line(sb, "  - " + run.OverlaySeed.MiniGameIds[i]);
            }
            for (var i = 0; i < run.ArtNotes.Count; i++) Line(sb, "- art: " + run.ArtNotes[i]);
            for (var i = 0; i < run.FontNotes.Count; i++) Line(sb, "- fonts: " + run.FontNotes[i]);
            Line(sb, string.Empty);
        }

        static void WriteTodos(StringBuilder sb, VNImportRun run)
        {
            Line(sb, "## 11. TODO(contract) and assumptions");
            Line(sb, string.Empty);
            if (run.ContractTodos.Count == 0) Line(sb, "- none");
            for (var i = 0; i < run.ContractTodos.Count; i++) Line(sb, "- " + run.ContractTodos[i]);
            Line(sb, string.Empty);
        }

        static void Line(StringBuilder sb, string text)
        {
            sb.Append(text).Append("\r\n");
        }

        static string Num(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
