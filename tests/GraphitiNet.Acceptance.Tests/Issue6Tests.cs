// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.CrossEncoder;
using GraphitiNet.Search;
using static GraphitiNet.Acceptance.Tests.Support;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>tekthar/graphiti-net#6: a cross-encoder that leaves the reciprocal rank fusion order unchanged.</summary>
public class Issue6Tests
{
    private static readonly ScriptedEpisode[] Episodes =
    [
        new("Alice works at Acme.", ["Alice", "Acme"], [new("Alice", "WORKS_AT", "Acme", "Alice works at Acme.")]),
        new("Bob works at Globex with Alice.", ["Bob", "Globex"],
            [new("Bob", "WORKS_AT", "Globex", "Bob works at Globex.")]),
        new("Carol works in Oslo near Acme.", ["Carol", "Oslo"],
            [new("Carol", "WORKS_IN", "Oslo", "Carol works in Oslo.")]),
        new("Dave visits Alice in Berlin.", ["Dave", "Berlin"],
            [new("Dave", "VISITS", "Berlin", "Dave visits Berlin to see Alice.")]),
    ];

    private static SearchConfig Config(bool crossEncoder) => new()
    {
        EdgeConfig = new EdgeSearchConfig
        {
            SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity],
            Reranker = crossEncoder ? EdgeReranker.CrossEncoder : EdgeReranker.Rrf,
        },
        // One method: entities reach the cross-encoder in the order search found them, as upstream.
        NodeConfig = new NodeSearchConfig
        {
            SearchMethods = [NodeSearchMethod.Bm25],
            Reranker = crossEncoder ? NodeReranker.CrossEncoder : NodeReranker.Rrf,
        },
        EpisodeConfig = new EpisodeSearchConfig
        {
            SearchMethods = [EpisodeSearchMethod.Bm25],
            Reranker = crossEncoder ? EpisodeReranker.CrossEncoder : EpisodeReranker.Rrf,
        },
        Limit = 3,
    };

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task The_cross_encoder_reranker_with_the_pass_through_client_gives_the_rrf_order(string driver)
    {
        await using var h = await Harness.CreateAsync(
            driver, new ScriptedLlm(Episodes), crossEncoder: PassThroughCrossEncoderClient.Instance);
        var group = Harness.NewGroup();
        foreach (var episode in Episodes)
        {
            await h.AddAsync(group, episode.Body, Jan);
        }

        const string query = "Alice works at Acme with Bob in Oslo";
        var rrf = await h.Graphiti.SearchAsync(query, Config(crossEncoder: false), [group]);
        var crossEncoded = await h.Graphiti.SearchAsync(query, Config(crossEncoder: true), [group]);

        // Enough results that the order is tested, and the limit cuts the candidates.
        Assert.Equal(3, rrf.Edges.Count);
        Assert.Equal(3, rrf.Nodes.Count);
        Assert.Equal(3, rrf.Episodes.Count);
        Assert.Equal(rrf.Edges.Select(e => e.Uuid), crossEncoded.Edges.Select(e => e.Uuid));
        Assert.Equal(rrf.Nodes.Select(n => n.Uuid), crossEncoded.Nodes.Select(n => n.Uuid));
        Assert.Equal(rrf.Episodes.Select(e => e.Uuid), crossEncoded.Episodes.Select(e => e.Uuid));

        // The scores fall with rank.
        Assert.Equal([1.0, 1.0 / 2, 1.0 / 3], crossEncoded.EdgeRerankerScores);
    }
}
