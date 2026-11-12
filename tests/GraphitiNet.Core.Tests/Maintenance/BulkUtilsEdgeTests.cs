// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_bulk_utils.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Maintenance;
using GraphitiNet.Models;

namespace GraphitiNet.Tests.Maintenance;

public class BulkUtilsEdgeTests
{
    private static EntityEdge Edge(string name, string fact, string source, string target) => new()
    {
        Name = name,
        Fact = fact,
        GroupId = "group",
        SourceNodeUuid = source,
        TargetNodeUuid = target,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void BuildDirectedUuidMap_Empty() => Assert.Empty(BulkUtils.BuildDirectedUuidMap([]));

    [Fact]
    public void BuildDirectedUuidMap_Chain()
    {
        var mapping = BulkUtils.BuildDirectedUuidMap([("a", "b"), ("b", "c")]);

        Assert.Equal("c", mapping["a"]);
        Assert.Equal("c", mapping["b"]);
        Assert.Equal("c", mapping["c"]);
    }

    [Fact]
    public void BuildDirectedUuidMap_PreservesDirection()
    {
        var mapping = BulkUtils.BuildDirectedUuidMap([("zzz-alias", "aaa-canonical"), ("alias", "canonical")]);

        Assert.Equal("canonical", mapping["alias"]);
        Assert.Equal("canonical", mapping["canonical"]);
        Assert.Equal("aaa-canonical", mapping["zzz-alias"]);
    }

    [Fact]
    public void CompressUuidMap_MapsToSmallestIdInEachGroup()
    {
        var mapping = BulkUtils.CompressUuidMap([("c", "b"), ("b", "d"), ("x", "y")]);

        Assert.Equal(
            new Dictionary<string, string> { ["b"] = "b", ["c"] = "b", ["d"] = "b", ["x"] = "x", ["y"] = "x" },
            mapping.ToDictionary());
    }

    [Fact]
    public void ResolveEdgePointers_UpdatesSources()
    {
        var edge = Edge("knows", "fact", "alias", "target");

        var surviving = BulkUtils.ResolveEdgePointers([edge], new Dictionary<string, string> { ["alias"] = "canonical" });

        Assert.Equal("canonical", edge.SourceNodeUuid);
        Assert.Equal("target", edge.TargetNodeUuid);
        Assert.Equal([edge], surviving);
    }

    [Fact]
    public void ResolveEdgePointers_DropsEdgesCollapsedOntoOneNode()
    {
        var collapsing = Edge("pinned_at", "Postgres is pinned at Postgres 16.", "postgres", "postgres-16");
        var survivingEdge = Edge("knows", "fact", "alias", "target");

        var surviving = BulkUtils.ResolveEdgePointers(
            [collapsing, survivingEdge],
            new Dictionary<string, string> { ["postgres-16"] = "postgres", ["alias"] = "canonical" });

        Assert.Equal([survivingEdge], surviving);
    }

    [Fact]
    public void ResolveEdgePointers_KeepsOriginalSelfEdges()
    {
        var selfEdge = Edge("feels_happy", "Alice feels happy", "alice", "alice");
        var remappedSelfEdge = Edge("jogs", "Alice goes jogging every morning", "alice-alias", "alice-alias");

        var surviving = BulkUtils.ResolveEdgePointers(
            [selfEdge, remappedSelfEdge], new Dictionary<string, string> { ["alice-alias"] = "alice" });

        Assert.Equal([selfEdge, remappedSelfEdge], surviving);
        Assert.Equal("alice", remappedSelfEdge.SourceNodeUuid);
        Assert.Equal("alice", remappedSelfEdge.TargetNodeUuid);
    }

    [Fact]
    public void ResolveEdgePointers_WorksOnEpisodicEdges()
    {
        var edge = new EpisodicEdge
        {
            GroupId = "g",
            SourceNodeUuid = "episode",
            TargetNodeUuid = "alias",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var surviving = BulkUtils.ResolveEdgePointers([edge], new Dictionary<string, string> { ["alias"] = "canonical" });

        Assert.Equal("canonical", Assert.Single(surviving).TargetNodeUuid);
    }
}
