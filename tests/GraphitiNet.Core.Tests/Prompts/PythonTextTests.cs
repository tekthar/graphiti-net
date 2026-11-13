// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Prompts;

/// <summary>Python's <c>str()</c> and <c>repr()</c>; every expected string was printed by Python 3.</summary>
public class PythonTextTests
{
    [Fact]
    public void Str_of_a_string_is_the_string_and_containers_use_repr()
    {
        Assert.Equal("it's", PythonText.Str("it's"));
        Assert.Equal("None", PythonText.Str(null));
        Assert.Equal("True", PythonText.Str(true));
        Assert.Equal(
            "[{'entity_type_id': 0, 'entity_type_name': 'Entity', 'entity_type_description': None}]",
            PythonText.Str(new[]
            {
                new Dictionary<string, object?>
                {
                    ["entity_type_id"] = 0,
                    ["entity_type_name"] = "Entity",
                    ["entity_type_description"] = null,
                },
            }));
    }

    [Theory]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("both ' and \"", "'both \\' and \"'")]
    [InlineData("é", "'é'")]
    [InlineData("😀", "'😀'")]
    [InlineData("\ue000", "'\\ue000'")]
    [InlineData("\U000e0001", "'\\U000e0001'")]
    [InlineData("a\u00a0b\u200bc\u2028", "'a\\xa0b\\u200bc\\u2028'")]
    [InlineData("x\u0001y\u007fz\u0085", "'x\\x01y\\x7fz\\x85'")]
    [InlineData("tab\there\nnew\rret\\", "'tab\\there\\nnew\\rret\\\\'")]
    public void Repr_of_strings_matches_cpython(string value, string expected)
    {
        Assert.Equal(expected, PythonText.Repr(value));
    }

    [Theory]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(1e22, "1e+22")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1e16, "1e+16")]
    [InlineData(9999999999999998.0, "9999999999999998.0")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(123e-20, "1.23e-18")]
    [InlineData(3.0, "3.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(double.NaN, "nan")]
    [InlineData(double.NegativeInfinity, "-inf")]
    public void Float_repr_matches_cpython(double value, string expected)
    {
        Assert.Equal(expected, PythonText.FloatRepr(value));
    }

    [Fact]
    public void Datetimes_render_like_python_datetimes()
    {
        var utc = new DateTimeOffset(2025, 1, 3, 9, 30, 15, TimeSpan.Zero).AddTicks(1_234_567);
        var offset = new DateTimeOffset(2025, 1, 3, 9, 30, 0, new TimeSpan(-5, -30, 0));

        Assert.Equal("2025-01-03 09:30:15.123456+00:00", PythonText.Str(utc));
        Assert.Equal("2025-01-03T09:30:15.123456+00:00", PythonText.IsoFormat(utc));
        Assert.Equal("2025-01-03T09:30:00-05:30", PythonText.IsoFormat(offset));
        Assert.Equal("2025-01-03T00:00:00", PythonText.IsoFormat(new DateTime(2025, 1, 3)));
        Assert.Equal("2025-01-03T00:00:00+00:00", PythonText.IsoFormat(new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("datetime.datetime(2025, 1, 3, 0, 0)", PythonText.Repr(new DateTime(2025, 1, 3)));
        Assert.Equal(
            "datetime.datetime(2025, 1, 3, 9, 30, tzinfo=datetime.timezone(datetime.timedelta(days=-1, seconds=66600)))",
            PythonText.Repr(offset));
    }

    [Fact]
    public void Truthiness_matches_python()
    {
        Assert.False(PythonText.IsTruthy(null));
        Assert.False(PythonText.IsTruthy(""));
        Assert.False(PythonText.IsTruthy(0));
        Assert.False(PythonText.IsTruthy(0.0));
        Assert.False(PythonText.IsTruthy(Array.Empty<string>()));
        Assert.False(PythonText.IsTruthy(new Dictionary<string, object?>()));
        Assert.True(PythonText.IsTruthy("x"));
        Assert.True(PythonText.IsTruthy(new List<int> { 0 }));
        Assert.True(PythonText.IsTruthy(new Dictionary<string, object?> { ["a"] = null }));
    }

    [Fact]
    public void Len_counts_lists_dicts_and_strings()
    {
        Assert.Equal(3, PythonText.Len(new List<int> { 1, 2, 3 }));
        Assert.Equal(1, PythonText.Len(new Dictionary<string, int> { ["a"] = 1 }));
        Assert.Equal(2, PythonText.Len("ab"));
        Assert.Throws<NotSupportedException>(() => PythonText.Len(null));
    }
}
