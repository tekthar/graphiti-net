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
    /// Creates (or updates) the full-text collection statistics: the <c>search_statistics</c> and
    /// <c>search_term_counts</c> tables, the triggers that keep them up to date as the searchable tables change, and a
    /// fresh count of the rows already there. BM25 ranking reads them for each field's document count and average
    /// length and each term's document frequency. It also converts, once, the keyword index of rows written by
    /// graphiti-net 0.1, rewriting each such row (see docs/postgresql.md, "Upgrading from 0.1"). Idempotent: run it in
    /// a migration after the one that
    /// creates the graph tables (<c>migrationBuilder.Sql(GraphitiSchemaSql.SearchStatistics(options))</c>), and again
    /// after any migration that recreates them. <c>BuildIndicesAndConstraintsAsync</c> runs it itself.
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
/// The collection statistics BM25 needs, kept by statement-level triggers so a search reads a few rows instead of
/// scanning a table: per field, how many rows have any term and their total length (<c>search_statistics</c>); and
/// per <c>search_keys</c> key (a term in a field, in one impact class), how many rows hold it, and how many rows each
/// text search configuration and each group has (<c>search_term_counts</c>). For the first, writers insert delta
/// rows, which a trigger folds now and then under a try-lock; for the second, each writer adds its changes to the
/// key's row in a lane (see <see cref="Lanes"/>) its transaction holds alone. Either way concurrent writers never wait
/// on each other. Everything is written in the writer's transaction, so every snapshot sees statistics that match
/// exactly the rows it sees.
/// </summary>
internal static class SearchStatisticsSql
{
    /// <summary>The most searchable fields any table has.</summary>
    public const int MaxFields = 3;

    public const string Table = "search_statistics";

    /// <summary>The table of row counts per <c>search_keys</c> key.</summary>
    public const string TermCountsTable = "search_term_counts";

    /// <summary>
    /// In <see cref="TermCountsTable"/>, the key of each table's marker row, whose count is the table's
    /// <c>relfilenode</c> when its keys were last counted from scratch; a new file (the table was recreated or
    /// truncated) means they must be counted again.
    /// </summary>
    public const string CountedMarker = "";

    /// <summary>In <see cref="TermCountsTable"/>, the prefix of the keys counting the rows of each text search configuration.</summary>
    public const string ConfigPrefix = "#";

    /// <summary>In <see cref="TermCountsTable"/>, the prefix of the keys counting the rows of each group.</summary>
    public const string GroupPrefix = "@";

    /// <summary>The statistics tables, for dropping and truncating.</summary>
    public static readonly IReadOnlyList<string> Tables = [Table, TermCountsTable];

    private static readonly string[] Operations = ["insert", "update", "delete"];

    /// <summary>The number of triggers a complete installation has.</summary>
    public static int TriggerCount => SearchIndex.Columns.Count * Operations.Length;

    public static string TriggerName(string operation) => "graphiti_search_statistics_" + operation;

    /// <summary>
    /// The number of lanes of <see cref="TermCountsTable"/>: each key has a row per lane that some transaction wrote
    /// in, and a transaction writes in one lane at a time, alone, so concurrent writers never update the same row.
    /// </summary>
    public const int Lanes = 8;

    /// <summary>The advisory lock key (with the lane as second key) of a table's lanes, from a SQL text expression
    /// giving <c>schema.table</c>.</summary>
    public static string LaneLock(string qualifiedName) => $"hashtext({qualifiedName} || ' search term counts')";

    /// <summary>The advisory lock key that serialises folding a table's deltas.</summary>
    public static string LockKey(SqlNames names, string table) =>
        $"hashtext('{names.Schema}.{names.Name(table)} search statistics')";

