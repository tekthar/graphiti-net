// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Differential;

/// <summary>text_utils.py and bulk_utils.py against upstream's recorded results.</summary>
public class TextAndBulkUtilsDifferentialTests
{
    [Fact]
    public void TruncateAtSentence_matches_upstream()
    {
        var m = new Mismatches("truncate_at_sentence");
        foreach (var c in Fixture.Cases("text_utils", "truncate_at_sentence"))
        {
            var result = TextUtils.TruncateAtSentence(
                c.Input.GetProperty("text").GetString()!, c.Input.GetProperty("max_chars").GetInt32());
            m.Equal(c, c.Output, result);
        }

        m.AssertNone();
    }

    [Fact]
    public void TruncateUtf8ToBytes_matches_upstream()
    {
        var m = new Mismatches("truncate_utf8_to_bytes");
        foreach (var c in Fixture.Cases("text_utils", "truncate_utf8_to_bytes"))
        {
            var result = TextUtils.TruncateUtf8ToBytes(
                c.Input.GetProperty("text").GetString()!, c.Input.GetProperty("max_bytes").GetInt32());
            m.Equal(c, c.Output, result);
        }

        m.AssertNone();
    }

    [Fact]
    public void ConcatenateEpisodes_matches_upstream()
    {
        var m = new Mismatches("concatenate_episodes");
        foreach (var c in Fixture.Cases("text_utils", "concatenate_episodes"))
        {
            m.Equal(c, c.Output, TextUtils.ConcatenateEpisodes(Episodes(c.Input)));
        }

        m.AssertNone();
    }

    [Fact]
    public void ConcatenateTimeline_matches_upstream()
    {
        var m = new Mismatches("concatenate_timeline");
        foreach (var c in Fixture.Cases("text_utils", "concatenate_timeline"))
        {
            var result = TextUtils.ConcatenateTimeline(
                Episodes(c.Input.GetProperty("episodes")), Episodes(c.Input.GetProperty("timeline")));
            m.Equal(c, c.Output, result);
        }

        m.AssertNone();
    }

    [Fact]
    public void CompressUuidMap_matches_upstream()
    {
        var m = new Mismatches("compress_uuid_map");
        foreach (var c in Fixture.Cases("bulk_utils", "compress_uuid_map"))
        {
            // Upstream builds the map from a set, so its key order is arbitrary: compare as a map.
            var result = BulkUtils.CompressUuidMap(Pairs(c.Input));
            m.Equal(c, Sorted(c.Output), Sorted(result), "map");
        }

        m.AssertNone();
    }

    [Fact]
    public void BuildDirectedUuidMap_matches_upstream()
    {
        var m = new Mismatches("_build_directed_uuid_map");
        foreach (var c in Fixture.Cases("bulk_utils", "build_directed_uuid_map"))
        {
            var result = BulkUtils.BuildDirectedUuidMap(Pairs(c.Input));
            m.Equal(c, Sorted(c.Output), Sorted(result), "map");
        }

        m.AssertNone();
    }

    [Fact]
    public void ResolveEdgePointers_matches_upstream()
    {
        var m = new Mismatches("resolve_edge_pointers");
        foreach (var c in Fixture.Cases("bulk_utils", "resolve_edge_pointers"))
        {
            var edges = c.Input.GetProperty("edges").EnumerateArray().Select(e =>
            {
                var edge = EdgeOperationsDifferentialTests.Edge(e[0].GetString()!);
                edge.SourceNodeUuid = e[1].GetString()!;
                edge.TargetNodeUuid = e[2].GetString()!;
                return edge;
            }).ToList();
            var uuidMap = c.Input.GetProperty("uuid_map").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

            var kept = BulkUtils.ResolveEdgePointers(edges, uuidMap);
            m.Equal(c, c.Output.GetProperty("kept"), kept.Select(e => e.Uuid).ToList(), "kept");
            m.Equal(c, c.Output.GetProperty("edges"),
                edges.Select(e => new[] { e.Uuid, e.SourceNodeUuid, e.TargetNodeUuid }).ToList(), "edges");
        }

        m.AssertNone();
    }

    private static List<EpisodicNode> Episodes(JsonElement list) =>
        list.EnumerateArray().Select(e => new EpisodicNode
        {
            Uuid = e.GetProperty("uuid").GetString()!,
            Name = e.GetProperty("uuid").GetString()!,
            GroupId = "g",
            Source = EpisodeType.Message,
            SourceDescription = "chat",
            Content = e.GetProperty("content").GetString()!,
            ValidAt = Fixture.DateTime(e.GetProperty("valid_at"))!.Value,
        }).ToList();

    private static List<(string, string)> Pairs(JsonElement list) =>
        list.EnumerateArray().Select(p => (p[0].GetString()!, p[1].GetString()!)).ToList();

    private static List<string[]> Sorted(IReadOnlyDictionary<string, string> map) =>
        map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value }).ToList();

    // A recorded dict as a list of [key, value] pairs ordered by key, for order-independent comparison.
    private static JsonElement Sorted(JsonElement map) =>
        JsonSerializer.SerializeToElement(map.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new[] { p.Name, p.Value.GetString() })
            .ToList());
}
