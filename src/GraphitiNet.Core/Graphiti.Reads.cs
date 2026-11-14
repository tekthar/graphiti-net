// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet;

// Reads that apply an episode access filter (graphiti-net additions; upstream has no access control). See
// Search/VisibleReads.cs for the rules and the paging contract.
public sealed partial class Graphiti
{
    /// <summary>
    /// One page of a group's graph that the caller may see: entities, newest id first, and the facts that start at
    /// them. Applies <paramref name="episodeAccessFilter"/> with the same rules as search; see
    /// <see cref="VisibleReads.GetGroupGraphAsync"/> for visibility and paging. A graphiti-net addition.
    /// </summary>
    /// <param name="groupId">The group.</param>
    /// <param name="episodeAccessFilter">Hides what the caller may not read; <see langword="null"/> returns what
    /// <c>Nodes.Entity.GetByGroupIdsAsync</c> and <c>Edges.Entity.GetByNodeUuidAsync</c> return.</param>
    /// <param name="limit">The most entities on the page.</param>
    /// <param name="cursor">The previous page's <see cref="GroupGraphPage.NextCursor"/>; <see langword="null"/> for
    /// the first page.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<GroupGraphPage> GetGroupGraphAsync(
        string groupId,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        int limit = VisibleReads.DefaultLimit,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        VisibleReads.GetGroupGraphAsync(
            Driver, groupId, episodeAccessFilter, limit, cursor, MaxCoroutines, cancellationToken);

    /// <summary>
    /// The facts that start or end at an entity that the caller may see (a node's edges), with the same rule as
    /// search: every episode a fact came from is readable. A graphiti-net addition.
    /// </summary>
    /// <param name="nodeUuid">The entity.</param>
    /// <param name="episodeAccessFilter">Hides what the caller may not read; <see langword="null"/> returns what
    /// <c>Edges.Entity.GetByNodeUuidAsync</c> returns.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<IReadOnlyList<EntityEdge>> GetFactsByNodeUuidAsync(
        string nodeUuid,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default) =>
        VisibleReads.GetFactsByNodeUuidAsync(Driver, nodeUuid, episodeAccessFilter, cancellationToken);

    /// <summary>
    /// One page of a group's episodes that the caller may read, newest id first; see
    /// <see cref="VisibleReads.GetGroupEpisodesAsync"/> for paging. A graphiti-net addition.
    /// </summary>
    /// <param name="groupId">The group.</param>
    /// <param name="episodeAccessFilter">Hides what the caller may not read; <see langword="null"/> returns what
    /// <c>Nodes.Episode.GetByGroupIdsAsync</c> returns.</param>
    /// <param name="limit">The most episodes on the page.</param>
    /// <param name="cursor">The previous page's <see cref="RecordPage{T}.NextCursor"/>; <see langword="null"/> for
    /// the first page.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<RecordPage<EpisodicNode>> GetGroupEpisodesAsync(
        string groupId,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        int limit = VisibleReads.DefaultLimit,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        VisibleReads.GetGroupEpisodesAsync(Driver, groupId, episodeAccessFilter, limit, cursor, cancellationToken);
}
