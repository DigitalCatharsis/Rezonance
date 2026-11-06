// REZONANCE VN EDITOR - READABLE markdown parser.
// Pure C# on purpose: no UnityEngine/UnityEditor types here, so the parser can be covered by plain
// unit tests and reused by batch tools. Ids come from Rezonance.VN.Core only (VNSlug / VNIdFactory).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Rezonance.VN.Core;

namespace Rezonance.VN.Import
{
    /// <summary>Line classes produced by the tokenizer.</summary>
    public enum VNItemType
    {
        SceneHeading = 0,
        BranchHeading = 1,
        MenuPrompt = 2,
        MenuOption = 3,
        Conditional = 4,
        MiniGame = 5,
        Remark = 6,
        ScreenText = 7,
        Dialogue = 8,
        Paragraph = 9,
        Glossary = 10,
        DocTitle = 11
    }

    /// <summary>One logical source item (a line, or a joined paragraph) with its 1-based line number.</summary>
    public sealed class VNSourceItem
    {
        public VNItemType Type;
        public int Line;
        /// <summary>True when a blank line separated this item from the previous one (paragraph boundary).</summary>
        public bool GapBefore;
        /// <summary>True for everything below the "# Глоссарий" heading.</summary>
        public bool InGlossary;
        /// <summary>Speaker label for Dialogue items, term for Glossary items.</summary>
        public string Speaker;
        /// <summary>Parenthetical delivery brief, null when absent.</summary>
        public string Delivery;
        /// <summary>Main text with the markup wrappers removed.</summary>
        public string Text;
        /// <summary>Inline backtick fragments of this item (screen text). May be null.</summary>
        public List<string> Fragments;
    }

    /// <summary>One validation finding produced during parsing.</summary>
    public sealed class VNIssue
    {
        public string Code;
        public int Line;
        public string NodeId;
        public string Message;

        public VNIssue(string code, int line, string nodeId, string message)
        {
            Code = code;
            Line = line;
            NodeId = nodeId;
            Message = message;
        }
    }

    /// <summary>Parser output: the script data plus everything the report needs.</summary>
    public sealed class VNParseResult
    {
        public VNScriptData Data;
        public VNDataRegistry Registry;
        public readonly List<VNIssue> Issues = new List<VNIssue>();
        public readonly List<VNNode> AllNodes = new List<VNNode>();
        /// <summary>Speaker labels in first-appearance order (stable; dictionary order is not guaranteed).</summary>
        public readonly List<string> SpeakerOrder = new List<string>();
        public readonly Dictionary<string, int> SpeakerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        public int SourceLines;
        public int BacktickLines;
        public int MenuOptionLines;
        public int SceneIntroBranches;
        public int FuzzyMatchedOptions;

        /// <summary>
        /// Source line of every generated menu option, keyed by option id. VNMenuOption has no line field, so the
        /// report needs this side table to point at the exact "- «…»" line of an unresolved option.
        /// </summary>
        public readonly Dictionary<string, int> OptionLines = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Adds one line to the speaker histogram, keeping first-appearance order.</summary>
        public void CountSpeaker(string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            int n;
            if (!SpeakerCounts.TryGetValue(label, out n))
            {
                n = 0;
                SpeakerOrder.Add(label);
            }
            SpeakerCounts[label] = n + 1;
        }

        /// <summary>Records a validation finding.</summary>
        public void Issue(string code, int line, string nodeId, string message)
        {
            Issues.Add(new VNIssue(code, line, nodeId, message));
        }
    }

    /// <summary>Tokenizes and assembles the READABLE markdown into <see cref="VNScriptData"/>.</summary>
    public static class VNMarkdownParser
    {
        /// <summary>Placeholder written into conditionExpr by the parser: "resolve me from the bindings file".</summary>
        public const string BindToken = "BIND:";

        // Cyrillic source markers are written as escapes so the parser keeps working if an editor or a
        // VCS pipeline re-saves the file without a UTF-8 BOM.
        const string PlayerLabel = "\u0412\u044B";                                   // "Vy" - the player
        const string InnerVoiceDelivery = "\u0432\u043D\u0443\u0442\u0440\u0435\u043D\u043D\u0438\u0439 \u0433\u043E\u043B\u043E\u0441";
        const string MiniGameMarker = "[\u041C\u0438\u043D\u0438-\u0438\u0433\u0440\u0430]";
        const string MiniGameText = "\u041C\u0438\u043D\u0438-\u0438\u0433\u0440\u0430";
        const string GlossarySlug = "\u0433\u043B\u043E\u0441\u0441\u0430\u0440\u0438\u0439";
        const string IntroSlug = "intro";

