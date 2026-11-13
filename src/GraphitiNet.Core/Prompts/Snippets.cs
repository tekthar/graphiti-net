// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/snippets.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Prompts;

/// <summary>Text shared by several prompts.</summary>
public static class Snippets
{
    /// <summary>
    /// The maximum length of an entity summary, in characters (upstream <c>utils/text_utils.MAX_SUMMARY_CHARS</c>).
    /// </summary>
    public const int MaxSummaryChars = 1000;

    /// <summary><see cref="MaxSummaryChars"/> as text, so prompt text that includes it can be a constant.</summary>
    internal const string MaxSummaryCharsText = "1000";

    /// <summary>The summary guidelines included in the summary prompts.</summary>
    public const string SummaryInstructions = $"""
            Guidelines:
                    1. Output only factual content. Never explain what you're doing, why, or mention limitations or constraints.
                    2. Only use the provided messages, entity, and entity context to set attribute values.
                    3. Keep the summary information-dense and entity-specific. STATE FACTS DIRECTLY IN UNDER {Snippets.MaxSummaryCharsText} CHARACTERS.
                    4. Preserve all materially relevant names, roles, places, dates, counts, and temporal qualifiers that are explicitly supported.
                    5. Prefer compact factual sentences over vague thematic phrasing or meta-language.
                    6. When the durable fact is the content of what was said, state the content directly instead of narrating that it was said.
                    7. Use communication verbs only when the act of speaking, asking, sharing, presenting, announcing, or telling is itself the important fact.
                    8. Never use filler verbs like "mentioned", "described", "stated", "reported", "noted", "discussed", "referenced", or "indicated" unless the communication act itself is the fact.
                    9. Include temporal anchors when the messages provide them and they help ground the fact.
                    10. Begin with the entity name or a direct fact, not with "A", "An", "The", or "This is" unless that wording is part of the entity name.

                    Example summary:
                    BAD: "The context shows John ordered pizza. Due to length constraints, other details are omitted from this summary."
                    GOOD: "John ordered pepperoni pizza from Mario's at 7:30 PM and had it delivered to the office."
                    
            """;
}
