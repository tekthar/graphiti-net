// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/temporal_edge_utils.py (Graphiti v0.31.2) and changed for .NET.

using System.Globalization;
using System.Text.RegularExpressions;
using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Maintenance;

/// <summary>Helpers for normalising the time window of a fact (entity edge).</summary>
public static partial class TemporalEdgeUtils
{
    /// <summary>
    /// Normalises the time window of a future-tense assertion (a plan, promise or scheduled event).
    /// </summary>
    /// <remarks>
    /// For planned, promised or scheduled facts, <c>valid_at</c> is the statement's reference time, not the future
    /// occurrence or deadline date; the future date stays in the fact text or typed attributes. <c>invalid_at</c> is
    /// only for facts that stop being true, so a soft deadline like "will complete by Friday" is dropped. Every
    /// returned timestamp is in UTC, even when the fact is not future-tense.
    /// </remarks>
    /// <param name="fact">The fact text.</param>
    /// <param name="referenceTime">When the fact was stated; without it nothing changes except the UTC conversion.</param>
    /// <param name="validAt">The extracted start.</param>
    /// <param name="invalidAt">The extracted end.</param>
    /// <returns>The normalised start and end.</returns>
    public static (DateTimeOffset? ValidAt, DateTimeOffset? InvalidAt) NormalizeFutureTemporalBounds(
        string fact,
        DateTimeOffset? referenceTime,
        DateTimeOffset? validAt,
        DateTimeOffset? invalidAt)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var referenceTimeUtc = DateTimeUtils.EnsureUtc(referenceTime);
        var validAtUtc = DateTimeUtils.EnsureUtc(validAt);
        var invalidAtUtc = DateTimeUtils.EnsureUtc(invalidAt);

        if (referenceTimeUtc is null || !FutureAssertion().IsMatch(fact))
        {
            return (validAtUtc, invalidAtUtc);
        }

        if (validAtUtc is null || validAtUtc > referenceTimeUtc)
        {
            validAtUtc = referenceTimeUtc;
        }

        if (invalidAtUtc is not null
            && invalidAtUtc > referenceTimeUtc
            && Deadline().IsMatch(fact)
            && !OngoingStateEnd().IsMatch(fact))
        {
            invalidAtUtc = null;
        }

        return (validAtUtc, invalidAtUtc);
    }

    /// <summary>
    /// Parses ISO 8601 timestamps onto an edge, then normalises future-tense bounds with
    /// <see cref="NormalizeFutureTemporalBounds"/>. A value that does not parse leaves the edge's field unchanged.
    /// </summary>
    /// <param name="edge">The edge to update.</param>
    /// <param name="validAt">The start, as ISO 8601 text, or <see langword="null"/>/empty.</param>
    /// <param name="invalidAt">The end, as ISO 8601 text, or <see langword="null"/>/empty.</param>
    /// <param name="referenceTime">When the fact was stated.</param>
    public static void ApplyExtractedTimestamps(
        EntityEdge edge,
        string? validAt,
        string? invalidAt,
        DateTimeOffset? referenceTime)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!string.IsNullOrEmpty(validAt) && TryParseIsoDateTime(validAt, out var parsedValidAt))
        {
            edge.ValidAt = parsedValidAt;
        }

        if (!string.IsNullOrEmpty(invalidAt) && TryParseIsoDateTime(invalidAt, out var parsedInvalidAt))
        {
            edge.InvalidAt = parsedInvalidAt;
        }

        (edge.ValidAt, edge.InvalidAt) = NormalizeFutureTemporalBounds(
            edge.Fact, referenceTime, edge.ValidAt, edge.InvalidAt);
    }

    /// <summary>
    /// Upstream's <c>ensure_utc(datetime.fromisoformat(text.replace('Z', '+00:00')))</c>: parses a timestamp the way
    /// Python 3.13 does (see <see cref="PythonDateTime"/>), taking a value without an offset to be UTC, and returns it
    /// in UTC.
    /// </summary>
    /// <remarks>
    /// Deliberate deviation: where the conversion to UTC leaves the range of datetimes (<c>0001-01-01T00:00+01:00</c>),
    /// Python raises <c>OverflowError</c>, which upstream does not catch, so one odd timestamp from the LLM fails the
    /// whole extraction. The port treats such a value as unparseable instead, as it does any text Python rejects with
    /// <c>ValueError</c>.
    /// </remarks>
    internal static bool TryParseIsoDateTime(string text, out DateTimeOffset value)
    {
        value = default;
        return PythonDateTime.TryFromIsoFormat(text.Replace("Z", "+00:00", StringComparison.Ordinal), out var parsed) &&
               parsed.TryToUtc(out value);
    }

    // Upstream's patterns, compiled with Python's re.IGNORECASE semantics (see PythonIgnoreCase).
    private static readonly Regex FutureAssertionRegex = PythonIgnoreCase(
        @"\b(will|shall|is going to|are going to|going to|plans? to|planned to|planning to|intends? to|intended to|expects? to|expected to|promises? to|promised to|scheduled to|is scheduled to|are scheduled to|set to|aims? to|aimed to|committed to)\b|[a-z]+'ll\b");

    private static readonly Regex DeadlineRegex = PythonIgnoreCase(
        @"\b(by|before|no later than|deadline|due by|due on)\b");

    private static readonly Regex OngoingStateEndRegex = PythonIgnoreCase(
        @"\b(until|through|thru|expires?|expired|ends?|ended|ending|stops?|stopped|ceases?|ceased|no longer)\b");

    private static Regex FutureAssertion() => FutureAssertionRegex;

    private static Regex Deadline() => DeadlineRegex;

    private static Regex OngoingStateEnd() => OngoingStateEndRegex;

    /// <summary>
    /// Builds a .NET regex that matches as Python's <c>re.compile(pattern, re.IGNORECASE)</c> does on <c>str</c>, for
    /// patterns made of ASCII letters, <c>\b</c>, <c>[a-z]</c> and plain punctuation.
    /// </summary>
    /// <remarks>
    /// Two things differ from .NET's <see cref="RegexOptions.IgnoreCase"/> and <c>\b</c>. Python folds case with the
    /// simple Unicode mapping, so <c>i</c> also matches <c>İ</c> and <c>ı</c>, <c>k</c> the Kelvin sign <c>K</c>, and
    /// <c>s</c> the long s <c>ſ</c>. And Python's word characters are letters, numbers and <c>_</c>, so a combining mark
    /// (an Arabic vowel sign, say) is a word boundary in Python and not in .NET.
    /// </remarks>
    private static Regex PythonIgnoreCase(string pattern)
    {
        const string word = @"[\p{L}\p{N}_]";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (pattern.AsSpan(i).StartsWith(@"\b"))
            {
                sb.Append(CultureInfo.InvariantCulture, $"(?:(?<={word})(?!{word})|(?<!{word})(?={word}))");
                i++;
            }
            else if (pattern.AsSpan(i).StartsWith("[a-z]"))
            {
                sb.Append("[a-zA-Z\u0130\u0131\u017f\u212a]");
                i += 4;
            }
            else if (c is >= 'a' and <= 'z')
            {
                sb.Append('[').Append(c).Append(char.ToUpperInvariant(c)).Append(c switch
                {
                    'i' => "\u0130\u0131",
                    'k' => "\u212a",
                    's' => "\u017f",
                    _ => "",
                }).Append(']');
            }
            else
            {
                sb.Append(c);
            }
        }

        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }
}
