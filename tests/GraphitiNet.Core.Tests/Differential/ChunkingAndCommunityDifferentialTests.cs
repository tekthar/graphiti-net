// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Driver;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Tests.Utils;
using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Differential;

/// <summary>content_chunking.py and community_operations.label_propagation against upstream's recorded
/// results.</summary>
/// <remarks>
/// Deliberate deviation: the port measures text in UTF-16 code units where upstream counts code points (see
/// <see cref="ContentChunking"/>). Cases whose text has characters outside the Basic Multilingual Plane are marked
/// <c>astral</c> by the generator; for those a different result is the documented deviation, not a failure. Every
/// other case must match exactly. Runs in the chunking settings collection, at upstream's default settings.
/// </remarks>
[Collection(nameof(ContentChunkingSettings))]
public class ChunkingAndCommunityDifferentialTests
{
    private const string File = "content_chunking";

    [Fact]
    public void EstimateTokens_matches_upstream() =>
        Chunking("estimate_tokens", c => ContentChunking.EstimateTokens(c.Input.GetString()!));

    [Fact]
    public void ShouldChunk_matches_upstream() =>
        Chunking("should_chunk", c => ContentChunking.ShouldChunk(
            c.Input.GetProperty("content").GetString()!,
            EpisodeTypes.FromStorageName(c.Input.GetProperty("episode_type").GetString()!)));

    [Fact]
    public void ChunkJsonContent_matches_upstream() =>
        Chunking("chunk_json_content", c => ContentChunking.ChunkJsonContent(Content(c), Size(c), Overlap(c)));

    [Fact]
    public void ChunkTextContent_matches_upstream() =>
        Chunking("chunk_text_content", c => ContentChunking.ChunkTextContent(Content(c), Size(c), Overlap(c)));

    [Fact]
    public void ChunkMessageContent_matches_upstream() =>
        Chunking("chunk_message_content", c => ContentChunking.ChunkMessageContent(Content(c), Size(c), Overlap(c)));

    [Fact]
    public void GenerateCoveringChunks_matches_upstream()
    {
        // Only inputs small enough for upstream to enumerate every combination: larger ones sample with Python's
        // random module, which the port cannot reproduce. Pairs the greedy pass cannot cover (every pair when k is 1)
        // are appended in Python's frozenset iteration order upstream, in the port's set order here: a documented
        // deviation, so for those cases the chunks are compared as a set.
        var m = new Mismatches("generate_covering_chunks");
        foreach (var c in Fixture.Cases(File, "generate_covering_chunks"))
        {
            var chunks = ContentChunking.GenerateCoveringChunks(
                    Fixture.Strings(c.Input.GetProperty("items")), c.Input.GetProperty("k").GetInt32())
                .Select(chunk => new object[] { chunk.Items, chunk.Indices })
                .ToList();
            if (c.Flag("unordered"))
            {
                var expected = c.Output.EnumerateArray().Select(e => e.GetRawText()).Order(StringComparer.Ordinal);
                var actual = chunks.Select(chunk => JsonSerializer.Serialize(chunk)).Order(StringComparer.Ordinal);
                m.Check(c, expected.SequenceEqual(actual, StringComparer.Ordinal), () => "chunks differ as a set");
                continue;
            }

            m.Equal(c, c.Output, chunks);
        }

        m.AssertNone();
    }

    [Fact]
    public void LabelPropagation_matches_upstream()
    {
        // The generator keeps only graphs on which upstream terminates; on graphs where it oscillates forever the
        // port stops (a documented deviation), which no fixture can record.
        var m = new Mismatches("label_propagation");
        foreach (var c in Fixture.Cases("community_operations", "label_propagation"))
        {
            var projection = new Dictionary<string, IReadOnlyList<Neighbor>>(StringComparer.Ordinal);
            foreach (var entry in c.Input.EnumerateArray())
            {
                projection[entry[0].GetString()!] = entry[1].EnumerateArray()
                    .Select(n => new Neighbor(n[0].GetString()!, n[1].GetInt32()))
                    .ToList();
            }

            m.Equal(c, c.Output, CommunityOperations.LabelPropagation(projection));
        }

        m.AssertNone();
    }

    private static void Chunking(string function, Func<Case, object> port)
    {
        var m = new Mismatches(function);
        var deviations = 0;
        foreach (var c in Fixture.Cases(File, function))
        {
            var actual = port(c);
            if (c.Flag("astral") && !Mismatches.Same(c.Output, actual, 0, out _))
            {
                deviations++;  // UTF-16 lengths; see the class remarks.
                continue;
            }

            m.Equal(c, c.Output, actual);
        }

        m.AssertNone();
        Assert.True(deviations * 2 < m.Checked, $"{function}: {deviations} astral deviations, {m.Checked} matched");
    }

    private static string Content(Case c) => c.Input.GetProperty("content").GetString()!;

    private static int? Size(Case c) =>
        c.Input.GetProperty("chunk_size_tokens") is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;

    private static int? Overlap(Case c) =>
        c.Input.GetProperty("overlap_tokens") is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;
}
