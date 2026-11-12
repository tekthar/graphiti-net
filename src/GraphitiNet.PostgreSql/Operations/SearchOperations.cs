// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/neo4j/operations/search_ops.py and graphiti_core/search/search_utils.py
// (Graphiti v0.31.2) and changed for .NET: Lucene full-text becomes PostgreSQL text search, Cypher vector similarity
// becomes pgvector, and variable-length Cypher paths become recursive CTEs.

using GraphitiNet.Driver.PostgreSql.Internal;
using GraphitiNet.Models;
using GraphitiNet.Search;
using GraphitiNet.Utils;
using Npgsql;
using NpgsqlTypes;

namespace GraphitiNet.Driver.PostgreSql.Operations;

/// <summary>Full-text, vector and graph search in SQL.</summary>
/// <remarks>
/// <para><b>Full-text:</b> the query is normalised with each searched group's text search configuration and its lexemes are
/// OR-ed, like Lucene's default operator upstream, so a record matching any term is a candidate. Each row is
/// matched with its group's text search configuration. Candidates are ranked by the number of distinct query terms
/// they contain, then by <c>ts_rank</c> (normalisation 1, dividing by the log of the document length), best first,
/// ties by uuid. PostgreSQL has no BM25, so ranking differs from upstream's Lucene scores.</para>
/// <para><b>Similarity:</b> cosine similarity (<c>1 - (embedding &lt;=&gt; query)</c>), strictly above
/// <c>minScore</c> as upstream, exact by default; see
/// <see cref="PostgresGraphOptions.ApproximateVectorSearch"/>.</para>
/// <para><b>Breadth-first:</b> a recursive CTE walks facts from source to target and episode-to-entity links
/// outward, up to <c>maxDepth</c> hops, as upstream's Neo4j queries. Results are distinct and ordered by hop distance,
/// then uuid.</para>
/// </remarks>
internal sealed class SearchOperations(DriverCore core) : ISearchOperations
{
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
        if (!IsSearchableText(query, groupIds) || limit <= 0)
        {
            return Empty<EntityNode>();
        }

        var b = new SqlBuilder();
        var tsQuery = TsQuery(b, query, groupIds, SqlNames.Entities);
        var conditions = FilterSql.NodeConditions(b, searchFilter, "n");
        AddGroupCondition(b, groupIds, "n", conditions);
        b.Append($"SELECT {RecordMapper.EntityColumns("n")} FROM ({tsQuery}) AS tq ")
            .Append($"JOIN {N.Table(SqlNames.Entities)} AS n ON n.text_search_config = tq.cfg AND n.search_vector @@ tq.q")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append($" ORDER BY {Rank("n")} DESC, n.uuid LIMIT {b.Int(limit)}");
        return ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
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
        if (limit <= 0)
        {
            return Empty<EntityNode>();
        }

        var b = new SqlBuilder();
        var vector = b.Vector(searchVector);
        var conditions = new List<string> { "n.name_embedding IS NOT NULL" };
        conditions.AddRange(FilterSql.NodeConditions(b, searchFilter, "n"));
        AddGroupCondition(b, groupIds, "n", conditions);
        AppendSimilarity(
            b, RecordMapper.EntityColumns("n"), $"{N.Table(SqlNames.Entities)} AS n", "n.name_embedding", vector,
            conditions, limit, minScore);
        return ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
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
        if (originUuids.Count == 0 || maxDepth < 1 || limit <= 0)
        {
            return Empty<EntityNode>();
        }

        var b = new SqlBuilder();
        AppendBfs(b, originUuids, maxDepth, groupIds);
        var conditions = FilterSql.NodeConditions(b, searchFilter, "n");
        AddGroupCondition(b, groupIds, "n", conditions);
        // As upstream, only entities in the origin's group, 1 to maxDepth hops away (an origin on a cycle counts).
        b.Append($"SELECT {RecordMapper.EntityColumns("n")} FROM (")
            .Append("SELECT bfs.uuid, min(bfs.depth) AS depth FROM bfs ")
            .Append($"JOIN {N.Table(SqlNames.Entities)} AS r ON r.uuid = bfs.uuid AND r.group_id = bfs.origin_group ")
            .Append("WHERE bfs.depth >= 1 GROUP BY bfs.uuid) AS hit ")
            .Append($"JOIN {N.Table(SqlNames.Entities)} AS n ON n.uuid = hit.uuid")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append($" ORDER BY hit.depth, n.uuid LIMIT {b.Int(limit)}");
        return ListAsync(b, RecordMapper.ReadEntity, cancellationToken);
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
        if (!IsSearchableText(query, groupIds) || limit <= 0)
        {
            return Empty<EntityEdge>();
        }

