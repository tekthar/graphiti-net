// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

// Saves, gets, paging, deletes and round-trips for every record type.
public abstract partial class GraphDriverConformanceTests
{
    // ---------------------------------------------------------------- generic record operations

    /// <summary>
    /// Runs the operations every record type shares: save, bulk save, get, get many, get by group with paging,
    /// upsert, delete and delete by ids.
    /// </summary>
    /// <param name="ops">The operations under test.</param>
    /// <param name="make">Makes an unsaved record in a group, saving whatever it points to first.</param>
    /// <param name="replace">Makes a different record with the same id, for the upsert check.</param>
    /// <param name="assertEqual">Compares a saved record with what the driver returns.</param>
    /// <param name="isNode">Whether a missing id throws <see cref="NodeNotFoundException"/> (else
    /// <see cref="EdgeNotFoundException"/>).</param>
    private async Task CheckRecordOperations<T>(
        IRecordOperations<T> ops,
        Func<string, Task<T>> make,
        Func<T, T> replace,
        Action<T, T> assertEqual,
        bool isNode)
        where T : class
    {
        static string UuidOf(T item) => item switch
        {
            Node n => n.Uuid,
            Edge e => e.Uuid,
            _ => throw new InvalidOperationException(),
        };

        var group = NewGroup();
        var otherGroup = NewGroup();
        var items = new List<T>();
        for (var i = 0; i < 5; i++)
        {
            items.Add(await make(group));
        }

        var other = await make(otherGroup);

        // Save one, bulk-save the rest.
        await ops.SaveAsync(items[0]);
        await ops.SaveBulkAsync([.. items.Skip(1), other]);

        foreach (var item in items)
        {
            assertEqual(item, await ops.GetByUuidAsync(UuidOf(item)));
        }

        // A missing id throws the not-found exception for nodes or edges.
        var missing = Guid.NewGuid().ToString();
        if (isNode)
        {
            var ex = await Assert.ThrowsAsync<NodeNotFoundException>(() => ops.GetByUuidAsync(missing));
            Assert.Equal(missing, ex.Uuid);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<EdgeNotFoundException>(() => ops.GetByUuidAsync(missing));
            Assert.Equal(missing, ex.Uuid);
        }

        // Get many skips missing ids.
        var many = await ops.GetByUuidsAsync([UuidOf(items[0]), missing, UuidOf(items[3])]);
        AssertSameSet([UuidOf(items[0]), UuidOf(items[3])], many.Select(UuidOf));
        foreach (var found in many)
        {
            assertEqual(items.Single(i => UuidOf(i) == UuidOf(found)), found);
        }

        Assert.Empty(await ops.GetByUuidsAsync([missing]));
        Assert.Empty(await ops.GetByUuidsAsync([]));

        // Get by group: descending id order, a limit, and an exclusive cursor.
        var descending = items.Select(UuidOf).OrderDescending(StringComparer.Ordinal).ToList();
        Assert.Equal(descending, (await ops.GetByGroupIdsAsync([group])).Select(UuidOf).ToList());
        Assert.Equal(descending.Take(2), (await ops.GetByGroupIdsAsync([group], limit: 2)).Select(UuidOf));
        Assert.Equal(
            descending.Skip(2).Take(2),
            (await ops.GetByGroupIdsAsync([group], limit: 2, uuidCursor: descending[1])).Select(UuidOf));
        Assert.Equal(
            descending.Skip(1),
            (await ops.GetByGroupIdsAsync([group], uuidCursor: descending[0])).Select(UuidOf));
        Assert.Empty(await ops.GetByGroupIdsAsync([group], uuidCursor: descending[^1]));

        var paged = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = await ops.GetByGroupIdsAsync([group], limit: 2, uuidCursor: cursor);
            if (page.Count == 0)
            {
                break;
            }

            Assert.True(page.Count <= 2);
            paged.AddRange(page.Select(UuidOf));
            cursor = UuidOf(page[^1]);
        }

        Assert.Equal(descending, paged);

        foreach (var found in await ops.GetByGroupIdsAsync([group]))
        {
            assertEqual(items.Single(i => UuidOf(i) == UuidOf(found)), found);
        }

        AssertSameSet(
            [.. items.Select(UuidOf), UuidOf(other)],
            (await ops.GetByGroupIdsAsync([group, otherGroup])).Select(UuidOf));
        Assert.Empty(await ops.GetByGroupIdsAsync([]));
        Assert.Empty(await ops.GetByGroupIdsAsync([NewGroup()]));

