// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/neo4j/operations/graph_ops.py (Graphiti v0.31.2) and changed for .NET: Cypher
// becomes SQL, and index creation becomes schema creation from the EF Core model.

using GraphitiNet.Driver.PostgreSql.Entities;
using GraphitiNet.Driver.PostgreSql.Internal;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Operations;

/// <summary>Schema, clean-up and community helpers.</summary>
internal sealed class GraphMaintenanceOperations(DriverCore core) : IGraphMaintenanceOperations
{
    private SqlNames N => core.Names;

    public Task ClearDataAsync(IReadOnlyList<string>? groupIds = null, CancellationToken cancellationToken = default)
    {
        if (groupIds is { Count: 0 })
        {
            return Task.CompletedTask;
        }

        return core.WithConnectionAsync(
            async connection =>
            {
                await using var batch = new NpgsqlBatch(connection);
                if (groupIds is null)
                {
                    DriverCore.AddCommand(batch, "TRUNCATE " + string.Join(", ", SqlNames.AllTables.Select(N.Table)));
                }
                else
                {
                    // Edges in the groups, then nodes in the groups with every edge touching them.
                    foreach (var edgeTable in SqlNames.EdgeTables)
                    {
                        DriverCore.AddCommand(
                            batch,
                            $"DELETE FROM {N.Table(edgeTable)} WHERE group_id = ANY($1)",
                            Params.TextArray(groupIds));
                    }

                    foreach (var nodeTable in SqlNames.NodeTables)
                    {
                        var nodes = $"SELECT uuid FROM {N.Table(nodeTable)} WHERE group_id = ANY($1)";
                        foreach (var edgeTable in SqlNames.EdgeTables)
                        {
                            DriverCore.AddCommand(
                                batch,
                                $"DELETE FROM {N.Table(edgeTable)} WHERE source_node_uuid IN ({nodes}) "
                                + $"OR target_node_uuid IN ({nodes})",
                                Params.TextArray(groupIds));
                        }

                        DriverCore.AddCommand(
                            batch,
                            $"DELETE FROM {N.Table(nodeTable)} WHERE group_id = ANY($1)",
                            Params.TextArray(groupIds));
                    }
                }

                await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task BuildIndicesAndConstraintsAsync(
        bool deleteExisting = false,
        CancellationToken cancellationToken = default) =>
        SchemaBuilder.EnsureSchemaAsync(core, deleteExisting, cancellationToken);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>> GetEntityProjectionAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var entities = N.Table(SqlNames.Entities);
        // Facts between two entities of the group, counted once per direction they can be walked (a self-loop
        // once), as upstream's undirected (n)-[e:RELATES_TO]-(m) match.
        var sql = "WITH pair AS ("
            + $"SELECT f.source_node_uuid AS a, f.target_node_uuid AS b FROM {N.Table(SqlNames.EntityEdges)} f "
            + $"JOIN {entities} s ON s.uuid = f.source_node_uuid AND s.group_id = $1 "
            + $"JOIN {entities} t ON t.uuid = f.target_node_uuid AND t.group_id = $1), "
            + "walk AS (SELECT a, b FROM pair UNION ALL SELECT b, a FROM pair WHERE a <> b) "
            + $"SELECT n.uuid, w.b, count(w.b)::int FROM {entities} n LEFT JOIN walk w ON w.a = n.uuid "
            + "WHERE n.group_id = $1 GROUP BY n.uuid, w.b ORDER BY n.uuid, w.b";
        return core.WithConnectionAsync<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>>(
            async connection =>
            {
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.Add(Params.Text(groupId));
                var rows = await DriverCore.ReadListAsync(
                    command,
                    r => (Node: r.GetString(0), Neighbor: r.IsDBNull(1) ? null : r.GetString(1), Count: r.GetInt32(2)),
                    cancellationToken).ConfigureAwait(false);
                var projection = new Dictionary<string, IReadOnlyList<Neighbor>>(StringComparer.Ordinal);
                foreach (var group in rows.GroupBy(r => r.Node))
                {
                    projection[group.Key] = group
                        .Where(r => r.Neighbor is not null)
                        .Select(r => new Neighbor(r.Neighbor!, r.Count))
                        .ToList();
                }

                return projection;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetEntityGroupIdsAsync(CancellationToken cancellationToken = default) =>
        core.QueryAsync<IReadOnlyList<string>>(async context => await context.Set<EntityNodeRecord>()
            .AsNoTracking()
            .Select(e => e.GroupId)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

    public Task RemoveCommunitiesAsync(
        IReadOnlyList<string>? groupIds = null,
        CancellationToken cancellationToken = default) =>
        core.WithConnectionAsync(
            c => core.DeleteNodesByGroupAsync(
                c, SqlNames.Communities, groupIds is { Count: > 0 } ? groupIds : null, cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<EntityNode>> GetMentionedNodesAsync(
        IReadOnlyList<EpisodicNode> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        if (episodes.Count == 0)
        {
            return [];
        }

        var uuids = episodes.Select(e => e.Uuid).Distinct().ToArray();
        var rows = await core.QueryAsync(context => context.Set<EntityNodeRecord>()
            .AsNoTracking()
            .Where(n => context.Set<EpisodicEdgeRecord>()
                .Any(m => m.TargetNodeUuid == n.Uuid && uuids.Contains(m.SourceNodeUuid)))
            .Select(RecordMapper.EntityProjection)
            .ToListAsync(cancellationToken)).ConfigureAwait(false);
        return rows.ConvertAll(RecordMapper.ToModel);
    }

    public async Task<IReadOnlyList<CommunityNode>> GetCommunitiesByNodesAsync(
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0)
        {
            return [];
        }

        var uuids = nodes.Select(n => n.Uuid).Distinct().ToArray();
        var rows = await core.QueryAsync(context => context.Set<CommunityRecord>()
            .AsNoTracking()
            .Where(c => context.Set<CommunityEdgeRecord>()
                .Any(m => m.SourceNodeUuid == c.Uuid && uuids.Contains(m.TargetNodeUuid)))
            .Select(RecordMapper.CommunityProjection)
            .ToListAsync(cancellationToken)).ConfigureAwait(false);
        return rows.ConvertAll(RecordMapper.ToModel);
    }

    public Task<IReadOnlyList<CommunityNode>> GetNeighborCommunitiesAsync(
        string entityUuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityUuid);
        var facts = N.Table(SqlNames.EntityEdges);
        // One row per (fact, membership edge) pair, facts in either direction, as upstream's
        // (c)-[:HAS_MEMBER]->(m)-[:RELATES_TO]-(n) match.
        var sql = $"SELECT {RecordMapper.CommunityColumns("c")} FROM ("
            + $"SELECT f.uuid AS fact, f.target_node_uuid AS member FROM {facts} f WHERE f.source_node_uuid = $1 "
            + $"UNION ALL SELECT f.uuid, f.source_node_uuid FROM {facts} f WHERE f.target_node_uuid = $1) AS x "
            + $"JOIN {N.Table(SqlNames.CommunityEdges)} ce ON ce.target_node_uuid = x.member "
            + $"JOIN {N.Table(SqlNames.Communities)} c ON c.uuid = ce.source_node_uuid ORDER BY c.uuid, x.member";
        return core.WithConnectionAsync<IReadOnlyList<CommunityNode>>(
            async connection =>
            {
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.Add(Params.Text(entityUuid));
                return await DriverCore.ReadListAsync(command, RecordMapper.ReadCommunity, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountEpisodeMentionsAsync(
        IReadOnlyList<string> entityUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityUuids);
        var result = entityUuids.Distinct().ToDictionary(u => u, _ => 0, StringComparer.Ordinal);
        if (result.Count == 0)
        {
            return result;
        }

        var uuids = result.Keys.ToArray();
        var counts = await core.QueryAsync(context => context.Set<EpisodicEdgeRecord>()
            .AsNoTracking()
            .Where(m => uuids.Contains(m.TargetNodeUuid))
            .GroupBy(m => m.TargetNodeUuid)
            .Select(g => new { Uuid = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)).ConfigureAwait(false);
        foreach (var row in counts)
        {
            result[row.Uuid] = row.Count;
        }

        return result;
    }
}
