// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/text_utils.py (Graphiti v0.31.2) and changed for .NET.

using System.Text;
using System.Text.RegularExpressions;
using GraphitiNet.Models;
using GraphitiNet.Prompts;

namespace GraphitiNet.Utils;

/// <summary>Text helpers used by the pipeline.</summary>
public static partial class TextUtils
{
    /// <summary>Upstream <c>MAX_SUMMARY_CHARS</c>: the cap on entity and community summaries.</summary>
    public const int MaxSummaryChars = Snippets.MaxSummaryChars;

    /// <summary>Upstream <c>SAGA_SUMMARY_MAX_CHARS</c>.</summary>
    public const int SagaSummaryMaxChars = 4000;

    /// <summary>Cuts text to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    public static string TruncateUtf8ToBytes(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxBytes <= 0)
        {
            return "";
        }

        var encoded = Encoding.UTF8.GetBytes(text);
        if (encoded.Length <= maxBytes)
        {
            return text;
        }

        // Back off to a character boundary: continuation bytes are 10xxxxxx.
        var end = maxBytes;
        while (end > 0 && (encoded[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return Encoding.UTF8.GetString(encoded, 0, end);
    }

    /// <summary>
    /// Cuts text to about <paramref name="maxChars"/> characters, at the last sentence end (<c>.</c>, <c>!</c> or
    /// <c>?</c> followed by whitespace or the end) before the limit, or at the limit if there is none.
    /// </summary>
    public static string TruncateAtSentence(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || PythonText.Len(text) <= maxChars)
        {
            return text;
        }

        var truncated = PySlice(text, maxChars);
        var matches = SentenceEnd().Matches(truncated);
        if (matches.Count > 0)
        {
            var last = matches[^1];
            return text[..(last.Index + last.Length)].TrimEnd();
        }

        return truncated.TrimEnd();
    }

    /// <summary>
    /// Joins episode contents. One episode returns its content unchanged; several get <c>[Episode N]</c> headers with
    /// their timestamps.
    /// </summary>
    public static string ConcatenateEpisodes(IReadOnlyList<EpisodicNode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        if (episodes.Count == 1)
        {
            return episodes[0].Content;
        }

        return string.Join("\n\n", episodes.Select((ep, i) =>
            $"[Episode {i}] (timestamp: {PythonText.IsoFormat(ep.ValidAt)})\n{ep.Content}"));
    }

    /// <summary>
    /// Renders <paramref name="timeline"/> in order, numbering members of <paramref name="episodes"/> by their index
    /// there and marking the rest as context.
    /// </summary>
    public static string ConcatenateTimeline(IReadOnlyList<EpisodicNode> episodes, IReadOnlyList<EpisodicNode> timeline)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(timeline);
        var indexByUuid = new Dictionary<string, int>();
        for (var i = 0; i < episodes.Count; i++)
        {
            indexByUuid[episodes[i].Uuid] = i;
        }

        return string.Join("\n\n", timeline.Select(ep =>
        {
            var timestamp = PythonText.IsoFormat(ep.ValidAt);
            var header = indexByUuid.TryGetValue(ep.Uuid, out var idx)
                ? $"[Episode {idx}] (timestamp: {timestamp})"
                : $"[CONTEXT EPISODE] (timestamp: {timestamp})";
            return $"{header}\n{ep.Content}";
        }));
    }

    // Python slices by code point; .NET strings are UTF-16.
    private static string PySlice(string text, int maxCodePoints)
    {
        var index = 0;
        for (var count = 0; count < maxCodePoints && index < text.Length; count++)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
        }

        return text[..index];
    }

    [GeneratedRegex(@"[.!?](?:\s|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();
}
