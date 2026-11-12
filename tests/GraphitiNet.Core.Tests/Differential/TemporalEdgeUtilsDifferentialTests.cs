// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Maintenance;

namespace GraphitiNet.Tests.Differential;

/// <summary>temporal_edge_utils.py against upstream's recorded results, including every timestamp form Python's
/// <c>datetime.fromisoformat</c> accepts or rejects.</summary>
public class TemporalEdgeUtilsDifferentialTests
{
    private const string File = "temporal_edge_utils";

    [Fact]
    public void NormalizeFutureTemporalBounds_matches_upstream()
    {
        var m = new Mismatches("normalize_future_temporal_bounds");
        foreach (var c in Fixture.Cases(File, "normalize_future_temporal_bounds"))
        {
            var (validAt, invalidAt) = TemporalEdgeUtils.NormalizeFutureTemporalBounds(
                c.Input.GetProperty("fact").GetString()!,
                Fixture.DateTime(c.Input.GetProperty("reference_time")),
                Fixture.DateTime(c.Input.GetProperty("valid_at")),
                Fixture.DateTime(c.Input.GetProperty("invalid_at")));
            m.Equal(c, c.Output, new object?[] { validAt, invalidAt });
        }

        m.AssertNone();
    }

    [Fact]
    public void FromIsoFormat_matches_python()
    {
        // ensure_utc(datetime.fromisoformat(text.replace('Z', '+00:00'))), the parse upstream applies to LLM
        // timestamps. Where Python raises OverflowError (the UTC conversion leaves the datetime range), the port
        // deliberately treats the text as unparseable, as for ValueError.
        var m = new Mismatches("fromisoformat");
        foreach (var c in Fixture.Cases(File, "fromisoformat"))
        {
            var text = c.Input.GetString()!;
            var parsed = TemporalEdgeUtils.TryParseIsoDateTime(text, out var value);
            if (Fixture.Raises(c.Output) is { } error)
            {
                m.Check(c, !parsed, () => $"Python raised {error}; the port parsed {value:O}");
                continue;
            }

            m.Check(c, parsed, () => "Python parsed it; the port did not");
            if (parsed)
            {
                m.Equal(c, c.Output, value);
            }
        }

        m.AssertNone();
    }

    /// <remarks>Cases marked <c>deviation</c> are where upstream raises OverflowError; their recorded output is
    /// upstream's result with the out-of-range timestamp left out, which is what the port does.</remarks>
    [Fact]
    public void ApplyExtractedTimestamps_matches_upstream()
    {
        var m = new Mismatches("apply_extracted_timestamps");
        foreach (var c in Fixture.Cases(File, "apply_extracted_timestamps"))
        {
            var edge = EdgeOperationsDifferentialTests.Edge("e");
            edge.Fact = c.Input.GetProperty("fact").GetString()!;
            edge.ValidAt = Fixture.DateTime(c.Input.GetProperty("edge_valid_at"));
            edge.InvalidAt = Fixture.DateTime(c.Input.GetProperty("edge_invalid_at"));
            var validAt = c.Input.GetProperty("valid_at").GetString();
            var invalidAt = c.Input.GetProperty("invalid_at").GetString();
            var referenceTime = Fixture.DateTime(c.Input.GetProperty("reference_time"));

            TemporalEdgeUtils.ApplyExtractedTimestamps(edge, validAt, invalidAt, referenceTime);
            m.Equal(c, c.Output, new Dictionary<string, object?>
            {
                ["valid_at"] = edge.ValidAt,
                ["invalid_at"] = edge.InvalidAt,
            });
        }

        m.AssertNone();
    }
}
