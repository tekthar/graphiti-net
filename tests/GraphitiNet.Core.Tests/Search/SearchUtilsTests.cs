// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/search/search_utils_test.py (Graphiti v0.31.2) and changed for .NET. The tests after the
// hybrid node search ones are new and cover graphiti_core/search/search_utils.py.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

public class SearchUtilsTests
{
    [Fact]
    public async Task Hybrid_node_search_deduplication()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = query => query == "Alice"
            ? [Make.Node("1", "Alice")]
            : [Make.Node("2", "Bob")];
        driver.Search.NodeSimilarity = vector => vector[0] == 0.1f
            ? [Make.Node("1", "Alice")]
            : [Make.Node("3", "Charlie")];

        var results = await SearchUtils.HybridNodeSearchAsync(
            ["Alice", "Bob"],
            [[0.1f, 0.2f, 0.3f], [0.4f, 0.5f, 0.6f]],
            driver,
            new SearchFilters());

        Assert.Equal(3, results.Count);
        Assert.Equal(new HashSet<string> { "1", "2", "3" }, results.Select(n => n.Uuid).ToHashSet());
        Assert.Equal(new HashSet<string> { "Alice", "Bob", "Charlie" }, results.Select(n => n.Name).ToHashSet());
        Assert.Equal(2, driver.Search.CallCount("NodeFulltextSearchAsync"));
        Assert.Equal(2, driver.Search.CallCount("NodeSimilaritySearchAsync"));
    }

    [Fact]
    public async Task Hybrid_node_search_empty_results()
    {
        var driver = new FakeGraphDriver();

        var results = await SearchUtils.HybridNodeSearchAsync(
            ["NonExistent"], [[0.1f, 0.2f, 0.3f]], driver, new SearchFilters());

        Assert.Empty(results);
    }

    [Fact]
    public async Task Hybrid_node_search_only_fulltext()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("1", "Alice")];

        var results = await SearchUtils.HybridNodeSearchAsync(["Alice"], [], driver, new SearchFilters());

        Assert.Single(results);
        Assert.Equal("Alice", results[0].Name);
        Assert.Equal(1, driver.Search.CallCount("NodeFulltextSearchAsync"));
        Assert.Equal(0, driver.Search.CallCount("NodeSimilaritySearchAsync"));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    public async Task Hybrid_node_search_applies_twice_the_limit_per_method(int limit, int expectedCount)
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("1", "Alice"), Make.Node("2", "Bob")];
        driver.Search.NodeSimilarity = _ => limit == 1
            ? [Make.Node("3", "Charlie"), Make.Node("4", "David")]
            : [Make.Node("1", "Alice"), Make.Node("3", "Charlie")];

        var results = await SearchUtils.HybridNodeSearchAsync(
            ["Test"], [[0.1f, 0.2f, 0.3f]], driver, new SearchFilters(), ["1"], limit);

        // The limit applies per search method before deduplication; the hybrid search itself does not cut.
        Assert.Equal(expectedCount, results.Count);
        var fulltext = Assert.Single(driver.Search.Calls, c => c.Method == "NodeFulltextSearchAsync");
        Assert.Equal("Test", fulltext.Args[0]);
        Assert.Equal(["1"], (IReadOnlyList<string>)fulltext.Args[2]!);
        Assert.Equal(2 * limit, fulltext.Args[3]);
        var similarity = Assert.Single(driver.Search.Calls, c => c.Method == "NodeSimilaritySearchAsync");
        Assert.Equal(2 * limit, similarity.Args[3]);
        Assert.Equal(SearchDefaults.DefaultMinScore, similarity.Args[4]);
    }

    [Fact]
    public void Cosine_similarity_matches_numpy()
    {
        Assert.Equal(1.0, SearchUtils.CalculateCosineSimilarity([1, 2, 3], [2, 4, 6]), 12);
        Assert.Equal(0.0, SearchUtils.CalculateCosineSimilarity([1, 0], [0, 1]), 12);
        Assert.Equal(-1.0, SearchUtils.CalculateCosineSimilarity([1, 1], [-1, -1]), 12);
        Assert.Equal(0.0, SearchUtils.CalculateCosineSimilarity([0, 0], [1, 1]));
        Assert.Equal(32.0 / (Math.Sqrt(14) * Math.Sqrt(77)), SearchUtils.CalculateCosineSimilarity([1, 2, 3], [4, 5, 6]), 12);
    }

    [Fact]
    public void Rrf_sums_reciprocal_ranks_and_keeps_first_seen_order_on_ties()
    {
        var (uuids, scores) = SearchUtils.Rrf([["a", "b", "c"], ["b", "d"]]);

        // a: 1, b: 1/2 + 1, c: 1/3, d: 1/2.
        Assert.Equal(["b", "a", "d", "c"], uuids);
        Assert.Equal([1.5, 1.0, 0.5, 1.0 / 3], scores);

        var (tied, _) = SearchUtils.Rrf([["x"], ["y"], ["z"]]);
        Assert.Equal(["x", "y", "z"], tied);
    }

    [Fact]
    public void Rrf_drops_scores_below_min_score()
    {
        var (uuids, scores) = SearchUtils.Rrf([["a", "b", "c"]], minScore: 0.5);

        Assert.Equal(["a", "b"], uuids);
        Assert.Equal([1.0, 0.5], scores);
    }

    [Fact]
    public void Mmr_uses_lambda_minus_one_times_max_similarity()
    {
        // a and b are identical; c is orthogonal to both. The query leans towards a/b.
        var candidates = new Dictionary<string, float[]>
        {
            ["a"] = [1, 0],
            ["b"] = [2, 0],
            ["c"] = [0, 3],
        };

        var (uuids, scores) = SearchUtils.MaximalMarginalRelevance([0.8f, 0.6f], candidates, 0.5);

        // a, b: 0.5 * 0.8 + (0.5 - 1) * 1 = -0.1; c: 0.5 * 0.6 + (0.5 - 1) * 0 = 0.3.
        Assert.Equal(["c", "a", "b"], uuids);
        Assert.Equal(0.3, scores[0], 6);
        Assert.Equal(-0.1, scores[1], 6);
        Assert.Equal(-0.1, scores[2], 6);
    }

    [Fact]
    public void Mmr_with_lambda_one_is_pure_relevance_and_min_score_filters()
    {
        var candidates = new Dictionary<string, float[]> { ["a"] = [1, 0], ["b"] = [0, 1] };

        var (uuids, scores) = SearchUtils.MaximalMarginalRelevance([0.2f, 0.9f], candidates, 1, minScore: 0.5);

        Assert.Equal(["b"], uuids);
        Assert.Equal(0.9, scores[0], 6);
    }

    [Fact]
    public void Mmr_default_min_score_keeps_negative_scores()
    {
        var candidates = new Dictionary<string, float[]> { ["a"] = [-1, 0] };

        var (uuids, scores) = SearchUtils.MaximalMarginalRelevance([1f, 0f], candidates);

        Assert.Equal(["a"], uuids);
        Assert.Equal(-0.5, scores[0], 6);
    }

    [Fact]
    public async Task Node_distance_reranker_puts_centre_first_then_neighbours_then_others()
    {
        var driver = new FakeGraphDriver();
        driver.Search.Connections.Add(("center", "n2"));
        driver.Search.Connections.Add(("n4", "center"));

        var (uuids, scores) = await SearchUtils.NodeDistanceRerankerAsync(
            driver, ["n1", "n2", "center", "n3", "n4"], "center");

        Assert.Equal(["center", "n2", "n4", "n1", "n3"], uuids);
        Assert.Equal([10.0, 1.0, 1.0, 0.0, 0.0], scores);
        var call = Assert.Single(driver.Search.Calls);
        Assert.Equal(["n1", "n2", "n3", "n4"], (IReadOnlyList<string>)call.Args[1]!);
    }

    [Fact]
    public async Task Node_distance_reranker_min_score_drops_non_neighbours()
    {
        var driver = new FakeGraphDriver();
        driver.Search.Connections.Add(("center", "n2"));

        var (uuids, scores) = await SearchUtils.NodeDistanceRerankerAsync(driver, ["n1", "n2"], "center", 0.5);

        Assert.Equal(["n2"], uuids);
        Assert.Equal([1.0], scores);
    }

    [Fact]
    public async Task Node_distance_reranker_without_centre_in_candidates()
    {
        var driver = new FakeGraphDriver();

        var (uuids, scores) = await SearchUtils.NodeDistanceRerankerAsync(driver, ["n1"], "center");

        Assert.Equal(["n1"], uuids);
        Assert.Equal([0.0], scores);
    }

    [Fact]
    public async Task Get_embeddings_skips_missing_and_leaves_records_unchanged()
    {
        var driver = new FakeGraphDriver();
        driver.EntityEdges.Embeddings["e1"] = [1, 2];
        var e1 = Make.Edge("e1", "fact 1");
        var e2 = Make.Edge("e2", "fact 2");

        var embeddings = await SearchUtils.GetEmbeddingsForEdgesAsync(driver, [e1, e2]);

        Assert.Equal(["e1"], embeddings.Keys);
        Assert.Equal([1f, 2f], embeddings["e1"]);
        Assert.Null(e1.FactEmbedding);

        driver.EntityNodes.Embeddings["n1"] = [3];
        var nodeEmbeddings = await SearchUtils.GetEmbeddingsForNodesAsync(driver, [Make.Node("n1")]);
        Assert.Equal([3f], nodeEmbeddings["n1"]);

        driver.CommunityNodes.Embeddings["c1"] = [4];
        var community = Make.Community("c1", "c");
        var communityEmbeddings = await SearchUtils.GetEmbeddingsForCommunitiesAsync(driver, [community]);
        Assert.Equal([4f], communityEmbeddings["c1"]);
        Assert.Null(community.NameEmbedding);
    }

    [Fact]
    public async Task Get_episodes_by_mentions_cuts_episode_ids_to_limit()
    {
        var driver = new FakeGraphDriver();
        foreach (var uuid in new[] { "ep1", "ep2", "ep3" })
        {
            driver.EpisodeNodes.Episodes[uuid] = Make.Episode(uuid, uuid);
        }

        var episodes = await SearchUtils.GetEpisodesByMentionsAsync(
            driver,
            [],
            [Make.Edge("e1", "f", episodes: ["ep1", "ep2"]), Make.Edge("e2", "f", episodes: ["ep3"])],
            limit: 2);

        Assert.Equal(["ep1", "ep2"], episodes.Select(e => e.Uuid));
        Assert.Equal(["ep1", "ep2"], Assert.Single(driver.EpisodeNodes.GetByUuidsCalls));
    }

    [Fact]
    public async Task Mentioned_nodes_and_communities_delegate_to_the_driver()
    {
        var driver = new FakeGraphDriver();
        driver.Graph.MentionedNodes = _ => [Make.Node("n1")];
        driver.Graph.CommunitiesByNodes = _ => [Make.Community("c1", "c")];

        Assert.Equal("n1", Assert.Single(await SearchUtils.GetMentionedNodesAsync(driver, [])).Uuid);
        Assert.Equal("c1", Assert.Single(await SearchUtils.GetCommunitiesByNodesAsync(driver, [])).Uuid);
    }

    [Fact]
    public async Task Fulltext_queries_at_the_word_limit_return_nothing_without_calling_the_driver()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ => [Make.Edge("e1", "f")];
        var longQuery = string.Join(' ', Enumerable.Repeat("word", 127));

        Assert.Single(await SearchUtils.EdgeFulltextSearchAsync(driver, longQuery, new SearchFilters()));
        Assert.Empty(await SearchUtils.EdgeFulltextSearchAsync(driver, longQuery, new SearchFilters(), ["g1"]));
        Assert.Equal(1, driver.Search.CallCount("EdgeFulltextSearchAsync"));
        Assert.True(SearchUtils.IsFulltextQueryTooLong(string.Join(' ', Enumerable.Repeat("w", 128)), null));
    }

    [Fact]
    public async Task Bfs_without_origins_returns_nothing_without_calling_the_driver()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeBfs = _ => [Make.Edge("e1", "f")];

        Assert.Empty(await SearchUtils.EdgeBfsSearchAsync(driver, null, 3, new SearchFilters()));
        Assert.Empty(await SearchUtils.EdgeBfsSearchAsync(driver, [], 3, new SearchFilters()));
        Assert.Empty(await SearchUtils.NodeBfsSearchAsync(driver, null, new SearchFilters(), 3));
        Assert.Empty(driver.Search.Calls);
        Assert.Single(await SearchUtils.EdgeBfsSearchAsync(driver, ["n1"], 3, new SearchFilters()));
    }
}
