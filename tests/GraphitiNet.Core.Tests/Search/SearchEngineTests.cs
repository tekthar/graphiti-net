// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Tests for the port of graphiti_core/search/search.py, search_config.py, search_config_recipes.py and
// search_helpers.py (Graphiti v0.31.2). Upstream has no direct tests for most of this behaviour; each test states the
// upstream behaviour it pins.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

public class SearchEngineTests
{
    private static readonly float[] Vector = [0.6f, 0.8f];

    private static Task<SearchResults> SearchAsync(
        FakeGraphDriver driver,
        SearchConfig config,
        string query = "query",
        IReadOnlyList<string>? groupIds = null,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? bfsOriginNodeUuids = null,
        IReadOnlyList<float>? queryVector = null,
        FakeEmbedder? embedder = null,
        FakeCrossEncoder? crossEncoder = null) =>
        SearchEngine.SearchAsync(
            driver,
            embedder ?? new FakeEmbedder(Vector),
            crossEncoder,
            query,
            groupIds,
            config,
            new SearchFilters(),
            centerNodeUuid,
            bfsOriginNodeUuids,
            queryVector);

    [Fact]
    public async Task Blank_query_returns_empty_results_without_searching()
    {
        var driver = new FakeGraphDriver();
        var embedder = new FakeEmbedder(Vector);

        var results = await SearchAsync(driver, SearchConfigRecipes.CombinedHybridSearchRrf, " \n\t", embedder: embedder);

        Assert.Empty(results.Edges);
        Assert.Empty(driver.Search.Calls);
        Assert.Empty(embedder.Inputs);
    }

    [Fact]
    public async Task Query_is_embedded_once_with_newlines_replaced()
    {
        var driver = new FakeGraphDriver();
        var embedder = new FakeEmbedder(Vector);

        await SearchAsync(driver, SearchConfigRecipes.CombinedHybridSearchRrf, "line one\nline two", embedder: embedder);

        Assert.Equal(["line one line two"], embedder.Inputs);
        foreach (var method in new[]
                 {
                     "EdgeSimilaritySearchAsync", "NodeSimilaritySearchAsync", "CommunitySimilaritySearchAsync",
                 })
        {
            var call = Assert.Single(driver.Search.Calls, c => c.Method == method);
            Assert.Same(Vector, call.Args[0]);
        }
    }

    [Fact]
    public async Task Given_query_vector_skips_the_embedder()
    {
        var driver = new FakeGraphDriver();
        var embedder = new FakeEmbedder(Vector);
        float[] given = [1, 0];

        await SearchAsync(driver, SearchConfigRecipes.EdgeHybridSearchRrf, queryVector: given, embedder: embedder);

        Assert.Empty(embedder.Inputs);
        Assert.Same(given, Assert.Single(driver.Search.Calls, c => c.Method == "EdgeSimilaritySearchAsync").Args[0]);
    }

