// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/neo4j/operations/search_ops.py and graphiti_core/search/search_utils.py
// (Graphiti v0.31.2) and changed for .NET: Lucene full-text becomes BM25 over a keyword index table, Cypher vector
// similarity becomes VECTOR_DISTANCE, and variable-length Cypher paths become an iterative breadth-first walk.

using System.Globalization;
using System.Text.Json;
using GraphitiNet.Driver.SqlServer.Internal;
using GraphitiNet.Models;
using GraphitiNet.Search;
using GraphitiNet.Utils;
using Microsoft.Data.SqlClient;

namespace GraphitiNet.Driver.SqlServer.Operations;

/// <summary>Full-text, vector and graph search in T-SQL.</summary>
/// <remarks>
/// <para><b>Full-text:</b> Okapi BM25 the way Lucene (and so upstream's Neo4j indexes) and the in-memory driver rank:
/// the query is split with the same tokenizer as the stored text, a record matches when it holds any query term, and
/// each term scores against each field with that field's own statistics (k1 = 1.2, b = 0.75), summed. The statistics
/// cover every record of the type, as a Lucene index covers every group; only the requested groups are scored. The
/// terms live in the driver's keyword index tables, written with every save.</para>
/// <para><b>Similarity:</b> Neo4j's cosine score, <c>(1 + cosine) / 2</c> =
/// <c>1 - VECTOR_DISTANCE('cosine', embedding, query) / 2</c>, strictly above <c>minScore</c> as upstream, computed
/// exactly over the filtered rows. Zero vectors never match.</para>
/// <para><b>Breadth-first:</b> one batch walks facts from source to target and episode-to-entity links outward, one
/// hop per round, up to <c>maxDepth</c> hops, as upstream's Neo4j queries; each node is visited once per origin, at
/// its fewest hops. Results are distinct and ordered by hop distance, then uuid.</para>
/// </remarks>
internal sealed class SearchOperations(DriverCore core) : ISearchOperations
{
    private const string Bin2 = SqlServerGraphOptions.BinaryCollation;

    private SqlNames N => core.Names;

    // ----- Entities -----

    public Task<IReadOnlyList<EntityNode>> NodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (QueryTerms(query, groupIds, limit) is not { } terms)
        {
            return Empty<EntityNode>();
        }

