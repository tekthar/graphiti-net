// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;

namespace GraphitiNet.Tests.Differential;

/// <summary>edge_operations.py's temporal invalidation and edge type signatures against upstream's recorded
/// results.</summary>
/// <remarks>The generator freezes upstream's <c>utc_now()</c>; where upstream set an expiry to "now" the fixture says
/// <c>{"$now": true}</c>, and the port must have set the current time there.</remarks>
public class EdgeOperationsDifferentialTests
{
    private const string File = "edge_operations";

    [Fact]
    public void ResolveEdgeContradictions_matches_upstream() =>
        Temporal("resolve_edge_contradictions", (r, c) => EdgeOperations.ResolveEdgeContradictions(r, c));

    [Fact]
    public void ApplyTemporalInvalidation_matches_upstream() =>
        Temporal("apply_temporal_invalidation", (r, c) => EdgeOperations.ApplyTemporalInvalidation(r, c));

    [Fact]
    public void EnforceEdgeTypeSignatures_matches_upstream()
    {
        var m = new Mismatches("enforce_edge_type_signatures");
        foreach (var c in Fixture.Cases(File, "enforce_edge_type_signatures"))
        {
            var edges = c.Input.GetProperty("edges").EnumerateArray().Select(e =>
            {
                var edge = Edge(e.GetProperty("uuid").GetString()!);
                edge.Name = e.GetProperty("name").GetString()!;
                edge.SourceNodeUuid = e.GetProperty("source").GetString()!;
                edge.TargetNodeUuid = e.GetProperty("target").GetString()!;
                foreach (var (key, value) in (Dictionary<string, object?>)Fixture.Decode(e.GetProperty("attributes"))!)
                {
                    edge.Attributes[key] = value;
                }

                return edge;
            }).ToList();
            var matching = c.Input.GetProperty("matching").EnumerateArray()
                .Select(names => (IReadOnlyDictionary<string, Type>)TypeMap(Fixture.Strings(names)))
                .ToList();
            var (kept, keptTypes) = EdgeOperations.EnforceEdgeTypeSignatures(
                edges, matching, EdgeTypeMap(c.Input.GetProperty("edge_type_map")),
                c.Input.GetProperty("strict").GetBoolean());

            m.Equal(c, c.Output.GetProperty("kept"), kept.Select(e => new Dictionary<string, object?>
            {
                ["uuid"] = e.Uuid,
                ["name"] = e.Name,
                ["attributes"] = e.Attributes,
            }).ToList(), "kept");
            m.Equal(c, c.Output.GetProperty("kept_types"), keptTypes.Select(t => t.Keys.ToList()).ToList(),
                "kept_types");
        }

        m.AssertNone();
    }

    [Fact]
    public void EdgeTypesForEndpoints_matches_upstream()
    {
        var m = new Mismatches("_edge_types_for_endpoints");
        foreach (var c in Fixture.Cases(File, "edge_types_for_endpoints"))
        {
            var nodes = c.Input.GetProperty("nodes").EnumerateArray().Select(n => new EntityNode
            {
                Uuid = n.GetProperty("uuid").GetString()!,
                Name = n.GetProperty("uuid").GetString()!,
                GroupId = "g",
                Labels = Fixture.Strings(n.GetProperty("labels")),
            }).ToDictionary(n => n.Uuid, StringComparer.Ordinal);
            var edge = Edge("e");
            edge.SourceNodeUuid = c.Input.GetProperty("edge").GetProperty("source").GetString()!;
            edge.TargetNodeUuid = c.Input.GetProperty("edge").GetProperty("target").GetString()!;

            var result = EdgeOperations.EdgeTypesForEndpoints(
                edge, nodes, TypeMap(Fixture.Strings(c.Input.GetProperty("edge_types"))),
                EdgeTypeMap(c.Input.GetProperty("edge_type_map")));
            m.Equal(c, c.Output, result.Keys.ToList());
        }

        m.AssertNone();
    }

    private static void Temporal(string function, Func<EntityEdge, List<EntityEdge>, IReadOnlyList<EntityEdge>> port)
    {
        var m = new Mismatches(function);
        foreach (var c in Fixture.Cases(File, function))
        {
            var resolved = Edge(c.Input.GetProperty("resolved"));
            var candidates = c.Input.GetProperty("candidates").EnumerateArray().Select(Edge).ToList();
            var invalidated = port(resolved, candidates);

            m.Equal(c, c.Output.GetProperty("invalidated"), invalidated.Select(e => e.Uuid).ToList(), "invalidated");
            m.Equal(c, c.Output.GetProperty("resolved"), State(resolved), "resolved edge");
            m.Equal(c, c.Output.GetProperty("candidates"), candidates.Select(State).ToList(), "candidates");
        }

        m.AssertNone();
    }

    private static Dictionary<string, object?> State(EntityEdge e) => new()
    {
        ["uuid"] = e.Uuid,
        ["valid_at"] = e.ValidAt,
        ["invalid_at"] = e.InvalidAt,
        ["expired_at"] = e.ExpiredAt,
    };

    private static EntityEdge Edge(JsonElement state)
    {
        var edge = Edge(state.GetProperty("uuid").GetString()!);
        edge.ValidAt = Fixture.DateTime(state.GetProperty("valid_at"));
        edge.InvalidAt = Fixture.DateTime(state.GetProperty("invalid_at"));
        edge.ExpiredAt = Fixture.DateTime(state.GetProperty("expired_at"));
        return edge;
    }

    internal static EntityEdge Edge(string uuid) => new()
    {
        Uuid = uuid,
        GroupId = "g",
        SourceNodeUuid = "s",
        TargetNodeUuid = "t",
        CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Name = "RELATES_TO",
        Fact = $"fact {uuid}",
    };

    private static Dictionary<string, Type> TypeMap(IEnumerable<string> names) =>
        names.ToDictionary(n => n, _ => typeof(object), StringComparer.Ordinal);

    private static Dictionary<(string Source, string Target), IReadOnlyList<string>> EdgeTypeMap(JsonElement map) =>
        map.EnumerateArray().ToDictionary(
            e => (e[0].GetString()!, e[1].GetString()!),
            e => (IReadOnlyList<string>)Fixture.Strings(e[2]));
}
