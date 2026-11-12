// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search_utils.py and graphiti_core/helpers.py (normalize_l2) (Graphiti v0.31.2)
// and changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Search;

/// <summary>
/// The driver-independent parts of upstream's <c>search_utils.py</c>: fusion, rerankers, embedding loading and the
/// guards upstream applies before handing a search to the database.
/// </summary>
/// <remarks>
/// Upstream's query building (Cypher, Lucene) belongs to the drivers and is not here. The search wrappers below
/// (<see cref="EdgeFulltextSearchAsync"/> and friends) add the checks upstream's generic path makes in core: full-text
/// queries that are too long return nothing, and breadth-first searches without origins return nothing.
/// </remarks>
public static class SearchUtils
{
    /// <summary>Upstream <c>MAX_QUERY_LENGTH</c>: full-text queries with this many words (plus group ids) or more
    /// return nothing.</summary>
    public const int MaxQueryLength = 128;

    /// <summary>
    /// Cosine similarity of two vectors, or 0 when either has zero length (norm). Computed in double precision, as
    /// NumPy does on Python floats.
    /// </summary>
    /// <exception cref="ArgumentException">The vectors differ in length.</exception>
    public static double CalculateCosineSimilarity(IReadOnlyList<float> vector1, IReadOnlyList<float> vector2)
    {
        ArgumentNullException.ThrowIfNull(vector1);
        ArgumentNullException.ThrowIfNull(vector2);
        if (vector1.Count != vector2.Count)
        {
            throw new ArgumentException("Vectors must have the same length.", nameof(vector2));
        }

        double dot = 0, norm1 = 0, norm2 = 0;
        for (var i = 0; i < vector1.Count; i++)
        {
            double a = vector1[i], b = vector2[i];
            dot += a * b;
            norm1 += a * a;
            norm2 += b * b;
        }

        norm1 = Math.Sqrt(norm1);
        norm2 = Math.Sqrt(norm2);
        if (norm1 == 0 || norm2 == 0)
        {
            return 0;
        }

        return dot / (norm1 * norm2);
    }

