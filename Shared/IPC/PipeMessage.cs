using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Shared.Enums;
using Windows.Foundation.Collections;

namespace Shared.IPC
{
    /// <summary>
    /// Message format for Named Pipe communication between widget and helper.
    /// Provides JSON serialization/deserialization compatible with ValueSet format.
    /// </summary>
    public class PipeMessage
    {
        /// <summary>
        /// Unique request ID for correlating requests with responses.
        /// Set by client, echoed by server. 0 means async push (no correlation needed).
        /// </summary>
        public int RequestId { get; set; }

        /// <summary>
        /// Command type (Get, Set, Response)
        /// </summary>
        public Command Command { get; set; }

        /// <summary>
        /// Function/property being accessed
        /// </summary>
        public Function Function { get; set; }

        /// <summary>
        /// Content/value (string representation)
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Additional key-value pairs
        /// </summary>
        public Dictionary<string, object> Extra { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Creates a PipeMessage from a ValueSet
        /// </summary>
        public static PipeMessage FromValueSet(ValueSet valueSet)
        {
            var msg = new PipeMessage();

            if (valueSet.TryGetValue(nameof(Command), out var cmdObj))
            {
                msg.Command = (Command)(int)cmdObj;
            }

            if (valueSet.TryGetValue(nameof(Function), out var funcObj))
            {
                msg.Function = (Function)(int)funcObj;
            }

            if (valueSet.TryGetValue(nameof(Content), out var contentObj))
            {
                msg.Content = contentObj?.ToString();
            }

            // Copy other properties
            foreach (var kvp in valueSet)
            {
                if (kvp.Key != nameof(Command) && kvp.Key != nameof(Function) && kvp.Key != nameof(Content))
                {
                    msg.Extra[kvp.Key] = kvp.Value;
                }
            }

            return msg;
        }

        /// <summary>
        /// Converts to a ValueSet for compatibility with existing code
        /// </summary>
        public ValueSet ToValueSet()
        {
            var valueSet = new ValueSet
            {
                { nameof(Command), (int)Command },
                { nameof(Function), (int)Function }
            };

            if (Content != null)
            {
                valueSet[nameof(Content)] = Content;
            }

            foreach (var kvp in Extra)
            {
                valueSet[kvp.Key] = kvp.Value;
            }

            return valueSet;
        }

        /// <summary>
        /// Serializes to JSON string
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"RequestId\":{RequestId}");
            sb.Append($",\"Command\":{(int)Command}");
            sb.Append($",\"Function\":{(int)Function}");

            if (Content != null)
            {
                sb.Append($",\"Content\":\"{EscapeJson(Content)}\"");
            }

            foreach (var kvp in Extra)
            {
                sb.Append($",\"{EscapeJson(kvp.Key)}\":");
                sb.Append(ValueToJson(kvp.Value));
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Deserializes from JSON string using a fast, robust linear tokenizer.
        /// Handles nested JSON structures, escaped quotes, and large payloads without regex limits.
        /// </summary>
        public static PipeMessage FromJson(string json)
        {
            var msg = new PipeMessage();
            if (string.IsNullOrWhiteSpace(json))
                return msg;

            try
            {
                int index = 0;
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '{')
                    return msg;

                index++; // Skip opening '{'

                while (index < json.Length)
                {
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length || json[index] == '}')
                        break;

                    if (json[index] == ',')
                    {
                        index++;
                        SkipWhitespace(json, ref index);
                    }

                    if (index >= json.Length || json[index] == '}')
                        break;

                    if (json[index] != '"')
                    {
                        index++;
                        continue;
                    }

                    string key = ReadString(json, ref index);
                    SkipWhitespace(json, ref index);

                    if (index >= json.Length || json[index] != ':')
                        break;

                    index++; // Skip ':'
                    SkipWhitespace(json, ref index);

                    object val = ReadValue(json, ref index);

                    if (string.Equals(key, nameof(RequestId), StringComparison.OrdinalIgnoreCase))
                    {
                        if (val is int i) msg.RequestId = i;
                        else if (val is long l) msg.RequestId = (int)l;
                        else if (val != null && int.TryParse(val.ToString(), out int parsed)) msg.RequestId = parsed;
                    }
                    else if (string.Equals(key, nameof(Command), StringComparison.OrdinalIgnoreCase))
                    {
                        if (val is int i) msg.Command = (Command)i;
                        else if (val is long l) msg.Command = (Command)(int)l;
                        else if (val != null && int.TryParse(val.ToString(), out int parsed)) msg.Command = (Command)parsed;
                    }
                    else if (string.Equals(key, nameof(Function), StringComparison.OrdinalIgnoreCase))
                    {
                        if (val is int i) msg.Function = (Function)i;
                        else if (val is long l) msg.Function = (Function)(int)l;
                        else if (val != null && int.TryParse(val.ToString(), out int parsed)) msg.Function = (Function)parsed;
                    }
                    else if (string.Equals(key, nameof(Content), StringComparison.OrdinalIgnoreCase))
                    {
                        if (val is bool b)
                            msg.Content = b ? "True" : "False";
                        else
                            msg.Content = val?.ToString();
                    }
                    else
                    {
                        msg.Extra[key] = val;
                    }
                }
            }
            catch
            {
                // Return empty message on parse error
            }

