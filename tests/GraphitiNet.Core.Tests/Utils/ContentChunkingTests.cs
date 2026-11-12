// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/test_content_chunking.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Utils;

// The threshold tests change process-wide settings, as upstream's monkeypatch does.
[CollectionDefinition(nameof(ContentChunkingSettings), DisableParallelization = true)]
public sealed class ContentChunkingSettings;

[Collection(nameof(ContentChunkingSettings))]
public class ContentChunkingTests
{
    private static string Dumps(object value) => JsonSerializer.Serialize(value);

    private static void WithSettings(double? density, int? minTokens, Action body)
    {
        var oldDensity = ContentChunking.ChunkDensityThreshold;
        var oldMin = ContentChunking.ChunkMinTokens;
        try
        {
            if (density is { } d)
            {
                ContentChunking.ChunkDensityThreshold = d;
            }

            if (minTokens is { } m)
            {
                ContentChunking.ChunkMinTokens = m;
            }

            body();
        }
        finally
        {
            ContentChunking.ChunkDensityThreshold = oldDensity;
            ContentChunking.ChunkMinTokens = oldMin;
        }
    }

    private static HashSet<(int, int)> CoveredPairs(IEnumerable<(IReadOnlyList<int> Items, IReadOnlyList<int> Indices)> result)
    {
        var covered = new HashSet<(int, int)>();
        foreach (var (_, indices) in result)
        {
            for (var i = 0; i < indices.Count; i++)
            {
                for (var j = i + 1; j < indices.Count; j++)
                {
                    covered.Add((Math.Min(indices[i], indices[j]), Math.Max(indices[i], indices[j])));
                }
            }
        }

        return covered;
    }

