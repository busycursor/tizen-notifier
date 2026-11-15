// Minimal JSON reader/writer (no package dependencies on the TV).
// Objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double, plus string/bool/null.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TizenNotifier
{
    internal static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("trailing data at " + i);
            return v;
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("bad value at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; Ws(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string k = Str(s, ref i);
                Ws(s, ref i);
                if (s[i] != ':') throw new FormatException("expected : at " + i);
                i++;
                d[k] = Value(s, ref i);
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected , or } at " + i);
            }
        }

        private static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++; Ws(s, ref i);
            if (s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected , or ] at " + i);
            }
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string at " + i);
            i++;
            var b = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': b.Append('\n'); break;
                    case 't': b.Append('\t'); break;
                    case 'r': b.Append('\r'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'u': b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: b.Append(e); break;
                }
            }
        }

        public static string Write(object v)
        {
            var b = new StringBuilder();
            W(b, v);
            return b.ToString();
        }

        private static void W(StringBuilder b, object v)
        {
            switch (v)
            {
                case null: b.Append("null"); break;
                case bool x: b.Append(x ? "true" : "false"); break;
                case string x: Q(b, x); break;
                case double x: b.Append(x.ToString("R", CultureInfo.InvariantCulture)); break;
                case float x: b.Append(x.ToString("R", CultureInfo.InvariantCulture)); break;
                case int x: b.Append(x.ToString(CultureInfo.InvariantCulture)); break;
                case long x: b.Append(x.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> d:
                    b.Append('{'); bool f = true;
                    foreach (var kv in d) { if (!f) b.Append(','); f = false; Q(b, kv.Key); b.Append(':'); W(b, kv.Value); }
                    b.Append('}'); break;
                case System.Collections.IEnumerable l:
                    b.Append('['); bool g = true;
                    foreach (var x in l) { if (!g) b.Append(','); g = false; W(b, x); }
                    b.Append(']'); break;
                default: Q(b, v.ToString()); break;
            }
        }

        private static void Q(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            b.Append('"');
        }

        // ---- helpers for reading request objects ----
        public static Dictionary<string, object> O(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        public static List<object> A(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v as List<object> : null;
        public static string S(object o, string k, string def = null) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v != null ? (v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture)) : def;
        public static double N(object o, string k, double def) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v is double x ? x : def;
        public static bool B(object o, string k, bool def) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) && v is bool x ? x : def;
        public static bool Has(object o, string k) => o is Dictionary<string, object> d && d.ContainsKey(k);
        public static object Get(object o, string k) => o is Dictionary<string, object> d && d.TryGetValue(k, out var v) ? v : null;
    }
}