    /// <summary>
    /// Whether a full-text query is too long to run, by upstream's rule for its default (Lucene) backend: the number
    /// of space-separated words plus the number of group ids reaches <see cref="MaxQueryLength"/>.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="groupIds">The group ids the search is restricted to, or <see langword="null"/>.</param>
    public static bool IsFulltextQueryTooLong(string query, IReadOnlyList<string>? groupIds)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Lucene escaping adds backslashes but never spaces, so the word count of the escaped query upstream measures
        // equals the raw query's.
        var words = query.Split(' ').Length;
        return words + (groupIds?.Count ?? 0) >= MaxQueryLength;
    }

    /// <summary>
    /// Reciprocal rank fusion: each list adds <c>1 / (rank + rankConst)</c> (rank from 0) to each id's score. Ids are
    /// returned best first; ties keep the order in which the ids were first seen.
    /// </summary>
    /// <param name="results">Rankings of ids, each best first.</param>
    /// <param name="rankConst">The rank constant; upstream uses 1.</param>
    /// <param name="minScore">Ids scoring below this are dropped.</param>
    /// <returns>The fused ids and their scores.</returns>
    public static (IReadOnlyList<string> Uuids, IReadOnlyList<double> Scores) Rrf(
        IEnumerable<IReadOnlyList<string>> results,
        int rankConst = 1,
        double minScore = 0)
    {
        ArgumentNullException.ThrowIfNull(results);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var result in results)
        {
            for (var i = 0; i < result.Count; i++)
            {
                var uuid = result[i];
                if (!scores.TryGetValue(uuid, out var score))
                {
                    order.Add(uuid);
                    score = 0;
                }

                scores[uuid] = score + (1.0 / (i + rankConst));
            }
        }

        // OrderByDescending is stable, as Python's sort(reverse=True).
        var kept = order.OrderByDescending(uuid => scores[uuid]).Where(uuid => scores[uuid] >= minScore).ToList();
        return (kept, kept.Select(uuid => scores[uuid]).ToList());
    }

    /// <summary>
    /// Maximal marginal relevance: <c>lambda * sim(query, c) + (lambda - 1) * max sim(c, other)</c>, where candidate
    /// embeddings are L2-normalised and the query vector is used as given. Best first.
    /// </summary>
    /// <param name="queryVector">The query embedding.</param>
    /// <param name="candidates">Candidate ids and embeddings, in order. A repeated id keeps its first position and its
    /// last embedding, as a Python dict.</param>
    /// <param name="mmrLambda">Relevance-versus-diversity weight.</param>
    /// <param name="minScore">Candidates scoring below this are dropped. Upstream's default is -2.</param>
    /// <returns>The ids and their scores.</returns>
    public static (IReadOnlyList<string> Uuids, IReadOnlyList<double> Scores) MaximalMarginalRelevance(
        IReadOnlyList<float> queryVector,
        IEnumerable<KeyValuePair<string, float[]>> candidates,
        double mmrLambda = SearchDefaults.DefaultMmrLambda,
        double minScore = -2.0)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        ArgumentNullException.ThrowIfNull(candidates);

        var query = queryVector.Select(value => (double)value).ToArray();
        var candidateArrays = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var uuids = new List<string>();
        foreach (var (uuid, embedding) in candidates)
        {
            if (!candidateArrays.ContainsKey(uuid))
            {
                uuids.Add(uuid);
            }

            candidateArrays[uuid] = NormalizeL2(embedding);
        }

        var n = uuids.Count;
        var similarity = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var value = Dot(candidateArrays[uuids[i]], candidateArrays[uuids[j]]);
                similarity[i, j] = value;
                similarity[j, i] = value;
            }
        }

        var mmrScores = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            // The row includes the zero diagonal, so max_sim is never below 0 (as upstream's np.max over the row).
            var maxSim = double.NegativeInfinity;
            for (var j = 0; j < n; j++)
            {
                maxSim = Math.Max(maxSim, similarity[i, j]);
            }

            var uuid = uuids[i];
            mmrScores[uuid] = (mmrLambda * Dot(query, candidateArrays[uuid])) + ((mmrLambda - 1) * maxSim);
        }

        var kept = uuids.OrderByDescending(uuid => mmrScores[uuid])
            .Where(uuid => mmrScores[uuid] >= minScore)
            .ToList();
        return (kept, kept.Select(uuid => mmrScores[uuid]).ToList());
    }

    /// <summary>
    /// Reranks entities by graph distance to a centre node: neighbours of the centre (joined by a fact) first, scoring
    /// 1; other nodes after them, scoring 0 (upstream's <c>1 / inf</c>); the centre node itself, if among the
    /// candidates, comes first with score 10 (<c>1 / 0.1</c>). Order within each group is kept.
    /// </summary>
    /// <param name="driver">The graph driver.</param>
    /// <param name="nodeUuids">The candidate entity ids, in their current order.</param>
    /// <param name="centerNodeUuid">The centre entity.</param>
    /// <param name="minScore">Ids scoring below this are dropped. Non-neighbours (score 0) are kept at 0.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ids and their scores.</returns>
    public static async Task<(IReadOnlyList<string> Uuids, IReadOnlyList<double> Scores)> NodeDistanceRerankerAsync(
        IGraphDriver driver,
        IReadOnlyList<string> nodeUuids,
        string centerNodeUuid,
        double minScore = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(nodeUuids);
        ArgumentNullException.ThrowIfNull(centerNodeUuid);

        var filteredUuids = nodeUuids.Where(uuid => uuid != centerNodeUuid).ToList();

        IReadOnlySet<string> connected = filteredUuids.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : await driver.SearchOps.GetConnectedNodeUuidsAsync(centerNodeUuid, filteredUuids, cancellationToken)
                .ConfigureAwait(false);

        // Upstream scores neighbours 1 and everything else infinity, sorts ascending (stable), then reports 1 / score.
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var uuid in filteredUuids)
        {
            scores[uuid] = connected.Contains(uuid) ? 1.0 : double.PositiveInfinity;
        }

        var ordered = filteredUuids.OrderBy(uuid => scores[uuid]).ToList();

        if (nodeUuids.Contains(centerNodeUuid))
        {
            scores[centerNodeUuid] = 0.1;
            ordered.Insert(0, centerNodeUuid);
        }

        var kept = ordered.Where(uuid => 1 / scores[uuid] >= minScore).ToList();
        return (kept, kept.Select(uuid => 1 / scores[uuid]).ToList());
    }

    /// <summary>
    /// Reranks entities by how many episodes mention them, most first. The rankings are first fused with
    /// <see cref="Rrf"/>, whose order breaks ties. Entities with no mentions score 0.
    /// </summary>
    /// <param name="driver">The graph driver.</param>
    /// <param name="nodeUuids">Rankings of entity ids from the search methods.</param>
    /// <param name="minScore">Ids with fewer mentions than this are dropped.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ids and their mention counts.</returns>
    public static async Task<(IReadOnlyList<string> Uuids, IReadOnlyList<double> Scores)> EpisodeMentionsRerankerAsync(
        IGraphDriver driver,
        IReadOnlyList<IReadOnlyList<string>> nodeUuids,
        double minScore = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(nodeUuids);

        var (sortedUuids, _) = Rrf(nodeUuids);
        var counts = sortedUuids.Count == 0
            ? new Dictionary<string, int>()
            : await driver.GraphOps.CountEpisodeMentionsAsync(sortedUuids, cancellationToken).ConfigureAwait(false);

        var scores = sortedUuids.ToDictionary(
            uuid => uuid,
            uuid => counts.TryGetValue(uuid, out var count) ? (double)count : 0.0,
            StringComparer.Ordinal);

        var kept = sortedUuids.OrderByDescending(uuid => scores[uuid]).Where(uuid => scores[uuid] >= minScore).ToList();
        return (kept, kept.Select(uuid => scores[uuid]).ToList());
    }

    /// <summary>The stored name embeddings of these entities, keyed by id, in the order given; entities without one
    /// are left out. The nodes' <see cref="EntityNode.NameEmbedding"/> is left as it was.</summary>
    public static async Task<IReadOnlyDictionary<string, float[]>> GetEmbeddingsForNodesAsync(
        IGraphDriver driver,
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(nodes);

        var originals = nodes.Select(node => node.NameEmbedding).ToList();
        try
        {
            await driver.EntityNodeOps.LoadEmbeddingsBulkAsync(nodes, cancellationToken).ConfigureAwait(false);
            return Collect(nodes, node => node.Uuid, node => node.NameEmbedding);
        }
        finally
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                nodes[i].NameEmbedding = originals[i];
            }
        }
    }

    /// <summary>The stored fact embeddings of these facts, keyed by id, in the order given; facts without one are
    /// left out. The edges' <see cref="EntityEdge.FactEmbedding"/> is left as it was.</summary>
    public static async Task<IReadOnlyDictionary<string, float[]>> GetEmbeddingsForEdgesAsync(
        IGraphDriver driver,
        IReadOnlyList<EntityEdge> edges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(edges);

        var originals = edges.Select(edge => edge.FactEmbedding).ToList();
        try
        {
            await driver.EntityEdgeOps.LoadEmbeddingsBulkAsync(edges, cancellationToken).ConfigureAwait(false);
            return Collect(edges, edge => edge.Uuid, edge => edge.FactEmbedding);
        }
        finally
        {
            for (var i = 0; i < edges.Count; i++)
            {
                edges[i].FactEmbedding = originals[i];
            }
        }
    }

    /// <summary>The stored name embeddings of these communities, keyed by id, in the order given; communities without
    /// one are left out. The communities' <see cref="CommunityNode.NameEmbedding"/> is left as it was.</summary>
    public static async Task<IReadOnlyDictionary<string, float[]>> GetEmbeddingsForCommunitiesAsync(
        IGraphDriver driver,
        IReadOnlyList<CommunityNode> communities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(communities);

        var originals = communities.Select(community => community.NameEmbedding).ToList();
        try
        {
            foreach (var community in communities)
            {
                await driver.CommunityNodeOps.LoadNameEmbeddingAsync(community, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Collect(communities, community => community.Uuid, community => community.NameEmbedding);
        }
        finally
        {
            for (var i = 0; i < communities.Count; i++)
            {
                communities[i].NameEmbedding = originals[i];
            }
        }
    }

    /// <summary>
    /// The episodes that state these facts: the facts' episode ids, in order, cut to <paramref name="limit"/> ids
    /// before lookup (as upstream). <paramref name="nodes"/> is unused, as upstream.
    /// </summary>
    public static Task<IReadOnlyList<EpisodicNode>> GetEpisodesByMentionsAsync(
        IGraphDriver driver,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EntityEdge> edges,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(edges);

        var episodeUuids = edges.SelectMany(edge => edge.Episodes).Take(limit).ToList();
        return driver.EpisodeNodeOps.GetByUuidsAsync(episodeUuids, cancellationToken);
    }

    /// <summary>The entities mentioned by these episodes, without duplicates.</summary>
    public static Task<IReadOnlyList<EntityNode>> GetMentionedNodesAsync(
        IGraphDriver driver,
        IReadOnlyList<EpisodicNode> episodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.GraphOps.GetMentionedNodesAsync(episodes, cancellationToken);
    }

    /// <summary>The communities with any of these entities as a member, without duplicates.</summary>
    public static Task<IReadOnlyList<CommunityNode>> GetCommunitiesByNodesAsync(
        IGraphDriver driver,
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.GraphOps.GetCommunitiesByNodesAsync(nodes, cancellationToken);
    }

    /// <summary>
    /// Hybrid entity search for several queries at once: one full-text search per query and one vector search per
    /// embedding, each with limit <c>2 * limit</c>, fused with <see cref="Rrf"/> and deduplicated.
    /// </summary>
    /// <param name="queries">Text queries.</param>
    /// <param name="embeddings">Query embeddings; empty for full-text only.</param>
    /// <param name="driver">The graph driver.</param>
    /// <param name="searchFilter">Filters for every search.</param>
    /// <param name="groupIds">Groups to search, or <see langword="null"/> for all.</param>
    /// <param name="limit">Half the per-search limit, as upstream.</param>
    /// <param name="maxCoroutines">The concurrency limit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<IReadOnlyList<EntityNode>> HybridNodeSearchAsync(
        IReadOnlyList<string> queries,
        IReadOnlyList<IReadOnlyList<float>> embeddings,
        IGraphDriver driver,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        int? maxCoroutines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(embeddings);

        var operations = queries
            .Select<string, Func<Task<IReadOnlyList<EntityNode>>>>(query => () =>
                NodeFulltextSearchAsync(driver, query, searchFilter, groupIds, 2 * limit, cancellationToken))
            .Concat(embeddings.Select<IReadOnlyList<float>, Func<Task<IReadOnlyList<EntityNode>>>>(embedding => () =>
                driver.SearchOps.NodeSimilaritySearchAsync(
                    embedding,
                    searchFilter,
                    groupIds,
                    2 * limit,
                    cancellationToken: cancellationToken)));

        var results = await Concurrency.SemaphoreGatherAsync(operations, maxCoroutines, cancellationToken)
            .ConfigureAwait(false);

        var nodeUuidMap = UuidMap(results, node => node.Uuid);
        var (rankedUuids, _) = Rrf(results.Select(result => (IReadOnlyList<string>)[.. result.Select(n => n.Uuid)]));
        return [.. rankedUuids.Select(uuid => nodeUuidMap[uuid])];
    }

    /// <summary>Entity full-text search through the driver; returns nothing when the query is too long
    /// (<see cref="IsFulltextQueryTooLong"/>).</summary>
    public static async Task<IReadOnlyList<EntityNode>> NodeFulltextSearchAsync(
        IGraphDriver driver,
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (IsFulltextQueryTooLong(query, groupIds))
        {
            return [];
        }

        return await driver.SearchOps.NodeFulltextSearchAsync(query, searchFilter, groupIds, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Fact full-text search through the driver; returns nothing when the query is too long
    /// (<see cref="IsFulltextQueryTooLong"/>).</summary>
    public static async Task<IReadOnlyList<EntityEdge>> EdgeFulltextSearchAsync(
        IGraphDriver driver,
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (IsFulltextQueryTooLong(query, groupIds))
        {
            return [];
        }

        return await driver.SearchOps.EdgeFulltextSearchAsync(query, searchFilter, groupIds, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Episode full-text search through the driver; returns nothing when the query is too long
    /// (<see cref="IsFulltextQueryTooLong"/>).</summary>
    public static async Task<IReadOnlyList<EpisodicNode>> EpisodeFulltextSearchAsync(
        IGraphDriver driver,
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (IsFulltextQueryTooLong(query, groupIds))
        {
            return [];
        }

        return await driver.SearchOps
            .EpisodeFulltextSearchAsync(query, searchFilter, groupIds, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Community full-text search through the driver; returns nothing when the query is too long
    /// (<see cref="IsFulltextQueryTooLong"/>).</summary>
    public static async Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(
        IGraphDriver driver,
        string query,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (IsFulltextQueryTooLong(query, groupIds))
        {
            return [];
        }

        return await driver.SearchOps.CommunityFulltextSearchAsync(query, groupIds, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Breadth-first fact search through the driver; returns nothing when there are no origins
    /// (<see langword="null"/> or empty), as upstream.</summary>
    public static async Task<IReadOnlyList<EntityEdge>> EdgeBfsSearchAsync(
        IGraphDriver driver,
        IReadOnlyList<string>? bfsOriginNodeUuids,
        int bfsMaxDepth,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (bfsOriginNodeUuids is null || bfsOriginNodeUuids.Count == 0)
        {
            return [];
        }

        return await driver.SearchOps
            .EdgeBfsSearchAsync(bfsOriginNodeUuids, bfsMaxDepth, searchFilter, groupIds, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Breadth-first entity search through the driver; returns nothing when there are no origins
    /// (<see langword="null"/> or empty), as upstream.</summary>
    public static async Task<IReadOnlyList<EntityNode>> NodeBfsSearchAsync(
        IGraphDriver driver,
        IReadOnlyList<string>? bfsOriginNodeUuids,
        SearchFilters searchFilter,
        int bfsMaxDepth,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (bfsOriginNodeUuids is null || bfsOriginNodeUuids.Count == 0)
        {
            return [];
        }

        return await driver.SearchOps
            .NodeBfsSearchAsync(bfsOriginNodeUuids, searchFilter, bfsMaxDepth, groupIds, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Upstream's <c>{x.uuid: x for result in results for x in result}</c>: the last object per id wins.
    /// </summary>
    internal static Dictionary<string, T> UuidMap<T>(IEnumerable<IReadOnlyList<T>> results, Func<T, string> uuid)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            foreach (var item in result)
            {
                map[uuid(item)] = item;
            }
        }

        return map;
    }

    /// <summary>Upstream's <c>helpers.normalize_l2</c>: divides by the L2 norm unless it is zero.</summary>
    internal static double[] NormalizeL2(IReadOnlyList<float> embedding)
    {
        var array = embedding.Select(value => (double)value).ToArray();
        var norm = Math.Sqrt(array.Sum(value => value * value));
        if (norm == 0)
        {
            return array;
        }

        for (var i = 0; i < array.Length; i++)
        {
            array[i] /= norm;
        }

        return array;
    }

    private static double Dot(double[] a, double[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors must have the same length.");
        }

        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static Dictionary<string, float[]> Collect<T>(
        IEnumerable<T> items,
        Func<T, string> uuid,
        Func<T, float[]?> embedding)
    {
        var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (embedding(item) is { } vector)
            {
                result[uuid(item)] = vector;
            }
        }

        return result;
    }
}