    public static string Script(SqlNames names)
    {
        var stats = names.Table(Table);
        var terms = names.Table(TermCountsTable);
        var docs = string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"docs{f}"));
        var lengths = string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"length{f}"));
        var sql = new Builder();
        sql.Append($"CREATE TABLE IF NOT EXISTS {stats} (table_name text NOT NULL, ")
            .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"docs{f} bigint NOT NULL DEFAULT 0")))
            .Append(", ")
            .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"length{f} bigint NOT NULL DEFAULT 0")))
            .Append(");\n")
            .Append($"CREATE INDEX IF NOT EXISTS {SqlNames.Quote($"ix_{names.Name(Table)}_table_name")} ")
            .Append($"ON {stats} (table_name);\n")
            .Append($"CREATE TABLE IF NOT EXISTS {terms} (table_name text COLLATE \"C\" NOT NULL, ")
            .Append("key text COLLATE \"C\" NOT NULL, lane smallint NOT NULL DEFAULT 0, docs bigint NOT NULL) ")
            .Append("WITH (fillfactor = 70);\n")
            .Append($"CREATE UNIQUE INDEX IF NOT EXISTS {SqlNames.Quote($"ux_{names.Name(TermCountsTable)}_table_name_key_lane")} ")
            .Append($"ON {terms} (table_name, key, lane);\n");

        foreach (var operation in Operations)
        {
            var function = $"{names.QuotedSchema}.{SqlNames.Quote($"{names.Prefix}search_statistics_{operation}")}";
            var body = new Builder().Append("DECLARE slot int;\nBEGIN\n");
            var keyRows = new List<string>();
            if (operation != "delete")
            {
                body.Append(Delta(stats, "new_rows", "", docs, lengths));
                keyRows.Add(KeyRows("new_rows", "1"));
            }

            if (operation != "insert")
            {
                body.Append(Delta(stats, "old_rows", "-", docs, lengths));
                keyRows.Add(KeyRows("old_rows", "-1"));
            }

            // Fold the statistics' deltas now and then, so a search sums only a few rows.
            body.Append("IF random() < 0.03 AND pg_try_advisory_xact_lock(")
                .Append("hashtext(TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME || ' search statistics')) THEN\n")
                .Append($"WITH d AS (DELETE FROM {stats} WHERE table_name = TG_TABLE_NAME::text RETURNING *) ")
                .Append($"INSERT INTO {stats} (table_name, {docs}, {lengths}) SELECT TG_TABLE_NAME, ")
                .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"sum(d.docs{f})")))
                .Append(", ")
                .Append(string.Join(", ", Enumerable.Range(1, MaxFields).Select(f => $"sum(d.length{f})")))
                .Append(" FROM d HAVING count(*) > 0;\n")
                .Append("END IF;\n");

            // Add this statement's change per key (keys whose count it leaves as it was, as an unchanged re-save does,
            // are left alone) to the key's row in a lane this transaction holds alone, so no two writers ever update
            // the same row: no waiting, no deadlock. A transaction keeps the first lane it gets; with every lane taken,
            // it waits for one (holding none, so it cannot be part of a cycle).
            body.Append("slot := NULL;\n")
                .Append($"FOR i IN 0..{Lanes - 1} LOOP\n")
                .Append($"IF pg_try_advisory_xact_lock({LaneLock("TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME")}, i) THEN\n")
                .Append("slot := i;\nEXIT;\nEND IF;\nEND LOOP;\n")
                .Append("IF slot IS NULL THEN\n")
                .Append($"slot := pg_backend_pid() % {Lanes};\n")
                .Append($"PERFORM pg_advisory_xact_lock({LaneLock("TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME")}, slot);\n")
                .Append("END IF;\n")
                .Append($"INSERT INTO {terms} AS c (table_name, key, lane, docs) SELECT TG_TABLE_NAME, k.key, slot, sum(k.docs) ")
                .Append($"FROM ({string.Join(" UNION ALL ", keyRows)}) AS k GROUP BY k.key HAVING sum(k.docs) <> 0 ")
                .Append("ORDER BY k.key ON CONFLICT (table_name, key, lane) DO UPDATE SET docs = c.docs + EXCLUDED.docs;\n")
                .Append("RETURN NULL;\nEND;");

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
                .Append($" FROM {names.Table(table)} AS r;\n")
                .Append(CountTerms(names, table, terms));
        }

        return sql.ToString();
    }

    /// <summary>
    /// Counts a table's keys from scratch, unless they were counted since the table's file was created (the triggers
    /// keep them from then on). Before that, converts the keys of rows written by graphiti-net 0.1 (<c>field:term</c>,
    /// without an impact class) from their stored frequencies and lengths.
    /// </summary>
    private static string CountTerms(SqlNames names, string table, string terms)
    {
        var t = names.Table(table);
        var name = names.Name(table);
        var fields = SearchIndex.Columns[table].Length;
        var tf = $"((e.value::bigint >> ({SearchIndex.FrequencyBits} * (f.i - 1))) & {SearchIndex.MaxFrequency})";
        var converted = $"ARRAY(SELECT (f.i - 1)::text || ':' || e.key || '|' || "
            + $"{SearchIndex.ImpactClassSql("x.tf", "r.search_lengths[f.i]")}::text "
            + $"FROM jsonb_each_text(r.search_terms) AS e(key, value) CROSS JOIN generate_series(1, {fields}) AS f(i) "
            + $"CROSS JOIN LATERAL (SELECT {tf} AS tf) AS x WHERE x.tf > 0)";
        var regclass = $"'{t.Replace("'", "''", StringComparison.Ordinal)}'::regclass";
        var file = $"(SELECT relfilenode::bigint FROM pg_class WHERE oid = {regclass})";
        var unconverted = "r.search_lengths IS NOT NULL AND cardinality(r.search_keys) > 0 AND strpos(r.search_keys[1], '|') = 0";

        // The conversion rewrites every row: the GIN index is dropped first and built again after, which is much
        // faster than updating it row by row, and leaves it compact.
        return new Builder()
            .Append("DO $graphiti$\nDECLARE ix record; definitions text[] := '{}'; definition text;\nBEGIN\n")
            // Every lane, so that no writer changes the counts meanwhile (writers in progress finish first).
            .Append($"FOR i IN 0..{Lanes - 1} LOOP\nPERFORM pg_advisory_xact_lock({LaneLock($"'{names.Schema}.{name}'")}, i);\nEND LOOP;\n")
            .Append($"IF NOT EXISTS (SELECT 1 FROM {terms} WHERE table_name = '{name}' AND key = '{CountedMarker}' ")
            .Append($"AND docs = {file}) THEN\n")
            .Append($"IF EXISTS (SELECT 1 FROM {t} AS r WHERE {unconverted}) THEN\n")
            .Append("PERFORM set_config('maintenance_work_mem', '256MB', true);\n")
            .Append("FOR ix IN SELECT i.indexrelid::regclass::text AS name, pg_get_indexdef(i.indexrelid) AS def ")
            .Append($"FROM pg_index AS i WHERE i.indrelid = {regclass} ")
            .Append("AND pg_get_indexdef(i.indexrelid) LIKE '%USING gin (search_keys)' LOOP\n")
            .Append("definitions := definitions || ix.def;\nEXECUTE 'DROP INDEX ' || ix.name;\nEND LOOP;\n")
            .Append($"ALTER TABLE {t} DISABLE TRIGGER {TriggerName("update")};\n")
            .Append($"UPDATE {t} AS r SET search_keys = {converted} WHERE {unconverted};\n")
            .Append($"ALTER TABLE {t} ENABLE TRIGGER {TriggerName("update")};\n")
            .Append("FOREACH definition IN ARRAY definitions LOOP\nEXECUTE definition;\nEND LOOP;\n")
            .Append("END IF;\n")
            .Append($"DELETE FROM {terms} WHERE table_name = '{name}';\n")
            .Append($"INSERT INTO {terms} (table_name, key, docs) SELECT '{name}', k.key, count(*) ")
            .Append($"FROM {t} AS r CROSS JOIN LATERAL unnest(r.search_keys) AS k(key) GROUP BY k.key;\n")
            .Append($"INSERT INTO {terms} (table_name, key, docs) SELECT '{name}', '{ConfigPrefix}' || r.text_search_config::text, ")
            .Append($"count(*) FROM {t} AS r WHERE r.search_lengths IS NOT NULL GROUP BY 2;\n")
            .Append($"INSERT INTO {terms} (table_name, key, docs) SELECT '{name}', '{GroupPrefix}' || r.group_id, ")
            .Append($"count(*) FROM {t} AS r WHERE r.search_lengths IS NOT NULL GROUP BY 2;\n")
            .Append($"INSERT INTO {terms} (table_name, key, docs) VALUES ('{name}', '{CountedMarker}', {file});\n")
            .Append("ELSE\n")
            // Already counted: fold every lane into the first, and drop the keys no row holds any more.
            .Append($"WITH d AS (DELETE FROM {terms} WHERE table_name = '{name}' AND lane <> 0 RETURNING key, docs) ")
            .Append($"INSERT INTO {terms} AS c (table_name, key, lane, docs) SELECT '{name}', d.key, 0, sum(d.docs) ")
            .Append("FROM d GROUP BY d.key ON CONFLICT (table_name, key, lane) DO UPDATE SET docs = c.docs + EXCLUDED.docs;\n")
            .Append($"DELETE FROM {terms} WHERE table_name = '{name}' AND docs = 0;\n")
            .Append("END IF;\nEND\n$graphiti$;\n")
            .ToString();
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

    /// <summary>Each row's keys, and its text search configuration and group (once it has been analysed), counted <paramref name="sign"/>.</summary>
    private static string KeyRows(string rows, string sign) =>
        $"SELECT unnest(r.search_keys) AS key, {sign} AS docs FROM {rows} AS r UNION ALL "
        + $"SELECT '{ConfigPrefix}' || r.text_search_config::text, {sign} FROM {rows} AS r WHERE r.search_lengths IS NOT NULL "
        + $"UNION ALL SELECT '{GroupPrefix}' || r.group_id, {sign} FROM {rows} AS r WHERE r.search_lengths IS NOT NULL";

    private static string Sums(string alias, string sign) =>
        string.Join(", ", Enumerable.Range(1, MaxFields).Select(f =>
            $"{sign}count(*) FILTER (WHERE {alias}.search_lengths[{f}] > 0)"))
        + ", "
        + string.Join(", ", Enumerable.Range(1, MaxFields).Select(f =>
            $"{sign}coalesce(sum({alias}.search_lengths[{f}]), 0)"));
}
