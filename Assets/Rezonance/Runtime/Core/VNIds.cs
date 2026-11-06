// REZONANCE VN ENGINE - id slugs and id helpers.
// The importer, the overlay tools and the runtime must all use these, so ids stay stable across re-imports.
using System;
using System.Collections.Generic;
using System.Text;

namespace Rezonance.VN.Core
{
    /// <summary>Transliterates Cyrillic headings into ASCII slugs for stable ids and asset names.</summary>
    public static class VNSlug
    {
        private static readonly Dictionary<char, string> Map = new Dictionary<char, string>
        {
            { 'а', "a" }, { 'б', "b" }, { 'в', "v" }, { 'г', "g" }, { 'д', "d" }, { 'е', "e" },
            { 'ё', "e" }, { 'ж', "zh" }, { 'з', "z" }, { 'и', "i" }, { 'й', "y" }, { 'к', "k" },
            { 'л', "l" }, { 'м', "m" }, { 'н', "n" }, { 'о', "o" }, { 'п', "p" }, { 'р', "r" },
            { 'с', "s" }, { 'т', "t" }, { 'у', "u" }, { 'ф', "f" }, { 'х', "kh" }, { 'ц', "ts" },
            { 'ч', "ch" }, { 'ш', "sh" }, { 'щ', "sch" }, { 'ъ', "" }, { 'ы', "y" }, { 'ь', "" },
            { 'э', "e" }, { 'ю', "yu" }, { 'я', "ya" }
        };

        /// <summary>
        /// Lowercase ASCII slug: transliterated, non letters collapsed to '-', trimmed to 48 chars.
        /// Returns "untitled" when nothing survives (pure punctuation headings do exist in the source).
        /// </summary>
        public static string Of(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "untitled";

            var sb = new StringBuilder(raw.Length * 2);
            foreach (var chRaw in raw.ToLowerInvariant())
            {
                var ch = chRaw;
                if (Map.TryGetValue(ch, out var rep))
                {
                    sb.Append(rep);
                }
                else if (ch >= 'a' && ch <= 'z')
                {
                    sb.Append(ch);
                }
                else if (ch >= '0' && ch <= '9')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('-');
                }
            }

            var s = sb.ToString();
            while (s.Contains("--")) s = s.Replace("--", "-");
            s = s.Trim('-');
            if (s.Length == 0) return "untitled";
            if (s.Length > 48) s = s.Substring(0, 48).Trim('-');
            if (char.IsDigit(s[0])) s = "n" + s;
            return s;
        }

        /// <summary>Slug with a guaranteed uniqueness suffix ("-2", "-3"...). Thread-unsafe by design: import is single threaded.</summary>
        public static string Unique(string slug, ISet<string> taken)
        {
            if (taken.Add(slug)) return slug;
            var i = 2;
            while (!taken.Add(slug + "-" + i)) i++;
            return slug + "-" + i;
        }
    }

    /// <summary>Builds and parses the dotted id scheme described in ARCHITECTURE.md section 2.</summary>
    public static class VNIdFactory
    {
        /// <summary>act1.belaya-komnata</summary>
        public static string Scene(string act, string sceneTitle) => act + "." + VNSlug.Of(sceneTitle);

        /// <summary>act1.belaya-komnata.chto-vy-mne-vveli</summary>
        public static string Branch(string sceneId, string branchTitle) => sceneId + "." + VNSlug.Of(branchTitle);

        /// <summary>Node ordinal is per branch, so editing a neighbour never shifts this id.</summary>
        public static string Node(string branchId, VNNodeKind kind, int ordinal)
        {
            return branchId + "." + Prefix(kind) + ordinal.ToString("D3");
        }

        public static string Prefix(VNNodeKind kind)
        {
            switch (kind)
            {
                case VNNodeKind.Dialogue: return "d";
                case VNNodeKind.InnerVoice: return "iv";
                case VNNodeKind.Remark: return "r";
                case VNNodeKind.Narration: return "n";
                case VNNodeKind.ScreenText: return "st";
                case VNNodeKind.Menu: return "m";
                case VNNodeKind.Conditional: return "c";
                case VNNodeKind.MiniGame: return "mg";
                case VNNodeKind.GlossaryEntry: return "gl";
                case VNNodeKind.SceneTitle: return "sc";
                case VNNodeKind.BranchTitle: return "br";
                case VNNodeKind.Blank: return "b";
                default: return "x";
            }
        }
    }
}
