// REZONANCE VN ENGINE - pure expression evaluator for VNNode.conditionExpr (ARCHITECTURE.md 9.5).
// Grammar: or := and ('||' and)* | and := cmp ('&&' cmp)* | cmp := unary [ '==' '!=' '>=' '<=' '>' '<' unary ]
//          unary := '!' unary | primary | primary := '(' or ')' | literal | func '(' name ')'
// Literals: true/false, numbers, quoted strings. Functions: seen("nodeId"), flag("name"), chosen("menuId").
// Reading is side-effect free: missing flags are false/zero, bad expressions are false plus one warning.
using System;
using System.Collections.Generic;
using System.Globalization;
using Rezonance.VN.Core;

namespace Rezonance.VN.Engine
{
    /// <summary>Token kinds of a condition expression.</summary>
    public enum VNTokenKind
    {
        End = 0,
        Number,
        Text,
        Identifier,
        LeftParen,
        RightParen,
        Not,
        And,
        Or,
        Equal,
        NotEqual,
        GreaterOrEqual,
        LessOrEqual,
        Greater,
        Less
    }

    /// <summary>One lexed token.</summary>
    public struct VNConditionToken
    {
        public VNTokenKind kind;
        public double number;
        public string text;
        public int position;
    }

    /// <summary>Runtime values the condition language can produce: nothing, bool, number, text.</summary>
    public struct VNConditionValue
    {
        public bool isBool;
        public bool boolValue;
        public double number;
        public string text;

        public static VNConditionValue Nothing() => default;

        public static VNConditionValue From(bool v) => new VNConditionValue { isBool = true, boolValue = v };

        public static VNConditionValue From(double v) => new VNConditionValue { number = v };

        public static VNConditionValue FromText(string v) => new VNConditionValue { text = v ?? string.Empty };

        /// <summary>Missing and zero are false; text is true when non-empty.</summary>
        public bool Truthy()
        {
            if (isBool) return boolValue;
            if (text != null) return text.Length > 0;
            return Math.Abs(number) > double.Epsilon;
        }

        /// <summary>Numeric view of the value; NaN when it has no numeric meaning.</summary>
        public double Number()
        {
            if (isBool) return boolValue ? 1 : 0;
            if (text != null)
            {
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return 0;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : double.NaN;
            }
            return number;
        }

