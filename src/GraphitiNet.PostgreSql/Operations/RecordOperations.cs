// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Linq.Expressions;
using GraphitiNet.Driver.PostgreSql.Entities;
using GraphitiNet.Driver.PostgreSql.Internal;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Operations;

/// <summary>
/// CRUD shared by every record type: upserts through batched <c>INSERT ... ON CONFLICT</c>, reads through no-tracking
/// LINQ that never selects embeddings, deletes in one round trip.
/// </summary>
internal abstract class RecordOperations<TModel, TRecord>(
    DriverCore core,
    string table,
    Expression<Func<TRecord, TRecord>> projection,
    Func<TRecord, TModel> toModel)
    : IRecordOperations<TModel>
    where TRecord : GraphRecord
{
    protected DriverCore Core { get; } = core;

    protected string Table { get; } = table;

    protected abstract bool IsNode { get; }

    public Task SaveAsync(TModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return SaveBulkAsync([item], cancellationToken);
    }

    public Task SaveBulkAsync(IReadOnlyList<TModel> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count == 0
            ? Task.CompletedTask
            : Core.WithConnectionAsync(c => UpsertAsync(c, items, cancellationToken), cancellationToken);
    }

    /// <summary>Upserts on an open connection (and its transaction, if any).</summary>
    public abstract Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<TModel> items, CancellationToken ct);

    public Task DeleteAsync(TModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return DeleteByUuidsAsync([UuidOf(item)], cancellationToken);
    }

    public Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        if (uuids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Core.WithConnectionAsync(
            c => IsNode
                ? Core.DeleteNodesAsync(c, Table, uuids, cancellationToken)
                : Core.DeleteEdgesAsync(c, Table, uuids, cancellationToken),
            cancellationToken);
    }

    public async Task<TModel> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uuid);
        var found = await GetByUuidsAsync([uuid], cancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
        {
            throw IsNode ? new NodeNotFoundException(uuid) : new EdgeNotFoundException(uuid);
        }

        return found[0];
    }

    public Task<IReadOnlyList<TModel>> GetByUuidsAsync(
        IReadOnlyList<string> uuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uuids);
        if (uuids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<TModel>>([]);
        }

        var ids = uuids.Distinct().ToArray();
        return ListAsync(q => q.Where(r => ids.Contains(r.Uuid)), cancellationToken);
    }

    public Task<IReadOnlyList<TModel>> GetByGroupIdsAsync(
        IReadOnlyList<string> groupIds,
        int? limit = null,
        string? uuidCursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        if (groupIds.Count == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<TModel>>([]);
        }

        var groups = groupIds.ToArray();
        return ListAsync(
            q =>
            {
                q = q.Where(r => groups.Contains(r.GroupId));
                if (uuidCursor is not null)
                {
                    // The uuid column uses the "C" collation, so this comparison is ordinal.
                    q = q.Where(r => r.Uuid.CompareTo(uuidCursor) < 0);
                }

                q = q.OrderByDescending(r => r.Uuid);
                return limit is { } l ? q.Take(l) : q;
            },
            cancellationToken);
    }

    /// <summary>Runs a no-tracking LINQ query over the table and maps the rows.</summary>
    protected Task<IReadOnlyList<TModel>> ListAsync(
        Func<IQueryable<TRecord>, IQueryable<TRecord>> shape,
        CancellationToken cancellationToken) =>
        ListAsync((_, q) => shape(q), cancellationToken);

    /// <summary>Runs a no-tracking LINQ query over the table, with access to the context for joins, and maps the
    /// rows.</summary>
    protected Task<IReadOnlyList<TModel>> ListAsync(
        Func<DbContext, IQueryable<TRecord>, IQueryable<TRecord>> shape,
        CancellationToken cancellationToken) =>
        Core.QueryAsync<IReadOnlyList<TModel>>(async context =>
        {
            var rows = await shape(context, context.Set<TRecord>().AsNoTracking())
                .Select(projection)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return rows.ConvertAll(r => toModel(r));
        });

    protected abstract string UuidOf(TModel item);
}

/// <summary>Node CRUD: deletes cascade to every edge touching the node.</summary>
internal abstract class NodeOperations<TModel, TRecord>(
    DriverCore core,
    string table,
    Expression<Func<TRecord, TRecord>> projection,
    Func<TRecord, TModel> toModel)
    : RecordOperations<TModel, TRecord>(core, table, projection, toModel), INodeOperations<TModel>
    where TModel : Node
    where TRecord : NodeRecord
{
    protected override bool IsNode => true;

    public Task DeleteByGroupIdAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        return Core.WithConnectionAsync(
            c => Core.DeleteNodesByGroupAsync(c, Table, [groupId], cancellationToken),
            cancellationToken);
    }

    protected override string UuidOf(TModel item) => item.Uuid;
}

/// <summary>Edge CRUD for one edge table.</summary>
internal class EdgeOperations<TModel, TRecord>(
    DriverCore core,
    string table,
    Expression<Func<TRecord, TRecord>> projection,
    Func<TRecord, TModel> toModel)
    : RecordOperations<TModel, TRecord>(core, table, projection, toModel)
    where TModel : Edge
    where TRecord : EdgeRecord
{
    protected override bool IsNode => false;

    public override Task UpsertAsync(NpgsqlConnection connection, IReadOnlyList<TModel> items, CancellationToken ct) =>
        Core.UpsertPlainEdgesAsync(connection, Table, items, ct);

    protected override string UuidOf(TModel item) => item.Uuid;
}

/// <summary>Plain edge operations (no extra columns).</summary>
internal sealed class EpisodicEdgeOperations(DriverCore core)
    : EdgeOperations<EpisodicEdge, EpisodicEdgeRecord>(
        core, SqlNames.EpisodicEdges, r => r, RecordMapper.ToEdge<EpisodicEdge>), IEpisodicEdgeOperations;

/// <summary>Community membership operations.</summary>
internal sealed class CommunityEdgeOperations(DriverCore core)
    : EdgeOperations<CommunityEdge, CommunityEdgeRecord>(
        core, SqlNames.CommunityEdges, r => r, RecordMapper.ToEdge<CommunityEdge>), ICommunityEdgeOperations;

/// <summary>Saga-to-episode link operations.</summary>
internal sealed class HasEpisodeEdgeOperations(DriverCore core)
    : EdgeOperations<HasEpisodeEdge, HasEpisodeEdgeRecord>(
        core, SqlNames.HasEpisodeEdges, r => r, RecordMapper.ToEdge<HasEpisodeEdge>), IHasEpisodeEdgeOperations;

/// <summary>Episode chain operations.</summary>
internal sealed class NextEpisodeEdgeOperations(DriverCore core)
    : EdgeOperations<NextEpisodeEdge, NextEpisodeEdgeRecord>(
        core, SqlNames.NextEpisodeEdges, r => r, RecordMapper.ToEdge<NextEpisodeEdge>), INextEpisodeEdgeOperations;
