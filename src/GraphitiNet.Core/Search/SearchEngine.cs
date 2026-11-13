// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Models;
using GraphitiNet.Utils;
using static GraphitiNet.Search.SearchTracing;

namespace GraphitiNet.Search;

/// <summary>
/// The search engine, as upstream's <c>search.py</c>: runs the configured search methods per scope concurrently,
/// fuses and reranks the results, and applies the limit.
/// </summary>
public static class SearchEngine
{
    /// <summary>
    /// Searches the graph. Ported from upstream's <c>search()</c>.
    /// </summary>
    /// <param name="driver">The graph driver.</param>
    /// <param name="embedder">Embeds the query when a scope uses vector search or MMR and no
    /// <paramref name="queryVector"/> is given.</param>
    /// <param name="crossEncoder">Reranks for the cross-encoder rerankers; may be <see langword="null"/> when no scope
    /// uses one.</param>
    /// <param name="query">The query text. A blank query returns empty results.</param>
    /// <param name="groupIds">Groups to search. <see langword="null"/>, an empty list or <c>[""]</c> searches every
    /// group, as upstream.</param>
    /// <param name="config">What to search and how.</param>
    /// <param name="searchFilter">Filters for facts, entities and episodes.</param>
    /// <param name="centerNodeUuid">The centre node for the node-distance rerankers.</param>
    /// <param name="bfsOriginNodeUuids">Origins for breadth-first search. When <see langword="null"/>, breadth-first
    /// search starts from the other methods' results instead; an empty list disables it.</param>
    /// <param name="queryVector">A precomputed query embedding; skips the embedder.</param>
    /// <param name="maxCoroutines">The concurrency limit; <see langword="null"/> uses
    /// <see cref="Concurrency.SemaphoreLimit"/>.</param>
    /// <param name="episodeAccessFilter">Hides results the caller may not read, before reranking and the limit (see
    /// <see cref="IEpisodeAccessFilter"/> for the rules). When set, each method fetches
    /// <see cref="EpisodeAccess.CandidateMultiplier"/> times more candidates, communities are not returned, and
    /// <see cref="SearchResults.NodeEpisodeUuids"/> is filled. <see langword="null"/> behaves exactly as
    /// upstream.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results per scope, best first.</returns>
    /// <exception cref="GroupIdValidationException">A group id contains invalid characters.</exception>
    /// <exception cref="SearchRerankerException">A node-distance reranker has no centre node, or a cross-encoder
    /// reranker has no cross-encoder.</exception>
    public static async Task<SearchResults> SearchAsync(
        IGraphDriver driver,
        EmbedderClient embedder,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<string>? groupIds,
        SearchConfig config,
        SearchFilters searchFilter,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? bfsOriginNodeUuids = null,
        IReadOnlyList<float>? queryVector = null,
        int? maxCoroutines = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        Validation.ValidateGroupIds(groupIds);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(searchFilter);

        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResults();
        }

        IReadOnlyList<float> searchVector;
        if (NeedsQueryVector(config))
        {
            searchVector = await TraceAsync(
                "search.embed_query_vector",
                [("query.length", query.Length), ("query_vector.provided", queryVector is not null)],
                async phase =>
                {
                    IReadOnlyList<float> vector = queryVector ?? await EmbedQueryAsync(embedder, query, cancellationToken)
                        .ConfigureAwait(false);
                    phase.Add(("query_vector.dimension", vector.Count));
                    return vector;
                }).ConfigureAwait(false);
        }
        else
        {
            searchVector = new float[EmbedderConfig.DefaultEmbeddingDim];
        }

        // An empty list (or [""]) means every group, as upstream.
        var effectiveGroupIds = groupIds is { Count: > 0 } && !(groupIds.Count == 1 && groupIds[0].Length == 0)
            ? groupIds
            : null;

