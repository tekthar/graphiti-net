// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Prompts;

/// <summary>
/// <see cref="PromptHelpers.ToPromptJson"/> must produce exactly what Python's <c>json.dumps</c> produces. Every
/// expected string here was printed by Python 3 with the same arguments.
/// </summary>
public class PromptHelpersTests
{
    private static Dictionary<string, object?> Sample() => new()
    {
        ["a"] = 1,
        ["b"] = new List<object?> { 1, 2, new Dictionary<string, object?> { ["c"] = null } },
        ["d"] = true,
        ["e"] = "x",
    };

    [Fact]
    public void Minified_output_uses_python_separators_and_keeps_key_order()
    {
        Assert.Equal("""{"a": 1, "b": [1, 2, {"c": null}], "d": true, "e": "x"}""", PromptHelpers.ToPromptJson(Sample()));
    }

    [Fact]
    public void Indented_output_matches_json_dumps_indent()
    {
        var data = new Dictionary<string, object?>
        {
            ["a"] = 1,
            ["b"] = new object?[] { 1, 2, new Dictionary<string, object?> { ["c"] = null } },
            ["e"] = Array.Empty<object>(),
            ["f"] = new Dictionary<string, object?>(),
        };

        Assert.Equal(
            "{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2,\n    {\n      \"c\": null\n    }\n  ],\n  \"e\": [],\n  \"f\": {}\n}",
            PromptHelpers.ToPromptJson(data, indent: 2));
        Assert.Equal("[]", PromptHelpers.ToPromptJson(Array.Empty<int>(), indent: 2));
        Assert.Equal("{}", PromptHelpers.ToPromptJson(new Dictionary<string, int>(), indent: 4));
        Assert.Equal("[\n[]\n]", PromptHelpers.ToPromptJson(new[] { Array.Empty<int>() }, indent: 0));
    }

    [Fact]
    public void Non_ascii_is_kept_by_default_and_escaped_with_ensure_ascii()
    {
        const string text = "Zürich 😀 \u2028 \u007f";

        Assert.Equal("\"Zürich 😀 \u2028 \u007f\"", PromptHelpers.ToPromptJson(text));
        Assert.Equal("\"Z\\u00fcrich \\ud83d\\ude00 \\u2028 \\u007f\"", PromptHelpers.ToPromptJson(text, ensureAscii: true));
    }

    [Fact]
    public void Control_characters_quotes_and_backslashes_are_escaped()
    {
        Assert.Equal(
            "\"q\\\"b\\\\s\\n\\r\\t\\b\\f\\u0000\\u001f\"",
            PromptHelpers.ToPromptJson("q\"b\\s\n\r\t\b\f\u0000\u001f"));
    }

    [Fact]
    public void Floats_render_like_python_repr()
    {
        var values = new object[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e22, 5e-324, 1.7976931348623157e308,
            0.1 + 0.2, 100.0, 1e16, 123456789012345678.0, 1e-5, 0.0001, -0.0, 12345.678,
        };

        Assert.Equal(
            "[NaN, Infinity, -Infinity, 1e+22, 5e-324, 1.7976931348623157e+308, 0.30000000000000004, 100.0, 1e+16, " +
            "1.2345678901234568e+17, 1e-05, 0.0001, -0.0, 12345.678]",
            PromptHelpers.ToPromptJson(values));
    }

    [Fact]
    public void Integers_of_any_size_render_as_digits()
    {
        Assert.Equal("18446744073709551616", PromptHelpers.ToPromptJson(BigInteger.Pow(2, 64)));
        Assert.Equal("[18446744073709551615, -5, 7]", PromptHelpers.ToPromptJson(new object[] { ulong.MaxValue, (sbyte)-5, (byte)7 }));
    }

    [Fact]
    public void Non_string_keys_convert_like_python()
    {
        Assert.Equal("""{"1": "a"}""", PromptHelpers.ToPromptJson(new Dictionary<int, string> { [1] = "a" }));
        Assert.Equal("""{"true": "b"}""", PromptHelpers.ToPromptJson(new Dictionary<bool, string> { [true] = "b" }));
        Assert.Equal("""{"2.5": "d"}""", PromptHelpers.ToPromptJson(new Dictionary<double, string> { [2.5] = "d" }));
    }

    [Fact]
    public void Datetimes_render_as_their_isoformat_string()
    {
        // Upstream passes dt.isoformat() strings (json.dumps rejects datetimes); a DateTimeOffset renders the same text.
        var timestamp = new DateTimeOffset(2025, 1, 3, 9, 30, 15, TimeSpan.Zero).AddTicks(1_234_567);

        Assert.Equal("\"2025-01-03T09:30:15.123456+00:00\"", PromptHelpers.ToPromptJson(timestamp));
        Assert.Equal(
            """[{"content": "hi", "timestamp": "2025-01-03T09:30:00-05:30"}, {"content": "yo", "timestamp": null}]""",
            PromptHelpers.ToPromptJson(new[]
            {
                new Dictionary<string, object?>
                {
                    ["content"] = "hi",
                    ["timestamp"] = new DateTimeOffset(2025, 1, 3, 9, 30, 0, new TimeSpan(-5, -30, 0)),
                },
                new Dictionary<string, object?> { ["content"] = "yo", ["timestamp"] = null },
            }));
    }

    [Fact]
    public void Json_elements_and_nodes_render_like_json_loads_values()
    {
        using var doc = JsonDocument.Parse("""{"i": 10, "f": 1.0, "e": 1e2, "s": "é", "n": null, "l": [true, false]}""");

        Assert.Equal("""{"i": 10, "f": 1.0, "e": 100.0, "s": "é", "n": null, "l": [true, false]}""", PromptHelpers.ToPromptJson(doc.RootElement));
        Assert.Equal(
            """{"x": [1, "two", 3.5], "y": {"z": null}}""",
            PromptHelpers.ToPromptJson(new JsonObject
            {
                ["x"] = new JsonArray(1, "two", 3.5),
                ["y"] = new JsonObject { ["z"] = null },
            }));
    }

    [Fact]
    public void Read_only_dictionaries_and_sequences_are_supported()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> data = new SortedList<string, IReadOnlyList<string>>
        {
            ["b"] = ["1"],
            ["a"] = [],
        };

        Assert.Equal("""{"a": [], "b": ["1"]}""", PromptHelpers.ToPromptJson(data));
    }

    [Fact]
    public void Unsupported_values_throw()
    {
        var ex = Assert.Throws<NotSupportedException>(() => PromptHelpers.ToPromptJson(new { Name = "x" }));
        Assert.Contains("is not JSON serializable", ex.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => PromptHelpers.ToPromptJson(DayOfWeek.Monday));
    }

    [Fact]
    public void Do_not_escape_unicode_note_matches_upstream()
    {
        Assert.Equal("\nDo not escape unicode characters.\n", PromptHelpers.DoNotEscapeUnicode);
    }
}
