// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/neo4j/operations/episode_node_ops.py, entity_node_ops.py, community_node_ops.py,
// saga_node_ops.py and entity_edge_ops.py (Graphiti v0.31.2) and changed for .NET: Cypher becomes LINQ and T-SQL.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Driver.SqlServer.Entities;
using GraphitiNet.Driver.SqlServer.Internal;
using GraphitiNet.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlTypes;

namespace GraphitiNet.Driver.SqlServer.Operations;

/// <summary>Episode storage.</summary>
internal sealed class EpisodeNodeOperations(DriverCore core)
    : NodeOperations<EpisodicNode, EpisodeRecord>(
        core, SqlNames.Episodes, RecordMapper.EpisodeProjection, RecordMapper.ToModel), IEpisodeNodeOperations
{
    public override Task UpsertAsync(Db db, IReadOnlyList<EpisodicNode> items, CancellationToken ct) =>
        Core.UpsertEpisodesAsync(db, items, ct);

    public async Task<EpisodicNode?> GetByIdempotencyKeyAsync(
        string groupId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        if (groupId.Length > GraphitiModelBuilderExtensions.IdLength
            || idempotencyKey.Length > GraphitiModelBuilderExtensions.IdLength)
        {
            // Longer values cannot be stored, so nothing matches.
            return null;
        }

        var found = await ListAsync(
            q => q.Where(e => e.GroupId == groupId && e.IdempotencyKey == idempotencyKey).Take(1),
            cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Inserts the episode; when the unique index on (group, key) refuses it, a concurrent or earlier
    /// writer holds the key, and its episode is returned.</summary>
    public async Task<EpisodicNode> AddOrGetByIdempotencyKeyAsync(
        EpisodicNode episode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var key = episode.IdempotencyKey
                  ?? throw new ArgumentException("The episode has no idempotency key.", nameof(episode));
        // Two writers racing on one key: the unique index refuses the second, or SQL Server picks one of them as a
        // deadlock victim (its whole transaction is rolled back). Either way the other writer's episode is the answer;
        // a victim whose rival also failed tries again.
        for (var attempt = 1; ; attempt++)
        {
            if (await GetByIdempotencyKeyAsync(episode.GroupId, key, cancellationToken).ConfigureAwait(false) is { } found)
            {
                return found;
            }

            try
            {
                await SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException e) when (e.Number is DuplicateKeyRow or UniqueConstraintViolation)
            {
                return await GetByIdempotencyKeyAsync(episode.GroupId, key, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException(
                           $"The idempotency key '{key}' was taken in group '{episode.GroupId}', then released.", e);
            }
            catch (SqlException e) when (e.Number == DeadlockVictim && attempt < MaxDeadlockAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 100) * attempt), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            return await GetByIdempotencyKeyAsync(episode.GroupId, key, cancellationToken).ConfigureAwait(false)
                   ?? episode;
        }
    }

    // "Transaction ... was deadlocked on lock resources with another process and has been chosen as the deadlock victim"
    private const int DeadlockVictim = 1205;

    private const int MaxDeadlockAttempts = 5;

    // "Cannot insert duplicate key row in object ... with unique index ..."
    private const int DuplicateKeyRow = 2601;

    // "Violation of UNIQUE KEY constraint ..."
    private const int UniqueConstraintViolation = 2627;

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

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetEpisodeUuidsByEntityNodeUuidsAsync(
        IReadOnlyList<string> entityNodeUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityNodeUuids);
        var uuids = entityNodeUuids.Distinct(StringComparer.Ordinal).ToArray();
        var result = uuids.ToDictionary(u => u, _ => (IReadOnlyList<string>)[], StringComparer.Ordinal);
        if (uuids.Length == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(result);
        }

        var n = Core.Names;
        var b = new SqlBuilder();
        b.Append("SELECT m.target_node_uuid, ep.uuid "
            + $"FROM {n.Table(SqlNames.EpisodicEdges)} m "
            + $"JOIN {n.Table(SqlNames.Entities)} en ON en.uuid = m.target_node_uuid "
            + $"JOIN {n.Table(SqlNames.Episodes)} ep ON ep.uuid = m.source_node_uuid AND ep.group_id = en.group_id "
            + $"WHERE m.target_node_uuid IN {b.Ids(uuids)} "
            + "GROUP BY m.target_node_uuid, ep.uuid, ep.valid_at ORDER BY m.target_node_uuid, ep.valid_at, ep.uuid");
        return GroupAsync();

        async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GroupAsync()
        {
            var rows = await Core.ListAsync(
                b, r => (Entity: r.GetString(0), Episode: r.GetString(1)), cancellationToken).ConfigureAwait(false);
            foreach (var group in rows.GroupBy(r => r.Entity, StringComparer.Ordinal))
            {
                result[group.Key] = [.. group.Select(r => r.Episode)];
            }

            return result;
        }
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
    public override Task UpsertAsync(Db db, IReadOnlyList<EntityNode> items, CancellationToken ct) =>
        Core.UpsertEntitiesAsync(db, items, ct);

    public Task<IReadOnlyList<EntityNode>> GetByNamesAsync(
        IReadOnlyList<string> names,
        IReadOnlyList<string> groupIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(groupIds);
        var nameArray = names.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToArray();
        var groupArray = groupIds.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (nameArray.Length == 0 || groupArray.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<EntityNode>>([]);
        }

        // Exact, case-sensitive match (binary collation), as upstream's get_by_names; the (group_id, name_key) index
        // finds the candidates by the name's hash.
        var b = new SqlBuilder();
        var hashes = $"(SELECT CAST(HASHBYTES('SHA2_256', x.v) AS binary(32)) FROM {b.List(nameArray)} AS x)";
        b.Append($"SELECT {RecordMapper.EntityColumns("n")} FROM {Core.Names.Table(SqlNames.Entities)} AS n ")
            .Append($"WHERE n.group_id IN {b.Ids(groupArray)} AND n.name_key IN {hashes} ")
            .Append($"AND n.name IN {b.List(nameArray)}");
        return Core.ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
    }

    public Task<IReadOnlyList<EntityNode>> GetByAttributeValuesAsync(
        string groupId,
        IReadOnlyDictionary<string, object?> attributes,
        IReadOnlyList<string>? nodeLabels = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<EntityNode>>([]);
        }

        // Scalars are matched in SQL with OPENJSON (strings exactly, numbers by value, booleans and null by type);
        // lists and objects are matched by type in SQL and compared as JSON here.
        var b = new SqlBuilder();
        var conditions = new List<string> { $"n.group_id = {b.Text(groupId)}" };
        var nested = new List<KeyValuePair<string, object?>>();
        foreach (var (key, value) in attributes)
        {
            string condition;
            switch (value)
            {
                case null:
                    condition = "a.[type] = 0";
                    break;
                case string text:
                    condition = $"a.[type] = 1 AND a.[value] COLLATE {SqlServerGraphOptions.BinaryCollation} = {b.Text(text)}";
                    break;
                case bool flag:
                    condition = $"a.[type] = 3 AND a.[value] = {b.Text(flag ? "true" : "false")}";
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    condition = "a.[type] = 2 AND TRY_CAST(a.[value] AS float) = "
                        + b.Float(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                default:
                    condition = "a.[type] IN (4, 5)";
                    nested.Add(new(key, value));
                    break;
            }

            conditions.Add("EXISTS (SELECT 1 FROM OPENJSON(n.attributes) AS a WHERE a.[key] COLLATE "
                + $"{SqlServerGraphOptions.BinaryCollation} = {b.Text(key)} AND {condition})");
        }

        foreach (var label in nodeLabels ?? [])
        {
            conditions.Add(FilterSql.HasAnyLabel("n.labels", $"({b.Text(label)})"));
        }

        var top = nested.Count == 0 ? $"TOP ({b.Int(limit)}) " : "";
        b.Append($"SELECT {top}{RecordMapper.EntityColumns("n")} FROM {Core.Names.Table(SqlNames.Entities)} AS n WHERE ")
            .Append(string.Join(" AND ", conditions))
            .Append(" ORDER BY n.created_at, n.uuid");
        return FilterNestedAsync();

        async Task<IReadOnlyList<EntityNode>> FilterNestedAsync()
        {
            var rows = await Core.ListAsync(b, RecordMapper.ReadEntity, cancellationToken).ConfigureAwait(false);
            if (nested.Count == 0)
            {
                return rows;
            }

            return rows
                .Where(node => nested.All(pair => node.Attributes.TryGetValue(pair.Key, out var stored)
                    && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(stored), JsonSerializer.SerializeToNode(pair.Value))))
                .Take(limit)
                .ToList();
        }
    }

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
    public override Task UpsertAsync(Db db, IReadOnlyList<CommunityNode> items, CancellationToken ct) =>
        Core.UpsertCommunitiesAsync(db, items, ct);

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
    public override Task UpsertAsync(Db db, IReadOnlyList<SagaNode> items, CancellationToken ct) =>
        Core.UpsertSagasAsync(db, items, ct);

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
    public override Task UpsertAsync(Db db, IReadOnlyList<EntityEdge> items, CancellationToken ct) =>
        Core.UpsertEntityEdgesAsync(db, items, ct);

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

/// <summary>Reads embeddings from the native <c>vector</c> columns.</summary>
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

        var b = new SqlBuilder();
        b.Append($"SELECT uuid, {column} FROM {core.Names.Table(table)} WHERE uuid IN {b.Ids(DriverCore.ValidIds(uuids))}");
        return LoadAsync();

        async Task<Dictionary<string, float[]?>> LoadAsync()
        {
            var rows = await core.ListAsync(
                b,
                r => (Uuid: r.GetString(0),
                    Embedding: r.IsDBNull(1) ? null : r.GetFieldValue<SqlVector<float>>(1).Memory.ToArray()),
                ct).ConfigureAwait(false);
            return rows.ToDictionary(r => r.Uuid, r => r.Embedding);
        }
    }
}