        // ---------------------------------------------------------------- public entry

        /// <summary>Parses a whole act. actId feeds the id scheme; sourcePath is only stored.</summary>
        public static VNParseResult Parse(string text, string actId, string sourcePath)
        {
            var lines = SplitLines(text);
            var result = new VNParseResult { SourceLines = lines.Length };
            var items = Tokenize(lines, result);
            var data = Build(items, actId ?? "act1", sourcePath ?? string.Empty, result);
            ResolveMenus(data, result);
            result.Data = data;
            result.Registry = VNDataRegistry.Build(data);
            return result;
        }

        /// <summary>Splits text into lines, tolerating BOM, CRLF and lone CR.</summary>
        public static string[] SplitLines(string text)
        {
            if (text == null) return new string[0];
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return text.Split('\n');
        }

        // ---------------------------------------------------------------- tokenizer

        /// <summary>Turns raw lines into logical items and gathers the raw markup counters.</summary>
        public static List<VNSourceItem> Tokenize(string[] lines, VNParseResult stats)
        {
            var items = new List<VNSourceItem>();
            var inGlossary = false;
            var blankSinceLast = true;

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var s = raw.Trim();
                var lineNo = i + 1;

                if (s.Length == 0) { blankSinceLast = true; continue; }
                if (IsSeparator(s)) { blankSinceLast = false; continue; }

                var item = new VNSourceItem { Line = lineNo, GapBefore = blankSinceLast, InGlossary = inGlossary };
                blankSinceLast = false;
                if (raw.IndexOf('`') >= 0) stats.BacktickLines++;

                string term, definition, name, delivery, payload;

                if (s.StartsWith("#### "))
                {
                    item.Type = VNItemType.BranchHeading;
                    item.Text = s.Substring(5).Trim();
                    stats.Issue("NESTED_BRANCH", lineNo, null, "#### treated as a branch heading: " + item.Text);
                }
                else if (s.StartsWith("### "))
                {
                    item.Type = VNItemType.BranchHeading;
                    item.Text = s.Substring(4).Trim();
                }
                else if (s.StartsWith("## "))
                {
                    item.Type = VNItemType.SceneHeading;
                    item.Text = s.Substring(3).Trim();
                }
                else if (s.StartsWith("# "))
                {
                    item.Type = VNItemType.DocTitle;
                    item.Text = s.Substring(2).Trim();
                    if (VNSlug.Of(item.Text) == GlossarySlug)
                    {
                        inGlossary = true;
                        item.InGlossary = true;
                    }
                    else
                    {
                        stats.Issue("DOC_TITLE", lineNo, null, "Top level heading outside the glossary: " + item.Text);
                    }
                }
                else if (s.StartsWith("- "))
                {
                    item.Type = VNItemType.MenuOption;
                    item.Text = StripChevrons(s.Substring(2).Trim());
                    stats.MenuOptionLines++;
                }
                else if (IsMenuPrompt(s))
                {
                    item.Type = VNItemType.MenuPrompt;
                    item.Text = s.Substring(2, s.Length - 4).Trim();
                }
                else if (inGlossary && TryGlossary(s, out term, out definition))
                {
                    item.Type = VNItemType.Glossary;
                    item.Speaker = term;
                    item.Text = definition;
                }
                else if (s.StartsWith("*(") && s.EndsWith(")*") && s.Length > 3)
                {
                    item.Type = VNItemType.Conditional;
                    item.Text = s.Substring(2, s.Length - 4).Trim();
                }
                else if (s == MiniGameMarker)
                {
                    item.Type = VNItemType.MiniGame;
                    item.Text = MiniGameText;
                }
                else if (s.StartsWith("[") && s.EndsWith("]") && s.Length > 2)
                {
                    item.Type = VNItemType.Remark;
                    item.Fragments = ExtractBackticks(s);
                    item.Text = RemoveBacktickChars(s.Substring(1, s.Length - 2).Trim());
                }
                else if (IsWholeLineBacktick(s))
                {
                    item.Type = VNItemType.ScreenText;
                    item.Text = RemoveBacktickChars(s);
                }
                else if (TrySplitSpeaker(s, out name, out delivery, out payload))
                {
                    item.Speaker = name;
                    item.Delivery = delivery;
                    if (IsWholeLineBacktick(payload))
                    {
                        // "ЭКРАН: `...`" - a diegetic system line that carries a speaker label.
                        item.Type = VNItemType.ScreenText;
                        item.Text = RemoveBacktickChars(payload);
                    }
                    else
                    {
                        item.Type = VNItemType.Dialogue;
                        item.Fragments = ExtractBackticks(payload);
                        item.Text = RemoveBacktickChars(StripOuterChevrons(payload));
                    }
                }
                else
                {
                    item.Type = VNItemType.Paragraph;
                    var sb = new StringBuilder(s);
                    item.Fragments = ExtractBackticks(s);
                    // Wrapped paragraph: join continuation lines up to a blank line or a structural line.
                    while (i + 1 < lines.Length)
                    {
                        var nextRaw = lines[i + 1];
                        var next = nextRaw.Trim();
                        if (next.Length == 0 || IsStructuralStart(next)) break;
                        sb.Append(' ').Append(next);
                        if (nextRaw.IndexOf('`') >= 0) stats.BacktickLines++;
                        var more = ExtractBackticks(next);
                        if (more != null)
                        {
                            if (item.Fragments == null) item.Fragments = new List<string>();
                            item.Fragments.AddRange(more);
                        }
                        i++;
                    }
                    item.Text = RemoveBacktickChars(sb.ToString());
                }

                items.Add(item);
            }

