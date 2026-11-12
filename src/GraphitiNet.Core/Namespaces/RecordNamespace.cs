// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/namespaces/nodes.py and graphiti_core/namespaces/edges.py (Graphiti v0.31.2) and changed
// for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Models;

namespace GraphitiNet.Namespaces;

/// <summary>
/// Storage operations for one record type, bound to a driver: the shape of upstream's per-type namespaces
/// (<c>graphiti.nodes.entity</c>, <c>graphiti.edges.entity</c> and so on).
/// </summary>
/// <remarks>Upstream's <c>tx</c> (transaction) and <c>batch_size</c> parameters are not ported: drivers decide how to
/// batch and transact.</remarks>
/// <typeparam name="T">The record type.</typeparam>
public abstract class RecordNamespace<T>
{
    private readonly IRecordOperations<T> _ops;

    private protected RecordNamespace(IRecordOperations<T> ops)
    {
        _ops = ops;
    }

    /// <summary>Saves the record (upstream <c>save</c>) and returns it.</summary>
    public virtual async Task<T> SaveAsync(T item, CancellationToken cancellationToken = default)
    {
        await _ops.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>Saves the records as they are (upstream <c>save_bulk</c>); no embeddings are generated.</summary>
    public Task SaveBulkAsync(IReadOnlyList<T> items, CancellationToken cancellationToken = default) =>
        _ops.SaveBulkAsync(items, cancellationToken);

    /// <summary>Deletes the record (upstream <c>delete</c>). Deleting a node also deletes the edges that touch
    /// it.</summary>
    public Task DeleteAsync(T item, CancellationToken cancellationToken = default) =>
        _ops.DeleteAsync(item, cancellationToken);

    /// <summary>Deletes the records with these ids (upstream <c>delete_by_uuids</c>).</summary>
    public Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default) =>
        _ops.DeleteByUuidsAsync(uuids, cancellationToken);

    /// <summary>Gets one record (upstream <c>get_by_uuid</c>).</summary>
    /// <exception cref="NodeNotFoundException">No node has this id.</exception>
    /// <exception cref="EdgeNotFoundException">No edge has this id.</exception>
    public Task<T> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default) =>
        _ops.GetByUuidAsync(uuid, cancellationToken);

    /// <summary>Gets the records that exist among these ids (upstream <c>get_by_uuids</c>).</summary>
    public Task<IReadOnlyList<T>> GetByUuidsAsync(
        IReadOnlyList<string> uuids,
        CancellationToken cancellationToken = default) =>
        _ops.GetByUuidsAsync(uuids, cancellationToken);

    /// <summary>Gets records in these groups, newest id first, one page at a time (upstream
    /// <c>get_by_group_ids</c>).</summary>
    public Task<IReadOnlyList<T>> GetByGroupIdsAsync(
        IReadOnlyList<string> groupIds,
        int? limit = null,
        string? uuidCursor = null,
        CancellationToken cancellationToken = default) =>
        _ops.GetByGroupIdsAsync(groupIds, limit, uuidCursor, cancellationToken);
}

/// <summary>Storage operations for one node type.</summary>
/// <typeparam name="T">The node type.</typeparam>
public abstract class NodeRecordNamespace<T> : RecordNamespace<T>
    where T : Node
{
    private readonly INodeOperations<T> _ops;

    private protected NodeRecordNamespace(INodeOperations<T> ops)
        : base(ops)
    {
        _ops = ops;
    }

    /// <summary>Deletes every node of this type in the group, with the edges that touch them (upstream
    /// <c>delete_by_group_id</c>). Use <see cref="Graphiti.ClearDataAsync"/> to delete everything in a group.</summary>
    public Task DeleteByGroupIdAsync(string groupId, CancellationToken cancellationToken = default) =>
        _ops.DeleteByGroupIdAsync(groupId, cancellationToken);
}
