// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/prompt_helpers.py (Graphiti v0.31.2) and changed for .NET.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace GraphitiNet.Prompts;

/// <summary>Helpers shared by the prompts.</summary>
public static class PromptHelpers
{
    /// <summary>The note appended to every system message so providers keep non-ASCII characters as they are.</summary>
    public const string DoNotEscapeUnicode = "\nDo not escape unicode characters.\n";

    /// <summary>
    /// Serializes data to JSON for use in prompts, producing exactly the text of Python's
    /// <c>json.dumps(data, ensure_ascii=ensure_ascii, indent=indent)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without an indent the separators are <c>", "</c> and <c>": "</c>, so <c>{"a": 1, "b": [1, 2]}</c>. With an
    /// indent the item separator is <c>","</c> and each item goes on its own line; empty lists and dicts stay
    /// <c>[]</c> and <c>{}</c>. Dict keys keep their order. Floats use Python's <c>repr</c> (<c>3.0</c>,
    /// <c>1e-05</c>), NaN and infinities render as <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c>. Only
    /// <c>"</c>, <c>\</c> and control characters are escaped, unless <paramref name="ensureAscii"/> is set, which
    /// also escapes every character outside printable ASCII as <c>\uXXXX</c> (surrogate pairs as two escapes).
    /// </para>
    /// <para>
    /// Values map to Python values as described on <see cref="PythonText"/>. Python's <c>json.dumps</c> rejects
    /// datetimes; upstream passes <c>dt.isoformat()</c> strings instead, so a <see cref="DateTimeOffset"/> or
    /// <see cref="DateTime"/> here is written as the string <see cref="PythonText.IsoFormat(DateTimeOffset)"/>
    /// returns, which renders the same text. A <see cref="DateOnly"/> is written as <c>YYYY-MM-DD</c> and a
    /// <see cref="Guid"/> as its lowercase string. Non-string dict keys are converted the way Python converts them
    /// (<c>1</c> → <c>"1"</c>, <c>True</c> → <c>"true"</c>, <c>None</c> → <c>"null"</c>).
    /// </para>
    /// </remarks>
    /// <param name="data">The data to serialize.</param>
    /// <param name="ensureAscii">Escape non-ASCII characters. The default keeps them, which is easier to read in
    /// LLM logs and helps the model.</param>
    /// <param name="indent">Spaces per indentation level, or <see langword="null"/> for a single line.</param>
    /// <exception cref="NotSupportedException">The data contains a value with no JSON equivalent.</exception>
    public static string ToPromptJson(object? data, bool ensureAscii = false, int? indent = null)
    {
        var sb = new StringBuilder();
        new PythonJsonWriter(sb, ensureAscii, indent).Write(data, 0);
        return sb.ToString();
    }

    private sealed class PythonJsonWriter(StringBuilder sb, bool ensureAscii, int? indent)
    {
        public void Write(object? value, int level)
        {
            value = PythonText.Normalize(value);
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case string s:
                    WriteString(s);
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case BigInteger bi:
                    sb.Append(bi.ToString(CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(FloatJson(d));
                    return;
                case DateTimeOffset dto:
                    WriteString(PythonText.IsoFormat(dto));
                    return;
                case DateTime dt:
                    WriteString(PythonText.IsoFormat(dt));
                    return;
                case DateOnly or Guid:
                    WriteString(PythonText.Str(value));
                    return;
            }

            if (PythonText.TryGetMapping(value, out var entries))
            {
                WriteContainer('{', '}', entries, level, (pair, l) =>
                {
                    WriteString(KeyString(pair.Key));
                    sb.Append(": ");
                    Write(pair.Value, l);
                });
                return;
            }

            if (PythonText.TryGetSequence(value, out var items))
            {
                WriteContainer('[', ']', items, level, Write);
                return;
            }

            throw new NotSupportedException($"Object of type {PythonText.TypeName(value)} is not JSON serializable");
        }

        private void WriteContainer<T>(char open, char close, IEnumerable<T> items, int level, Action<T, int> writeItem)
        {
            var first = true;
            sb.Append(open);
            foreach (var item in items)
            {
                if (!first)
                {
                    sb.Append(indent is null ? ", " : ",");
                }

                if (indent is { } i)
                {
                    sb.Append('\n').Append(' ', Math.Max(i, 0) * (level + 1));
                }

                first = false;
                writeItem(item, level + 1);
            }

            if (!first && indent is { } j)
            {
                sb.Append('\n').Append(' ', Math.Max(j, 0) * level);
            }

            sb.Append(close);
        }

        private static string KeyString(object? key)
        {
            key = PythonText.Normalize(key);
            return key switch
            {
                string s => s,
                null => "null",
                bool b => b ? "true" : "false",
                double d => FloatJson(d),
                long or BigInteger => PythonText.Str(key),
                _ => throw new NotSupportedException(
                    $"keys must be str, int, float, bool or None, not {PythonText.TypeName(key)}"),
            };
        }

        private static string FloatJson(double d) => d switch
        {
            double.NaN => "NaN",
            double.PositiveInfinity => "Infinity",
            double.NegativeInfinity => "-Infinity",
            _ => PythonText.FloatRepr(d),
        };

        private void WriteString(string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
                        if (c < ' ' || (ensureAscii && c > '~'))
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
}