            return items;
        }

        static bool IsSeparator(string s)
        {
            if (s.Length < 3) return false;
            for (var i = 0; i < s.Length; i++) if (s[i] != '-') return false;
            return true;
        }

        static bool IsMenuPrompt(string s)
        {
            if (s.Length <= 5 || !s.StartsWith("**") || !s.EndsWith(":**")) return false;
            return s.IndexOf('*', 2) == s.Length - 3;
        }

        static bool IsWholeLineBacktick(string s)
        {
            return s.Length > 2 && s[0] == '`' && s[s.Length - 1] == '`' && s.IndexOf('`', 1) == s.Length - 1;
        }

        static bool IsStructuralStart(string s)
        {
            if (s.Length == 0) return true;
            var c = s[0];
            if (c == '#' || c == '*' || c == '[' || c == '`') return true;
            if (s.StartsWith("- ")) return true;
            string name, delivery, payload;
            return TrySplitSpeaker(s, out name, out delivery, out payload);
        }

        static bool TryGlossary(string s, out string term, out string definition)
        {
            term = null;
            definition = null;
            if (!s.StartsWith("**")) return false;
            var close = s.IndexOf("**", 2, StringComparison.Ordinal);
            if (close <= 2) return false;
            var tail = s.Substring(close + 2).TrimStart();
            if (tail.Length < 2 || tail[0] != '\u2014') return false;
            term = StripChevrons(s.Substring(2, close - 2).Trim());
            definition = tail.Substring(1).Trim();
            return definition.Length > 0;
        }

        /// <summary>Reads "NAME: payload", "NAME (brief): payload" and the "Вы:" player forms.</summary>
        public static bool TrySplitSpeaker(string s, out string name, out string delivery, out string payload)
        {
            name = null;
            delivery = null;
            payload = null;
            if (string.IsNullOrEmpty(s)) return false;

            var colon = s.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 || colon > 60) return false;
            var head = s.Substring(0, colon).Trim();
            if (head.Length == 0) return false;

            var open = head.IndexOf('(');
            if (open >= 0)
            {
                if (!head.EndsWith(")")) return false;
                delivery = head.Substring(open + 1, head.Length - open - 2).Trim();
                head = head.Substring(0, open).Trim();
                if (delivery.Length == 0) delivery = null;
            }

            if (head == PlayerLabel)
            {
                name = PlayerLabel;
            }
            else
            {
                if (head.Length == 0 || !IsUpperLetter(head[0])) return false;
                for (var i = 0; i < head.Length; i++)
                {
                    var c = head[i];
                    if (c == ' ')
                    {
                        if (i == 0 || head[i - 1] == ' ' || i == head.Length - 1) return false;
                        continue;
                    }
                    if (!IsUpperLetter(c)) return false;
                }
                name = head;
            }

