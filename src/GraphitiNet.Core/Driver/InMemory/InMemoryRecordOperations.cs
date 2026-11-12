// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.InMemory;

/// <summary>Storage operations shared by every record type.</summary>
/// <typeparam name="T">The record type.</typeparam>
internal abstract class InMemoryRecordOperations<T>(InMemoryStore store) : IRecordOperations<T>
    where T : class
{
    protected InMemoryStore Store { get; } = store;

    /// <summary>Where records of this type live.</summary>
    protected abstract Dictionary<string, T> Items { get; }

    protected abstract string UuidOf(T item);

    protected abstract string GroupOf(T item);

    /// <summary>The normalised copy to store.</summary>
    protected abstract T ToStored(T item);

    /// <summary>The copy to return to callers, without embeddings.</summary>
    protected abstract T ToResult(T stored);

    protected abstract Exception NotFound(string uuid);

    /// <summary>Removes one record (and, for nodes, the edges that touch it). Called with the lock held.</summary>
    protected virtual void Remove(string uuid) => Items.Remove(uuid);

    public Task SaveAsync(T item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        var stored = ToStored(item);
        lock (Store.Gate)
        {
            Items[UuidOf(stored)] = stored;
        }

        return Task.CompletedTask;
    }

    public Task SaveBulkAsync(IReadOnlyList<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        var stored = items.Select(ToStored).ToList();
        lock (Store.Gate)
        {
            foreach (var item in stored)
            {
                Items[UuidOf(item)] = item;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(T item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return DeleteByUuidsAsync([UuidOf(item)], cancellationToken);
    }

    public Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            foreach (var uuid in uuids)
            {
                Remove(uuid);
            }
        }

        return Task.CompletedTask;
    }

    public Task<T> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uuid);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            return Items.TryGetValue(uuid, out var stored)
                ? Task.FromResult(ToResult(stored))
                : Task.FromException<T>(NotFound(uuid));
        }
    }

    public Task<IReadOnlyList<T>> GetByUuidsAsync(
        IReadOnlyList<string> uuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            IReadOnlyList<T> result = uuids
                .Distinct(StringComparer.Ordinal)
                .Select(uuid => Items.TryGetValue(uuid, out var stored) ? ToResult(stored) : null)
                .OfType<T>()
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<T>> GetByGroupIdsAsync(
        IReadOnlyList<string> groupIds,
        int? limit = null,
        string? uuidCursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        cancellationToken.ThrowIfCancellationRequested();
        var groups = groupIds.ToHashSet(StringComparer.Ordinal);
        lock (Store.Gate)
        {
            var query = Items.Values.Where(item => groups.Contains(GroupOf(item)));
            if (!string.IsNullOrEmpty(uuidCursor))
            {
                query = query.Where(item => string.CompareOrdinal(UuidOf(item), uuidCursor) < 0);
            }

            query = query.OrderByDescending(UuidOf, StringComparer.Ordinal);
            if (limit is { } max)
            {
                query = query.Take(Math.Max(max, 0));
            }

            IReadOnlyList<T> result = query.Select(ToResult).ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>Runs <paramref name="read"/> under the lock and returns its result as a completed task.</summary>
    protected Task<TResult> Read<TResult>(Func<TResult> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            return Task.FromResult(read());
        }
    }
}

/// <summary>Storage operations shared by node types.</summary>
internal abstract class InMemoryNodeOperations<T>(InMemoryStore store)
    : InMemoryRecordOperations<T>(store), INodeOperations<T>
    where T : Node
{
    protected override string UuidOf(T item) => item.Uuid;

    protected override string GroupOf(T item) => item.GroupId;

    protected override Exception NotFound(string uuid) => new NodeNotFoundException(uuid);

    protected override void Remove(string uuid) => Store.RemoveNode(Items, uuid);

    public Task DeleteByGroupIdAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            Store.RemoveNodesInGroups(Items, new HashSet<string>(StringComparer.Ordinal) { groupId });
        }

        return Task.CompletedTask;
    }
}

