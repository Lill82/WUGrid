using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WUGrid.Core
{
    /// <summary>Just enough JSON for WUGrid's own files (audit log, events log, state snapshot): written field by field,
    /// never parsed back.</summary>
    internal static class Json
    {
        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
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
            return sb.Append('"').ToString();
        }

        /// <summary>Local time with its offset, so a reader elsewhere knows which moment it was.</summary>
        public static string Time(DateTime? t) => t.HasValue ? Quote(t.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)) : "null";

        public static string Array(IEnumerable<string> json) => "[" + string.Join(",", json) + "]";

        public static string Strings(IEnumerable<string> values) => Array(values.Select(Quote));
    }

    /// <summary>One JSON object, built field by field in the order added.</summary>
    internal sealed class JsonObject
    {
        readonly StringBuilder sb = new StringBuilder("{");

        public JsonObject Str(string name, string value) => Raw(name, Json.Quote(value));

        public JsonObject Bool(string name, bool? value) => Raw(name, value.HasValue ? (value.Value ? "true" : "false") : "null");

        public JsonObject Num(string name, long? value) => Raw(name, value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");

        public JsonObject Time(string name, DateTime? value) => Raw(name, Json.Time(value));

        public JsonObject Raw(string name, string json)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append(Json.Quote(name)).Append(':').Append(json);
            return this;
        }

        public override string ToString() => sb + "}";
    }
}
