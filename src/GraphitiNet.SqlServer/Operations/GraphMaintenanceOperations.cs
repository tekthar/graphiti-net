// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/neo4j/operations/graph_ops.py (Graphiti v0.31.2) and changed for .NET: Cypher
// becomes T-SQL, and index creation becomes schema creation from the EF Core model.

using GraphitiNet.Driver.SqlServer.Entities;
using GraphitiNet.Driver.SqlServer.Internal;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;

namespace GraphitiNet.Driver.SqlServer.Operations;

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

        var b = new SqlBuilder().Append("SET NOCOUNT ON; SET XACT_ABORT ON; ");
        if (groupIds is null)
        {
            foreach (var table in SqlNames.AllTables)
            {
                b.Append($"TRUNCATE TABLE {N.Table(table)}; ");
            }
        }
        else
        {
            // Edges in the groups, then nodes in the groups with every edge touching them; keyword index rows go
            // with their records.
            var groups = b.Ids(groupIds);
            var facts = N.Table(SqlNames.EntityEdges);
            core.AppendDeleteIndex(b, DocKind.Facts, $"(SELECT uuid FROM {facts} WHERE group_id IN {groups})");
            foreach (var edgeTable in SqlNames.EdgeTables)
            {
                b.Append($"DELETE FROM {N.Table(edgeTable)} WHERE group_id IN {groups}; ");
            }

            foreach (var nodeTable in SqlNames.NodeTables)
            {
                core.AppendDeleteNodes(
                    b, nodeTable, $"(SELECT uuid AS v FROM {N.Table(nodeTable)} WHERE group_id IN {groups})");
            }
        }

        return core.InTransactionAsync(db => DriverCore.ExecuteAsync(db, b, cancellationToken), cancellationToken);
    }

    public Task BuildIndicesAndConstraintsAsync(
        bool deleteExisting = false,
        CancellationToken cancellationToken = default) =>
        SchemaBuilder.EnsureSchemaAsync(core, deleteExisting, cancellationToken);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>> GetEntityProjectionAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var entities = N.Table(SqlNames.Entities);
        var b = new SqlBuilder();
        var group = b.Text(groupId);
        // Facts between two entities of the group, counted once per direction they can be walked (a self-loop
        // once), as upstream's undirected (n)-[e:RELATES_TO]-(m) match.
        b.Append("WITH pair AS (")
            .Append($"SELECT f.source_node_uuid AS a, f.target_node_uuid AS b FROM {N.Table(SqlNames.EntityEdges)} f ")
            .Append($"JOIN {entities} s ON s.uuid = f.source_node_uuid AND s.group_id = {group} ")
            .Append($"JOIN {entities} t ON t.uuid = f.target_node_uuid AND t.group_id = {group}), ")
            .Append("walk AS (SELECT a, b FROM pair UNION ALL SELECT b, a FROM pair WHERE a <> b) ")
            .Append($"SELECT n.uuid, w.b, COUNT(w.b) FROM {entities} n LEFT JOIN walk w ON w.a = n.uuid ")
            .Append($"WHERE n.group_id = {group} GROUP BY n.uuid, w.b ORDER BY n.uuid, w.b");
        var rows = await core.ListAsync(
            b,
            r => (Node: r.GetString(0), Neighbor: r.IsDBNull(1) ? null : r.GetString(1), Count: r.GetInt32(2)),
            cancellationToken).ConfigureAwait(false);
        var projection = new Dictionary<string, IReadOnlyList<Neighbor>>(StringComparer.Ordinal);
        foreach (var node in rows.GroupBy(r => r.Node))
        {
            projection[node.Key] = node
                .Where(r => r.Neighbor is not null)
                .Select(r => new Neighbor(r.Neighbor!, r.Count))
                .ToList();
        }

        return projection;
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
        core.DeleteNodesByGroupAsync(
            SqlNames.Communities, groupIds is { Count: > 0 } ? groupIds : null, cancellationToken);

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
        var b = new SqlBuilder();
        var entity = b.Text(entityUuid);
        // One row per (fact, membership edge) pair, facts in either direction, as upstream's
        // (c)-[:HAS_MEMBER]->(m)-[:RELATES_TO]-(n) match.
        b.Append($"SELECT {RecordMapper.CommunityColumns("c")} FROM (")
            .Append($"SELECT f.uuid AS fact, f.target_node_uuid AS member FROM {facts} f WHERE f.source_node_uuid = {entity} ")
            .Append($"UNION ALL SELECT f.uuid, f.source_node_uuid FROM {facts} f WHERE f.target_node_uuid = {entity}) AS x ")
            .Append($"JOIN {N.Table(SqlNames.CommunityEdges)} ce ON ce.target_node_uuid = x.member ")
            .Append($"JOIN {N.Table(SqlNames.Communities)} c ON c.uuid = ce.source_node_uuid ORDER BY c.uuid, x.member");
        return core.ListAsync(b, RecordMapper.ReadCommunity, cancellationToken);
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
