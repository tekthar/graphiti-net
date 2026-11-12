// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver;
using GraphitiNet.Models;

namespace GraphitiNet.Search;

/// <summary>
/// Decides which episodes the caller may read, so search returns only what the caller may see. Implement it over
/// your own permission store; one call checks a whole batch, so the host can answer with one query.
/// </summary>
/// <remarks>
/// <para>With a filter, search hides candidates <i>before</i> reranking and the limit, so the limit is filled from
/// visible results (graphiti-net extension; upstream has no access control). The rules:</para>
/// <list type="bullet">
/// <item><b>Facts</b> are visible only if every episode in <see cref="EntityEdge.Episodes"/> is readable. A fact with
/// no episodes is hidden.</item>
/// <item><b>Entities</b> are judged by the episodes in the entity's group that mention it (episodic edges), under
/// <see cref="EntityVisibility"/>. An entity no episode mentions is hidden.</item>
/// <item><b>Episodes</b> are visible only if readable.</item>
/// <item><b>Communities</b> are never returned while a filter is set: their summaries aggregate member entities and
/// there is no cheap way to check every member, so the safe choice is to leave them out.</item>
/// </list>
/// </remarks>
public interface IEpisodeAccessFilter
{
    /// <summary>The subset of <paramref name="episodeUuids"/> the caller may read. Ids not returned are treated as
    /// unreadable.</summary>
    /// <param name="episodeUuids">Distinct episode ids to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<IReadOnlySet<string>> GetReadableEpisodeUuidsAsync(
        IReadOnlyCollection<string> episodeUuids,
        CancellationToken cancellationToken = default);

    /// <summary>How entities are judged. The default, <see cref="Search.EntityVisibility.AllMentionsReadable"/>,
    /// matches the rule for facts and never shows an entity whose summary may draw on an unreadable episode.</summary>
    EntityVisibility EntityVisibility => EntityVisibility.AllMentionsReadable;
}

/// <summary>When an entity counts as visible under an <see cref="IEpisodeAccessFilter"/>.</summary>
public enum EntityVisibility
{
    /// <summary>Every episode that mentions the entity is readable (the default).</summary>
    AllMentionsReadable,

    /// <summary>At least one episode that mentions the entity is readable. Looser: the entity's name and summary may
    /// reflect episodes the caller cannot read.</summary>
    AnyMentionReadable,
}

/// <summary>Applies an <see cref="IEpisodeAccessFilter"/> to search candidates.</summary>
public static class EpisodeAccess
{
    /// <summary>
    /// How much more each search method fetches when a filter is set: <c>2 * limit * 4</c> candidates instead of
    /// upstream's <c>2 * limit</c>, so hidden candidates rarely starve the limit. Without a filter, search fetches
    /// exactly what upstream does.
    /// </summary>
    public const int CandidateMultiplier = 4;

    /// <summary>The facts whose episodes are all readable, in order.</summary>
    public static async Task<IReadOnlyList<EntityEdge>> FilterEdgesAsync(
        IEpisodeAccessFilter filter,
        IReadOnlyList<EntityEdge> edges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(edges);
        var visible = await VisibleEdgeUuidsAsync(filter, edges, cancellationToken).ConfigureAwait(false);
        return [.. edges.Where(edge => visible.Contains(edge.Uuid))];
    }

    /// <summary>The episodes the caller may read, in order.</summary>
    public static async Task<IReadOnlyList<EpisodicNode>> FilterEpisodesAsync(
        IEpisodeAccessFilter filter,
        IReadOnlyList<EpisodicNode> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(episodes);
        var readable = await ReadableAsync(filter, episodes.Select(e => e.Uuid), cancellationToken)
            .ConfigureAwait(false);
        return [.. episodes.Where(episode => readable.Contains(episode.Uuid))];
    }

    /// <summary>
    /// The entities visible under the filter's <see cref="IEpisodeAccessFilter.EntityVisibility"/>, in order, with
    /// the readable episodes that mention each visible entity.
    /// </summary>
    public static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, IReadOnlyList<string>>
        EpisodeUuids)> FilterNodesAsync(
            IGraphDriver driver,
            IEpisodeAccessFilter filter,
            IReadOnlyList<EntityNode> nodes,
            int? maxCoroutines = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var visible = await VisibleNodesAsync(driver, filter, nodes, maxCoroutines, cancellationToken)
            .ConfigureAwait(false);
        return ([.. nodes.Where(node => visible.ContainsKey(node.Uuid))], visible);
    }

    /// <summary>Ids of the visible facts.</summary>
    internal static async Task<HashSet<string>> VisibleEdgeUuidsAsync(
        IEpisodeAccessFilter filter,
        IEnumerable<EntityEdge> edges,
        CancellationToken cancellationToken)
    {
        var list = edges.ToList();
        var readable = await ReadableAsync(filter, list.SelectMany(e => e.Episodes), cancellationToken)
            .ConfigureAwait(false);
        return list.Where(edge => edge.Episodes.Count > 0 && edge.Episodes.All(readable.Contains))
            .Select(edge => edge.Uuid)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Visible entity ids, each with its readable mentioning episodes.</summary>
    internal static async Task<Dictionary<string, IReadOnlyList<string>>> VisibleNodesAsync(
        IGraphDriver driver,
        IEpisodeAccessFilter filter,
        IEnumerable<EntityNode> nodes,
        int? maxCoroutines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(filter);

        var distinct = nodes.DistinctBy(node => node.Uuid).ToList();
        var mentions = await SearchUtils.GetMentioningEpisodeUuidsAsync(driver, distinct, maxCoroutines, cancellationToken)
            .ConfigureAwait(false);
        var readable = await ReadableAsync(filter, mentions.Values.SelectMany(m => m), cancellationToken)
            .ConfigureAwait(false);

        var requireAll = filter.EntityVisibility == EntityVisibility.AllMentionsReadable;
        var visible = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var node in distinct)
        {
            var episodeUuids = mentions[node.Uuid];
            var readableMentions = episodeUuids.Where(readable.Contains).ToList();
            var isVisible = readableMentions.Count > 0
                && (!requireAll || readableMentions.Count == episodeUuids.Count);
            if (isVisible)
            {
                visible[node.Uuid] = readableMentions;
            }
        }

        return visible;
    }

    private static async Task<IReadOnlySet<string>> ReadableAsync(
        IEpisodeAccessFilter filter,
        IEnumerable<string> episodeUuids,
        CancellationToken cancellationToken)
    {
        var distinct = episodeUuids.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await filter.GetReadableEpisodeUuidsAsync(distinct, cancellationToken).ConfigureAwait(false);
    }
}
