// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphitiNet.Llm;

/// <summary>
/// Writes JSON the way Python's <c>json.dumps</c> does with default settings: <c>", "</c> and <c>": "</c> separators
/// and non-ASCII characters escaped. Upstream puts <c>json.dumps</c> output into prompts and cache keys, so matching
/// it keeps prompts and keys identical.
/// </summary>
internal static class PythonJson
{
    public static string Dumps(JsonNode? node, bool sortKeys = false)
    {
        var sb = new StringBuilder();
        Write(sb, node, sortKeys);
        return sb.ToString();
    }

    public static string Dumps(JsonElement element, bool sortKeys = false) =>
        Dumps(JsonSerializer.SerializeToNode(element), sortKeys);

    private static void Write(StringBuilder sb, JsonNode? node, bool sortKeys)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
            {
                sb.Append('{');
                IEnumerable<KeyValuePair<string, JsonNode?>> props = sortKeys
                    ? obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                    : obj;
                var first = true;
                foreach (var (key, value) in props)
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    first = false;
                    WriteString(sb, key);
                    sb.Append(": ");
                    Write(sb, value, sortKeys);
                }

                sb.Append('}');
                break;
            }

            case JsonArray arr:
            {
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, arr[i], sortKeys);
                }

                sb.Append(']');
                break;
            }

            default:
            {
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(sb, node.GetValue<string>());
                        break;
                    case JsonValueKind.True:
                        sb.Append("true");
                        break;
                    case JsonValueKind.False:
                        sb.Append("false");
                        break;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        break;
                    default:
                        sb.Append(node.ToJsonString());
                        break;
                }

                break;
            }
        }
    }

    public static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || c >= 0x80)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
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
}
