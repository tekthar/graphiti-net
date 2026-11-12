// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Driver.Tests.Conformance;

// Full-text, similarity and breadth-first search, and connected-node lookups.
public abstract partial class GraphDriverConformanceTests
{
    private static SearchFilters NoFilter => SearchFilters.None;

    // ---------------------------------------------------------------- full-text

    [Fact]
    public async Task Node_fulltext_search_ranks_by_relevance_on_name_and_summary()
    {
        var group = NewGroup();
        var both = Entity(group, "Quokka Zebra", summary: "A quokka zebra habitat survey.");
        var nameOnly = Entity(group, "Quokka", summary: "Lives on islands.");
        var summaryOnly = Entity(group, "Rottnest", summary: "Home of the zebra crossing.");
        var neither = Entity(group, "Wombat", summary: "Burrowing marsupial.");
        await Save(both, nameOnly, summaryOnly, neither);

        var result = await Driver.SearchOps.NodeFulltextSearchAsync("quokka zebra", NoFilter, [group]);
        Assert.Equal(both.Uuid, result[0].Uuid);
        AssertSameNodes([both, nameOnly, summaryOnly], result);
        AssertEntity(both, result[0]);

        AssertSameNodes([summaryOnly], await Driver.SearchOps.NodeFulltextSearchAsync("crossing", NoFilter, [group]));
        AssertSameNodes([neither], await Driver.SearchOps.NodeFulltextSearchAsync("MARSUPIAL", NoFilter, [group]));
        Assert.Single(await Driver.SearchOps.NodeFulltextSearchAsync("quokka zebra", NoFilter, [group], limit: 1));
    }

    [Fact]
    public async Task Fact_fulltext_search_ranks_by_relevance_on_name_and_fact()
    {
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        await Save(alice, bob);
        var both = Fact(group, alice, bob, "MENTORS", "Alice mentors Bob in pottery and pottery glazing.");
        var nameOnly = Fact(group, bob, alice, "MENTORS", "Bob coaches Alice at chess.");
        var factOnly = Fact(group, alice, bob, "KNOWS", "Alice knows Bob from a pottery class.");
        var neither = Fact(group, bob, alice, "KNOWS", "Bob knows Alice from school.");
        await Save(both, nameOnly, factOnly, neither);

        var result = await Driver.SearchOps.EdgeFulltextSearchAsync("mentors pottery", NoFilter, [group]);
        Assert.Equal(both.Uuid, result[0].Uuid);
        AssertSameEdges([both, nameOnly, factOnly], result);
        AssertFact(both, result[0]);
        Assert.Equal(2, (await Driver.SearchOps.EdgeFulltextSearchAsync("mentors pottery", NoFilter, [group], limit: 2)).Count);
        AssertSameEdges([nameOnly], await Driver.SearchOps.EdgeFulltextSearchAsync("chess", NoFilter, [group]));
    }

    [Fact]
    public async Task Episode_fulltext_search_ranks_by_relevance_on_content_and_source_description()
    {
        var group = NewGroup();
        var both = Episode(group, "e1", "Meeting about the lighthouse budget and lighthouse repairs.", sourceDescription: "lighthouse minutes");
        var contentOnly = Episode(group, "e2", "The budget was approved.", sourceDescription: "email");
        var descriptionOnly = Episode(group, "e3", "Nothing to report.", sourceDescription: "lighthouse log");
        var neither = Episode(group, "e4", "Weather was fine.", sourceDescription: "diary");
        await Save(both, contentOnly, descriptionOnly, neither);

        var result = await Driver.SearchOps.EpisodeFulltextSearchAsync("lighthouse budget", NoFilter, [group]);
        Assert.Equal(both.Uuid, result[0].Uuid);
        AssertSameNodes([both, contentOnly, descriptionOnly], result);
        AssertEpisode(both, result[0]);
        AssertSameNodes([descriptionOnly, both], await Driver.SearchOps.EpisodeFulltextSearchAsync("lighthouse", NoFilter, [group]));
    }