        var b = new SqlBuilder();
        var tsQuery = TsQuery(b, query, groupIds, SqlNames.EntityEdges);
        var conditions = FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m");
        AddGroupCondition(b, groupIds, "e", conditions);
        b.Append($"SELECT {RecordMapper.EntityEdgeColumns("e")} FROM ({tsQuery}) AS tq ")
            .Append($"JOIN {N.Table(SqlNames.EntityEdges)} AS e ON e.text_search_config = tq.cfg AND e.search_vector @@ tq.q")
            .Append(EndpointJoins(searchFilter, "e"))
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append($" ORDER BY {Rank("e")} DESC, e.uuid LIMIT {b.Int(limit)}");
        return ListAsync(b, RecordMapper.ReadEntityEdge, cancellationToken);
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
        if (limit <= 0)
        {
            return Empty<EntityEdge>();
        }

        var b = new SqlBuilder();
        var vector = b.Vector(searchVector);
        var conditions = new List<string> { "e.fact_embedding IS NOT NULL" };
        conditions.AddRange(FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m"));
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
            $"{N.Table(SqlNames.EntityEdges)} AS e{EndpointJoins(searchFilter, "e")}", "e.fact_embedding", vector,
            conditions, limit, minScore);
        return ListAsync(b, RecordMapper.ReadEntityEdge, cancellationToken);
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
        if (originUuids.Count == 0 || maxDepth < 1 || limit <= 0)
        {
            return Empty<EntityEdge>();
        }

        var b = new SqlBuilder();
        // A fact is on a path of at most maxDepth hops when its source is at most maxDepth - 1 hops away.
        AppendBfs(b, originUuids, maxDepth - 1, groupIds: null);
        var conditions = FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m");
        AddGroupCondition(b, groupIds, "e", conditions);
        var facts = N.Table(SqlNames.EntityEdges);
        b.Append(", frontier AS (SELECT uuid, min(depth) AS depth FROM bfs GROUP BY uuid), ")
            .Append("hit AS (SELECT f.uuid, min(fr.depth) AS depth FROM frontier fr ")
            .Append($"JOIN {facts} f ON f.source_node_uuid = fr.uuid GROUP BY f.uuid) ")
            .Append($"SELECT {RecordMapper.EntityEdgeColumns("e")} FROM hit JOIN {facts} AS e ON e.uuid = hit.uuid")
            .Append(EndpointJoins(searchFilter, "e"))
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append($" ORDER BY hit.depth, e.uuid LIMIT {b.Int(limit)}");
        return ListAsync(b, RecordMapper.ReadEntityEdge, cancellationToken);
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
        if (!IsSearchableText(query, groupIds) || limit <= 0)
        {
            return Empty<EpisodicNode>();
        }