            payload = s.Substring(colon + 2).Trim();
            return payload.Length > 0;
        }

        static bool IsUpperLetter(char c)
        {
            return (c >= '\u0410' && c <= '\u042F') || c == '\u0401' || (c >= 'A' && c <= 'Z');
        }

        static bool IsCyrillicLetter(char c)
        {
            return (c >= '\u0430' && c <= '\u044F') || (c >= '\u0410' && c <= '\u042F') || c == '\u0451' || c == '\u0401';
        }

        /// <summary>All `fragment` occurrences of a line in order; null when there is none.</summary>
        public static List<string> ExtractBackticks(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('`') < 0) return null;
            var list = new List<string>();
            var start = -1;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '`') continue;
                if (start < 0) { start = i; continue; }
                if (i > start + 1) list.Add(s.Substring(start + 1, i - start - 1).Trim());
                start = -1;
            }
            return list.Count == 0 ? null : list;
        }

        static string RemoveBacktickChars(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('`') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++) if (s[i] != '`') sb.Append(s[i]);
            return sb.ToString().Trim();
        }

        /// <summary>Drops only the outer « » pair so inner quotations survive untouched.</summary>
        public static string StripOuterChevrons(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '\u00AB' && s[s.Length - 1] == '\u00BB')
                return s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        /// <summary>Drops « » wherever they appear (menu option labels).</summary>
        public static string StripChevrons(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\u00AB", string.Empty).Replace("\u00BB", string.Empty).Trim();
        }

        // ---------------------------------------------------------------- builder

        sealed class BranchContext
        {
            public VNBranchDef Branch;
            readonly Dictionary<int, int> ordinals = new Dictionary<int, int>();

            public string NextId(VNNodeKind kind)
            {
                var key = (int)kind;
                int n;
                if (!ordinals.TryGetValue(key, out n)) n = 0;
                n++;
                ordinals[key] = n;
                return VNIdFactory.Node(Branch.id, kind, n);
            }
        }

        sealed class MenuBuilder
        {
            public VNSourceItem Prompt;
            public readonly List<VNSourceItem> Options = new List<VNSourceItem>();
        }

        sealed class BuildState
        {
            public VNSceneDef Scene;
            public BranchContext Ctx;
            public MenuBuilder Menu;
            public VNNode ConditionalMarker;
            public bool ConditionalTookContent;
            public HashSet<string> TakenScenes = new HashSet<string>(StringComparer.Ordinal);
        }

        static VNScriptData Build(List<VNSourceItem> items, string actId, string sourcePath, VNParseResult res)
        {
            var data = new VNScriptData
            {
                formatVersion = VNDataFormat.Version,
                actId = actId,
                sourcePath = sourcePath.Replace('\\', '/'),
                sourceSha = string.Empty,
                scenes = new List<VNSceneDef>(),
                glossary = new List<VNGlossaryEntry>()
            };

            var st = new BuildState();

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];

                // Menu options belong to the open menu block and never flush it.
                if (item.Type == VNItemType.MenuOption && st.Menu != null)
                {
                    st.Menu.Options.Add(item);
                    continue;
                }

                FlushMenu(st, res);

                if (item.InGlossary && item.Type != VNItemType.Glossary)
                {
                    CloseConditional(st, res);
                    res.Issue("GLOSSARY_NOISE", item.Line, null, "Ignored line inside the glossary block: " + Shorten(item.Text));
                    continue;
                }

                switch (item.Type)
                {
                    case VNItemType.DocTitle:
                        st.Scene = null;
                        st.Ctx = null;
                        break;

                    case VNItemType.SceneHeading:
                        st.Scene = OpenScene(data, st, item, res);
                        st.Ctx = null;
                        break;

                    case VNItemType.BranchHeading:
                        if (st.Scene == null)
                        {
                            res.Issue("BRANCH_WITHOUT_SCENE", item.Line, null, "Branch heading before any scene heading: " + item.Text);
                            st.Scene = OpenScene(data, st, new VNSourceItem { Type = VNItemType.SceneHeading, Line = item.Line, Text = "Untitled scene" }, res);
                        }
                        st.Ctx = OpenBranch(st.Scene, item, res);
                        break;

                    case VNItemType.MenuPrompt:
                        EnsureContext(data, st, item, res);
                        st.Menu = new MenuBuilder { Prompt = item };
                        break;

                    case VNItemType.Conditional:
                        EnsureContext(data, st, item, res);
                        {
                            var node = NewNode(st.Ctx, VNNodeKind.Conditional, item.Line, res);
                            node.text = item.Text;
                            node.conditionRaw = item.Text;
                            node.conditionExpr = BindToken + node.id;
                            node.role = VNSpeakerRole.System;
                            st.ConditionalMarker = node;
                            st.ConditionalTookContent = false;
                        }
                        break;

                    case VNItemType.MiniGame:
                        EnsureContext(data, st, item, res);
                        {
                            var node = NewNode(st.Ctx, VNNodeKind.MiniGame, item.Line, res);
                            node.text = item.Text;
                            node.role = VNSpeakerRole.System;
                        }
                        break;

                    case VNItemType.Remark:
                        EnsureContext(data, st, item, res);
                        {
                            var node = NewNode(st.Ctx, VNNodeKind.Remark, item.Line, res);
                            node.text = item.Text;
                            EmitScreenFragments(st.Ctx, item, res);
                        }
                        break;

                    case VNItemType.ScreenText:
                        EnsureContext(data, st, item, res);
                        {
                            var node = NewNode(st.Ctx, VNNodeKind.ScreenText, item.Line, res);
                            node.text = item.Text;
                            node.speaker = item.Speaker;
                            if (!string.IsNullOrEmpty(item.Speaker))
                            {
                                node.role = VNSpeakerRole.System;
                                res.CountSpeaker(item.Speaker);
                            }
                        }
                        break;

                    case VNItemType.Dialogue:
                        EnsureContext(data, st, item, res);
                        {
                            var inner = item.Speaker == PlayerLabel && !string.IsNullOrEmpty(item.Delivery) &&
                                        NormalizePhrase(item.Delivery) == NormalizePhrase(InnerVoiceDelivery);
                            var node = NewNode(st.Ctx, inner ? VNNodeKind.InnerVoice : VNNodeKind.Dialogue, item.Line, res);
                            node.speaker = item.Speaker;
                            node.delivery = item.Delivery;
                            node.text = item.Text;
                            node.role = inner ? VNSpeakerRole.InnerVoice
                                              : (item.Speaker == PlayerLabel ? VNSpeakerRole.Player : VNSpeakerRole.Unknown);
                            res.CountSpeaker(item.Speaker);
                            EmitScreenFragments(st.Ctx, item, res);
                        }
                        break;

                    case VNItemType.Paragraph:
                        EnsureContext(data, st, item, res);
                        {
                            var node = NewNode(st.Ctx, VNNodeKind.Narration, item.Line, res);
                            node.text = item.Text;
                            EmitScreenFragments(st.Ctx, item, res);
                        }
                        break;

                    case VNItemType.Glossary:
                        data.glossary.Add(new VNGlossaryEntry
                        {
                            id = "gl." + VNSlug.Of(item.Speaker),
                            term = item.Speaker,
                            definition = item.Text
                        });
                        break;

                    case VNItemType.MenuOption:
                        res.Issue("STRAY_OPTION", item.Line, null, "List line outside a menu block: " + Shorten(item.Text));
                        break;
                }

                // ARCHITECTURE 9.5: the insertion is the first block after the marker plus every block that
                // is glued to it without an empty line. Any structural item ends it.
                if (st.ConditionalMarker != null && item.Type != VNItemType.Conditional)
                {
                    var structural = item.Type == VNItemType.SceneHeading || item.Type == VNItemType.BranchHeading ||
                                     item.Type == VNItemType.MenuPrompt || item.Type == VNItemType.DocTitle ||
                                     item.Type == VNItemType.Glossary;
                    if (structural)
                    {
                        CloseConditional(st, res);
                    }
                    else if (st.ConditionalTookContent && item.GapBefore)
                    {
                        CloseConditional(st, res);
                    }
                    else if (item.Type != VNItemType.MenuOption)
                    {
                        TagInsertion(st, item);
                        st.ConditionalTookContent = true;
                    }
                }
            }

            FlushMenu(st, res);
            CloseConditional(st, res);
            return data;
        }

        static void CloseConditional(BuildState st, VNParseResult res)
        {
            if (st.ConditionalMarker == null) return;
            if (!st.ConditionalTookContent)
                res.Issue("COND_EMPTY_INSERTION", st.ConditionalMarker.sourceLine, st.ConditionalMarker.id,
                    "Conditional marker with no content block after it: " + Shorten(st.ConditionalMarker.text));
            st.ConditionalMarker = null;
            st.ConditionalTookContent = false;
        }

        static void TagInsertion(BuildState st, VNSourceItem item)
        {
            if (st.Ctx == null || st.Ctx.Branch.nodes == null || st.ConditionalMarker == null) return;
            var nodes = st.Ctx.Branch.nodes;
            for (var i = nodes.Count - 1; i >= 0; i--)
            {
                var n = nodes[i];
                if (n.sourceLine != item.Line) continue;
                if (n.kind == VNNodeKind.Conditional) continue;
                n.conditionRaw = st.ConditionalMarker.conditionRaw;
                n.conditionExpr = st.ConditionalMarker.conditionExpr;
            }
        }

        static void EmitScreenFragments(BranchContext ctx, VNSourceItem item, VNParseResult res)
        {
            if (item.Fragments == null || item.Fragments.Count == 0) return;
            for (var i = 0; i < item.Fragments.Count; i++)
            {
                var node = NewNode(ctx, VNNodeKind.ScreenText, item.Line, res);
                node.text = item.Fragments[i];
                node.role = VNSpeakerRole.System;
            }
        }

        static VNNode NewNode(BranchContext ctx, VNNodeKind kind, int line, VNParseResult res)
        {
            var node = new VNNode { id = ctx.NextId(kind), kind = kind, sourceLine = line };
            ctx.Branch.nodes.Add(node);
            res.AllNodes.Add(node);
            return node;
        }

        static VNSceneDef OpenScene(VNScriptData data, BuildState st, VNSourceItem item, VNParseResult res)
        {
            var raw = VNSlug.Of(item.Text);
            var slug = VNSlug.Unique(raw, st.TakenScenes);
            if (slug != raw) res.Issue("DUP_SCENE_SLUG", item.Line, null, "Scene slug collision: " + slug);
            var scene = new VNSceneDef
            {
                id = data.actId + "." + slug,
                title = item.Text,
                sourceLine = item.Line,
                branches = new List<VNBranchDef>()
            };
            data.scenes.Add(scene);
            return scene;
        }

        static BranchContext OpenBranch(VNSceneDef scene, VNSourceItem item, VNParseResult res)
        {
            var raw = VNSlug.Of(item.Text);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < scene.branches.Count; i++) taken.Add(SlugPart(scene.branches[i].id, scene.id));
            var slug = VNSlug.Unique(raw, taken);
            if (slug != raw)
                res.Issue("DUP_BRANCH_SLUG", item.Line, null, "Branch slug collision in scene \"" + scene.title + "\": " + slug);
            var branch = new VNBranchDef
            {
                id = scene.id + "." + slug,
                sceneId = scene.id,
                title = item.Text,
                sourceLine = item.Line,
                nodes = new List<VNNode>()
            };
            scene.branches.Add(branch);
            return new BranchContext { Branch = branch };
        }

        static void EnsureContext(VNScriptData data, BuildState st, VNSourceItem item, VNParseResult res)
        {
            if (st.Ctx != null) return;
            if (st.Scene == null)
            {
                res.Issue("CONTENT_WITHOUT_SCENE", item.Line, null, "Content before the first scene heading");
                st.Scene = OpenScene(data, st, new VNSourceItem { Type = VNItemType.SceneHeading, Line = item.Line, Text = "Opening" }, res);
            }

            var slug = IntroSlug;
            var suffix = 2;
            while (ContainsBranchId(st.Scene, st.Scene.id + "." + slug))
            {
                slug = IntroSlug + "-" + suffix;
                suffix++;
            }
            var branch = new VNBranchDef
            {
                id = st.Scene.id + "." + slug,
                sceneId = st.Scene.id,
                title = "(scene intro) " + st.Scene.title,
                sourceLine = item.Line,
                nodes = new List<VNNode>()
            };
            st.Scene.branches.Add(branch);
            st.Ctx = new BranchContext { Branch = branch };
            res.SceneIntroBranches++;
            res.Issue("IMPLICIT_BRANCH", item.Line, branch.id, "Content before the first branch heading of scene \"" + st.Scene.title + "\"");
        }

        static bool ContainsBranchId(VNSceneDef scene, string branchId)
        {
            for (var i = 0; i < scene.branches.Count; i++)
                if (scene.branches[i].id == branchId) return true;
            return false;
        }

        static string SlugPart(string id, string prefix)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length + 1)
                return id.Substring(prefix.Length + 1);
            return id;
        }

        static void FlushMenu(BuildState st, VNParseResult res)
        {
            if (st.Menu == null) return;
            var item = st.Menu.Prompt;
            if (st.Ctx == null)
            {
                res.Issue("MENU_WITHOUT_BRANCH", item.Line, null, "Menu prompt outside any branch: " + item.Text);
                st.Menu = null;
                return;
            }

            var node = NewNode(st.Ctx, VNNodeKind.Menu, item.Line, res);
            node.menuPrompt = item.Text;
            node.text = item.Text;
            node.role = VNSpeakerRole.System;
            node.options = new List<VNMenuOption>();
            for (var i = 0; i < st.Menu.Options.Count; i++)
            {
                var optionItem = st.Menu.Options[i];
                var optionId = node.id + "-o" + (i + 1).ToString(CultureInfo.InvariantCulture);
                res.OptionLines[optionId] = optionItem.Line;
                node.options.Add(new VNMenuOption
                {
                    id = optionId,
                    label = optionItem.Text,
                    resolved = false,
                    matchMode = "pending"
                });
            }
            if (node.options.Count == 0)
                res.Issue("EMPTY_MENU", item.Line, node.id, "Menu prompt with no options: " + item.Text);
            st.Menu = null;
        }

        // ---------------------------------------------------------------- menu resolution

        static void ResolveMenus(VNScriptData data, VNParseResult res)
        {
            var registry = VNDataRegistry.Build(data);
            for (var s = 0; s < data.scenes.Count; s++)
            {
                var scene = data.scenes[s];
                if (scene.branches == null) continue;
                for (var b = 0; b < scene.branches.Count; b++)
                {
                    var branch = scene.branches[b];
                    if (branch.nodes == null) continue;
                    for (var n = 0; n < branch.nodes.Count; n++)
                    {
                        var node = branch.nodes[n];
                        if (node.kind != VNNodeKind.Menu || node.options == null) continue;

                        var maxIndex = -1;
                        for (var o = 0; o < node.options.Count; o++)
                        {
                            var option = node.options[o];
                            string mode;
                            var target = FindTarget(registry, scene, option.label, out mode);
                            option.matchMode = mode;
                            if (target == null)
                            {
                                option.resolved = false;
                                int optionLine;
                                if (!res.OptionLines.TryGetValue(option.id, out optionLine)) optionLine = node.sourceLine;
                                res.Issue("MENU_UNRESOLVED", optionLine, node.id,
                                          "Option " + (o + 1) + " \"" + option.label + "\" of the menu at line " +
                                          node.sourceLine + " has no branch in scene \"" + scene.title + "\"");
                                continue;
                            }
                            option.resolved = true;
                            option.targetBranchId = target.id;
                            if (mode.StartsWith("fuzzy", StringComparison.Ordinal)) res.FuzzyMatchedOptions++;
                            var idx = registry.BranchIndex(target.id);
                            if (idx > maxIndex) maxIndex = idx;
                        }

                        VNBranchDef resume = null;
                        if (maxIndex >= 0)
                        {
                            if (maxIndex + 1 < scene.branches.Count)
                            {
                                resume = scene.branches[maxIndex + 1];
                            }
                            else
                            {
                                resume = registry.FirstBranchOfNextScene(scene);
                                res.Issue("RESUME_FALLBACK", node.sourceLine, node.id,
                                          "Option group ends scene \"" + scene.title + "\"; resume = " +
                                          (resume == null ? "none (end of act)" : resume.id));
                            }
                        }
                        else
                        {
                            resume = registry.NextBranch(branch);
                            if (resume == null)
                            {
                                resume = registry.FirstBranchOfNextScene(scene);
                                res.Issue("RESUME_FALLBACK", node.sourceLine, node.id,
                                          "No option resolved in scene \"" + scene.title + "\"; resume = " +
                                          (resume == null ? "none (end of act)" : resume.id));
                            }
                        }
                        node.resumeBranchId = resume == null ? null : resume.id;
                        if (resume == null)
                            res.Issue("RESUME_NONE", node.sourceLine, node.id, "Menu at the very end of the act has no resume branch");
                    }
                }
            }
        }

        static VNBranchDef FindTarget(VNDataRegistry registry, VNSceneDef scene, string label, out string mode)
        {
            mode = "unresolved";
            if (string.IsNullOrEmpty(label)) return null;

            var exact = registry.FindBranchByTitle(scene, label);
            if (exact != null) { mode = "exact"; return exact; }

            var normalized = NormalizePhrase(label);
            if (normalized.Length == 0 || scene.branches == null) return null;

            for (var i = 0; i < scene.branches.Count; i++)
            {
                if (NormalizePhrase(scene.branches[i].title) == normalized)
                {
                    mode = "normalized";
                    return scene.branches[i];
                }
            }

            var best = 0f;
            VNBranchDef bestBranch = null;
            for (var i = 0; i < scene.branches.Count; i++)
            {
                var candidate = NormalizePhrase(scene.branches[i].title);
                if (candidate.Length == 0) continue;
                if (candidate.IndexOf(normalized, StringComparison.Ordinal) >= 0 ||
                    normalized.IndexOf(candidate, StringComparison.Ordinal) >= 0)
                {
                    mode = "contains";
                    return scene.branches[i];
                }
                var score = Similarity(normalized, candidate);
                if (score > best) { best = score; bestBranch = scene.branches[i]; }
            }

            if (bestBranch != null && best >= 0.72f)
            {
                mode = "fuzzy:" + best.ToString("0.00", CultureInfo.InvariantCulture);
                return bestBranch;
            }
            return null;
        }

        /// <summary>Lowercase letters+digits only, single spaces - the fuzzy comparison form.</summary>
        public static string NormalizePhrase(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var raw in s.ToLowerInvariant())
            {
                var c = raw;
                if (IsCyrillicLetter(c) || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == ' ' && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        static float Similarity(string a, string b)
        {
            if (a == b) return 1f;
            var max = Math.Max(a.Length, b.Length);
            if (max == 0) return 1f;
            return 1f - (float)Levenshtein(a, b) / max;
        }

        static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    var v = Math.Min(prev[j] + 1, cur[j - 1] + 1);
                    cur[j] = Math.Min(v, prev[j - 1] + cost);
                }
                var swap = prev;
                prev = cur;
                cur = swap;
            }
            return prev[b.Length];
        }

        static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= 60 ? s : s.Substring(0, 60) + "...";
        }
    }

    /// <summary>Shared naming helpers for the importer, the overlay seeder and the art tools.</summary>
    public static class VNImportNaming
    {
        /// <summary>Backdrop key of a scene: "bg_" + slug of the heading.</summary>
        public static string BackdropKey(string sceneTitle, string prefix)
        {
            return (string.IsNullOrEmpty(prefix) ? "bg_" : prefix) + VNSlug.Of(sceneTitle);
        }

        /// <summary>Portrait key of a character: "por_" + characterId + ("_" + emotion).</summary>
        public static string PortraitKey(string characterId, string emotionId, string prefix)
        {
            var key = (string.IsNullOrEmpty(prefix) ? "por_" : prefix) + VNSlug.Of(characterId);
            return string.IsNullOrEmpty(emotionId) ? key : key + "_" + VNSlug.Of(emotionId);
        }

        /// <summary>
        /// Mini game id derived from the node id: "mg_{branch-slug}_{ordinal}". Derived, not stored, so the
        /// node id scheme stays the single place that has to remain stable.
        /// </summary>
        public static string MiniGameId(string nodeId)
        {
            var parsed = new VNNodeId(nodeId);
            var local = parsed.Local;
            var digits = 0;
            while (digits < local.Length && local[digits] >= '0' && local[digits] <= '9') digits++;
            var ordinal = digits > 0 ? local.Substring(digits) : local;
            var branchSlug = parsed.Branch;
            var dot = branchSlug.LastIndexOf('.');
            if (dot >= 0) branchSlug = branchSlug.Substring(dot + 1);
            return "mg_" + branchSlug + "_" + ordinal;
        }

        /// <summary>Localization key of a speaker name plate.</summary>
        public static string NameKey(string label)
        {
            return "name." + VNSlug.Of(label ?? string.Empty);
        }
    }
}