        // Saving an existing id replaces it.
        var replacement = replace(items[0]);
        await ops.SaveAsync(replacement);
        assertEqual(replacement, await ops.GetByUuidAsync(UuidOf(items[0])));
        Assert.Equal(5, (await ops.GetByGroupIdsAsync([group])).Count);

        var bulkReplacement = replace(items[1]);
        await ops.SaveBulkAsync([bulkReplacement]);
        assertEqual(bulkReplacement, await ops.GetByUuidAsync(UuidOf(items[1])));

        // Delete one, then delete by ids (unknown ids are ignored).
        await ops.DeleteAsync(items[0]);
        if (isNode)
        {
            await Assert.ThrowsAsync<NodeNotFoundException>(() => ops.GetByUuidAsync(UuidOf(items[0])));
        }
        else
        {
            await Assert.ThrowsAsync<EdgeNotFoundException>(() => ops.GetByUuidAsync(UuidOf(items[0])));
        }

        await ops.DeleteByUuidsAsync([UuidOf(items[1]), UuidOf(items[2]), missing]);
        AssertSameSet(
            [UuidOf(items[3]), UuidOf(items[4])],
            (await ops.GetByGroupIdsAsync([group])).Select(UuidOf));
        await ops.DeleteByUuidsAsync([]);
        Assert.Single(await ops.GetByGroupIdsAsync([otherGroup]));
    }

    private static int s_nameCounter;

    private static string NextName(string prefix) => $"{prefix} {Interlocked.Increment(ref s_nameCounter)}";

    [Fact]
    public Task Episode_nodes_support_record_operations() => CheckRecordOperations(
        Driver.EpisodeNodeOps,
        group => Task.FromResult(Episode(group, NextName("episode"), validAt: Day(2))),
        e => new EpisodicNode
        {
            Uuid = e.Uuid,
            Name = e.Name + " v2",
            GroupId = e.GroupId,
            CreatedAt = e.CreatedAt.AddMinutes(1),
            Source = EpisodeType.Json,
            SourceDescription = "replaced",
            Content = "{\"replaced\": true}",
            ValidAt = Day(3),
            EntityEdges = ["edge-1"],
            EpisodeMetadata = new Dictionary<string, object?> { ["k"] = "v" },
        },
        AssertEpisode,
        isNode: true);

    [Fact]
    public Task Entity_nodes_support_record_operations() => CheckRecordOperations(
        Driver.EntityNodeOps,
        group => Task.FromResult(Entity(group, NextName("entity"), summary: "summary", labels: ["Person"])),
        n => new EntityNode
        {
            Uuid = n.Uuid,
            Name = n.Name + " v2",
            GroupId = n.GroupId,
            CreatedAt = n.CreatedAt.AddMinutes(1),
            Summary = "replaced",
            Labels = ["Organization"],
            Attributes = new Dictionary<string, object?> { ["size"] = 3L },
        },
        AssertEntity,
        isNode: true);

    [Fact]
    public Task Community_nodes_support_record_operations() => CheckRecordOperations(
        Driver.CommunityNodeOps,
        group => Task.FromResult(Community(group, NextName("community"), summary: "summary")),
        c => new CommunityNode
        {
            Uuid = c.Uuid,
            Name = c.Name + " v2",
            GroupId = c.GroupId,
            CreatedAt = c.CreatedAt.AddMinutes(1),
            Summary = "replaced",
        },
        AssertCommunity,
        isNode: true);

    [Fact]
    public Task Saga_nodes_support_record_operations() => CheckRecordOperations(
        Driver.SagaNodeOps,
        group => Task.FromResult(Saga(group, NextName("saga"))),
        s => new SagaNode
        {
            Uuid = s.Uuid,
            Name = s.Name + " v2",
            GroupId = s.GroupId,
            CreatedAt = s.CreatedAt.AddMinutes(1),
            Summary = "replaced",
            FirstEpisodeUuid = "first",
            LastEpisodeUuid = "last",
            LastSummarizedAt = Day(5),
            LastSummarizedEpisodeValidAt = Day(4),
        },
        AssertSaga,
        isNode: true);

    [Fact]
    public Task Episodic_edges_support_record_operations() => CheckRecordOperations(
        Driver.EpisodicEdgeOps,
        async group =>
        {
            var episode = Episode(group, NextName("episode"));
            var entity = Entity(group, NextName("entity"));
            await Save(episode);
            await Save(entity);
            return Mention(group, episode, entity);
        },
        e => new EpisodicEdge
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            SourceNodeUuid = e.SourceNodeUuid,
            TargetNodeUuid = e.TargetNodeUuid,
            CreatedAt = e.CreatedAt.AddMinutes(1),
        },
        AssertPlainEdge,
        isNode: false);

    [Fact]
    public Task Entity_edges_support_record_operations() => CheckRecordOperations(
        Driver.EntityEdgeOps,
        async group =>
        {
            var source = Entity(group, NextName("source"));
            var target = Entity(group, NextName("target"));
            await Save(source, target);
            return Fact(group, source, target, "KNOWS", validAt: Day(2));
        },
        e => new EntityEdge
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            SourceNodeUuid = e.SourceNodeUuid,
            TargetNodeUuid = e.TargetNodeUuid,
            CreatedAt = e.CreatedAt.AddMinutes(1),
            Name = "LIKES",
            Fact = "replaced fact",
            Episodes = ["episode-1"],
            ValidAt = Day(3),
            InvalidAt = Day(4),
            ExpiredAt = Day(5),
            ReferenceTime = Day(3),
            Attributes = new Dictionary<string, object?> { ["weight"] = 2L },
        },
        AssertFact,
        isNode: false);

    [Fact]
    public Task Community_edges_support_record_operations() => CheckRecordOperations(
        Driver.CommunityEdgeOps,
        async group =>
        {
            var community = Community(group, NextName("community"));
            var entity = Entity(group, NextName("entity"));
            await Save(community);
            await Save(entity);
            return Member(group, community, entity);
        },
        e => new CommunityEdge
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            SourceNodeUuid = e.SourceNodeUuid,
            TargetNodeUuid = e.TargetNodeUuid,
            CreatedAt = e.CreatedAt.AddMinutes(1),
        },
        AssertPlainEdge,
        isNode: false);

    [Fact]
    public Task Has_episode_edges_support_record_operations() => CheckRecordOperations(
        Driver.HasEpisodeEdgeOps,
        async group =>
        {
            var saga = Saga(group, NextName("saga"));
            var episode = Episode(group, NextName("episode"));
            await Save(saga);
            await Save(episode);
            return HasEpisode(group, saga, episode);
        },
        e => new HasEpisodeEdge
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            SourceNodeUuid = e.SourceNodeUuid,
            TargetNodeUuid = e.TargetNodeUuid,
            CreatedAt = e.CreatedAt.AddMinutes(1),
        },
        AssertPlainEdge,
        isNode: false);

    [Fact]
    public Task Next_episode_edges_support_record_operations() => CheckRecordOperations(
        Driver.NextEpisodeEdgeOps,
        async group =>
        {
            var first = Episode(group, NextName("first"));
            var second = Episode(group, NextName("second"));
            await Save(first, second);
            return NextEpisode(group, first, second);
        },
        e => new NextEpisodeEdge
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            SourceNodeUuid = e.SourceNodeUuid,
            TargetNodeUuid = e.TargetNodeUuid,
            CreatedAt = e.CreatedAt.AddMinutes(1),
        },
        AssertPlainEdge,
        isNode: false);

    [Fact]
    public async Task Operations_only_see_their_own_record_type()
    {
        var group = NewGroup();
        var episode = Episode(group, "episode");
        var entity = Entity(group, "entity");
        var community = Community(group, "community");
        var saga = Saga(group, "saga");
        await Save(episode);
        await Save(entity);
        await Save(community);
        await Save(saga);
        var mention = Mention(group, episode, entity);
        await Save(mention);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.EntityNodeOps.GetByUuidAsync(episode.Uuid));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.EpisodeNodeOps.GetByUuidAsync(entity.Uuid));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.CommunityNodeOps.GetByUuidAsync(saga.Uuid));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.SagaNodeOps.GetByUuidAsync(community.Uuid));
        await Assert.ThrowsAsync<EdgeNotFoundException>(() => Driver.EntityEdgeOps.GetByUuidAsync(mention.Uuid));
        Assert.Single(await Driver.EntityNodeOps.GetByGroupIdsAsync([group]));
        Assert.Single(await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
    }

    // ---------------------------------------------------------------- round trips

    private static Dictionary<string, object?> RichValues() => new()
    {
        ["text"] = "héllo, wörld",
        ["empty"] = "",
        ["integer"] = 42L,
        ["negative"] = -7L,
        ["big"] = 9_007_199_254_740_993L,
        ["real"] = 3.25,
        ["yes"] = true,
        ["no"] = false,
        ["nothing"] = null,
        ["list"] = new List<object?> { 1L, "two", false, null, 2.5 },
        ["nested"] = new Dictionary<string, object?>
        {
            ["inner"] = new Dictionary<string, object?> { ["deep"] = "value", ["n"] = 1L },
            ["items"] = new List<object?> { new Dictionary<string, object?> { ["a"] = 1L } },
            ["none"] = new List<object?>(),
        },
    };

    [Fact]
    public async Task Entity_nodes_round_trip_every_field()
    {
        var group = NewGroup();
        var created = new DateTimeOffset(2025, 3, 4, 5, 6, 7, 890, TimeSpan.FromHours(2));
        var node = Entity(
            group,
            "Alice Smith",
            summary: "Alice is an engineer.\nShe likes \"quotes\" and 'apostrophes'.",
            labels: ["Entity", "Person", "Engineer"],
            embedding: Vec(0.6f, 0.8f, 0),
            attributes: RichValues(),
            createdAt: created);
        await Save(node);

        var loaded = await Driver.EntityNodeOps.GetByUuidAsync(node.Uuid);
        AssertEntity(node, loaded);
        AssertUtc(created, loaded.CreatedAt);
        Assert.Equal(created.UtcDateTime, loaded.CreatedAt.UtcDateTime);

        var many = await Driver.EntityNodeOps.GetByUuidsAsync([node.Uuid]);
        AssertEntity(node, Assert.Single(many));
        AssertEntity(node, Assert.Single(await Driver.EntityNodeOps.GetByGroupIdsAsync([group])));
    }

    [Fact]
    public async Task Entity_nodes_with_no_labels_or_attributes_round_trip()
    {
        var group = NewGroup();
        var node = Entity(group, "Plain");
        await Save(node);

        var loaded = await Driver.EntityNodeOps.GetByUuidAsync(node.Uuid);
        AssertEntity(node, loaded);
        Assert.Empty(loaded.Attributes);
        Assert.Empty(loaded.Labels);
    }

    [Fact]
    public async Task Entity_edges_round_trip_every_field()
    {
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var acme = Entity(group, "Acme");
        await Save(alice, acme);

        var offset = TimeSpan.FromHours(-5);
        var edge = new EntityEdge
        {
            GroupId = group,
            SourceNodeUuid = alice.Uuid,
            TargetNodeUuid = acme.Uuid,
            CreatedAt = new DateTimeOffset(2025, 2, 1, 10, 0, 0, 123, offset),
            Name = "WORKS_AT",
            Fact = "Alice works at Acme.",
            FactEmbedding = Vec(1, 0, 0),
            Episodes = ["episode-a", "episode-b"],
            ExpiredAt = new DateTimeOffset(2025, 2, 5, 0, 0, 0, offset),
            ValidAt = new DateTimeOffset(2024, 6, 1, 9, 30, 0, offset),
            InvalidAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, 999, offset),
            ReferenceTime = new DateTimeOffset(2025, 2, 1, 9, 0, 0, offset),
            Attributes = RichValues(),
        };
        await Save(edge);

        AssertFact(edge, await Driver.EntityEdgeOps.GetByUuidAsync(edge.Uuid));
        AssertFact(edge, Assert.Single(await Driver.EntityEdgeOps.GetByUuidsAsync([edge.Uuid])));
        AssertFact(edge, Assert.Single(await Driver.EntityEdgeOps.GetByGroupIdsAsync([group])));

        var open = Fact(group, acme, alice, "EMPLOYS");
        await Save(open);
        var loadedOpen = await Driver.EntityEdgeOps.GetByUuidAsync(open.Uuid);
        AssertFact(open, loadedOpen);
        Assert.Null(loadedOpen.ValidAt);
        Assert.Null(loadedOpen.InvalidAt);
        Assert.Null(loadedOpen.ExpiredAt);
        Assert.Null(loadedOpen.ReferenceTime);
        Assert.Empty(loadedOpen.Episodes);
        Assert.Empty(loadedOpen.Attributes);
    }

    [Fact]
    public async Task Episodes_round_trip_every_field_and_source_type()
    {
        var group = NewGroup();
        var episodes = new List<EpisodicNode>();
        foreach (var (source, i) in Enum.GetValues<EpisodeType>().Select((s, i) => (s, i)))
        {
            episodes.Add(new EpisodicNode
            {
                Name = $"episode {source}",
                GroupId = group,
                CreatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(9)),
                Source = source,
                SourceDescription = "chat transcript",
                Content = "user: Hello\nassistant: Hi! {\"json\": [1, 2]}",
                ValidAt = new DateTimeOffset(2024, 12, 31, 22, 0, 0, TimeSpan.FromHours(-3)).AddDays(i),
                EntityEdges = ["fact-1", "fact-2"],
                EpisodeMetadata = RichValues(),
            });
        }

        episodes.Add(Episode(group, "no metadata"));
        await Save([.. episodes]);

        foreach (var episode in episodes)
        {
            AssertEpisode(episode, await Driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid));
        }

        var plain = await Driver.EpisodeNodeOps.GetByUuidAsync(episodes[^1].Uuid);
        Assert.Empty(plain.EntityEdges);
    }

    [Fact]
    public async Task Communities_and_sagas_round_trip_every_field()
    {
        var group = NewGroup();
        var community = Community(group, "Engineers", Vec(0, 1, 0), "People who build things.");
        var saga = new SagaNode
        {
            Name = "Support chat",
            GroupId = group,
            CreatedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(1)),
            Summary = "A long chat.",
            FirstEpisodeUuid = "first",
            LastEpisodeUuid = "last",
            LastSummarizedAt = new DateTimeOffset(2025, 1, 3, 0, 0, 0, TimeSpan.FromHours(1)),
            LastSummarizedEpisodeValidAt = new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.FromHours(-1)),
        };
        var bareSaga = Saga(group, "Empty saga");
        await Save(community);
        await Save(saga, bareSaga);

        AssertCommunity(community, await Driver.CommunityNodeOps.GetByUuidAsync(community.Uuid));
        AssertSaga(saga, await Driver.SagaNodeOps.GetByUuidAsync(saga.Uuid));
        var bare = await Driver.SagaNodeOps.GetByUuidAsync(bareSaga.Uuid);
        AssertSaga(bareSaga, bare);
        Assert.Null(bare.FirstEpisodeUuid);
        Assert.Null(bare.LastSummarizedAt);
    }

    // ---------------------------------------------------------------- embeddings

    [Fact]
    public async Task Entity_embeddings_are_not_loaded_by_gets_but_by_load_embeddings()
    {
        var group = NewGroup();
        var withEmbedding = Entity(group, "Alice", embedding: Vec(0.6f, 0.8f, 0));
        var withoutEmbedding = Entity(group, "Bob");
        await Save(withEmbedding, withoutEmbedding);

        var loaded = await Driver.EntityNodeOps.GetByUuidAsync(withEmbedding.Uuid);
        Assert.Null(loaded.NameEmbedding);
        Assert.All(await Driver.EntityNodeOps.GetByGroupIdsAsync([group]), n => Assert.Null(n.NameEmbedding));
        Assert.All(
            await Driver.EntityNodeOps.GetByUuidsAsync([withEmbedding.Uuid, withoutEmbedding.Uuid]),
            n => Assert.Null(n.NameEmbedding));

        await Driver.EntityNodeOps.LoadEmbeddingsAsync(loaded);
        Assert.Equal(withEmbedding.NameEmbedding, loaded.NameEmbedding);

        var bare = await Driver.EntityNodeOps.GetByUuidAsync(withoutEmbedding.Uuid);
        bare.NameEmbedding = Vec(1, 1, 1);
        await Driver.EntityNodeOps.LoadEmbeddingsAsync(bare);
        Assert.Null(bare.NameEmbedding);

        var missing = Entity(group, "Missing");
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.EntityNodeOps.LoadEmbeddingsAsync(missing));

        var bulk = (await Driver.EntityNodeOps.GetByUuidsAsync([withEmbedding.Uuid])).ToList();
        var stray = Entity(group, "Stray", embedding: Vec(0, 0, 1));
        await Driver.EntityNodeOps.LoadEmbeddingsBulkAsync([.. bulk, stray]);
        Assert.Equal(withEmbedding.NameEmbedding, bulk[0].NameEmbedding);
        Assert.Equal(Vec(0, 0, 1), stray.NameEmbedding);
        await Driver.EntityNodeOps.LoadEmbeddingsBulkAsync([]);
    }

    [Fact]
    public async Task Fact_embeddings_are_not_loaded_by_gets_but_by_load_embeddings()
    {
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        await Save(alice, bob);
        var edge = Fact(group, alice, bob, "KNOWS", embedding: Vec(0, 0.6f, 0.8f));
        var bare = Fact(group, bob, alice, "KNOWS");
        await Save(edge, bare);

        var loaded = await Driver.EntityEdgeOps.GetByUuidAsync(edge.Uuid);
        Assert.Null(loaded.FactEmbedding);
        Assert.All(await Driver.EntityEdgeOps.GetByGroupIdsAsync([group]), e => Assert.Null(e.FactEmbedding));
        Assert.All(await Driver.EntityEdgeOps.GetByNodeUuidAsync(alice.Uuid), e => Assert.Null(e.FactEmbedding));

        await Driver.EntityEdgeOps.LoadEmbeddingsAsync(loaded);
        Assert.Equal(edge.FactEmbedding, loaded.FactEmbedding);

        var loadedBare = await Driver.EntityEdgeOps.GetByUuidAsync(bare.Uuid);
        await Driver.EntityEdgeOps.LoadEmbeddingsAsync(loadedBare);
        Assert.Null(loadedBare.FactEmbedding);

        var missing = Fact(group, alice, bob);
        await Assert.ThrowsAsync<EdgeNotFoundException>(() => Driver.EntityEdgeOps.LoadEmbeddingsAsync(missing));

        var bulk = (await Driver.EntityEdgeOps.GetByUuidsAsync([edge.Uuid])).ToList();
        await Driver.EntityEdgeOps.LoadEmbeddingsBulkAsync([.. bulk, missing]);
        Assert.Equal(edge.FactEmbedding, bulk[0].FactEmbedding);
    }

    [Fact]
    public async Task Community_embeddings_are_not_loaded_by_gets_but_by_load_name_embedding()
    {
        var group = NewGroup();
        var community = Community(group, "Engineers", Vec(0, 1, 0));
        await Save(community);

        var loaded = await Driver.CommunityNodeOps.GetByUuidAsync(community.Uuid);
        Assert.Null(loaded.NameEmbedding);
        await Driver.CommunityNodeOps.LoadNameEmbeddingAsync(loaded);
        Assert.Equal(community.NameEmbedding, loaded.NameEmbedding);

        await Assert.ThrowsAsync<NodeNotFoundException>(
            () => Driver.CommunityNodeOps.LoadNameEmbeddingAsync(Community(group, "missing")));
    }

    [Fact]
    public async Task Saving_replaces_the_stored_embedding()
    {
        var group = NewGroup();
        var node = Entity(group, "Alice", embedding: Vec(1, 0, 0));
        await Save(node);
        node.NameEmbedding = Vec(0, 1, 0);
        await Driver.EntityNodeOps.SaveAsync(node);

        var loaded = await Driver.EntityNodeOps.GetByUuidAsync(node.Uuid);
        await Driver.EntityNodeOps.LoadEmbeddingsAsync(loaded);
        Assert.Equal(Vec(0, 1, 0), loaded.NameEmbedding);
    }

    // ---------------------------------------------------------------- edge lookups

    [Fact]
    public async Task Facts_can_be_found_between_nodes_and_by_node()
    {
        var group = NewGroup();
        var a = Entity(group, "A");
        var b = Entity(group, "B");
        var c = Entity(group, "C");
        await Save(a, b, c);
        var ab1 = Fact(group, a, b, "KNOWS");
        var ab2 = Fact(group, a, b, "LIKES");
        var ba = Fact(group, b, a, "KNOWS");
        var bc = Fact(group, b, c, "KNOWS");
        await Save(ab1, ab2, ba, bc);

        AssertSameEdges([ab1, ab2], await Driver.EntityEdgeOps.GetBetweenNodesAsync(a.Uuid, b.Uuid));
        AssertSameEdges([ba], await Driver.EntityEdgeOps.GetBetweenNodesAsync(b.Uuid, a.Uuid));
        Assert.Empty(await Driver.EntityEdgeOps.GetBetweenNodesAsync(a.Uuid, c.Uuid));
        AssertFact(ab1, (await Driver.EntityEdgeOps.GetBetweenNodesAsync(a.Uuid, b.Uuid)).Single(e => e.Uuid == ab1.Uuid));

        AssertSameEdges([ab1, ab2, ba], await Driver.EntityEdgeOps.GetByNodeUuidAsync(a.Uuid));
        AssertSameEdges([ab1, ab2, ba, bc], await Driver.EntityEdgeOps.GetByNodeUuidAsync(b.Uuid));
        AssertSameEdges([bc], await Driver.EntityEdgeOps.GetByNodeUuidAsync(c.Uuid));
        Assert.Empty(await Driver.EntityEdgeOps.GetByNodeUuidAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task Episodes_can_be_found_by_the_entities_they_mention()
    {
        var group = NewGroup();
        var e1 = Episode(group, "one");
        var e2 = Episode(group, "two");
        var e3 = Episode(group, "three");
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        await Save(e1, e2, e3);
        await Save(alice, bob);
        await Save(Mention(group, e1, alice), Mention(group, e2, alice), Mention(group, e2, bob));

        var found = await Driver.EpisodeNodeOps.GetByEntityNodeUuidAsync(alice.Uuid);
        AssertSameNodes([e1, e2], found);
        AssertEpisode(e1, found.Single(e => e.Uuid == e1.Uuid));
        AssertSameNodes([e2], await Driver.EpisodeNodeOps.GetByEntityNodeUuidAsync(bob.Uuid));
        Assert.Empty(await Driver.EpisodeNodeOps.GetByEntityNodeUuidAsync(Guid.NewGuid().ToString()));
    }

    // ---------------------------------------------------------------- deletes

    [Fact]
    public async Task Deleting_an_entity_deletes_the_edges_that_touch_it()
    {
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        var carol = Entity(group, "Carol");
        await Save(alice, bob, carol);
        var episode = Episode(group, "episode");
        await Save(episode);
        var community = Community(group, "community");
        await Save(community);

        var outgoing = Fact(group, alice, bob);
        var incoming = Fact(group, carol, alice);
        var unrelated = Fact(group, bob, carol);
        await Save(outgoing, incoming, unrelated);
        var mentionsAlice = Mention(group, episode, alice);
        var mentionsBob = Mention(group, episode, bob);
        await Save(mentionsAlice, mentionsBob);
        var aliceMember = Member(group, community, alice);
        var bobMember = Member(group, community, bob);
        await Save(aliceMember, bobMember);

        await Driver.EntityNodeOps.DeleteAsync(alice);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid));
        AssertSameEdges([unrelated], await Driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
        AssertSameEdges([mentionsBob], await Driver.EpisodicEdgeOps.GetByGroupIdsAsync([group]));
        AssertSameEdges([bobMember], await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));
        AssertSameNodes([bob, carol], await Driver.EntityNodeOps.GetByGroupIdsAsync([group]));
        await Driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid);
        await Driver.CommunityNodeOps.GetByUuidAsync(community.Uuid);

        // Delete by ids cascades too.
        await Driver.EntityNodeOps.DeleteByUuidsAsync([bob.Uuid]);
        Assert.Empty(await Driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.EpisodicEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task Deleting_an_episode_deletes_the_edges_that_touch_it()
    {
        var group = NewGroup();
        var saga = Saga(group, "saga");
        await Save(saga);
        var first = Episode(group, "first");
        var middle = Episode(group, "middle");
        var last = Episode(group, "last");
        await Save(first, middle, last);
        var alice = Entity(group, "Alice");
        await Save(alice);

        var mention = Mention(group, middle, alice);
        var otherMention = Mention(group, first, alice);
        await Save(mention, otherMention);
        var hasMiddle = HasEpisode(group, saga, middle);
        var hasFirst = HasEpisode(group, saga, first);
        await Save(hasMiddle, hasFirst);
        await Save(NextEpisode(group, first, middle), NextEpisode(group, middle, last));
        var firstToLast = NextEpisode(group, first, last);
        await Save(firstToLast);

        await Driver.EpisodeNodeOps.DeleteAsync(middle);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.EpisodeNodeOps.GetByUuidAsync(middle.Uuid));
        AssertSameEdges([otherMention], await Driver.EpisodicEdgeOps.GetByGroupIdsAsync([group]));
        AssertSameEdges([hasFirst], await Driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([group]));
        AssertSameEdges([firstToLast], await Driver.NextEpisodeEdgeOps.GetByGroupIdsAsync([group]));
        await Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid);
        await Driver.SagaNodeOps.GetByUuidAsync(saga.Uuid);
    }

    [Fact]
    public async Task Deleting_a_community_or_saga_deletes_its_edges_but_not_its_members()
    {
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        await Save(alice);
        var community = Community(group, "community");
        await Save(community);
        await Save(Member(group, community, alice));
        var saga = Saga(group, "saga");
        await Save(saga);
        var episode = Episode(group, "episode");
        await Save(episode);
        await Save(HasEpisode(group, saga, episode));

        await Driver.CommunityNodeOps.DeleteAsync(community);
        await Driver.SagaNodeOps.DeleteByUuidsAsync([saga.Uuid]);

        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.CommunityNodeOps.GetByUuidAsync(community.Uuid));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => Driver.SagaNodeOps.GetByUuidAsync(saga.Uuid));
        Assert.Empty(await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([group]));
        await Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid);
        await Driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid);
    }

    [Fact]
    public async Task Deleting_an_edge_leaves_its_nodes()
    {
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        await Save(alice, bob);
        var edge = Fact(group, alice, bob);
        var other = Fact(group, bob, alice);
        await Save(edge, other);

        await Driver.EntityEdgeOps.DeleteAsync(edge);

        await Assert.ThrowsAsync<EdgeNotFoundException>(() => Driver.EntityEdgeOps.GetByUuidAsync(edge.Uuid));
        AssertSameNodes([alice, bob], await Driver.EntityNodeOps.GetByGroupIdsAsync([group]));
        AssertSameEdges([other], await Driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task Delete_by_group_removes_only_that_type_in_that_group()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        var otherEntity = Entity(otherGroup, "Other");
        await Save(alice, bob, otherEntity);
        var episode = Episode(group, "episode");
        var otherEpisode = Episode(otherGroup, "other episode");
        await Save(episode, otherEpisode);
        var community = Community(group, "community");
        var otherCommunity = Community(otherGroup, "other community");
        await Save(community, otherCommunity);
        var saga = Saga(group, "saga");
        var otherSaga = Saga(otherGroup, "other saga");
        await Save(saga, otherSaga);
        await Save(Fact(group, alice, bob));
        await Save(Mention(group, episode, alice));
        await Save(Member(group, community, alice));
        await Save(HasEpisode(group, saga, episode));

        await Driver.EntityNodeOps.DeleteByGroupIdAsync(group);
        Assert.Empty(await Driver.EntityNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.EpisodicEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Single(await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Single(await Driver.CommunityNodeOps.GetByGroupIdsAsync([group]));
        Assert.Single(await Driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([group]));

        await Driver.EpisodeNodeOps.DeleteByGroupIdAsync(group);
        Assert.Empty(await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await Driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Single(await Driver.SagaNodeOps.GetByGroupIdsAsync([group]));

        await Driver.CommunityNodeOps.DeleteByGroupIdAsync(group);
        Assert.Empty(await Driver.CommunityNodeOps.GetByGroupIdsAsync([group]));

        await Driver.SagaNodeOps.DeleteByGroupIdAsync(group);
        Assert.Empty(await Driver.SagaNodeOps.GetByGroupIdsAsync([group]));

        AssertSameNodes([otherEntity], await Driver.EntityNodeOps.GetByGroupIdsAsync([otherGroup]));
        AssertSameNodes([otherEpisode], await Driver.EpisodeNodeOps.GetByGroupIdsAsync([otherGroup]));
        AssertSameNodes([otherCommunity], await Driver.CommunityNodeOps.GetByGroupIdsAsync([otherGroup]));
        AssertSameNodes([otherSaga], await Driver.SagaNodeOps.GetByGroupIdsAsync([otherGroup]));
    }

    // ---------------------------------------------------------------- bulk ingestion

    [Fact]
    public async Task Add_nodes_and_edges_bulk_saves_everything_with_embeddings()
    {
        var group = NewGroup();
        var episode = Episode(group, "Alice met Bob", validAt: Day(3));
        var alice = Entity(group, "Alice", labels: ["Person"], embedding: Vec(1, 0, 0));
        var bob = Entity(group, "Bob", labels: ["Person"], embedding: Vec(0, 1, 0));
        var fact = Fact(group, alice, bob, "MET", "Alice met Bob.", Vec(0, 0, 1), validAt: Day(3));
        fact.Episodes.Add(episode.Uuid);
        episode.EntityEdges.Add(fact.Uuid);
        var mentions = new[] { Mention(group, episode, alice), Mention(group, episode, bob) };

        await Driver.AddNodesAndEdgesBulkAsync([episode], mentions, [alice, bob], [fact]);

        AssertEpisode(episode, await Driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid));
        AssertEntity(alice, await Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid));
        AssertEntity(bob, await Driver.EntityNodeOps.GetByUuidAsync(bob.Uuid));
        var loadedFact = await Driver.EntityEdgeOps.GetByUuidAsync(fact.Uuid);
        AssertFact(fact, loadedFact);
        AssertSameEdges(mentions, await Driver.EpisodicEdgeOps.GetByGroupIdsAsync([group]));

        var nodes = (await Driver.EntityNodeOps.GetByUuidsAsync([alice.Uuid, bob.Uuid])).ToList();
        await Driver.EntityNodeOps.LoadEmbeddingsBulkAsync(nodes);
        Assert.Equal(alice.NameEmbedding, nodes.Single(n => n.Uuid == alice.Uuid).NameEmbedding);
        Assert.Equal(bob.NameEmbedding, nodes.Single(n => n.Uuid == bob.Uuid).NameEmbedding);
        await Driver.EntityEdgeOps.LoadEmbeddingsAsync(loadedFact);
        Assert.Equal(fact.FactEmbedding, loadedFact.FactEmbedding);

        // A second call upserts.
        alice.Summary = "Alice, updated";
        await Driver.AddNodesAndEdgesBulkAsync([], [], [alice], []);
        Assert.Equal("Alice, updated", (await Driver.EntityNodeOps.GetByUuidAsync(alice.Uuid)).Summary);
        Assert.Equal(2, (await Driver.EntityNodeOps.GetByGroupIdsAsync([group])).Count);
        await Driver.AddNodesAndEdgesBulkAsync([], [], [], []);
    }
}