            return msg;
        }

        private static void SkipWhitespace(string s, ref int index)
        {
            while (index < s.Length && char.IsWhiteSpace(s[index]))
                index++;
        }

        private static string ReadString(string s, ref int index)
        {
            if (index >= s.Length || s[index] != '"')
                return string.Empty;

            index++; // Skip opening '"'
            var sb = new StringBuilder();
            while (index < s.Length)
            {
                char c = s[index++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c == '\\' && index < s.Length)
                {
                    char next = s[index++];
                    switch (next)
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
                            if (index + 4 <= s.Length)
                            {
                                string hex = s.Substring(index, 4);
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int codePoint))
                                {
                                    sb.Append((char)codePoint);
                                    index += 4;
                                }
                                else
                                {
                                    sb.Append("\\u").Append(hex);
                                    index += 4;
                                }
                            }
                            break;
                        default:
                            sb.Append(next);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static object ReadValue(string s, ref int index)
        {
            if (index >= s.Length) return null;

            char c = s[index];
            if (c == '"')
            {
                return ReadString(s, ref index);
            }
            if (c == '{' || c == '[')
            {
                return ReadRawJsonStructure(s, ref index, c, c == '{' ? '}' : ']');
            }
            if (c == 't' || c == 'T')
            {
                if (index + 4 <= s.Length && string.Equals(s.Substring(index, 4), "true", StringComparison.OrdinalIgnoreCase))
                {
                    index += 4;
                    return true;
                }
            }
            if (c == 'f' || c == 'F')
            {
                if (index + 5 <= s.Length && string.Equals(s.Substring(index, 5), "false", StringComparison.OrdinalIgnoreCase))
                {
                    index += 5;
                    return false;
                }
            }
            if (c == 'n' || c == 'N')
            {
                if (index + 4 <= s.Length && string.Equals(s.Substring(index, 4), "null", StringComparison.OrdinalIgnoreCase))
                {
                    index += 4;
                    return null;
                }
            }

            int start = index;
            bool hasDot = false;
            while (index < s.Length && s[index] != ',' && s[index] != '}' && s[index] != ']' && !char.IsWhiteSpace(s[index]))
            {
                if (s[index] == '.') hasDot = true;
                index++;
            }
            string token = s.Substring(start, index - start);

            if (hasDot && double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                return d;

            if (long.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long l))
            {
                if (l >= int.MinValue && l <= int.MaxValue)
                    return (int)l;
                return l;
            }

            return token;
        }

        private static string ReadRawJsonStructure(string s, ref int index, char openChar, char closeChar)
        {
            int start = index;
            int depth = 0;
            bool inString = false;
            while (index < s.Length)
            {
                char c = s[index++];
                if (inString)
                {
                    if (c == '\\' && index < s.Length)
                    {
                        index++; // Skip escaped char inside string
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == openChar)
                    {
                        depth++;
                    }
                    else if (c == closeChar)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return s.Substring(start, index - start);
                        }
                    }
                }
            }
            return s.Substring(start);
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            // Single-pass scan, not sequential whole-string Replace() calls. Replacing "\\"
            // with "\" before replacing "\t"/"\n"/"\r" lets a freshly-collapsed backslash pair
            // up with an unrelated adjacent letter from the ORIGINAL string and get misread as
            // an escape one step later — e.g. "C:\\temp" (escaped "C:\temp") collapses "\\" ->
            // "\" first, leaving "C:\temp", and the very next step matches that backslash + "t"
            // as "\t" and corrupts it into a real tab ("C:<TAB>emp"). Decode each escape once.
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        default: sb.Append(c); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string ValueToJson(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"\"{EscapeJson(s)}\"";
            if (value is bool b) return b ? "true" : "false";
            // Numbers are always written culture-invariant: value.ToString() gave "1,5" on
            // comma-decimal locales (de-DE, fr-FR, ...), which is malformed JSON. NaN and
            // Infinity have no JSON form, so they become null.
            if (value is int || value is long)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (value is double d)
                return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (value is float f)
                return float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return $"\"{EscapeJson(value.ToString())}\"";
        }

        public override string ToString()
        {
            return $"PipeMessage[{Command} {Function}: {Content?.Substring(0, Math.Min(50, Content?.Length ?? 0))}...]";
        }
    }
}
