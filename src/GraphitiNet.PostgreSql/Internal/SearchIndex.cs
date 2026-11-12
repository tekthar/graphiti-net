// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Models;
using Npgsql;
using NpgsqlTypes;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// A row's full-text columns: <c>search_terms</c> (JSON, each term to its packed frequencies), <c>search_keys</c>
/// (each field's distinct terms as <see cref="SearchIndex.Key"/>s) and <c>search_lengths</c> (each field's term count).
/// </summary>
internal readonly record struct SearchDocument(string Terms, string[] Keys, int[] Lengths);

/// <summary>
/// Turns a row's searchable fields into the columns BM25 is scored from. Text is split into terms with
/// <see cref="FullText.Tokenize"/>, the same analysis as upstream's Lucene indexes and the in-memory driver. Under the
/// <c>simple</c> configuration (the default) those terms are indexed as they are; under any other configuration each
/// term is normalised by PostgreSQL with <c>to_tsvector(config, term)</c>, which stems it or drops it as a stop word
/// (and may split it into several lexemes). Queries are normalised the same way, with <see cref="NormaliseAsync"/>.
/// </summary>
internal static class SearchIndex
{
    // The JSON goes to PostgreSQL only, never into HTML, so non-ASCII text needs no escaping.
    private static readonly System.Text.Encodings.Web.JavaScriptEncoder JsonEncoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Bits per field in a packed term frequency: <c>search_terms</c> maps each term to
    /// <c>tf(field 0) | tf(field 1) &lt;&lt; 20 | tf(field 2) &lt;&lt; 40</c>, a compact number rather than an array, so
    /// that rows stay small (and seldom compressed) and frequencies decode with integer operations.
    /// </summary>
    public const int FrequencyBits = 20;

    /// <summary>The largest frequency a field can record (a term repeated more often in one field counts this many times).</summary>
    public const int MaxFrequency = (1 << FrequencyBits) - 1;

    /// <summary>
    /// A field-qualified term, <c>field:term</c> (field from 0), as <c>search_keys</c> holds them: its GIN index finds
    /// the rows with a term in a field, and counts them, without reading the rows' values.
    /// </summary>
    public static string Key(int field, string term) =>
        field.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + term;

    /// <summary>SQL reading field <paramref name="field"/> (from 0) of a packed frequency (<c>jsonb</c>).</summary>
    public static string FrequencySql(string packed, int field) =>
        field == 0
            ? $"(({packed})::bigint & {MaxFrequency})"
            : $"((({packed})::bigint >> {FrequencyBits * field}) & {MaxFrequency})";

    /// <summary>The searchable columns of each table, in field order.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Columns = new Dictionary<string, string[]>
    {
        [SqlNames.Entities] = ["name", "summary"],
        [SqlNames.EntityEdges] = ["name", "fact"],
        [SqlNames.Episodes] = ["content", "source", "source_description"],
        [SqlNames.Communities] = ["name"],
    };

    public static string?[] Fields(EntityNode n) => [n.Name, n.Summary];

    public static string?[] Fields(EntityEdge e) => [e.Name, e.Fact];

    public static string?[] Fields(EpisodicNode e) => [e.Content, e.Source.ToStorageName(), e.SourceDescription];

    public static string?[] Fields(CommunityNode c) => [c.Name];

    /// <summary>Whether a configuration name is PostgreSQL's <c>simple</c>, under which terms are kept as they are.</summary>
    public static bool IsSimple(string config) =>
        string.Equals(CanonicalConfig(config), PostgresGraphOptions.SimpleTextSearchConfig, StringComparison.Ordinal);

    /// <summary>A configuration name without the <c>pg_catalog</c> schema, lower-cased, so one configuration has one name.</summary>
    public static string CanonicalConfig(string config)
    {
        var name = config.ToLowerInvariant();
        return name.StartsWith("pg_catalog.", StringComparison.Ordinal) ? name["pg_catalog.".Length..] : name;
    }

    /// <summary>
    /// The full-text columns of each row, given its text search configuration and its fields. Asks PostgreSQL to
    /// normalise the terms of rows that use a language configuration (one round trip); rows under <c>simple</c> need
    /// none.
    /// </summary>
    public static async Task<SearchDocument[]> AnalyzeAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(string Config, string?[] Fields)> rows,
        CancellationToken ct)
    {
        var tokens = rows.Select(r => r.Fields.Select(FullText.Tokenize).ToArray()).ToArray();
        var lexemes = await NormaliseAsync(
            connection,
            rows.SelectMany((r, i) => IsSimple(r.Config)
                ? []
                : tokens[i].SelectMany(field => field.Select(token => (r.Config, token)))),
            ct).ConfigureAwait(false);

        var documents = new SearchDocument[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var config = rows[i].Config;
            var fields = IsSimple(config)
                ? tokens[i]
                : tokens[i].Select(f => f.SelectMany(t => lexemes[(config, t)]).ToList()).ToArray();
            documents[i] = Document(fields);
        }

        return documents;
    }

    private static SearchDocument Document(List<string>[] fields)
    {
        var frequencies = new Dictionary<string, int[]>(StringComparer.Ordinal);
        for (var f = 0; f < fields.Length; f++)
        {
            foreach (var term in fields[f])
            {
                if (!frequencies.TryGetValue(term, out var counts))
                {
                    counts = new int[fields.Length];
                    frequencies[term] = counts;
                }

                counts[f]++;
            }
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonEncoder }))
        {
            writer.WriteStartObject();
            foreach (var (term, counts) in frequencies)
            {
                var packed = 0L;
                for (var f = 0; f < counts.Length; f++)
                {
                    packed |= (long)Math.Min(counts[f], MaxFrequency) << (FrequencyBits * f);
                }

                writer.WriteNumber(term, packed);
            }

            writer.WriteEndObject();
        }

        return new SearchDocument(
            System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan),
            [.. frequencies.SelectMany(t => fields.Select((_, f) => f).Where(f => t.Value[f] > 0).Select(f => Key(f, t.Key)))],
            [.. fields.Select(f => f.Count)]);
    }


    /// <summary>
    /// Each (configuration, token) pair's lexemes under that language configuration: what
    /// <c>to_tsvector(config, token)</c> yields, in text order, a lexeme repeated as often as it occurs (none for a stop
    /// word). One round trip; none when there are no pairs.
    /// </summary>
    public static async Task<Dictionary<(string Config, string Token), List<string>>> NormaliseAsync(
        NpgsqlConnection connection,
        IEnumerable<(string Config, string Token)> pairs,
        CancellationToken ct)
    {
        var lexemes = new Dictionary<(string Config, string Token), List<string>>();
        foreach (var pair in pairs)
        {
            lexemes.TryAdd(pair, []);
        }

        if (lexemes.Count == 0)
        {
            return lexemes;
        }

        var keys = lexemes.Keys.ToArray();
        await using var command = new NpgsqlCommand(
            "SELECT p.i::int, l.lexeme FROM unnest($1::text[], $2::text[]) WITH ORDINALITY AS p(cfg, tok, i) "
            + "CROSS JOIN LATERAL unnest(to_tsvector(CAST(p.cfg AS regconfig), p.tok)) AS l "
            + "CROSS JOIN LATERAL unnest(l.positions) AS x(pos) ORDER BY p.i, x.pos",
            connection);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = keys.Select(k => k.Config).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = keys.Select(k => k.Token).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            lexemes[keys[reader.GetInt32(0) - 1]].Add(reader.GetString(1));
        }

        return lexemes;
    }
}
