// REZONANCE VN ENGINE - typed flag store for the runtime.
// Holds bool/int/float/string rows keyed by name, plus the ledger of taken decisions, and converts to
// the JsonUtility-friendly List<VNFlagEntry> the save system persists. No Unity object references here.
using System;
using System.Collections.Generic;
using System.Globalization;
using Rezonance.VN.Core;

namespace Rezonance.VN.Engine
{
    /// <summary>Which value a flag row carries.</summary>
    public enum VNFlagKind
    {
        /// <summary>No such row.</summary>
        Missing = 0,
        Bool = 1,
        Int = 2,
        Float = 3,
        Str = 4
    }

    /// <summary>One flag row. Small struct so the ledger stays cheap to scan during condition checks.</summary>
    public struct VNFlagValue
    {
        /// <summary>Row kind; Missing means the row does not exist.</summary>
        public VNFlagKind kind;
        public bool boolValue;
        public long intValue;
        public double floatValue;
        public string text;

        public static readonly VNFlagValue Missing = default;

        public static VNFlagValue From(bool v) => new VNFlagValue { kind = VNFlagKind.Bool, boolValue = v };

        public static VNFlagValue From(long v) => new VNFlagValue { kind = VNFlagKind.Int, intValue = v };

        public static VNFlagValue From(double v) => new VNFlagValue { kind = VNFlagKind.Float, floatValue = v };

        public static VNFlagValue FromText(string v) => new VNFlagValue { kind = VNFlagKind.Str, text = v };

        /// <summary>True, non-zero number or non-empty text; Missing is false.</summary>
        public bool Truthy
        {
            get
            {
                switch (kind)
                {
                    case VNFlagKind.Bool: return boolValue;
                    case VNFlagKind.Int: return intValue != 0;
                    case VNFlagKind.Float: return Math.Abs(floatValue) > double.Epsilon;
                    case VNFlagKind.Str: return !string.IsNullOrEmpty(text);
                    default: return false;
                }
            }
        }

        public bool AsBool()
        {
            switch (kind)
            {
                case VNFlagKind.Bool: return boolValue;
                case VNFlagKind.Int: return intValue != 0;
                case VNFlagKind.Float: return Math.Abs(floatValue) > double.Epsilon;
                case VNFlagKind.Str: return IsTrueWord(text);
                default: return false;
            }
        }

        public double AsNumber()
        {
            switch (kind)
            {
                case VNFlagKind.Bool: return boolValue ? 1 : 0;
                case VNFlagKind.Int: return intValue;
                case VNFlagKind.Float: return floatValue;
                case VNFlagKind.Str: return TryParseNumber(text, out var n) ? n : double.NaN;
                default: return 0;
            }
        }

        /// <summary>Canonical text form used by the save format.</summary>
        public string AsText()
        {
            switch (kind)
            {
                case VNFlagKind.Bool: return boolValue ? "true" : "false";
                case VNFlagKind.Int: return intValue.ToString(CultureInfo.InvariantCulture);
                case VNFlagKind.Float: return floatValue.ToString("R", CultureInfo.InvariantCulture);
                case VNFlagKind.Str: return text ?? string.Empty;
                default: return string.Empty;
            }
        }

