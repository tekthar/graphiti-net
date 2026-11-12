// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/test_graphiti_mock.py (the parts that need no extraction), tests/test_request_scope_concurrency.py
// and tests/test_add_triplet.py (signature-level parts) (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Models;
using GraphitiNet.Search;
using static GraphitiNet.Tests.GraphitiApi.GraphitiFixture;

namespace GraphitiNet.Tests.GraphitiApi;

public class GraphitiTests
{
    private const string AwaitingWiring = "Needs NodeOperations/EdgeOperations wired into Graphiti.Seams.cs";

    // Construction

    [Fact]
    public async Task Constructor_exposes_the_clients()
    {
        var llm = new ScriptedLlmClient("{}");
        var embedder = new HashEmbedder();
        await using var graphiti = Create(out var driver, llm, embedder);

        Assert.Same(driver, graphiti.Driver);
        Assert.Same(llm, graphiti.LlmClient);
        Assert.Same(embedder, graphiti.Embedder);
        Assert.Same(llm.TokenTracker, graphiti.TokenTracker);
        Assert.Same(driver, graphiti.Clients.Driver);
        Assert.Same(embedder, graphiti.Clients.Embedder);
        Assert.True(graphiti.StoreRawEpisodeContent);
        Assert.Null(graphiti.MaxCoroutines);
        Assert.NotNull(graphiti.Nodes.Entity);
        Assert.NotNull(graphiti.Edges.NextEpisode);
    }

    [Fact]
    public async Task Create_in_memory_uses_the_in_memory_driver()
    {
        await using var graphiti = Graphiti.CreateInMemory(new ScriptedLlmClient("{}"), new HashEmbedder(),
            new LengthCrossEncoder());
        Assert.IsType<InMemoryGraphDriver>(graphiti.Driver);
        await graphiti.BuildIndicesAndConstraintsAsync();
    }

    [Fact]
    public void Driver_is_required() =>
        Assert.Throws<ArgumentNullException>(() => new Graphiti(null!, new ScriptedLlmClient("{}"), new HashEmbedder(),
            new LengthCrossEncoder()));

    // Request scope (test_request_scope_concurrency.py). graphiti-net has no per-group database, so the shared driver
    // serves every group.

    [Fact]
    public async Task Request_scope_defaults_to_the_providers_default_group()
    {
        await using var graphiti = Create(out var driver);
        var (groupId, scopedDriver, clients) = graphiti.ResolveRequestScope(null);
        Assert.Equal("", groupId);
        Assert.Same(driver, scopedDriver);
        Assert.Same(graphiti.Clients, clients);
        Assert.Equal("_", Graphiti.GetDefaultGroupId("falkordb"));
        Assert.Equal("", Graphiti.GetDefaultGroupId(InMemoryGraphDriver.ProviderName));
    }

