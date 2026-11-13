// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Differential;

/// <summary>
/// Loads the differential fixtures that parity/python/generate_differential_fixtures.py writes from upstream's own
/// functions, and decodes their values (see the script for the encoding).
/// </summary>
internal static class Fixture
{
    private static readonly Dictionary<string, JsonDocument> Documents = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>The cases recorded for one upstream function: (index, input, output, whole case).</summary>
    public static IReadOnlyList<Case> Cases(string file, string function)
    {
        JsonDocument document;
        lock (Gate)
        {
            if (!Documents.TryGetValue(file, out document!))
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Differential", "Fixtures", file + ".json");
                document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                Documents[file] = document;
            }
        }

        var cases = document.RootElement.GetProperty(function).EnumerateArray()
            .Select((c, i) => new Case(i, c.GetProperty("input"), c.GetProperty("output"), c))
            .ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    /// <summary>A JSON value as the .NET value the port takes for the Python value.</summary>
    /// <remarks>Dicts become <see cref="Dictionary{TKey, TValue}"/> (insertion-ordered), lists
    /// <see cref="List{T}"/>, tuples arrays, ints <see cref="long"/> or <see cref="BigInteger"/>, floats
    /// <see cref="double"/>, datetimes <see cref="DateTimeOffset"/> (naive ones in UTC), and dicts with non-string keys
    /// a list of key-value pairs.</remarks>
    public static object? Decode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
                {
                    return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                }

