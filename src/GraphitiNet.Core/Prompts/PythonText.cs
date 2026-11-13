// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraphitiNet.Prompts;

/// <summary>
/// Renders .NET values the way Python renders the equivalent Python values, so prompt text built from a context
/// matches upstream byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// Upstream prompts are f-strings. A field such as <c>{context['entity_types']}</c> renders with Python's
/// <c>str()</c>, which for a list or dict is its <c>repr()</c> (<c>[{'name': 'Alice', 'active': True}]</c>), not
/// JSON. <see cref="Str"/> and <see cref="Repr"/> reproduce that.
/// </para>
/// <para>
/// Values map to Python values like this: <see langword="null"/> is <c>None</c>; <see cref="bool"/> is
/// <c>bool</c>; integral types and <see cref="BigInteger"/> are <c>int</c>; <see cref="double"/>,
/// <see cref="float"/> and <see cref="decimal"/> are <c>float</c> (converted to <see cref="double"/>);
/// <see cref="string"/> and <see cref="char"/> are <c>str</c>; dictionaries (any <see cref="IDictionary"/> or
/// <see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey, TValue}"/>, in enumeration order) are
/// <c>dict</c>; other enumerables are <c>list</c>; <see cref="DateTimeOffset"/> is an aware
/// <c>datetime</c>; <see cref="DateTime"/> is an aware <c>datetime</c> when its kind is UTC or local and a naive
/// one when it is unspecified; <see cref="DateOnly"/> is <c>date</c>; <see cref="Guid"/> is <c>uuid.UUID</c>;
/// <see cref="JsonElement"/> and <see cref="JsonNode"/> are what <c>json.loads</c> would return for them (a
/// number with no fraction or exponent is an <c>int</c>, any other number a <c>float</c>). Other types are not
/// supported and throw <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// Datetimes: build contexts the way upstream does. Where upstream passes <c>dt.isoformat()</c> (episode
/// timestamps in <c>previous_episodes</c>, <c>extract_edges.extract_timestamps</c>'s <c>reference_time</c>), pass
/// the string <see cref="IsoFormat(DateTimeOffset)"/> returns. Where upstream passes the <c>datetime</c> itself
/// (<c>extract_edges.edge</c> and <c>extract_edges.extract_attributes</c>'s <c>reference_time</c>), pass the
/// <see cref="DateTimeOffset"/>: it renders as <c>str(datetime)</c>, <c>2025-01-03 09:30:15+00:00</c>.
/// </para>
/// <para>
/// Dictionary order matters: Python dicts keep insertion order. <see cref="Dictionary{TKey, TValue}"/> keeps it
/// as long as no entry is removed; use <see cref="OrderedDictionary{TKey, TValue}"/> when in doubt.
/// </para>
/// </remarks>
public static class PythonText
{
    /// <summary>Python's <c>str(value)</c>: what an f-string field <c>{value}</c> renders.</summary>
    public static string Str(object? value)
    {
        value = Normalize(value);
        return value switch
        {
            string s => s,
            DateTimeOffset dto => IsoFormat(dto, ' '),
            DateTime dt => IsoFormat(dt, ' '),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            _ => Repr(value),
        };
    }

    /// <summary>Python's <c>repr(value)</c>.</summary>
    public static string Repr(object? value)
    {
        var sb = new StringBuilder();
        AppendRepr(sb, value);
        return sb.ToString();
    }

    /// <summary>
    /// Python's <c>datetime.isoformat()</c> for an aware datetime: <c>2025-01-03T09:30:15.123456+00:00</c>, with the
    /// fraction only when the microseconds are not zero. Upstream builds many prompt contexts with
    /// <c>valid_at.isoformat()</c>; pass this where upstream does.
    /// </summary>
    /// <remarks>Python datetimes have microsecond resolution, so ticks below a microsecond are dropped.</remarks>
    public static string IsoFormat(DateTimeOffset value) => IsoFormat(value, 'T');

    /// <summary>
    /// Python's <c>datetime.isoformat()</c> for a <see cref="DateTime"/>: with a UTC offset when the kind is UTC or
    /// local, and without one (a naive datetime) when it is unspecified.
    /// </summary>
    public static string IsoFormat(DateTime value) => IsoFormat(value, 'T');