        var b = new SqlBuilder();
        var hits = Bm25(b, terms, DocKind.Entities, groupIds);
        var conditions = FilterSql.NodeConditions(b, searchFilter, "n");
        AddGroupCondition(b, groupIds, "n", conditions);
        b.Append($"{hits} SELECT TOP ({b.Int(limit)}) {RecordMapper.EntityColumns("n")} FROM hit ")
            .Append($"JOIN {N.Table(SqlNames.Entities)} AS n ON n.uuid = hit.uuid")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append(" ORDER BY ROUND(hit.score, 9) DESC, n.uuid");
        return core.ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
    }

    public Task<IReadOnlyList<EntityNode>> NodeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (!IsComparable(searchVector, limit))
        {
            return Empty<EntityNode>();
        }

        var b = new SqlBuilder();
        var conditions = FilterSql.NodeConditions(b, searchFilter, "n");
        AddGroupCondition(b, groupIds, "n", conditions);
        AppendSimilarity(
            b, RecordMapper.EntityColumns("n"), $"{N.Table(SqlNames.Entities)} AS n", "n.name_embedding", searchVector,
            conditions, limit, minScore);
        return core.ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
    }

    public Task<IReadOnlyList<EntityNode>> NodeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        SearchFilters searchFilter,
        int maxDepth,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originUuids);
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (originUuids.Count == 0 || maxDepth < 1 || limit <= 0 || groupIds is { Count: 0 })
        {
            return Empty<EntityNode>();
        }

        var b = new SqlBuilder();
        AppendBfs(b, originUuids, maxDepth, groupIds);
        var conditions = FilterSql.NodeConditions(b, searchFilter, "n");
        AddGroupCondition(b, groupIds, "n", conditions);
        // As upstream, only entities in the origin's group, 1 to maxDepth hops away (an origin on a cycle counts).
        b.Append($"SELECT TOP ({b.Int(limit)}) {RecordMapper.EntityColumns("n")} FROM (")
            .Append("SELECT w.uuid, MIN(w.depth) AS depth FROM @bfs AS w ")
            .Append($"JOIN {N.Table(SqlNames.Entities)} AS r ON r.uuid = w.uuid AND r.group_id = w.origin_group ")
            .Append("GROUP BY w.uuid) AS hit ")
            .Append($"JOIN {N.Table(SqlNames.Entities)} AS n ON n.uuid = hit.uuid")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append(" ORDER BY hit.depth, n.uuid;");
        return core.ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
    }

    // ----- Facts -----

    public Task<IReadOnlyList<EntityEdge>> EdgeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (QueryTerms(query, groupIds, limit) is not { } terms)
        {
            return Empty<EntityEdge>();
        }

        var b = new SqlBuilder();
        var hits = Bm25(b, terms, DocKind.Facts, groupIds);
        var conditions = FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m");
        AddGroupCondition(b, groupIds, "e", conditions);
        b.Append($"{hits} SELECT TOP ({b.Int(limit)}) {RecordMapper.EntityEdgeColumns("e")} FROM hit ")
            .Append($"JOIN {N.Table(SqlNames.EntityEdges)} AS e ON e.uuid = hit.uuid")
            .Append(EndpointJoins(searchFilter, "e"))
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append(" ORDER BY ROUND(hit.score, 9) DESC, e.uuid");
        return core.ListAsync(b, RecordMapper.ReadEntityEdge, cancellationToken);
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        string? sourceNodeUuid,
        string? targetNodeUuid,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (!IsComparable(searchVector, limit))
        {
            return Empty<EntityEdge>();
        }

        var b = new SqlBuilder();
        var conditions = FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m");
        AddGroupCondition(b, groupIds, "e", conditions);
        // Upstream applies the endpoint ids only when group ids are given; the contract applies them always.
        if (sourceNodeUuid is not null)
        {
            conditions.Add($"e.source_node_uuid = {b.Text(sourceNodeUuid)}");
        }

        if (targetNodeUuid is not null)
        {
            conditions.Add($"e.target_node_uuid = {b.Text(targetNodeUuid)}");
        }

        AppendSimilarity(
            b, RecordMapper.EntityEdgeColumns("e"),
            $"{N.Table(SqlNames.EntityEdges)} AS e{EndpointJoins(searchFilter, "e")}", "e.fact_embedding", searchVector,
            conditions, limit, minScore);
        return core.ListAsync(b, RecordMapper.ReadEntityEdge, cancellationToken);
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        int maxDepth,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originUuids);
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (originUuids.Count == 0 || maxDepth < 1 || limit <= 0 || groupIds is { Count: 0 })
        {
            return Empty<EntityEdge>();
        }

        var b = new SqlBuilder();
        // A fact is on a path of at most maxDepth hops when its source is at most maxDepth - 1 hops away.
        AppendBfs(b, originUuids, maxDepth - 1, groupIds: null);
        var conditions = FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m");
        AddGroupCondition(b, groupIds, "e", conditions);
        var facts = N.Table(SqlNames.EntityEdges);
        b.Append($"SELECT TOP ({b.Int(limit)}) {RecordMapper.EntityEdgeColumns("e")} FROM (")
            .Append("SELECT f.uuid, MIN(s.depth) AS depth FROM (")
            .Append("SELECT uuid, 0 AS depth FROM @origins WHERE is_episode = 0 UNION ALL SELECT uuid, depth FROM @bfs")
            .Append($") AS s JOIN {facts} AS f ON f.source_node_uuid = s.uuid GROUP BY f.uuid) AS hit ")
            .Append($"JOIN {facts} AS e ON e.uuid = hit.uuid")
            .Append(EndpointJoins(searchFilter, "e"))
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append(" ORDER BY hit.depth, e.uuid;");
        return core.ListAsync(b, RecordMapper.ReadEntityEdge, cancellationToken);
    }

    // ----- Episodes and communities -----

    public Task<IReadOnlyList<EpisodicNode>> EpisodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        // As upstream, the search filter does not apply to episodes: its fields describe facts and entities.
        ArgumentNullException.ThrowIfNull(searchFilter);
        if (QueryTerms(query, groupIds, limit) is not { } terms)
        {
            return Empty<EpisodicNode>();
        }

        var b = new SqlBuilder();
        var hits = Bm25(b, terms, DocKind.Episodes, groupIds);
        var conditions = new List<string>();
        AddGroupCondition(b, groupIds, "ep", conditions);
        b.Append($"{hits} SELECT TOP ({b.Int(limit)}) {RecordMapper.EpisodeColumns("ep")} FROM hit ")
            .Append($"JOIN {N.Table(SqlNames.Episodes)} AS ep ON ep.uuid = hit.uuid")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append(" ORDER BY ROUND(hit.score, 9) DESC, ep.uuid");
        return core.ListAsync(b, RecordMapper.ReadEpisode, cancellationToken);
    }

    public Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(
        string query,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        if (QueryTerms(query, groupIds, limit) is not { } terms)
        {
            return Empty<CommunityNode>();
        }

        var b = new SqlBuilder();
        var hits = Bm25(b, terms, DocKind.Communities, groupIds);
        var conditions = new List<string>();
        AddGroupCondition(b, groupIds, "c", conditions);
        b.Append($"{hits} SELECT TOP ({b.Int(limit)}) {RecordMapper.CommunityColumns("c")} FROM hit ")
            .Append($"JOIN {N.Table(SqlNames.Communities)} AS c ON c.uuid = hit.uuid")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append(" ORDER BY ROUND(hit.score, 9) DESC, c.uuid");
        return core.ListAsync(b, RecordMapper.ReadCommunity, cancellationToken);
    }

    public Task<IReadOnlyList<CommunityNode>> CommunitySimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        if (!IsComparable(searchVector, limit))
        {
            return Empty<CommunityNode>();
        }

        var b = new SqlBuilder();
        var conditions = new List<string>();
        AddGroupCondition(b, groupIds, "c", conditions);
        AppendSimilarity(
            b, RecordMapper.CommunityColumns("c"), $"{N.Table(SqlNames.Communities)} AS c", "c.name_embedding",
            searchVector, conditions, limit, minScore);
        return core.ListAsync(b, RecordMapper.ReadCommunity, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetConnectedNodeUuidsAsync(
        string centerNodeUuid,
        IReadOnlyList<string> candidateUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(centerNodeUuid);
        ArgumentNullException.ThrowIfNull(candidateUuids);
        var candidates = candidateUuids.Where(u => u != centerNodeUuid).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            return new HashSet<string>();
        }

        var facts = N.Table(SqlNames.EntityEdges);
        var b = new SqlBuilder();
        var center = b.Text(centerNodeUuid);
        var ids = b.Ids(candidates);
        b.Append($"SELECT f.target_node_uuid FROM {facts} f WHERE f.source_node_uuid = {center} ")
            .Append($"AND f.target_node_uuid IN {ids} ")
            .Append($"UNION SELECT f.source_node_uuid FROM {facts} f WHERE f.target_node_uuid = {center} ")
            .Append($"AND f.source_node_uuid IN {ids}");
        var rows = await core.ListAsync(b, r => r.GetString(0), cancellationToken).ConfigureAwait(false);
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    // ----- SQL helpers -----

    /// <summary>
    /// Upstream's checks before a full-text query (group ids must be valid; an empty query or an empty group list
    /// matches nothing), then the query's terms with how often each occurs, or <see langword="null"/> when nothing
    /// can match. Unlike upstream, long queries are not cut off.
    /// </summary>
    private static Dictionary<string, int>? QueryTerms(string query, IReadOnlyList<string>? groupIds, int limit)
    {
        Validation.ValidateGroupIds(groupIds);
        if (string.IsNullOrWhiteSpace(query) || groupIds is { Count: 0 } || limit <= 0)
        {
            return null;
        }

        var (length, frequencies) = Tokenizer.Count(query);
        return length == 0 ? null : frequencies;
    }

    /// <summary>
    /// A <c>WITH</c> clause defining <c>hit(uuid, score)</c>: the BM25 score of every record of a kind that holds a
    /// query term, in the requested groups. A query term counts once per occurrence in the query, as Lucene's.
    /// </summary>
    private string Bm25(SqlBuilder b, Dictionary<string, int> terms, byte kind, IReadOnlyList<string>? groupIds)
    {
        var k = kind.ToString(CultureInfo.InvariantCulture);
        var termsJson = b.Json(JsonSerializer.Serialize(terms.Select(t => new { t = t.Key, n = t.Value })));
        var groups = groupIds is null ? "" : $" AND t.group_id IN {b.Ids(groupIds)}";
        var termTable = N.Table(SqlNames.SearchTerms);
        // idf = ln(1 + (N - df + 0.5) / (df + 0.5)); tf part = tf (k1 + 1) / (tf + k1 (1 - b + b dl / avgdl)).
        return "SET NOCOUNT ON; WITH "
            + $"q AS (SELECT term, qtf FROM OPENJSON({termsJson}) WITH (term nvarchar(255) '$.t', qtf int '$.n')), "
            + "fs AS (SELECT field, CAST(COUNT_BIG(*) AS float) AS docs, "
            + "CAST(SUM(CAST(field_length AS bigint)) AS float) AS total "
            + $"FROM {N.Table(SqlNames.SearchFields)} WHERE doc_kind = {k} GROUP BY field), "
            + "df AS (SELECT t.field, t.term, CAST(COUNT_BIG(*) AS float) AS df "
            + $"FROM {termTable} AS t WHERE t.doc_kind = {k} AND t.term IN (SELECT term FROM q) GROUP BY t.field, t.term), "
            + "hit AS (SELECT t.uuid, SUM(q.qtf * LOG(1E0 + (fs.docs - df.df + 5E-1) / (df.df + 5E-1)) "
            + "* (t.frequency * 2.2E0) / (t.frequency + 1.2E0 * (2.5E-1 + 7.5E-1 * t.field_length / (fs.total / fs.docs)))) "
            + $"AS score FROM {termTable} AS t JOIN q ON q.term = t.term "
            + "JOIN df ON df.field = t.field AND df.term = t.term JOIN fs ON fs.field = t.field "
            + $"WHERE t.doc_kind = {k}{groups} GROUP BY t.uuid)";
    }

    /// <summary>Whether a query vector can score: it has the stored size and is not all zeros.</summary>
    private bool IsComparable(IReadOnlyList<float> vector, int limit) =>
        limit > 0 && vector.Count == core.Options.EmbeddingDimension && vector.Any(x => x != 0);

    private static void AppendSimilarity(
        SqlBuilder b,
        string columns,
        string from,
        string embedding,
        IReadOnlyList<float> vector,
        List<string> conditions,
        int limit,
        double minScore)
    {
        var query = b.Vector(vector);
        var min = b.Float(minScore);
        var limitParam = b.Int(limit);
        // Zero vectors have no direction: SQL Server scores them as orthogonal, the contract never matches them.
        var all = new List<string> { $"{embedding} IS NOT NULL", $"VECTOR_NORM({embedding}, 'norm2') > 0" };
        all.AddRange(conditions);
        // The minimum is exclusive, as upstream (score > min_score).
        b.Append($"SELECT TOP ({limitParam}) s.* FROM (SELECT {columns}, ")
            .Append($"1E0 - VECTOR_DISTANCE('cosine', {embedding}, {query}) / 2E0 AS score FROM {from}")
            .Append(FilterSql.Where(all, " WHERE "))
            .Append($") AS s WHERE s.score > {min} ORDER BY s.score DESC, s.uuid");
    }

    /// <summary>
    /// Appends a batch that fills two table variables: <c>@origins(uuid, group_id, is_episode)</c>, the origin entities
    /// and episodes, and <c>@bfs(origin, origin_group, uuid, depth)</c>, every node reachable from each origin in 1 to
    /// <paramref name="maxDepth"/> hops, at its fewest hops. Entities an episode origin mentions are 1 hop away; each
    /// fact, followed from source to target as upstream's directed Cypher paths, adds a hop. An origin is in
    /// <c>@bfs</c> only when a cycle leads back to it. Shortest walks are simple paths, so the reachable set equals
    /// upstream's relationship-unique paths.
    /// </summary>
    private void AppendBfs(SqlBuilder b, IReadOnlyList<string> originUuids, int maxDepth, IReadOnlyList<string>? groupIds)
    {
        var origins = b.Ids(DriverCore.ValidIds(originUuids));
        var depth = b.Int(maxDepth);
        var originGroup = groupIds is null ? "" : $" AND o.group_id IN {b.Ids(groupIds)}";
        var facts = N.Table(SqlNames.EntityEdges);
        var id = $"nvarchar(255) COLLATE {Bin2}";
        b.Append("SET NOCOUNT ON; ")
            .Append($"DECLARE @origins TABLE (uuid {id} PRIMARY KEY, group_id {id}, is_episode bit); ")
            .Append($"DECLARE @bfs TABLE (origin {id}, origin_group {id}, uuid {id}, depth int, PRIMARY KEY (origin, uuid)); ")
            .Append("DECLARE @depth int = 1; ")
            .Append($"INSERT INTO @origins SELECT o.uuid, o.group_id, 0 FROM {N.Table(SqlNames.Entities)} o ")
            .Append($"WHERE o.uuid IN {origins}{originGroup}; ")
            .Append($"INSERT INTO @origins SELECT o.uuid, o.group_id, 1 FROM {N.Table(SqlNames.Episodes)} o ")
            .Append($"WHERE o.uuid IN {origins}{originGroup} AND o.uuid NOT IN (SELECT uuid FROM @origins); ")
            .Append($"IF {depth} >= 1 BEGIN ")
            .Append("INSERT INTO @bfs SELECT o.uuid, MIN(o.group_id), f.target_node_uuid, 1 FROM @origins o ")
            .Append($"JOIN {facts} f ON f.source_node_uuid = o.uuid WHERE o.is_episode = 0 ")
            .Append("GROUP BY o.uuid, f.target_node_uuid; ")
            .Append("INSERT INTO @bfs SELECT o.uuid, MIN(o.group_id), m.target_node_uuid, 1 FROM @origins o ")
            .Append($"JOIN {N.Table(SqlNames.EpisodicEdges)} m ON m.source_node_uuid = o.uuid WHERE o.is_episode = 1 ")
            .Append("GROUP BY o.uuid, m.target_node_uuid; END; ")
            .Append($"WHILE @depth < {depth} BEGIN ")
            .Append("INSERT INTO @bfs SELECT w.origin, MIN(w.origin_group), f.target_node_uuid, @depth + 1 FROM @bfs w ")
            .Append($"JOIN {facts} f ON f.source_node_uuid = w.uuid WHERE w.depth = @depth AND NOT EXISTS ")
            .Append("(SELECT 1 FROM @bfs x WHERE x.origin = w.origin AND x.uuid = f.target_node_uuid) ")
            .Append("GROUP BY w.origin, f.target_node_uuid; ")
            .Append("IF @@ROWCOUNT = 0 BREAK; SET @depth += 1; END; ");
    }

    private string EndpointJoins(SearchFilters filters, string e) =>
        FilterSql.NeedsEndpoints(filters)
            ? $" JOIN {N.Table(SqlNames.Entities)} AS n ON n.uuid = {e}.source_node_uuid "
            + $"JOIN {N.Table(SqlNames.Entities)} AS m ON m.uuid = {e}.target_node_uuid"
            : "";

    private static void AddGroupCondition(SqlBuilder b, IReadOnlyList<string>? groupIds, string alias, List<string> c)
    {
        if (groupIds is not null)
        {
            c.Add($"{alias}.group_id IN {b.Ids(groupIds)}");
        }
    }

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
}
