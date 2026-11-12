// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.InMemory;

/// <summary>Clean-up and community helpers over the in-memory graph.</summary>
internal sealed class InMemoryGraphMaintenanceOperations(InMemoryStore store) : IGraphMaintenanceOperations
{
    public Task ClearDataAsync(IReadOnlyList<string>? groupIds = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.Gate)
        {
            store.Clear(groupIds);
        }

        return Task.CompletedTask;
    }

    /// <summary>Nothing to build: the in-memory graph has no schema.</summary>
    public Task BuildIndicesAndConstraintsAsync(bool deleteExisting = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>> GetEntityProjectionAsync(
        string groupId,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>>(
            () =>
            {
                var counts = store.Entities.Values
                    .Where(n => n.GroupId == groupId)
                    .ToDictionary(
                        n => n.Uuid,
                        _ => new Dictionary<string, int>(StringComparer.Ordinal),
                        StringComparer.Ordinal);
                foreach (var edge in store.EntityEdges.Values)
                {
                    if (!counts.TryGetValue(edge.SourceNodeUuid, out var fromSource)
                        || !counts.TryGetValue(edge.TargetNodeUuid, out var fromTarget))
                    {
                        continue;
                    }

                    fromSource[edge.TargetNodeUuid] = fromSource.GetValueOrDefault(edge.TargetNodeUuid) + 1;
                    if (edge.SourceNodeUuid != edge.TargetNodeUuid)
                    {
                        fromTarget[edge.SourceNodeUuid] = fromTarget.GetValueOrDefault(edge.SourceNodeUuid) + 1;
                    }
                }

                return counts.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<Neighbor>)pair.Value
                        .OrderBy(n => n.Key, StringComparer.Ordinal)
                        .Select(n => new Neighbor(n.Key, n.Value))
                        .ToList(),
                    StringComparer.Ordinal);
            },
            cancellationToken);

    public Task<IReadOnlyList<string>> GetEntityGroupIdsAsync(CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<string>>(
            () => store.Entities.Values
                .Select(n => n.GroupId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            cancellationToken);

    public Task RemoveCommunitiesAsync(IReadOnlyList<string>? groupIds = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.Gate)
        {
            var uuids = store.Communities.Values
                .Where(c => groupIds is not { Count: > 0 } || groupIds.Contains(c.GroupId))
                .Select(c => c.Uuid)
                .ToList();
            foreach (var uuid in uuids)
            {
                store.RemoveNode(store.Communities, uuid);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EntityNode>> GetMentionedNodesAsync(
        IReadOnlyList<EpisodicNode> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var episodeUuids = episodes.Select(e => e.Uuid).ToHashSet(StringComparer.Ordinal);
        return Read<IReadOnlyList<EntityNode>>(
            () => store.EpisodicEdges.Values
                .Where(e => episodeUuids.Contains(e.SourceNodeUuid) && store.Episodes.ContainsKey(e.SourceNodeUuid))
                .Select(e => e.TargetNodeUuid)
                .Distinct(StringComparer.Ordinal)
                .Select(uuid => store.Entities.GetValueOrDefault(uuid))
                .OfType<EntityNode>()
                .OrderBy(n => n.Uuid, StringComparer.Ordinal)
                .Select(n => RecordCopies.Copy(n, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlyList<CommunityNode>> GetCommunitiesByNodesAsync(
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var nodeUuids = nodes.Select(n => n.Uuid).ToHashSet(StringComparer.Ordinal);
        return Read<IReadOnlyList<CommunityNode>>(
            () => store.CommunityEdges.Values
                .Where(e => nodeUuids.Contains(e.TargetNodeUuid) && store.Entities.ContainsKey(e.TargetNodeUuid))
                .Select(e => e.SourceNodeUuid)
                .Distinct(StringComparer.Ordinal)
                .Select(uuid => store.Communities.GetValueOrDefault(uuid))
                .OfType<CommunityNode>()
                .OrderBy(c => c.Uuid, StringComparer.Ordinal)
                .Select(c => RecordCopies.Copy(c, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlyList<CommunityNode>> GetNeighborCommunitiesAsync(
        string entityUuid,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<CommunityNode>>(
            () =>
            {
                var neighbours = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var edge in store.EntityEdges.Values)
                {
                    if (edge.SourceNodeUuid == entityUuid)
                    {
                        neighbours.Add(edge.TargetNodeUuid);
                    }
                    else if (edge.TargetNodeUuid == entityUuid)
                    {
                        neighbours.Add(edge.SourceNodeUuid);
                    }
                }

                neighbours.RemoveWhere(uuid => !store.Entities.ContainsKey(uuid));
                var result = new List<CommunityNode>();
                foreach (var neighbour in neighbours)
                {
                    var communities = store.CommunityEdges.Values
                        .Where(e => e.TargetNodeUuid == neighbour)
                        .Select(e => e.SourceNodeUuid)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Select(uuid => store.Communities.GetValueOrDefault(uuid))
                        .OfType<CommunityNode>();
                    result.AddRange(communities.Select(c => RecordCopies.Copy(c, includeEmbedding: false)));
                }

                return result;
            },
            cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> CountEpisodeMentionsAsync(
        IReadOnlyList<string> entityUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityUuids);
        return Read<IReadOnlyDictionary<string, int>>(
            () =>
            {
                var counts = entityUuids.Distinct(StringComparer.Ordinal).ToDictionary(
                    uuid => uuid,
                    uuid => store.EpisodicEdges.Values
                        .Where(e => e.TargetNodeUuid == uuid && store.Episodes.ContainsKey(e.SourceNodeUuid))
                        .Select(e => e.SourceNodeUuid)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    StringComparer.Ordinal);
                return counts;
            },
            cancellationToken);
    }

    private Task<T> Read<T>(Func<T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.Gate)
        {
            return Task.FromResult(read());
        }
    }
}
