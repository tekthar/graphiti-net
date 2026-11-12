// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/neo4j/operations/episode_node_ops.py, entity_node_ops.py, community_node_ops.py,
// saga_node_ops.py and entity_edge_ops.py (Graphiti v0.31.2) and changed for .NET: Cypher becomes LINQ and SQL.

using GraphitiNet.Driver.PostgreSql.Entities;
using GraphitiNet.Driver.PostgreSql.Internal;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Operations;

/// <summary>Episode storage.</summary>
internal sealed class EpisodeNodeOperations(DriverCore core)
    : NodeOperations<EpisodicNode, EpisodeRecord>(
        core, SqlNames.Episodes, RecordMapper.EpisodeProjection, RecordMapper.ToModel), IEpisodeNodeOperations
{
    public override Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<EpisodicNode> items, CancellationToken ct) =>
        Core.UpsertEpisodesAsync(connection, items, ct);

    public Task<IReadOnlyList<EpisodicNode>> GetByEntityNodeUuidAsync(
        string entityNodeUuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityNodeUuid);
        return ListAsync(
            (context, q) => q.Where(e => context.Set<EpisodicEdgeRecord>()
                .Any(m => m.SourceNodeUuid == e.Uuid && m.TargetNodeUuid == entityNodeUuid)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        DateTimeOffset referenceTime,
        int lastN = 3,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default)
    {
        if (lastN <= 0)
        {
            return [];
        }

        var reference = referenceTime.ToUniversalTime();
        // As upstream, an empty group list does not filter; with a saga, the saga is looked up in the first group
        // only, the group ids do not further filter its episodes, and no group ids match nothing.
        var groups = groupIds is { Count: > 0 } ? groupIds.ToArray() : null;
        if (saga is not null && groups is null)
        {
            return [];
        }

        var sagaGroup = groups?[0];
        var sourceName = source?.ToStorageName();
        var newestFirst = await ListAsync(
            (context, q) =>
            {
                q = q.Where(e => e.ValidAt <= reference);
                if (groups is not null && saga is null)
                {
                    q = q.Where(e => groups.Contains(e.GroupId));
                }

                if (sourceName is not null)
                {
                    q = q.Where(e => e.Source == sourceName);
                }

                if (saga is not null)
                {
                    var sagas = context.Set<SagaRecord>().Where(s => s.Name == saga && s.GroupId == sagaGroup);

                    q = q.Where(e => context.Set<HasEpisodeEdgeRecord>()
                        .Any(h => h.TargetNodeUuid == e.Uuid && sagas.Any(s => s.Uuid == h.SourceNodeUuid)));
                }

                return q.OrderByDescending(e => e.ValidAt).ThenByDescending(e => e.CreatedAt).Take(lastN);
            },
            cancellationToken).ConfigureAwait(false);
        return newestFirst.Reverse().ToList();
    }

    public Task<IReadOnlyList<EpisodicNode>> GetBySagaAsync(
        string sagaUuid,
        DateTimeOffset? createdAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sagaUuid);
        var after = createdAfter?.ToUniversalTime();
        return ListAsync(
            (context, q) =>
            {
                q = q.Where(e => context.Set<HasEpisodeEdgeRecord>()
                    .Any(h => h.SourceNodeUuid == sagaUuid && h.TargetNodeUuid == e.Uuid));
                if (after is not null)
                {
                    q = q.Where(e => e.CreatedAt > after.Value);
                }

                return q.OrderBy(e => e.CreatedAt).ThenBy(e => e.ValidAt);
            },
            cancellationToken);
    }
}

/// <summary>Entity storage.</summary>
internal sealed class EntityNodeOperations(DriverCore core)
    : NodeOperations<EntityNode, EntityNodeRecord>(
        core, SqlNames.Entities, RecordMapper.EntityProjection, RecordMapper.ToModel), IEntityNodeOperations
{
    public override Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<EntityNode> items, CancellationToken ct) =>
        Core.UpsertEntitiesAsync(connection, items, ct);

    public async Task LoadEmbeddingsAsync(EntityNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        var embeddings = await EmbeddingLoader.LoadAsync(
            Core, Table, "name_embedding", [node.Uuid], cancellationToken).ConfigureAwait(false);
        if (!embeddings.TryGetValue(node.Uuid, out var embedding))
        {
            throw new NodeNotFoundException(node.Uuid);
        }

        node.NameEmbedding = embedding;
    }

    public async Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityNode> nodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var embeddings = await EmbeddingLoader.LoadAsync(
            Core, Table, "name_embedding", nodes.Select(n => n.Uuid).ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (var node in nodes)
        {
            if (embeddings.TryGetValue(node.Uuid, out var embedding))
            {
                node.NameEmbedding = embedding;
            }
        }
    }
}