        var b = new SqlBuilder();
        var tsQuery = TsQuery(b, query, groupIds, SqlNames.Episodes);
        var conditions = new List<string>();
        AddGroupCondition(b, groupIds, "ep", conditions);
        b.Append($"SELECT {RecordMapper.EpisodeColumns("ep")} FROM ({tsQuery}) AS tq ")
            .Append($"JOIN {N.Table(SqlNames.Episodes)} AS ep ON ep.text_search_config = tq.cfg AND ep.search_vector @@ tq.q")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append($" ORDER BY {Rank("ep")} DESC, ep.uuid LIMIT {b.Int(limit)}");
        return ListAsync(b, RecordMapper.ReadEpisode, cancellationToken);
    }

    public Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(
        string query,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        if (!IsSearchableText(query, groupIds) || limit <= 0)
        {
            return Empty<CommunityNode>();
        }

        var b = new SqlBuilder();
        var tsQuery = TsQuery(b, query, groupIds, SqlNames.Communities);
        var conditions = new List<string>();
        AddGroupCondition(b, groupIds, "c", conditions);
        b.Append($"SELECT {RecordMapper.CommunityColumns("c")} FROM ({tsQuery}) AS tq ")
            .Append($"JOIN {N.Table(SqlNames.Communities)} AS c ON c.text_search_config = tq.cfg AND c.search_vector @@ tq.q")
            .Append(FilterSql.Where(conditions, " WHERE "))
            .Append($" ORDER BY {Rank("c")} DESC, c.uuid LIMIT {b.Int(limit)}");
        return ListAsync(b, RecordMapper.ReadCommunity, cancellationToken);
    }

    public Task<IReadOnlyList<CommunityNode>> CommunitySimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        if (limit <= 0)
        {
            return Empty<CommunityNode>();
        }

        var b = new SqlBuilder();
        var vector = b.Vector(searchVector);
        var conditions = new List<string> { "c.name_embedding IS NOT NULL" };
        AddGroupCondition(b, groupIds, "c", conditions);
        AppendSimilarity(
            b, RecordMapper.CommunityColumns("c"), $"{N.Table(SqlNames.Communities)} AS c", "c.name_embedding",
            vector, conditions, limit, minScore);
        return ListAsync(b, RecordMapper.ReadCommunity, cancellationToken);
    }

    public Task<IReadOnlySet<string>> GetConnectedNodeUuidsAsync(
        string centerNodeUuid,
        IReadOnlyList<string> candidateUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(centerNodeUuid);
        ArgumentNullException.ThrowIfNull(candidateUuids);
        var candidates = candidateUuids.Where(u => u != centerNodeUuid).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        }

        var facts = N.Table(SqlNames.EntityEdges);
        return core.WithConnectionAsync<IReadOnlySet<string>>(
            async connection =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT f.target_node_uuid FROM {facts} f WHERE f.source_node_uuid = $1 "
                    + "AND f.target_node_uuid = ANY($2) "
                    + $"UNION SELECT f.source_node_uuid FROM {facts} f WHERE f.target_node_uuid = $1 "
                    + "AND f.source_node_uuid = ANY($2)",
                    connection);
                command.Parameters.Add(Params.Text(centerNodeUuid));
                command.Parameters.Add(Params.TextArray(candidates));
                var rows = await DriverCore.ReadListAsync(command, r => r.GetString(0), cancellationToken)
                    .ConfigureAwait(false);
                return rows.ToHashSet(StringComparer.Ordinal);
            },
            cancellationToken);
    }

    // ----- SQL helpers -----

    /// <summary>Upstream's checks before a full-text query: group ids must be valid; an empty query or an empty group
    /// list matches nothing. Unlike upstream, long queries are not cut off.</summary>
    private static bool IsSearchableText(string query, IReadOnlyList<string>? groupIds)
    {
        Validation.ValidateGroupIds(groupIds);
        return !string.IsNullOrWhiteSpace(query) && groupIds is not { Count: 0 };
    }

    /// <summary>
    /// One row per text search configuration in play, yielding <c>cfg</c>, <c>q</c> (the OR of the query's lexemes
    /// normalised with that configuration) and <c>terms</c> (each lexeme as its own tsquery). Rows only match the
    /// query built with their own configuration. Each lexeme is quoted, so operators and punctuation in user input are
    /// never parsed as tsquery syntax. A query of only stop words yields no rows, which matches nothing.
    /// </summary>
    private string TsQuery(SqlBuilder b, string query, IReadOnlyList<string>? groupIds, string table)
    {
        string configs;
        if (groupIds is not null)
        {
            var names = groupIds.Select(core.Options.ResolveTextSearchConfig).Distinct().ToArray();
            configs = $"SELECT unnest(CAST({b.TextArray(names)} AS regconfig[])) AS cfg";
        }
        else if (core.Options.TextSearchConfigSelector is null)
        {
            var names = new[] { core.Options.DefaultTextSearchConfig }
                .Concat(core.Options.TextSearchConfigs?.Values ?? [])
                .Distinct()
                .ToArray();
            configs = $"SELECT unnest(CAST({b.TextArray(names)} AS regconfig[])) AS cfg";
        }
        else
        {
            configs = $"SELECT DISTINCT text_search_config AS cfg FROM {N.Table(table)}";
        }

        var text = b.Text(query);
        const string quoted = "('''' || replace(replace(lexeme, '\\', '\\\\'), '''', '''''') || '''')";
        return $"SELECT c.cfg, string_agg({quoted}, ' | ')::tsquery AS q, array_agg({quoted}::tsquery) AS terms "
            + $"FROM ({configs}) AS c CROSS JOIN LATERAL unnest(tsvector_to_array(to_tsvector(c.cfg, {text}))) "
            + "AS lexeme GROUP BY c.cfg";
    }

    /// <summary>
    /// The relevance of a row: how many distinct query terms it contains, then <c>ts_rank</c> with length
    /// normalisation. Matching more terms comes first, much as BM25's inverse document frequency rewards coverage.
    /// </summary>
    private static string Rank(string alias) =>
        $"(SELECT count(*) FROM unnest(tq.terms) AS term WHERE {alias}.search_vector @@ term) DESC, "
        + $"ts_rank({alias}.search_vector, tq.q, 1)";

    private void AppendSimilarity(
        SqlBuilder b,
        string columns,
        string from,
        string embedding,
        string vector,
        List<string> conditions,
        int limit,
        double minScore)
    {
        var min = b.Param(minScore, NpgsqlDbType.Double);
        var limitParam = b.Int(limit);
        b.Append($"SELECT s.* FROM (SELECT {columns}, 1 - ({embedding} <=> {vector}) AS score FROM {from}")
            .Append(FilterSql.Where(conditions, " WHERE "));
        if (core.Options.ApproximateVectorSearch)
        {
            // Ordering by the distance operator lets the HNSW index answer the inner query.
            b.Append($" ORDER BY {embedding} <=> {vector} LIMIT {limitParam}");
        }

        // NaN (a zero vector) is excluded explicitly: PostgreSQL sorts NaN above every number.
        // The minimum is exclusive, as upstream (score > min_score).
        b.Append($") AS s WHERE s.score > {min} AND s.score <> 'NaN'::float8 ")
            .Append($"ORDER BY s.score DESC, s.uuid LIMIT {limitParam}");
    }

    /// <summary>
    /// Starts a <c>WITH RECURSIVE</c> clause defining <c>bfs(origin, origin_group, uuid, depth)</c>: every entity
    /// reachable from each origin within <paramref name="maxDepth"/> hops. Entity origins are at depth 0; entities an
    /// episode origin mentions are at depth 1; each fact, followed from source to target as upstream's directed
    /// Cypher paths, adds a hop. Shortest walks are simple paths, so the reachable set equals upstream's
    /// relationship-unique paths.
    /// </summary>
    private void AppendBfs(SqlBuilder b, IReadOnlyList<string> originUuids, int maxDepth, IReadOnlyList<string>? groupIds)
    {
        var origins = b.TextArray(originUuids.Distinct());
        var depth = b.Int(maxDepth);
        var originGroup = groupIds is null ? "" : $" AND o.group_id = ANY({b.TextArray(groupIds)})";
        var facts = N.Table(SqlNames.EntityEdges);
        b.Append("WITH RECURSIVE ")
            .Append("origins AS (")
            .Append($"SELECT o.uuid, o.group_id, FALSE AS is_episode FROM {N.Table(SqlNames.Entities)} o ")
            .Append($"WHERE o.uuid = ANY({origins}){originGroup} ")
            .Append($"UNION ALL SELECT o.uuid, o.group_id, TRUE FROM {N.Table(SqlNames.Episodes)} o ")
            .Append($"WHERE o.uuid = ANY({origins}){originGroup}), ")
            .Append("bfs(origin, origin_group, uuid, depth) AS (")
            .Append("SELECT o.uuid, o.group_id, o.uuid, 0 FROM origins o WHERE NOT o.is_episode ")
            .Append("UNION SELECT o.uuid, o.group_id, me.target_node_uuid, 1 FROM origins o ")
            .Append($"JOIN {N.Table(SqlNames.EpisodicEdges)} me ON me.source_node_uuid = o.uuid ")
            .Append($"WHERE o.is_episode AND {depth} >= 1 ")
            .Append("UNION SELECT b.origin, b.origin_group, f.target_node_uuid, b.depth + 1 FROM bfs b ")
            .Append($"JOIN {facts} f ON f.source_node_uuid = b.uuid WHERE b.depth < {depth})");
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
            c.Add($"{alias}.group_id = ANY({b.TextArray(groupIds)})");
        }
    }

    private Task<IReadOnlyList<T>> ListAsync<T>(SqlBuilder b, Func<NpgsqlDataReader, T> map, CancellationToken ct) =>
        core.WithConnectionAsync<IReadOnlyList<T>>(
            async connection =>
            {
                await using var command = b.ToCommand(connection);
                return await DriverCore.ReadListAsync(command, map, ct).ConfigureAwait(false);
            },
            ct);

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
}
