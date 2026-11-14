// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using GraphitiNet.Driver.PostgreSql.Entities;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// Shared plumbing for the operations classes: creating contexts, borrowing their pooled Npgsql connection, and the
/// batched upsert and delete statements (EF Core has no upsert, and deletes cascade across tables).
/// </summary>
internal sealed partial class DriverCore(Func<DbContext> contextFactory, PostgresGraphOptions options)
{
    private volatile bool _modelChecked;
    private volatile bool _searchStatisticsChecked;

    public PostgresGraphOptions Options { get; } = options;

    public SqlNames Names { get; } = new(options);

    /// <summary>Creates a context; checks once that its model holds the graph tables in the configured schema.</summary>
    public DbContext CreateContext()
    {
        var context = contextFactory() ?? throw new InvalidOperationException("The DbContext factory returned null.");
        if (!_modelChecked)
        {
            CheckModel(context);
            _modelChecked = true;
        }

        return context;
    }

    /// <summary>Runs a LINQ query on a fresh context.</summary>
    public async Task<T> QueryAsync<T>(Func<DbContext, Task<T>> query)
    {
        await using var context = CreateContext();
        return await query(context).ConfigureAwait(false);
    }

    /// <summary>Runs raw ADO.NET work on a fresh context's connection.</summary>
    public async Task<T> WithConnectionAsync<T>(
        Func<NpgsqlConnection, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work((NpgsqlConnection)context.Database.GetDbConnection()).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public Task WithConnectionAsync(Func<NpgsqlConnection, Task> work, CancellationToken cancellationToken) =>
        WithConnectionAsync<bool>(
            async connection =>
            {
                await work(connection).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>Runs raw ADO.NET work in one transaction.</summary>
    public Task InTransactionAsync(Func<NpgsqlConnection, Task> work, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            async connection =>
            {
                await using var transaction =
                    await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await work(connection).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlBatch batch, CancellationToken ct)
    {
        if (batch.BatchCommands.Count == 0)
        {
            return;
        }

        batch.Connection = connection;
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ----- Upserts -----

    /// <summary>
    /// Saves items with <c>INSERT ... ON CONFLICT (uuid) DO UPDATE</c>, <see cref="PostgresGraphOptions.BulkBatchSize"/>
    /// rows per statement, so that statement-level triggers (the full-text statistics) run once per batch rather than
    /// once per row. <paramref name="sql"/> is the statement for one row; its first parameter is the uuid. Of items
    /// sharing a uuid, the last is saved, as saving them one after another would leave it.
    /// </summary>
    public Task UpsertAsync<T>(
        NpgsqlConnection connection,
        IReadOnlyList<T> items,
        string sql,
        Func<T, NpgsqlParameter[]> parameters,
        CancellationToken ct) =>
        UpsertAsync(connection, items, sql, (item, _) => parameters(item), ct);

    /// <summary>As above, with the item's index in <paramref name="items"/>.</summary>
    public async Task UpsertAsync<T>(
        NpgsqlConnection connection,
        IReadOnlyList<T> items,
        string sql,
        Func<T, int, NpgsqlParameter[]> parameters,
        CancellationToken ct)
    {
        var rows = items.Select((item, i) => parameters(item, i)).ToList();
        var last = new Dictionary<object, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            last[rows[i][0].Value!] = i;
        }

        rows = [.. rows.Where((row, i) => last[row[0].Value!] == i)];
        if (rows.Count == 0)
        {
            return;
        }

        // The single-row statement split around its VALUES list, whose $n placeholders are renumbered for each row.
        const string Values = " VALUES (";
        const string Conflict = ") ON CONFLICT";
        var valuesAt = sql.IndexOf(Values, StringComparison.Ordinal);
        var conflictAt = sql.IndexOf(Conflict, StringComparison.Ordinal);
        var row = sql[(valuesAt + Values.Length)..conflictAt];
        var width = rows[0].Length;
        var perStatement = Math.Max(1, Math.Min(Options.BulkBatchSize, ushort.MaxValue / width));
        for (var start = 0; start < rows.Count; start += perStatement)
        {
            var count = Math.Min(perStatement, rows.Count - start);
            var text = new System.Text.StringBuilder(sql, 0, valuesAt, sql.Length * count).Append(" VALUES ");
            await using var command = new NpgsqlCommand { Connection = connection };
            for (var r = 0; r < count; r++)
            {
                text.Append(r == 0 ? "(" : ", (").Append(Renumber(row, r * width)).Append(')');
                command.Parameters.AddRange(rows[start + r]);
            }

            command.CommandText = text.Append(sql, conflictAt + 1, sql.Length - conflictAt - 1).ToString();
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static string Renumber(string row, int offset) =>
        offset == 0
            ? row
            : Placeholders().Replace(row, m => "$" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + offset)
                .ToString(CultureInfo.InvariantCulture));

    [System.Text.RegularExpressions.GeneratedRegex(@"\$(\d+)")]
    private static partial System.Text.RegularExpressions.Regex Placeholders();

    private string UpsertSql(string table, params string[] columns)
    {
        var placeholders = string.Join(", ", columns.Select((c, i) => Placeholder(c, i + 1)));
        var updates = string.Join(", ", columns.Skip(1).Select(c => $"{c} = EXCLUDED.{c}"));
        return $"INSERT INTO {Names.Table(table)} ({string.Join(", ", columns)}) VALUES ({placeholders}) "
            + $"ON CONFLICT (uuid) DO UPDATE SET {updates}";

        static string Placeholder(string column, int index) => column switch
        {
            _ when column.EndsWith("_embedding", StringComparison.Ordinal) => $"CAST(${index} AS vector)",
            "text_search_config" => $"CAST(${index} AS regconfig)",
            _ => "$" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public async Task UpsertEpisodesAsync(NpgsqlConnection c, IReadOnlyList<EpisodicNode> items, CancellationToken ct)
    {
        var search = await AnalyzeAsync(c, items, n => n.GroupId, SearchIndex.Fields, ct).ConfigureAwait(false);
        await UpsertAsync(
            c,
            items,
            UpsertSql(SqlNames.Episodes, "uuid", "group_id", "name", "labels", "created_at", "source",
                "source_description", "content", "valid_at", "entity_edges", "episode_metadata", "idempotency_key",
                "text_search_config", "search_terms", "search_keys", "search_lengths"),
            (n, i) =>
            [
                Params.Text(n.Uuid), Params.Text(n.GroupId), Params.Text(n.Name), Params.TextArray(n.Labels),
                Params.Time(n.CreatedAt), Params.Text(n.Source.ToStorageName()), Params.Text(n.SourceDescription),
                Params.Text(n.Content), Params.Time(n.ValidAt), Params.TextArray(n.EntityEdges),
                Params.Jsonb(JsonValues.SerializeNullable(n.EpisodeMetadata)), Params.Text(n.IdempotencyKey),
                Config(n.GroupId),
                Params.Jsonb(search[i].Terms), Params.TextArray(search[i].Keys), Params.IntArray(search[i].Lengths),
            ],
            ct).ConfigureAwait(false);
    }

    public async Task UpsertEntitiesAsync(NpgsqlConnection c, IReadOnlyList<EntityNode> items, CancellationToken ct)
    {
        var search = await AnalyzeAsync(c, items, n => n.GroupId, SearchIndex.Fields, ct).ConfigureAwait(false);
        await UpsertAsync(
            c,
            items,
            UpsertSql(SqlNames.Entities, "uuid", "group_id", "name", "labels", "created_at", "summary", "attributes",
                "name_embedding", "text_search_config", "search_terms", "search_keys", "search_lengths"),
            (n, i) =>
            [
                // Every entity carries the Entity label, as upstream's Neo4j nodes do.
                Params.Text(n.Uuid), Params.Text(n.GroupId), Params.Text(n.Name), Params.TextArray(EntityLabels(n)),
                Params.Time(n.CreatedAt), Params.Text(n.Summary), Params.Jsonb(JsonValues.Serialize(n.Attributes)),
                Params.Embedding(n.NameEmbedding), Config(n.GroupId),
                Params.Jsonb(search[i].Terms), Params.TextArray(search[i].Keys), Params.IntArray(search[i].Lengths),
            ],
            ct).ConfigureAwait(false);
    }

    public async Task UpsertCommunitiesAsync(NpgsqlConnection c, IReadOnlyList<CommunityNode> items, CancellationToken ct)
    {
        var search = await AnalyzeAsync(c, items, n => n.GroupId, SearchIndex.Fields, ct).ConfigureAwait(false);
        await UpsertAsync(
            c,
            items,
            UpsertSql(SqlNames.Communities, "uuid", "group_id", "name", "labels", "created_at", "summary",
                "name_embedding", "text_search_config", "search_terms", "search_keys", "search_lengths"),
            (n, i) =>
            [
                Params.Text(n.Uuid), Params.Text(n.GroupId), Params.Text(n.Name), Params.TextArray(n.Labels),
                Params.Time(n.CreatedAt), Params.Text(n.Summary), Params.Embedding(n.NameEmbedding),
                Config(n.GroupId), Params.Jsonb(search[i].Terms), Params.TextArray(search[i].Keys), Params.IntArray(search[i].Lengths),
            ],
            ct).ConfigureAwait(false);
    }

    public Task UpsertSagasAsync(NpgsqlConnection c, IReadOnlyList<SagaNode> items, CancellationToken ct) =>
        UpsertAsync(
            c,
            items,
            UpsertSql(SqlNames.Sagas, "uuid", "group_id", "name", "labels", "created_at", "summary",
                "first_episode_uuid", "last_episode_uuid", "last_summarized_at", "last_summarized_episode_valid_at"),
            n =>
            [
                Params.Text(n.Uuid), Params.Text(n.GroupId), Params.Text(n.Name), Params.TextArray(n.Labels),
                Params.Time(n.CreatedAt), Params.Text(n.Summary), Params.Text(n.FirstEpisodeUuid),
                Params.Text(n.LastEpisodeUuid), Params.Time(n.LastSummarizedAt),
                Params.Time(n.LastSummarizedEpisodeValidAt),
            ],
            ct);

    public async Task UpsertEntityEdgesAsync(NpgsqlConnection c, IReadOnlyList<EntityEdge> items, CancellationToken ct)
    {
        var search = await AnalyzeAsync(c, items, e => e.GroupId, SearchIndex.Fields, ct).ConfigureAwait(false);
        await UpsertAsync(
            c,
            items,
            UpsertSql(SqlNames.EntityEdges, "uuid", "group_id", "source_node_uuid", "target_node_uuid", "created_at",
                "name", "fact", "episodes", "expired_at", "valid_at", "invalid_at", "reference_time", "attributes",
                "fact_embedding", "text_search_config", "search_terms", "search_keys", "search_lengths"),
            (e, i) =>
            [
                Params.Text(e.Uuid), Params.Text(e.GroupId), Params.Text(e.SourceNodeUuid),
                Params.Text(e.TargetNodeUuid), Params.Time(e.CreatedAt), Params.Text(e.Name), Params.Text(e.Fact),
                Params.TextArray(e.Episodes), Params.Time(e.ExpiredAt), Params.Time(e.ValidAt),
                Params.Time(e.InvalidAt), Params.Time(e.ReferenceTime),
                Params.Jsonb(JsonValues.Serialize(e.Attributes)), Params.Embedding(e.FactEmbedding),
                Config(e.GroupId), Params.Jsonb(search[i].Terms), Params.TextArray(search[i].Keys), Params.IntArray(search[i].Lengths),
            ],
            ct).ConfigureAwait(false);
    }

    /// <summary>Upserts plain edges (episodic, community, has-episode, next-episode) into a table.</summary>
    public Task UpsertPlainEdgesAsync<TEdge>(
        NpgsqlConnection c,
        string table,
        IReadOnlyList<TEdge> items,
        CancellationToken ct)
        where TEdge : Edge =>
        UpsertAsync(
            c,
            items,
            UpsertSql(table, "uuid", "group_id", "source_node_uuid", "target_node_uuid", "created_at"),
            e =>
            [
                Params.Text(e.Uuid), Params.Text(e.GroupId), Params.Text(e.SourceNodeUuid),
                Params.Text(e.TargetNodeUuid), Params.Time(e.CreatedAt),
            ],
            ct);

    private Task<SearchDocument[]> AnalyzeAsync<T>(
        NpgsqlConnection connection,
        IReadOnlyList<T> items,
        Func<T, string> groupOf,
        Func<T, string?[]> fields,
        CancellationToken ct) =>
        SearchIndex.AnalyzeAsync(
            connection,
            [.. items.Select(item => (Options.ResolveTextSearchConfig(groupOf(item)), fields(item)))],
            ct);

    private NpgsqlParameter Config(string groupId) => Params.Text(Options.ResolveTextSearchConfig(groupId));

    private static string[] EntityLabels(EntityNode node) =>
        node.Labels.Contains("Entity", StringComparer.Ordinal) ? [.. node.Labels.Distinct()] : ["Entity", .. node.Labels.Distinct()];

    // ----- Deletes -----

    /// <summary>
    /// Deletes nodes by id together with every edge that touches them, in every edge table (upstream's
    /// <c>DETACH DELETE</c>). One round trip, one implicit transaction.
    /// </summary>
    public async Task DeleteNodesAsync(
        NpgsqlConnection connection,
        string nodeTable,
        IReadOnlyList<string> uuids,
        CancellationToken ct)
    {
        if (uuids.Count == 0)
        {
            return;
        }

        await using var batch = new NpgsqlBatch(connection);
        foreach (var edgeTable in SqlNames.EdgeTables)
        {
            AddCommand(batch,
                $"DELETE FROM {Names.Table(edgeTable)} WHERE source_node_uuid = ANY($1) OR target_node_uuid = ANY($1)",
                Params.TextArray(uuids));
        }

        AddCommand(batch, $"DELETE FROM {Names.Table(nodeTable)} WHERE uuid = ANY($1)", Params.TextArray(uuids));
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes every node of a table in some groups (or all groups), with the edges that touch them.</summary>
    public async Task DeleteNodesByGroupAsync(
        NpgsqlConnection connection,
        string nodeTable,
        IReadOnlyList<string>? groupIds,
        CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection);
        var nodes = groupIds is null
            ? $"SELECT uuid FROM {Names.Table(nodeTable)}"
            : $"SELECT uuid FROM {Names.Table(nodeTable)} WHERE group_id = ANY($1)";
        foreach (var edgeTable in SqlNames.EdgeTables)
        {
            var sql = $"DELETE FROM {Names.Table(edgeTable)} WHERE source_node_uuid IN ({nodes}) "
                + $"OR target_node_uuid IN ({nodes})";
            AddCommand(batch, sql, groupIds is null ? [] : [Params.TextArray(groupIds)]);
        }

        AddCommand(
            batch,
            groupIds is null
                ? $"DELETE FROM {Names.Table(nodeTable)}"
                : $"DELETE FROM {Names.Table(nodeTable)} WHERE group_id = ANY($1)",
            groupIds is null ? [] : [Params.TextArray(groupIds)]);
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteEdgesAsync(
        NpgsqlConnection connection,
        string edgeTable,
        IReadOnlyList<string> uuids,
        CancellationToken ct)
    {
        if (uuids.Count == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            $"DELETE FROM {Names.Table(edgeTable)} WHERE uuid = ANY($1)",
            connection);
        command.Parameters.Add(Params.TextArray(uuids));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static void AddCommand(NpgsqlBatch batch, string sql, params NpgsqlParameter[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        batch.BatchCommands.Add(command);
    }

    // ----- Reads -----

    /// <summary>Runs a query and maps each row.</summary>
    public static async Task<List<T>> ReadListAsync<T>(
        NpgsqlCommand command,
        Func<NpgsqlDataReader, T> map,
        CancellationToken ct)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(map(reader));
        }

        return result;
    }

    /// <summary>
    /// Checks once that the triggers keeping the full-text statistics are installed, so ranking never runs on stale
    /// statistics silently (a host that manages the schema with migrations must add them).
    /// </summary>
    public async Task CheckSearchStatisticsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (_searchStatisticsChecked)
        {
            return;
        }

        // The term counts table came with 0.2; a schema whose statistics script ran under 0.1 lacks it.
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int, to_regclass($3) IS NOT NULL FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid "
            + "JOIN pg_namespace s ON s.oid = c.relnamespace WHERE s.nspname = $1 AND c.relname = ANY($2) "
            + "AND t.tgname LIKE 'graphiti_search_statistics_%'",
            connection);
        command.Parameters.Add(Params.Text(Options.Schema));
        command.Parameters.Add(Params.TextArray(SearchIndex.Columns.Keys.Select(Names.Name)));
        command.Parameters.Add(Params.Text(Names.Table(SearchStatisticsSql.TermCountsTable)));
        int installed;
        bool counted;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            installed = reader.GetInt32(0);
            counted = reader.GetBoolean(1);
        }

        if (installed < SearchStatisticsSql.TriggerCount || !counted)
        {
            throw new InvalidOperationException(
                "Full-text search needs graphiti-net's search statistics tables and triggers, which are missing from schema "
                + $"\"{Options.Schema}\" or date from an earlier version. Call GraphOps.BuildIndicesAndConstraintsAsync(), or, if you manage the schema "
                + "with EF Core migrations, add migrationBuilder.Sql(GraphitiSchemaSql.SearchStatistics(options)) to a "
                + "migration.");
        }

        _searchStatisticsChecked = true;
    }

    private void CheckModel(DbContext context)
    {
        var entityType = context.Model.FindEntityType(typeof(EntityNodeRecord))
            ?? throw new InvalidOperationException(
                "The DbContext does not include graphiti-net's tables. Call modelBuilder.AddGraphiti(options) in "
                + "OnModelCreating, or use GraphitiDbContext.");
        var schema = entityType.GetSchema();
        var table = entityType.GetTableName();
        if (!string.Equals(schema, Options.Schema, StringComparison.Ordinal)
            || !string.Equals(table, Names.Name(SqlNames.Entities), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The DbContext maps graphiti-net's entities to \"{schema}\".\"{table}\", but the driver was "
                + $"configured with \"{Options.Schema}\".\"{Names.Name(SqlNames.Entities)}\". Pass the same "
                + "PostgresGraphOptions (schema and table prefix) to AddGraphiti and PostgresDriver.");
        }

        if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new InvalidOperationException("PostgresDriver needs a DbContext configured with UseNpgsql.");
        }
    }
}
