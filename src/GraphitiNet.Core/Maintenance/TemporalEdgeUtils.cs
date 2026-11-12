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
    /// Upstream's <c>ensure_utc(datetime.fromisoformat(text.replace('Z', '+00:00')))</c>: parses an ISO 8601 date or
    /// date-time, taking a value without an offset to be UTC, and returns it in UTC.
    /// </summary>
    /// <remarks>
    /// Accepts what Python 3.11's <c>fromisoformat</c> accepts in practice: <c>YYYY-MM-DD</c>, optionally followed by
    /// <c>T</c> or a space and <c>HH:MM</c>, <c>HH:MM:SS</c> or <c>HH:MM:SS.fff…</c>, optionally followed by an offset
    /// (<c>+HH:MM</c>, <c>+HHMM</c> or <c>Z</c>). Fractions longer than 7 digits are cut to 7 (.NET's resolution).
    /// </remarks>
    internal static bool TryParseIsoDateTime(string text, out DateTimeOffset value)
    {
        value = default;
        var normalized = text.Replace("Z", "+00:00", StringComparison.Ordinal);
        var match = IsoDateTime().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        var date = match.Groups["date"].Value;
        var time = match.Groups["time"].Success ? match.Groups["time"].Value : "00:00:00";
        if (time.Length == 5)
        {
            time += ":00";
        }

        var fraction = match.Groups["fraction"].Success ? match.Groups["fraction"].Value : "";
        if (fraction.Length > 7)
        {
            fraction = fraction[..7];
        }

        var offset = match.Groups["offset"].Success ? match.Groups["offset"].Value : "+00:00";
        if (offset.Length == 5)
        {
            offset = offset[..3] + ":" + offset[3..];
        }

        var composed = $"{date}T{time}{(fraction.Length > 0 ? "." + fraction : "")}{offset}";
        var format = fraction.Length > 0 ? $"yyyy-MM-dd'T'HH:mm:ss.{new string('F', fraction.Length)}zzz" : "yyyy-MM-dd'T'HH:mm:sszzz";
        if (!DateTimeOffset.TryParseExact(composed, format, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    [GeneratedRegex(
        @"^(?<date>\d{4}-\d{2}-\d{2})(?:[T ](?<time>\d{2}:\d{2}(?::\d{2})?)(?:[.,](?<fraction>\d+))?)?(?<offset>[+-]\d{2}:?\d{2})?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(
        @"\b(will|shall|is going to|are going to|going to|plans? to|planned to|planning to|intends? to|intended to|expects? to|expected to|promises? to|promised to|scheduled to|is scheduled to|are scheduled to|set to|aims? to|aimed to|committed to)\b|[a-z]+'ll\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FutureAssertion();

    [GeneratedRegex(
        @"\b(by|before|no later than|deadline|due by|due on)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Deadline();

    [GeneratedRegex(
        @"\b(until|through|thru|expires?|expired|ends?|ended|ending|stops?|stopped|ceases?|ceased|no longer)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OngoingStateEnd();
}