/// <summary>Community storage.</summary>
internal sealed class CommunityNodeOperations(DriverCore core)
    : NodeOperations<CommunityNode, CommunityRecord>(
        core, SqlNames.Communities, RecordMapper.CommunityProjection, RecordMapper.ToModel), ICommunityNodeOperations
{
    public override Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<CommunityNode> items, CancellationToken ct) =>
        Core.UpsertCommunitiesAsync(connection, items, ct);

    public async Task LoadNameEmbeddingAsync(CommunityNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        var embeddings = await EmbeddingLoader.LoadAsync(
            Core, Table, "name_embedding", [node.Uuid], cancellationToken).ConfigureAwait(false);
        if (!embeddings.TryGetValue(node.Uuid, out var embedding))
        {
            throw new NodeNotFoundException(node.Uuid);
        }

        node.NameEmbedding = embedding;
    }
}

/// <summary>Saga storage.</summary>
internal sealed class SagaNodeOperations(DriverCore core)
    : NodeOperations<SagaNode, SagaRecord>(core, SqlNames.Sagas, r => r, RecordMapper.ToModel), ISagaNodeOperations
{
    public override Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<SagaNode> items, CancellationToken ct) =>
        Core.UpsertSagasAsync(connection, items, ct);

    public async Task<SagaNode?> GetByNameAsync(
        string name,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(groupId);
        var found = await ListAsync(
            q => q.Where(s => s.Name == name && s.GroupId == groupId).OrderBy(s => s.CreatedAt).Take(1),
            cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    public Task<string?> GetPreviousEpisodeUuidAsync(
        string sagaUuid,
        string currentEpisodeUuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sagaUuid);
        ArgumentNullException.ThrowIfNull(currentEpisodeUuid);
        return Core.QueryAsync(context => context.Set<EpisodeRecord>()
            .AsNoTracking()
            .Where(e => e.Uuid != currentEpisodeUuid
                && context.Set<HasEpisodeEdgeRecord>()
                    .Any(h => h.SourceNodeUuid == sagaUuid && h.TargetNodeUuid == e.Uuid))
            .OrderByDescending(e => e.ValidAt)
            .ThenByDescending(e => e.CreatedAt)
            .Select(e => e.Uuid)
            .FirstOrDefaultAsync(cancellationToken));
    }
}

/// <summary>Fact storage.</summary>
internal sealed class EntityEdgeOperations(DriverCore core)
    : EdgeOperations<EntityEdge, EntityEdgeRecord>(
        core, SqlNames.EntityEdges, RecordMapper.EntityEdgeProjection, RecordMapper.ToModel), IEntityEdgeOperations
{
    public override Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<EntityEdge> items, CancellationToken ct) =>
        Core.UpsertEntityEdgesAsync(connection, items, ct);

    public Task<IReadOnlyList<EntityEdge>> GetBetweenNodesAsync(
        string sourceNodeUuid,
        string targetNodeUuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceNodeUuid);
        ArgumentNullException.ThrowIfNull(targetNodeUuid);
        return ListAsync(
            q => q.Where(e => e.SourceNodeUuid == sourceNodeUuid && e.TargetNodeUuid == targetNodeUuid),
            cancellationToken);
    }

    public Task<IReadOnlyList<EntityEdge>> GetByNodeUuidAsync(
        string nodeUuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeUuid);
        return ListAsync(
            q => q.Where(e => e.SourceNodeUuid == nodeUuid || e.TargetNodeUuid == nodeUuid),
            cancellationToken);
    }

    public async Task LoadEmbeddingsAsync(EntityEdge edge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var embeddings = await EmbeddingLoader.LoadAsync(
            Core, Table, "fact_embedding", [edge.Uuid], cancellationToken).ConfigureAwait(false);
        if (!embeddings.TryGetValue(edge.Uuid, out var embedding))
        {
            throw new EdgeNotFoundException(edge.Uuid);
        }

        edge.FactEmbedding = embedding;
    }

    public async Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var embeddings = await EmbeddingLoader.LoadAsync(
            Core, Table, "fact_embedding", edges.Select(e => e.Uuid).ToArray(), cancellationToken).ConfigureAwait(false);
        foreach (var edge in edges)
        {
            if (embeddings.TryGetValue(edge.Uuid, out var embedding))
            {
                edge.FactEmbedding = embedding;
            }
        }
    }
}

/// <summary>Reads embeddings as <c>real[]</c>, so no pgvector type mapping is needed on the connection.</summary>
internal static class EmbeddingLoader
{
    public static Task<Dictionary<string, float[]?>> LoadAsync(
        DriverCore core,
        string table,
        string column,
        IReadOnlyList<string> uuids,
        CancellationToken ct)
    {
        if (uuids.Count == 0)
        {
            return Task.FromResult(new Dictionary<string, float[]?>());
        }

        return core.WithConnectionAsync(
            async connection =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT uuid, {column}::real[] FROM {core.Names.Table(table)} WHERE uuid = ANY($1)",
                    connection);
                command.Parameters.Add(Params.TextArray(uuids));
                var rows = await DriverCore.ReadListAsync(
                    command,
                    r => (Uuid: r.GetString(0), Embedding: r.IsDBNull(1) ? null : r.GetFieldValue<float[]>(1)),
                    ct).ConfigureAwait(false);
                return rows.ToDictionary(r => r.Uuid, r => r.Embedding);
            },
            ct);
    }
}
