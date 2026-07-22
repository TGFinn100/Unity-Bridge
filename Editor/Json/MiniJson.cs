using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityBridge.Editor
{
    // Minimal compact JSON reader/writer (LOCKED write contract: compact keys,
    // no pretty-printing — the consumer is a model, humans debug via jq).
    // Parse() exists for POST bodies (Phase 2's /query) and for reading the
    // jsonl index files back off disk — both need only the small JSON subset
    // Write() itself produces, not arbitrary JSON (no comments, no trailing
    // commas, no non-finite numbers).
    internal static class MiniJson
    {
        internal static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        internal static object Parse(string json)
        {
            int i = 0;
            object result = ParseValue(json, ref i);
            SkipWhitespace(json, ref i);
            if (i != json.Length)
                throw new FormatException($"unexpected trailing content at position {i}");
            return result;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>();
            i++; // consume '{'
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == '}') { i++; return dict; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != ':') throw new FormatException($"expected ':' at position {i}");
                i++;
                dict[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == '}') { i++; break; }
                throw new FormatException($"expected ',' or '}}' at position {i}");
            }
            return dict;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // consume '['
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == ']') { i++; return list; }

            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == ']') { i++; break; }
                throw new FormatException($"expected ',' or ']' at position {i}");
            }
            return list;
        }

        static string ParseString(string s, ref int i)
        {
            if (Peek(s, i) != '"') throw new FormatException($"expected '\"' at position {i}");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("unterminated escape");
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("truncated \\u escape");
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw new FormatException($"invalid escape '\\{esc}' at position {i}");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (Peek(s, i) == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            bool isFloat = false;
            if (Peek(s, i) == '.')
            {
                isFloat = true;
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (Peek(s, i) == 'e' || Peek(s, i) == 'E')
            {
                isFloat = true;
                i++;
                if (Peek(s, i) == '+' || Peek(s, i) == '-') i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i == start) throw new FormatException($"invalid number at position {i}");
            string token = s.Substring(start, i - start);
            if (isFloat) return double.Parse(token, CultureInfo.InvariantCulture);
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                if (l >= int.MinValue && l <= int.MaxValue) return (int)l;
                return l;
            }
            return double.Parse(token, CultureInfo.InvariantCulture);
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new FormatException($"expected '{literal}' at position {i}");
            i += literal.Length;
        }

        static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static void WriteValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case float f:
                    // JSON has no Infinity/NaN literal — a bare token would
                    // be invalid JSON that MiniJson.Parse itself couldn't
                    // even read back. Found live (v2): HingeJoint's default
                    // m_BreakForce is float.PositiveInfinity ("never
                    // breaks"). Quoted as a string when non-finite so the
                    // value stays informative rather than silently clamped.
                    if (float.IsNaN(f) || float.IsInfinity(f)) WriteString(sb, f.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(f.ToString(CultureInfo.InvariantCulture));
                    break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) WriteString(sb, d.ToString(CultureInfo.InvariantCulture));
                    else sb.Append(d.ToString(CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    WriteObject(sb, dict);
                    break;
                case IEnumerable<object> list:
                    WriteArray(sb, list);
                    break;
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        static void WriteObject(StringBuilder sb, IDictionary<string, object> dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable<object> list)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
