// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_temporal_edge_utils.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Maintenance;
using GraphitiNet.Models;

namespace GraphitiNet.Tests.Maintenance;

public class TemporalEdgeUtilsTests
{
    private static DateTimeOffset Dt(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, DateTimeOffset?, DateTimeOffset?> FutureAssertions => new()
    {
        { "Emma will relocate to Tokyo on December 25, 2022", Dt(2022, 12, 25), null },
        { "David will launch the TechNova mobile app on September 1, 2026", Dt(2026, 9, 1), null },
        { "The team will ship version 2.0 by the end of Q3 2025", Dt(2025, 9, 30), null },
        { "Maria will pay back the loan by March 2026", null, null },
    };

    [Theory]
    [MemberData(nameof(FutureAssertions))]
    public void FutureAssertionsAreValidAtReferenceTime(string fact, DateTimeOffset? validAt, DateTimeOffset? invalidAt)
    {
        var referenceTime = Dt(2020, 1, 1);

        var (normalizedValidAt, normalizedInvalidAt) =
            TemporalEdgeUtils.NormalizeFutureTemporalBounds(fact, referenceTime, validAt, invalidAt);

        Assert.Equal(referenceTime, normalizedValidAt);
        Assert.Equal(invalidAt, normalizedInvalidAt);
    }

    [Fact]
    public void FutureSoftDeadlineIsNotInvalidAt()
    {
        var referenceTime = Dt(2020, 1, 1);

        var (validAt, invalidAt) = TemporalEdgeUtils.NormalizeFutureTemporalBounds(
            "Alice will complete the project by July 30, 2024", referenceTime, Dt(2024, 7, 30), Dt(2024, 7, 30));

        Assert.Equal(referenceTime, validAt);
        Assert.Null(invalidAt);
    }

    [Fact]
    public void FutureOngoingStateEndKeepsInvalidAt()
    {
        var referenceTime = Dt(2020, 1, 1);
        var end = Dt(2024, 12, 25);

        var (validAt, invalidAt) = TemporalEdgeUtils.NormalizeFutureTemporalBounds(
            "TinyBirds styles will be unavailable until December 25, 2024", referenceTime, null, end);

        Assert.Equal(referenceTime, validAt);
        Assert.Equal(end, invalidAt);
    }

    [Fact]
    public void CompletedEventDateIsUnchanged()
    {
        var start = Dt(2024, 9, 1);

        var (validAt, invalidAt) = TemporalEdgeUtils.NormalizeFutureTemporalBounds(
            "David launched the TechNova mobile app on September 1, 2024", Dt(2020, 1, 1), start, null);

        Assert.Equal(start, validAt);
        Assert.Null(invalidAt);
    }

    [Theory]
    [InlineData("She'll call tomorrow")]
    [InlineData("Bob PLANS TO move")]
    [InlineData("The release is scheduled to ship")]
    public void ContractionsAndPlansCountAsFuture(string fact)
    {
        var referenceTime = Dt(2020, 1, 1);
        var (validAt, _) = TemporalEdgeUtils.NormalizeFutureTemporalBounds(fact, referenceTime, Dt(2021, 1, 1), null);
        Assert.Equal(referenceTime, validAt);
    }

    [Fact]
    public void ResultsAreUtc()
    {
        var local = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(5));
        var (validAt, _) = TemporalEdgeUtils.NormalizeFutureTemporalBounds("Alice knows Bob", null, local, null);
        Assert.Equal(TimeSpan.Zero, validAt!.Value.Offset);
        Assert.Equal(local, validAt);
    }

    [Fact]
    public void ApplyExtractedTimestamps_ParsesAndNormalizes()
    {
        var edge = new EntityEdge
        {
            SourceNodeUuid = "a",
            TargetNodeUuid = "b",
            Name = "WILL_MOVE",
            Fact = "Emma will relocate to Tokyo on December 25, 2022",
            GroupId = "g",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        TemporalEdgeUtils.ApplyExtractedTimestamps(edge, "2022-12-25T00:00:00Z", "garbage", Dt(2020, 1, 1));

        Assert.Equal(Dt(2020, 1, 1), edge.ValidAt);
        Assert.Null(edge.InvalidAt);
    }

    [Theory]
    [InlineData("2025-04-30T00:00:00Z", "2025-04-30T00:00:00+00:00")]
    [InlineData("2025-04-30T10:15:30.123456Z", "2025-04-30T10:15:30.1234560+00:00")]
    [InlineData("2025-04-30T10:15:30+02:00", "2025-04-30T08:15:30+00:00")]
    [InlineData("2025-04-30T10:15:30+0200", "2025-04-30T08:15:30+00:00")]
    [InlineData("2025-04-30T10:15", "2025-04-30T10:15:00+00:00")]
    [InlineData("2025-04-30 10:15:30", "2025-04-30T10:15:30+00:00")]
    [InlineData("2025-04-30", "2025-04-30T00:00:00+00:00")]
    [InlineData("2025-04-30T10:15:30.123456789Z", "2025-04-30T10:15:30.1234567+00:00")]
    public void TryParseIsoDateTime_AcceptsIsoForms(string text, string expected)
    {
        Assert.True(TemporalEdgeUtils.TryParseIsoDateTime(text, out var value));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("April 30, 2025")]
    [InlineData("2025-13-01")]
    [InlineData("2025-04-30T25:00:00Z")]
    public void TryParseIsoDateTime_RejectsOtherText(string text) =>
        Assert.False(TemporalEdgeUtils.TryParseIsoDateTime(text, out _));
}
