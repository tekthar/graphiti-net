// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text;
using System.Text.Json;
using GraphitiNet.Driver.SqlServer.Entities;
using GraphitiNet.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>The record types the keyword index covers, as stored in its <c>doc_kind</c> column.</summary>
internal static class DocKind
{
    public const byte Episodes = 1;
    public const byte Entities = 2;
    public const byte Communities = 3;
    public const byte Facts = 4;

    /// <summary>The kind of a node or edge table, or <see langword="null"/> when the table is not searchable.</summary>
    public static byte? Of(string table) => table switch
    {
        SqlNames.Episodes => Episodes,
        SqlNames.Entities => Entities,
        SqlNames.Communities => Communities,
        SqlNames.EntityEdges => Facts,
        _ => null,
    };
}

/// <summary>
/// Shared plumbing for the operations classes: creating contexts, borrowing their pooled SqlClient connection, and the
/// batched upsert and delete statements (EF Core has no upsert, deletes cascade across tables, and every write keeps
/// the keyword index in step).
/// </summary>
internal sealed class DriverCore(Func<DbContext> contextFactory, SqlServerGraphOptions options)
{
    private const string Bin2 = SqlServerGraphOptions.BinaryCollation;

    private volatile bool _modelChecked;

    public SqlServerGraphOptions Options { get; } = options;

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
    public async Task<T> WithConnectionAsync<T>(Func<Db, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var db = new Db((SqlConnection)context.Database.GetDbConnection(), null, context.Database.GetCommandTimeout());
            return await work(db).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs raw ADO.NET work in one transaction.</summary>
    public async Task InTransactionAsync(Func<Db, Task> work, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)context.Database.GetDbConnection();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await work(new Db(connection, transaction, context.Database.GetCommandTimeout())).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Runs a batch that returns no rows.</summary>
    public static async Task ExecuteAsync(Db db, SqlBuilder b, CancellationToken ct)
    {
        await using var command = b.ToCommand(db);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ----- Upserts -----

    private enum ColumnKind
    {
        Plain,
        Json,
        Vector,
    }

    private readonly record struct Column(string Name, string Type, ColumnKind Kind = ColumnKind.Plain)
    {
        public static Column Text(string name) => new(name, "nvarchar(max)");

        public static Column Time(string name) => new(name, "datetimeoffset");

        public static Column Json(string name) => new(name, "nvarchar(max)", ColumnKind.Json);

        public static Column Vector(string name) => new(name, "nvarchar(max)", ColumnKind.Vector);
    }

    /// <summary>
    /// Upserts records <see cref="SqlServerGraphOptions.BulkBatchSize"/> at a time: each batch travels as one JSON
    /// parameter into a <c>MERGE</c> (with <c>HOLDLOCK</c>, so concurrent upserts of one id serialise), followed for
    /// searchable records by a rewrite of their rows in the keyword index. Later duplicates of an id win.
    /// </summary>
    private async Task UpsertAsync<T>(
        Db db,
        string table,
        IReadOnlyList<T> items,
        Column[] columns,
        Func<T, string> uuidOf,
        Func<T, string> groupOf,
        Action<Utf8JsonWriter, T> write,
        Func<T, string?[]>? searchFields,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var unique = Deduplicate(items, uuidOf);
        var sql = UpsertSql(table, columns, searchFields is not null);
        for (var start = 0; start < unique.Count; start += Options.BulkBatchSize)
        {
            var batch = unique.Skip(start).Take(Options.BulkBatchSize).ToList();
            await using var command = db.Command(sql);
            command.Parameters.Add(Named("@items", Params.Json(WriteJson(batch, write))));
            if (searchFields is not null)
            {
                var (terms, fields) = IndexJson(batch, uuidOf, groupOf, searchFields);
                command.Parameters.Add(Named("@terms", Params.Json(terms)));
                command.Parameters.Add(Named("@fields", Params.Json(fields)));
            }

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        static SqlParameter Named(string name, SqlParameter parameter)
        {
            parameter.ParameterName = name;
            return parameter;
        }
    }

    private static List<T> Deduplicate<T>(IReadOnlyList<T> items, Func<T, string> uuidOf)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<T>(items.Count);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (seen.Add(uuidOf(items[i])))
            {
                result.Add(items[i]);
            }
        }

        result.Reverse();
        return result;
    }

    private static string WriteJson<T>(IReadOnlyList<T> items, Action<Utf8JsonWriter, T> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();
                write(writer, item);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>The keyword index rows of a batch: one per (record, field, term) and one per non-empty field.</summary>
    private static (string Terms, string Fields) IndexJson<T>(
        IReadOnlyList<T> items,
        Func<T, string> uuidOf,
        Func<T, string> groupOf,
        Func<T, string?[]> searchFields)
    {
        using var termStream = new MemoryStream();
        using var fieldStream = new MemoryStream();
        using (var terms = new Utf8JsonWriter(termStream))
        using (var fields = new Utf8JsonWriter(fieldStream))
        {
            terms.WriteStartArray();
            fields.WriteStartArray();
            foreach (var item in items)
            {
                var uuid = uuidOf(item);
                var group = groupOf(item);
                var texts = searchFields(item);
                for (var f = 0; f < texts.Length; f++)
                {
                    var (length, frequencies) = Tokenizer.Count(texts[f]);
                    if (length == 0)
                    {
                        continue;
                    }

                    fields.WriteStartObject();
                    fields.WriteString("u", uuid);
                    fields.WriteNumber("f", f);
                    fields.WriteNumber("l", length);
                    fields.WriteEndObject();
                    foreach (var (term, count) in frequencies)
                    {
                        terms.WriteStartObject();
                        terms.WriteString("u", uuid);
                        terms.WriteNumber("f", f);
                        terms.WriteString("t", term);
                        terms.WriteString("g", group);
                        terms.WriteNumber("n", count);
                        terms.WriteNumber("l", length);
                        terms.WriteEndObject();
                    }
                }
            }

            terms.WriteEndArray();
            fields.WriteEndArray();
        }

        return (
            Encoding.UTF8.GetString(termStream.GetBuffer(), 0, (int)termStream.Length),
            Encoding.UTF8.GetString(fieldStream.GetBuffer(), 0, (int)fieldStream.Length));
    }

    private string UpsertSql(string table, Column[] columns, bool indexed)
    {
        var vectorType = "vector(" + Options.EmbeddingDimension.ToString(CultureInfo.InvariantCulture) + ")";
        var with = string.Join(", ", columns.Select(c => c.Kind == ColumnKind.Plain
            ? $"[{c.Name}] {(c.Name == "uuid" ? "nvarchar(4000)" : c.Type)} '$.{c.Name}'"
            : $"[{c.Name}] nvarchar(max) '$.{c.Name}' AS JSON"));
        string Value(Column c) => c.Kind == ColumnKind.Vector ? $"CAST(s.[{c.Name}] AS {vectorType})" : $"s.[{c.Name}]";
        var updates = string.Join(", ", columns.Skip(1).Select(c => $"t.[{c.Name}] = {Value(c)}"));
        // For searchable records the MERGE outputs the batch's ids into a keyed table variable, so the index rewrite
        // does not parse the items JSON (which carries the embeddings) again.
        var sql = string.Concat(
            "SET NOCOUNT ON; SET XACT_ABORT ON; ",
            indexed ? $"DECLARE @ids TABLE (uuid nvarchar(255) COLLATE {Bin2} PRIMARY KEY); " : "",
            $"MERGE {Names.Table(table)} WITH (HOLDLOCK) AS t USING OPENJSON(@items) WITH ({with}) AS s ",
            "ON t.[uuid] = s.[uuid] ",
            $"WHEN MATCHED THEN UPDATE SET {updates} ",
            $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", columns.Select(c => $"[{c.Name}]"))}) ",
            $"VALUES ({string.Join(", ", columns.Select(Value))})",
            indexed ? " OUTPUT inserted.[uuid] INTO @ids (uuid);" : ";");
        if (indexed)
        {
            var kind = DocKind.Of(table)!.Value.ToString(CultureInfo.InvariantCulture);
            var ids = "(SELECT uuid FROM @ids)";
            var terms = Names.Table(SqlNames.SearchTerms);
            var fields = Names.Table(SqlNames.SearchFields);
            sql += string.Concat(
                $" DELETE FROM {terms} WHERE doc_kind = {kind} AND uuid IN {ids};",
                $" DELETE FROM {fields} WHERE doc_kind = {kind} AND uuid IN {ids};",
                $" INSERT INTO {terms} (doc_kind, uuid, field, term, group_id, frequency, field_length) ",
                $"SELECT {kind}, u, f, t, g, n, l FROM OPENJSON(@terms) WITH (u nvarchar(4000) '$.u', ",
                "f tinyint '$.f', t nvarchar(4000) '$.t', g nvarchar(4000) '$.g', n int '$.n', l int '$.l');",
                $" INSERT INTO {fields} (doc_kind, uuid, field, field_length) ",
                $"SELECT {kind}, u, f, l FROM OPENJSON(@fields) ",
                "WITH (u nvarchar(4000) '$.u', f tinyint '$.f', l int '$.l');");
        }

        return sql;
    }

    private static readonly Column[] NodeColumns =
        [Column.Text("uuid"), Column.Text("group_id"), Column.Text("name"), Column.Json("labels"), Column.Time("created_at")];

    private static readonly Column[] EdgeColumns =
    [
        Column.Text("uuid"), Column.Text("group_id"), Column.Text("source_node_uuid"), Column.Text("target_node_uuid"),
        Column.Time("created_at"),
    ];

    private static void WriteNode(Utf8JsonWriter w, Node n, IEnumerable<string> labels)
    {
        w.WriteString("uuid", n.Uuid);
        w.WriteString("group_id", n.GroupId);
        w.WriteString("name", n.Name);
        WriteStrings(w, "labels", labels.Distinct(StringComparer.Ordinal));
        w.WriteString("created_at", n.CreatedAt.ToUniversalTime());
    }

    private static void WriteEdge(Utf8JsonWriter w, Edge e)
    {
        w.WriteString("uuid", e.Uuid);
        w.WriteString("group_id", e.GroupId);
        w.WriteString("source_node_uuid", e.SourceNodeUuid);
        w.WriteString("target_node_uuid", e.TargetNodeUuid);
        w.WriteString("created_at", e.CreatedAt.ToUniversalTime());
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
        {
            w.WriteStringValue(value);
        }

        w.WriteEndArray();
    }

    private static void WriteTime(Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is { } v)
        {
            w.WriteString(name, v.ToUniversalTime());
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteVector(Utf8JsonWriter w, string name, float[]? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartArray(name);
        foreach (var x in value)
        {
            w.WriteNumberValue(x);
        }

        w.WriteEndArray();
    }

    private static void WriteRawJson(Utf8JsonWriter w, string name, string? json)
    {
        w.WritePropertyName(name);
        if (json is null)
        {
            w.WriteNullValue();
        }
        else
        {
            w.WriteRawValue(json, skipInputValidation: true);
        }
    }

    public Task UpsertEpisodesAsync(Db db, IReadOnlyList<EpisodicNode> items, CancellationToken ct) =>
        UpsertAsync(
            db,
            SqlNames.Episodes,
            items,
            [.. NodeColumns, Column.Text("source"), Column.Text("source_description"), Column.Text("content"),
                Column.Time("valid_at"), Column.Json("entity_edges"), Column.Json("episode_metadata")],
            n => n.Uuid,
            n => n.GroupId,
            (w, n) =>
            {
                WriteNode(w, n, n.Labels);
                w.WriteString("source", n.Source.ToStorageName());
                w.WriteString("source_description", n.SourceDescription);
                w.WriteString("content", n.Content);
                w.WriteString("valid_at", n.ValidAt.ToUniversalTime());
                WriteStrings(w, "entity_edges", n.EntityEdges);
                WriteRawJson(w, "episode_metadata", JsonValues.SerializeNullable(n.EpisodeMetadata));
            },
            n => [n.Content, n.Source.ToStorageName(), n.SourceDescription],
            ct);

    public Task UpsertEntitiesAsync(Db db, IReadOnlyList<EntityNode> items, CancellationToken ct) =>
        UpsertAsync(
            db,
            SqlNames.Entities,
            items,
            [.. NodeColumns, Column.Text("summary"), Column.Json("attributes"), Column.Vector("name_embedding")],
            n => n.Uuid,
            n => n.GroupId,
            (w, n) =>
            {
                // Every entity carries the Entity label, as upstream's Neo4j nodes do.
                WriteNode(w, n, n.Labels.Contains("Entity", StringComparer.Ordinal) ? n.Labels : ["Entity", .. n.Labels]);
                w.WriteString("summary", n.Summary);
                WriteRawJson(w, "attributes", JsonValues.Serialize(n.Attributes));
                WriteVector(w, "name_embedding", n.NameEmbedding);
            },
            n => [n.Name, n.Summary],
            ct);

    public Task UpsertCommunitiesAsync(Db db, IReadOnlyList<CommunityNode> items, CancellationToken ct) =>
        UpsertAsync(
            db,
            SqlNames.Communities,
            items,
            [.. NodeColumns, Column.Text("summary"), Column.Vector("name_embedding")],
            n => n.Uuid,
            n => n.GroupId,
            (w, n) =>
            {
                WriteNode(w, n, n.Labels);
                w.WriteString("summary", n.Summary);
                WriteVector(w, "name_embedding", n.NameEmbedding);
            },
            n => [n.Name],
            ct);

    public Task UpsertSagasAsync(Db db, IReadOnlyList<SagaNode> items, CancellationToken ct) =>
        UpsertAsync(
            db,
            SqlNames.Sagas,
            items,
            [.. NodeColumns, Column.Text("summary"), Column.Text("first_episode_uuid"), Column.Text("last_episode_uuid"),
                Column.Time("last_summarized_at"), Column.Time("last_summarized_episode_valid_at")],
            n => n.Uuid,
            n => n.GroupId,
            (w, n) =>
            {
                WriteNode(w, n, n.Labels);
                w.WriteString("summary", n.Summary);
                w.WriteString("first_episode_uuid", n.FirstEpisodeUuid);
                w.WriteString("last_episode_uuid", n.LastEpisodeUuid);
                WriteTime(w, "last_summarized_at", n.LastSummarizedAt);
                WriteTime(w, "last_summarized_episode_valid_at", n.LastSummarizedEpisodeValidAt);
            },
            null,
            ct);

    public Task UpsertEntityEdgesAsync(Db db, IReadOnlyList<EntityEdge> items, CancellationToken ct) =>
        UpsertAsync(
            db,
            SqlNames.EntityEdges,
            items,
            [.. EdgeColumns, Column.Text("name"), Column.Text("fact"), Column.Json("episodes"), Column.Time("expired_at"),
                Column.Time("valid_at"), Column.Time("invalid_at"), Column.Time("reference_time"),
                Column.Json("attributes"), Column.Vector("fact_embedding")],
            e => e.Uuid,
            e => e.GroupId,
            (w, e) =>
            {
                WriteEdge(w, e);
                w.WriteString("name", e.Name);
                w.WriteString("fact", e.Fact);
                WriteStrings(w, "episodes", e.Episodes);
                WriteTime(w, "expired_at", e.ExpiredAt);
                WriteTime(w, "valid_at", e.ValidAt);
                WriteTime(w, "invalid_at", e.InvalidAt);
                WriteTime(w, "reference_time", e.ReferenceTime);
                WriteRawJson(w, "attributes", JsonValues.Serialize(e.Attributes));
                WriteVector(w, "fact_embedding", e.FactEmbedding);
            },
            e => [e.Name, e.Fact],
            ct);

    /// <summary>Upserts plain edges (episodic, community, has-episode, next-episode) into a table.</summary>
    public Task UpsertPlainEdgesAsync<TEdge>(Db db, string table, IReadOnlyList<TEdge> items, CancellationToken ct)
        where TEdge : Edge =>
        UpsertAsync(db, table, items, EdgeColumns, e => e.Uuid, e => e.GroupId, WriteEdge, null, ct);

    // ----- Deletes -----

    /// <summary>
    /// Appends statements deleting the nodes of <paramref name="nodeTable"/> whose ids <paramref name="idsQuery"/>
    /// yields (as column <c>v</c>), together with every edge that touches them in every edge table (upstream's
    /// <c>DETACH DELETE</c>) and their keyword index rows.
    /// </summary>
    public void AppendDeleteNodes(SqlBuilder b, string nodeTable, string idsQuery)
    {
        var ids = b.Variable("ids");
        b.Append($"DECLARE {ids} TABLE (uuid nvarchar(255) COLLATE {Bin2} PRIMARY KEY); ")
            .Append($"INSERT INTO {ids} (uuid) SELECT DISTINCT x.v FROM {idsQuery} AS x; ");
        var facts = Names.Table(SqlNames.EntityEdges);
        if (nodeTable == SqlNames.Entities)
        {
            AppendDeleteIndex(
                b,
                DocKind.Facts,
                $"(SELECT f.uuid FROM {facts} f WHERE f.source_node_uuid IN (SELECT uuid FROM {ids}) "
                + $"UNION SELECT f.uuid FROM {facts} f WHERE f.target_node_uuid IN (SELECT uuid FROM {ids}))");
        }

        foreach (var edgeTable in SqlNames.EdgeTables)
        {
            var t = Names.Table(edgeTable);
            b.Append($"DELETE FROM {t} WHERE source_node_uuid IN (SELECT uuid FROM {ids}); ")
                .Append($"DELETE FROM {t} WHERE target_node_uuid IN (SELECT uuid FROM {ids}); ");
        }

        if (DocKind.Of(nodeTable) is { } kind)
        {
            AppendDeleteIndex(b, kind, $"(SELECT uuid FROM {ids})");
        }

        b.Append($"DELETE FROM {Names.Table(nodeTable)} WHERE uuid IN (SELECT uuid FROM {ids}); ");
    }

    /// <summary>Appends statements deleting edges of a table by an id query (column <c>v</c> or <c>uuid</c>), with their
    /// keyword index rows.</summary>
    public void AppendDeleteEdges(SqlBuilder b, string edgeTable, string idsQuery)
    {
        if (DocKind.Of(edgeTable) is { } kind)
        {
            AppendDeleteIndex(b, kind, idsQuery);
        }

        b.Append($"DELETE FROM {Names.Table(edgeTable)} WHERE uuid IN {idsQuery}; ");
    }

    /// <summary>Appends statements deleting the keyword index rows of records of one kind.</summary>
    public void AppendDeleteIndex(SqlBuilder b, byte kind, string idsQuery)
    {
        var k = kind.ToString(CultureInfo.InvariantCulture);
        b.Append($"DELETE FROM {Names.Table(SqlNames.SearchTerms)} WHERE doc_kind = {k} AND uuid IN {idsQuery}; ")
            .Append($"DELETE FROM {Names.Table(SqlNames.SearchFields)} WHERE doc_kind = {k} AND uuid IN {idsQuery}; ");
    }

    /// <summary>Deletes nodes by id, with the edges touching them, in one transaction.</summary>
    public Task DeleteNodesAsync(string nodeTable, IReadOnlyList<string> uuids, CancellationToken ct)
    {
        var ids = ValidIds(uuids);
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        var b = new SqlBuilder().Append("SET NOCOUNT ON; SET XACT_ABORT ON; ");
        AppendDeleteNodes(b, nodeTable, b.Ids(ids));
        return InTransactionAsync(db => ExecuteAsync(db, b, ct), ct);
    }

    /// <summary>Deletes every node of a table in some groups (or all groups), with the edges touching them.</summary>
    public Task DeleteNodesByGroupAsync(string nodeTable, IReadOnlyList<string>? groupIds, CancellationToken ct)
    {
        var b = new SqlBuilder().Append("SET NOCOUNT ON; SET XACT_ABORT ON; ");
        var where = groupIds is null ? "" : $" WHERE group_id IN {b.Ids(groupIds)}";
        AppendDeleteNodes(b, nodeTable, $"(SELECT uuid AS v FROM {Names.Table(nodeTable)}{where})");
        return InTransactionAsync(db => ExecuteAsync(db, b, ct), ct);
    }

    public Task DeleteEdgesAsync(string edgeTable, IReadOnlyList<string> uuids, CancellationToken ct)
    {
        var ids = ValidIds(uuids);
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        var b = new SqlBuilder().Append("SET NOCOUNT ON; SET XACT_ABORT ON; ");
        AppendDeleteEdges(b, edgeTable, b.Ids(ids));
        return InTransactionAsync(db => ExecuteAsync(db, b, ct), ct);
    }

    /// <summary>The distinct ids that can exist (ids longer than the column are never stored).</summary>
    public static List<string> ValidIds(IEnumerable<string> uuids) =>
        uuids.Where(u => u is not null && u.Length <= GraphitiModelBuilderExtensions.IdLength)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // ----- Reads -----

    /// <summary>Runs a query and maps each row.</summary>
    public static async Task<List<T>> ReadListAsync<T>(SqlCommand command, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(map(reader));
            }
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));

        return result;
    }

    /// <summary>Runs a built query on a fresh connection and maps each row.</summary>
    public Task<IReadOnlyList<T>> ListAsync<T>(SqlBuilder b, Func<SqlDataReader, T> map, CancellationToken ct) =>
        WithConnectionAsync<IReadOnlyList<T>>(
            async db =>
            {
                await using var command = b.ToCommand(db);
                return await ReadListAsync(command, map, ct).ConfigureAwait(false);
            },
            ct);

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
                $"The DbContext maps graphiti-net's entities to [{schema}].[{table}], but the driver was "
                + $"configured with [{Options.Schema}].[{Names.Name(SqlNames.Entities)}]. Pass the same "
                + "SqlServerGraphOptions (schema and table prefix) to AddGraphiti and SqlServerDriver.");
        }

        if (context.Database.ProviderName != "Microsoft.EntityFrameworkCore.SqlServer")
        {
            throw new InvalidOperationException(
                "SqlServerDriver needs a DbContext configured with UseSqlServer or UseAzureSql.");
        }
    }
}
