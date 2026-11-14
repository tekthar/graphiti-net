// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.InMemory;

/// <summary>
/// The in-memory graph. Every access holds <see cref="Gate"/>. Stored records are private copies, normalised to UTC
/// and JSON-shaped values; callers only ever see further copies.
/// </summary>
internal sealed class InMemoryStore
{
    public Lock Gate { get; } = new();

    public Dictionary<string, EpisodicNode> Episodes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EntityNode> Entities { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, CommunityNode> Communities { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SagaNode> Sagas { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EpisodicEdge> EpisodicEdges { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EntityEdge> EntityEdges { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, CommunityEdge> CommunityEdges { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, HasEpisodeEdge> HasEpisodeEdges { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, NextEpisodeEdge> NextEpisodeEdges { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The unique index on (group, idempotency key) of the database drivers: throws if saving
    /// <paramref name="episodes"/> would give two episodes of one group the same key. Called with the lock held, before
    /// anything is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another episode in the group has the key.</exception>
    public void CheckIdempotencyKeys(IReadOnlyList<EpisodicNode> episodes)
    {
        var claimed = new Dictionary<(string Group, string Key), string>();
        foreach (var episode in episodes)
        {
            if (episode.IdempotencyKey is not { } key)
            {
                continue;
            }

            if (claimed.TryGetValue((episode.GroupId, key), out var other) && other != episode.Uuid)
            {
                throw DuplicateKey(episode.GroupId, key);
            }

            claimed[(episode.GroupId, key)] = episode.Uuid;
        }

        if (claimed.Count == 0)
        {
            return;
        }

        foreach (var stored in Episodes.Values)
        {
            if (stored.IdempotencyKey is { } key
                && claimed.TryGetValue((stored.GroupId, key), out var uuid)
                && uuid != stored.Uuid)
            {
                throw DuplicateKey(stored.GroupId, key);
            }
        }

        static InvalidOperationException DuplicateKey(string group, string key) =>
            new($"Another episode in group '{group}' already has the idempotency key '{key}'.");
    }

    /// <summary>The episode in the group with this idempotency key, or <see langword="null"/>.</summary>
    public EpisodicNode? EpisodeByIdempotencyKey(string groupId, string key) =>
        Episodes.Values.FirstOrDefault(e =>
            e.GroupId == groupId && string.Equals(e.IdempotencyKey, key, StringComparison.Ordinal));

    /// <summary>Removes every edge, of any type, that starts or ends at <paramref name="nodeUuid"/>.</summary>
    public void RemoveEdgesTouching(string nodeUuid)
    {
        RemoveWhere(EpisodicEdges, e => Touches(e, nodeUuid));
        RemoveWhere(EntityEdges, e => Touches(e, nodeUuid));
        RemoveWhere(CommunityEdges, e => Touches(e, nodeUuid));
        RemoveWhere(HasEpisodeEdges, e => Touches(e, nodeUuid));
        RemoveWhere(NextEpisodeEdges, e => Touches(e, nodeUuid));
    }

    /// <summary>Removes a node of any type and the edges that touch it.</summary>
    public void RemoveNode<T>(Dictionary<string, T> nodes, string uuid)
        where T : Node
    {
        if (nodes.Remove(uuid))
        {
            RemoveEdgesTouching(uuid);
        }
    }

    /// <summary>Removes every record, or only the records in <paramref name="groupIds"/>.</summary>
    public void Clear(IReadOnlyCollection<string>? groupIds)
    {
        if (groupIds is null)
        {
            Episodes.Clear();
            Entities.Clear();
            Communities.Clear();
            Sagas.Clear();
            EpisodicEdges.Clear();
            EntityEdges.Clear();
            CommunityEdges.Clear();
            HasEpisodeEdges.Clear();
            NextEpisodeEdges.Clear();
            return;
        }

        var groups = groupIds.ToHashSet(StringComparer.Ordinal);
        RemoveNodesInGroups(Episodes, groups);
        RemoveNodesInGroups(Entities, groups);
        RemoveNodesInGroups(Communities, groups);
        RemoveNodesInGroups(Sagas, groups);
        RemoveWhere(EpisodicEdges, e => groups.Contains(e.GroupId));
        RemoveWhere(EntityEdges, e => groups.Contains(e.GroupId));
        RemoveWhere(CommunityEdges, e => groups.Contains(e.GroupId));
        RemoveWhere(HasEpisodeEdges, e => groups.Contains(e.GroupId));
        RemoveWhere(NextEpisodeEdges, e => groups.Contains(e.GroupId));
    }

    /// <summary>Removes the nodes in a dictionary that belong to these groups, with the edges that touch them.</summary>
    public void RemoveNodesInGroups<T>(Dictionary<string, T> nodes, IReadOnlySet<string> groups)
        where T : Node
    {
        foreach (var uuid in nodes.Values.Where(n => groups.Contains(n.GroupId)).Select(n => n.Uuid).ToList())
        {
            RemoveNode(nodes, uuid);
        }
    }

    /// <summary>The group of the node with this id, of any type, or <see langword="null"/>.</summary>
    public string? NodeGroup(string uuid) =>
        Entities.TryGetValue(uuid, out var entity) ? entity.GroupId
        : Episodes.TryGetValue(uuid, out var episode) ? episode.GroupId
        : Communities.TryGetValue(uuid, out var community) ? community.GroupId
        : Sagas.TryGetValue(uuid, out var saga) ? saga.GroupId
        : null;

    private static bool Touches(Edge edge, string nodeUuid) =>
        string.Equals(edge.SourceNodeUuid, nodeUuid, StringComparison.Ordinal)
        || string.Equals(edge.TargetNodeUuid, nodeUuid, StringComparison.Ordinal);

    private static void RemoveWhere<T>(Dictionary<string, T> items, Func<T, bool> predicate)
    {
        foreach (var key in items.Where(pair => predicate(pair.Value)).Select(pair => pair.Key).ToList())
        {
            items.Remove(key);
        }
    }
}

/// <summary>Copies records. Storing copies normalises them; reading copies leaves embeddings out unless asked.</summary>
internal static class RecordCopies
{
    public static EpisodicNode Copy(EpisodicNode node) => new()
    {
        Uuid = node.Uuid,
        Name = node.Name,
        GroupId = node.GroupId,
        Labels = Distinct(node.Labels),
        CreatedAt = node.CreatedAt.ToUniversalTime(),
        Source = node.Source,
        SourceDescription = node.SourceDescription,
        Content = node.Content,
        ValidAt = node.ValidAt.ToUniversalTime(),
        EntityEdges = [.. node.EntityEdges],
        EpisodeMetadata = node.EpisodeMetadata is null ? null : CopyValues(node.EpisodeMetadata),
        IdempotencyKey = node.IdempotencyKey,
    };

    public static EntityNode Copy(EntityNode node, bool includeEmbedding) => new()
    {
        Uuid = node.Uuid,
        Name = node.Name,
        GroupId = node.GroupId,

        // Upstream saves every entity with the Entity label, and returns it.
        Labels = Distinct([.. node.Labels, FilterEvaluator.EntityLabel]),
        CreatedAt = node.CreatedAt.ToUniversalTime(),
        NameEmbedding = includeEmbedding ? CopyVector(node.NameEmbedding) : null,
        Summary = node.Summary,
        Attributes = CopyValues(node.Attributes),
    };

    public static CommunityNode Copy(CommunityNode node, bool includeEmbedding) => new()
    {
        Uuid = node.Uuid,
        Name = node.Name,
        GroupId = node.GroupId,
        Labels = Distinct(node.Labels),
        CreatedAt = node.CreatedAt.ToUniversalTime(),
        NameEmbedding = includeEmbedding ? CopyVector(node.NameEmbedding) : null,
        Summary = node.Summary,
    };

    public static SagaNode Copy(SagaNode node) => new()
    {
        Uuid = node.Uuid,
        Name = node.Name,
        GroupId = node.GroupId,
        Labels = Distinct(node.Labels),
        CreatedAt = node.CreatedAt.ToUniversalTime(),
        Summary = node.Summary,
        FirstEpisodeUuid = node.FirstEpisodeUuid,
        LastEpisodeUuid = node.LastEpisodeUuid,
        LastSummarizedAt = node.LastSummarizedAt?.ToUniversalTime(),
        LastSummarizedEpisodeValidAt = node.LastSummarizedEpisodeValidAt?.ToUniversalTime(),
    };

    public static EntityEdge Copy(EntityEdge edge, bool includeEmbedding) => new()
    {
        Uuid = edge.Uuid,
        GroupId = edge.GroupId,
        SourceNodeUuid = edge.SourceNodeUuid,
        TargetNodeUuid = edge.TargetNodeUuid,
        CreatedAt = edge.CreatedAt.ToUniversalTime(),
        Name = edge.Name,
        Fact = edge.Fact,
        FactEmbedding = includeEmbedding ? CopyVector(edge.FactEmbedding) : null,
        Episodes = [.. edge.Episodes],
        ExpiredAt = edge.ExpiredAt?.ToUniversalTime(),
        ValidAt = edge.ValidAt?.ToUniversalTime(),
        InvalidAt = edge.InvalidAt?.ToUniversalTime(),
        ReferenceTime = edge.ReferenceTime?.ToUniversalTime(),
        Attributes = CopyValues(edge.Attributes),
    };

    public static EpisodicEdge Copy(EpisodicEdge edge) => new()
    {
        Uuid = edge.Uuid,
        GroupId = edge.GroupId,
        SourceNodeUuid = edge.SourceNodeUuid,
        TargetNodeUuid = edge.TargetNodeUuid,
        CreatedAt = edge.CreatedAt.ToUniversalTime(),
    };

    public static CommunityEdge Copy(CommunityEdge edge) => new()
    {
        Uuid = edge.Uuid,
        GroupId = edge.GroupId,
        SourceNodeUuid = edge.SourceNodeUuid,
        TargetNodeUuid = edge.TargetNodeUuid,
        CreatedAt = edge.CreatedAt.ToUniversalTime(),
    };

    public static HasEpisodeEdge Copy(HasEpisodeEdge edge) => new()
    {
        Uuid = edge.Uuid,
        GroupId = edge.GroupId,
        SourceNodeUuid = edge.SourceNodeUuid,
        TargetNodeUuid = edge.TargetNodeUuid,
        CreatedAt = edge.CreatedAt.ToUniversalTime(),
    };

    public static NextEpisodeEdge Copy(NextEpisodeEdge edge) => new()
    {
        Uuid = edge.Uuid,
        GroupId = edge.GroupId,
        SourceNodeUuid = edge.SourceNodeUuid,
        TargetNodeUuid = edge.TargetNodeUuid,
        CreatedAt = edge.CreatedAt.ToUniversalTime(),
    };

    public static float[]? CopyVector(float[]? vector) => vector is null ? null : [.. vector];

    private static List<string> Distinct(IReadOnlyList<string> labels) => labels.Distinct(StringComparer.Ordinal).ToList();

    // Normalize already returns a fresh deep copy, and it is idempotent on stored (normalised) values.
    private static Dictionary<string, object?> CopyValues(IDictionary<string, object?>? values) =>
        JsonValues.Normalize(values) ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