        return await TraceAsync(
            "search.execute_scopes",
            [
                ("group_id.count", effectiveGroupIds?.Count ?? 0),
                ("scope.edges", config.EdgeConfig is not null),
                ("scope.nodes", config.NodeConfig is not null),
                ("scope.episodes", config.EpisodeConfig is not null),
                ("scope.communities", config.CommunityConfig is not null),
                ("limit", config.Limit),
            ],
            async phase =>
            {
                (IReadOnlyList<EntityEdge>, IReadOnlyList<double>) edges = ([], []);
                (IReadOnlyList<EntityNode>, IReadOnlyList<double>, IReadOnlyDictionary<string, IReadOnlyList<string>>?)
                    nodes = ([], [], null);
                (IReadOnlyList<EpisodicNode>, IReadOnlyList<double>) episodes = ([], []);
                (IReadOnlyList<CommunityNode>, IReadOnlyList<double>) communities = ([], []);

                await Concurrency.SemaphoreGatherAsync(
                    [
                        async () => edges = await EdgeSearchAsync(
                            driver,
                            crossEncoder,
                            query,
                            searchVector,
                            effectiveGroupIds,
                            config.EdgeConfig,
                            searchFilter,
                            centerNodeUuid,
                            bfsOriginNodeUuids,
                            config.Limit,
                            config.RerankerMinScore,
                            maxCoroutines,
                            episodeAccessFilter,
                            cancellationToken).ConfigureAwait(false),
                        async () => nodes = await NodeSearchCoreAsync(
                            driver,
                            crossEncoder,
                            query,
                            searchVector,
                            effectiveGroupIds,
                            config.NodeConfig,
                            searchFilter,
                            centerNodeUuid,
                            bfsOriginNodeUuids,
                            config.Limit,
                            config.RerankerMinScore,
                            maxCoroutines,
                            episodeAccessFilter,
                            cancellationToken).ConfigureAwait(false),
                        async () => episodes = await EpisodeSearchAsync(
                            driver,
                            crossEncoder,
                            query,
                            searchVector,
                            effectiveGroupIds,
                            config.EpisodeConfig,
                            searchFilter,
                            config.Limit,
                            config.RerankerMinScore,
                            maxCoroutines,
                            episodeAccessFilter,
                            cancellationToken).ConfigureAwait(false),
                        async () => communities = await CommunitySearchAsync(
                            driver,
                            crossEncoder,
                            query,
                            searchVector,
                            effectiveGroupIds,
                            config.CommunityConfig,
                            config.Limit,
                            config.RerankerMinScore,
                            maxCoroutines,
                            episodeAccessFilter,
                            cancellationToken).ConfigureAwait(false),
                    ],
                    maxCoroutines,
                    cancellationToken).ConfigureAwait(false);

                phase.Add(
                    ("result.edges", edges.Item1.Count),
                    ("result.nodes", nodes.Item1.Count),
                    ("result.episodes", episodes.Item1.Count),
                    ("result.communities", communities.Item1.Count));

                return new SearchResults
                {
                    Edges = edges.Item1,
                    EdgeRerankerScores = edges.Item2,
                    Nodes = nodes.Item1,
                    NodeRerankerScores = nodes.Item2,
                    NodeEpisodeUuids = nodes.Item3,
                    Episodes = episodes.Item1,
                    EpisodeRerankerScores = episodes.Item2,
                    Communities = communities.Item1,
                    CommunityRerankerScores = communities.Item2,
                };
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches facts. Ported from upstream's <c>edge_search()</c>. Each method fetches up to <c>2 * limit</c>
    /// candidates; the reranker orders them and the result is cut to <paramref name="limit"/>.
    /// </summary>
    /// <returns>The facts and their reranker scores; empty when <paramref name="config"/> is
    /// <see langword="null"/>.</returns>
    /// <exception cref="SearchRerankerException">Node-distance reranking without a centre node, or cross-encoder
    /// reranking without a cross-encoder.</exception>
    public static async Task<(IReadOnlyList<EntityEdge> Edges, IReadOnlyList<double> Scores)> EdgeSearchAsync(
        IGraphDriver driver,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<float> queryVector,
        IReadOnlyList<string>? groupIds,
        EdgeSearchConfig? config,
        SearchFilters searchFilter,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? bfsOriginNodeUuids = null,
        int limit = SearchDefaults.DefaultSearchLimit,
        double rerankerMinScore = 0,
        int? maxCoroutines = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (config is null)
        {
            return ([], []);
        }

        ArgumentNullException.ThrowIfNull(driver);

        return await TraceAsync(
            "search.edge_search",
            [
                ("limit", limit),
                ("reranker", SearchEnumValues.Value(config.Reranker)),
                ("search_methods", config.SearchMethods.Select(SearchEnumValues.Value).ToArray()),
                ("bfs_origin_count", bfsOriginNodeUuids?.Count ?? 0),
                ("center_node_uuid.provided", centerNodeUuid is not null),
            ],
            async span =>
            {
                var candidateLimit = CandidateLimit(limit, episodeAccessFilter);
                var searchTasks = new List<Func<Task<IReadOnlyList<EntityEdge>>>>();
                if (config.SearchMethods.Contains(EdgeSearchMethod.Bm25))
                {
                    searchTasks.Add(() => SearchUtils.EdgeFulltextSearchAsync(
                        driver, query, searchFilter, groupIds, candidateLimit, cancellationToken));
                }

                if (config.SearchMethods.Contains(EdgeSearchMethod.CosineSimilarity))
                {
                    searchTasks.Add(() => driver.SearchOps.EdgeSimilaritySearchAsync(
                        queryVector,
                        null,
                        null,
                        searchFilter,
                        groupIds,
                        candidateLimit,
                        config.SimMinScore,
                        cancellationToken));
                }

                if (config.SearchMethods.Contains(EdgeSearchMethod.Bfs))
                {
                    searchTasks.Add(() => SearchUtils.EdgeBfsSearchAsync(
                        driver,
                        bfsOriginNodeUuids,
                        config.BfsMaxDepth,
                        searchFilter,
                        groupIds,
                        candidateLimit,
                        cancellationToken));
                }

                var searchResults = await ExecuteMethodsAsync(
                    "search.edge_search.execute_methods", searchTasks, candidateLimit, maxCoroutines, cancellationToken)
                    .ConfigureAwait(false);

                if (config.SearchMethods.Contains(EdgeSearchMethod.Bfs) && bfsOriginNodeUuids is null)
                {
                    var sourceNodeUuids = searchResults.SelectMany(r => r).Select(e => e.SourceNodeUuid).ToList();
                    searchResults.Add(await TraceAsync(
                        "search.edge_search.expand_bfs",
                        [("origin_node_count", sourceNodeUuids.Count), ("candidate_limit", candidateLimit)],
                        _ => SearchUtils.EdgeBfsSearchAsync(
                            driver,
                            sourceNodeUuids,
                            config.BfsMaxDepth,
                            searchFilter,
                            groupIds,
                            candidateLimit,
                            cancellationToken)).ConfigureAwait(false));
                }

                if (episodeAccessFilter is not null)
                {
                    var visible = await TraceAsync(
                        "search.edge_search.access_filter",
                        [("candidate_count", searchResults.Sum(r => r.Count))],
                        _ => EpisodeAccess.VisibleEdgeUuidsAsync(
                            episodeAccessFilter, searchResults.SelectMany(r => r), cancellationToken))
                        .ConfigureAwait(false);
                    searchResults = KeepVisible(searchResults, edge => visible.Contains(edge.Uuid));
                }

                var edgeUuidMap = SearchUtils.UuidMap(searchResults, edge => edge.Uuid);
                var resultUuids = UuidLists(searchResults, edge => edge.Uuid);

                var (rerankedUuids, edgeScores) = await TraceAsync(
                    "search.edge_search.rerank",
                    [
                        ("candidate_count", edgeUuidMap.Count),
                        ("result_set_count", searchResults.Count),
                        ("reranker", SearchEnumValues.Value(config.Reranker)),
                    ],
                    _ => RerankEdgesAsync(
                        driver,
                        crossEncoder,
                        query,
                        queryVector,
                        config,
                        centerNodeUuid,
                        limit,
                        rerankerMinScore,
                        resultUuids,
                        edgeUuidMap,
                        cancellationToken)).ConfigureAwait(false);

                IReadOnlyList<EntityEdge> rerankedEdges = [.. rerankedUuids.Select(uuid => edgeUuidMap[uuid])];

                if (config.Reranker == EdgeReranker.EpisodeMentions)
                {
                    // Stable descending sort, as Python's sort(reverse=True).
                    rerankedEdges = [.. rerankedEdges.OrderByDescending(edge => edge.Episodes.Count)];
                }

                span.Add(
                    ("candidate_count", edgeUuidMap.Count),
                    ("reranked_count", rerankedEdges.Count),
                    ("returned_count", Math.Min(rerankedEdges.Count, limit)));

                return ((IReadOnlyList<EntityEdge>)[.. rerankedEdges.Take(limit)],
                    (IReadOnlyList<double>)[.. edgeScores.Take(limit)]);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches entities. Ported from upstream's <c>node_search()</c>. Each method fetches up to <c>2 * limit</c>
    /// candidates; the reranker orders them and the result is cut to <paramref name="limit"/>.
    /// </summary>
    /// <returns>The entities and their reranker scores; empty when <paramref name="config"/> is
    /// <see langword="null"/>.</returns>
    /// <exception cref="SearchRerankerException">Node-distance reranking without a centre node, or cross-encoder
    /// reranking without a cross-encoder.</exception>
    public static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyList<double> Scores)> NodeSearchAsync(
        IGraphDriver driver,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<float> queryVector,
        IReadOnlyList<string>? groupIds,
        NodeSearchConfig? config,
        SearchFilters searchFilter,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? bfsOriginNodeUuids = null,
        int limit = SearchDefaults.DefaultSearchLimit,
        double rerankerMinScore = 0,
        int? maxCoroutines = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        var (nodes, scores, _) = await NodeSearchCoreAsync(
            driver,
            crossEncoder,
            query,
            queryVector,
            groupIds,
            config,
            searchFilter,
            centerNodeUuid,
            bfsOriginNodeUuids,
            limit,
            rerankerMinScore,
            maxCoroutines,
            episodeAccessFilter,
            cancellationToken).ConfigureAwait(false);
        return (nodes, scores);
    }

    /// <summary>Node search that also returns, when a filter is set, the readable episodes behind each returned
    /// entity.</summary>
    private static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyList<double> Scores,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? EpisodeUuids)> NodeSearchCoreAsync(
        IGraphDriver driver,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<float> queryVector,
        IReadOnlyList<string>? groupIds,
        NodeSearchConfig? config,
        SearchFilters searchFilter,
        string? centerNodeUuid,
        IReadOnlyList<string>? bfsOriginNodeUuids,
        int limit,
        double rerankerMinScore,
        int? maxCoroutines,
        IEpisodeAccessFilter? episodeAccessFilter,
        CancellationToken cancellationToken)
    {
        if (config is null)
        {
            return ([], [], episodeAccessFilter is null ? null : new Dictionary<string, IReadOnlyList<string>>());
        }

        ArgumentNullException.ThrowIfNull(driver);

        return await TraceAsync(
            "search.node_search",
            [
                ("limit", limit),
                ("reranker", SearchEnumValues.Value(config.Reranker)),
                ("search_methods", config.SearchMethods.Select(SearchEnumValues.Value).ToArray()),
                ("bfs_origin_count", bfsOriginNodeUuids?.Count ?? 0),
                ("center_node_uuid.provided", centerNodeUuid is not null),
            ],
            async span =>
            {
                var candidateLimit = CandidateLimit(limit, episodeAccessFilter);
                var searchTasks = new List<Func<Task<IReadOnlyList<EntityNode>>>>();
                if (config.SearchMethods.Contains(NodeSearchMethod.Bm25))
                {
                    searchTasks.Add(() => SearchUtils.NodeFulltextSearchAsync(
                        driver, query, searchFilter, groupIds, candidateLimit, cancellationToken));
                }

                if (config.SearchMethods.Contains(NodeSearchMethod.CosineSimilarity))
                {
                    searchTasks.Add(() => driver.SearchOps.NodeSimilaritySearchAsync(
                        queryVector, searchFilter, groupIds, candidateLimit, config.SimMinScore, cancellationToken));
                }

                if (config.SearchMethods.Contains(NodeSearchMethod.Bfs))
                {
                    searchTasks.Add(() => SearchUtils.NodeBfsSearchAsync(
                        driver,
                        bfsOriginNodeUuids,
                        searchFilter,
                        config.BfsMaxDepth,
                        groupIds,
                        candidateLimit,
                        cancellationToken));
                }

                var searchResults = await ExecuteMethodsAsync(
                    "search.node_search.execute_methods", searchTasks, candidateLimit, maxCoroutines, cancellationToken)
                    .ConfigureAwait(false);

                if (config.SearchMethods.Contains(NodeSearchMethod.Bfs) && bfsOriginNodeUuids is null)
                {
                    var originNodeUuids = searchResults.SelectMany(r => r).Select(n => n.Uuid).ToList();
                    searchResults.Add(await TraceAsync(
                        "search.node_search.expand_bfs",
                        [("origin_node_count", originNodeUuids.Count), ("candidate_limit", candidateLimit)],
                        _ => SearchUtils.NodeBfsSearchAsync(
                            driver,
                            originNodeUuids,
                            searchFilter,
                            config.BfsMaxDepth,
                            groupIds,
                            candidateLimit,
                            cancellationToken)).ConfigureAwait(false));
                }

                Dictionary<string, IReadOnlyList<string>>? visibleNodes = null;
                if (episodeAccessFilter is not null)
                {
                    visibleNodes = await TraceAsync(
                        "search.node_search.access_filter",
                        [("candidate_count", searchResults.Sum(r => r.Count))],
                        _ => EpisodeAccess.VisibleNodesAsync(
                            driver,
                            episodeAccessFilter,
                            searchResults.SelectMany(r => r),
                            maxCoroutines,
                            cancellationToken)).ConfigureAwait(false);
                    var visible = visibleNodes;
                    searchResults = KeepVisible(searchResults, node => visible.ContainsKey(node.Uuid));
                }

                var resultUuids = UuidLists(searchResults, node => node.Uuid);
                var nodeUuidMap = SearchUtils.UuidMap(searchResults, node => node.Uuid);

                var (rerankedUuids, nodeScores) = await TraceAsync(
                    "search.node_search.rerank",
                    [
                        ("candidate_count", nodeUuidMap.Count),
                        ("result_set_count", searchResults.Count),
                        ("reranker", SearchEnumValues.Value(config.Reranker)),
                    ],
                    _ => RerankNodesAsync(
                        driver,
                        crossEncoder,
                        query,
                        queryVector,
                        config,
                        centerNodeUuid,
                        rerankerMinScore,
                        resultUuids,
                        nodeUuidMap,
                        visibleNodes?.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal),
                        cancellationToken)).ConfigureAwait(false);

                IReadOnlyList<EntityNode> rerankedNodes = [.. rerankedUuids.Select(uuid => nodeUuidMap[uuid])];

                span.Add(
                    ("candidate_count", nodeUuidMap.Count),
                    ("reranked_count", rerankedNodes.Count),
                    ("returned_count", Math.Min(rerankedNodes.Count, limit)));

                IReadOnlyList<EntityNode> returned = [.. rerankedNodes.Take(limit)];
                IReadOnlyDictionary<string, IReadOnlyList<string>>? episodeUuids = visibleNodes is null
                    ? null
                    : returned.DistinctBy(node => node.Uuid)
                        .ToDictionary(node => node.Uuid, node => visibleNodes[node.Uuid], StringComparer.Ordinal);

                return (returned, (IReadOnlyList<double>)[.. nodeScores.Take(limit)], episodeUuids);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches episodes. Ported from upstream's <c>episode_search()</c>: full-text search for up to
    /// <c>2 * limit</c> candidates, then RRF or the cross-encoder (over the top <paramref name="limit"/> only, as
    /// upstream).
    /// </summary>
    /// <returns>The episodes and their reranker scores; empty when <paramref name="config"/> is
    /// <see langword="null"/>.</returns>
    /// <exception cref="SearchRerankerException">Cross-encoder reranking without a cross-encoder.</exception>
    public static async Task<(IReadOnlyList<EpisodicNode> Episodes, IReadOnlyList<double> Scores)> EpisodeSearchAsync(
        IGraphDriver driver,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<float> queryVector,
        IReadOnlyList<string>? groupIds,
        EpisodeSearchConfig? config,
        SearchFilters searchFilter,
        int limit = SearchDefaults.DefaultSearchLimit,
        double rerankerMinScore = 0,
        int? maxCoroutines = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (config is null)
        {
            return ([], []);
        }

        ArgumentNullException.ThrowIfNull(driver);

        return await TraceAsync(
            "search.episode_search",
            [
                ("limit", limit),
                ("reranker", SearchEnumValues.Value(config.Reranker)),
                ("search_methods", config.SearchMethods.Select(SearchEnumValues.Value).ToArray()),
            ],
            async span =>
            {
                var candidateLimit = CandidateLimit(limit, episodeAccessFilter);
                var searchResults = await TraceAsync(
                    "search.episode_search.execute_methods",
                    [("candidate_limit", candidateLimit)],
                    async _ => (await Concurrency.SemaphoreGatherAsync<IReadOnlyList<EpisodicNode>>(
                            [
                                () => SearchUtils.EpisodeFulltextSearchAsync(
                                    driver, query, searchFilter, groupIds, candidateLimit, cancellationToken),
                            ],
                            maxCoroutines,
                            cancellationToken).ConfigureAwait(false)).ToList()).ConfigureAwait(false);

                if (episodeAccessFilter is not null)
                {
                    var readable = await TraceAsync(
                        "search.episode_search.access_filter",
                        [("candidate_count", searchResults.Sum(r => r.Count))],
                        _ => EpisodeAccess.FilterEpisodesAsync(
                            episodeAccessFilter, [.. searchResults.SelectMany(r => r)], cancellationToken))
                        .ConfigureAwait(false);
                    var readableUuids = readable.Select(e => e.Uuid).ToHashSet(StringComparer.Ordinal);
                    searchResults = KeepVisible(searchResults, episode => readableUuids.Contains(episode.Uuid));
                }

                var resultUuids = UuidLists(searchResults, episode => episode.Uuid);
                var episodeUuidMap = SearchUtils.UuidMap(searchResults, episode => episode.Uuid);

                var (rerankedUuids, episodeScores) = await TraceAsync(
                    "search.episode_search.rerank",
                    [
                        ("candidate_count", episodeUuidMap.Count),
                        ("result_set_count", searchResults.Count),
                        ("reranker", SearchEnumValues.Value(config.Reranker)),
                    ],
                    async _ =>
                    {
                        switch (config.Reranker)
                        {
                            case EpisodeReranker.Rrf:
                                return SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore);
                            case EpisodeReranker.CrossEncoder:
                                var (rrfUuids, _) = Trace(
                                    "search.episode_search.seed_rrf",
                                    [("result_set_count", searchResults.Count)],
                                    _ => SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore));
                                var rrfResults = rrfUuids.Select(uuid => episodeUuidMap[uuid]).Take(limit);
                                return await CrossEncoderRankAsync(
                                    "search.episode_search.cross_encoder_rank",
                                    crossEncoder,
                                    query,
                                    rrfResults.Select(episode => (episode.Content, episode.Uuid)),
                                    rerankerMinScore,
                                    cancellationToken).ConfigureAwait(false);
                            default:
                                return (Array.Empty<string>(), Array.Empty<double>());
                        }
                    }).ConfigureAwait(false);

                IReadOnlyList<EpisodicNode> rerankedEpisodes = [.. rerankedUuids.Select(uuid => episodeUuidMap[uuid])];
                span.Add(
                    ("candidate_count", episodeUuidMap.Count),
                    ("reranked_count", rerankedEpisodes.Count),
                    ("returned_count", Math.Min(rerankedEpisodes.Count, limit)));

                return ((IReadOnlyList<EpisodicNode>)[.. rerankedEpisodes.Take(limit)],
                    (IReadOnlyList<double>)[.. episodeScores.Take(limit)]);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches communities. Ported from upstream's <c>community_search()</c>: as upstream, full-text and vector
    /// search always both run (whatever <see cref="CommunitySearchConfig.SearchMethods"/> holds), each for up to
    /// <c>2 * limit</c> candidates.
    /// </summary>
    /// <returns>The communities and their reranker scores; empty when <paramref name="config"/> is <see langword="null"/>
    /// or an <paramref name="episodeAccessFilter"/> is set (communities aggregate entities the caller may not see).
    /// </returns>
    /// <exception cref="SearchRerankerException">Cross-encoder reranking without a cross-encoder.</exception>
    public static async Task<(IReadOnlyList<CommunityNode> Communities, IReadOnlyList<double> Scores)>
        CommunitySearchAsync(
            IGraphDriver driver,
            CrossEncoderClient? crossEncoder,
            string query,
            IReadOnlyList<float> queryVector,
            IReadOnlyList<string>? groupIds,
            CommunitySearchConfig? config,
            int limit = SearchDefaults.DefaultSearchLimit,
            double rerankerMinScore = 0,
            int? maxCoroutines = null,
            IEpisodeAccessFilter? episodeAccessFilter = null,
            CancellationToken cancellationToken = default)
    {
        if (config is null || episodeAccessFilter is not null)
        {
            return ([], []);
        }

        ArgumentNullException.ThrowIfNull(driver);

        return await TraceAsync(
            "search.community_search",
            [
                ("limit", limit),
                ("reranker", SearchEnumValues.Value(config.Reranker)),
                ("search_methods", config.SearchMethods.Select(SearchEnumValues.Value).ToArray()),
            ],
            async span =>
            {
                var candidateLimit = 2 * limit;
                var searchResults = await TraceAsync(
                    "search.community_search.execute_methods",
                    [("candidate_limit", candidateLimit)],
                    async _ => (await Concurrency.SemaphoreGatherAsync<IReadOnlyList<CommunityNode>>(
                            [
                                () => SearchUtils.CommunityFulltextSearchAsync(
                                    driver, query, groupIds, candidateLimit, cancellationToken),
                                () => driver.SearchOps.CommunitySimilaritySearchAsync(
                                    queryVector, groupIds, candidateLimit, config.SimMinScore, cancellationToken),
                            ],
                            maxCoroutines,
                            cancellationToken).ConfigureAwait(false)).ToList()).ConfigureAwait(false);

                var resultUuids = UuidLists(searchResults, community => community.Uuid);
                var communityUuidMap = SearchUtils.UuidMap(searchResults, community => community.Uuid);

                var (rerankedUuids, communityScores) = await TraceAsync(
                    "search.community_search.rerank",
                    [
                        ("candidate_count", communityUuidMap.Count),
                        ("result_set_count", searchResults.Count),
                        ("reranker", SearchEnumValues.Value(config.Reranker)),
                    ],
                    async _ =>
                    {
                        switch (config.Reranker)
                        {
                            case CommunityReranker.Rrf:
                                return SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore);
                            case CommunityReranker.Mmr:
                                var vectors = await TraceAsync(
                                    "search.community_search.load_embeddings",
                                    [("candidate_count", communityUuidMap.Count)],
                                    _ => SearchUtils.GetEmbeddingsForCommunitiesAsync(
                                        driver, [.. communityUuidMap.Values], cancellationToken)).ConfigureAwait(false);
                                return Trace(
                                    "search.community_search.compute_mmr",
                                    [("candidate_count", vectors.Count)],
                                    _ => SearchUtils.MaximalMarginalRelevance(
                                        queryVector, vectors, config.MmrLambda, rerankerMinScore));
                            case CommunityReranker.CrossEncoder:
                                return await CrossEncoderRankAsync(
                                    "search.community_search.cross_encoder_rank",
                                    crossEncoder,
                                    query,
                                    searchResults.SelectMany(r => r).Select(c => (c.Name, c.Uuid)),
                                    rerankerMinScore,
                                    cancellationToken).ConfigureAwait(false);
                            default:
                                return (Array.Empty<string>(), Array.Empty<double>());
                        }
                    }).ConfigureAwait(false);

                IReadOnlyList<CommunityNode> rerankedCommunities =
                    [.. rerankedUuids.Select(uuid => communityUuidMap[uuid])];
                span.Add(
                    ("candidate_count", communityUuidMap.Count),
                    ("reranked_count", rerankedCommunities.Count),
                    ("returned_count", Math.Min(rerankedCommunities.Count, limit)));

                return ((IReadOnlyList<CommunityNode>)[.. rerankedCommunities.Take(limit)],
                    (IReadOnlyList<double>)[.. communityScores.Take(limit)]);
            }).ConfigureAwait(false);
    }

    private static async Task<(IReadOnlyList<string>, IReadOnlyList<double>)> RerankEdgesAsync(
        IGraphDriver driver,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<float> queryVector,
        EdgeSearchConfig config,
        string? centerNodeUuid,
        int limit,
        double rerankerMinScore,
        List<IReadOnlyList<string>> resultUuids,
        Dictionary<string, EntityEdge> edgeUuidMap,
        CancellationToken cancellationToken)
    {
        switch (config.Reranker)
        {
            case EdgeReranker.Rrf:
            case EdgeReranker.EpisodeMentions:
                return SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore);

            case EdgeReranker.Mmr:
                {
                    var vectors = await TraceAsync(
                        "search.edge_search.load_embeddings",
                        [("candidate_count", edgeUuidMap.Count)],
                        _ => SearchUtils.GetEmbeddingsForEdgesAsync(driver, [.. edgeUuidMap.Values], cancellationToken))
                        .ConfigureAwait(false);
                    return Trace(
                        "search.edge_search.compute_mmr",
                        [("candidate_count", vectors.Count)],
                        _ => SearchUtils.MaximalMarginalRelevance(
                            queryVector, vectors, config.MmrLambda, rerankerMinScore));
                }

            case EdgeReranker.CrossEncoder:
                {
                    // RRF shortlist of 2 * limit candidates, so every method's top hits reach the cross-encoder.
                    var (rrfUuids, _) = SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore);
                    var rrfEdges = rrfUuids.Select(uuid => edgeUuidMap[uuid]).Take(2 * limit);
                    return await CrossEncoderRankAsync(
                        "search.edge_search.cross_encoder_rank",
                        crossEncoder,
                        query,
                        rrfEdges.Select(edge => (edge.Fact, edge.Uuid)),
                        rerankerMinScore,
                        cancellationToken).ConfigureAwait(false);
                }

            case EdgeReranker.NodeDistance:
                {
                    if (centerNodeUuid is null)
                    {
                        throw new SearchRerankerException("No center node provided for Node Distance reranker");
                    }

                    var (sortedUuids, _) = Trace(
                        "search.edge_search.seed_rrf",
                        [("result_set_count", resultUuids.Count)],
                        _ => SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore));

                    var sourceToEdgeUuids = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    var sourceUuids = new List<string>();
                    foreach (var edge in sortedUuids.Select(uuid => edgeUuidMap[uuid]))
                    {
                        if (!sourceToEdgeUuids.TryGetValue(edge.SourceNodeUuid, out var list))
                        {
                            list = [];
                            sourceToEdgeUuids[edge.SourceNodeUuid] = list;
                            sourceUuids.Add(edge.SourceNodeUuid);
                        }

                        list.Add(edge.Uuid);
                    }

                    var (rerankedNodeUuids, scores) = await TraceAsync(
                        "search.edge_search.node_distance_rank",
                        [("source_node_count", sourceUuids.Count), ("center_node_uuid.provided", true)],
                        _ => SearchUtils.NodeDistanceRerankerAsync(
                            driver, sourceUuids, centerNodeUuid, rerankerMinScore, cancellationToken))
                        .ConfigureAwait(false);

                    var rerankedUuids = new List<string>();
                    foreach (var nodeUuid in rerankedNodeUuids)
                    {
                        // Upstream's defaultdict yields an empty list for the centre node when it is not a source.
                        if (sourceToEdgeUuids.TryGetValue(nodeUuid, out var edgeUuids))
                        {
                            rerankedUuids.AddRange(edgeUuids);
                        }
                    }

                    // As upstream, the scores are per source node, not per edge.
                    return (rerankedUuids, scores);
                }

            default:
                return ([], []);
        }
    }

    private static async Task<(IReadOnlyList<string>, IReadOnlyList<double>)> RerankNodesAsync(
        IGraphDriver driver,
        CrossEncoderClient? crossEncoder,
        string query,
        IReadOnlyList<float> queryVector,
        NodeSearchConfig config,
        string? centerNodeUuid,
        double rerankerMinScore,
        List<IReadOnlyList<string>> resultUuids,
        Dictionary<string, EntityNode> nodeUuidMap,
        IReadOnlyDictionary<string, int>? mentionCounts,
        CancellationToken cancellationToken)
    {
        switch (config.Reranker)
        {
            case NodeReranker.Rrf:
                return SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore);

            case NodeReranker.Mmr:
                {
                    var vectors = await TraceAsync(
                        "search.node_search.load_embeddings",
                        [("candidate_count", nodeUuidMap.Count)],
                        _ => SearchUtils.GetEmbeddingsForNodesAsync(driver, [.. nodeUuidMap.Values], cancellationToken))
                        .ConfigureAwait(false);
                    return Trace(
                        "search.node_search.compute_mmr",
                        [("candidate_count", vectors.Count)],
                        _ => SearchUtils.MaximalMarginalRelevance(
                            queryVector, vectors, config.MmrLambda, rerankerMinScore));
                }

            case NodeReranker.CrossEncoder:
                return await CrossEncoderRankAsync(
                    "search.node_search.cross_encoder_rank",
                    crossEncoder,
                    query,
                    nodeUuidMap.Values.Select(node => (node.Name, node.Uuid)),
                    rerankerMinScore,
                    cancellationToken).ConfigureAwait(false);

            case NodeReranker.EpisodeMentions:
                return await TraceAsync(
                    "search.node_search.episode_mentions_rank",
                    [("candidate_count", nodeUuidMap.Count)],
                    _ => SearchUtils.EpisodeMentionsRerankerAsync(
                        driver, resultUuids, rerankerMinScore, mentionCounts, cancellationToken)).ConfigureAwait(false);

            case NodeReranker.NodeDistance:
                {
                    if (centerNodeUuid is null)
                    {
                        throw new SearchRerankerException("No center node provided for Node Distance reranker");
                    }

                    var (seededUuids, _) = Trace(
                        "search.node_search.seed_rrf",
                        [("result_set_count", resultUuids.Count)],
                        _ => SearchUtils.Rrf(resultUuids, minScore: rerankerMinScore));

                    return await TraceAsync(
                        "search.node_search.node_distance_rank",
                        [("source_node_count", seededUuids.Count), ("center_node_uuid.provided", true)],
                        _ => SearchUtils.NodeDistanceRerankerAsync(
                            driver, seededUuids, centerNodeUuid, rerankerMinScore, cancellationToken))
                        .ConfigureAwait(false);
                }

            default:
                return ([], []);
        }
    }

    /// <summary>
    /// Upstream's cross-encoder step: <c>{passage: uuid}</c> (a repeated passage keeps its first position and its
    /// last id), rank the passages, keep those scoring at least the minimum.
    /// </summary>
    private static async Task<(IReadOnlyList<string>, IReadOnlyList<double>)> CrossEncoderRankAsync(
        string spanName,
        CrossEncoderClient? crossEncoder,
        string query,
        IEnumerable<(string Passage, string Uuid)> candidates,
        double rerankerMinScore,
        CancellationToken cancellationToken)
    {
        if (crossEncoder is null)
        {
            throw new SearchRerankerException("No cross encoder provided for Cross Encoder reranker");
        }

        var passageToUuid = new Dictionary<string, string>(StringComparer.Ordinal);
        var passages = new List<string>();
        foreach (var (passage, uuid) in candidates)
        {
            if (!passageToUuid.ContainsKey(passage))
            {
                passages.Add(passage);
            }

            passageToUuid[passage] = uuid;
        }

        var ranked = await TraceAsync(
            spanName,
            [("candidate_count", passages.Count)],
            _ => crossEncoder.RankAsync(query, passages, cancellationToken)).ConfigureAwait(false);

        var kept = ranked.Where(item => item.Score >= rerankerMinScore).ToList();
        return ([.. kept.Select(item => passageToUuid[item.Passage])], [.. kept.Select(item => item.Score)]);
    }

    private static async Task<List<IReadOnlyList<T>>> ExecuteMethodsAsync<T>(
        string spanName,
        List<Func<Task<IReadOnlyList<T>>>> searchTasks,
        int candidateLimit,
        int? maxCoroutines,
        CancellationToken cancellationToken)
    {
        if (searchTasks.Count == 0)
        {
            return [];
        }

        return await TraceAsync(
            spanName,
            [("method_count", searchTasks.Count), ("candidate_limit", candidateLimit)],
            async phase =>
            {
                var results = (await Concurrency.SemaphoreGatherAsync(searchTasks, maxCoroutines, cancellationToken)
                    .ConfigureAwait(false)).ToList();
                phase.Add(
                    ("result_set_count", results.Count),
                    ("non_empty_result_sets", results.Count(result => result.Count > 0)));
                return results;
            }).ConfigureAwait(false);
    }

    private static int CandidateLimit(int limit, IEpisodeAccessFilter? episodeAccessFilter) =>
        episodeAccessFilter is null ? 2 * limit : 2 * limit * EpisodeAccess.CandidateMultiplier;

    // Drops hidden candidates from each ranking; the rankings close up, so rerankers see only the visible set.
    private static List<IReadOnlyList<T>> KeepVisible<T>(List<IReadOnlyList<T>> results, Func<T, bool> isVisible) =>
        [.. results.Select(result => (IReadOnlyList<T>)[.. result.Where(isVisible)])];

    private static List<IReadOnlyList<string>> UuidLists<T>(List<IReadOnlyList<T>> results, Func<T, string> uuid) =>
        [.. results.Select(result => (IReadOnlyList<string>)[.. result.Select(uuid)])];

    private static bool NeedsQueryVector(SearchConfig config) =>
        (config.EdgeConfig is { } edge
            && (edge.SearchMethods.Contains(EdgeSearchMethod.CosineSimilarity) || edge.Reranker == EdgeReranker.Mmr))
        || (config.NodeConfig is { } node
            && (node.SearchMethods.Contains(NodeSearchMethod.CosineSimilarity) || node.Reranker == NodeReranker.Mmr))
        || (config.CommunityConfig is { } community
            && (community.SearchMethods.Contains(CommunitySearchMethod.CosineSimilarity)
                || community.Reranker == CommunityReranker.Mmr));

    private static async Task<IReadOnlyList<float>> EmbedQueryAsync(
        EmbedderClient embedder,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        return await embedder.CreateAsync(query.Replace('\n', ' '), cancellationToken).ConfigureAwait(false);
    }
}
