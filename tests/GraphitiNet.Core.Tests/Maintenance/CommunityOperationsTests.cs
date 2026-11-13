// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_remove_communities.py and the community tests of
// tests/test_graphiti_mock.py (test_determine_entity_community, test_get_community_clusters) (Graphiti v0.31.2) and
// changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Tests.GraphitiApi;
using static GraphitiNet.Tests.GraphitiApi.GraphitiFixture;

namespace GraphitiNet.Tests.Maintenance;

public class CommunityOperationsTests
{
    private static Dictionary<string, IReadOnlyList<Neighbor>> Projection(
        params (string Node, (string Neighbor, int Count)[] Neighbors)[] entries) =>
        entries.ToDictionary(
            entry => entry.Node,
            entry => (IReadOnlyList<Neighbor>)entry.Neighbors.Select(n => new Neighbor(n.Neighbor, n.Count)).ToList());

    private static List<HashSet<string>> AsSets(IReadOnlyList<IReadOnlyList<string>> clusters) =>
        clusters.Select(cluster => cluster.ToHashSet()).ToList();

    [Fact]
    public void Label_propagation_keeps_isolated_nodes_apart()
    {
        var clusters = CommunityOperations.LabelPropagation(Projection(("a", []), ("b", []), ("c", [])));
        Assert.Equal(3, clusters.Count);
    }

    [Fact]
    public void Label_propagation_joins_a_connected_pair_into_one_cluster()
    {
        var clusters = CommunityOperations.LabelPropagation(Projection(("a", [("b", 1)]), ("b", [("a", 1)])));
        Assert.Equal([new HashSet<string> { "a", "b" }], AsSets(clusters));
    }