        public static bool IsTrueWord(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "on", StringComparison.OrdinalIgnoreCase)) return true;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Math.Abs(n) > double.Epsilon;
        }

        public static bool TryParseNumber(string s, out double value)
        {
            if (string.IsNullOrEmpty(s))
            {
                value = 0;
                return false;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>Flag store plus the ordered record of player decisions.</summary>
    public sealed class FlagLedger
    {
        private const int ChoiceTrimThreshold = 4096;

        private readonly Dictionary<string, VNFlagValue> values = new Dictionary<string, VNFlagValue>(StringComparer.Ordinal);
        private readonly List<VNChoiceEntry> choices = new List<VNChoiceEntry>();

        /// <summary>Number of flag rows.</summary>
        public int Count => values.Count;

        /// <summary>Taken decisions in order, oldest first. Read-only view; use <see cref="RecordChoice"/>.</summary>
        public IReadOnlyList<VNChoiceEntry> Choices => choices;

        public bool Has(string name) => name != null && values.ContainsKey(name);

        public bool TryGet(string name, out VNFlagValue value)
        {
            if (name == null)
            {
                value = VNFlagValue.Missing;
                return false;
            }
            return values.TryGetValue(name, out value);
        }

        /// <summary>Raw row; <see cref="VNFlagValue.Missing"/> when absent.</summary>
        public VNFlagValue Value(string name)
        {
            TryGet(name, out var v);
            return v;
        }

        /// <summary>Truthiness of a row; false when missing.</summary>
        public bool Check(string name)
        {
            return TryGet(name, out var v) && v.Truthy;
        }

        public bool GetBool(string name) => TryGet(name, out var v) && v.AsBool();

        /// <summary>Numeric row as long; text rows are parsed, missing rows read as 0.</summary>
        public long GetInt(string name)
        {
            if (!TryGet(name, out var v)) return 0;
            if (v.kind == VNFlagKind.Int) return v.intValue;
            var d = v.AsNumber();
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
            return (long)Math.Round(d);
        }

        /// <summary>Numeric row as double; missing and unparsable rows read as 0.</summary>
        public double GetFloat(string name)
        {
            if (!TryGet(name, out var v)) return 0;
            var d = v.AsNumber();
            return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;
        }

        /// <summary>Row as text in its stored form; empty string when missing.</summary>
        public string GetString(string name) => TryGet(name, out var v) ? v.AsText() : string.Empty;

        public void Set(string name, bool value) { if (name != null) values[name] = VNFlagValue.From(value); }

        public void Set(string name, int value) { if (name != null) values[name] = VNFlagValue.From(value); }

        public void Set(string name, long value) { if (name != null) values[name] = VNFlagValue.From(value); }

        public void Set(string name, float value) { if (name != null) values[name] = VNFlagValue.From(value); }

        public void Set(string name, double value) { if (name != null) values[name] = VNFlagValue.From(value); }

        public void SetText(string name, string value) { if (name != null) values[name] = VNFlagValue.FromText(value); }

        /// <summary>Stores a value of unknown static type; anything unparsable becomes a text row.</summary>
        public void SetObject(string name, object value)
        {
            if (name == null) return;
            switch (value)
            {
                case null: values[name] = VNFlagValue.FromText(string.Empty); break;
                case bool b: values[name] = VNFlagValue.From(b); break;
                case string s: values[name] = VNFlagValue.FromText(s); break;
                case IConvertible c:
                    var kind = c.GetTypeCode();
                    if (kind == TypeCode.Byte || kind == TypeCode.SByte || kind == TypeCode.Int16 || kind == TypeCode.UInt16 ||
                        kind == TypeCode.Int32 || kind == TypeCode.UInt32 || kind == TypeCode.Int64)
                        values[name] = VNFlagValue.From(c.ToInt64(CultureInfo.InvariantCulture));
                    else if (kind == TypeCode.Single || kind == TypeCode.Double || kind == TypeCode.Decimal)
                        values[name] = VNFlagValue.From(c.ToDouble(CultureInfo.InvariantCulture));
                    else values[name] = VNFlagValue.FromText(c.ToString(CultureInfo.InvariantCulture));
                    break;
                default: values[name] = VNFlagValue.FromText(value.ToString()); break;
            }
        }

        /// <summary>Reads a value produced by <see cref="SetObject"/>; missing rows read as null.</summary>
        public object GetObject(string name)
        {
            if (!TryGet(name, out var v)) return null;
            switch (v.kind)
            {
                case VNFlagKind.Bool: return v.boolValue;
                case VNFlagKind.Int: return v.intValue;
                case VNFlagKind.Float: return v.floatValue;
                default: return v.AsText();
            }
        }

        /// <summary>Adds an integer delta; a float or text row keeps its float precision. Returns the new value.</summary>
        public long Inc(string name, long delta = 1)
        {
            if (name == null) return 0;
            TryGet(name, out var cur);
            var isFloat = cur.kind == VNFlagKind.Float;
            var next = cur.AsNumber() + delta;
            if (double.IsNaN(next) || double.IsInfinity(next)) next = delta;
            if (isFloat)
            {
                values[name] = VNFlagValue.From(next);
                return (long)Math.Round(next);
            }
            var l = (long)Math.Round(next);
            values[name] = VNFlagValue.From(l);
            return l;
        }

        /// <summary>Adds a float delta; a non numeric row is treated as zero. Returns the new value.</summary>
        public double Inc(string name, double delta)
        {
            if (name == null) return 0;
            TryGet(name, out var cur);
            var next = cur.AsNumber() + delta;
            if (double.IsNaN(next) || double.IsInfinity(next)) next = delta;
            values[name] = VNFlagValue.From(next);
            return next;
        }

        public void Remove(string name)
        {
            if (name != null) values.Remove(name);
        }

        public void Clear()
        {
            values.Clear();
            choices.Clear();
        }

        /// <summary>Appends one decision and mirrors it into flags: chosen.&lt;optionId&gt;=1 and chosen.&lt;menuId&gt;=index.</summary>
        public void RecordChoice(VNChoiceEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.menuId)) return;
            if (choices.Count >= ChoiceTrimThreshold)
                choices.RemoveRange(0, choices.Count / 4);
            choices.Add(entry);
            if (!string.IsNullOrEmpty(entry.optionId)) Set("chosen." + entry.optionId, 1L);
            Set("chosen." + entry.menuId, entry.optionIndex);
            Inc("choices.total", 1L);
        }

        /// <summary>Serializes rows alphabetically so diffs between saves stay readable.</summary>
        public List<VNFlagEntry> ToEntries()
        {
            var list = new List<KeyValuePair<string, VNFlagValue>>(values);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var result = new List<VNFlagEntry>(list.Count);
            foreach (var kv in list)
            {
                if (kv.Value.kind == VNFlagKind.Missing) continue;
                result.Add(new VNFlagEntry { name = kv.Key, value = kv.Value.AsText() });
            }
            return result;
        }

        /// <summary>Replaces all rows from the save format; the type is inferred from the text form.</summary>
        public void FromEntries(IList<VNFlagEntry> entries)
        {
            values.Clear();
            if (entries == null) return;
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null || string.IsNullOrEmpty(e.name)) continue;
                values[e.name] = ParseRaw(e.value);
            }
        }

        /// <summary>Replaces the decision ledger from the save format.</summary>
        public void FromChoices(IList<VNChoiceEntry> taken)
        {
            choices.Clear();
            if (taken == null) return;
            for (var i = 0; i < taken.Count; i++)
            {
                if (taken[i] != null) choices.Add(taken[i]);
            }
        }

        /// <summary>Copy of the ledger for a snapshot; rows are immutable so a shallow copy is enough.</summary>
        public FlagLedger Clone()
        {
            var copy = new FlagLedger();
            foreach (var kv in values) copy.values[kv.Key] = kv.Value;
            for (var i = 0; i < choices.Count; i++) copy.choices.Add(choices[i]);
            return copy;
        }

        private static VNFlagValue ParseRaw(string raw)
        {
            if (raw == null) return VNFlagValue.FromText(string.Empty);
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return VNFlagValue.From(true);
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return VNFlagValue.From(false);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return VNFlagValue.From(l);
            if (VNFlagValue.TryParseNumber(raw, out var d)) return VNFlagValue.From(d);
            return VNFlagValue.FromText(raw);
        }
    }
}