    [Fact]
    public async Task Request_scope_keeps_the_shared_driver_for_any_group()
    {
        await using var graphiti = Create(out var driver);
        var scopes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => graphiti.ResolveRequestScope($"group-{i}"))));
        Assert.All(scopes, scope => Assert.Same(driver, scope.Driver));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"group-{i}"), scopes.Select(scope => scope.GroupId));
        Assert.Throws<GroupIdValidationException>(() => graphiti.ResolveRequestScope("bad group!"));
    }

    // Retrieval and search

    [Fact]
    public async Task Retrieve_episodes_returns_the_latest_before_the_reference_time()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var e1 = Episode(group, "Test message 1", Now.AddDays(-2), "test_episode_1");
        var e2 = Episode(group, "Test message 2", Now.AddDays(-4), "test_episode_2");
        var e3 = Episode(group, "Test message 3", Now.AddDays(-6), "test_episode_3");
        await driver.EpisodeNodeOps.SaveBulkAsync([e1, e2, e3]);

        var episodes = await graphiti.RetrieveEpisodesAsync(Now.AddDays(-3), lastN: 5, groupIds: [group],
            source: EpisodeType.Message);

        Assert.Equal(["test_episode_3", "test_episode_2"], episodes.Select(e => e.Name));
    }

    [Fact]
    public async Task Search_returns_facts_and_respects_the_limit()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        var facts = Enumerable.Range(0, 5).Select(i => Fact(group, alice, bob, $"Alice likes Bob number {i}")).ToList();
        await SaveAsync(driver, [], [], [alice, bob], facts);

        var edges = await graphiti.SearchAsync("Alice likes Bob", groupIds: [group], numResults: 3);
        Assert.Equal(3, edges.Count);
        Assert.All(edges, edge => Assert.Contains(edge.Uuid, facts.Select(f => f.Uuid)));

        // The shared recipe is not changed by the limit (upstream mutates it).
        Assert.Equal(SearchDefaults.DefaultSearchLimit, SearchConfigRecipes.EdgeHybridSearchRrf.Limit);

        var centred = await graphiti.SearchAsync("Alice likes Bob", centerNodeUuid: alice.Uuid, groupIds: [group]);
        Assert.Equal(5, centred.Count);
    }

    [Fact]
    public async Task Advanced_search_returns_every_scope()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var alice = Entity(group, "Alice", "Alice is an engineer");
        var bob = Entity(group, "Bob");
        await SaveAsync(driver, [], [], [alice, bob], [Fact(group, alice, bob, "Alice manages Bob")]);

        var results = await graphiti.SearchAsync("Alice", SearchConfigRecipes.CombinedHybridSearchRrf, [group]);
        Assert.NotEmpty(results.Edges);
        Assert.Contains(results.Nodes, node => node.Uuid == alice.Uuid);

        var advanced = await graphiti.SearchAdvancedAsync("Alice", groupIds: [group]);
        Assert.NotEmpty(advanced.Nodes);
    }

    [Fact]
    public async Task Get_nodes_and_edges_by_episode()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var episode = Episode(group, "Alice likes Bob", Now);
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        var edge = Fact(group, alice, bob, "Alice likes Bob", episode);
        episode.EntityEdges.Add(edge.Uuid);
        await SaveAsync(driver, [episode], [Mention(episode, alice), Mention(episode, bob)], [alice, bob], [edge]);

        var results = await graphiti.GetNodesAndEdgesByEpisodeAsync([episode.Uuid]);

        Assert.Equal([edge.Uuid], results.Edges.Select(e => e.Uuid));
        Assert.Equal(new HashSet<string> { alice.Uuid, bob.Uuid }, results.Nodes.Select(n => n.Uuid).ToHashSet());
    }

    // Namespaces

    [Fact]
    public async Task Namespaces_embed_on_save_and_read_back()
    {
        var embedder = new HashEmbedder();
        await using var graphiti = Create(out _, embedder: embedder);
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");

        await graphiti.Nodes.Entity.SaveAsync(alice);
        await graphiti.Nodes.Entity.SaveAsync(bob);
        Assert.NotNull(alice.NameEmbedding);
        var edge = await graphiti.Edges.Entity.SaveAsync(Fact(group, alice, bob, "Alice likes Bob"));
        Assert.NotNull(edge.FactEmbedding);
        Assert.Equal(3, embedder.Calls);

        var loaded = await graphiti.Nodes.Entity.GetByUuidAsync(alice.Uuid);
        Assert.Null(loaded.NameEmbedding);
        await graphiti.Nodes.Entity.LoadEmbeddingsAsync(loaded);
        Assert.Equal(alice.NameEmbedding, loaded.NameEmbedding);
        Assert.Equal([edge.Uuid], (await graphiti.Edges.Entity.GetBetweenNodesAsync(alice.Uuid, bob.Uuid))
            .Select(e => e.Uuid));

        await graphiti.Nodes.Entity.DeleteByGroupIdAsync(group);
        Assert.Empty(await graphiti.Nodes.Entity.GetByGroupIdsAsync([group]));
        Assert.Empty(await graphiti.Edges.Entity.GetByGroupIdsAsync([group]));
        await Assert.ThrowsAsync<NodeNotFoundException>(() => graphiti.Nodes.Saga.GetByUuidAsync("missing"));
    }

    // Communities

    [Fact]
    public async Task Build_communities_saves_named_and_embedded_communities()
    {
        var llm = new ScriptedLlmClient(call => call.Text.Contains("one sentence description", StringComparison.Ordinal)
            ? """{"description": "Alice and Bob"}"""
            : """{"summary": "Alice and Bob work together."}""");
        await using var graphiti = Create(out var driver, llm);
        var group = NewGroup();
        var alice = Entity(group, "Alice", "Alice summary");
        var bob = Entity(group, "Bob", "Bob summary");
        await SaveAsync(driver, [], [], [alice, bob], [Fact(group, alice, bob, "Alice works with Bob")]);

        var (communities, edges) = await graphiti.BuildCommunitiesAsync([group]);

        var community = Assert.Single(communities);
        Assert.Equal("Alice and Bob", community.Name);
        Assert.Equal(2, edges.Count);
        var stored = await driver.CommunityNodeOps.GetByUuidAsync(community.Uuid);
        await driver.CommunityNodeOps.LoadNameEmbeddingAsync(stored);
        Assert.NotNull(stored.NameEmbedding);
        Assert.All(llm.Calls, call => Assert.Equal("build_communities", call.Context!.Operation));

        // Rebuilding replaces the group's communities.
        await graphiti.BuildCommunitiesAsync([group]);
        Assert.Single(await driver.CommunityNodeOps.GetByGroupIdsAsync([group]));
    }

    // Removal (test_remove_episode, test_remove_episode_keeps_restated_fact)

    [Fact]
    public async Task Remove_episode_deletes_what_only_it_produced()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var episode = Episode(group, "Alice likes Bob", Now);
        var alice = Entity(group, "Alice", "Alice summary");
        var bob = Entity(group, "Bob", "Bob summary");
        var edge = Fact(group, alice, bob, "Alice likes Bob", episode);
        episode.EntityEdges.Add(edge.Uuid);
        var mentions = new[] { Mention(episode, alice), Mention(episode, bob) };
        await SaveAsync(driver, [episode], mentions, [alice, bob], [edge]);

        await graphiti.RemoveEpisodeAsync(episode.Uuid);

        Assert.Empty(await driver.EpisodeNodeOps.GetByUuidsAsync([episode.Uuid]));
        Assert.Empty(await driver.EntityNodeOps.GetByUuidsAsync([alice.Uuid, bob.Uuid]));
        Assert.Empty(await driver.EntityEdgeOps.GetByUuidsAsync([edge.Uuid]));
        Assert.Empty(await driver.EpisodicEdgeOps.GetByUuidsAsync(mentions.Select(m => m.Uuid).ToList()));

        // Adding the same records again works.
        await SaveAsync(driver, [episode], mentions, [alice, bob], [edge]);
        Assert.Equal(2, (await driver.EntityNodeOps.GetByUuidsAsync([alice.Uuid, bob.Uuid])).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remove_episode_keeps_a_restated_fact(bool removeFirstEpisodeFirst)
    {
        await using var graphiti = Create(out var driver,
            new ScriptedLlmClient("""{"summaries": [{"name": "Alice", "summary": "Alice summary"}, {"name": "Bob", "summary": "Bob summary"}]}"""));
        var group = NewGroup();
        var episode1 = Episode(group, "Alice likes Bob", Now, "episode1");
        var episode2 = Episode(group, "Alice likes Bob", Now, "episode2");
        var alice = Entity(group, "Alice", "Alice summary");
        var bob = Entity(group, "Bob", "Bob summary");
        var edge = Fact(group, alice, bob, "Alice likes Bob", episode1, episode2);
        var factEmbedding = await new HashEmbedder().CreateAsync(edge.Fact);
        edge.FactEmbedding = factEmbedding;
        episode1.EntityEdges.Add(edge.Uuid);
        episode2.EntityEdges.Add(edge.Uuid);
        await SaveAsync(driver, [episode1, episode2],
            [Mention(episode1, alice), Mention(episode1, bob), Mention(episode2, alice), Mention(episode2, bob)],
            [alice, bob], [edge]);

        var first = removeFirstEpisodeFirst ? episode1 : episode2;
        var second = removeFirstEpisodeFirst ? episode2 : episode1;

        await graphiti.RemoveEpisodeAsync(first.Uuid);

        var surviving = await driver.EntityEdgeOps.GetByUuidAsync(edge.Uuid);
        await driver.EntityEdgeOps.LoadEmbeddingsAsync(surviving);
        Assert.Equal([second.Uuid], surviving.Episodes);
        Assert.Equal(factEmbedding, surviving.FactEmbedding);
        Assert.Empty(await driver.EpisodeNodeOps.GetByUuidsAsync([first.Uuid]));
        Assert.Equal(2, (await driver.EntityNodeOps.GetByUuidsAsync([alice.Uuid, bob.Uuid])).Count);

        await graphiti.RemoveEpisodeAsync(second.Uuid);

        Assert.Empty(await driver.EntityEdgeOps.GetByUuidsAsync([edge.Uuid]));
        Assert.Empty(await driver.EntityNodeOps.GetByUuidsAsync([alice.Uuid, bob.Uuid]));
    }

    [Fact]
    public async Task Remove_episode_keeps_the_stored_summary_when_the_rebuild_fails()
    {
        // Until the summary seam is wired the rebuild throws, which must leave the stored summary, as upstream's
        // test_remove_episode_keeps_old_summary_when_rebuild_fails.
        var llm = new ScriptedLlmClient(_ => throw new InvalidOperationException("summary failure"));
        await using var graphiti = Create(out var driver, llm);
        var group = NewGroup();
        var removed = Episode(group, "Alice mentioned OLD_MARKER and spoke with Bob.", Now.AddDays(-3));
        var remaining = Episode(group, "Alice started the first remaining project.", Now.AddDays(-2));
        var alice = Entity(group, "Alice", "Alice has marker OLD_MARKER");
        var bob = Entity(group, "Bob", "Bob summary");
        var edge = Fact(group, alice, bob, "Alice has marker OLD_MARKER", removed);
        removed.EntityEdges.Add(edge.Uuid);
        await SaveAsync(driver, [removed, remaining],
            [Mention(removed, alice), Mention(removed, bob), Mention(remaining, alice)], [alice, bob], [edge]);

        await graphiti.RemoveEpisodeAsync(removed.Uuid);

        Assert.Empty(await driver.EntityNodeOps.GetByUuidsAsync([bob.Uuid]));
        Assert.Empty(await driver.EntityEdgeOps.GetByUuidsAsync([edge.Uuid]));
        Assert.Equal("Alice has marker OLD_MARKER", (await driver.EntityNodeOps.GetByUuidAsync(alice.Uuid)).Summary);
    }

    [Fact(Skip = AwaitingWiring)]
    public async Task Remove_episode_rebuilds_the_summary_from_the_remaining_episodes()
    {
        var llm = new ScriptedLlmClient("""{"summaries": [{"name": "Alice", "summary": "Alice works on the pilot project."}]}""");
        await using var graphiti = Create(out var driver, llm);
        var group = NewGroup();
        var episode1 = Episode(group, "Alice mentioned OLD_MARKER and spoke with Bob.", Now.AddDays(-1));
        var episode2 = Episode(group, "Alice works on the pilot project with Carol.", Now);
        var alice = Entity(group, "Alice", "Alice has marker OLD_MARKER\nAlice works on the pilot project.");
        var bob = Entity(group, "Bob", "Bob summary");
        var carol = Entity(group, "Carol", "Carol summary");
        var edge1 = Fact(group, alice, bob, "Alice has marker OLD_MARKER", episode1);
        var edge2 = Fact(group, alice, carol, "Alice works on the pilot project", episode2);
        episode1.EntityEdges.Add(edge1.Uuid);
        episode2.EntityEdges.Add(edge2.Uuid);
        await SaveAsync(driver, [episode1, episode2],
            [Mention(episode1, alice), Mention(episode1, bob), Mention(episode2, alice), Mention(episode2, carol)],
            [alice, bob, carol], [edge1, edge2]);

        await graphiti.RemoveEpisodeAsync(episode1.Uuid);

        var call = Assert.Single(llm.Calls);
        Assert.Contains(episode2.Content, call.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD_MARKER", call.Text, StringComparison.Ordinal);
        Assert.Equal("Alice works on the pilot project.", (await driver.EntityNodeOps.GetByUuidAsync(alice.Uuid)).Summary);
        Assert.Empty(await driver.EntityNodeOps.GetByUuidsAsync([bob.Uuid]));
        Assert.Single(await driver.EntityNodeOps.GetByUuidsAsync([carol.Uuid]));
        Assert.Single(await driver.EntityEdgeOps.GetByUuidsAsync([edge2.Uuid]));
    }

    [Fact(Skip = AwaitingWiring)]
    public async Task Remove_episode_rebuilds_the_summary_in_batches()
    {
        var responses = new Queue<string>([
            """{"summaries": [{"name": "Alice", "summary": "summary-1"}]}""",
            """{"summaries": [{"name": "Alice", "summary": "summary-2"}]}""",
        ]);
        var llm = new ScriptedLlmClient(_ => responses.Dequeue());
        await using var graphiti = Create(out var driver, llm);
        graphiti.SummaryRebuildBatch = 2;
        var group = NewGroup();
        var removed = Episode(group, "Alice mentioned OLD_MARKER and spoke with Bob.", Now.AddDays(-4));
        var oldest = Episode(group, "Alice started the first remaining project.", Now.AddDays(-3));
        var middle = Episode(group, "Alice continued the second remaining project.", Now.AddDays(-2));
        var newest = Episode(group, "Alice completed the newest remaining project.", Now.AddDays(-1));
        var alice = Entity(group, "Alice", "Alice original summary");
        var bob = Entity(group, "Bob", "Bob summary");
        var edge = Fact(group, alice, bob, "Alice has marker OLD_MARKER", removed);
        removed.EntityEdges.Add(edge.Uuid);
        await SaveAsync(driver, [removed, oldest, middle, newest],
            [Mention(removed, alice), Mention(removed, bob), Mention(oldest, alice), Mention(middle, alice),
                Mention(newest, alice)],
            [alice, bob], [edge]);

        await graphiti.RemoveEpisodeAsync(removed.Uuid);

        Assert.Equal("summary-2", (await driver.EntityNodeOps.GetByUuidAsync(alice.Uuid)).Summary);
        Assert.Equal(2, llm.Calls.Count);
        Assert.Contains(oldest.Content, llm.Calls[0].Text, StringComparison.Ordinal);
        Assert.Contains(middle.Content, llm.Calls[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(newest.Content, llm.Calls[0].Text, StringComparison.Ordinal);
        Assert.Contains(newest.Content, llm.Calls[1].Text, StringComparison.Ordinal);
        Assert.Contains("summary-1", llm.Calls[1].Text, StringComparison.Ordinal);
    }

    // Strict removal and group deletion (graphiti-net additions, issue #1 item 2)

    [Fact]
    public async Task Strict_removal_leaves_no_text_from_the_episode()
    {
        var llm = new ScriptedLlmClient("""{"summaries": [{"name": "Alice", "summary": "Alice works on the pilot project."}]}""");
        await using var graphiti = Create(out var driver, llm);
        var group = NewGroup();
        var removed = Episode(group, "Alice mentioned OLD_MARKER and works with Bob.", Now.AddDays(-1));
        var kept = Episode(group, "Alice works with Bob on the pilot project.", Now);
        var alice = Entity(group, "Alice", "Alice has marker OLD_MARKER and works on the pilot project.");
        alice.Attributes["code_name"] = "OLD_MARKER";
        var bob = Entity(group, "Bob", "Bob summary");
        // A merged fact both episodes state, whose text carries the removed episode's detail.
        var merged = Fact(group, alice, bob, "Alice works with Bob (OLD_MARKER)", removed, kept);
        removed.EntityEdges.Add(merged.Uuid);
        kept.EntityEdges.Add(merged.Uuid);
        await SaveAsync(driver, [removed, kept],
            [Mention(removed, alice), Mention(removed, bob), Mention(kept, alice), Mention(kept, bob)],
            [alice, bob], [merged]);
        var community = new CommunityNode
        {
            Name = "c", GroupId = group, CreatedAt = Now, Summary = "Alice has marker OLD_MARKER",
        };
        await driver.CommunityNodeOps.SaveAsync(community);
        await driver.CommunityEdgeOps.SaveAsync(new CommunityEdge
        {
            GroupId = group, SourceNodeUuid = community.Uuid, TargetNodeUuid = alice.Uuid, CreatedAt = Now,
        });
        var saga = new SagaNode
        {
            Name = "chat", GroupId = group, CreatedAt = Now, Summary = "OLD_MARKER was mentioned",
            FirstEpisodeUuid = removed.Uuid, LastEpisodeUuid = kept.Uuid, LastSummarizedAt = Now,
        };
        await driver.SagaNodeOps.SaveAsync(saga);
        await driver.HasEpisodeEdgeOps.SaveBulkAsync([
            new HasEpisodeEdge { GroupId = group, SourceNodeUuid = saga.Uuid, TargetNodeUuid = removed.Uuid, CreatedAt = Now },
            new HasEpisodeEdge { GroupId = group, SourceNodeUuid = saga.Uuid, TargetNodeUuid = kept.Uuid, CreatedAt = Now },
        ]);

        await graphiti.RemoveEpisodeAsync(removed.Uuid, strict: true);

        var facts = await driver.EntityEdgeOps.GetByGroupIdsAsync([group]);
        var entities = await driver.EntityNodeOps.GetByGroupIdsAsync([group]);
        var communities = await driver.CommunityNodeOps.GetByGroupIdsAsync([group]);
        var sagas = await driver.SagaNodeOps.GetByGroupIdsAsync([group]);
        Assert.DoesNotContain(facts, fact => fact.Fact.Contains("OLD_MARKER", StringComparison.Ordinal));
        Assert.DoesNotContain(entities, entity => entity.Summary.Contains("OLD_MARKER", StringComparison.Ordinal));
        Assert.DoesNotContain(entities, entity => entity.Attributes.Values.Any(v => v as string == "OLD_MARKER"));
        Assert.Empty(communities);
        var resetSaga = Assert.Single(sagas);
        Assert.Equal("", resetSaga.Summary);
        Assert.Null(resetSaga.LastSummarizedAt);
        Assert.Equal(kept.Uuid, resetSaga.FirstEpisodeUuid);
        Assert.Equal(kept.Uuid, resetSaga.LastEpisodeUuid);

        // The other episode and the entities it mentions survive.
        Assert.Single(await driver.EpisodeNodeOps.GetByUuidsAsync([kept.Uuid]));
        Assert.Equal(2, entities.Count);
    }

    [Fact]
    public async Task Default_removal_keeps_merged_facts_that_strict_removal_deletes()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var removed = Episode(group, "one", Now);
        var kept = Episode(group, "two", Now);
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        var merged = Fact(group, alice, bob, "Alice knows Bob", removed, kept);
        removed.EntityEdges.Add(merged.Uuid);
        await SaveAsync(driver, [removed, kept],
            [Mention(removed, alice), Mention(removed, bob), Mention(kept, alice), Mention(kept, bob)],
            [alice, bob], [merged]);

        await graphiti.RemoveEpisodeAsync(removed.Uuid);

        Assert.Equal([kept.Uuid], (await driver.EntityEdgeOps.GetByUuidAsync(merged.Uuid)).Episodes);
    }

    [Fact]
    public async Task Clear_data_removes_everything_in_the_group_only()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var other = NewGroup();
        foreach (var g in new[] { group, other })
        {
            var episode = Episode(g, "Alice likes Bob", Now);
            var alice = Entity(g, "Alice");
            var bob = Entity(g, "Bob");
            await SaveAsync(driver, [episode], [Mention(episode, alice), Mention(episode, bob)], [alice, bob],
                [Fact(g, alice, bob, "Alice likes Bob", episode)]);
            var saga = new SagaNode { Name = "chat", GroupId = g, CreatedAt = Now };
            await driver.SagaNodeOps.SaveAsync(saga);
            await driver.HasEpisodeEdgeOps.SaveAsync(new HasEpisodeEdge
            {
                GroupId = g, SourceNodeUuid = saga.Uuid, TargetNodeUuid = episode.Uuid, CreatedAt = Now,
            });
            var community = new CommunityNode { Name = "c", GroupId = g, CreatedAt = Now };
            await driver.CommunityNodeOps.SaveAsync(community);
            await driver.CommunityEdgeOps.SaveAsync(new CommunityEdge
            {
                GroupId = g, SourceNodeUuid = community.Uuid, TargetNodeUuid = alice.Uuid, CreatedAt = Now,
            });
        }

        await graphiti.ClearDataAsync([group]);

        Assert.Empty(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.EntityNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.EpisodicEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.SagaNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.CommunityNodeOps.GetByGroupIdsAsync([group]));
        Assert.Empty(await driver.CommunityEdgeOps.GetByGroupIdsAsync([group]));

        Assert.Single(await driver.EpisodeNodeOps.GetByGroupIdsAsync([other]));
        Assert.Equal(2, (await driver.EntityNodeOps.GetByGroupIdsAsync([other])).Count);
        Assert.Single(await driver.EntityEdgeOps.GetByGroupIdsAsync([other]));
        Assert.Single(await driver.SagaNodeOps.GetByGroupIdsAsync([other]));
        Assert.Single(await driver.CommunityNodeOps.GetByGroupIdsAsync([other]));
    }

    // add_episode: validation and the parts around extraction

    [Fact]
    public async Task Add_episode_validates_its_arguments()
    {
        await using var graphiti = Create(out _);
        await Assert.ThrowsAsync<GroupIdValidationException>(() =>
            graphiti.AddEpisodeAsync("n", "body", "desc", Now, groupId: "bad group!"));
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            graphiti.AddEpisodeAsync("n", "body", "desc", Now, excludedEntityTypes: ["Unknown"]));
        Assert.StartsWith("Invalid excluded entity types: ['Unknown']. Available types: ['Entity']", error.Message,
            StringComparison.Ordinal);
        await Assert.ThrowsAsync<EntityTypeValidationException>(() =>
            graphiti.AddEpisodeAsync("n", "body", "desc", Now,
                entityTypes: new Dictionary<string, Type> { ["Person"] = typeof(ProtectedPerson) }));
        await Assert.ThrowsAsync<NodeNotFoundException>(() =>
            graphiti.AddEpisodeAsync("n", "body", "desc", Now, uuid: "missing"));
    }

    [Fact]
    public async Task Add_episode_honours_cancellation()
    {
        await using var graphiti = Create(out _);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            graphiti.AddEpisodeAsync("n", "body", "desc", Now, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task With_a_gate_the_episode_is_saved_before_any_model_call()
    {
        var gate = new DelegateModelCallGate((_, _) => ValueTask.FromResult(false));
        await using var graphiti = Create(out var driver, gate: gate);
        var group = NewGroup();

        // The call fails (today at the extraction seam; once wired, with ModelCallRefusedException), and the episode
        // stays saved, unprocessed, ready to be processed later by uuid.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            graphiti.AddEpisodeAsync("n", "Alice likes Bob", "desc", Now, groupId: group,
                episodeMetadata: new Dictionary<string, object?> { ["class"] = "restricted" }));

        var episode = Assert.Single(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Equal("Alice likes Bob", episode.Content);
        Assert.Equal("restricted", episode.EpisodeMetadata!["class"]);
        Assert.Empty(episode.EntityEdges);
    }

    [Fact]
    public async Task Without_a_gate_nothing_is_saved_when_processing_fails()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            graphiti.AddEpisodeAsync("n", "Alice likes Bob", "desc", Now, groupId: group));
        Assert.Empty(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
    }

    // add_triplet: signature-level parts (test_add_triplet.py needs resolve_extracted_edge)

    [Fact]
    public async Task Add_triplet_embeds_missing_embeddings_first()
    {
        var embedder = new HashEmbedder();
        await using var graphiti = Create(out var driver, embedder: embedder);
        var group = NewGroup();
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        await SaveAsync(driver, [], [], [alice, bob], []);
        var edge = Fact(group, alice, bob, "Alice manages Bob");

        // The body stops at the first EdgeOperations seam until it is wired; the embeddings are already set.
        await Assert.ThrowsAnyAsync<Exception>(() => graphiti.AddTripletAsync(alice, edge, bob));
        Assert.NotNull(edge.FactEmbedding);
        Assert.Equal(alice.Uuid, edge.SourceNodeUuid);
        Assert.Equal(bob.Uuid, edge.TargetNodeUuid);
    }

    private sealed class ProtectedPerson
    {
        public string? Summary { get; init; }
    }
}
