// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver;
using GraphitiNet.Models;
using static GraphitiNet.Acceptance.Tests.Support;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>
/// Idempotency keys, tekthar/graphiti-net#9: a call that fails after its episode is saved can be retried with the
/// same key, and ends with one episode and no duplicate entities or facts. Runs in memory, on PostgreSQL and on SQL
/// Server.
/// </summary>
public class Issue9Tests
{
    private static readonly ScriptedEpisode Paris = new(
        "Alice lives in Paris.",
        ["Alice", "Paris"],
        [new("Alice", "LIVES_IN", "Paris", "Alice lives in Paris.")]);

    private static readonly ScriptedEpisode Acme = new(
        "Bob works at Acme.",
        ["Bob", "Acme"],
        [new("Bob", "WORKS_AT", "Acme", "Bob works at Acme.")]);

    private static readonly ScriptedEpisode Carol = new(
        "Carol joined Acme.",
        ["Carol", "Acme"],
        [new("Carol", "JOINED", "Acme", "Carol joined Acme.")]);

    [SkippableTheory]
    [MemberData(nameof(Drivers.Every), MemberType = typeof(Drivers))]
    public async Task A_call_that_failed_after_saving_its_episode_is_retried_with_its_key_into_one_episode(string driver)
    {
        var llm = new ScriptedLlm(Paris);
        var failures = 1;
        llm.BeforeAnswer = (prompt, _) =>
            prompt == "extract_edges.edge" && Interlocked.Decrement(ref failures) >= 0
                ? throw new InvalidOperationException("The provider failed mid-extraction.")
                : Task.CompletedTask;
        await using var h = await Harness.CreateAsync(driver, llm);
        var group = Harness.NewGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(() => AddKeyedAsync(h, group, Paris.Body, "msg-1"));

        // The episode is saved under its key, unprocessed.
        var saved = Assert.Single(await h.EpisodesAsync(group));
        Assert.Equal("msg-1", saved.IdempotencyKey);
        Assert.Empty(await h.EntitiesAsync(group));

        // The retry resumes it: still one episode, now with its entities and fact.
        var retried = await AddKeyedAsync(h, group, Paris.Body, "msg-1");
        Assert.False(retried.Replayed);
        Assert.Equal(saved.Uuid, retried.Episode.Uuid);
        Assert.Equal(saved.Uuid, Assert.Single(await h.EpisodesAsync(group)).Uuid);
        Assert.Equal(["Alice", "Paris"], (await h.EntitiesAsync(group)).Select(n => n.Name).Order());
        var fact = Assert.Single(await h.FactsAsync(group));
        Assert.Equal([saved.Uuid], fact.Episodes);

        // Once processed, the key replays the stored result without asking the model again.
        var calls = llm.Calls.Count;
        var replayed = await AddKeyedAsync(h, group, "a different body is ignored", "msg-1");
        Assert.True(replayed.Replayed);
        Assert.Equal(calls, llm.Calls.Count);
        Assert.Equal(0, replayed.TokenUsage.TotalTokens);
        Assert.Equal(saved.Uuid, replayed.Episode.Uuid);
        Assert.Equal(["Alice", "Paris"], replayed.Nodes.Select(n => n.Name).Order());
        Assert.Equal(fact.Uuid, Assert.Single(replayed.Edges).Uuid);
        Assert.Equal(2, replayed.EpisodicEdges.Count);
        Assert.Single(await h.EpisodesAsync(group));
        Assert.Equal(2, (await h.EntitiesAsync(group)).Count);
        Assert.Single(await h.FactsAsync(group));
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.Every), MemberType = typeof(Drivers))]
    public async Task A_call_that_failed_after_saving_its_entities_and_facts_is_finished_by_the_retry(string driver)
    {
        // The saga join comes after entities and facts are saved; make the first one fail.
        FailingSagaLinks? failing = null;
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(Paris),
            wrapDriver: inner => failing = new FailingSagaLinks(inner));
        var group = Harness.NewGroup();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AddKeyedAsync(h, group, Paris.Body, "msg-1", saga: "chat"));
        var saved = Assert.Single(await h.EpisodesAsync(group));
        Assert.Equal(2, (await h.EntitiesAsync(group)).Count);
        Assert.Single(await h.FactsAsync(group));

        var retried = await AddKeyedAsync(h, group, Paris.Body, "msg-1", saga: "chat");

        Assert.True(retried.Replayed);
        Assert.Equal(saved.Uuid, retried.Episode.Uuid);
        Assert.Single(await h.EpisodesAsync(group));
        Assert.Equal(2, (await h.EntitiesAsync(group)).Count);
        Assert.Single(await h.FactsAsync(group));

        // The saga join the first call did not finish is done once.
        var sagaNode = await h.Graphiti.Driver.SagaNodeOps.GetByNameAsync("chat", group);
        var members = await h.Graphiti.Driver.EpisodeNodeOps.GetBySagaAsync(sagaNode!.Uuid);
        Assert.Equal([saved.Uuid], members.Select(e => e.Uuid));
        await AddKeyedAsync(h, group, Paris.Body, "msg-1", saga: "chat");
        Assert.Single(await h.Graphiti.Driver.EpisodeNodeOps.GetBySagaAsync(sagaNode.Uuid));
        Assert.Equal(1, failing!.Failures);
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.Every), MemberType = typeof(Drivers))]
    public async Task Two_concurrent_calls_with_one_key_create_one_episode(string driver)
    {
        // Under the default group lock the second call waits, then replays the first one's result.
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(Paris));
        var group = Harness.NewGroup();

        var results = await Task.WhenAll(
            Task.Run(() => AddKeyedAsync(h, group, Paris.Body, "msg-1")),
            Task.Run(() => AddKeyedAsync(h, group, Paris.Body, "msg-1")));

        var episode = Assert.Single(await h.EpisodesAsync(group));
        Assert.All(results, r => Assert.Equal(episode.Uuid, r.Episode.Uuid));
        Assert.Single(results, r => r.Replayed);
        Assert.Equal(2, (await h.EntitiesAsync(group)).Count);
        Assert.Single(await h.FactsAsync(group));
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.Every), MemberType = typeof(Drivers))]
    public async Task Two_concurrent_calls_with_one_key_and_no_group_lock_create_one_episode(string driver)
    {
        // Without a lock both calls run at once; the unique key in storage decides which episode they both use.
        var llm = new ScriptedLlm(Paris);
        using var bothStarted = new Barrier(2);
        llm.BeforeAnswer = (prompt, cancellationToken) =>
        {
            if (prompt == "extract_nodes.extract_message")
            {
                bothStarted.SignalAndWait(WaitLimit, cancellationToken);
            }

            return Task.CompletedTask;
        };
        await using var h = await Harness.CreateAsync(driver, llm, groupLock: NoGroupLock.Instance);
        var group = Harness.NewGroup();

        var results = await Task.WhenAll(
            Task.Run(() => AddKeyedAsync(h, group, Paris.Body, "msg-1")),
            Task.Run(() => AddKeyedAsync(h, group, Paris.Body, "msg-1")));

        var episode = Assert.Single(await h.EpisodesAsync(group));
        Assert.Equal("msg-1", episode.IdempotencyKey);
        Assert.All(results, r => Assert.Equal(episode.Uuid, r.Episode.Uuid));
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.Every), MemberType = typeof(Drivers))]
    public async Task A_failed_bulk_call_is_retried_with_its_keys_into_one_episode_each(string driver)
    {
        var llm = new ScriptedLlm(Paris, Acme, Carol);
        var failures = 1;
        llm.BeforeAnswer = (prompt, _) =>
            prompt == "extract_edges.edge" && Interlocked.Decrement(ref failures) >= 0
                ? throw new InvalidOperationException("The provider failed mid-extraction.")
                : Task.CompletedTask;
        await using var h = await Harness.CreateAsync(driver, llm);
        var group = Harness.NewGroup();
        RawEpisode[] batch =
        [
            Raw(Paris.Body, Jan, "msg-1"),
            Raw(Acme.Body, Mar, "msg-2"),
        ];

        // Bulk ingestion saves its episodes first, as upstream; without keys a retry would add them again.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Graphiti.AddEpisodeBulkAsync(batch, group, useCombinedExtraction: false));
        var saved = await h.EpisodesAsync(group);
        Assert.Equal(2, saved.Count);

        var retried = await h.Graphiti.AddEpisodeBulkAsync(batch, group, useCombinedExtraction: false);
        Assert.Equal(saved.Select(e => e.Uuid).Order(), retried.Episodes.Select(e => e.Uuid).Order());
        Assert.Equal(2, (await h.EpisodesAsync(group)).Count);
        Assert.Equal(["Acme", "Alice", "Bob", "Paris"], (await h.EntitiesAsync(group)).Select(n => n.Name).Order());
        Assert.Equal(2, (await h.FactsAsync(group)).Count);

        // Retried again, every episode is already processed: nothing is extracted.
        var calls = llm.Calls.Count;
        var replayed = await h.Graphiti.AddEpisodeBulkAsync(batch, group, useCombinedExtraction: false);
        Assert.Equal(calls, llm.Calls.Count);
        Assert.Equal(retried.Episodes.Select(e => e.Uuid), replayed.Episodes.Select(e => e.Uuid));
        Assert.Equal(["Acme", "Alice", "Bob", "Paris"], replayed.Nodes.Select(n => n.Name).Order());
        Assert.Equal(2, replayed.Edges.Count);

        // A batch mixing processed and new keys extracts only the new episode.
        var mixed = await h.Graphiti.AddEpisodeBulkAsync([.. batch, Raw(Carol.Body, Jun, "msg-3")], group,
            useCombinedExtraction: false);
        Assert.Equal(3, mixed.Episodes.Count);
        Assert.Equal(3, (await h.EpisodesAsync(group)).Count);
        Assert.Equal(["Acme", "Alice", "Bob", "Carol", "Paris"], (await h.EntitiesAsync(group)).Select(n => n.Name).Order());
        Assert.Equal(3, (await h.FactsAsync(group)).Count);
        Assert.DoesNotContain(llm.Calls.Skip(calls), c => c.PromptName == "extract_nodes.extract_message"
                                                         && (c.Text.Contains(Paris.Body, StringComparison.Ordinal)
                                                             || c.Text.Contains(Acme.Body, StringComparison.Ordinal))
                                                         && !c.Text.Contains(Carol.Body, StringComparison.Ordinal));
    }

    private static Task<AddEpisodeResults> AddKeyedAsync(
        Harness h, string group, string body, string key, SagaReference? saga = null) =>
        h.Graphiti.AddEpisodeAsync(
            "episode",
            body,
            "chat message",
            Jan,
            EpisodeType.Message,
            groupId: group,
            saga: saga,
            idempotencyKey: key);

    private static RawEpisode Raw(string body, DateTimeOffset referenceTime, string key) => new()
    {
        Name = "episode",
        Content = body,
        SourceDescription = "chat message",
        Source = EpisodeType.Message,
        ReferenceTime = referenceTime,
        IdempotencyKey = key,
    };

    /// <summary>A driver whose first saga link fails, as a crash right after the entities and facts are saved.</summary>
    private sealed class FailingSagaLinks(IGraphDriver inner) : IGraphDriver
    {
        public int Failures { get; private set; }

        public string Provider => inner.Provider;

        public IEpisodeNodeOperations EpisodeNodeOps => inner.EpisodeNodeOps;

        public IEntityNodeOperations EntityNodeOps => inner.EntityNodeOps;

        public ICommunityNodeOperations CommunityNodeOps => inner.CommunityNodeOps;

        public ISagaNodeOperations SagaNodeOps => inner.SagaNodeOps;

        public IEpisodicEdgeOperations EpisodicEdgeOps => inner.EpisodicEdgeOps;

        public IEntityEdgeOperations EntityEdgeOps => inner.EntityEdgeOps;

        public ICommunityEdgeOperations CommunityEdgeOps => inner.CommunityEdgeOps;

        public IHasEpisodeEdgeOperations HasEpisodeEdgeOps => new FailOnce(this, inner.HasEpisodeEdgeOps);

        public INextEpisodeEdgeOperations NextEpisodeEdgeOps => inner.NextEpisodeEdgeOps;

        public ISearchOperations SearchOps => inner.SearchOps;

        public IGraphMaintenanceOperations GraphOps => inner.GraphOps;

        public Task AddNodesAndEdgesBulkAsync(
            IReadOnlyList<EpisodicNode> episodicNodes,
            IReadOnlyList<EpisodicEdge> episodicEdges,
            IReadOnlyList<EntityNode> entityNodes,
            IReadOnlyList<EntityEdge> entityEdges,
            CancellationToken cancellationToken = default) =>
            inner.AddNodesAndEdgesBulkAsync(episodicNodes, episodicEdges, entityNodes, entityEdges, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private sealed class FailOnce(FailingSagaLinks owner, IHasEpisodeEdgeOperations ops) : IHasEpisodeEdgeOperations
        {
            public Task SaveAsync(HasEpisodeEdge item, CancellationToken cancellationToken = default)
            {
                if (owner.Failures == 0)
                {
                    owner.Failures++;
                    throw new InvalidOperationException("The server went away before the saga link was saved.");
                }

                return ops.SaveAsync(item, cancellationToken);
            }

            public Task SaveBulkAsync(IReadOnlyList<HasEpisodeEdge> items, CancellationToken cancellationToken = default) =>
                ops.SaveBulkAsync(items, cancellationToken);

            public Task DeleteAsync(HasEpisodeEdge item, CancellationToken cancellationToken = default) =>
                ops.DeleteAsync(item, cancellationToken);

            public Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default) =>
                ops.DeleteByUuidsAsync(uuids, cancellationToken);

            public Task<HasEpisodeEdge> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default) =>
                ops.GetByUuidAsync(uuid, cancellationToken);

            public Task<IReadOnlyList<HasEpisodeEdge>> GetByUuidsAsync(
                IReadOnlyList<string> uuids, CancellationToken cancellationToken = default) =>
                ops.GetByUuidsAsync(uuids, cancellationToken);

            public Task<IReadOnlyList<HasEpisodeEdge>> GetByGroupIdsAsync(
                IReadOnlyList<string> groupIds,
                int? limit = null,
                string? uuidCursor = null,
                CancellationToken cancellationToken = default) =>
                ops.GetByGroupIdsAsync(groupIds, limit, uuidCursor, cancellationToken);
        }
    }
}
