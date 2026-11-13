// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.CrossEncoder;

namespace GraphitiNet.Tests.CrossEncoder;

/// <summary>tekthar/graphiti-net#6: a cross-encoder that keeps the given order.</summary>
public class PassThroughCrossEncoderClientTests
{
    [Fact]
    public async Task Keeps_the_given_order_and_scores_fall_with_rank()
    {
        string[] passages = ["zebra", "apple", "mango", "apple"];

        var ranked = await PassThroughCrossEncoderClient.Instance.RankAsync("apple", passages);

        Assert.Equal(passages, ranked.Select(r => r.Passage));
        Assert.Equal([1.0, 1.0 / 2, 1.0 / 3, 1.0 / 4], ranked.Select(r => r.Score));
        for (var i = 1; i < ranked.Count; i++)
        {
            Assert.True(ranked[i].Score < ranked[i - 1].Score);
        }
    }

    [Fact]
    public async Task No_passages_rank_to_nothing()
    {
        Assert.Empty(await new PassThroughCrossEncoderClient().RankAsync("query", []));
    }
}
