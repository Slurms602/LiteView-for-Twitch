// A small, strict JSON reader for the answers of Twitch and the emote services. It returns the same shapes
// upstream's JavaScriptSerializer did (objects = Dictionary<string, object>, arrays = object[], numbers as long
// or double, strings, bools, null), so the accessor helpers (V, S, N, B, A, Props) work unchanged. Depth is
// limited; any malformed input gives null. The caller limits the size (Bounded.MaxJsonBytes).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LiteViewCore
{
    public static class Json
    {
        public const int MaxDepth = 64;

        /// <summary>The parsed value, or null when s is null or not valid JSON.</summary>
        public static object? Parse(string? s)
        {
            if (s == null) return null;
            try
            {
                var p = new Reader(s);
                p.Ws();
                object? v = p.Value(0);
                p.Ws();
                return p.AtEnd ? v : null;
            }
            catch (FormatException) { return null; }
        }

        // ---- accessors (same behaviour as upstream's Tw.V/S/N/B/A/Props) ----
        public static object? V(object? o, string k) { return o is Dictionary<string, object?> d && d.TryGetValue(k, out var v) ? v : null; }
        public static string? S(object? o, string k) { var v = V(o, k); return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public static long N(object? o, string k)
        {
            var v = V(o, k);
            try { return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (FormatException) { return 0; } catch (InvalidCastException) { return 0; } catch (OverflowException) { return 0; }
        }
        public static bool B(object? o, string k) { return V(o, k) is bool b && b; }
        public static object?[] A(object? o, string k) { return V(o, k) as object?[] ?? Array.Empty<object?>(); }
        public static IEnumerable<KeyValuePair<string, object?>> Props(object? o) { return o as Dictionary<string, object?> ?? new Dictionary<string, object?>(); }

        sealed class Reader
        {
            readonly string s; int i;
            public Reader(string text) { s = text; }
            public bool AtEnd { get { return i >= s.Length; } }

            static FormatException Bad() { return new FormatException("bad JSON"); }

            public void Ws() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++; }

            public object? Value(int depth)
            {
                if (depth > MaxDepth || i >= s.Length) throw Bad();
                char c = s[i];
                if (c == '{') return Obj(depth);
                if (c == '[') return Arr(depth);
                if (c == '"') return Str();
                if (c == 't') { Word("true"); return true; }
                if (c == 'f') { Word("false"); return false; }
                if (c == 'n') { Word("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                throw Bad();
            }

            void Word(string w)
            {
                if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) throw Bad();
                i += w.Length;
            }

            Dictionary<string, object?> Obj(int depth)
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                i++; Ws();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws();
                    if (i >= s.Length || s[i] != '"') throw Bad();
                    string k = Str(); Ws();
                    if (i >= s.Length || s[i] != ':') throw Bad();
                    i++; Ws();
                    d[k] = Value(depth + 1); Ws();
                    if (i >= s.Length) throw Bad();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw Bad();
                }
            }

            object?[] Arr(int depth)
            {
                var a = new List<object?>();
                i++; Ws();
                if (i < s.Length && s[i] == ']') { i++; return a.ToArray(); }
                while (true)
                {
                    Ws(); a.Add(Value(depth + 1)); Ws();
                    if (i >= s.Length) throw Bad();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a.ToArray(); }
                    throw Bad();
                }
            }

            string Str()
            {
                i++;   // opening quote
                int start = i;
                while (i < s.Length && s[i] != '"' && s[i] != '\\' && s[i] >= ' ') i++;
                if (i < s.Length && s[i] == '"') { string fast = s.Substring(start, i - start); i++; return fast; }
                var sb = new StringBuilder(s, start, i - start, Math.Max(16, i - start + 16));
                while (true)
                {
                    if (i >= s.Length) throw Bad();
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c < ' ') throw Bad();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw Bad();
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw Bad();
                            if (!int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)) throw Bad();
                            sb.Append((char)cp); i += 4; break;   // surrogate pairs arrive as two escapes and combine by themselves
                        default: throw Bad();
                    }
                }
            }

            object Num()
            {
                int start = i;
                if (s[i] == '-') i++;
                if (i >= s.Length) throw Bad();
                if (s[i] == '0') i++;
                else if (s[i] >= '1' && s[i] <= '9') while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                else throw Bad();
                bool real = false;
                if (i < s.Length && s[i] == '.')
                {
                    real = true; i++;
                    if (i >= s.Length || s[i] < '0' || s[i] > '9') throw Bad();
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    real = true; i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    if (i >= s.Length || s[i] < '0' || s[i] > '9') throw Bad();
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                }
                string t = s.Substring(start, i - start);
                if (!real && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                throw Bad();
            }
        }
    }
}
