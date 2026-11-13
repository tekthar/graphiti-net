// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// Creates graphiti-net's schema from the EF Core model, so the DDL is exactly what an EF Core migration of the same
/// model would produce. Idempotent: only missing tables are created (with their indexes), plus columns, indexes and
/// full-text columns a newer version added to existing tables, whose rows are then indexed. Runs in one transaction under an
/// advisory lock, so concurrent callers do not race.
/// </summary>
internal static class SchemaBuilder
{
    public static async Task EnsureSchemaAsync(DriverCore core, bool deleteExisting, CancellationToken ct)
    {
        await using var context = core.CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();
        var schema = core.Options.Schema;
        var names = core.Names;

        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // One lock for every schema: CREATE EXTENSION itself is not safe to run concurrently.
            await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtext('graphiti-net schema'))", ct)
                .ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, VectorSupport.CreateExtensionSql, ct).ConfigureAwait(false);
            }
            catch (PostgresException e) when (VectorSupport.Unavailable(connection, e) is { } unavailable)
            {
                throw unavailable;
            }

            // The graph tables of the model: what GraphitiSchema.Tables lists, so the two cannot drift apart.
            var ours = GraphitiSchema.ModelTables(model, schema).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            if (deleteExisting)
            {
                var tables = string.Join(
                    ", ",
                    ours.Append(names.Name(SearchStatisticsSql.Table))
                        .Select(t => $"{names.QuotedSchema}.{SqlNames.Quote(t)}"));
                await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {tables} CASCADE", ct).ConfigureAwait(false);
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand(
                "SELECT table_name::text FROM information_schema.tables WHERE table_schema = $1",
                connection))
            {
                command.Parameters.Add(Params.Text(schema));
                existing.UnionWith(await DriverCore.ReadListAsync(command, r => r.GetString(0), ct).ConfigureAwait(false));
            }

            var existingIndexes = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand(
                "SELECT indexname::text FROM pg_indexes WHERE schemaname = $1",
                connection))
            {
                command.Parameters.Add(Params.Text(schema));
                existingIndexes.UnionWith(
                    await DriverCore.ReadListAsync(command, r => r.GetString(0), ct).ConfigureAwait(false));
            }

            var created = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<MigrationOperation>();
            foreach (var operation in operations)
            {
                switch (operation)
                {
                    case EnsureSchemaOperation ensure when ensure.Name == schema:
                        selected.Add(ensure);
                        break;
                    case CreateTableOperation table
                        when table.Schema == schema && ours.Contains(table.Name) && !existing.Contains(table.Name):
                        selected.Add(table);
                        created.Add(table.Name);
                        break;
                    // Indexes of new tables, and indexes a newer version added to existing tables.
                    case CreateIndexOperation index when index.Schema == schema && ours.Contains(index.Table)
                        && (created.Contains(index.Table) || !existingIndexes.Contains(index.Name)):
                        selected.Add(index);
                        break;
                }
            }

            // Tables from before BM25 ranking: swap the generated tsvector for the term columns, filled in below.
            var searchColumns = new HashSet<(string Table, string Column)>();
            await using (var command = new NpgsqlCommand(
                "SELECT table_name::text, column_name::text FROM information_schema.columns WHERE table_schema = $1 "
                + "AND column_name IN ('search_keys', 'search_vector')",
                connection))
            {
                command.Parameters.Add(Params.Text(schema));
                searchColumns.UnionWith(await DriverCore.ReadListAsync(command, r => (r.GetString(0), r.GetString(1)), ct)
                    .ConfigureAwait(false));
            }

            var searchable = SearchIndex.Columns.Keys.Where(t => existing.Contains(names.Name(t))).ToList();
            foreach (var table in searchable)
            {
                if (searchColumns.Contains((names.Name(table), "search_vector")))
                {
                    await ExecuteAsync(connection, $"ALTER TABLE {names.Table(table)} DROP COLUMN search_vector", ct)
                        .ConfigureAwait(false);
                }

                if (!searchColumns.Contains((names.Name(table), "search_keys")))
                {
                    await ExecuteAsync(
                        connection,
                        $"ALTER TABLE {names.Table(table)} ADD COLUMN IF NOT EXISTS search_terms jsonb, "
                        + "ADD COLUMN IF NOT EXISTS search_keys text[], ADD COLUMN IF NOT EXISTS search_lengths integer[]; "
                        + $"UPDATE {names.Table(table)} SET search_lengths = NULL",
                        ct).ConfigureAwait(false);
                }
            }

            // Columns a newer version added to existing tables (such as episodes.idempotency_key), before the indexes
            // that use them.
            var existingColumns = new HashSet<(string Table, string Column)>();
            await using (var command = new NpgsqlCommand(
                "SELECT table_name::text, column_name::text FROM information_schema.columns WHERE table_schema = $1",
                connection))
            {
                command.Parameters.Add(Params.Text(schema));
                existingColumns.UnionWith(await DriverCore.ReadListAsync(
                    command, r => (r.GetString(0), r.GetString(1)), ct).ConfigureAwait(false));
            }

            var addedColumns = operations.OfType<CreateTableOperation>()
                .Where(t => t.Schema == schema && ours.Contains(t.Name) && existing.Contains(t.Name))
                .SelectMany(t => t.Columns)
                .Where(c => !existingColumns.Contains((c.Table, c.Name)))
                .Cast<MigrationOperation>();
            var firstIndex = selected.FindIndex(o => o is CreateIndexOperation);
            selected.InsertRange(firstIndex < 0 ? selected.Count : firstIndex, addedColumns);

            foreach (var command in sqlGenerator.Generate(selected, model))
            {
                await ExecuteAsync(connection, command.CommandText, ct).ConfigureAwait(false);
            }

            foreach (var table in searchable)
            {
                await IndexUnindexedRowsAsync(core, connection, table, ct).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, SearchStatisticsSql.Script(names), ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes the full-text columns of rows that have none: rows from before BM25 ranking, or rows written outside
    /// the driver.
    /// </summary>
    private static async Task IndexUnindexedRowsAsync(
        DriverCore core,
        NpgsqlConnection connection,
        string table,
        CancellationToken ct)
    {
        var columns = SearchIndex.Columns[table];
        var t = core.Names.Table(table);
        var select = $"SELECT uuid, group_id, {string.Join(", ", columns)} FROM {t} "
            + $"WHERE search_lengths IS NULL ORDER BY uuid LIMIT {core.Options.BulkBatchSize}";
        while (true)
        {
            List<(string Uuid, string GroupId, string?[] Fields)> rows;
            await using (var command = new NpgsqlCommand(select, connection))
            {
                rows = await DriverCore.ReadListAsync(
                    command,
                    r => (r.GetString(0), r.GetString(1),
                        Enumerable.Range(2, columns.Length).Select(i => r.IsDBNull(i) ? null : r.GetString(i)).ToArray()),
                    ct).ConfigureAwait(false);
            }

            if (rows.Count == 0)
            {
                return;
            }

            var documents = await SearchIndex.AnalyzeAsync(
                connection,
                [.. rows.Select(r => (core.Options.ResolveTextSearchConfig(r.GroupId), r.Fields))],
                ct).ConfigureAwait(false);
            await using var batch = new NpgsqlBatch(connection);
            for (var i = 0; i < rows.Count; i++)
            {
                DriverCore.AddCommand(
                    batch,
                    $"UPDATE {t} SET search_terms = $2, search_keys = $3, search_lengths = $4 WHERE uuid = $1",
                    Params.Text(rows[i].Uuid), Params.Jsonb(documents[i].Terms), Params.TextArray(documents[i].Keys),
                    Params.IntArray(documents[i].Lengths));
            }

            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
