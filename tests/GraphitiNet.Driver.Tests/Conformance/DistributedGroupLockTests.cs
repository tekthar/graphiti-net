// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Acceptance.Tests;
using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

/// <summary>
/// Tests for an <see cref="IGroupLock"/> that holds across processes (issue #7). Separate lock objects stand in for
/// separate app servers: each opens its own database sessions, tagged with an application name so the tests can count
/// and kill them. A backend subclasses this in its own test project.
/// </summary>
public abstract class DistributedGroupLockTests : IAsyncLifetime
{
    /// <summary>How long any one wait may take before the test fails.</summary>
    protected static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StillWaiting = TimeSpan.FromMilliseconds(400);

    private static readonly ScriptedEpisode AliceChess = new(
        "Alice joined the chess club.",
        ["Alice", "chess club"],
        [new("Alice", "MEMBER_OF", "chess club", "Alice is a member of the chess club.")]);

    private static readonly ScriptedEpisode AliceBike = new(
        "Alice bought a new bike.",
        ["Alice", "bike"],
        [new("Alice", "OWNS", "bike", "Alice owns a new bike.")]);

    private bool _schemaBuilt;

    /// <summary>A schema of this test's own.</summary>
    protected string Schema { get; } = "lock_" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>Why the database can't be used, or <see langword="null"/>.</summary>
    protected abstract string? SkipReason { get; }

    /// <summary>A lock over <paramref name="schema"/> whose connections carry <paramref name="applicationName"/>.</summary>
    protected abstract IGroupLock CreateLock(string applicationName, string schema);

    /// <summary>A driver over <paramref name="schema"/> storing <see cref="WordHashEmbedder.Dimension"/>-sized
    /// vectors.</summary>
    protected abstract IGraphDriver CreateDriver(string schema);

    /// <summary>Drops a schema and its tables.</summary>
    protected abstract Task DropSchemaAsync(string schema);

    /// <summary>The number of open database sessions with this application name.</summary>
    protected abstract Task<int> CountSessionsAsync(string applicationName);