    [Fact]
    public async Task Fulltext_only_config_does_not_embed()
    {
        var driver = new FakeGraphDriver();
        var embedder = new FakeEmbedder(Vector);
        var config = new SearchConfig { EdgeConfig = new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.Bm25] } };

        await SearchAsync(driver, config, embedder: embedder);

        Assert.Empty(embedder.Inputs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Empty_group_ids_search_every_group(int variant)
    {
        string[]? groupIds = variant switch { 0 => null, 1 => [], _ => [""] };
        var driver = new FakeGraphDriver();

        await SearchAsync(driver, SearchConfigRecipes.EdgeHybridSearchRrf, groupIds: groupIds);

        Assert.All(driver.Search.Calls, call => Assert.Null(call.Args[2]));
    }

    [Fact]
    public async Task Methods_fetch_twice_the_limit_and_results_are_cut_to_the_limit()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ => [.. Enumerable.Range(0, 6).Select(i => Make.Edge($"e{i}", $"fact {i}"))];
        var config = SearchConfigRecipes.EdgeHybridSearchRrf with { Limit = 3 };

        var results = await SearchAsync(driver, config, groupIds: ["g1"]);

        Assert.Equal(["e0", "e1", "e2"], results.Edges.Select(e => e.Uuid));
        Assert.Equal([1.0, 0.5, 1.0 / 3], results.EdgeRerankerScores);
        var fulltext = Assert.Single(driver.Search.Calls, c => c.Method == "EdgeFulltextSearchAsync");
        Assert.Equal(["g1"], (IReadOnlyList<string>)fulltext.Args[2]!);
        Assert.Equal(6, fulltext.Args[3]);
        Assert.Equal(SearchDefaults.DefaultMinScore, driver.Search.Calls.Single(c => c.Method == "EdgeSimilaritySearchAsync").Args[4]);
    }

    [Fact]
    public async Task Reranker_min_score_drops_low_rrf_scores()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("a"), Make.Node("b"), Make.Node("c")];
        driver.Search.NodeSimilarity = _ => [Make.Node("c")];
        var config = SearchConfigRecipes.NodeHybridSearchRrf with { RerankerMinScore = 0.6 };

        var results = await SearchAsync(driver, config);

        // a = 1, c = 1/3 + 1, b = 1/2 (dropped).
        Assert.Equal(["c", "a"], results.Nodes.Select(n => n.Uuid));
    }

    [Fact]
    public async Task Bfs_without_origins_expands_from_the_other_methods_results()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ => [Make.Edge("e1", "f1", source: "s1"), Make.Edge("e2", "f2", source: "s2")];
        driver.Search.EdgeBfs = _ => [Make.Edge("e3", "f3")];
        var config = new SearchConfig
        {
            EdgeConfig = new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.Bfs] },
        };

        var results = await SearchAsync(driver, config);

        var bfs = Assert.Single(driver.Search.Calls, c => c.Method == "EdgeBfsSearchAsync");
        Assert.Equal(["s1", "s2"], (IReadOnlyList<string>)bfs.Args[0]!);
        Assert.Equal(SearchDefaults.MaxSearchDepth, bfs.Args[1]);
        Assert.Equal(["e1", "e3", "e2"], results.Edges.Select(e => e.Uuid));
    }

    [Fact]
    public async Task Bfs_with_origins_uses_them_and_does_not_expand()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("n1")];
        driver.Search.NodeBfs = _ => [Make.Node("n2")];
        var config = new SearchConfig
        {
            NodeConfig = new NodeSearchConfig { SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.Bfs] },
        };

        var results = await SearchAsync(driver, config, bfsOriginNodeUuids: ["origin"]);

        var bfs = Assert.Single(driver.Search.Calls, c => c.Method == "NodeBfsSearchAsync");
        Assert.Equal(["origin"], (IReadOnlyList<string>)bfs.Args[0]!);
        Assert.Equal(["n1", "n2"], results.Nodes.Select(n => n.Uuid));
    }

    [Fact]
    public async Task Empty_bfs_origins_disable_bfs()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("n1")];
        var config = new SearchConfig
        {
            NodeConfig = new NodeSearchConfig { SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.Bfs] },
        };

        await SearchAsync(driver, config, bfsOriginNodeUuids: []);

        Assert.Equal(0, driver.Search.CallCount("NodeBfsSearchAsync"));
    }

    [Fact]
    public async Task Node_distance_requires_a_centre_node()
    {
        var ex = await Assert.ThrowsAsync<SearchRerankerException>(() =>
            SearchAsync(new FakeGraphDriver(), SearchConfigRecipes.NodeHybridSearchNodeDistance));
        Assert.Equal("No center node provided for Node Distance reranker", ex.Message);

        await Assert.ThrowsAsync<SearchRerankerException>(() =>
            SearchAsync(new FakeGraphDriver(), SearchConfigRecipes.EdgeHybridSearchNodeDistance));
    }

    [Fact]
    public async Task Node_distance_ranks_nodes_by_closeness_to_the_centre()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("far"), Make.Node("near"), Make.Node("center")];
        driver.Search.Connections.Add(("center", "near"));

        var results = await SearchAsync(driver, SearchConfigRecipes.NodeHybridSearchNodeDistance, centerNodeUuid: "center");

        Assert.Equal(["center", "near", "far"], results.Nodes.Select(n => n.Uuid));
        Assert.Equal([10.0, 1.0, 0.0], results.NodeRerankerScores);
    }

    [Fact]
    public async Task Edge_node_distance_groups_facts_by_source_node()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ =>
        [
            Make.Edge("e1", "f1", source: "far"),
            Make.Edge("e2", "f2", source: "near"),
            Make.Edge("e3", "f3", source: "far"),
            Make.Edge("e4", "f4", source: "center"),
        ];
        driver.Search.Connections.Add(("near", "center"));

        var results = await SearchAsync(driver, SearchConfigRecipes.EdgeHybridSearchNodeDistance, centerNodeUuid: "center");

        Assert.Equal(["e4", "e2", "e1", "e3"], results.Edges.Select(e => e.Uuid));

        // As upstream, the scores are per source node.
        Assert.Equal([10.0, 1.0, 0.0], results.EdgeRerankerScores);
    }

    [Fact]
    public async Task Edge_episode_mentions_sorts_by_episode_count_and_keeps_rrf_scores()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ =>
        [
            Make.Edge("e1", "f1", episodes: ["ep1"]),
            Make.Edge("e2", "f2", episodes: ["ep1", "ep2", "ep3"]),
            Make.Edge("e3", "f3", episodes: ["ep1"]),
        ];

        var results = await SearchAsync(driver, SearchConfigRecipes.EdgeHybridSearchEpisodeMentions);

        Assert.Equal(["e2", "e1", "e3"], results.Edges.Select(e => e.Uuid));
        Assert.Equal([1.0, 0.5, 1.0 / 3], results.EdgeRerankerScores);
    }

    [Fact]
    public async Task Node_episode_mentions_uses_the_driver_counts()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("a"), Make.Node("b")];
        driver.Graph.MentionCounts["b"] = 4;
        driver.Graph.MentionCounts["a"] = 1;

        var results = await SearchAsync(driver, SearchConfigRecipes.NodeHybridSearchEpisodeMentions);

        Assert.Equal(["b", "a"], results.Nodes.Select(n => n.Uuid));
        Assert.Equal([4.0, 1.0], results.NodeRerankerScores);
    }

    [Fact]
    public async Task Mmr_loads_embeddings_and_leaves_results_without_them()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EdgeFulltext = _ => [Make.Edge("e1", "f1"), Make.Edge("e2", "f2"), Make.Edge("e3", "f3")];
        driver.EntityEdges.Embeddings["e1"] = [1, 0];
        driver.EntityEdges.Embeddings["e2"] = [0, 1];

        var results = await SearchAsync(driver, SearchConfigRecipes.EdgeHybridSearchMmr with { RerankerMinScore = -2 });

        // e3 has no embedding and drops out, as upstream (its id is not in the candidate map).
        Assert.Equal(["e2", "e1"], results.Edges.Select(e => e.Uuid));
        Assert.All(results.Edges, edge => Assert.Null(edge.FactEmbedding));
    }

    [Fact]
    public async Task Node_cross_encoder_ranks_names_and_applies_min_score()
    {
        var driver = new FakeGraphDriver();
        driver.Search.NodeFulltext = _ => [Make.Node("1", "Alice"), Make.Node("2", "Bob")];
        driver.Search.NodeSimilarity = _ => [Make.Node("3", "Carol")];
        var crossEncoder = new FakeCrossEncoder(name => name switch { "Bob" => 0.9, "Carol" => 0.5, _ => 0.1 });

        var results = await SearchAsync(
            driver,
            SearchConfigRecipes.NodeHybridSearchCrossEncoder with { RerankerMinScore = 0.2 },
            crossEncoder: crossEncoder);

        Assert.Equal(["2", "3"], results.Nodes.Select(n => n.Uuid));
        Assert.Equal([0.9, 0.5], results.NodeRerankerScores);
    }

    [Fact]
    public async Task Cross_encoder_reranker_without_a_cross_encoder_throws()
    {
        var driver = new FakeGraphDriver();

        await Assert.ThrowsAsync<SearchRerankerException>(() =>
            SearchAsync(driver, SearchConfigRecipes.CommunityHybridSearchCrossEncoder));
    }

    [Fact]
    public async Task Episode_cross_encoder_ranks_only_the_top_limit_contents()
    {
        var driver = new FakeGraphDriver();
        driver.Search.EpisodeFulltext = _ =>
            [.. Enumerable.Range(0, 4).Select(i => Make.Episode($"ep{i}", $"content {i}"))];
        var crossEncoder = new FakeCrossEncoder(content => content == "content 1" ? 1 : 0.5);
        var config = new SearchConfig
        {
            EpisodeConfig = new EpisodeSearchConfig
            {
                SearchMethods = [EpisodeSearchMethod.Bm25],
                Reranker = EpisodeReranker.CrossEncoder,
            },
            Limit = 2,
        };

        var results = await SearchAsync(driver, config, crossEncoder: crossEncoder);

        Assert.Equal(["content 0", "content 1"], crossEncoder.RankedPassages);
        Assert.Equal(["ep1", "ep0"], results.Episodes.Select(e => e.Uuid));
    }

    [Fact]
    public async Task Community_search_always_runs_fulltext_and_similarity()
    {
        var driver = new FakeGraphDriver();
        driver.Search.CommunityFulltext = _ => [Make.Community("c1", "one")];
        driver.Search.CommunitySimilarity = _ => [Make.Community("c2", "two"), Make.Community("c1", "one")];
        var config = new SearchConfig
        {
            CommunityConfig = new CommunitySearchConfig { SearchMethods = [CommunitySearchMethod.Bm25] },
        };

        var results = await SearchAsync(driver, config);

        Assert.Equal(1, driver.Search.CallCount("CommunitySimilaritySearchAsync"));

        // Without cosine similarity in the config, the query is not embedded and the vector is all zeros.
        var vector = (IReadOnlyList<float>)driver.Search.Calls
            .Single(c => c.Method == "CommunitySimilaritySearchAsync").Args[0]!;
        Assert.All(vector, value => Assert.Equal(0f, value));
        Assert.Equal(["c1", "c2"], results.Communities.Select(c => c.Uuid));
    }

    [Fact]
    public void Recipes_are_fresh_immutable_copies()
    {
        var first = SearchConfigRecipes.EdgeHybridSearchRrf;
        var changed = first with { Limit = 50 };

        Assert.Equal(10, SearchConfigRecipes.EdgeHybridSearchRrf.Limit);
        Assert.Equal(50, changed.Limit);
        Assert.NotSame(first, SearchConfigRecipes.EdgeHybridSearchRrf);
        Assert.Equal(3, SearchConfigRecipes.CommunityHybridSearchCrossEncoder.Limit);
        Assert.Equal(1, SearchConfigRecipes.CombinedHybridSearchMmr.EdgeConfig!.MmrLambda);
        Assert.Equal(
            [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity, EdgeSearchMethod.Bfs],
            SearchConfigRecipes.CombinedHybridSearchCrossEncoder.EdgeConfig!.SearchMethods);
        Assert.Null(SearchConfigRecipes.NodeHybridSearchRrf.EdgeConfig);
        Assert.Equal(0, SearchConfigRecipes.NodeHybridSearchRrf.RerankerMinScore);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<EdgeSearchMethod>)SearchConfigRecipes.EdgeHybridSearchRrf.EdgeConfig!.SearchMethods).Clear());
    }

    [Fact]
    public void Merge_concatenates_every_scope()
    {
        var a = new SearchResults { Edges = [Make.Edge("e1", "f")], EdgeRerankerScores = [1], Nodes = [Make.Node("n1")] };
        var b = new SearchResults { Edges = [Make.Edge("e2", "f")], EdgeRerankerScores = [0.5], Communities = [Make.Community("c", "c")] };

        var merged = SearchResults.Merge([a, b]);

        Assert.Equal(["e1", "e2"], merged.Edges.Select(e => e.Uuid));
        Assert.Equal([1.0, 0.5], merged.EdgeRerankerScores);
        Assert.Single(merged.Nodes);
        Assert.Single(merged.Communities);
        Assert.Empty(SearchResults.Merge([]).Edges);
    }

    [Fact]
    public void Context_string_matches_upstream_format()
    {
        var edge = Make.Edge("e1", "Alice works at \"Zep\"");
        edge.ValidAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var node = Make.Node("n1", "Alice");
        node.Summary = "Engineer – café";
        var results = new SearchResults
        {
            Edges = [edge],
            Nodes = [node],
            Episodes = [Make.Episode("ep", "line1\nline2")],
        };

        var context = SearchHelpers.SearchResultsToContextString(results);

        const string expected = "\n"
            + "    FACTS and ENTITIES represent relevant context to the current conversation.\n"
            + "    COMMUNITIES represent a cluster of closely related entities.\n"
            + "\n"
            + "    These are the most relevant facts and their valid and invalid dates. Facts are considered valid\n"
            + "    between their valid_at and invalid_at dates. Facts with an invalid_at date of \"Present\" are considered valid.\n"
            + "    <FACTS>\n"
            + "            [{\"fact\": \"Alice works at \\\"Zep\\\"\", \"valid_at\": \"2024-01-02 03:04:05+00:00\", \"invalid_at\": \"Present\"}]\n"
            + "    </FACTS>\n"
            + "    <ENTITIES>\n"
            + "            [{\"entity_name\": \"Alice\", \"summary\": \"Engineer – café\"}]\n"
            + "    </ENTITIES>\n"
            + "    <EPISODES>\n"
            + "            [{\"source_description\": \"test\", \"content\": \"line1\\nline2\"}]\n"
            + "    </EPISODES>\n"
            + "    <COMMUNITIES>\n"
            + "            []\n"
            + "    </COMMUNITIES>\n";
        Assert.Equal(expected, context);
    }

    [Fact]
    public void Edge_date_range_uses_python_datetime_text()
    {
        var edge = Make.Edge("e1", "f");
        Assert.Equal("date unknown - present", SearchHelpers.FormatEdgeDateRange(edge));

        edge.ValidAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);
        edge.InvalidAt = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        Assert.Equal(
            "2024-01-02 03:04:05.123456+00:00 - 2024-05-06 07:08:09+00:00",
            SearchHelpers.FormatEdgeDateRange(edge));
    }
}
