// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/search/test_edge_cross_encoder_rrf_shortlist.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

public class EdgeCrossEncoderRrfShortlistTests
{
    private static readonly EdgeSearchConfig Config = new()
    {
        SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity],
        Reranker = EdgeReranker.CrossEncoder,
    };

    [Fact]
    public async Task Rrf_shortlist_includes_cosine_top_hit()
    {
        // RRF + 2*limit shortlist: cosine rank-1 reaches the cross-encoder even when the BM25 list is long.
        const int limit = 2;
        var bm25Hits = Enumerable.Range(0, 6).Select(i => Make.Edge($"bm25-{i}", $"bm25-fact-{i}")).ToList();
        var cosineHit = Make.Edge("cosine-1", "cosine-top-fact");
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ => bm25Hits;
        driver.Search.EdgeSimilarity = _ => [cosineHit];
        var crossEncoder = new FakeCrossEncoder(p => p == cosineHit.Fact ? 0.99 : 0.1);

        var (edges, scores) = await SearchEngine.EdgeSearchAsync(
            driver,
            crossEncoder,
            "find the cosine top hit",
            [0.1f, 0.2f, 0.3f],
            null,
            Config,
            new SearchFilters(),
            limit: limit);

        Assert.Contains(cosineHit.Fact, crossEncoder.RankedPassages);
        Assert.Equal(2 * limit, crossEncoder.RankedPassages.Count);
        Assert.Equal(cosineHit.Uuid, edges[0].Uuid);
        Assert.Equal(limit, edges.Count);
        Assert.Equal(limit, scores.Count);
    }

    [Fact]
    public async Task Smoke_alice_works_at_zep_reaches_cross_encoder()
    {
        // BM25 fills weak hits; cosine has the real answer. RRF shortlists up to 2 * limit candidates so the answer
        // reaches the cross-encoder and can rank first.
        const int limit = 2;
        var coffee = Make.Edge("bm25-1", "Alice likes coffee");
        var livesInSf = Make.Edge("bm25-2", "Alice lives in SF");
        var extra = Enumerable.Range(3, 2).Select(i => Make.Edge($"bm25-{i}", $"Alice unrelated {i}"));
        var worksAtZep = Make.Edge("cosine-1", "Alice works at Zep");
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ => [coffee, livesInSf, .. extra];
        driver.Search.EdgeSimilarity = _ => [worksAtZep];
        var crossEncoder = new FakeCrossEncoder(p => p == worksAtZep.Fact ? 0.99 : 0.1);

        var (edges, scores) = await SearchEngine.EdgeSearchAsync(
            driver,
            crossEncoder,
            "Where does Alice work?",
            [0.1f, 0.2f, 0.3f],
            null,
            Config,
            new SearchFilters(),
            limit: limit);

        Assert.Contains(worksAtZep.Fact, crossEncoder.RankedPassages);
        Assert.True(crossEncoder.RankedPassages.Count <= 2 * limit);
        Assert.Equal(worksAtZep.Uuid, edges[0].Uuid);
        Assert.Equal("Alice works at Zep", edges[0].Fact);
        Assert.Equal(limit, edges.Count);
        Assert.Equal(limit, scores.Count);
    }
}