    /// <summary>Ends every database session with this application name from the server side.</summary>
    protected abstract Task KillSessionsAsync(string applicationName);

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_schemaBuilt)
        {
            await DropSchemaAsync(Schema);
        }
    }

    private static string NewApplication() => "glt_" + Guid.NewGuid().ToString("N")[..12];

    private static string NewGroup() => "g" + Guid.NewGuid().ToString("N")[..12];

    private void SkipIfUnavailable() => Skip.If(SkipReason is not null, SkipReason);

    private async Task WaitForSessionsAsync(string applicationName, int count)
    {
        using var limit = new CancellationTokenSource(WaitLimit);
        while (await CountSessionsAsync(applicationName) != count)
        {
            await Task.Delay(50, limit.Token);
        }
    }

    // ---- The lock on its own ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Two_locks_take_turns_on_one_group_and_not_on_others()
    {
        SkipIfUnavailable();
        var serverA = CreateLock(NewApplication(), Schema);
        var serverB = CreateLock(NewApplication(), Schema);
        var group = NewGroup();

        var held = await serverA.AcquireAsync(group);
        var waiting = serverB.AcquireAsync(group).AsTask();
        await using (await serverB.AcquireAsync(NewGroup()).AsTask().WaitAsync(WaitLimit))
        {
        }

        await Task.Delay(StillWaiting);
        Assert.False(waiting.IsCompleted);

        await held.DisposeAsync();
        await using var second = await waiting.WaitAsync(WaitLimit);
    }

    [SkippableFact]
    public async Task Graphs_in_different_schemas_do_not_share_locks()
    {
        SkipIfUnavailable();
        var group = NewGroup();
        await using var first = await CreateLock(NewApplication(), Schema).AcquireAsync(group);
        await using var second = await CreateLock(NewApplication(), Schema + "_other").AcquireAsync(group).AsTask()
            .WaitAsync(WaitLimit);
    }

    [SkippableFact]
    public async Task Waiters_in_one_process_queue_without_a_connection_each()
    {
        SkipIfUnavailable();
        var application = NewApplication();
        var groupLock = CreateLock(application, Schema);
        var group = NewGroup();

        var held = await groupLock.AcquireAsync(group);
        var waiting = new[] { groupLock.AcquireAsync(group).AsTask(), groupLock.AcquireAsync(group).AsTask() };
        await Task.Delay(StillWaiting);
        Assert.DoesNotContain(waiting, w => w.IsCompleted);
        Assert.Equal(1, await CountSessionsAsync(application));

        await held.DisposeAsync();
        var next = await Task.WhenAny(waiting).WaitAsync(WaitLimit);
        await (await next).DisposeAsync();
        await (await waiting.Single(w => w != next).WaitAsync(WaitLimit)).DisposeAsync();
        await WaitForSessionsAsync(application, 0);
    }

    [SkippableFact]
    public async Task A_cancelled_wait_leaves_no_connection_and_no_lock()
    {
        SkipIfUnavailable();
        var holder = CreateLock(NewApplication(), Schema);
        var waiterApplication = NewApplication();
        var waiter = CreateLock(waiterApplication, Schema);
        var group = NewGroup();

        var held = await holder.AcquireAsync(group);
        using var cancellation = new CancellationTokenSource();
        var waiting = waiter.AcquireAsync(group, cancellation.Token).AsTask();
        await WaitForSessionsAsync(waiterApplication, 1); // waiting inside the database
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(WaitLimit));
        await WaitForSessionsAsync(waiterApplication, 0);

        await held.DisposeAsync();
        await using var again = await waiter.AcquireAsync(group).AsTask().WaitAsync(WaitLimit);
    }

    [SkippableFact]
    public async Task The_lock_is_released_when_the_holders_connection_drops()
    {
        SkipIfUnavailable();
        var holderApplication = NewApplication();
        var holder = CreateLock(holderApplication, Schema);
        var other = CreateLock(NewApplication(), Schema);
        var group = NewGroup();

        var held = await holder.AcquireAsync(group);
        var waiting = other.AcquireAsync(group).AsTask();
        await Task.Delay(StillWaiting);
        Assert.False(waiting.IsCompleted);

        await KillSessionsAsync(holderApplication);
        await using var taken = await waiting.WaitAsync(WaitLimit);

        // The holder finds its connection gone; releasing does not throw, twice.
        await held.DisposeAsync();
        await held.DisposeAsync();
    }

    [SkippableFact]
    public async Task Disposing_a_handle_twice_releases_once()
    {
        SkipIfUnavailable();
        var groupLock = CreateLock(NewApplication(), Schema);
        var other = CreateLock(NewApplication(), Schema);
        var group = NewGroup();

        var first = await groupLock.AcquireAsync(group);
        await first.DisposeAsync();
        var second = await other.AcquireAsync(group).AsTask().WaitAsync(WaitLimit);
        await first.DisposeAsync(); // must not release the other holder's lock

        var third = groupLock.AcquireAsync(group).AsTask();
        await Task.Delay(StillWaiting);
        Assert.False(third.IsCompleted);
        await second.DisposeAsync();
        await (await third.WaitAsync(WaitLimit)).DisposeAsync();
    }

    // ---- Two Graphiti instances, as two app servers ----------------------------------------------------------

    /// <summary>Holds every entity-extraction call, of either instance, until released.</summary>
    private sealed class ExtractionBlocker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task OnAnswerAsync(string prompt, CancellationToken cancellationToken)
        {
            if (prompt != "extract_nodes.extract_message")
            {
                return;
            }

            var entry = Interlocked.Increment(ref _entries);
            (entry == 1 ? FirstEntered : SecondEntered).TrySetResult();
            await _release.Task.WaitAsync(WaitLimit, cancellationToken);
        }
    }

    private async Task<(Graphiti Server, IGraphDriver Driver)> CreateServerAsync(ExtractionBlocker blocker)
    {
        var driver = CreateDriver(Schema);
        var llm = new ScriptedLlm(AliceChess, AliceBike) { BeforeAnswer = blocker.OnAnswerAsync };
        var graphiti = new Graphiti(
            driver,
            llm,
            new WordHashEmbedder(),
            new WordOverlapCrossEncoder(),
            groupLock: CreateLock(NewApplication(), Schema));
        if (!_schemaBuilt)
        {
            await graphiti.BuildIndicesAndConstraintsAsync();
            _schemaBuilt = true;
        }

        return (graphiti, driver);
    }

    private static Task<AddEpisodeResults> AddAsync(Graphiti server, string group, string body) =>
        server.AddEpisodeAsync(
            "episode",
            body,
            "chat message",
            new DateTimeOffset(2026, 1, 10, 9, 0, 0, TimeSpan.Zero),
            EpisodeType.Message,
            groupId: group);

    [SkippableFact]
    public async Task Two_servers_ingest_into_one_group_one_at_a_time()
    {
        SkipIfUnavailable();
        var blocker = new ExtractionBlocker();
        var (serverA, driver) = await CreateServerAsync(blocker);
        await using var a = serverA;
        var (serverB, _) = await CreateServerAsync(blocker);
        await using var b = serverB;
        var group = NewGroup();

        var first = AddAsync(serverA, group, AliceChess.Body);
        await blocker.FirstEntered.Task.WaitAsync(WaitLimit);
        var second = AddAsync(serverB, group, AliceBike.Body);

        // The second server waits for the group while the first is inside a model call.
        await Task.Delay(StillWaiting);
        Assert.False(blocker.SecondEntered.Task.IsCompleted);
        Assert.False(second.IsCompleted);

        blocker.Release();
        var results = await Task.WhenAll(first, second).WaitAsync(WaitLimit);

        // Run one after the other, the second add found the first's Alice instead of making another.
        var alice = Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([group]), n => n.Name == "Alice");
        Assert.All(results, r => Assert.Contains(r.Nodes, n => n.Uuid == alice.Uuid));
    }

    [SkippableFact]
    public async Task Two_servers_ingest_into_different_groups_at_the_same_time()
    {
        SkipIfUnavailable();
        var blocker = new ExtractionBlocker();
        var (serverA, driver) = await CreateServerAsync(blocker);
        await using var a = serverA;
        var (serverB, _) = await CreateServerAsync(blocker);
        await using var b = serverB;
        var groupA = NewGroup();
        var groupB = NewGroup();

        var first = AddAsync(serverA, groupA, AliceChess.Body);
        await blocker.FirstEntered.Task.WaitAsync(WaitLimit);
        var second = AddAsync(serverB, groupB, AliceBike.Body);

        // Both are inside a model call at once.
        await blocker.SecondEntered.Task.WaitAsync(WaitLimit);

        blocker.Release();
        await Task.WhenAll(first, second).WaitAsync(WaitLimit);
        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([groupA]), n => n.Name == "Alice");
        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([groupB]), n => n.Name == "Alice");
    }
}
