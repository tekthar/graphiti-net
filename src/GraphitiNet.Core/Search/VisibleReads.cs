// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver;
using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Search;

/// <summary>One page of records and the cursor for the next page.</summary>
/// <typeparam name="T">The record type.</typeparam>
/// <param name="Items">Up to the page limit of visible records, in descending id order.</param>
/// <param name="NextCursor">Pass it as the cursor to read the next page; <see langword="null"/> when no records
/// remain.</param>
public sealed record RecordPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>One page of a group's graph: entities and the facts that start at them.</summary>
/// <param name="Entities">Up to the page limit of visible entities, in descending id order.</param>
/// <param name="Facts">The visible facts of the group whose source entity this page covers (scanned, whether or not
/// that entity is visible), in descending id order. Each fact appears on exactly one page.</param>
/// <param name="NextCursor">Pass it as the cursor to read the next page; <see langword="null"/> when no entities
/// remain.</param>
public sealed record GroupGraphPage(
    IReadOnlyList<EntityNode> Entities,
    IReadOnlyList<EntityEdge> Facts,
    string? NextCursor);

/// <summary>
/// Reads of the graph that apply an <see cref="IEpisodeAccessFilter"/> with the same rules as search (see
/// <see cref="IEpisodeAccessFilter"/>): a fact is visible only if every episode it came from is readable, an entity is
/// judged by the episodes in its group that mention it under <see cref="IEpisodeAccessFilter.EntityVisibility"/>, an
/// episode only if it is readable. A graphiti-net addition; also exposed on <see cref="Graphiti"/>.
/// </summary>
/// <remarks>
/// <para>Without a filter each read returns what the record namespaces return
/// (<c>graphiti.Nodes.Entity.GetByGroupIdsAsync</c>, <c>graphiti.Edges.Entity.GetByNodeUuidAsync</c>,
/// <c>graphiti.Nodes.Episode.GetByGroupIdsAsync</c>).</para>
/// <para>Paging follows the driver contract, descending id with an id cursor, and stays stable under filtering: a page
/// holds up to <c>limit</c> <i>visible</i> records, and the next cursor is the id of the last record <i>scanned</i>
/// (visible or not). The read fetches underlying pages until the page is full or the records run out, so hidden
/// records never shorten a page except the last, and no record is returned twice or skipped while records are added
/// or removed between calls. The last page can be empty.</para>
/// </remarks>
public static class VisibleReads
{
    /// <summary>The page limit used when none is given.</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// One page of a group's graph: the visible entities, newest id first, and the visible facts that start at the
    /// entities the page scanned.
    /// </summary>
    /// <remarks>Facts and entities are judged separately, as search judges them, so a visible fact can point at an
    /// entity that is hidden. A host drawing the graph can drop such facts.</remarks>
    /// <param name="driver">Storage.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="episodeAccessFilter">Hides what the caller may not read; <see langword="null"/> hides
    /// nothing.</param>
    /// <param name="limit">The most entities on the page.</param>
    /// <param name="cursor">The previous page's <see cref="GroupGraphPage.NextCursor"/>, or <see langword="null"/>
    /// for the first page.</param>
    /// <param name="maxCoroutines">Concurrency cap for the per-entity fact reads.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<GroupGraphPage> GetGroupGraphAsync(
        IGraphDriver driver,
        string groupId,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        int limit = DefaultLimit,
        string? cursor = null,
        int? maxCoroutines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(groupId);
        Validation.ValidateGroupId(groupId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var (entities, scanned, nextCursor) = await ScanAsync(
            (size, after) => driver.EntityNodeOps.GetByGroupIdsAsync([groupId], size, after, cancellationToken),
            episodeAccessFilter is null
                ? null
                : async batch => (await EpisodeAccess.VisibleNodesAsync(
                        driver, episodeAccessFilter, batch, maxCoroutines, cancellationToken).ConfigureAwait(false))
                    .Keys.ToHashSet(StringComparer.Ordinal),
            node => node.Uuid,
            limit,
            cursor,
            episodeAccessFilter,
            cancellationToken).ConfigureAwait(false);

        var scannedUuids = scanned.Select(node => node.Uuid).ToHashSet(StringComparer.Ordinal);
        var perNode = await Concurrency.SemaphoreGatherAsync(
            scanned.Select<EntityNode, Func<Task<IReadOnlyList<EntityEdge>>>>(node =>
                () => driver.EntityEdgeOps.GetByNodeUuidAsync(node.Uuid, cancellationToken)),
            maxCoroutines,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<EntityEdge> facts = [.. perNode
            .SelectMany(edges => edges)
            .Where(edge => edge.GroupId == groupId && scannedUuids.Contains(edge.SourceNodeUuid))
            .DistinctBy(edge => edge.Uuid)
            .OrderByDescending(edge => edge.Uuid, StringComparer.Ordinal)];
        if (episodeAccessFilter is not null)
        {
            facts = await EpisodeAccess.FilterEdgesAsync(episodeAccessFilter, facts, cancellationToken)
                .ConfigureAwait(false);
        }

        return new GroupGraphPage(entities, facts, nextCursor);
    }

    /// <summary>The visible facts that start or end at an entity (a node's edges).</summary>
    /// <remarks>Facts are judged on their own episodes, as in search, whether or not the entity itself is
    /// visible.</remarks>
    /// <param name="driver">Storage.</param>
    /// <param name="nodeUuid">The entity.</param>
    /// <param name="episodeAccessFilter">Hides what the caller may not read; <see langword="null"/> hides
    /// nothing.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<EntityEdge>> GetFactsByNodeUuidAsync(
        IGraphDriver driver,
        string nodeUuid,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(nodeUuid);
        var facts = await driver.EntityEdgeOps.GetByNodeUuidAsync(nodeUuid, cancellationToken).ConfigureAwait(false);
        return episodeAccessFilter is null
            ? facts
            : await EpisodeAccess.FilterEdgesAsync(episodeAccessFilter, facts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One page of a group's readable episodes, newest id first.</summary>
    /// <param name="driver">Storage.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="episodeAccessFilter">Hides what the caller may not read; <see langword="null"/> hides
    /// nothing.</param>
    /// <param name="limit">The most episodes on the page.</param>
    /// <param name="cursor">The previous page's <see cref="RecordPage{T}.NextCursor"/>, or <see langword="null"/>
    /// for the first page.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<RecordPage<EpisodicNode>> GetGroupEpisodesAsync(
        IGraphDriver driver,
        string groupId,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        int limit = DefaultLimit,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(groupId);
        Validation.ValidateGroupId(groupId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var (episodes, _, nextCursor) = await ScanAsync(
            (size, after) => driver.EpisodeNodeOps.GetByGroupIdsAsync([groupId], size, after, cancellationToken),
            episodeAccessFilter is null
                ? null
                : async batch => (await EpisodeAccess.FilterEpisodesAsync(
                        episodeAccessFilter, batch, cancellationToken).ConfigureAwait(false))
                    .Select(episode => episode.Uuid).ToHashSet(StringComparer.Ordinal),
            episode => episode.Uuid,
            limit,
            cursor,
            episodeAccessFilter,
            cancellationToken).ConfigureAwait(false);
        return new RecordPage<EpisodicNode>(episodes, nextCursor);
    }

    /// <summary>
    /// Fills a page with up to <paramref name="limit"/> visible records, fetching underlying pages (descending id,
    /// id cursor) until it is full or the records run out.
    /// </summary>
    /// <returns>The visible records, every record scanned, and the id of the last record scanned (or
    /// <see langword="null"/> when none remain).</returns>
    private static async Task<(List<T> Visible, List<T> Scanned, string? NextCursor)> ScanAsync<T>(
        Func<int, string?, Task<IReadOnlyList<T>>> fetch,
        Func<IReadOnlyList<T>, Task<HashSet<string>>>? visibleUuids,
        Func<T, string> uuidOf,
        int limit,
        string? cursor,
        IEpisodeAccessFilter? episodeAccessFilter,
        CancellationToken cancellationToken)
    {
        // Without a filter one underlying page is the answer; with one, fetch more per round trip, as search does.
        var batchSize = episodeAccessFilter is null ? limit : checked(limit * EpisodeAccess.CandidateMultiplier);
        var visible = new List<T>();
        var scanned = new List<T>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await fetch(batchSize, cursor).ConfigureAwait(false);
            var visibleInBatch = visibleUuids is null ? null : await visibleUuids(batch).ConfigureAwait(false);
            var exhausted = batch.Count < batchSize;
            for (var i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                scanned.Add(item);
                cursor = uuidOf(item);
                if (visibleInBatch is not null && !visibleInBatch.Contains(cursor))
                {
                    continue;
                }

                visible.Add(item);
                if (visible.Count == limit)
                {
                    var last = i == batch.Count - 1;
                    return (visible, scanned, last && exhausted ? null : cursor);
                }
            }

            if (exhausted)
            {
                return (visible, scanned, null);
            }
        }
    }
}
