// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Driver.InMemory;

/// <summary>
/// Okapi BM25 ranking over several text fields, the way Lucene (and so upstream's Neo4j full-text indexes) ranks a
/// query without field names: each query term is scored against each field with that field's own statistics, and the
/// scores are summed. A record matches when it contains at least one query term.
/// </summary>
internal static class Bm25
{
    /// <summary>Term frequency saturation, Lucene's default.</summary>
    public const double K1 = FullText.K1;

    /// <summary>Length normalisation, Lucene's default.</summary>
    public const double B = FullText.B;

    /// <summary>Splits text into lower-case terms; see <see cref="FullText.Tokenize"/>.</summary>
    public static List<string> Tokenize(string? text) => FullText.Tokenize(text);

    /// <summary>
    /// Scores <paramref name="corpus"/> against <paramref name="query"/> and returns the matching records that pass
    /// <paramref name="include"/>, best first (ties by id), at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="corpus">Every record of the type, which sets the collection statistics, as a Lucene index
    /// covers every group.</param>
    /// <param name="uuidOf">The record's id.</param>
    /// <param name="fields">The record's indexed fields.</param>
    /// <param name="include">Group and search filters; excluded records still count in the statistics.</param>
    /// <param name="limit">The maximum number of results.</param>
    public static List<T> Search<T>(
        string query,
        IReadOnlyCollection<T> corpus,
        Func<T, string> uuidOf,
        Func<T, IReadOnlyList<string?>> fields,
        Func<T, bool> include,
        int limit)
    {
        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0 || limit <= 0 || corpus.Count == 0)
        {
            return [];
        }

        var documents = corpus
            .Select(item => (Item: item, Fields: fields(item).Select(Count).ToList()))
            .ToList();
        var fieldCount = documents[0].Fields.Count;

        // Per-field collection statistics: documents with at least one term, total length, document frequency.
        var docCount = new int[fieldCount];
        var totalLength = new long[fieldCount];
        var docFrequency = new Dictionary<string, int>[fieldCount];
        for (var f = 0; f < fieldCount; f++)
        {
            docFrequency[f] = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var distinctQueryTerms = queryTerms.ToHashSet(StringComparer.Ordinal);
        foreach (var (_, docFields) in documents)
        {
            for (var f = 0; f < fieldCount; f++)
            {
                var field = docFields[f];
                if (field.Length == 0)
                {
                    continue;
                }

                docCount[f]++;
                totalLength[f] += field.Length;
                foreach (var term in distinctQueryTerms)
                {
                    if (field.Frequencies.ContainsKey(term))
                    {
                        docFrequency[f][term] = docFrequency[f].GetValueOrDefault(term) + 1;
                    }
                }
            }
        }

        var scored = new List<(T Item, double Score)>();
        foreach (var (item, docFields) in documents)
        {
            var score = 0.0;
            var matched = false;
            for (var f = 0; f < fieldCount; f++)
            {
                var field = docFields[f];
                if (field.Length == 0)
                {
                    continue;
                }

                var averageLength = (double)totalLength[f] / docCount[f];
                foreach (var term in queryTerms)
                {
                    if (!field.Frequencies.TryGetValue(term, out var tf))
                    {
                        continue;
                    }

                    matched = true;
                    var df = docFrequency[f][term];
                    var idf = Math.Log(1 + ((docCount[f] - df + 0.5) / (df + 0.5)));
                    var norm = K1 * (1 - B + (B * field.Length / averageLength));
                    score += idf * (tf * (K1 + 1)) / (tf + norm);
                }
            }

            if (matched && include(item))
            {
                scored.Add((item, score));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => uuidOf(s.Item), StringComparer.Ordinal)
            .Take(limit)
            .Select(s => s.Item)
            .ToList();
    }

    private static FieldTerms Count(string? text)
    {
        var terms = Tokenize(text);
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
        }

        return new FieldTerms(terms.Count, frequencies);
    }

    private readonly record struct FieldTerms(int Length, Dictionary<string, int> Frequencies);
}
