// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql.Internal;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>
/// SQL that is part of graphiti-net's schema but outside the EF Core model, for hosts that manage the schema with
/// migrations instead of <c>BuildIndicesAndConstraintsAsync</c>.
/// </summary>
public static class GraphitiSchemaSql
{
    /// <summary>
    /// Creates (or updates) the full-text collection statistics: a <c>search_statistics</c> table, the triggers that
    /// keep it up to date as the searchable tables change, and a fresh count of the rows already there. BM25 ranking
    /// reads it for each field's document count and average length. Idempotent: run it in a migration after the one
    /// that creates the graph tables (<c>migrationBuilder.Sql(GraphitiSchemaSql.SearchStatistics(options))</c>), and
    /// again after any migration that recreates them. <c>BuildIndicesAndConstraintsAsync</c> runs it itself.
    /// </summary>
    /// <param name="options">The same settings as the driver's; the defaults if <see langword="null"/>.</param>
    /// <returns>The SQL script.</returns>
    public static string SearchStatistics(PostgresGraphOptions? options = null)
    {
        options ??= PostgresGraphOptions.Default;
        options.Validate();
        return SearchStatisticsSql.Script(new SqlNames(options));
    }
}

/// <summary>
/// The collection statistics BM25 needs (per field: how many rows have any term, and their total length), kept by
/// statement-level triggers so a search reads a few rows instead of scanning a table. Writers only ever insert delta
/// rows, so concurrent writes never wait on each other; now and then a trigger folds a table's deltas into one row,
/// under a try-lock so it never waits either. The deltas are written in the writer's transaction, so every snapshot
/// sees statistics that match exactly the rows it sees.
/// </summary>
internal static class SearchStatisticsSql
{
    /// <summary>The most searchable fields any table has.</summary>
    public const int MaxFields = 3;

    public const string Table = "search_statistics";

    private static readonly string[] Operations = ["insert", "update", "delete"];

    /// <summary>The number of triggers a complete installation has.</summary>
    public static int TriggerCount => SearchIndex.Columns.Count * Operations.Length;

    public static string TriggerName(string operation) => "graphiti_search_statistics_" + operation;

    /// <summary>The advisory lock key that serialises folding a table's deltas.</summary>
    public static string LockKey(SqlNames names, string table) =>
        $"hashtext('{names.Schema}.{names.Name(table)} search statistics')";

    public static string Script(SqlNames names)
    {
        var stats = names.Table(Table);
        var docs = string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"docs{f}"));
        var lengths = string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"length{f}"));
        var sql = new Builder();
        sql.Append($"CREATE TABLE IF NOT EXISTS {stats} (table_name text NOT NULL, ")
            .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"docs{f} bigint NOT NULL DEFAULT 0")))
            .Append(", ")
            .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"length{f} bigint NOT NULL DEFAULT 0")))
            .Append(");\n")
            .Append($"CREATE INDEX IF NOT EXISTS {SqlNames.Quote($"ix_{names.Name(Table)}_table_name")} ")
            .Append($"ON {stats} (table_name);\n");

        foreach (var operation in Operations)
        {
            var function = $"{names.QuotedSchema}.{SqlNames.Quote($"{names.Prefix}search_statistics_{operation}")}";
            var body = new Builder().Append("BEGIN\n");
            if (operation != "delete")
            {
                body.Append(Delta(stats, "new_rows", "", docs, lengths));
            }

            if (operation != "insert")
            {
                body.Append(Delta(stats, "old_rows", "-", docs, lengths));
            }

            // Fold the deltas now and then, so a search sums only a few rows.
            body.Append("IF random() < 0.03 AND pg_try_advisory_xact_lock(")
                .Append("hashtext(TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME || ' search statistics')) THEN\n")
                .Append($"WITH d AS (DELETE FROM {stats} WHERE table_name = TG_TABLE_NAME RETURNING *) ")
                .Append($"INSERT INTO {stats} (table_name, {docs}, {lengths}) SELECT TG_TABLE_NAME, ")
                .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"sum(d.docs{f})")))
                .Append(", ")
                .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"sum(d.length{f})")))
                .Append(" FROM d HAVING count(*) > 0;\nEND IF;\nRETURN NULL;\nEND;");
            sql.Append($"CREATE OR REPLACE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $graphiti$\n")
                .Append(body.ToString())
                .Append("\n$graphiti$;\n");

            foreach (var table in SearchIndex.Columns.Keys)
            {
                var transition = operation switch
                {
                    "insert" => "NEW TABLE AS new_rows",
                    "update" => "OLD TABLE AS old_rows NEW TABLE AS new_rows",
                    _ => "OLD TABLE AS old_rows",
                };
                sql.Append($"DROP TRIGGER IF EXISTS {TriggerName(operation)} ON {names.Table(table)};\n")
                    .Append($"CREATE TRIGGER {TriggerName(operation)} AFTER {operation.ToUpperInvariant()} ")
                    .Append($"ON {names.Table(table)} REFERENCING {transition} FOR EACH STATEMENT ")
                    .Append($"EXECUTE FUNCTION {function}();\n");
            }
        }

        // Count what is already there, replacing every delta, in one snapshot.
        foreach (var table in SearchIndex.Columns.Keys)
        {
            sql.Append($"SELECT pg_advisory_xact_lock({LockKey(names, table)});\n")
                .Append($"WITH d AS (DELETE FROM {stats} WHERE table_name = '{names.Name(table)}') ")
                .Append($"INSERT INTO {stats} (table_name, {docs}, {lengths}) SELECT '{names.Name(table)}', ")
                .Append(Sums("r", ""))
                .Append($" FROM {names.Table(table)} AS r;\n");
        }

        return sql.ToString();
    }

    /// <summary>Concatenates SQL text (no formatting, so no culture).</summary>
    private sealed class Builder
    {
        private readonly List<string> _parts = [];

        public Builder Append(string part)
        {
            _parts.Add(part);
            return this;
        }

        public override string ToString() => string.Concat(_parts);
    }

    private static string Delta(string stats, string rows, string sign, string docs, string lengths) =>
        $"INSERT INTO {stats} (table_name, {docs}, {lengths}) SELECT TG_TABLE_NAME, {Sums("r", sign)} "
        + $"FROM {rows} AS r HAVING count(r.search_lengths) > 0;\n";

    private static string Sums(string alias, string sign) =>
        string.Join(", ", Enumerable.Range(1, MaxFields).Select(f =>
            $"{sign}count(*) FILTER (WHERE {alias}.search_lengths[{f}] > 0)"))
        + ", "
        + string.Join(", ", Enumerable.Range(1, MaxFields).Select(f =>
            $"{sign}coalesce(sum({alias}.search_lengths[{f}]), 0)"));
}