/// <summary>Storage operations shared by edge types.</summary>
internal abstract class InMemoryEdgeOperations<T>(InMemoryStore store) : InMemoryRecordOperations<T>(store)
    where T : Edge
{
    protected override string UuidOf(T item) => item.Uuid;

    protected override string GroupOf(T item) => item.GroupId;

    protected override Exception NotFound(string uuid) => new EdgeNotFoundException(uuid);
}

internal sealed class InMemoryEpisodeNodeOperations(InMemoryStore store)
    : InMemoryNodeOperations<EpisodicNode>(store), IEpisodeNodeOperations
{
    protected override Dictionary<string, EpisodicNode> Items => Store.Episodes;

    protected override EpisodicNode ToStored(EpisodicNode item) => RecordCopies.Copy(item);

    protected override EpisodicNode ToResult(EpisodicNode stored) => RecordCopies.Copy(stored);

    public Task<IReadOnlyList<EpisodicNode>> GetByEntityNodeUuidAsync(
        string entityNodeUuid,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<EpisodicNode>>(
            () => Store.EpisodicEdges.Values
                .Where(e => e.TargetNodeUuid == entityNodeUuid && Store.Entities.ContainsKey(e.TargetNodeUuid))
                .Select(e => e.SourceNodeUuid)
                .Distinct(StringComparer.Ordinal)
                .Select(uuid => Store.Episodes.GetValueOrDefault(uuid))
                .OfType<EpisodicNode>()
                .OrderBy(n => n.ValidAt)
                .ThenBy(n => n.Uuid, StringComparer.Ordinal)
                .Select(RecordCopies.Copy)
                .ToList(),
            cancellationToken);

    public Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        DateTimeOffset referenceTime,
        int lastN = 3,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<EpisodicNode>>(
            () =>
            {
                // As upstream: an empty group list does not restrict.
                var groups = groupIds is { Count: > 0 } ? groupIds.ToHashSet(StringComparer.Ordinal) : null;
                IEnumerable<EpisodicNode> episodes = Store.Episodes.Values;
                if (saga is not null)
                {
                    // As upstream: the saga is looked up in the first group only, so no group ids find nothing.
                    var sagaGroup = groupIds is { Count: > 0 } ? groupIds[0] : null;
                    var sagaUuids = Store.Sagas.Values
                        .Where(s => s.Name == saga && s.GroupId == sagaGroup)
                        .Select(s => s.Uuid)
                        .ToHashSet(StringComparer.Ordinal);
                    var episodeUuids = Store.HasEpisodeEdges.Values
                        .Where(e => sagaUuids.Contains(e.SourceNodeUuid))
                        .Select(e => e.TargetNodeUuid)
                        .ToHashSet(StringComparer.Ordinal);
                    episodes = episodes.Where(e => episodeUuids.Contains(e.Uuid));
                }

                var latest = episodes
                    .Where(e => e.ValidAt <= referenceTime)
                    .Where(e => saga is not null || groups is null || groups.Contains(e.GroupId))
                    .Where(e => source is null || e.Source == source)
                    .OrderByDescending(e => e.ValidAt)
                    .ThenByDescending(e => e.CreatedAt)
                    .ThenByDescending(e => e.Uuid, StringComparer.Ordinal)
                    .Take(Math.Max(lastN, 0))
                    .Select(RecordCopies.Copy)
                    .ToList();
                latest.Reverse();
                return latest;
            },
            cancellationToken);

    public Task<IReadOnlyList<EpisodicNode>> GetBySagaAsync(
        string sagaUuid,
        DateTimeOffset? createdAfter = null,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<EpisodicNode>>(
            () => SagaEpisodes(Store, sagaUuid)
                .Where(e => createdAfter is null || e.CreatedAt > createdAfter)
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.ValidAt)
                .ThenBy(e => e.Uuid, StringComparer.Ordinal)
                .Select(RecordCopies.Copy)
                .ToList(),
            cancellationToken);

    internal static IEnumerable<EpisodicNode> SagaEpisodes(InMemoryStore store, string sagaUuid) =>
        store.HasEpisodeEdges.Values
            .Where(e => e.SourceNodeUuid == sagaUuid && store.Sagas.ContainsKey(sagaUuid))
            .Select(e => e.TargetNodeUuid)
            .Distinct(StringComparer.Ordinal)
            .Select(uuid => store.Episodes.GetValueOrDefault(uuid))
            .OfType<EpisodicNode>();
}

internal sealed class InMemoryEntityNodeOperations(InMemoryStore store)
    : InMemoryNodeOperations<EntityNode>(store), IEntityNodeOperations
{
    protected override Dictionary<string, EntityNode> Items => Store.Entities;

    protected override EntityNode ToStored(EntityNode item) => RecordCopies.Copy(item, includeEmbedding: true);

    protected override EntityNode ToResult(EntityNode stored) => RecordCopies.Copy(stored, includeEmbedding: false);

    public Task<IReadOnlyList<EntityNode>> GetByNamesAsync(
        IReadOnlyList<string> names,
        IReadOnlyList<string> groupIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(groupIds);
        cancellationToken.ThrowIfCancellationRequested();
        var nameSet = names.Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.Ordinal);
        var groupSet = groupIds.Where(g => !string.IsNullOrEmpty(g)).ToHashSet(StringComparer.Ordinal);
        lock (Store.Gate)
        {
            IReadOnlyList<EntityNode> result = [.. Store.Entities.Values
                .Where(n => groupSet.Contains(n.GroupId) && nameSet.Contains(n.Name))
                .Select(ToResult)];
            return Task.FromResult(result);
        }
    }

    public Task LoadEmbeddingsAsync(EntityNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            if (!Store.Entities.TryGetValue(node.Uuid, out var stored))
            {
                return Task.FromException(new NodeNotFoundException(node.Uuid));
            }

            node.NameEmbedding = RecordCopies.CopyVector(stored.NameEmbedding);
        }

        return Task.CompletedTask;
    }

    public Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityNode> nodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            foreach (var node in nodes)
            {
                if (Store.Entities.TryGetValue(node.Uuid, out var stored))
                {
                    node.NameEmbedding = RecordCopies.CopyVector(stored.NameEmbedding);
                }
            }
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryCommunityNodeOperations(InMemoryStore store)
    : InMemoryNodeOperations<CommunityNode>(store), ICommunityNodeOperations
{
    protected override Dictionary<string, CommunityNode> Items => Store.Communities;

    protected override CommunityNode ToStored(CommunityNode item) => RecordCopies.Copy(item, includeEmbedding: true);

    protected override CommunityNode ToResult(CommunityNode stored) =>
        RecordCopies.Copy(stored, includeEmbedding: false);

    public Task LoadNameEmbeddingAsync(CommunityNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            if (!Store.Communities.TryGetValue(node.Uuid, out var stored))
            {
                return Task.FromException(new NodeNotFoundException(node.Uuid));
            }

            node.NameEmbedding = RecordCopies.CopyVector(stored.NameEmbedding);
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemorySagaNodeOperations(InMemoryStore store)
    : InMemoryNodeOperations<SagaNode>(store), ISagaNodeOperations
{
    protected override Dictionary<string, SagaNode> Items => Store.Sagas;

    protected override SagaNode ToStored(SagaNode item) => RecordCopies.Copy(item);

    protected override SagaNode ToResult(SagaNode stored) => RecordCopies.Copy(stored);

    public Task<SagaNode?> GetByNameAsync(string name, string groupId, CancellationToken cancellationToken = default) =>
        Read(
            () => Store.Sagas.Values
                .Where(s => s.Name == name && s.GroupId == groupId)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Uuid, StringComparer.Ordinal)
                .Select(RecordCopies.Copy)
                .FirstOrDefault(),
            cancellationToken);

    public Task<string?> GetPreviousEpisodeUuidAsync(
        string sagaUuid,
        string currentEpisodeUuid,
        CancellationToken cancellationToken = default) =>
        Read(
            () => InMemoryEpisodeNodeOperations.SagaEpisodes(Store, sagaUuid)
                .Where(e => e.Uuid != currentEpisodeUuid)
                .OrderByDescending(e => e.ValidAt)
                .ThenByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.Uuid, StringComparer.Ordinal)
                .Select(e => e.Uuid)
                .FirstOrDefault(),
            cancellationToken);
}