        public string Text()
        {
            if (isBool) return boolValue ? "true" : "false";
            if (text != null) return text;
            return number.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Read-only view of everything the condition language can ask about. The evaluator never writes here.</summary>
    public interface IConditionSource
    {
        bool IsSeen(string nodeId);

        bool TryFlag(string name, out VNConditionValue value);

        /// <summary>Selected option index of a menu, -1 when it was never answered.</summary>
        int ChosenIndex(string menuId);

        /// <summary>True when the menu was answered at all.</summary>
        bool WasChosen(string menuId);
    }

    /// <summary>Recursive-descent condition evaluator with one warning per broken expression.</summary>
    public sealed class ConditionEvaluator
    {
        private const int CacheCap = 256;
        private const int WarningCap = 256;

        private readonly Dictionary<string, VNConditionToken[]> tokens = new Dictionary<string, VNConditionToken[]>(StringComparer.Ordinal);
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);
        private int warnedOnceLogged;
        private bool warningBudgetReported;

        /// <summary>Shared instance so token caches stay alive between nodes.</summary>
        public static readonly ConditionEvaluator Shared = new ConditionEvaluator();

        /// <summary>True when the last evaluation failed to parse or referenced an unknown name.</summary>
        public bool LastHadError { get; private set; }

        /// <summary>Evaluates and warns once per broken expression; empty expression means "no gate" = true.</summary>
        public bool Evaluate(string expression, IConditionSource source)
        {
            LastHadError = false;
            if (string.IsNullOrWhiteSpace(expression)) return true;
            if (source == null)
            {
                Warn(expression, "no condition source");
                return false;
            }

            var list = Lex(expression);
            if (list == null)
            {
                Warn(expression, "unlexable input");
                return false;
            }

            var p = new Cursor(list);
            try
            {
                var v = ParseOr(ref p, source, expression);
                if (p.index < list.Length && list[p.index].kind != VNTokenKind.End)
                {
                    Warn(expression, "unexpected '" + Name(list[p.index]) + "' at offset " + list[p.index].position);
                    return false;
                }
                return v.Truthy();
            }
            catch (Exception e)
            {
                Warn(expression, e.Message);
                return false;
            }
        }

        /// <summary>Drops cached expressions and the warning set; call after a re-import.</summary>
        public void ClearCache()
        {
            tokens.Clear();
            warned.Clear();
            warnedOnceLogged = 0;
            warningBudgetReported = false;
        }

        // ----------------------------------------------------------------- parsing

        private static VNConditionValue ParseOr(ref Cursor p, IConditionSource src, string expr)
        {
            var left = ParseAnd(ref p, src, expr);
            while (p.Match(VNTokenKind.Or))
            {
                var right = ParseAnd(ref p, src, expr);
                left = VNConditionValue.From(left.Truthy() || right.Truthy());
            }
            return left;
        }

        private static VNConditionValue ParseAnd(ref Cursor p, IConditionSource src, string expr)
        {
            var left = ParseComparison(ref p, src, expr);
            while (p.Match(VNTokenKind.And))
            {
                var right = ParseComparison(ref p, src, expr);
                left = VNConditionValue.From(left.Truthy() && right.Truthy());
            }
            return left;
        }

        private static VNConditionValue ParseComparison(ref Cursor p, IConditionSource src, string expr)
        {
            var left = ParseUnary(ref p, src, expr);
            var op = p.PeekKind;
            if (op != VNTokenKind.Equal && op != VNTokenKind.NotEqual && op != VNTokenKind.GreaterOrEqual &&
                op != VNTokenKind.LessOrEqual && op != VNTokenKind.Greater && op != VNTokenKind.Less)
                return left;

            p.Advance();
            var right = ParseUnary(ref p, src, expr);
            return VNConditionValue.From(Compare(left, right, op));
        }

        private static VNConditionValue ParseUnary(ref Cursor p, IConditionSource src, string expr)
        {
            if (p.Match(VNTokenKind.Not))
            {
                var v = ParseUnary(ref p, src, expr);
                return VNConditionValue.From(!v.Truthy());
            }
            return ParsePrimary(ref p, src, expr);
        }

        private static VNConditionValue ParsePrimary(ref Cursor p, IConditionSource src, string expr)
        {
            var token = p.Current;
            switch (token.kind)
            {
                case VNTokenKind.LeftParen:
                {
                    p.Advance();
                    var v = ParseOr(ref p, src, expr);
                    if (!p.Match(VNTokenKind.RightParen)) throw Fail(expr, "expected ')'", p);
                    return v;
                }
                case VNTokenKind.Number:
                    p.Advance();
                    return VNConditionValue.From(token.number);
                case VNTokenKind.Text:
                    p.Advance();
                    return VNConditionValue.FromText(token.text);
                case VNTokenKind.Identifier:
                {
                    p.Advance();
                    var name = token.text;
                    var lowered = name.ToLowerInvariant();
                    if (lowered == "true") return VNConditionValue.From(true);
                    if (lowered == "false") return VNConditionValue.From(false);

                    var argument = string.Empty;
                    if (p.PeekKind == VNTokenKind.LeftParen)
                    {
                        p.Advance();
                        argument = ReadArgument(ref p, expr);
                        if (!p.Match(VNTokenKind.RightParen)) throw Fail(expr, "expected ')' after " + name + "()", p);
                    }

                    switch (lowered)
                    {
                        case "seen":
                            return VNConditionValue.From(src.IsSeen(argument));
                        case "flag":
                            return src.TryFlag(argument, out var flag) ? flag : VNConditionValue.Nothing();
                        case "chosen":
                            var index = src.ChosenIndex(argument);
                            return index < 0
                                ? VNConditionValue.From(false)
                                : VNConditionValue.From((double)index);
                        default:
                            // Bare identifier: a flag name written without flag("..."), kept lenient on purpose.
                            if (string.IsNullOrEmpty(argument) && src.TryFlag(name, out var bare)) return bare;
                            throw Fail(expr, "unknown function or name '" + name + "'", p);
                    }
                }
                default:
                    throw Fail(expr, "unexpected '" + Name(token) + "'", p);
            }
        }

        private static string ReadArgument(ref Cursor p, string expr)
        {
            var token = p.Current;
            if (token.kind == VNTokenKind.Text || token.kind == VNTokenKind.Identifier)
            {
                p.Advance();
                return token.text ?? string.Empty;
            }
            if (token.kind == VNTokenKind.Number)
            {
                p.Advance();
                return token.number.ToString("R", CultureInfo.InvariantCulture);
            }
            throw Fail(expr, "expected a quoted name inside ()", p);
        }

        private static bool Compare(VNConditionValue left, VNConditionValue right, VNTokenKind op)
        {
            // Text equality wins when both sides carry text; otherwise compare as numbers.
            var ln = left.Number();
            var rn = right.Number();
            var bothNumeric = !double.IsNaN(ln) && !double.IsNaN(rn);
            if (!bothNumeric)
            {
                var c = string.CompareOrdinal(left.Text(), right.Text());
                switch (op)
                {
                    case VNTokenKind.Equal: return c == 0;
                    case VNTokenKind.NotEqual: return c != 0;
                    case VNTokenKind.GreaterOrEqual: return c >= 0;
                    case VNTokenKind.LessOrEqual: return c <= 0;
                    case VNTokenKind.Greater: return c > 0;
                    case VNTokenKind.Less: return c < 0;
                    default: return false;
                }
            }
            switch (op)
            {
                case VNTokenKind.Equal: return Math.Abs(ln - rn) < 1e-9;
                case VNTokenKind.NotEqual: return Math.Abs(ln - rn) >= 1e-9;
                case VNTokenKind.GreaterOrEqual: return ln >= rn - 1e-9;
                case VNTokenKind.LessOrEqual: return ln <= rn + 1e-9;
                case VNTokenKind.Greater: return ln > rn + 1e-9;
                case VNTokenKind.Less: return ln < rn - 1e-9;
                default: return false;
            }
        }

        // ----------------------------------------------------------------- lexing

        private VNConditionToken[] Lex(string expression)
        {
            if (tokens.TryGetValue(expression, out var cached)) return cached;

            var list = new List<VNConditionToken>(16);
            var i = 0;
            while (i < expression.Length)
            {
                var c = expression[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    i++;
                    continue;
                }

                if (c == '(') { list.Add(Token(VNTokenKind.LeftParen, i, 0, null)); i++; continue; }
                if (c == ')') { list.Add(Token(VNTokenKind.RightParen, i, 0, null)); i++; continue; }
                if (c == '&' && Next(expression, i) == '&') { list.Add(Token(VNTokenKind.And, i, 0, null)); i += 2; continue; }
                if (c == '|' && Next(expression, i) == '|') { list.Add(Token(VNTokenKind.Or, i, 0, null)); i += 2; continue; }
                if (c == '!')
                {
                    if (Next(expression, i) == '=') { list.Add(Token(VNTokenKind.NotEqual, i, 0, null)); i += 2; continue; }
                    list.Add(Token(VNTokenKind.Not, i, 0, null)); i++; continue;
                }
                if (c == '=' && Next(expression, i) == '=') { list.Add(Token(VNTokenKind.Equal, i, 0, null)); i += 2; continue; }
                if (c == '>' && Next(expression, i) == '=') { list.Add(Token(VNTokenKind.GreaterOrEqual, i, 0, null)); i += 2; continue; }
                if (c == '<' && Next(expression, i) == '=') { list.Add(Token(VNTokenKind.LessOrEqual, i, 0, null)); i += 2; continue; }
                if (c == '>') { list.Add(Token(VNTokenKind.Greater, i, 0, null)); i++; continue; }
                if (c == '<') { list.Add(Token(VNTokenKind.Less, i, 0, null)); i++; continue; }

                if (c == '"' || c == '\'')
                {
                    var end = i + 1;
                    var sb = new System.Text.StringBuilder(16);
                    while (end < expression.Length && expression[end] != c)
                    {
                        if (expression[end] == '\\' && end + 1 < expression.Length)
                        {
                            end++;
                            sb.AddCharUnescaped(expression[end]);
                        }
                        else sb.Append(expression[end]);
                        end++;
                    }
                    if (end >= expression.Length) return null; // unterminated quote
                    list.Add(Token(VNTokenKind.Text, i, 0, sb.ToString()));
                    i = end + 1;
                    continue;
                }

                if (char.IsDigit(c) || (c == '-' && char.IsDigit(Next(expression, i))))
                {
                    var end = i;
                    if (expression[end] == '-') end++;
                    while (end < expression.Length && (char.IsDigit(expression[end]) || expression[end] == '.')) end++;
                    var slice = expression.Substring(i, end - i);
                    if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null;
                    list.Add(Token(VNTokenKind.Number, i, num, slice));
                    i = end;
                    continue;
                }

                if (IsNameChar(c))
                {
                    var end = i;
                    while (end < expression.Length && IsNameChar(expression[end])) end++;
                    list.Add(Token(VNTokenKind.Identifier, i, 0, expression.Substring(i, end - i)));
                    i = end;
                    continue;
                }

                return null; // character outside the grammar
            }

            list.Add(Token(VNTokenKind.End, expression.Length, 0, null));
            var result = list.ToArray();
            if (tokens.Count >= CacheCap) tokens.Clear();
            tokens[expression] = result;
            return result;
        }

        private static VNConditionToken Token(VNTokenKind kind, int position, double number, string text)
        {
            return new VNConditionToken { kind = kind, position = position, number = number, text = text };
        }

        private static char Next(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';

        private static bool IsNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == ':';
        }

        private static string Name(VNConditionToken t) => t.text ?? t.kind.ToString();

        private static Exception Fail(string expr, string message, Cursor p)
        {
            var pos = p.index < p.list.Length ? p.list[p.index].position : expr.Length;
            return new FormatException(message + " (offset " + pos + ")");
        }

        private void Warn(string expression, string reason)
        {
            LastHadError = true;
            if (warned.Count >= WarningCap)
            {
                if (warningBudgetReported) return;
                warningBudgetReported = true;
                VNLog.Warn("Engine", "condition warnings reached the " + WarningCap + " limit; further expression errors are silent");
                return;
            }
            if (!warned.Add(expression)) return;
            warnedOnceLogged++;
            VNLog.Warn("Engine", "bad condition '" + expression + "': " + reason + " (treated as false)");
        }

        /// <summary>Cursor over a lexed token array; a struct so parsing allocates nothing.</summary>
        private struct Cursor
        {
            public readonly VNConditionToken[] list;
            public int index;

            public Cursor(VNConditionToken[] list)
            {
                this.list = list;
                index = 0;
            }

            public VNConditionToken Current => list[index];

            public VNTokenKind PeekKind => list[index].kind;

            public void Advance()
            {
                if (index < list.Length - 1) index++;
            }

            public bool Match(VNTokenKind kind)
            {
                if (list[index].kind != kind) return false;
                Advance();
                return true;
            }
        }
    }

    /// <summary>Escape-aware append used while lexing quoted strings.</summary>
    internal static class StringBuilderExtensions
    {
        public static void AddCharUnescaped(this System.Text.StringBuilder sb, char c)
        {
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                default: sb.Append(c); break;
            }
        }
    }
}