    [Fact]
    public async Task Community_fulltext_search_ranks_by_relevance_on_name()
    {
        var group = NewGroup();
        var both = Community(group, "Glacier Hikers Glacier Club", summary: "unrelated words");
        var one = Community(group, "Hikers", summary: "glacier");
        var neither = Community(group, "Chess players");
        await Save(both, one, neither);

        var result = await Driver.SearchOps.CommunityFulltextSearchAsync("glacier hikers", [group]);
        Assert.Equal(Ids([both, one]), Ids(result));
        AssertCommunity(both, result[0]);
        Assert.Single(await Driver.SearchOps.CommunityFulltextSearchAsync("glacier hikers", [group], limit: 1));
    }

    [Fact]
    public async Task Fulltext_search_restricts_to_the_given_groups()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var word = UniqueWord();
        var mine = Entity(group, $"{word} one");
        var theirs = Entity(otherGroup, $"{word} two");
        await Save(mine, theirs);
        var fact = Fact(group, mine, mine, "SAYS", $"{word} fact");
        var otherFact = Fact(otherGroup, theirs, theirs, "SAYS", $"{word} fact");
        await Save(fact, otherFact);
        var episode = Episode(group, "e", $"{word} episode");
        var otherEpisode = Episode(otherGroup, "e", $"{word} episode");
        await Save(episode, otherEpisode);
        var community = Community(group, $"{word} community");
        var otherCommunity = Community(otherGroup, $"{word} community");
        await Save(community, otherCommunity);

        AssertSameNodes([mine], await Driver.SearchOps.NodeFulltextSearchAsync(word, NoFilter, [group]));
        AssertSameNodes([mine, theirs], await Driver.SearchOps.NodeFulltextSearchAsync(word, NoFilter, [group, otherGroup]));
        AssertSameNodes([mine, theirs], await Driver.SearchOps.NodeFulltextSearchAsync(word, NoFilter));
        Assert.Empty(await Driver.SearchOps.NodeFulltextSearchAsync(word, NoFilter, []));

        AssertSameEdges([fact], await Driver.SearchOps.EdgeFulltextSearchAsync(word, NoFilter, [group]));
        AssertSameEdges([fact, otherFact], await Driver.SearchOps.EdgeFulltextSearchAsync(word, NoFilter));
        Assert.Empty(await Driver.SearchOps.EdgeFulltextSearchAsync(word, NoFilter, []));

        AssertSameNodes([episode], await Driver.SearchOps.EpisodeFulltextSearchAsync(word, NoFilter, [group]));
        AssertSameNodes([episode, otherEpisode], await Driver.SearchOps.EpisodeFulltextSearchAsync(word, NoFilter));
        Assert.Empty(await Driver.SearchOps.EpisodeFulltextSearchAsync(word, NoFilter, []));

