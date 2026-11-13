// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/summarize_nodes.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>Response of <c>summarize_nodes.summarize_pair</c> and <c>summarize_nodes.summarize_context</c>.</summary>
public sealed record Summary
{
    /// <summary>The summary (upstream field <c>summary</c>).</summary>
    [JsonPropertyName("summary")]
    [Description($"""
            Summary containing the important information about the entity. Under {Snippets.MaxSummaryCharsText} characters
            """)]
    public required string Text { get; init; }
}

/// <summary>Response of <c>summarize_nodes.summary_description</c>.</summary>
public sealed record SummaryDescription
{
    /// <summary>A one-sentence description of the summary.</summary>
    [JsonPropertyName("description")]
    [Description("One sentence description of the provided summary")]
    public required string Description { get; init; }
}

/// <summary>The <c>summarize_nodes</c> prompts: entity and community summaries.</summary>
public static class SummarizeNodesPrompts
{
    /// <summary>
    /// <c>summarize_nodes.summarize_pair</c>: merges two summaries. Context: <c>node_summaries</c>.
    /// </summary>
    public static ChatPrompt SummarizePair(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are a helpful assistant that combines summaries into a single dense factual summary.",
            $"""

                    Synthesize the information from the following two summaries into a single information-dense summary.

                    IMPORTANT:
                    - Preserve all materially relevant names, roles, places, dates, counts, and changes over time that are explicitly supported.
                    - Prefer compact factual sentences over vague thematic phrasing.
                    - When the durable fact is the content of what was said, state the content directly instead of narrating that it was said.
                    - Use communication verbs only when the act of speaking, asking, sharing, presenting, or announcing is itself the important fact.
                    - Avoid filler verbs like "mentioned", "described", "stated", "reported", "noted", "discussed", "referenced", and "indicated" unless the communication act itself matters.
                    - SUMMARIES MUST BE LESS THAN {Snippets.MaxSummaryCharsText} CHARACTERS.

                    Summaries:
                    {Json(context, "node_summaries")}
                    
            """);

    /// <summary>
    /// <c>summarize_nodes.summarize_context</c>: summary and attributes of an entity. Context:
    /// <c>previous_episodes</c>, <c>episode_content</c>, <c>node_name</c>, <c>node_summary</c>, <c>attributes</c>.
    /// </summary>
    public static ChatPrompt SummarizeContext(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are a helpful assistant that generates detailed, information-dense summaries and attributes from provided text.",
            $"""

                    Given the MESSAGES and the ENTITY name, create a summary for the ENTITY. Your summary must only use
                    information from the provided MESSAGES. Your summary should also only contain information relevant to the
                    provided ENTITY.

                    In addition, extract any values for the provided entity properties based on their descriptions.
                    If the value of the entity property cannot be found in the current context, set the value of the property to the Python value None.

                    {Snippets.SummaryInstructions}

                    <MESSAGES>
                    {Json(context, "previous_episodes")}
                    {Json(context, "episode_content")}
                    </MESSAGES>

                    <ENTITY>
                    {Str(context, "node_name")}
                    </ENTITY>

                    <ENTITY CONTEXT>
                    {Str(context, "node_summary")}
                    </ENTITY CONTEXT>

                    <ATTRIBUTES>
                    {Json(context, "attributes")}
                    </ATTRIBUTES>
                    
            """);

    /// <summary>
    /// <c>summarize_nodes.summary_description</c>: a one-sentence description of a summary. Context:
    /// <c>summary</c>.
    /// </summary>
    public static ChatPrompt SummaryDescription(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are a helpful assistant that describes provided contents in a single sentence.",
            $"""

                    Create a short one sentence description of the summary that explains what kind of information is summarized.
                    Summaries must be under {Snippets.MaxSummaryCharsText} characters.

                    Summary:
                    {Json(context, "summary")}
                    
            """);
}
