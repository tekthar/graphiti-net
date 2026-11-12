// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet;

/// <summary>
/// Serialises writes to one group. <see cref="Graphiti"/> holds a group's lock for the whole of
/// <see cref="Graphiti.AddEpisodeAsync"/>, <see cref="Graphiti.AddEpisodeBulkAsync"/>,
/// <see cref="Graphiti.AddTripletAsync"/>, <see cref="Graphiti.RemoveEpisodeAsync"/> and
/// <see cref="Graphiti.ClearDataAsync"/>, so two episodes added to one group at once cannot both create the same
/// entity unseen by each other's deduplication. A graphiti-net addition: upstream leaves ordering to the caller
/// (its MCP server queues episodes per group).
/// </summary>
/// <remarks>
/// The default, <see cref="InProcessGroupLock.Shared"/>, works within one process. A host running several servers
/// against one database plugs in a distributed lock (a database advisory lock, Redis and so on). Locks are not
/// re-entrant: do not start another write to the same group from inside a model call of a running write.
/// </remarks>
public interface IGroupLock
{
    /// <summary>Waits until the group is free and takes it.</summary>
    /// <param name="groupId">The group.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>A handle that releases the group when disposed.</returns>
    ValueTask<IAsyncDisposable> AcquireAsync(string groupId, CancellationToken cancellationToken = default);
}

/// <summary>An in-process <see cref="IGroupLock"/>: one asynchronous mutex per group id.</summary>
public sealed class InProcessGroupLock : IGroupLock
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The lock every <see cref="Graphiti"/> instance in the process uses by default, so separate
    /// instances over one database also take turns.</summary>
    public static InProcessGroupLock Shared { get; } = new();

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        Entry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(groupId, out entry!))
            {
                entry = new Entry();
                _entries[groupId] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(groupId, entry, held: false);
            throw;
        }

        return new Handle(this, groupId, entry);
    }

    private void Release(string groupId, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_lock)
        {
            entry.References--;
            if (entry.References == 0)
            {
                _entries.Remove(groupId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class Handle(InProcessGroupLock owner, string groupId, Entry entry) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(groupId, entry, held: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A <see cref="IGroupLock"/> that never waits: writes to one group may run concurrently, as upstream.</summary>
public sealed class NoGroupLock : IGroupLock
{
    /// <summary>The instance.</summary>
    public static NoGroupLock Instance { get; } = new();

    private NoGroupLock()
    {
    }

    /// <inheritdoc/>
    public ValueTask<IAsyncDisposable> AcquireAsync(string groupId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IAsyncDisposable>(Released.Instance);

    private sealed class Released : IAsyncDisposable
    {
        public static Released Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