    [Fact]
    public void Label_propagation_separates_two_components()
    {
        var clusters = CommunityOperations.LabelPropagation(Projection(
            ("a", [("b", 1)]),
            ("b", [("a", 1)]),
            ("c", [("d", 1)]),
            ("d", [("c", 1)])));
        var sets = AsSets(clusters);
        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, set => set.SetEquals(["a", "b"]));
        Assert.Contains(sets, set => set.SetEquals(["c", "d"]));
    }

    [Fact]
    public void Label_propagation_triangle_converges_on_the_highest_id()
    {
        var clusters = CommunityOperations.LabelPropagation(Projection(
            ("a", [("b", 1), ("c", 1)]),
            ("b", [("a", 1), ("c", 1)]),
            ("c", [("a", 1), ("b", 1)])));
        Assert.Equal([new HashSet<string> { "a", "b", "c" }], AsSets(clusters));
    }

    [Fact]
    public void Label_propagation_stops_on_an_oscillating_pair()
    {
        // Two facts between a and b make them swap communities every round; upstream never returns here.
        var clusters = CommunityOperations.LabelPropagation(Projection(("a", [("b", 2)]), ("b", [("a", 2)])));
        Assert.Equal(2, clusters.Sum(cluster => cluster.Count));
    }

    [Fact]
    public void Label_propagation_star_joins_the_hub()
    {
        // Every leaf has one edge to the hub; the leaves take the higher community id and converge.
        var clusters = CommunityOperations.LabelPropagation(Projection(
            ("hub", [("x", 1), ("y", 1), ("z", 1)]),
            ("x", [("hub", 1)]),
            ("y", [("hub", 1)]),
            ("z", [("hub", 1)])));
        Assert.Equal([new HashSet<string> { "hub", "x", "y", "z" }], AsSets(clusters));
    }

    [Fact]
    public async Task Remove_communities_scoped_and_unscoped()
    {
        await using var driver = new InMemoryGraphDriver();
        var a = new CommunityNode { Name = "a", GroupId = "group_a", CreatedAt = Now };
        var b = new CommunityNode { Name = "b", GroupId = "group_b", CreatedAt = Now };
        var c = new CommunityNode { Name = "c", GroupId = "group_c", CreatedAt = Now };
        await driver.CommunityNodeOps.SaveBulkAsync([a, b, c]);

        await CommunityOperations.RemoveCommunitiesAsync(driver, ["group_a", "group_b"]);
        Assert.Equal([c.Uuid], (await driver.CommunityNodeOps.GetByGroupIdsAsync(["group_a", "group_b", "group_c"]))
            .Select(n => n.Uuid));

        // An empty list means no scoping, as upstream: every community goes.
        await CommunityOperations.RemoveCommunitiesAsync(driver, []);
        Assert.Empty(await driver.CommunityNodeOps.GetByGroupIdsAsync(["group_c"]));

        await driver.CommunityNodeOps.SaveAsync(a);
        await CommunityOperations.RemoveCommunitiesAsync(driver);
        Assert.Empty(await driver.CommunityNodeOps.GetByGroupIdsAsync(["group_a"]));
    }

    [Fact]
    public async Task Determine_entity_community()
    {
        await using var driver = new InMemoryGraphDriver();
        var group = NewGroup();
        var e1 = Entity(group, "test_entity_1");
        var e2 = Entity(group, "test_entity_2");
        var e3 = Entity(group, "test_entity_3");
        var e4 = Entity(group, "test_entity_4");
        await SaveAsync(driver, [], [], [e1, e2, e3, e4], [
            Fact(group, e1, e4, "test_entity_1 relates to test_entity_4"),
            Fact(group, e2, e4, "test_entity_2 relates to test_entity_4"),
            Fact(group, e3, e4, "test_entity_3 relates to test_entity_4"),
        ]);
        var c1 = new CommunityNode { Name = "test_community_1", GroupId = group, CreatedAt = Now };
        var c2 = new CommunityNode { Name = "test_community_2", GroupId = group, CreatedAt = Now };
        await driver.CommunityNodeOps.SaveBulkAsync([c1, c2]);
        await driver.CommunityEdgeOps.SaveBulkAsync([
            new CommunityEdge { GroupId = group, SourceNodeUuid = c1.Uuid, TargetNodeUuid = e1.Uuid, CreatedAt = Now },
            new CommunityEdge { GroupId = group, SourceNodeUuid = c1.Uuid, TargetNodeUuid = e2.Uuid, CreatedAt = Now },
            new CommunityEdge { GroupId = group, SourceNodeUuid = c2.Uuid, TargetNodeUuid = e3.Uuid, CreatedAt = Now },
        ]);

        var (community, isNew) = await CommunityOperations.DetermineEntityCommunityAsync(driver, e4);
        Assert.Equal("test_community_1", community!.Name);
        Assert.True(isNew);

        await driver.CommunityEdgeOps.SaveAsync(
            new CommunityEdge { GroupId = group, SourceNodeUuid = c1.Uuid, TargetNodeUuid = e4.Uuid, CreatedAt = Now });
        (community, isNew) = await CommunityOperations.DetermineEntityCommunityAsync(driver, e4);
        Assert.Equal("test_community_1", community!.Name);
        Assert.False(isNew);

        var loner = Entity(group, "loner");
        await SaveAsync(driver, [], [], [loner], []);
        Assert.Equal((null, false), await CommunityOperations.DetermineEntityCommunityAsync(driver, loner));

        await CommunityOperations.RemoveCommunitiesAsync(driver);
        Assert.Empty(await driver.CommunityNodeOps.GetByUuidsAsync([c1.Uuid, c2.Uuid]));
    }

    [Fact]
    public async Task Community_clusters_stay_within_groups()
    {
        await using var driver = new InMemoryGraphDriver();
        var group1 = NewGroup();
        var group2 = NewGroup();
        var e1 = Entity(group1, "test_entity_1");
        var e2 = Entity(group1, "test_entity_2");
        var e3 = Entity(group2, "test_entity_3");
        var e4 = Entity(group2, "test_entity_4");
        await SaveAsync(driver, [], [], [e1, e2, e3, e4], [
            Fact(group1, e1, e2, "test_entity_1 relates to test_entity_2"),
            Fact(group2, e3, e4, "test_entity_3 relates to test_entity_4"),
        ]);

        var clusters = await CommunityOperations.GetCommunityClustersAsync(driver, null);

        var names = clusters.Select(cluster => cluster.Select(n => n.Name).ToHashSet()).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(names, set => set.SetEquals(["test_entity_1", "test_entity_2"]));
        Assert.Contains(names, set => set.SetEquals(["test_entity_3", "test_entity_4"]));

        Assert.Single(await CommunityOperations.GetCommunityClustersAsync(driver, [group1]));
    }

    [Fact]
    public async Task Build_community_merges_summaries_pairwise_and_names_it()
    {
        var llm = new ScriptedLlmClient(call => call.Text.Contains("one sentence description", StringComparison.Ordinal)
            ? """{"description": "Community name"}"""
            : """{"summary": "Merged."}""");
        await using var driver = new InMemoryGraphDriver();
        var clients = new GraphitiClients { Driver = driver, LlmClient = llm, Embedder = new HashEmbedder() };
        var group = NewGroup();
        var members = Enumerable.Range(0, 5).Select(i => Entity(group, $"e{i}", $"Summary {i}.")).ToList();

        var (community, edges) = await CommunityOperations.BuildCommunityAsync(clients, members);

        // 5 summaries: 2 pairs (+1 odd), then 1 pair (+1 odd), then 1 pair = 4 merges, then 1 description.
        Assert.Equal(5, llm.Calls.Count);
        Assert.Equal("Community name", community.Name);
        Assert.Equal("Merged.", community.Summary);
        Assert.Equal(["Community"], community.Labels);
        Assert.Equal(group, community.GroupId);
        Assert.Equal(members.Select(m => m.Uuid), edges.Select(e => e.TargetNodeUuid));
        Assert.All(edges, e => Assert.Equal(community.Uuid, e.SourceNodeUuid));
    }

    [Fact]
    public async Task Update_community_folds_a_new_member_in()
    {
        var llm = new ScriptedLlmClient(call => call.Text.Contains("one sentence description", StringComparison.Ordinal)
            ? """{"description": "Renamed"}"""
            : """{"summary": "Merged summary."}""");
        await using var driver = new InMemoryGraphDriver();
        var embedder = new HashEmbedder();
        var clients = new GraphitiClients { Driver = driver, LlmClient = llm, Embedder = embedder };
        var group = NewGroup();
        var member = Entity(group, "member");
        var newcomer = Entity(group, "newcomer", "New summary.");
        await SaveAsync(driver, [], [], [member, newcomer], [Fact(group, newcomer, member, "newcomer knows member")]);
        var community = new CommunityNode { Name = "old", GroupId = group, CreatedAt = Now, Summary = "Old." };
        await driver.CommunityNodeOps.SaveAsync(community);
        await driver.CommunityEdgeOps.SaveAsync(new CommunityEdge
        {
            GroupId = group, SourceNodeUuid = community.Uuid, TargetNodeUuid = member.Uuid, CreatedAt = Now,
        });

        var (communities, edges) = await CommunityOperations.UpdateCommunityAsync(clients, newcomer);

        var updated = Assert.Single(communities);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("Merged summary.", updated.Summary);
        Assert.Equal(newcomer.Uuid, Assert.Single(edges).TargetNodeUuid);
        var stored = await driver.CommunityNodeOps.GetByUuidAsync(community.Uuid);
        Assert.Equal("Renamed", stored.Name);
        await driver.CommunityNodeOps.LoadNameEmbeddingAsync(stored);
        Assert.NotNull(stored.NameEmbedding);

        var loner = Entity(group, "loner");
        await SaveAsync(driver, [], [], [loner], []);
        var (none, noEdges) = await CommunityOperations.UpdateCommunityAsync(clients, loner);
        Assert.Empty(none);
        Assert.Empty(noEdges);
    }
}
