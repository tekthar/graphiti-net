// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

// Clean-up, schema and community helpers.
public abstract partial class GraphDriverConformanceTests
{
    private async Task<string> SaveOneOfEverything(string group)
    {
        var episode = Episode(group, "episode");
        var entity = Entity(group, "entity");
        var other = Entity(group, "other");
        var community = Community(group, "community");
        var saga = Saga(group, "saga");
        await Save(episode);
        await Save(entity, other);
        await Save(community);
        await Save(saga);
        await Save(Mention(group, episode, entity));
        await Save(Fact(group, entity, other));
        await Save(Member(group, community, entity));
        await Save(HasEpisode(group, saga, episode));
        var next = Episode(group, "next");
        await Save(next);
        await Save(NextEpisode(group, episode, next));
        return group;
    }

    private async Task<int> CountRecords(string group) =>
        (await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.EntityNodeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.CommunityNodeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.SagaNodeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.EpisodicEdgeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.EntityEdgeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([group])).Count
        + (await Driver.NextEpisodeEdgeOps.GetByGroupIdsAsync([group])).Count;

    [Fact]
    public async Task Clear_data_removes_every_record_in_the_given_groups()
    {
        var group = await SaveOneOfEverything(NewGroup());
        var second = await SaveOneOfEverything(NewGroup());
        var kept = await SaveOneOfEverything(NewGroup());
        Assert.Equal(11, await CountRecords(group));

        await Driver.GraphOps.ClearDataAsync([group, second]);

        Assert.Equal(0, await CountRecords(group));
        Assert.Equal(0, await CountRecords(second));
        Assert.Equal(11, await CountRecords(kept));
        await Driver.GraphOps.ClearDataAsync([]);
        Assert.Equal(11, await CountRecords(kept));
    }

    [Fact]
    public async Task Clear_data_with_no_groups_removes_everything()
    {
        if (!IsIsolated)
        {
            return;
        }

        var group = await SaveOneOfEverything(NewGroup());
        var other = await SaveOneOfEverything(NewGroup());

        await Driver.GraphOps.ClearDataAsync();

        Assert.Equal(0, await CountRecords(group));
        Assert.Equal(0, await CountRecords(other));
        Assert.Empty(await Driver.GraphOps.GetEntityGroupIdsAsync());
    }

    [Fact]
    public async Task Build_indices_and_constraints_is_repeatable_and_keeps_data()
    {
        var group = await SaveOneOfEverything(NewGroup());
        await Driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await Driver.GraphOps.BuildIndicesAndConstraintsAsync();
        Assert.Equal(11, await CountRecords(group));
    }