internal sealed class InMemoryEpisodicEdgeOperations(InMemoryStore store)
    : InMemoryEdgeOperations<EpisodicEdge>(store), IEpisodicEdgeOperations
{
    protected override Dictionary<string, EpisodicEdge> Items => Store.EpisodicEdges;

    protected override EpisodicEdge ToStored(EpisodicEdge item) => RecordCopies.Copy(item);

    protected override EpisodicEdge ToResult(EpisodicEdge stored) => RecordCopies.Copy(stored);
}

internal sealed class InMemoryEntityEdgeOperations(InMemoryStore store)
    : InMemoryEdgeOperations<EntityEdge>(store), IEntityEdgeOperations
{
    protected override Dictionary<string, EntityEdge> Items => Store.EntityEdges;

    protected override EntityEdge ToStored(EntityEdge item) => RecordCopies.Copy(item, includeEmbedding: true);

    protected override EntityEdge ToResult(EntityEdge stored) => RecordCopies.Copy(stored, includeEmbedding: false);

    public Task<IReadOnlyList<EntityEdge>> GetBetweenNodesAsync(
        string sourceNodeUuid,
        string targetNodeUuid,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<EntityEdge>>(
            () => Store.EntityEdges.Values
                .Where(e => e.SourceNodeUuid == sourceNodeUuid && e.TargetNodeUuid == targetNodeUuid)
                .OrderBy(e => e.Uuid, StringComparer.Ordinal)
                .Select(ToResult)
                .ToList(),
            cancellationToken);

    public Task<IReadOnlyList<EntityEdge>> GetByNodeUuidAsync(
        string nodeUuid,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<EntityEdge>>(
            () => Store.EntityEdges.Values
                .Where(e => e.SourceNodeUuid == nodeUuid || e.TargetNodeUuid == nodeUuid)
                .OrderBy(e => e.Uuid, StringComparer.Ordinal)
                .Select(ToResult)
                .ToList(),
            cancellationToken);

    public Task LoadEmbeddingsAsync(EntityEdge edge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            if (!Store.EntityEdges.TryGetValue(edge.Uuid, out var stored))
            {
                return Task.FromException(new EdgeNotFoundException(edge.Uuid));
            }

            edge.FactEmbedding = RecordCopies.CopyVector(stored.FactEmbedding);
        }

        return Task.CompletedTask;
    }

    public Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Store.Gate)
        {
            foreach (var edge in edges)
            {
                if (Store.EntityEdges.TryGetValue(edge.Uuid, out var stored))
                {
                    edge.FactEmbedding = RecordCopies.CopyVector(stored.FactEmbedding);
                }
            }
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryCommunityEdgeOperations(InMemoryStore store)
    : InMemoryEdgeOperations<CommunityEdge>(store), ICommunityEdgeOperations
{
    protected override Dictionary<string, CommunityEdge> Items => Store.CommunityEdges;

    protected override CommunityEdge ToStored(CommunityEdge item) => RecordCopies.Copy(item);

    protected override CommunityEdge ToResult(CommunityEdge stored) => RecordCopies.Copy(stored);
}

internal sealed class InMemoryHasEpisodeEdgeOperations(InMemoryStore store)
    : InMemoryEdgeOperations<HasEpisodeEdge>(store), IHasEpisodeEdgeOperations
{
    protected override Dictionary<string, HasEpisodeEdge> Items => Store.HasEpisodeEdges;

    protected override HasEpisodeEdge ToStored(HasEpisodeEdge item) => RecordCopies.Copy(item);

    protected override HasEpisodeEdge ToResult(HasEpisodeEdge stored) => RecordCopies.Copy(stored);
}

internal sealed class InMemoryNextEpisodeEdgeOperations(InMemoryStore store)
    : InMemoryEdgeOperations<NextEpisodeEdge>(store), INextEpisodeEdgeOperations
{
    protected override Dictionary<string, NextEpisodeEdge> Items => Store.NextEpisodeEdges;

    protected override NextEpisodeEdge ToStored(NextEpisodeEdge item) => RecordCopies.Copy(item);

    protected override NextEpisodeEdge ToResult(NextEpisodeEdge stored) => RecordCopies.Copy(stored);
}
