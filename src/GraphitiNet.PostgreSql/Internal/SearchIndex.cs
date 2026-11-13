// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Models;
using Npgsql;
using NpgsqlTypes;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// A row's full-text columns: <c>search_terms</c> (JSON, each term to its packed frequencies), <c>search_keys</c>
/// (each field's distinct terms with their impact class, as <see cref="SearchIndex.Key"/>s) and <c>search_lengths</c>
/// (each field's term count).
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
    /// A field-qualified term with its impact class, <c>field:term|class</c> (field from 0), as <c>search_keys</c> holds
    /// them: its GIN index finds the rows with a term in a field, or only those where the term weighs at least so
    /// much, without reading the rows' values; <c>search_term_counts</c> counts the rows holding each key.
    /// </summary>
    public static string Key(int field, string term, int impactClass) =>
        impactClass == UnclassedImpact
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{field}:{term}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{field}:{term}|{impactClass}");

    /// <summary>
    /// Every key a term can have in a field: one per impact class, then the key of <see cref="UnclassedImpact"/>,
    /// <c>field:term</c>, which graphiti-net 0.1 wrote and rows keep until <c>BuildIndicesAndConstraintsAsync</c>
    /// converts them (searches treat it as the highest class).
    /// </summary>
    public static IEnumerable<string> Keys(int field, string term) =>
        Enumerable.Range(0, ImpactClasses).Select(c => Key(field, term, c)).Append(Key(field, term, UnclassedImpact));

    /// <summary>The impact class of a key without one, from graphiti-net 0.1.</summary>
    public const int UnclassedImpact = -1;

    /// <summary>
    /// The lower bounds of the classes of <c>length / tf</c>, times four so that they are integers: four classes per
    /// doubling, from 1 to 256 and above. A row's class is the number of bounds at or below its ratio, computed with
    /// integers so that .NET and SQL agree exactly.
    /// </summary>
    public static readonly int[] RatioBoundsTimesFour =
    [
        4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384,
        448, 512, 640, 768, 896, 1024,
    ];

    /// <summary>Term frequencies are classed 1, 2, 3, and 4 or more.</summary>
    public const int FrequencyClasses = 4;

    /// <summary>The number of impact classes a (term, field) pair can be in.</summary>
    public static int ImpactClasses => FrequencyClasses * RatioBoundsTimesFour.Length;

    /// <summary>
    /// The impact class of a term occurring <paramref name="tf"/> times in a field of <paramref name="length"/> terms:
    /// <c>(min(tf, 4) - 1) * 33 + r - 1</c>, where <c>r</c> counts the <see cref="RatioBoundsTimesFour"/> at or below
    /// <c>length / tf</c>. A class bounds the term's BM25 weight in the field whatever the collection statistics; see
    /// <see cref="MaxWeight"/>.
    /// </summary>
    public static int ImpactClass(long tf, long length)
    {
        var ratio = 0;
        foreach (var bound in RatioBoundsTimesFour)
        {
            if (4 * length >= bound * tf)
            {
                ratio++;
            }
        }

        return ((int)(Math.Min(tf, FrequencyClasses) - 1) * RatioBoundsTimesFour.Length) + Math.Max(ratio, 1) - 1;
    }

    /// <summary>The SQL of <see cref="ImpactClass"/> for the integer expressions <paramref name="tf"/> and <paramref name="length"/>.</summary>
    public static string ImpactClassSql(string tf, string length) =>
        $"(((least({tf}, {FrequencyClasses}) - 1) * {RatioBoundsTimesFour.Length}) + greatest((SELECT count(*) FROM "
        + $"unnest('{{{string.Join(",", RatioBoundsTimesFour)}}}'::bigint[]) AS m(b) "
        + $"WHERE 4 * ({length})::bigint >= m.b * ({tf})), 1) - 1)";

    /// <summary>
    /// An upper bound of <c>tf * (k1 + 1) / (tf + k1 * (1 - b + b * length / averageLength))</c>, a term's BM25 weight
    /// in a field before its idf, for every row whose (term, field) is in <paramref name="impactClass"/>. The weight is
    /// <c>(k1 + 1) / (1 + k1 * (1 - b) / tf + k1 * b * (length / tf) / averageLength)</c>; the class fixes <c>tf</c>
    /// (from 4 on, only bounds it below) and bounds <c>length / tf</c> below.
    /// </summary>
    public static double MaxWeight(int impactClass, double averageLength)
    {
        if (impactClass == UnclassedImpact)
        {
            // Any tf and length; length / tf is at least 1.
            return (FullText.K1 + 1) / (1 + (FullText.K1 * FullText.B / averageLength));
        }

        var tfClass = (impactClass / RatioBoundsTimesFour.Length) + 1;
        var ratio = RatioBoundsTimesFour[impactClass % RatioBoundsTimesFour.Length] / 4.0;
        var saturation = tfClass < FrequencyClasses ? FullText.K1 * (1 - FullText.B) / tfClass : 0;
        return (FullText.K1 + 1) / (1 + saturation + (FullText.K1 * FullText.B * ratio / averageLength));
    }

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
            [.. frequencies.SelectMany(t => fields.Select((_, f) => f).Where(f => t.Value[f] > 0)
                .Select(f => Key(f, t.Key, ImpactClass(Math.Min(t.Value[f], MaxFrequency), fields[f].Count))))],
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
