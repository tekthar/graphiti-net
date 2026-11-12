// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using static GraphitiNet.Tests.Maintenance.EdgeTestData;

namespace GraphitiNet.Tests.Maintenance;

/// <summary>The temporal rules of <c>edge_operations.py</c>: which contradicted facts close, and when.</summary>
public class TemporalInvalidationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OlderContradictedFactIsClosedAtTheNewStart()
    {
        var resolved = Edge("a", "b", "new", validAt: T0.AddDays(10));
        var old = Edge("a", "c", "old", validAt: T0);

        var invalidated = EdgeOperations.ApplyTemporalInvalidation(resolved, [old]);

        Assert.Same(old, Assert.Single(invalidated));
        Assert.Equal(T0.AddDays(10), old.InvalidAt);
        Assert.NotNull(old.ExpiredAt);
        Assert.Null(resolved.ExpiredAt);
    }

    [Fact]
    public void LaterCandidateClosesTheNewFactInstead()
    {
        var resolved = Edge("a", "b", "new", validAt: T0);
        var later = Edge("a", "c", "later", validAt: T0.AddDays(5));
        var latest = Edge("a", "d", "latest", validAt: T0.AddDays(9));
        var undated = Edge("a", "e", "undated");
        var candidates = new List<EntityEdge> { undated, latest, later };

        var invalidated = EdgeOperations.ApplyTemporalInvalidation(resolved, candidates);

        Assert.Empty(invalidated);
        Assert.Equal(T0.AddDays(5), resolved.InvalidAt);
        Assert.NotNull(resolved.ExpiredAt);
        // Sorted in place by start date, undated last.
        Assert.Equal([later, latest, undated], candidates);
    }

    [Fact]
    public void FactWithEndDateIsExpiredOnArrival()
    {
        var resolved = Edge("a", "b", "new", validAt: T0, invalidAt: T0.AddDays(1));

        var invalidated = EdgeOperations.ApplyTemporalInvalidation(resolved, []);

        Assert.Empty(invalidated);
        Assert.NotNull(resolved.ExpiredAt);
    }

    [Fact]
    public void NonOverlappingFactsDoNotConflict()
    {
        var resolved = Edge("a", "b", "new", validAt: T0.AddDays(10), invalidAt: T0.AddDays(20));
        var endedBefore = Edge("a", "c", "ended", validAt: T0, invalidAt: T0.AddDays(10));
        var startsAfter = Edge("a", "d", "after", validAt: T0.AddDays(20));
        var undated = Edge("a", "e", "undated");

        var invalidated = EdgeOperations.ResolveEdgeContradictions(resolved, [endedBefore, startsAfter, undated]);

        Assert.Empty(invalidated);
        Assert.Equal(T0.AddDays(10), endedBefore.InvalidAt);
        Assert.Null(startsAfter.InvalidAt);
        Assert.Null(undated.ExpiredAt);
    }

    [Fact]
    public void ExistingExpiryIsKept()
    {
        var expired = T0.AddDays(-100);
        var resolved = Edge("a", "b", "new", validAt: T0.AddDays(10));
        var old = Edge("a", "c", "old", validAt: T0);
        old.ExpiredAt = expired;

        EdgeOperations.ResolveEdgeContradictions(resolved, [old]);

        Assert.Equal(expired, old.ExpiredAt);
        Assert.Equal(T0.AddDays(10), old.InvalidAt);
    }

    [Fact]
    public void ComparesInstantsAcrossOffsets()
    {
        var resolved = Edge("a", "b", "new", validAt: new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(5)));
        // 06:00 UTC is after 05:00 UTC, even though 06:00+00:00 reads earlier than 10:00+05:00.
        var later = Edge("a", "c", "later", validAt: new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero));

        var invalidated = EdgeOperations.ApplyTemporalInvalidation(resolved, [later]);

        Assert.Empty(invalidated);
        Assert.Equal(later.ValidAt, resolved.InvalidAt);
    }
}
