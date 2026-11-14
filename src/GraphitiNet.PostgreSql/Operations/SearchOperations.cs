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
/// <para><b>Full-text:</b> Lucene's BM25 in SQL, as upstream's Neo4j indexes and the in-memory driver rank. The query is
/// split into terms like Lucene's standard tokenizer, normalised with each searched group's text search
/// configuration (unchanged under <c>simple</c>), and a record containing any term is a candidate (Lucene's default
/// OR), found through a GIN index. Candidates are scored with BM25 from the per-field term frequencies and lengths
/// the driver stores with each row and collection statistics over the whole table; best first, ties by uuid. See
/// <see cref="Bm25"/>.</para>
/// <para><b>Similarity:</b> Neo4j's cosine score, <c>(1 + cosine) / 2</c> = <c>1 - (embedding &lt;=&gt; query) / 2</c>, strictly above
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

        return FulltextAsync(
            query,
            groupIds,
            SqlNames.Entities,
            "n",
            (b, bm25) =>
            {
                var filters = FilterSql.NodeConditions(b, searchFilter, "n");
                if (filters.Count == 0)
                {
                    b.Append(bm25.TopK(RecordMapper.EntityColumns("n"), b.Int(limit)));
                    return;
                }

                var conditions = new List<string> { bm25.Match };
                conditions.AddRange(filters);
                AddGroupCondition(b, groupIds, "n", conditions);
                b.Append($"SELECT {RecordMapper.EntityColumns("n")}, {bm25.Columns} FROM {bm25.From}")
                    .Append(FilterSql.Where(conditions, " WHERE "))
                    .Append($" ORDER BY {bm25.Score} DESC, n.uuid LIMIT {b.Int(limit)}");
            },
            RecordMapper.ReadEntity,
            limit,
            cancellationToken);
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

        return FulltextAsync(
            query,
            groupIds,
            SqlNames.EntityEdges,
            "e",
            (b, bm25) =>
            {
                var filters = FilterSql.EdgeConditions(b, searchFilter, "e", "n", "m");
                if (filters.Count == 0 && !FilterSql.NeedsEndpoints(searchFilter))
                {
                    b.Append(bm25.TopK(RecordMapper.EntityEdgeColumns("e"), b.Int(limit)));
                    return;
                }

                var conditions = new List<string> { bm25.Match };
                conditions.AddRange(filters);
                AddGroupCondition(b, groupIds, "e", conditions);
                b.Append($"SELECT {RecordMapper.EntityEdgeColumns("e")}, {bm25.Columns} FROM {bm25.From}")
                    .Append(EndpointJoins(searchFilter, "e"))
                    .Append(FilterSql.Where(conditions, " WHERE "))
                    .Append($" ORDER BY {bm25.Score} DESC, e.uuid LIMIT {b.Int(limit)}");
            },
            RecordMapper.ReadEntityEdge,
            limit,
            cancellationToken);
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

        return FulltextAsync(
            query,
            groupIds,
            SqlNames.Episodes,
            "ep",
            (b, bm25) => b.Append(bm25.TopK(RecordMapper.EpisodeColumns("ep"), b.Int(limit))),
            RecordMapper.ReadEpisode,
            limit,
            cancellationToken);
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

        return FulltextAsync(
            query,
            groupIds,
            SqlNames.Communities,
            "c",
            (b, bm25) => b.Append(bm25.TopK(RecordMapper.CommunityColumns("c"), b.Int(limit))),
            RecordMapper.ReadCommunity,
            limit,
            cancellationToken);
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
        return groupIds is not { Count: 0 } && FullText.Tokenize(query).Count > 0;
    }

    /// <summary>
    /// Runs a BM25 full-text query over <paramref name="table"/> (aliased <paramref name="alias"/>): works out the
    /// query's terms (<see cref="QueryTermsAsync"/>) and reads the collection statistics
    /// (<see cref="StatisticsAsync"/>), then reads the rows the <see cref="KeywordPlan"/> picks, one to three passes,
    /// each statement written by <paramref name="build"/> from the <see cref="Bm25Sql"/> parts. Everything runs in one
    /// repeatable-read snapshot, so the statistics are exactly those of the rows scored, whatever is written
    /// concurrently.
    /// </summary>
    private Task<IReadOnlyList<T>> FulltextAsync<T>(
        string query,
        IReadOnlyList<string>? groupIds,
        string table,
        string alias,
        Action<SqlBuilder, Bm25Sql> build,
        Func<NpgsqlDataReader, T> map,
        int limit,
        CancellationToken ct) =>
        core.WithConnectionAsync<IReadOnlyList<T>>(
            async connection =>
            {
                await core.CheckSearchStatisticsAsync(connection, ct).ConfigureAwait(false);
                await using var transaction = await connection
                    .BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct)
                    .ConfigureAwait(false);
                var terms = await QueryTermsAsync(connection, query, groupIds, table, ct).ConfigureAwait(false);
                var distinct = terms.SelectMany(q => q.Terms).Distinct(StringComparer.Ordinal).ToList();
                if (distinct.Count == 0)
                {
                    return [];
                }

                var statistics = await StatisticsAsync(connection, table, distinct, groupIds, ct).ConfigureAwait(false);
                var plan = new KeywordPlan(terms, statistics, SearchIndex.Columns[table].Length);
                if (plan.IsEmpty)
                {
                    return [];
                }

                List<Scored<T>>? rows = null;
                var readKeys = 0;
                for (var attempt = 0; rows is null && attempt < 3; attempt++)
                {
                    var first = plan.First(attempt);
                    if (first is null || first.Keys.Count == readKeys)
                    {
                        break;
                    }

                    readKeys = first.Keys.Count;
                    rows = await RunAsync(b => $"s.search_keys && {b.TextArray(first.Keys)}").ConfigureAwait(false);
                    if (rows.Count < limit)
                    {
                        // Too few rows in the searched groups to set a threshold: try again with more keys.
                        rows = null;
                    }
                    else if (rows[^1].Score <= first.Rest)
                    {
                        // Rows without the first pass's keys may still beat its last row: read those that can.
                        string? second = null;
                        var page = rows;
                        var more = await RunAsync(b => second = plan.Second(first, page[^1].Score, "s", b.TextArray))
                            .ConfigureAwait(false);
                        rows = second is null
                            ? null
                            : [.. page.Concat(more).DistinctBy(r => r.Uuid, StringComparer.Ordinal)
                                .OrderByDescending(r => r.Score).ThenBy(r => r.Uuid, StringComparer.Ordinal).Take(limit)];
                        break;
                    }
                }

                if (rows is not null)
                {
                    Interlocked.Increment(ref KeywordPlan.PrunedSearches);
                }
                else
                {
                    // A full pass: every row with a query term in the searched groups, through the groups' rows when
                    // they are few (testing each one's term map, a binary search), else through the GIN index.
                    rows = await RunAsync(b => plan.GroupsFirst
                            ? $"s.search_terms ?| {b.TextArray(distinct)}"
                            : $"s.search_keys && {b.TextArray(plan.AllKeys())}")
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return rows.Select(r => r.Row).ToList();

                // Scores the rows matching the condition the function writes, best first, or none when it writes null
                // (or "false").
                async Task<List<Scored<T>>> RunAsync(Func<SqlBuilder, string?> candidates)
                {
                    var b = new SqlBuilder();
                    var where = candidates(b);
                    if (where is null or "false")
                    {
                        return [];
                    }

                    build(b, Bm25(b, terms, plan, where, groupIds, table, alias));
                    await using var command = b.ToCommand(connection);
                    return await DriverCore.ReadListAsync(
                            command,
                            r => new Scored<T>(map(r), r.GetString(r.FieldCount - 2), r.GetDouble(r.FieldCount - 1)),
                            ct)
                        .ConfigureAwait(false);
                }
            },
            ct);

    private sealed record Scored<T>(T Row, string Uuid, double Score);

    /// <summary>
    /// The query's terms under each text search configuration in play, in query order with repeats: the searched
    /// groups' configurations, or, for every group, each configuration the options name (or, with a selector, each
    /// one the table holds, from <c>search_term_counts</c>). Under <c>simple</c> they are the tokenizer's terms; under
    /// a language configuration each is normalised exactly as the rows' terms were.
    /// </summary>
    private async Task<List<(string Config, List<string> Terms)>> QueryTermsAsync(
        NpgsqlConnection connection,
        string query,
        IReadOnlyList<string>? groupIds,
        string table,
        CancellationToken ct)
    {
        IEnumerable<string> configs;
        if (groupIds is not null)
        {
            configs = groupIds.Select(core.Options.ResolveTextSearchConfig);
        }
        else if (core.Options.TextSearchConfigSelector is null)
        {
            configs = new[] { core.Options.DefaultTextSearchConfig }.Concat(core.Options.TextSearchConfigs?.Values ?? []);
        }
        else
        {
            await using var command = new NpgsqlCommand(
                $"SELECT substr(key, {SearchStatisticsSql.ConfigPrefix.Length + 1}) FROM "
                + $"{N.Table(SearchStatisticsSql.TermCountsTable)} WHERE table_name = $1 AND key LIKE '{SearchStatisticsSql.ConfigPrefix}%' "
                + "GROUP BY key HAVING sum(docs) > 0",
                connection);
            command.Parameters.Add(Params.Text(N.Name(table)));
            configs = await DriverCore.ReadListAsync(command, r => r.GetString(0), ct).ConfigureAwait(false);
        }

        var tokens = FullText.Tokenize(query);
        var distinct = configs.Select(SearchIndex.CanonicalConfig).Distinct(StringComparer.Ordinal).ToList();
        var lexemes = await SearchIndex.NormaliseAsync(
            connection,
            distinct.Where(c => !SearchIndex.IsSimple(c)).SelectMany(c => tokens.Select(t => (c, t))),
            ct).ConfigureAwait(false);
        return distinct
            .Select(c => (c, SearchIndex.IsSimple(c) ? tokens : tokens.SelectMany(t => lexemes[(c, t)]).ToList()))
            .ToList();
    }

    /// <summary>
    /// The collection statistics of <paramref name="table"/> for the given terms, over the whole table (every group),
    /// as a Lucene index keeps them: per field, how many rows have any term and their total length, from
    /// <c>search_statistics</c>; and how many rows hold each of the terms' keys, from <c>search_term_counts</c> (see
    /// <c>SearchStatisticsSql</c>). Both are kept by triggers; one round trip reads a few rows per term.
    /// </summary>
    private async Task<Bm25Statistics> StatisticsAsync(
        NpgsqlConnection connection,
        string table,
        List<string> terms,
        IReadOnlyList<string>? groupIds,
        CancellationToken ct)
    {
        var fields = Enumerable.Range(1, SearchIndex.Columns[table].Length).ToArray();
        var collection = string.Join(", ", fields.Select(f =>
            $"coalesce(sum(docs{f}), 0)::bigint, coalesce(sum(length{f}), 0)::bigint"));
        await using var batch = new NpgsqlBatch(connection);

        // For the rest of the transaction: the planner cannot tell how few rows the GIN conditions select, and a
        // sequential scan would test every row's keys against them (an array overlap, slow on long arrays), so the
        // indexes are the way in; bitmaps stay exact, so their rows are not tested again either; and the statements
        // read too few rows to gain from parallel workers or JIT compilation, which take milliseconds to start (the
        // planner's estimates for these conditions are far too high to judge).
        DriverCore.AddCommand(batch, "SET LOCAL enable_seqscan = off");
        DriverCore.AddCommand(batch, "SET LOCAL work_mem = '64MB'");
        DriverCore.AddCommand(batch, "SET LOCAL max_parallel_workers_per_gather = 0");
        DriverCore.AddCommand(batch, "SET LOCAL jit = off");
        DriverCore.AddCommand(
            batch,
            $"SELECT {collection} FROM {N.Table(SearchStatisticsSql.Table)} WHERE table_name = $1",
            Params.Text(N.Name(table)));

        // Each (term, field)'s keys: a range of the index (field:term|0 to field:term|131 sort together), plus the
        // key without a class that rows written by 0.1 have.
        var pairs = terms.SelectMany(term => fields.Select(f => SearchIndex.Key(f - 1, term, SearchIndex.UnclassedImpact)))
            .ToList();
        DriverCore.AddCommand(
            batch,
            $"SELECT x.key, sum(x.docs)::bigint FROM (SELECT c.key, c.docs FROM unnest($2::text[]) AS p(pair) "
            + $"JOIN {N.Table(SearchStatisticsSql.TermCountsTable)} AS c ON c.table_name = $1 "
            + "AND c.key = (p.pair COLLATE \"C\") UNION ALL SELECT c.key, c.docs FROM unnest($2::text[]) AS p(pair) "
            + $"JOIN {N.Table(SearchStatisticsSql.TermCountsTable)} AS c ON c.table_name = $1 "
            + "AND c.key >= ((p.pair || '|') COLLATE \"C\") AND c.key < ((p.pair || '}') COLLATE \"C\")) AS x "
            + "GROUP BY x.key HAVING sum(x.docs) > 0",
            Params.Text(N.Name(table)),
            Params.TextArray(pairs));
        DriverCore.AddCommand(
            batch,
            $"SELECT coalesce(sum(docs), 0)::bigint FROM {N.Table(SearchStatisticsSql.TermCountsTable)} "
            + "WHERE table_name = $1 AND key = ANY($2)",
            Params.Text(N.Name(table)),
            Params.TextArray(groupIds?.Select(g => SearchStatisticsSql.GroupPrefix + g) ?? []));
        await using var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var docCount = fields.Select(f => reader.GetInt64((2 * f) - 2)).ToArray();
        var totalLength = fields.Select(f => reader.GetInt64((2 * f) - 1)).ToArray();
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var keyCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keyCounts[reader.GetString(0)] = reader.GetInt64(1);
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var groupRows = groupIds is null ? long.MaxValue : reader.GetInt64(0);
        return new Bm25Statistics(docCount, totalLength, keyCounts, groupRows);
    }

    /// <summary>
    /// The parts of a BM25 full-text statement over the rows matching <paramref name="candidates"/> (a condition on
    /// <c>s.search_keys</c>, which the GIN index answers) in the searched groups: <see cref="Bm25Sql.From"/>, those
    /// rows under the table's alias, with their score; <see cref="Bm25Sql.Score"/>, the score column; and
    /// <see cref="Bm25Sql.Match"/>, the condition keeping the rows that contain a query term under their own
    /// configuration. Every statement ends with the row's uuid and score columns.
    /// </summary>
    /// <remarks>
    /// The score is Lucene's BM25 as the in-memory driver computes it: for each field, then each query term (repeats
    /// included), <c>idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * length / averageLength))</c> with
    /// <c>idf = ln(1 + (N - df + 0.5) / (df + 0.5))</c>, added up in that order. The inverse document frequencies and
    /// average lengths are computed here, in .NET, with the in-memory driver's expressions, and sent as parameters;
    /// PostgreSQL does the rest with the same floating-point operations in the same order (a term a row lacks adds
    /// exactly zero), so scores, and so ties, come out bit for bit the same.
    /// </remarks>
    private Bm25Sql Bm25(
        SqlBuilder b,
        List<(string Config, List<string> Terms)> terms,
        KeywordPlan plan,
        string candidates,
        IReadOnlyList<string>? groupIds,
        string table,
        string alias)
    {
        var statistics = plan.Statistics;
        var fieldCount = SearchIndex.Columns[table].Length;
        var fields = Enumerable.Range(0, fieldCount).ToArray();

        // The (term, field) pairs some row has; any other pair adds nothing to any score.
        var present = terms.SelectMany(q => q.Terms).Distinct(StringComparer.Ordinal)
            .Select(t => (Term: t, Df: fields.Select(f => statistics.DocumentFrequency(t, f)).ToArray()))
            .Where(t => t.Df.Any(df => df > 0))
            .Select((t, i) => (t.Term, t.Df, Index: i + 1, Fields: fields.Where(f => t.Df[f] > 0).ToArray()))
            .ToDictionary(t => t.Term, StringComparer.Ordinal);

        var k1 = b.Param(FullText.K1, NpgsqlDbType.Double);
        var k1Plus1 = b.Param(FullText.K1 + 1, NpgsqlDbType.Double);
        var oneMinusB = b.Param(1 - FullText.B, NpgsqlDbType.Double);
        var bp = b.Param(FullText.B, NpgsqlDbType.Double);
        var average = fields
            .Select(f => statistics.DocCount[f] > 0
                ? b.Param((double)statistics.TotalLength[f] / statistics.DocCount[f], NpgsqlDbType.Double)
                : null)
            .ToArray();
        var idf = new Dictionary<(string Term, int Field), string>();
        foreach (var (term, (_, df, _, termFields)) in present)
        {
            foreach (var f in termFields)
            {
                var n = statistics.DocCount[f];
                idf[(term, f)] = b.Param(Math.Log(1 + ((n - df[f] + 0.5) / (df[f] + 0.5))), NpgsqlDbType.Double);
            }
        }

        var cases = string.Concat(terms.Select(q =>
        {
            var sum = new List<string> { "0::float8" };
            foreach (var f in fields)
            {
                foreach (var term in q.Terms)
                {
                    if (present.TryGetValue(term, out var p) && p.Fields.Contains(f))
                    {
                        var tf = $"x.bm_t{p.Index}_{f}";
                        sum.Add($"coalesce({idf[(term, f)]} * ({tf} * {k1Plus1}) / ({tf} + ({k1} * ({oneMinusB} + "
                            + $"({bp} * x.bm_len{f} / {average[f]})))), 0)");
                    }
                }
            }

            return sum.Count == 1 ? "" : $" WHEN CAST({b.Text(q.Config)} AS regconfig) THEN {string.Join(" + ", sum)}";
        }));

        // Nested derived tables, each fenced with OFFSET 0 so that the planner neither repeats work in every use nor
        // caches it: each candidate's term map is decompressed once (jsonb || '{}' returns it detoasted), its query
        // terms' entries are looked up once, its frequencies and lengths decoded once, and its score computed once.
        var termColumns = string.Join(", ", present.Values.Select(p =>
            $"s1.bm_terms -> {b.Text(p.Term)} AS bm_t{p.Index}"));
        var valueColumns = string.Join(", ", present.Values.SelectMany(p => p.Fields.Select(f =>
                $"{SearchIndex.FrequencySql($"tf.bm_t{p.Index}", f)}::float8 AS bm_t{p.Index}_{f}"))
            .Concat(fields.Select(f => $"tf.search_lengths[{f + 1}]::float8 AS bm_len{f}")));
        var groups = groupIds is null ? "" : $" AND s.group_id = ANY({b.TextArray(groupIds)})";
        var where = $"WHERE ({candidates}){groups}";
        var score = cases.Length == 0 ? "0::float8" : $"CASE x.text_search_config{cases} END";
        string Scored(bool wide) =>
            $"(SELECT {(wide ? "x.*" : "x.uuid")}, {score} AS bm_score FROM ("
            + $"SELECT {(wide ? "tf.*" : "tf.uuid, tf.text_search_config")}, {valueColumns} FROM ("
            + $"SELECT {(wide ? "s1.*" : "s1.uuid, s1.text_search_config, s1.search_lengths")}, {termColumns} FROM ("
            + $"SELECT {(wide ? "s.*" : "s.uuid, s.text_search_config, s.search_lengths")}, "
            + $"s.search_terms || '{{}}'::jsonb AS bm_terms FROM {N.Table(table)} AS s {where} OFFSET 0) AS s1 "
            + "OFFSET 0) AS tf OFFSET 0) AS x OFFSET 0)";
        return new Bm25Sql(
            $"{Scored(wide: true)} AS {alias}",
            $"{alias}.uuid, {alias}.bm_score",
            $"{alias}.bm_score",
            $"{alias}.bm_score > 0",
            (columns, limit) =>
                // Without filters on the records, the best ids are picked from narrow rows first, then joined.
                $"SELECT {columns}, top.uuid, top.bm_score FROM (SELECT sc.uuid, sc.bm_score FROM {Scored(wide: false)} AS sc "
                + $"WHERE sc.bm_score > 0 ORDER BY sc.bm_score DESC, sc.uuid LIMIT {limit}) AS top "
                + $"JOIN {N.Table(table)} AS {alias} ON {alias}.uuid = top.uuid ORDER BY top.bm_score DESC, top.uuid");
    }

    /// <summary>The parts of a BM25 statement; see <see cref="Bm25"/>.</summary>
    /// <param name="From">The scored candidate rows, under the table's alias.</param>
    /// <param name="Columns">The trailing columns every statement selects: the uuid and the score.</param>
    /// <param name="Score">The score column.</param>
    /// <param name="Match">The condition keeping the rows that contain a query term.</param>
    /// <param name="TopK">The statement without filters, given the record columns and the limit.</param>
    private sealed record Bm25Sql(string From, string Columns, string Score, string Match, Func<string, string, string> TopK);

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
        b.Append($"SELECT s.* FROM (SELECT {columns}, 1 - ({embedding} <=> {vector}) / 2 AS score FROM {from}")
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