                return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)
                    ? (object)l
                    : BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(Decode).ToList();
            case JsonValueKind.Object:
                if (TryTagged(element, out var tag, out var body))
                {
                    return tag switch
                    {
                        "$dt" => Dt(body),
                        "$float" => Float(body),
                        "$tuple" => body.EnumerateArray().Select(Decode).ToArray(),
                        "$set" => body.EnumerateArray().Select(Decode).ToList(),
                        "$pairs" => body.EnumerateArray()
                            .Select(p => new KeyValuePair<object?, object?>(Decode(p[0]), Decode(p[1])))
                            .ToList(),
                        _ => throw new NotSupportedException(tag),
                    };
                }

                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = Decode(property.Value);
                }

                return dict;
            default:
                throw new NotSupportedException(element.ValueKind.ToString());
        }
    }

    /// <summary>A <c>{"$dt": ...}</c> value or null.</summary>
    public static DateTimeOffset? DateTime(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : Dt(element.GetProperty("$dt"));

    public static float[] Floats(JsonElement element) =>
        element.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();

    public static List<string> Strings(JsonElement element) =>
        element.EnumerateArray().Select(e => e.GetString()!).ToList();

    public static string? Raises(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty("$raises", out var r) ? r.GetString() : null;

    public static bool TryTagged(JsonElement element, out string tag, out JsonElement body)
    {
        tag = "";
        body = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var properties = element.EnumerateObject();
        if (!properties.MoveNext() || !properties.Current.Name.StartsWith('$'))
        {
            return false;
        }

        tag = properties.Current.Name;
        body = properties.Current.Value;
        return !properties.MoveNext();
    }

    private static DateTimeOffset Dt(JsonElement body) =>
        DateTimeOffset.Parse(body.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static double Float(JsonElement body) => body.GetString() switch
    {
        "nan" => double.NaN,
        "inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        var other => throw new NotSupportedException(other),
    };
}

/// <summary>One recorded call.</summary>
internal sealed record Case(int Index, JsonElement Input, JsonElement Output, JsonElement Raw)
{
    /// <summary>A boolean flag recorded next to input and output, false when absent.</summary>
    public bool Flag(string name) => Raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Collects every mismatch of a run so one failing test lists all differing cases, not just the first.
/// </summary>
internal sealed class Mismatches(string function)
{
    private readonly List<string> _messages = [];

    public int Checked { get; private set; }

    public void Add(Case c, string message) =>
        _messages.Add($"case {c.Index}: {message}\n    input: {Short(c.Input.GetRawText())}");

    /// <summary>Compares an actual value with upstream's recorded value; see <see cref="Same"/>.</summary>
    public void Equal(Case c, JsonElement expected, object? actual, string what = "output", double relTol = 0)
    {
        Checked++;
        if (!Same(expected, actual, relTol, out var why))
        {
            Add(c, $"{what} differs ({why})\n    upstream: {Short(expected.GetRawText())}\n" +
                   $"    port:     {Short(Show(actual))}");
        }
    }

    public void Check(Case c, bool ok, Func<string> message)
    {
        Checked++;
        if (!ok)
        {
            Add(c, message());
        }
    }

    /// <summary>Fails with every mismatch collected.</summary>
    public void AssertNone()
    {
        Assert.True(Checked > 0, $"{function}: nothing was compared");
        if (_messages.Count > 0)
        {
            Assert.Fail($"{function}: {_messages.Count} of {Checked} comparisons differ from upstream:\n" +
                        string.Join("\n", _messages.Take(25)));
        }
    }

    /// <summary>
    /// Whether a .NET value equals a recorded Python value: ints exactly, floats exactly or within
    /// <paramref name="relTol"/> (relative, with the same bound as an absolute floor), strings ordinally,
    /// datetimes by instant and UTC offset (a naive Python datetime is UTC), dicts with keys in order, sets in any
    /// order. <c>{"$now": true}</c> matches any datetime within the last hour.
    /// </summary>
    public static bool Same(JsonElement expected, object? actual, double relTol, out string why)
    {
        why = "";
        actual = PythonText.Normalize(actual);
        if (Fixture.TryTagged(expected, out var tag, out var body))
        {
            switch (tag)
            {
                case "$now":
                    if (actual is DateTimeOffset now &&
                        (DateTimeOffset.UtcNow - now).Duration() < TimeSpan.FromHours(1))
                    {
                        return true;
                    }

                    why = "expected the current time";
                    return false;
                case "$dt":
                    var dt = (DateTimeOffset)Fixture.Decode(expected)!;
                    if (actual is DateTimeOffset a && a == dt && a.Offset == dt.Offset)
                    {
                        return true;
                    }

                    why = "datetime";
                    return false;
                case "$float":
                    return SameFloat((double)Fixture.Decode(expected)!, actual, relTol, out why);
                case "$tuple":
                    return SameSequence(body, actual, relTol, out why);
                case "$set":
                    if (actual is not IEnumerable set || actual is string)
                    {
                        why = "expected a set";
                        return false;
                    }

                    var expectedItems = body.EnumerateArray().Select(e => Show(Fixture.Decode(e)))
                        .Order(StringComparer.Ordinal);
                    var actualItems = set.Cast<object?>().Select(Show).Order(StringComparer.Ordinal);
                    if (expectedItems.SequenceEqual(actualItems, StringComparer.Ordinal))
                    {
                        return true;
                    }

                    why = "set";
                    return false;
                case "$pairs":
                    return SameMapping(
                        body.EnumerateArray().Select(p => (p[0], p[1])).ToList(), actual, relTol, out why);
                case "$raises":
                    why = "upstream raised " + body.GetString();
                    return false;
            }
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                why = "expected None";
                return actual is null;
            case JsonValueKind.True or JsonValueKind.False:
                why = "bool";
                return actual is bool b && b == expected.GetBoolean();
            case JsonValueKind.String:
                why = "string";
                return actual is string s && string.Equals(s, expected.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                var value = Fixture.Decode(expected);
                if (value is double d)
                {
                    return SameFloat(d, actual, relTol, out why);
                }

                why = "int";
                return actual switch
                {
                    long l => value is long el && el == l,
                    BigInteger bi => value is BigInteger eb ? eb == bi : value is long el2 && el2 == bi,
                    _ => false,
                };
            case JsonValueKind.Array:
                return SameSequence(expected, actual, relTol, out why);
            case JsonValueKind.Object:
                return SameMapping(expected.EnumerateObject().Select(p => (Key(p.Name), p.Value)).ToList(), actual,
                    relTol, out why);
        }

        why = "unsupported";
        return false;
    }

    private static JsonElement Key(string name) => JsonSerializer.SerializeToElement(name);

    private static bool SameFloat(double expected, object? actual, double relTol, out string why)
    {
        why = "float";
        if (actual is not double d)
        {
            return false;
        }

        if (double.IsNaN(expected) || double.IsNaN(d))
        {
            return double.IsNaN(expected) && double.IsNaN(d);
        }

        if (expected.Equals(d))
        {
            return true;
        }

        if (relTol > 0 && Math.Abs(expected - d) <= relTol * Math.Max(1e-3, Math.Max(Math.Abs(expected), Math.Abs(d))))
        {
            return true;
        }

        why = $"float {expected:R} vs {d:R}";
        return false;
    }

    private static bool SameSequence(JsonElement expected, object? actual, double relTol, out string why)
    {
        why = "expected a list";
        if (actual is null || actual is string || !PythonText.TryGetSequence(actual, out var items) ||
            PythonText.TryGetMapping(actual, out _))
        {
            return false;
        }

        var list = items.ToList();
        var expectedList = expected.EnumerateArray().ToList();
        if (list.Count != expectedList.Count)
        {
            why = $"length {expectedList.Count} vs {list.Count}";
            return false;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (!Same(expectedList[i], list[i], relTol, out var inner))
            {
                why = $"[{i}]: {inner}";
                return false;
            }
        }

        return true;
    }

    private static bool SameMapping(List<(JsonElement Key, JsonElement Value)> expected, object? actual, double relTol,
        out string why)
    {
        why = "expected a dict";
        if (actual is null || !PythonText.TryGetMapping(actual, out var entries))
        {
            return false;
        }

        var list = entries.ToList();
        if (list.Count != expected.Count)
        {
            why = $"size {expected.Count} vs {list.Count}";
            return false;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (!Same(expected[i].Key, list[i].Key, 0, out var keyWhy))
            {
                why = $"key {i}: {keyWhy}";
                return false;
            }

            if (!Same(expected[i].Value, list[i].Value, relTol, out var valueWhy))
            {
                why = $"[{expected[i].Key.GetRawText()}]: {valueWhy}";
                return false;
            }
        }

        return true;
    }

    /// <summary>A .NET value as JSON-ish text, for messages and set comparison.</summary>
    public static string Show(object? value)
    {
        value = PythonText.Normalize(value);
        return value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => JsonSerializer.Serialize(s, ShowOptions),
            double d => double.IsFinite(d) ? PythonText.FloatRepr(d) : $"{{\"$float\":\"{PythonText.FloatRepr(d)}\"}}",
            long or BigInteger => PythonText.Str(value),
            DateTimeOffset dto => $"{{\"$dt\":\"{PythonText.IsoFormat(dto)}\"}}",
            _ when PythonText.TryGetMapping(value, out var entries) =>
                "{" + string.Join(",", entries.Select(e => $"{Show(e.Key)}:{Show(e.Value)}")) + "}",
            _ when PythonText.TryGetSequence(value, out var items) => "[" + string.Join(",", items.Select(Show)) + "]",
            _ => value.ToString() ?? "",
        };
    }

    private static readonly JsonSerializerOptions ShowOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Short(string text) => text.Length > 600 ? text[..600] + "…" : text;
}
