// REZONANCE VN EDITOR - conditional binder.
// Bindings live in Assets/Rezonance/Data/VNConditionBindings.json keyed by node id and are merged into the
// script data by the importer on every reimport: this file is the ONLY place where a condition expression
// is authored, the generated JSON is never edited by hand.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>One authored expression.</summary>
    [Serializable]
    public sealed class VNConditionBinding
    {
        public string nodeId;
        public string expression;
        public string note;
    }

    /// <summary>Serialized shape of the bindings file (JsonUtility cannot do dictionaries).</summary>
    [Serializable]
    public sealed class VNConditionBindingFile
    {
        public int formatVersion = 1;
        public string actId = "act1";
        public List<VNConditionBinding> bindings = new List<VNConditionBinding>();

        /// <summary>Finds a binding by node id; returns null when absent.</summary>
        public VNConditionBinding Find(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            for (var i = 0; i < bindings.Count; i++)
                if (bindings[i] != null && bindings[i].nodeId == nodeId) return bindings[i];
            return null;
        }

        /// <summary>Returns the existing binding for the id or appends a fresh one.</summary>
        public VNConditionBinding GetOrAdd(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            var found = Find(nodeId);
            if (found != null) return found;
            found = new VNConditionBinding { nodeId = nodeId };
            bindings.Add(found);
            return found;
        }
    }

    /// <summary>Outcome of one merge pass, reported by the importer.</summary>
    public sealed class VNConditionApplyResult
    {
        public int Markers;
        public int Bound;
        public int DefaultedTrue;
        public readonly List<string> UnboundIds = new List<string>();
    }

    /// <summary>Reads, applies and authors the condition bindings.</summary>
    public static class VNConditionBinder
    {
        const string BindToken = VNMarkdownParser.BindToken;

        // Source wording is matched through escapes so the heuristics survive a BOM-less re-save of the file.
        static readonly string[] DropWords =
        {
            "\u0435\u0441\u043B\u0438",                                      // "yesli"  = if
            "\u043A\u043E\u0433\u0434\u0430",                                 // "kogda"  = when
            "\u043F\u043E\u0441\u043B\u0435"                                  // "posle"  = after
        };

        static readonly string[] NegationWords =
        {
            "\u043D\u0435",                                                  // "ne"     = not
            "\u043D\u0435\u0442",                                            // "net"    = none
            "\u0431\u0435\u0437"                                             // "bez"    = without
        };

        static readonly string OrWord = "\u0438\u043B\u0438";                // "ili"    = or

        const char QuoteOpen = '\u00AB';
        const char QuoteClose = '\u00BB';

        // ---------------------------------------------------------------- persistence

        /// <summary>Loads the bindings file, creating an empty result when it does not exist yet.</summary>
        public static VNConditionBindingFile LoadOrCreate(string path, string actId)
        {
            var full = VNImportSettings.ToAbsolute(path);
            if (File.Exists(full))
            {
                try
                {
                    var loaded = JsonUtility.FromJson<VNConditionBindingFile>(File.ReadAllText(full, Encoding.UTF8));
                    if (loaded != null && loaded.bindings != null)
                    {
                        if (!string.IsNullOrEmpty(actId)) loaded.actId = actId;
                        return loaded;
                    }
                }
                catch (Exception e)
                {
                    VNLog.Error("binder", "Bindings file unreadable, starting from empty: " + e.Message);
                }
            }
            return new VNConditionBindingFile { actId = actId ?? "act1", bindings = new List<VNConditionBinding>() };
        }

        /// <summary>Writes the bindings file with a stable ordering (node id, ordinal).</summary>
        public static void Save(string path, VNConditionBindingFile file)
        {
            if (file == null) return;
            file.bindings.Sort(delegate(VNConditionBinding a, VNConditionBinding b)
            {
                return string.CompareOrdinal(a.nodeId ?? string.Empty, b.nodeId ?? string.Empty);
            });
            var full = VNImportSettings.ToAbsolute(path);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(full, JsonUtility.ToJson(file, true), new UTF8Encoding(false));
        }

        // ---------------------------------------------------------------- merge

        /// <summary>
        /// Replaces the BIND: tokens left by the parser with the authored expressions. Nodes of a conditional
        /// insertion inherit the expression of their marker. Unbound nodes get "true" when the settings allow
        /// it (so nothing is silently skipped) or an empty expression otherwise.
        /// </summary>
        public static VNConditionApplyResult ApplyBindings(VNScriptData data, VNConditionBindingFile file, bool unboundDefaultTrue)
        {
            var result = new VNConditionApplyResult();
            if (data == null || data.scenes == null) return result;

            for (var s = 0; s < data.scenes.Count; s++)
            {
                var scene = data.scenes[s];
                if (scene == null || scene.branches == null) continue;
                for (var b = 0; b < scene.branches.Count; b++)
                {
                    var branch = scene.branches[b];
                    if (branch == null || branch.nodes == null) continue;
                    for (var n = 0; n < branch.nodes.Count; n++)
                    {
                        var node = branch.nodes[n];
                        if (node == null || string.IsNullOrEmpty(node.conditionExpr)) continue;
                        if (node.kind == VNNodeKind.Conditional) result.Markers++;
                        // A value without the token was written by hand in the overlay style: it stays.
                        if (!node.conditionExpr.StartsWith(BindToken, StringComparison.Ordinal)) continue;

                        var markerId = node.conditionExpr.Substring(BindToken.Length);
                        var expression = string.Empty;
                        if (file != null)
                        {
                            var binding = file.Find(markerId);
                            if (binding != null) expression = (binding.expression ?? string.Empty).Trim();
                        }
                        if (expression.Length == 0)
                        {
                            if (unboundDefaultTrue)
                            {
                                expression = "true";
                                result.DefaultedTrue++;
                            }
                            if (node.kind == VNNodeKind.Conditional) result.UnboundIds.Add(node.id);
                        }
                        else if (node.kind == VNNodeKind.Conditional)
                        {
                            result.Bound++;
                        }
                        node.conditionExpr = expression;
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- suggestion

        /// <summary>
        /// Deterministic first draft from the source wording: quoted phrases become chosen("slug") terms,
        /// anything else becomes flag("slug"), " or " splits into a disjunction.
        /// TODO(contract): ConditionEvaluator owns the real grammar and the FlagLedger naming; the draft is a
        /// stable starting point the author edits in the binder window, not a finished expression.
        /// </summary>
        public static string GenerateExpression(string conditionRaw)
        {
            if (string.IsNullOrEmpty(conditionRaw)) return string.Empty;
            var text = conditionRaw.Trim();
            while (text.Length > 0 && (text[text.Length - 1] == ':' || text[text.Length - 1] == ';'))
                text = text.Substring(0, text.Length - 1).Trim();

            var quoted = ExtractQuoted(text);
            if (quoted.Count > 0)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < quoted.Count; i++)
                {
                    if (i > 0) sb.Append(" || ");
                    sb.Append("chosen(\"").Append(VNSlug.Of(quoted[i])).Append("\")");
                }
                return sb.ToString();
            }

            var normalized = VNMarkdownParser.NormalizePhrase(text);
            for (var i = 0; i < DropWords.Length; i++)
            {
                var drop = VNMarkdownParser.NormalizePhrase(DropWords[i]);
                if (drop.Length > 0 && normalized.StartsWith(drop, StringComparison.Ordinal))
                {
                    normalized = normalized.Substring(drop.Length).Trim();
                    break;
                }
            }

            var alternatives = SplitWords(normalized, OrWord);
            if (alternatives.Count > 1)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < alternatives.Count; i++)
                {
                    if (i > 0) sb.Append(" || ");
                    sb.Append(FlagTerm(alternatives[i]));
                }
                return sb.ToString();
            }
            return FlagTerm(normalized);
        }

        static string FlagTerm(string phrase)
        {
            var trimmed = phrase == null ? string.Empty : phrase.Trim();
            if (trimmed.Length == 0) return "false";
            var negated = false;
            for (var i = 0; i < NegationWords.Length; i++)
            {
                if (trimmed.IndexOf(NegationWords[i], StringComparison.Ordinal) < 0) continue;
                negated = true;
                break;
            }
            var slug = VNSlug.Of(trimmed);
            return negated ? "!flag(\"" + slug + "\")" : "flag(\"" + slug + "\")";
        }

        static List<string> ExtractQuoted(string text)
        {
            var list = new List<string>();
            var start = -1;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == QuoteOpen) { start = i; continue; }
                if (text[i] != QuoteClose || start < 0) continue;
                var inner = text.Substring(start + 1, i - start - 1).Trim();
                if (inner.Length > 0) list.Add(inner);
                start = -1;
            }
            return list;
        }

        static List<string> SplitWords(string text, string separator)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            var parts = text.Split(' ');
            var sb = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == separator && sb.Length > 0)
                {
                    list.Add(sb.ToString().Trim());
                    sb.Length = 0;
                    continue;
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(parts[i]);
            }
            if (sb.Length > 0) list.Add(sb.ToString().Trim());
            return list;
        }
    }

    /// <summary>Editor window: every Conditional node on the left, its expression on the right.</summary>
    public class VNConditionBinderWindow : EditorWindow
    {
        VNImportSettings settings;
        VNConditionBindingFile bindings;
        VNScriptData data;
        readonly List<VNNode> markers = new List<VNNode>();
        readonly List<string> insertionIds = new List<string>();
        Vector2 scrollLeft;
        Vector2 scrollRight;
        int selected = -1;
        string search = string.Empty;
        string status = "Nothing imported yet.";

        [MenuItem("Rezonance/Condition Binder")]
        public static void Open()
        {
            var window = GetWindow<VNConditionBinderWindow>("Condition Binder");
            window.minSize = new Vector2(760, 420);
            window.Reload();
        }

        void OnEnable()
        {
            Reload();
        }

        /// <summary>Rereads settings, the generated script data and the bindings file.</summary>
        public void Reload()
        {
            settings = VNImportSettings.LoadOrCreate();
            data = LoadData(settings);
            bindings = VNConditionBinder.LoadOrCreate(settings.ConditionBindingsPath, settings.actId);
            markers.Clear();
            insertionIds.Clear();
            selected = -1;

            if (data != null && data.scenes != null)
            {
                for (var s = 0; s < data.scenes.Count; s++)
                {
                    var scene = data.scenes[s];
                    if (scene == null || scene.branches == null) continue;
                    for (var b = 0; b < scene.branches.Count; b++)
                    {
                        var branch = scene.branches[b];
                        if (branch == null || branch.nodes == null) continue;
                        for (var n = 0; n < branch.nodes.Count; n++)
                        {
                            var node = branch.nodes[n];
                            if (node == null) continue;
                            if (node.kind == VNNodeKind.Conditional) markers.Add(node);
                            else if (!string.IsNullOrEmpty(node.conditionRaw)) insertionIds.Add(node.id);
                        }
                    }
                }
            }

            status = markers.Count + " markers, " + insertionIds.Count + " inserted nodes, " +
                     (bindings != null ? bindings.bindings.Count : 0) + " bindings. Data: " +
                     (data == null ? "missing - run Rezonance/Import Act I" : settings.DataJsonPath);
        }

        static VNScriptData LoadData(VNImportSettings settings)
        {
            if (settings == null) return null;
            var full = VNImportSettings.ToAbsolute(settings.DataJsonPath);
            if (!File.Exists(full)) return null;
            try
            {
                return JsonUtility.FromJson<VNScriptData>(File.ReadAllText(full, Encoding.UTF8));
            }
            catch (Exception e)
            {
                VNLog.Error("binder", "Generated JSON unreadable: " + e.Message);
                return null;
            }
        }

        void OnGUI()
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.LabelField(status);

            EditorGUILayout.BeginHorizontal();
            DrawMarkerList();
            DrawEditor();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload")) Reload();
            if (GUILayout.Button("Generate for all empty")) GenerateAllEmpty();
            if (GUILayout.Button("Save bindings")) SaveBindings();
            if (GUILayout.Button("Reimport act")) VNMarkdownImporter.ImportAct1();
            EditorGUILayout.EndHorizontal();
        }

        void DrawMarkerList()
        {
            EditorGUILayout.BeginVertical(GUILayout.MinWidth(340), GUILayout.ExpandWidth(true));
            EditorGUILayout.LabelField("Conditional nodes", EditorStyles.boldLabel);
            search = EditorGUILayout.TextField("Filter", search);
            scrollLeft = EditorGUILayout.BeginScrollView(scrollLeft);
            for (var i = 0; i < markers.Count; i++)
            {
                var node = markers[i];
                if (!string.IsNullOrEmpty(search))
                {
                    var raw = node.conditionRaw ?? string.Empty;
                    if (raw.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                        node.id.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                var binding = bindings != null ? bindings.Find(node.id) : null;
                var bound = binding != null && !string.IsNullOrEmpty(binding.expression);
                var text = (bound ? "* " : "  ") + "L" + node.sourceLine.ToString(CultureInfo.InvariantCulture) +
                           "  " + Shorten(node.conditionRaw, 54) + "\n      " + node.id;
                if (GUILayout.Button(text, selected == i ? EditorStyles.miniButtonMid : EditorStyles.label))
                    selected = i;
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawEditor()
        {
            EditorGUILayout.BeginVertical(GUILayout.MinWidth(360), GUILayout.ExpandWidth(true));
            EditorGUILayout.LabelField("Expression", EditorStyles.boldLabel);
            scrollRight = EditorGUILayout.BeginScrollView(scrollRight);

            if (selected < 0 || selected >= markers.Count)
            {
                EditorGUILayout.HelpBox("Pick a marker on the left.", MessageType.Info);
            }
            else if (bindings == null)
            {
                EditorGUILayout.HelpBox("No data yet.", MessageType.Warning);
            }
            else
            {
                var node = markers[selected];
                var binding = bindings.GetOrAdd(node.id);
                if (binding != null)
                {
                    EditorGUILayout.LabelField("Node", node.id, EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.LabelField("Branch", new VNNodeId(node.id).BranchId, EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.LabelField("Source line", node.sourceLine.ToString(CultureInfo.InvariantCulture));
                    EditorGUILayout.LabelField("Condition text", node.conditionRaw, EditorStyles.wordWrappedLabel);

                    EditorGUILayout.BeginHorizontal();
                    binding.expression = EditorGUILayout.TextField("Expression", binding.expression);
                    if (GUILayout.Button("Generate", GUILayout.Width(80)))
                        binding.expression = VNConditionBinder.GenerateExpression(node.conditionRaw);
                    if (GUILayout.Button("Clear", GUILayout.Width(50)))
                        binding.expression = string.Empty;
                    EditorGUILayout.EndHorizontal();

                    binding.note = EditorGUILayout.TextField("Note", binding.note);
                    if (GUILayout.Button("Copy expression to the next marker with the same wording"))
                        CopyForwardFrom(selected);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>Copies the expression of the selected marker to the next marker sharing the same wording.</summary>
        void CopyForwardFrom(int index)
        {
            if (bindings == null || index < 0 || index >= markers.Count) return;
            var source = markers[index];
            var binding = bindings.GetOrAdd(source.id);
            if (binding == null || string.IsNullOrEmpty(binding.expression)) return;
            for (var i = index + 1; i < markers.Count; i++)
            {
                if (markers[i].conditionRaw != source.conditionRaw) continue;
                var next = bindings.GetOrAdd(markers[i].id);
                if (next == null) continue;
                next.expression = binding.expression;
                selected = i;
                return;
            }
        }

        void GenerateAllEmpty()
        {
            if (bindings == null) return;
            var made = 0;
            for (var i = 0; i < markers.Count; i++)
            {
                var node = markers[i];
                var binding = bindings.GetOrAdd(node.id);
                if (binding == null || !string.IsNullOrEmpty(binding.expression)) continue;
                binding.expression = VNConditionBinder.GenerateExpression(node.conditionRaw);
                made++;
            }
            status = "Drafted " + made + " expressions. Save bindings, then reimport.";
        }

        void SaveBindings()
        {
            if (bindings == null || settings == null) return;
            VNConditionBinder.Save(settings.ConditionBindingsPath, bindings);
            AssetDatabase.Refresh();
            status = "Bindings saved to " + settings.ConditionBindingsPath;
        }

        static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
