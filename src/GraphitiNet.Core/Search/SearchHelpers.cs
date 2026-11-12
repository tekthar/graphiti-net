// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search_helpers.py and graphiti_core/prompts/prompt_helpers.py (to_prompt_json)
// (Graphiti v0.31.2) and changed for .NET.

using System.Globalization;
using System.Text;
using GraphitiNet.Models;

namespace GraphitiNet.Search;

/// <summary>Formats search results for an LLM, as upstream's <c>search_helpers.py</c>.</summary>
public static class SearchHelpers
{
    /// <summary>
    /// A fact's validity window, <c>"&lt;valid_at&gt; - &lt;invalid_at&gt;"</c>, with <c>date unknown</c> and
    /// <c>present</c> for missing dates. Dates print as Python's <c>str(datetime)</c>.
    /// </summary>
    public static string FormatEdgeDateRange(EntityEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var validAt = edge.ValidAt is { } v ? PythonStr(v) : "date unknown";
        var invalidAt = edge.InvalidAt is { } i ? PythonStr(i) : "present";
        return $"{validAt} - {invalidAt}";
    }

    /// <summary>
    /// Turns search results into one context string for an LLM: facts with their validity dates, entities, episodes
    /// and communities, each as JSON in tagged sections. The text matches upstream character for character.
    /// </summary>
    public static string SearchResultsToContextString(SearchResults searchResults)
    {
        ArgumentNullException.ThrowIfNull(searchResults);

        var factJson = searchResults.Edges.Select(edge => new (string, string?)[]
        {
            ("fact", edge.Fact),
            ("valid_at", edge.ValidAt is { } v ? PythonStr(v) : "None"),
            ("invalid_at", edge.InvalidAt is { } i ? PythonStr(i) : "Present"),
        });
        var entityJson = searchResults.Nodes.Select(node => new (string, string?)[]
        {
            ("entity_name", node.Name),
            ("summary", node.Summary),
        });
        var episodeJson = searchResults.Episodes.Select(episode => new (string, string?)[]
        {
            ("source_description", episode.SourceDescription),
            ("content", episode.Content),
        });
        var communityJson = searchResults.Communities.Select(community => new (string, string?)[]
        {
            ("community_name", community.Name),
            ("summary", community.Summary),
        });

        return $"""

                FACTS and ENTITIES represent relevant context to the current conversation.
                COMMUNITIES represent a cluster of closely related entities.

                These are the most relevant facts and their valid and invalid dates. Facts are considered valid
                between their valid_at and invalid_at dates. Facts with an invalid_at date of "Present" are considered valid.
                <FACTS>
                        {ToPromptJson(factJson)}
                </FACTS>
                <ENTITIES>
                        {ToPromptJson(entityJson)}
                </ENTITIES>
                <EPISODES>
                        {ToPromptJson(episodeJson)}
                </EPISODES>
                <COMMUNITIES>
                        {ToPromptJson(communityJson)}
                </COMMUNITIES>

            """;
    }

    /// <summary>Python's <c>str()</c> of an aware datetime: <c>2024-01-02 03:04:05[.ffffff]+00:00</c>.</summary>
    internal static string PythonStr(DateTimeOffset value)
    {
        var builder = new StringBuilder(value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var microseconds = (value.Ticks % TimeSpan.TicksPerSecond) / 10;
        if (microseconds != 0)
        {
            builder.Append('.').Append(microseconds.ToString("D6", CultureInfo.InvariantCulture));
        }

        var offset = value.Offset;
        builder.Append(offset < TimeSpan.Zero ? '-' : '+');
        var absolute = offset.Duration();
        builder.Append(absolute.Hours.ToString("D2", CultureInfo.InvariantCulture))
            .Append(':')
            .Append(absolute.Minutes.ToString("D2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>
    /// Python's <c>json.dumps(data, ensure_ascii=False)</c> for a list of string-valued objects: separators
    /// <c>", "</c> and <c>": "</c>, non-ASCII kept as is.
    /// </summary>
    private static string ToPromptJson(IEnumerable<(string Key, string? Value)[]> items)
    {
        var builder = new StringBuilder("[");
        var firstItem = true;
        foreach (var item in items)
        {
            if (!firstItem)
            {
                builder.Append(", ");
            }

            firstItem = false;
            builder.Append('{');
            for (var i = 0; i < item.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                AppendJsonString(builder, item[i].Key);
                builder.Append(": ");
                if (item[i].Value is { } value)
                {
                    AppendJsonString(builder, value);
                }
                else
                {
                    builder.Append("null");
                }
            }

            builder.Append('}');
        }

        return builder.Append(']').ToString();
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
