// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/search/test_episode_mentions_reranker.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

/// <summary>Regression coverage for upstream #1342: the reranker must sort by descending mention count.</summary>
public class EpisodeMentionsRerankerTests
{
    private static FakeGraphDriver DriverWithCounts(params (string Uuid, int Count)[] counts)
    {
        var driver = new FakeGraphDriver();
        foreach (var (uuid, count) in counts)
        {
            driver.Graph.MentionCounts[uuid] = count;
        }

        return driver;
    }

    [Fact]
    public async Task Orders_by_descending_mentions()
    {
        var driver = DriverWithCounts(("low", 1), ("high", 10), ("mid", 5));

        var (uuids, scores) = await SearchUtils.EpisodeMentionsRerankerAsync(driver, [["low", "high", "mid"]]);

        Assert.Equal(["high", "mid", "low"], uuids);
        Assert.Equal([10.0, 5.0, 1.0], scores);
    }

    [Fact]
    public async Task Unmentioned_nodes_rank_last()
    {
        var driver = DriverWithCounts(("mentioned", 3));

        var (uuids, scores) = await SearchUtils.EpisodeMentionsRerankerAsync(
            driver, [["mentioned", "unmentioned"]], minScore: 0);

        Assert.Equal(["mentioned", "unmentioned"], uuids);
        Assert.Equal([3.0, 0.0], scores);
    }

    [Fact]
    public async Task Min_score_filters_unmentioned_nodes()
    {
        var driver = DriverWithCounts(("mentioned", 3));

        var (uuids, scores) = await SearchUtils.EpisodeMentionsRerankerAsync(
            driver, [["mentioned", "unmentioned"]], minScore: 1);

        Assert.Equal(["mentioned"], uuids);
        Assert.Equal([3.0], scores);
    }

    [Fact]
    public async Task All_unmentioned_returns_empty_when_filtered()
    {
        var driver = DriverWithCounts();

        var (uuids, scores) = await SearchUtils.EpisodeMentionsRerankerAsync(driver, [["a", "b"]], minScore: 1);

        Assert.Empty(uuids);
        Assert.Empty(scores);
    }

    [Fact]
    public async Task Ties_keep_the_rrf_order()
    {
        var driver = DriverWithCounts(("a", 2), ("b", 2), ("c", 2));

        var (uuids, _) = await SearchUtils.EpisodeMentionsRerankerAsync(driver, [["c", "a"], ["b", "a"]]);

        // RRF: a = 1/2 + 1/2 = 1, c = 1, b = 1; ties keep first-seen order (c, a, b) and then a stable sort.
        Assert.Equal(["c", "a", "b"], uuids);
        Assert.Equal(["c", "a", "b"], Assert.Single(driver.Graph.CountEpisodeMentionsCalls));
    }
}
