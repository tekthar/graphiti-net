// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Embedder;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Search;
using static GraphitiNet.Tests.GraphitiApi.GraphitiFixture;

namespace GraphitiNet.Tests.GraphitiApi;

/// <summary>graphiti-net additions: the group lock, the call context, per-call token usage and the model call
/// gate.</summary>
public class AdditionsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Concurrent triplets can see each other's fact as an invalidation candidate, which asks the LLM.
    private const string DedupeAnswer = """{"duplicate_facts": [], "contradicted_facts": []}""";

    /// <summary>An embedder whose calls wait for <see cref="Release"/>, counting how many run at once.</summary>
    private sealed class BlockingEmbedder : EmbedderClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Entries;

        public int MaxConcurrent;

        public void Release() => _release.TrySetResult();

        public override async Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
        {
            var entry = Interlocked.Increment(ref Entries);
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref MaxConcurrent, running);
            (entry == 1 ? FirstEntered : SecondEntered).TrySetResult();
            await _release.Task.WaitAsync(Timeout, cancellationToken);
            Interlocked.Decrement(ref _running);
            return await new HashEmbedder().CreateAsync(inputData, cancellationToken);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = target) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private static (EntityNode, EntityEdge, EntityNode) Triplet(string group)
    {
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        return (alice, Fact(group, alice, bob, "Alice manages Bob"), bob);
    }

    // Group lock (issue #1 item 3)

    [Fact]
    public async Task In_process_lock_serialises_one_group_and_not_others()
    {
        var groupLock = new InProcessGroupLock();
        var held = await groupLock.AcquireAsync("a");

        var sameGroup = groupLock.AcquireAsync("a").AsTask();
        var otherGroup = await groupLock.AcquireAsync("b").AsTask().WaitAsync(Timeout);
        Assert.False(sameGroup.IsCompleted);

        await held.DisposeAsync();
        await using var second = await sameGroup.WaitAsync(Timeout);
        await otherGroup.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_wait_does_not_take_the_lock()
    {
        var groupLock = new InProcessGroupLock();
        var held = await groupLock.AcquireAsync("a");
        using var cancellation = new CancellationTokenSource();
        var waiting = groupLock.AcquireAsync("a", cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        await held.DisposeAsync();
        await using var again = await groupLock.AcquireAsync("a").AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Add_episode_waits_for_the_groups_lock()
    {
        var gate = new DelegateModelCallGate((_, _) => ValueTask.FromResult(true));
        await using var graphiti = Create(out var driver, gate: gate);
        var group = NewGroup();

        var held = await graphiti.GroupLock.AcquireAsync(group);
        var adding = graphiti.AddEpisodeAsync("n", "Alice likes Bob", "desc", Now, groupId: group);
        await Task.Delay(50);

        // With a gate the episode is saved first thing inside the lock, so nothing saved means it is still waiting.
        Assert.False(adding.IsCompleted);
        Assert.Empty(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));

        await held.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => adding.WaitAsync(Timeout));
        Assert.Single(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task Two_writes_to_one_group_run_one_after_the_other()
    {
        var embedder = new BlockingEmbedder();
        var driver = new Driver.InMemory.InMemoryGraphDriver();
        await using var blocking = new Graphiti(driver, new ScriptedLlmClient("{}"), embedder, new LengthCrossEncoder(),
            groupLock: new InProcessGroupLock());
        var group = NewGroup();

        var (a1, e1, b1) = Triplet(group);
        var (a2, e2, b2) = Triplet(group);
        var first = blocking.AddTripletAsync(a1, e1, b1);
        await embedder.FirstEntered.Task.WaitAsync(Timeout);
        var second = blocking.AddTripletAsync(a2, e2, b2);
        await Task.Delay(50);
        Assert.False(embedder.SecondEntered.Task.IsCompleted);

        embedder.Release();
        await first.WaitAsync(Timeout);
        await second.WaitAsync(Timeout);
        Assert.Equal(1, embedder.MaxConcurrent);
    }

    [Fact]
    public async Task Without_the_lock_writes_to_one_group_overlap()
    {
        var embedder = new BlockingEmbedder();
        var driver = new Driver.InMemory.InMemoryGraphDriver();
        await using var graphiti = new Graphiti(driver, new ScriptedLlmClient(DedupeAnswer), embedder, new LengthCrossEncoder(),
            groupLock: NoGroupLock.Instance);
        var group = NewGroup();

        var (a1, e1, b1) = Triplet(group);
        var (a2, e2, b2) = Triplet(group);
        var first = graphiti.AddTripletAsync(a1, e1, b1);
        await embedder.FirstEntered.Task.WaitAsync(Timeout);
        var second = graphiti.AddTripletAsync(a2, e2, b2);
        await embedder.SecondEntered.Task.WaitAsync(Timeout);

        embedder.Release();
        await first.WaitAsync(Timeout);
        await second.WaitAsync(Timeout);
        Assert.Equal(2, embedder.MaxConcurrent);
    }

    // Call context (issue #1 item 4)

    [Fact]
    public async Task Llm_calls_see_the_operation_group_and_prompt()
    {
        var llm = new ScriptedLlmClient("""{"summary": "s"}""");
        await using var graphiti = Create(out var driver, llm);
        var group = NewGroup();
        var saga = new SagaNode { Name = "chat", GroupId = group, CreatedAt = Now };
        await driver.SagaNodeOps.SaveAsync(saga);
        var episode = Episode(group, "hello", Now);
        await driver.EpisodeNodeOps.SaveAsync(episode);
        await driver.HasEpisodeEdgeOps.SaveAsync(new HasEpisodeEdge
        {
            GroupId = group, SourceNodeUuid = saga.Uuid, TargetNodeUuid = episode.Uuid, CreatedAt = Now,
        });

        await graphiti.SummarizeSagaAsync(saga.Uuid);

        var context = Assert.Single(llm.Calls).Context!;
        Assert.Equal("summarize_saga", context.Operation);
        Assert.Equal(group, context.GroupId);
        Assert.Equal(PromptNames.SummarizeSagasSummarizeSaga, context.PromptName);
        Assert.Null(GraphitiCallContext.Current);
    }

    [Fact]
    public async Task Embedding_calls_see_the_search_context()
    {
        var embedder = new HashEmbedder();
        await using var graphiti = Create(out _, embedder: embedder);
        var group = NewGroup();

        await graphiti.SearchAsync("Alice", groupIds: [group]);

        var context = Assert.Single(embedder.Contexts)!;
        Assert.Equal("search", context.Operation);
        Assert.Equal(group, context.GroupId);
        Assert.Null(context.PromptName);
    }

    [Fact]
    public async Task Token_usage_is_measured_per_operation()
    {
        var llm = new ScriptedLlmClient("""{"summary": "s"}""") { InputTokensPerCall = 7, OutputTokensPerCall = 3 };
        var clients = new GraphitiClients
        {
            Driver = new Driver.InMemory.InMemoryGraphDriver(), LlmClient = llm, Embedder = new HashEmbedder(),
        };
        var context = new Dictionary<string, object?> { ["summary"] = "x" };

        async Task<GraphitiCallContext> RunAsync(int calls)
        {
            var callContext = new GraphitiCallContext { Operation = "test" };
            using var scope = GraphitiCallContext.Begin(callContext);
            for (var i = 0; i < calls; i++)
            {
                await clients.CompletePromptAsync(PromptNames.SummarizeNodesSummaryDescription, context);
            }

            return callContext;
        }

        var results = await Task.WhenAll(RunAsync(2), RunAsync(3));

        Assert.Equal(new GraphitiNet.Llm.TokenUsage(14, 6), results[0].Usage.GetTotalUsage());
        Assert.Equal(new GraphitiNet.Llm.TokenUsage(21, 9), results[1].Usage.GetTotalUsage());
        Assert.Equal(3, results[1].Usage.GetUsage()[PromptNames.SummarizeNodesSummaryDescription].CallCount);
        Assert.Equal(new GraphitiNet.Llm.TokenUsage(35, 15), llm.TokenTracker.GetTotalUsage());
        Assert.All(llm.Calls, call => Assert.Equal(PromptNames.SummarizeNodesSummaryDescription, call.Context!.PromptName));
    }

    // Model call gate (issue #1 item 5)

    [Fact]
    public async Task A_refused_llm_call_throws_and_is_not_made()
    {
        var seen = new List<ModelCallInfo>();
        var gate = new DelegateModelCallGate((call, _) =>
        {
            seen.Add(call);
            return ValueTask.FromResult(false);
        });
        var llm = new ScriptedLlmClient("""{"summary": "s"}""");
        await using var graphiti = Create(out var driver, llm, gate: gate);
        var group = NewGroup();
        var saga = new SagaNode { Name = "chat", GroupId = group, CreatedAt = Now };
        await driver.SagaNodeOps.SaveAsync(saga);
        var episode = Episode(group, "hello", Now);
        await driver.EpisodeNodeOps.SaveAsync(episode);
        await driver.HasEpisodeEdgeOps.SaveAsync(new HasEpisodeEdge
        {
            GroupId = group, SourceNodeUuid = saga.Uuid, TargetNodeUuid = episode.Uuid, CreatedAt = Now,
        });

        var error = await Assert.ThrowsAsync<ModelCallRefusedException>(() => graphiti.SummarizeSagaAsync(saga.Uuid));

        Assert.Empty(llm.Calls);
        Assert.Equal(ModelCallKind.Llm, error.Call.Kind);
        Assert.Equal(PromptNames.SummarizeSagasSummarizeSaga, error.Call.PromptName);
        Assert.Equal("summarize_saga", error.Call.Operation);
        Assert.Equal(group, error.Call.GroupId);
        Assert.Single(seen);
    }

    [Fact]
    public async Task A_refused_embedding_call_throws()
    {
        var embedder = new HashEmbedder();
        var gate = new DelegateModelCallGate((call, _) => ValueTask.FromResult(call.Kind != ModelCallKind.Embedding));
        await using var graphiti = Create(out _, embedder: embedder, gate: gate);

        var error = await Assert.ThrowsAsync<ModelCallRefusedException>(() => graphiti.SearchAsync("Alice"));

        Assert.Equal(ModelCallKind.Embedding, error.Call.Kind);
        Assert.Equal("search", error.Call.Operation);
        Assert.Equal(0, embedder.Calls);
        Assert.Same(embedder, graphiti.Embedder);
        Assert.NotSame(embedder, graphiti.Clients.Embedder);
    }

    [Fact]
    public async Task An_allowing_gate_sees_every_call()
    {
        var seen = new List<ModelCallInfo>();
        var gate = new DelegateModelCallGate((call, _) =>
        {
            lock (seen)
            {
                seen.Add(call);
            }

            return ValueTask.FromResult(true);
        });
        var embedder = new HashEmbedder();
        await using var graphiti = Create(out _, embedder: embedder, gate: gate);

        await graphiti.SearchAsync("Alice", SearchConfigRecipes.NodeHybridSearchCrossEncoder);

        Assert.Equal(1, embedder.Calls);
        Assert.Contains(seen, call => call.Kind == ModelCallKind.Embedding);
    }
    // Group isolation when reprocessing a stored episode

    [Fact]
    public async Task Reprocessing_by_uuid_without_a_group_works_in_the_episodes_group()
    {
        var llm = new ScriptedLlmClient("""{"extracted_entities": [], "edges": []}""");
        var groupLock = new InProcessGroupLock();
        await using var graphiti = Create(out var driver, llm, groupLock: groupLock);
        var group = NewGroup();
        var stored = Episode(group, "Alice likes Bob", Now);
        await driver.EpisodeNodeOps.SaveAsync(stored);
        await driver.EpisodeNodeOps.SaveAsync(Episode("", "SECRET of the default group", Now.AddMinutes(-1)));

        var held = await groupLock.AcquireAsync(group);
        var adding = graphiti.AddEpisodeAsync("ignored", "ignored", "ignored", Now, uuid: stored.Uuid);
        await Task.Delay(50);
        Assert.False(adding.IsCompleted);
        await held.DisposeAsync();
        var result = await adding.WaitAsync(Timeout);

        Assert.Equal(group, result.Episode.GroupId);
        Assert.NotEmpty(llm.Calls);
        Assert.All(llm.Calls, call =>
        {
            Assert.DoesNotContain("SECRET", call.Text, StringComparison.Ordinal);
            Assert.Equal(group, call.Context!.GroupId);
        });
    }

    [Fact]
    public async Task Reprocessing_by_uuid_in_another_group_is_refused()
    {
        var llm = new ScriptedLlmClient("{}");
        await using var graphiti = Create(out var driver, llm);
        var stored = Episode(NewGroup(), "Alice likes Bob", Now);
        await driver.EpisodeNodeOps.SaveAsync(stored);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            graphiti.AddEpisodeAsync("n", "b", "d", Now, groupId: NewGroup(), uuid: stored.Uuid));
        await Assert.ThrowsAsync<ArgumentException>(() => graphiti.AddEpisodeBulkAsync(
            [new RawEpisode { Name = "n", Content = "b", SourceDescription = "d", ReferenceTime = Now, Source = EpisodeType.Text, Uuid = stored.Uuid }],
            groupId: NewGroup()));
        Assert.Empty(llm.Calls);
    }

    // Other writes take the group lock

    [Fact]
    public async Task Summarize_saga_waits_for_the_groups_lock()
    {
        var llm = new ScriptedLlmClient("""{"summary": "s"}""");
        var groupLock = new InProcessGroupLock();
        await using var graphiti = Create(out var driver, llm, groupLock: groupLock);
        var group = NewGroup();
        var saga = new SagaNode { Name = "chat", GroupId = group, CreatedAt = Now };
        await driver.SagaNodeOps.SaveAsync(saga);
        var episode = Episode(group, "hello", Now);
        await driver.EpisodeNodeOps.SaveAsync(episode);
        await driver.HasEpisodeEdgeOps.SaveAsync(new HasEpisodeEdge
        {
            GroupId = group, SourceNodeUuid = saga.Uuid, TargetNodeUuid = episode.Uuid, CreatedAt = Now,
        });

        var held = await groupLock.AcquireAsync(group);
        var summarizing = graphiti.SummarizeSagaAsync(saga.Uuid);
        await Task.Delay(50);
        Assert.False(summarizing.IsCompleted);
        Assert.Empty(llm.Calls);

        await held.DisposeAsync();
        Assert.Equal("s", (await summarizing.WaitAsync(Timeout)).Summary);
    }

    [Fact]
    public async Task Clearing_every_group_waits_for_running_writes()
    {
        var groupLock = new InProcessGroupLock();
        await using var graphiti = Create(out var driver, groupLock: groupLock);
        var group = NewGroup();
        var (a, e, b) = Triplet(group);
        await driver.EntityNodeOps.SaveBulkAsync([a, b]);

        var held = await groupLock.AcquireAsync(group);
        var clearing = graphiti.ClearDataAsync();
        await Task.Delay(50);
        Assert.False(clearing.IsCompleted);

        await held.DisposeAsync();
        await clearing.WaitAsync(Timeout);
        Assert.Empty(await driver.EntityNodeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task Add_triplet_rejects_entities_from_another_group()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var other = NewGroup();
        var (a, e, _) = Triplet(group);
        var (_, _, foreign) = Triplet(other);

        await Assert.ThrowsAsync<ArgumentException>(() => graphiti.AddTripletAsync(a, e, foreign));

        // An entity stored in another group under the given id is rejected too.
        var (a2, e2, b2) = Triplet(group);
        var stored = new EntityNode { Uuid = b2.Uuid, Name = b2.Name, GroupId = other, CreatedAt = Now };
        await driver.EntityNodeOps.SaveAsync(stored);
        await Assert.ThrowsAsync<ArgumentException>(() => graphiti.AddTripletAsync(a2, e2, b2));
        Assert.Empty(await driver.EntityEdgeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task Build_communities_waits_for_the_groups_lock()
    {
        var groupLock = new InProcessGroupLock();
        await using var graphiti = Create(out _, groupLock: groupLock);
        var group = NewGroup();

        var held = await groupLock.AcquireAsync(group);
        var building = graphiti.BuildCommunitiesAsync([group]);
        await Task.Delay(50);
        Assert.False(building.IsCompleted);

        await held.DisposeAsync();
        await building.WaitAsync(Timeout);
    }
}