    /// <summary>
    /// Python's <c>repr(float)</c>, which <c>str()</c> and <c>json.dumps</c> also use: the shortest text that round-trips,
    /// in fixed notation with at least one decimal (<c>3.0</c>, <c>0.0001</c>) unless the exponent is below -4 or
    /// above 15 (<c>1e-05</c>, <c>1e+16</c>). Infinities and NaN render as <c>inf</c>, <c>-inf</c> and <c>nan</c>.
    /// </summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // .NET's "R" format is the shortest round-trip representation, like Python's repr; only the layout differs.
        var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..e];
        }

        var dot = r.IndexOf('.', StringComparison.Ordinal);
        var intLength = dot >= 0 ? dot : r.Length;
        var digits = dot >= 0 ? r.Remove(dot, 1) : r;

        // decimalPoint: value = 0.<digits> * 10^decimalPoint.
        var decimalPoint = intLength + exponent;
        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0')
        {
            leadingZeros++;
        }

        digits = digits[leadingZeros..];
        decimalPoint -= leadingZeros;
        digits = digits.TrimEnd('0');

        var sb = new StringBuilder();
        if (value < 0)
        {
            sb.Append('-');
        }

        if (decimalPoint is > -4 and <= 16)
        {
            if (decimalPoint <= 0)
            {
                sb.Append("0.").Append('0', -decimalPoint).Append(digits);
            }
            else if (decimalPoint >= digits.Length)
            {
                sb.Append(digits).Append('0', decimalPoint - digits.Length).Append(".0");
            }
            else
            {
                sb.Append(digits, 0, decimalPoint).Append('.').Append(digits, decimalPoint, digits.Length - decimalPoint);
            }
        }
        else
        {
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            }

            var exp = decimalPoint - 1;
            sb.Append('e').Append(exp < 0 ? '-' : '+')
                .Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Python truthiness: <c>None</c>, <c>False</c>, zero, and empty strings, lists and dicts are false; everything
    /// else is true. Upstream prompts use it for optional sections (<c>if context.get('edge_types'):</c>).
    /// </summary>
    public static bool IsTruthy(object? value)
    {
        value = Normalize(value);
        return value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            double d => d != 0,
            BigInteger i => !i.IsZero,
            long l => l != 0,
            _ when TryGetMapping(value, out var entries) => entries.Any(),
            IEnumerable items => items.Cast<object?>().Any(),
            _ => true,
        };
    }

    /// <summary>Python's <c>len(value)</c> for a string, list or dict.</summary>
    /// <exception cref="NotSupportedException">The value has no length.</exception>
    public static int Len(object? value)
    {
        value = Normalize(value);
        return value switch
        {
            null => throw new NotSupportedException("object of type 'NoneType' has no len()"),
            string s => CodePointLength(s),
            _ when TryGetMapping(value, out var entries) => entries.Count(),
            ICollection c => c.Count,
            IEnumerable items => items.Cast<object?>().Count(),
            _ => throw new NotSupportedException($"object of type '{TypeName(value)}' has no len()"),
        };
    }

    /// <summary>
    /// Reduces a value to the small set of types the formatters handle: JSON values become their .NET equivalents,
    /// numbers become <see cref="long"/>, <see cref="ulong"/>, <see cref="BigInteger"/> or <see cref="double"/>, and
    /// <see cref="char"/> becomes <see cref="string"/>. Containers are left as they are.
    /// </summary>
    internal static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or double or long or BigInteger:
                return value;
            case int i:
                return (long)i;
            case short s:
                return (long)s;
            case sbyte sb:
                return (long)sb;
            case byte b:
                return (long)b;
            case ushort us:
                return (long)us;
            case uint ui:
                return (long)ui;
            case ulong ul:
                return ul <= long.MaxValue ? (long)ul : new BigInteger(ul);
            case Int128 i128:
                return (BigInteger)i128;
            case UInt128 u128:
                return (BigInteger)u128;
            case float f:
                return (double)f;
            case decimal m:
                return (double)m;
            case Half h:
                return (double)h;
            case char c:
                return c.ToString();
            case JsonElement element:
                return NormalizeJson(element);
            case JsonValue jsonValue:
                return jsonValue.TryGetValue<JsonElement>(out var inner) ? NormalizeJson(inner) : Normalize(jsonValue.GetValue<object>());
            case JsonArray array:
                return array;
            case JsonObject obj:
                return obj;
            default:
                return value;
        }
    }

    private static object? NormalizeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => ParseJsonNumber(element.GetRawText()),
        _ => element,
    };

    private static object ParseJsonNumber(string raw)
    {
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
        {
            return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)
            ? l
            : BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>Enumerates a dict-like value's entries, in order.</summary>
    internal static bool TryGetMapping(object value, out IEnumerable<KeyValuePair<object?, object?>> entries)
    {
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                entries = element.EnumerateObject().Select(p => new KeyValuePair<object?, object?>(p.Name, p.Value));
                return true;
            case JsonObject obj:
                entries = obj.Select(p => new KeyValuePair<object?, object?>(p.Key, p.Value));
                return true;
            case IDictionary dictionary:
                entries = EnumerateDictionary(dictionary);
                return true;
            case string or JsonElement or JsonArray:
                entries = [];
                return false;
        }

        var pairType = value.GetType().GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>));
        if (pairType is null)
        {
            entries = [];
            return false;
        }

        var keyProperty = pairType.GetProperty(nameof(KeyValuePair<object, object>.Key))!;
        var valueProperty = pairType.GetProperty(nameof(KeyValuePair<object, object>.Value))!;
        entries = ((IEnumerable)value).Cast<object>()
            .Select(p => new KeyValuePair<object?, object?>(keyProperty.GetValue(p), valueProperty.GetValue(p)));
        return true;
    }

    // IDictionary's own enumerator yields DictionaryEntry; enumerating it as IEnumerable may yield KeyValuePair.
    private static IEnumerable<KeyValuePair<object?, object?>> EnumerateDictionary(IDictionary dictionary)
    {
        var enumerator = dictionary.GetEnumerator();
        while (enumerator.MoveNext())
        {
            yield return new KeyValuePair<object?, object?>(enumerator.Key, enumerator.Value);
        }
    }

    /// <summary>Enumerates a list-like value's items.</summary>
    internal static bool TryGetSequence(object value, out IEnumerable<object?> items)
    {
        switch (value)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                items = element.EnumerateArray().Select(e => (object?)e);
                return true;
            case string or JsonElement:
                items = [];
                return false;
            case IEnumerable enumerable:
                items = enumerable.Cast<object?>();
                return true;
            default:
                items = [];
                return false;
        }
    }

    internal static string TypeName(object? value) => value?.GetType().Name ?? "NoneType";

    /// <summary>
    /// The characters Python's <c>str.isspace()</c>, <c>str.split()</c>, <c>str.strip()</c> and the <c>\s</c> of a
    /// <c>str</c> regex treat as whitespace. .NET's <see cref="char.IsWhiteSpace(char)"/> and regex <c>\s</c> agree
    /// except for the information separators U+001C to U+001F, which only Python counts.
    /// </summary>
    internal static readonly char[] Whitespace =
    [
        '\t', '\n', '\v', '\f', '\r', '\u001c', '\u001d', '\u001e', '\u001f', ' ', '\u0085', '\u00a0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009',
        '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000',
    ];

    /// <summary>A regex class body matching Python's <c>\s</c> on <c>str</c>: <c>[\s\x1c-\x1f]</c>.</summary>
    internal const string WhitespaceClass = @"\s\x1c-\x1f";

    /// <summary>Python's <c>len(str)</c>: the number of code points (a surrogate pair counts once).</summary>
    internal static int CodePointLength(string text)
    {
        var count = text.Length;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
            {
                count--;
                i++;
            }
        }

        return count;
    }

    /// <summary>
    /// Python's <c>text[:stop]</c>: the first <paramref name="stop"/> code points, or for a negative
    /// <paramref name="stop"/> all but the last <c>-stop</c>.
    /// </summary>
    internal static string SliceTo(string text, int stop)
    {
        var length = CodePointLength(text);
        stop = stop < 0 ? Math.Max(0, length + stop) : Math.Min(stop, length);
        var index = 0;
        for (var count = 0; count < stop; count++)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
        }

        return text[..index];
    }

    /// <summary>
    /// Python's string order: by code point. <see cref="StringComparer.Ordinal"/> compares UTF-16 code units, which
    /// puts characters outside the Basic Multilingual Plane (emoji) before U+E000 to U+FFFF; Python puts them after.
    /// </summary>
    internal static StringComparer CodePointOrder { get; } = new CodePointComparer();

    private sealed class CodePointComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            var i = 0;
            var j = 0;
            while (i < x.Length && j < y.Length)
            {
                var a = Rune.GetRuneAt(x, i);
                var b = Rune.GetRuneAt(y, j);
                if (a != b)
                {
                    return a.Value.CompareTo(b.Value);
                }

                i += a.Utf16SequenceLength;
                j += b.Utf16SequenceLength;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }

        public override bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public override int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>Python's <c>str.isspace()</c> for one character.</summary>
    internal static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001c' and <= '\u001f';

    /// <summary>Python's <c>str.strip()</c>.</summary>
    internal static string Strip(string text) => text.Trim(Whitespace);

    /// <summary>Python's <c>str.rstrip()</c>.</summary>
    internal static string RStrip(string text) => text.TrimEnd(Whitespace);

    /// <summary>Python's <c>str.split()</c> with no separator: runs of whitespace split, and empty parts are
    /// dropped.</summary>
    internal static string[] Split(string text) => text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Python's <c>str.lower()</c>: Unicode full lowercase mapping. It differs from
    /// <see cref="string.ToLowerInvariant"/> for U+0130 (<c>İ</c>, which Python lowers to <c>i</c> plus a combining
    /// dot and .NET leaves alone) and for capital sigma at the end of a word, which Python lowers to final sigma
    /// (<c>ς</c>).
    /// </summary>
    internal static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(Rune.ToLowerInvariant(new Rune(c, text[i + 1])).ToString());
                i++;
            }
            else if (c == '\u0130')
            {
                sb.Append("i\u0307");
            }
            else if (c == '\u03a3')
            {
                sb.Append(IsFinalSigma(text, i) ? '\u03c2' : '\u03c3');
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    // CPython's handle_capital_sigma: \p{cased} \p{case-ignorable}* U+03A3 !(\p{case-ignorable}* \p{cased}).
    private static bool IsFinalSigma(string text, int index)
    {
        var j = index - 1;
        while (j >= 0 && IsCaseIgnorable(text[j]))
        {
            j--;
        }

        if (j < 0 || !IsCased(text[j]))
        {
            return false;
        }

        j = index + 1;
        while (j < text.Length && IsCaseIgnorable(text[j]))
        {
            j++;
        }

        return j == text.Length || !IsCased(text[j]);
    }

    private static bool IsCased(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter
            or UnicodeCategory.TitlecaseLetter ||
        char.ToLowerInvariant(c) != c || char.ToUpperInvariant(c) != c;

    private static bool IsCaseIgnorable(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol ||
        c is '\'' or '.' or ':' or '\u00b7' or '\u0387' or '\u055f' or '\u05f4' or '\u2018' or '\u2019' or '\u2024'
            or '\u2027' or '\ufe13' or '\ufe52' or '\ufe55' or '\uff07' or '\uff0e' or '\uff1a';

    private static void AppendRepr(StringBuilder sb, object? value)
    {
        value = Normalize(value);
        switch (value)
        {
            case null:
                sb.Append("None");
                return;
            case bool b:
                sb.Append(b ? "True" : "False");
                return;
            case string s:
                AppendStringRepr(sb, s);
                return;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            case BigInteger bi:
                sb.Append(bi.ToString(CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append(FloatRepr(d));
                return;
            case DateTimeOffset dto:
                AppendDateTimeRepr(sb, dto.DateTime, dto.Offset, dto.Ticks);
                return;
            case DateTime dt:
                if (dt.Kind == DateTimeKind.Unspecified)
                {
                    AppendDateTimeRepr(sb, dt, null, dt.Ticks);
                }
                else
                {
                    var aware = new DateTimeOffset(dt);
                    AppendDateTimeRepr(sb, aware.DateTime, aware.Offset, aware.Ticks);
                }

                return;
            case DateOnly date:
                sb.Append(CultureInfo.InvariantCulture, $"datetime.date({date.Year}, {date.Month}, {date.Day})");
                return;
            case Guid g:
                sb.Append("UUID('").Append(g.ToString("D")).Append("')");
                return;
        }

        if (TryGetMapping(value, out var entries))
        {
            sb.Append('{');
            var first = true;
            foreach (var (key, item) in entries)
            {
                if (!first)
                {
                    sb.Append(", ");
                }

                first = false;
                AppendRepr(sb, key);
                sb.Append(": ");
                AppendRepr(sb, item);
            }

            sb.Append('}');
            return;
        }

        if (TryGetSequence(value, out var items))
        {
            sb.Append('[');
            var first = true;
            foreach (var item in items)
            {
                if (!first)
                {
                    sb.Append(", ");
                }

                first = false;
                AppendRepr(sb, item);
            }

            sb.Append(']');
            return;
        }

        throw new NotSupportedException(
            $"Values of type '{TypeName(value)}' have no Python equivalent; pass strings, numbers, booleans, null, " +
            "dates, lists or dictionaries.");
    }

    /// <summary>CPython's <c>unicode_repr</c>.</summary>
    private static void AppendStringRepr(StringBuilder sb, string s)
    {
        var quote = s.Contains('\'', StringComparison.Ordinal) && !s.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        sb.Append(quote);
        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            var units = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                units = 2;
            }

            if (cp == quote || cp == '\\')
            {
                sb.Append('\\').Append((char)cp);
            }
            else if (cp == '\t')
            {
                sb.Append("\\t");
            }
            else if (cp == '\n')
            {
                sb.Append("\\n");
            }
            else if (cp == '\r')
            {
                sb.Append("\\r");
            }
            else if (cp < ' ' || cp == 0x7F)
            {
                sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            }
            else if (cp < 0x7F || IsPrintable(cp))
            {
                sb.Append(s, i, units);
            }
            else if (cp <= 0xFF)
            {
                sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
            }
            else if (cp <= 0xFFFF)
            {
                sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
            }

            i += units - 1;
        }

        sb.Append(quote);
    }

    /// <summary>Python's <c>str.isprintable()</c> for one code point.</summary>
    /// <remarks>Uses .NET's Unicode tables, which may be a different Unicode version from the Python build's.</remarks>
    private static bool IsPrintable(int codePoint) =>
        codePoint == ' ' || CharUnicodeInfo.GetUnicodeCategory(codePoint) is not (
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
            UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);

    private static void AppendDateTimeRepr(StringBuilder sb, DateTime local, TimeSpan? offset, long ticks)
    {
        var c = CultureInfo.InvariantCulture;
        var micro = ticks / 10 % 1_000_000;
        sb.Append(c, $"datetime.datetime({local.Year}, {local.Month}, {local.Day}, {local.Hour}, {local.Minute}");
        if (local.Second != 0 || micro != 0)
        {
            sb.Append(c, $", {local.Second}");
        }

        if (micro != 0)
        {
            sb.Append(c, $", {micro}");
        }

        if (offset is { } o)
        {
            sb.Append(", tzinfo=");
            if (o == TimeSpan.Zero)
            {
                sb.Append("datetime.timezone.utc");
            }
            else
            {
                // repr(timedelta) normalises to days >= -1 and 0 <= seconds < 86400.
                var totalSeconds = (long)o.TotalSeconds;
                var days = Math.DivRem(totalSeconds, 86400, out var seconds);
                if (seconds < 0)
                {
                    seconds += 86400;
                    days -= 1;
                }

                sb.Append("datetime.timezone(datetime.timedelta(");
                if (days != 0)
                {
                    sb.Append(c, $"days={days}");
                    if (seconds != 0)
                    {
                        sb.Append(", ");
                    }
                }

                if (seconds != 0)
                {
                    sb.Append(c, $"seconds={seconds}");
                }

                sb.Append("))");
            }
        }

        sb.Append(')');
    }

    private static string IsoFormat(DateTimeOffset value, char separator) =>
        IsoFormat(value.DateTime, value.Ticks, value.Offset, separator);

    private static string IsoFormat(DateTime value, char separator)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return IsoFormat(value, value.Ticks, null, separator);
        }

        var aware = new DateTimeOffset(value);
        return IsoFormat(aware.DateTime, aware.Ticks, aware.Offset, separator);
    }

    private static string IsoFormat(DateTime local, long ticks, TimeSpan? offset, char separator)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(local.ToString("yyyy-MM-dd", c)).Append(separator).Append(local.ToString("HH:mm:ss", c));
        var micro = ticks / 10 % 1_000_000;
        if (micro != 0)
        {
            sb.Append('.').Append(micro.ToString("000000", c));
        }

        if (offset is { } o)
        {
            sb.Append(o < TimeSpan.Zero ? '-' : '+').Append(o.Duration().ToString(@"hh\:mm", c));
        }

        return sb.ToString();
    }
}
