// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text;

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>
/// The keyword index's analyzer: the same tokenizer as the in-memory driver (<c>Bm25.Tokenize</c> in
/// GraphitiNet.Core), which follows Lucene's <c>StandardTokenizer</c> as upstream's Neo4j full-text indexes use it.
/// Kept as a copy so this package does not depend on core internals; the conformance and parity tests check that both
/// drivers rank alike.
/// </summary>
internal static class Tokenizer
{
    /// <summary>Lucene's maximum term length, in UTF-16 code units.</summary>
    public const int MaxTermLength = 255;

    /// <summary>
    /// Splits text into lower-case terms: letters, digits, marks and underscores form words
    /// (<c>ADDED_TO_WORKSPACE</c> is one term); an apostrophe, period or colon between two letters joins them
    /// (<c>can't</c>, <c>u.s.a</c>); a period, comma or semicolon between two digits joins them (<c>2026.03</c>,
    /// <c>1,000</c>); everything else separates words. No stop words, no stemming.
    /// </summary>
    public static List<string> Tokenize(string? text)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return terms;
        }

        var runes = text.EnumerateRunes().ToArray();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                var term = current.ToString();
                terms.Add(term.Length > MaxTermLength ? term[..MaxTermLength] : term);
                current.Clear();
            }
        }

        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            if (IsWordRune(rune))
            {
                current.Append(Rune.ToLowerInvariant(rune).ToString());
                continue;
            }

            if (current.Length > 0 && i + 1 < runes.Length && JoinsAcross(runes[i - 1], rune, runes[i + 1]))
            {
                current.Append(rune.ToString());
                continue;
            }

            Flush();
        }

        Flush();
        return terms;
    }

    /// <summary>The distinct terms of a text with their counts, and the text's length in terms.</summary>
    public static (int Length, Dictionary<string, int> Frequencies) Count(string? text)
    {
        var terms = Tokenize(text);
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
        }

        return (terms.Count, frequencies);
    }

    private static bool IsWordRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation;

    // UAX #29 WB6/WB7 (MidLetter, MidNumLet between letters) and WB11/WB12 (MidNum, MidNumLet between digits).
    private static bool JoinsAcross(Rune before, Rune joiner, Rune after)
    {
        var c = joiner.Value;
        var midLetter = c is '\'' or '.' or ':' or 0x2019 or 0x00B7 or 0x2027 or 0xFE13 or 0xFE55 or 0xFF1A;
        var midNum = c is '.' or ',' or ';' or '\'' or 0x2019 or 0x066C or 0xFE50 or 0xFE54 or 0xFF0C or 0xFF1B;
        return (midLetter && Rune.IsLetter(before) && Rune.IsLetter(after))
            || (midNum && Rune.IsDigit(before) && Rune.IsDigit(after));
    }
}