    [Fact]
    public async Task Entity_projection_counts_facts_between_neighbours_in_the_group()
    {
        var group = NewGroup();
        var a = Entity(group, "A");
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        var lonely = Entity(group, "lonely");
        var stranger = Entity(NewGroup(), "stranger");
        await Save(a, b, c, lonely, stranger);
        await Save(Fact(group, a, b), Fact(group, b, a), Fact(group, a, c), Fact(group, a, stranger));
        var episode = Episode(group, "episode");
        await Save(episode);
        await Save(Mention(group, episode, lonely));

        var projection = await Driver.GraphOps.GetEntityProjectionAsync(group);

        AssertSameSet([a.Uuid, b.Uuid, c.Uuid, lonely.Uuid], projection.Keys);
        AssertNeighbours([new(b.Uuid, 2), new(c.Uuid, 1)], projection[a.Uuid]);
        AssertNeighbours([new(a.Uuid, 2)], projection[b.Uuid]);
        AssertNeighbours([new(a.Uuid, 1)], projection[c.Uuid]);
        Assert.Empty(projection[lonely.Uuid]);
        Assert.Empty(await Driver.GraphOps.GetEntityProjectionAsync(NewGroup()));

        static void AssertNeighbours(IEnumerable<Neighbor> expected, IEnumerable<Neighbor> actual) =>
            Assert.Equal(
                expected.OrderBy(n => n.NodeUuid, StringComparer.Ordinal),
                actual.OrderBy(n => n.NodeUuid, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Entity_group_ids_are_the_distinct_groups_with_entities()
    {
        var group = NewGroup();
        var other = NewGroup();
        var episodesOnly = NewGroup();
        await Save(Entity(group, "a"), Entity(group, "b"), Entity(other, "c"));
        await Save(Episode(episodesOnly, "episode"));
        await Save(Community(episodesOnly, "community"));

        var groupIds = await Driver.GraphOps.GetEntityGroupIdsAsync();

        Assert.Single(groupIds, id => id == group);
        Assert.Single(groupIds, id => id == other);
        Assert.DoesNotContain(episodesOnly, groupIds);
        if (IsIsolated)
        {
            AssertSameSet([group, other], groupIds);
        }
    }

    [Fact]
    public async Task Remove_communities_removes_communities_and_memberships_in_the_given_groups()
    {
        var group = NewGroup();
        var other = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(other, "Bob");
        await Save(alice, bob);
        var community = Community(group, "c1");
        var otherCommunity = Community(other, "c2");
        await Save(community, otherCommunity);
        await Save(Member(group, community, alice), Member(other, otherCommunity, bob));

        await Driver.GraphOps.RemoveCommunitiesAsync([group]);

        Assert.Empty(await Driver.CommunityNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Single(await Driver.CommunityNodeOps.GetByGroupIdsAsync([other]));
        Assert.Single(await Driver.CommunityEdgeOps.GetByGroupIdsAsync([other]));
        await Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid);
    }

    [Fact]
    public async Task Remove_communities_with_no_or_empty_groups_removes_every_community()
    {
        if (!IsIsolated)
        {
            return;
        }

        var group = NewGroup();
        var other = NewGroup();
        var alice = Entity(group, "Alice");
        await Save(alice);
        var community = Community(group, "c1");
        await Save(community, Community(other, "c2"));
        await Save(Member(group, community, alice));

        await Driver.GraphOps.RemoveCommunitiesAsync();
        Assert.Empty(await Driver.CommunityNodeOps.GetByGroupIdsAsync([group, other]));
        Assert.Empty(await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));

        await Save(Community(group, "c3"));
        await Driver.GraphOps.RemoveCommunitiesAsync([]);
        Assert.Empty(await Driver.CommunityNodeOps.GetByGroupIdsAsync([group]));
        await Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid);
    }

    [Fact]
    public async Task Mentioned_nodes_are_the_distinct_entities_the_episodes_mention()
    {
        var group = NewGroup();
        var e1 = Episode(group, "one");
        var e2 = Episode(group, "two");
        var e3 = Episode(group, "three");
        await Save(e1, e2, e3);
        var a = Entity(group, "A", embedding: Vec(1, 0, 0));
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        var d = Entity(group, "D");
        await Save(a, b, c, d);
        await Save(Mention(group, e1, a), Mention(group, e1, b), Mention(group, e2, b), Mention(group, e2, c), Mention(group, e3, d));

        var mentioned = await Driver.GraphOps.GetMentionedNodesAsync([e1, e2]);

        AssertSameNodes([a, b, c], mentioned);
        AssertEntity(a, mentioned.Single(n => n.Uuid == a.Uuid));
        Assert.Empty(await Driver.GraphOps.GetMentionedNodesAsync([]));
    }

    [Fact]
    public async Task Communities_by_nodes_are_the_distinct_communities_of_those_members()
    {
        var group = NewGroup();
        var a = Entity(group, "A");
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        var d = Entity(group, "D");
        await Save(a, b, c, d);
        var c1 = Community(group, "c1", Vec(1, 0, 0));
        var c2 = Community(group, "c2");
        var c3 = Community(group, "c3");
        await Save(c1, c2, c3);
        await Save(Member(group, c1, a), Member(group, c1, b), Member(group, c2, c), Member(group, c3, d));

        var communities = await Driver.GraphOps.GetCommunitiesByNodesAsync([a, b]);
        AssertSameNodes([c1], communities);
        AssertCommunity(c1, communities[0]);
        AssertSameNodes([c1, c2], await Driver.GraphOps.GetCommunitiesByNodesAsync([a, c]));
        Assert.Empty(await Driver.GraphOps.GetCommunitiesByNodesAsync([]));
    }

    [Fact]
    public async Task Neighbor_communities_appear_once_per_neighbouring_member()
    {
        var group = NewGroup();
        var x = Entity(group, "X");
        var a = Entity(group, "A");
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        var far = Entity(group, "far");
        await Save(x, a, b, c, far);

        // A is joined to X twice, in both directions; it still counts once.
        await Save(Fact(group, x, a), Fact(group, a, x), Fact(group, b, x), Fact(group, x, c), Fact(group, c, far));
        var c1 = Community(group, "c1");
        var c2 = Community(group, "c2");
        var c3 = Community(group, "c3");
        await Save(c1, c2, c3);
        await Save(Member(group, c1, a), Member(group, c1, b), Member(group, c2, c), Member(group, c3, far));

        var communities = await Driver.GraphOps.GetNeighborCommunitiesAsync(x.Uuid);

        Assert.Equal(
            new[] { c1.Uuid, c1.Uuid, c2.Uuid }.Order(StringComparer.Ordinal),
            communities.Select(n => n.Uuid).Order(StringComparer.Ordinal));
        AssertCommunity(c2, communities.First(n => n.Uuid == c2.Uuid));
        Assert.Empty(await Driver.GraphOps.GetNeighborCommunitiesAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task Episode_mentions_are_counted_per_entity()
    {
        var group = NewGroup();
        var e1 = Episode(group, "one");
        var e2 = Episode(group, "two");
        await Save(e1, e2);
        var a = Entity(group, "A");
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        await Save(a, b, c);
        await Save(Mention(group, e1, a), Mention(group, e2, a), Mention(group, e1, b));

        var unknown = Guid.NewGuid().ToString();
        var counts = await Driver.GraphOps.CountEpisodeMentionsAsync([a.Uuid, b.Uuid, c.Uuid, unknown]);

        Assert.Equal(2, counts[a.Uuid]);
        Assert.Equal(1, counts[b.Uuid]);
        Assert.Equal(0, counts[c.Uuid]);
        Assert.Equal(0, counts[unknown]);
        Assert.Equal(4, counts.Count);
        Assert.Empty(await Driver.GraphOps.CountEpisodeMentionsAsync([]));
    }
}
