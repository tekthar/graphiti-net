// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text;

namespace GraphitiNet.Driver;

/// <summary>
/// The text analysis and BM25 parameters of upstream's full-text search (Neo4j's Lucene indexes with the
/// <c>standard-no-stop-words</c> analyzer), shared by graphiti-net's drivers so that they find and rank the same terms.
/// </summary>
public static class FullText
{
    /// <summary>BM25 term frequency saturation, Lucene's default.</summary>
    public const double K1 = 1.2;

    /// <summary>BM25 length normalisation, Lucene's default.</summary>
    public const double B = 0.75;

    /// <summary>
    /// Splits text into lower-case terms the way Lucene's <c>StandardTokenizer</c> (Unicode UAX #29 word boundaries)
    /// does for the scripts graphiti-net meets, as Neo4j's <c>standard-no-stop-words</c> analyzer upstream uses:
    /// letters, digits, marks and underscores form words (<c>ADDED_TO_WORKSPACE</c> is one term); an apostrophe,
    /// period or colon between two letters joins them (<c>can't</c>, <c>u.s.a</c>); a period, comma or semicolon
    /// between two digits joins them (<c>2026.03</c>, <c>1,000</c>); everything else separates words. Terms are
    /// capped at 255 characters, as Lucene.
    /// </summary>
    /// <param name="text">The text; <see langword="null"/> or empty gives no terms.</param>
    /// <returns>The terms in text order, with repeats.</returns>
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
                terms.Add(term.Length > 255 ? term[..255] : term);
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

    private static bool IsWordRune(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.ConnectorPunctuation;

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
