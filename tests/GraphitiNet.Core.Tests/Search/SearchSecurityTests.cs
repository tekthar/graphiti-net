// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/search/test_search_security.py (Graphiti v0.31.2) and changed for .NET. The query-builder
// tests (Neo4j, FalkorDB) belong to the drivers and are not ported here.

using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

public class SearchSecurityTests
{
    [Fact]
    public void Search_filters_reject_unsafe_node_labels()
    {
        var ex = Assert.Throws<NodeLabelValidationException>(() =>
            new SearchFilters { NodeLabels = ["Entity`) WITH n MATCH (x) DETACH DELETE x //"] });
        Assert.StartsWith("node_labels must start with a letter or underscore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_search_rejects_invalid_group_ids()
    {
        var driver = new FakeGraphDriver();

        var ex = await Assert.ThrowsAsync<GroupIdValidationException>(() => SearchEngine.SearchAsync(
            driver,
            new FakeEmbedder([1f]),
            null,
            "test",
            ["bad\"group"],
            new SearchConfig(),
            new SearchFilters()));

        Assert.Contains("must contain only alphanumeric", ex.Message, StringComparison.Ordinal);
        Assert.Empty(driver.Search.Calls);
    }

    [Fact]
    public async Task Group_ids_are_validated_even_for_a_blank_query()
    {
        await Assert.ThrowsAsync<GroupIdValidationException>(() => SearchEngine.SearchAsync(
            new FakeGraphDriver(),
            new FakeEmbedder([1f]),
            null,
            "   ",
            ["bad group"],
            SearchConfigRecipes.CombinedHybridSearchRrf,
            new SearchFilters()));
    }

    [Fact]
    public async Task Overlong_fulltext_queries_are_not_sent_to_the_driver()
    {
        var driver = new FakeGraphDriver();
        var query = string.Join(' ', Enumerable.Repeat("word", SearchUtils.MaxQueryLength));

        var results = await SearchEngine.SearchAsync(
            driver,
            new FakeEmbedder([1f]),
            null,
            query,
            null,
            SearchConfigRecipes.CombinedHybridSearchRrf,
            new SearchFilters());

        Assert.Empty(results.Edges);
        Assert.Equal(0, driver.Search.CallCount("EdgeFulltextSearchAsync"));
        Assert.Equal(0, driver.Search.CallCount("NodeFulltextSearchAsync"));
        Assert.Equal(0, driver.Search.CallCount("EpisodeFulltextSearchAsync"));
        Assert.Equal(0, driver.Search.CallCount("CommunityFulltextSearchAsync"));
        Assert.Equal(1, driver.Search.CallCount("EdgeSimilaritySearchAsync"));
    }
}
