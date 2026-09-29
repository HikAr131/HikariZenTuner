// HikariZenTuner - command forwarder for ZenStates-Core.
// Copyright (C) 2026 Hikari
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HikariZenTuner
{
    // Ordered JSON object; keeps insertion order so output is stable.
    public sealed class JObject
    {
        private readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();

        public JObject Set(string key, object value)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].Key, key, StringComparison.Ordinal))
                {
                    _items[i] = new KeyValuePair<string, object>(key, value);
                    return this;
                }
            }
            _items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        public bool TryGet(string key, out object value)
        {
            foreach (var item in _items)
            {
                if (string.Equals(item.Key, key, StringComparison.Ordinal))
                {
                    value = item.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        public object Get(string key)
        {
            object value;
            return TryGet(key, out value) ? value : null;
        }

        public IEnumerable<KeyValuePair<string, object>> Items => _items;

        public int Count => _items.Count;
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    public static class Json
    {
        public const int MaxInputLength = 65536;
        public const int MaxDepth = 32;

        public static object Parse(string text)
        {
            if (text == null) throw new JsonException("empty input");
            if (text.Length > MaxInputLength) throw new JsonException("input too long");
            var parser = new Parser(text);
            parser.SkipWhitespace();
            object value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new JsonException("trailing characters");
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int depth)
        {
            if (depth > MaxDepth) throw new JsonException("output too deep");
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is JObject)
            {
                sb.Append('{');
                bool first = true;
                foreach (var item in ((JObject)value).Items)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, item.Key);
                    sb.Append(':');
                    Write(sb, item.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            if (value is System.Collections.IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in (System.Collections.IEnumerable)value)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            if (value is double || value is float)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is int || value is long || value is uint || value is ulong || value is short || value is ushort || value is byte || value is sbyte)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        // Output is pure ASCII: every non-ASCII or control char is escaped, so the reader
        // never depends on the console code page.
        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append('\\').Append('"'); break;
                    case '\\': sb.Append('\\').Append('\\'); break;
                    case '\n': sb.Append('\\').Append('n'); break;
                    case '\r': sb.Append('\\').Append('r'); break;
                    case '\t': sb.Append('\\').Append('t'); break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                        {
                            sb.Append('\\').Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }

            public bool AtEnd => _i >= _s.Length;

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == (char)0x20 || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            public object ParseValue(int depth)
            {
                if (depth > MaxDepth) throw new JsonException("input too deep");
                if (AtEnd) throw new JsonException("unexpected end");
                char c = _s[_i];
                if (c == '{') return ParseObject(depth);
                if (c == '[') return ParseArray(depth);
                if (c == '"') return ParseString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                throw new JsonException("unexpected character at " + _i.ToString(CultureInfo.InvariantCulture));
            }

            private void Expect(string word)
            {
                if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw new JsonException("invalid literal at " + _i.ToString(CultureInfo.InvariantCulture));
                _i += word.Length;
            }

            private JObject ParseObject(int depth)
            {
                var obj = new JObject();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}') { _i++; return obj; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"') throw new JsonException("expected key");
                    string key = ParseString();
                    object existing;
                    if (obj.TryGet(key, out existing)) throw new JsonException("duplicate key");
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':') throw new JsonException("expected colon");
                    _i++;
                    SkipWhitespace();
                    obj.Set(key, ParseValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw new JsonException("unterminated object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return obj; }
                    throw new JsonException("expected comma or brace");
                }
            }

            private List<object> ParseArray(int depth)
            {
                var list = new List<object>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    SkipWhitespace();
                    list.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw new JsonException("unterminated array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return list; }
                    throw new JsonException("expected comma or bracket");
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                _i++;
                while (true)
                {
                    if (AtEnd) throw new JsonException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new JsonException("control character in string");
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new JsonException("unterminated escape");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw new JsonException("short unicode escape");
                            int code;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw new JsonException("bad unicode escape");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw new JsonException("bad escape");
                    }
                }
            }

            private object ParseNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                if (AtEnd || _s[_i] < '0' || _s[_i] > '9') throw new JsonException("bad number");
                if (_s[_i] == '0') _i++;
                else while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                bool isFloat = false;
                if (!AtEnd && _s[_i] == '.')
                {
                    isFloat = true;
                    _i++;
                    if (AtEnd || _s[_i] < '0' || _s[_i] > '9') throw new JsonException("bad number");
                    while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                if (!AtEnd && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    isFloat = true;
                    _i++;
                    if (!AtEnd && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    if (AtEnd || _s[_i] < '0' || _s[_i] > '9') throw new JsonException("bad number");
                    while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                }
                string token = _s.Substring(start, _i - start);
                if (!isFloat)
                {
                    long l;
                    if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                    throw new JsonException("integer out of range");
                }
                double d;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsInfinity(d)) return d;
                throw new JsonException("bad number");
            }
        }
    }
}