        AssertSameNodes([community], await Driver.SearchOps.CommunityFulltextSearchAsync(word, [group]));
        AssertSameNodes([community, otherCommunity], await Driver.SearchOps.CommunityFulltextSearchAsync(word));
        Assert.Empty(await Driver.SearchOps.CommunityFulltextSearchAsync(word, []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Empty_fulltext_queries_return_nothing(string query)
    {
        var group = NewGroup();
        var node = Entity(group, "Alice", summary: "An engineer.");
        await Save(node);
        await Save(Fact(group, node, node, "IS", "Alice is Alice."));
        await Save(Episode(group, "e", "Alice spoke."));
        await Save(Community(group, "Alice fans"));

        Assert.Empty(await Driver.SearchOps.NodeFulltextSearchAsync(query, NoFilter, [group]));
        Assert.Empty(await Driver.SearchOps.EdgeFulltextSearchAsync(query, NoFilter, [group]));
        Assert.Empty(await Driver.SearchOps.EpisodeFulltextSearchAsync(query, NoFilter, [group]));
        Assert.Empty(await Driver.SearchOps.CommunityFulltextSearchAsync(query, [group]));
    }

    [Fact]
    public async Task Fulltext_search_ignores_case_and_punctuation()
    {
        var group = NewGroup();
        var node = Entity(group, "Zanzibar-Archipelago", summary: "Spice islands, near TANZANIA.");
        await Save(node);

        AssertSameNodes([node], await Driver.SearchOps.NodeFulltextSearchAsync("zanzibar", NoFilter, [group]));
        AssertSameNodes([node], await Driver.SearchOps.NodeFulltextSearchAsync("Tanzania?", NoFilter, [group]));
        AssertSameNodes([node], await Driver.SearchOps.NodeFulltextSearchAsync("archipelago!", NoFilter, [group]));
    }

    // ---------------------------------------------------------------- similarity

    [Fact]
    public async Task Node_similarity_search_ranks_by_cosine_with_a_minimum_score()
    {
        var group = NewGroup();
        var exact = Entity(group, "exact", embedding: Vec(1, 0, 0));
        var close = Entity(group, "close", embedding: Vec(0.8f, 0.6f, 0));
        var far = Entity(group, "far", embedding: Vec(0.5f, 0.8660254f, 0));
        var orthogonal = Entity(group, "orthogonal", embedding: Vec(0, 1, 0));
        var opposite = Entity(group, "opposite", embedding: Vec(-1, 0, 0));
        var none = Entity(group, "none");
        await Save(exact, close, far, orthogonal, opposite, none);
        var query = Vec(1, 0, 0);

        var result = await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group], minScore: 0.7);
        Assert.Equal(Ids([exact, close]), Ids(result));
        AssertEntity(exact, result[0]);

        Assert.Equal(
            Ids([exact, close, far]),
            Ids(await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group], minScore: 0.4)));
        Assert.Equal(
            Ids([exact, close, far, orthogonal]),
            Ids(await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group], minScore: -0.5)));
        Assert.Equal(
            Ids([exact, close]),
            Ids(await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group], limit: 2, minScore: 0)));

        // The minimum is inclusive.
        Assert.Equal(
            Ids([exact]),
            Ids(await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group], minScore: 1.0)));

        // The default minimum score is 0.6. Cosine ignores length.
        Assert.Equal(
            Ids([exact, close]),
            Ids(await Driver.SearchOps.NodeSimilaritySearchAsync(Vec(3, 0, 0), NoFilter, [group])));
    }

    [Fact]
    public async Task Fact_similarity_search_ranks_by_cosine_and_can_be_restricted_to_endpoints()
    {
        var group = NewGroup();
        var a = Entity(group, "A");
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        await Save(a, b, c);
        var ab = Fact(group, a, b, embedding: Vec(1, 0, 0));
        var ab2 = Fact(group, a, b, embedding: Vec(0.8f, 0.6f, 0));
        var ba = Fact(group, b, a, embedding: Vec(0.6f, 0.8f, 0));
        var ac = Fact(group, a, c, embedding: Vec(0.8f, 0, 0.6f));
        var bc = Fact(group, b, c, embedding: Vec(0, 1, 0));
        var noEmbedding = Fact(group, a, b);
        await Save(ab, ab2, ba, ac, bc, noEmbedding);
        var query = Vec(1, 0, 0);

        var result = await Driver.SearchOps.EdgeSimilaritySearchAsync(query, null, null, NoFilter, [group], minScore: 0.5);
        Assert.Equal(ab.Uuid, result[0].Uuid);
        AssertSameEdges([ab, ab2, ac, ba], result);
        Assert.Equal(ba.Uuid, result[^1].Uuid);
        AssertFact(ab, result[0]);

        Assert.Equal(
            Ids([ab, ab2]),
            Ids(await Driver.SearchOps.EdgeSimilaritySearchAsync(query, a.Uuid, b.Uuid, NoFilter, [group], minScore: 0)));
        AssertSameEdges(
            [ab, ab2, ac],
            await Driver.SearchOps.EdgeSimilaritySearchAsync(query, a.Uuid, null, NoFilter, [group], minScore: 0));
        AssertSameEdges(
            [ac, bc],
            await Driver.SearchOps.EdgeSimilaritySearchAsync(query, null, c.Uuid, NoFilter, [group], minScore: -0.5));

        // Endpoint restriction applies with no group ids too (upstream only applies it with group ids).
        Assert.Equal(
            Ids([ba]),
            Ids(await Driver.SearchOps.EdgeSimilaritySearchAsync(query, b.Uuid, a.Uuid, NoFilter, minScore: 0)));
        Assert.Empty(await Driver.SearchOps.EdgeSimilaritySearchAsync(query, c.Uuid, a.Uuid, NoFilter, minScore: -1));
        Assert.Single(await Driver.SearchOps.EdgeSimilaritySearchAsync(query, null, null, NoFilter, [group], limit: 1, minScore: 0));
    }

    [Fact]
    public async Task Community_similarity_search_ranks_by_cosine_with_a_minimum_score()
    {
        var group = NewGroup();
        var exact = Community(group, "exact", Vec(0, 0, 1));
        var close = Community(group, "close", Vec(0, 0.6f, 0.8f));
        var far = Community(group, "far", Vec(1, 0, 0));
        var none = Community(group, "none");
        await Save(exact, close, far, none);

        var result = await Driver.SearchOps.CommunitySimilaritySearchAsync(Vec(0, 0, 1), [group]);
        Assert.Equal(Ids([exact, close]), Ids(result));
        AssertCommunity(exact, result[0]);
        Assert.Equal(
            Ids([exact, close, far]),
            Ids(await Driver.SearchOps.CommunitySimilaritySearchAsync(Vec(0, 0, 1), [group], minScore: -1)));
        Assert.Single(await Driver.SearchOps.CommunitySimilaritySearchAsync(Vec(0, 0, 1), [group], limit: 1));
    }

    [Fact]
    public async Task Similarity_search_restricts_to_the_given_groups()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var mine = Entity(group, "mine", embedding: Vec(1, 0, 0));
        var theirs = Entity(otherGroup, "theirs", embedding: Vec(1, 0, 0));
        await Save(mine, theirs);
        var fact = Fact(group, mine, mine, embedding: Vec(1, 0, 0));
        var otherFact = Fact(otherGroup, theirs, theirs, embedding: Vec(1, 0, 0));
        await Save(fact, otherFact);
        var community = Community(group, "c", Vec(1, 0, 0));
        var otherCommunity = Community(otherGroup, "c", Vec(1, 0, 0));
        await Save(community, otherCommunity);
        var query = Vec(1, 0, 0);

        AssertSameNodes([mine], await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group]));
        AssertSameNodes([mine, theirs], await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, [group, otherGroup]));
        Assert.Empty(await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter, []));
        AssertSameEdges([fact], await Driver.SearchOps.EdgeSimilaritySearchAsync(query, null, null, NoFilter, [group]));
        Assert.Empty(await Driver.SearchOps.EdgeSimilaritySearchAsync(query, null, null, NoFilter, []));
        AssertSameNodes([community], await Driver.SearchOps.CommunitySimilaritySearchAsync(query, [group]));
        Assert.Empty(await Driver.SearchOps.CommunitySimilaritySearchAsync(query, []));

        if (IsIsolated)
        {
            AssertSameNodes([mine, theirs], await Driver.SearchOps.NodeSimilaritySearchAsync(query, NoFilter));
            AssertSameEdges([fact, otherFact], await Driver.SearchOps.EdgeSimilaritySearchAsync(query, null, null, NoFilter));
            AssertSameNodes([community, otherCommunity], await Driver.SearchOps.CommunitySimilaritySearchAsync(query));
        }
    }

    // ---------------------------------------------------------------- breadth-first

    /// <summary>
    /// A -f1-> B -f2-> C -f3-> D, E -f4-> A, episode P mentions A, and F stands alone. B is a Person.
    /// </summary>
    private async Task<BfsGraph> SaveBfsGraph()
    {
        var group = NewGroup();
        var a = Entity(group, "A");
        var b = Entity(group, "B", labels: ["Person"]);
        var c = Entity(group, "C");
        var d = Entity(group, "D", labels: ["Person"]);
        var e = Entity(group, "E");
        var f = Entity(group, "F");
        await Save(a, b, c, d, e, f);
        var f1 = Fact(group, a, b);
        var f2 = Fact(group, b, c);
        var f3 = Fact(group, c, d);
        var f4 = Fact(group, e, a);
        await Save(f1, f2, f3, f4);
        var p = Episode(group, "P");
        await Save(p);
        await Save(Mention(group, p, a));
        return new BfsGraph(group, a, b, c, d, e, f, f1, f2, f3, f4, p);
    }

    private sealed record BfsGraph(
        string Group,
        EntityNode A,
        EntityNode B,
        EntityNode C,
        EntityNode D,
        EntityNode E,
        EntityNode F,
        EntityEdge F1,
        EntityEdge F2,
        EntityEdge F3,
        EntityEdge F4,
        EpisodicNode P);

    [Fact]
    public async Task Node_bfs_follows_facts_in_both_directions_up_to_the_depth()
    {
        var g = await SaveBfsGraph();
        Task<IReadOnlyList<EntityNode>> Bfs(int depth, params string[] origins) =>
            Driver.SearchOps.NodeBfsSearchAsync(origins, NoFilter, depth, [g.Group], limit: 100);

        AssertSameNodes([g.B, g.E], await Bfs(1, g.A.Uuid));
        AssertSameNodes([g.B, g.E, g.C], await Bfs(2, g.A.Uuid));
        AssertSameNodes([g.B, g.E, g.C, g.D], await Bfs(3, g.A.Uuid));
        AssertSameNodes([g.C], await Bfs(1, g.D.Uuid));
        AssertSameNodes([g.C, g.A], await Bfs(1, g.B.Uuid, g.D.Uuid));
        Assert.Empty(await Bfs(3, g.F.Uuid));
        Assert.Empty(await Bfs(0, g.A.Uuid));
        Assert.Empty(await Bfs(2));
        Assert.Empty(await Bfs(2, Guid.NewGuid().ToString()));

        var found = await Bfs(1, g.A.Uuid);
        AssertEntity(g.B, found.Single(n => n.Uuid == g.B.Uuid));
        Assert.Equal(2, (await Driver.SearchOps.NodeBfsSearchAsync([g.A.Uuid], NoFilter, 3, [g.Group], limit: 2)).Count);
    }

    [Fact]
    public async Task Node_bfs_starts_from_episodes_through_their_mentions()
    {
        var g = await SaveBfsGraph();
        Task<IReadOnlyList<EntityNode>> Bfs(int depth) =>
            Driver.SearchOps.NodeBfsSearchAsync([g.P.Uuid], NoFilter, depth, [g.Group], limit: 100);

        AssertSameNodes([g.A], await Bfs(1));
        AssertSameNodes([g.A, g.B, g.E], await Bfs(2));
        AssertSameNodes([g.A, g.B, g.E, g.C], await Bfs(3));

        // Mentions are followed only from the episode to the entity, never back.
        var other = Episode(g.Group, "other");
        await Save(other);
        await Save(Mention(g.Group, other, g.F));
        AssertSameNodes([g.F], await Driver.SearchOps.NodeBfsSearchAsync([other.Uuid], NoFilter, 3, [g.Group], limit: 100));
    }

    [Fact]
    public async Task Node_bfs_applies_groups_and_filters()
    {
        var g = await SaveBfsGraph();

        Assert.Empty(await Driver.SearchOps.NodeBfsSearchAsync([g.A.Uuid], NoFilter, 3, [NewGroup()], limit: 100));
        Assert.Empty(await Driver.SearchOps.NodeBfsSearchAsync([g.A.Uuid], NoFilter, 3, [], limit: 100));
        AssertSameNodes(
            [g.B, g.E, g.C, g.D],
            await Driver.SearchOps.NodeBfsSearchAsync([g.A.Uuid], NoFilter, 3, null, limit: 100));
        AssertSameNodes(
            [g.B, g.D],
            await Driver.SearchOps.NodeBfsSearchAsync(
                [g.A.Uuid], new SearchFilters { NodeLabels = ["Person"] }, 3, [g.Group], limit: 100));

        // Entities in another group are not returned, even when joined by a fact.
        var stranger = Entity(NewGroup(), "Stranger");
        await Save(stranger);
        await Save(Fact(g.Group, g.A, stranger));
        AssertSameNodes([g.B, g.E], await Driver.SearchOps.NodeBfsSearchAsync([g.A.Uuid], NoFilter, 1, null, limit: 100));
    }

    [Fact]
    public async Task Edge_bfs_returns_the_facts_on_paths_up_to_the_depth()
    {
        var g = await SaveBfsGraph();
        Task<IReadOnlyList<EntityEdge>> Bfs(int depth, params string[] origins) =>
            Driver.SearchOps.EdgeBfsSearchAsync(origins, depth, NoFilter, [g.Group], limit: 100);

        AssertSameEdges([g.F1, g.F4], await Bfs(1, g.A.Uuid));
        AssertSameEdges([g.F1, g.F4, g.F2], await Bfs(2, g.A.Uuid));
        AssertSameEdges([g.F1, g.F4, g.F2, g.F3], await Bfs(3, g.A.Uuid));
        AssertSameEdges([g.F3], await Bfs(1, g.D.Uuid));
        AssertSameEdges([g.F3, g.F1, g.F2], await Bfs(1, g.D.Uuid, g.B.Uuid));
        Assert.Empty(await Bfs(3, g.F.Uuid));
        Assert.Empty(await Bfs(0, g.A.Uuid));
        Assert.Empty(await Bfs(2));

        // From an episode, the mention is the first hop.
        Assert.Empty(await Bfs(1, g.P.Uuid));
        AssertSameEdges([g.F1, g.F4], await Bfs(2, g.P.Uuid));
        AssertSameEdges([g.F1, g.F4, g.F2], await Bfs(3, g.P.Uuid));

        var found = await Bfs(1, g.A.Uuid);
        AssertFact(g.F1, found.Single(e => e.Uuid == g.F1.Uuid));
        Assert.Single(await Driver.SearchOps.EdgeBfsSearchAsync([g.A.Uuid], 3, NoFilter, [g.Group], limit: 1));
        Assert.Empty(await Driver.SearchOps.EdgeBfsSearchAsync([g.A.Uuid], 3, NoFilter, [NewGroup()], limit: 100));
        Assert.Empty(await Driver.SearchOps.EdgeBfsSearchAsync([g.A.Uuid], 3, NoFilter, [], limit: 100));
        AssertSameEdges(
            [g.F1, g.F4, g.F2, g.F3],
            await Driver.SearchOps.EdgeBfsSearchAsync([g.A.Uuid], 3, NoFilter, null, limit: 100));
    }

    // ---------------------------------------------------------------- connected nodes

    [Fact]
    public async Task Connected_node_uuids_are_the_candidates_joined_by_a_fact_either_way()
    {
        var group = NewGroup();
        var center = Entity(group, "center");
        var outgoing = Entity(group, "outgoing");
        var incoming = Entity(group, "incoming");
        var twoHops = Entity(group, "two hops");
        var unrelated = Entity(group, "unrelated");
        await Save(center, outgoing, incoming, twoHops, unrelated);
        await Save(Fact(group, center, outgoing), Fact(group, incoming, center), Fact(group, outgoing, twoHops));
        var episode = Episode(group, "episode");
        await Save(episode);
        await Save(Mention(group, episode, center));

        var connected = await Driver.SearchOps.GetConnectedNodeUuidsAsync(
            center.Uuid,
            [outgoing.Uuid, incoming.Uuid, twoHops.Uuid, unrelated.Uuid, episode.Uuid, Guid.NewGuid().ToString()]);
        AssertSameSet([outgoing.Uuid, incoming.Uuid], connected);
        Assert.Empty(await Driver.SearchOps.GetConnectedNodeUuidsAsync(center.Uuid, []));
        Assert.Empty(await Driver.SearchOps.GetConnectedNodeUuidsAsync(unrelated.Uuid, [center.Uuid, outgoing.Uuid]));
    }
}