    private static HashSet<(int, int)> AllPairs(int n)
    {
        var pairs = new HashSet<(int, int)>();
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                pairs.Add((i, j));
            }
        }

        return pairs;
    }

    // TestEstimateTokens

    [Fact]
    public void Estimate_tokens_uses_four_chars_per_token()
    {
        Assert.Equal(0, ContentChunking.EstimateTokens(""));
        Assert.Equal(1, ContentChunking.EstimateTokens("abcd"));
        Assert.Equal(2, ContentChunking.EstimateTokens("abcdefgh"));
        Assert.Equal(100, ContentChunking.EstimateTokens(new string('a', 400)));
        Assert.Equal(10, ContentChunking.EstimateTokens(new string('x', ContentChunking.CharsPerToken * 10)));
    }

    // TestChunkJsonArray

    [Fact]
    public void Small_array_is_not_chunked()
    {
        var content = """[{"name": "Alice"}, {"name": "Bob"}]""";
        var chunks = ContentChunking.ChunkJsonContent(content, chunkSizeTokens: 1000);
        Assert.Equal([content], chunks);
    }

    [Fact]
    public void Empty_array_and_object()
    {
        Assert.Equal(["[]"], ContentChunking.ChunkJsonContent("[]", chunkSizeTokens: 100));
        Assert.Equal(["{}"], ContentChunking.ChunkJsonContent("{}", chunkSizeTokens: 100));
    }

    [Fact]
    public void Array_splits_at_element_boundaries()
    {
        var data = Enumerable.Range(0, 20).Select(i => new { id = i, data = new string('x', 100) }).ToList();
        var chunks = ContentChunking.ChunkJsonContent(Dumps(data), chunkSizeTokens: 100, overlapTokens: 20);
        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            var parsed = Assert.IsType<JsonArray>(JsonNode.Parse(chunk));
            foreach (var item in parsed)
            {
                Assert.NotNull(item!["id"]);
                Assert.NotNull(item["data"]);
            }
        }
    }

    [Fact]
    public void Array_preserves_all_elements()
    {
        var data = Enumerable.Range(0, 10).Select(i => new { id = i }).ToList();
        var chunks = ContentChunking.ChunkJsonContent(Dumps(data), chunkSizeTokens: 50, overlapTokens: 10);
        var seen = chunks.SelectMany(chunk => JsonNode.Parse(chunk)!.AsArray()).Select(item => (int)item!["id"]!)
            .ToHashSet();
        Assert.Equal(Enumerable.Range(0, 10).ToHashSet(), seen);
    }

    [Fact]
    public void Chunks_use_python_json_dumps_format()
    {
        var data = Enumerable.Range(0, 10).Select(i => new { id = i }).ToList();
        var chunks = ContentChunking.ChunkJsonContent(Dumps(data), chunkSizeTokens: 10, overlapTokens: 1);
        Assert.StartsWith("[{\"id\": 0}, ", chunks[0], StringComparison.Ordinal);
    }

    // TestChunkJsonObject

    [Fact]
    public void Object_splits_at_key_boundaries_and_keeps_every_key()
    {
        var data = Enumerable.Range(0, 20).ToDictionary(i => $"key_{i}", _ => new string('x', 100));
        var chunks = ContentChunking.ChunkJsonContent(Dumps(data), chunkSizeTokens: 100, overlapTokens: 20);
        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            var parsed = Assert.IsType<JsonObject>(JsonNode.Parse(chunk));
            Assert.All(parsed, pair => Assert.StartsWith("key_", pair.Key, StringComparison.Ordinal));
        }

        var small = Enumerable.Range(0, 10).ToDictionary(i => $"key_{i}", i => $"value_{i}");
        var keys = ContentChunking.ChunkJsonContent(Dumps(small), chunkSizeTokens: 50, overlapTokens: 10)
            .SelectMany(chunk => JsonNode.Parse(chunk)!.AsObject().Select(pair => pair.Key))
            .ToHashSet();
        Assert.Equal(small.Keys.ToHashSet(), keys);
    }

    [Fact]
    public void Small_object_is_not_chunked()
    {
        var content = """{"name": "Alice", "age": 30}""";
        Assert.Equal([content], ContentChunking.ChunkJsonContent(content, chunkSizeTokens: 1000));
    }

    // TestChunkJsonInvalid

    [Fact]
    public void Invalid_json_falls_back_to_text()
    {
        var chunks = ContentChunking.ChunkJsonContent("not valid json {", chunkSizeTokens: 1000);
        Assert.Contains("not valid json {", chunks[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"string\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    public void Scalar_json_is_returned_as_is(string scalar) =>
        Assert.Equal([scalar], ContentChunking.ChunkJsonContent(scalar, chunkSizeTokens: 1000));

    // TestChunkTextContent

    [Fact]
    public void Small_text_is_not_chunked() =>
        Assert.Equal(["This is a short text."], ContentChunking.ChunkTextContent("This is a short text.", 1000));

    [Fact]
    public void Text_splits_at_paragraphs()
    {
        var text = string.Join("\n\n", "Paragraph one.", "Paragraph two.", "Paragraph three.");
        var chunks = ContentChunking.ChunkTextContent(text, chunkSizeTokens: 10, overlapTokens: 5);
        Assert.All(chunks, chunk => Assert.False(chunk.EndsWith(' ')));
    }

    [Fact]
    public void Long_paragraph_splits_at_sentences()
    {
        var paragraph = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"This is sentence number {i}."));
        var chunks = ContentChunking.ChunkTextContent(paragraph, chunkSizeTokens: 50, overlapTokens: 10);
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void Text_chunks_keep_every_word()
    {
        const string text = "Alpha beta gamma delta epsilon zeta eta theta.";
        var chunks = ContentChunking.ChunkTextContent(text, chunkSizeTokens: 10, overlapTokens: 2);
        var all = text.Replace(".", "", StringComparison.Ordinal).Split(' ').ToHashSet();
        var found = chunks.SelectMany(c => c.Replace(".", "", StringComparison.Ordinal).Split(' ')).ToHashSet();
        Assert.Subset(found, all);
    }

    [Fact]
    public void Text_overlap_shares_words_between_chunks()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"Paragraph {i} with some content here."));
        var chunks = ContentChunking.ChunkTextContent(text, chunkSizeTokens: 50, overlapTokens: 20);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            Assert.NotEmpty(chunks[i].Split().Intersect(chunks[i + 1].Split()));
        }
    }

    // TestChunkMessageContent

    [Fact]
    public void Small_conversation_is_not_chunked()
    {
        const string content = "Alice: Hello!\nBob: Hi there!";
        Assert.Equal([content], ContentChunking.ChunkMessageContent(content, chunkSizeTokens: 1000));
    }

    [Fact]
    public void Speaker_messages_stay_whole()
    {
        var content = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"Speaker{i}: This is message number {i}."));
        var chunks = ContentChunking.ChunkMessageContent(content, chunkSizeTokens: 50, overlapTokens: 10);
        Assert.True(chunks.Count > 1);
        foreach (var line in chunks.SelectMany(c => c.Split('\n')).Where(l => l.Trim().Length > 0))
        {
            Assert.Contains(":", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Json_message_array_splits_at_messages()
    {
        var messages = Enumerable.Range(0, 10).Select(i => new { role = "user", content = $"Message {i}" }).ToList();
        var chunks = ContentChunking.ChunkMessageContent(Dumps(messages), chunkSizeTokens: 50, overlapTokens: 10);
        foreach (var chunk in chunks)
        {
            foreach (var message in JsonNode.Parse(chunk)!.AsArray())
            {
                Assert.NotNull(message!["role"]);
                Assert.NotNull(message["content"]);
            }
        }
    }

    // TestEdgeCases

    [Fact]
    public void Edge_cases()
    {
        Assert.NotEmpty(ContentChunking.ChunkJsonContent(
            Dumps(new[] { new { content = new string('x', 10000) } }), chunkSizeTokens: 100, overlapTokens: 10));
        Assert.Equal([""], ContentChunking.ChunkTextContent("", chunkSizeTokens: 100));
        Assert.Equal([""], ContentChunking.ChunkMessageContent("", chunkSizeTokens: 100));
        Assert.NotEmpty(ContentChunking.ChunkTextContent("   \n\n   ", chunkSizeTokens: 100));
    }

    // TestShouldChunk

    [Fact]
    public void Empty_content_never_chunks()
    {
        Assert.False(ContentChunking.ShouldChunk("", EpisodeType.Text));
        Assert.False(ContentChunking.ShouldChunk("", EpisodeType.Json));
    }

    [Fact]
    public void Short_content_never_chunks() => WithSettings(0.001, 1000, () =>
    {
        var dense = Dumps(Enumerable.Range(0, 50).Select(i => new { name = $"Entity{i}" }));
        Assert.False(ContentChunking.ShouldChunk(dense, EpisodeType.Json));
    });

    [Fact]
    public void Large_dense_json_chunks() => WithSettings(0.01, 500, () =>
    {
        var dense = Dumps(Enumerable.Range(0, 200).Select(i => new { name = $"Entity{i}", desc = new string('x', 20) }));
        Assert.True(ContentChunking.ShouldChunk(dense, EpisodeType.Json));
    });

    [Fact]
    public void Low_density_text_and_json_do_not_chunk() => WithSettings(0.05, 100, () =>
    {
        var prose = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 50));
        Assert.False(ContentChunking.ShouldChunk(prose, EpisodeType.Text));
        var sparse = Dumps(new[] { new { content = new string('x', 1000) }, new { content = new string('y', 1000) } });
        Assert.False(ContentChunking.ShouldChunk(sparse, EpisodeType.Json));
    });

    // TestJsonDensityEstimation

    [Fact]
    public void Json_density()
    {
        WithSettings(0.01, null, () =>
        {
            var dense = Dumps(Enumerable.Range(0, 100).Select(i => new { id = i }));
            Assert.True(ContentChunking.JsonLikelyDense(dense, ContentChunking.EstimateTokens(dense)));
            var keys = Dumps(Enumerable.Range(0, 50).ToDictionary(i => $"key_{i}", i => $"value_{i}"));
            Assert.True(ContentChunking.JsonLikelyDense(keys, ContentChunking.EstimateTokens(keys)));
        });
        WithSettings(0.05, null, () =>
        {
            var sparse = Dumps(new[] { new { content = new string('x', 1000) }, new { content = new string('y', 1000) } });
            Assert.False(ContentChunking.JsonLikelyDense(sparse, ContentChunking.EstimateTokens(sparse)));
        });
    }

    [Fact]
    public void Count_json_keys_respects_depth()
    {
        var data = JsonNode.Parse("""{"a": 1, "b": {"c": 2, "d": 3}, "e": [{"f": 4}, {"g": 5}]}""")!.AsObject();
        Assert.Equal(7, ContentChunking.CountJsonKeys(data, maxDepth: 2));
        var deep = JsonNode.Parse("""{"a": {"b": {"c": {"d": 1}}}}""")!.AsObject();
        Assert.Equal(1, ContentChunking.CountJsonKeys(deep, maxDepth: 1));
        Assert.Equal(2, ContentChunking.CountJsonKeys(deep, maxDepth: 2));
    }

    // TestTextDensityEstimation

    [Fact]
    public void Text_density()
    {
        WithSettings(0.01, null, () =>
        {
            var text = string.Concat(Enumerable.Repeat(
                "Alice met Bob at Acme Corp. Then Carol and David joined them. "
                + "Eve from Globex introduced Frank and Grace. Later Henry and Iris arrived from Initech. ", 10));
            Assert.True(ContentChunking.TextLikelyDense(text, ContentChunking.EstimateTokens(text)));
        });
        WithSettings(0.05, null, () =>
        {
            var prose = string.Concat(Enumerable.Repeat(
                """
                the sun was setting over the horizon as the old man walked slowly
                down the dusty road. he had been traveling for many days and his
                feet were tired.
                """, 10));
            Assert.False(ContentChunking.TextLikelyDense(prose, ContentChunking.EstimateTokens(prose)));
            var starters = string.Concat(Enumerable.Repeat("This is a sentence. Another one follows. Yet another here. ", 50));
            Assert.False(ContentChunking.TextLikelyDense(starters, ContentChunking.EstimateTokens(starters)));
        });
    }

    // TestGenerateCoveringChunks

    [Fact]
    public void Covering_chunks_small_inputs()
    {
        var empty = ContentChunking.GenerateCoveringChunks(Array.Empty<string>(), 3);
        Assert.Single(empty);
        Assert.Empty(empty[0].Items);

        var single = ContentChunking.GenerateCoveringChunks(["A"], 3);
        Assert.Equal(["A"], single[0].Items);
        Assert.Equal([0], single[0].Indices);

        var fit = ContentChunking.GenerateCoveringChunks(["A", "B", "C", "D"], 4);
        Assert.Single(fit);
        Assert.Equal([0, 1, 2, 3], fit[0].Indices);
    }

    [Fact]
    public void Covering_chunks_k2_cover_each_pair_once()
    {
        var result = ContentChunking.GenerateCoveringChunks(Enumerable.Range(0, 4).ToList(), 2);
        Assert.All(result, chunk => Assert.Equal(2, chunk.Indices.Count));
        Assert.Equal(6, result.Count);
        Assert.Equal(AllPairs(4), CoveredPairs(result));
    }

    [Theory]
    [InlineData(6, 3, 5)]
    [InlineData(10, 4, 1)]
    public void Covering_chunks_cover_every_pair(int n, int k, int minChunks)
    {
        var result = ContentChunking.GenerateCoveringChunks(Enumerable.Range(0, n).ToList(), k);
        Assert.All(result, chunk => Assert.Equal(k, chunk.Indices.Count));
        Assert.True(result.Count >= minChunks);
        Assert.Equal(AllPairs(n), CoveredPairs(result));
    }

    [Fact]
    public void Covering_chunk_indices_map_to_items()
    {
        string[] items = ["Alice", "Bob", "Carol", "Dave", "Eve"];
        foreach (var (chunkItems, indices) in ContentChunking.GenerateCoveringChunks(items, 3))
        {
            for (var i = 0; i < indices.Count; i++)
            {
                Assert.Equal(items[indices[i]], chunkItems[i]);
            }
        }
    }

    [Fact]
    public void Covering_chunks_are_deterministic_when_enumerated()
    {
        var items = Enumerable.Range(0, 8).ToList();
        var first = ContentChunking.GenerateCoveringChunks(items, 3);
        var second = ContentChunking.GenerateCoveringChunks(items, 3);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Indices, second[i].Indices);
        }
    }

    [Theory]
    [InlineData(30, 15)]
    [InlineData(50, 5)]
    [InlineData(100, 4)]
    [InlineData(20, 10)]
    [InlineData(30, 5)]
    public void Sampled_covering_chunks_cover_every_pair(int n, int k)
    {
        var result = ContentChunking.GenerateCoveringChunks(Enumerable.Range(0, n).ToList(), k);
        Assert.All(result, chunk => Assert.True(chunk.Indices.Count <= k));
        Assert.Equal(AllPairs(n), CoveredPairs(result));
    }
}
