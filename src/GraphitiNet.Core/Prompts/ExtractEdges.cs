// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/extract_edges.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>
/// A fact found by <c>extract_edges.edge</c> (upstream <c>Edge</c>; renamed so it does not clash with
/// <see cref="Models.Edge"/>, but its schema title is still <c>Edge</c>).
/// </summary>
[DisplayName("Edge")]
public sealed record ExtractedEdge
{
    /// <summary>The source entity's name, from the prompt's ENTITIES.</summary>
    [JsonPropertyName("source_entity_name")]
    [Description("The name of the source entity from the ENTITIES list")]
    public required string SourceEntityName { get; init; }

    /// <summary>The target entity's name, from the prompt's ENTITIES.</summary>
    [JsonPropertyName("target_entity_name")]
    [Description("The name of the target entity from the ENTITIES list")]
    public required string TargetEntityName { get; init; }

    /// <summary>The relationship type, in SCREAMING_SNAKE_CASE.</summary>
    [JsonPropertyName("relation_type")]
    [Description("The type of relationship between the entities, in SCREAMING_SNAKE_CASE (e.g., WORKS_AT, LIVES_IN, IS_FRIENDS_WITH)")]
    public required string RelationType { get; init; }

    /// <summary>The fact, in natural language.</summary>
    [JsonPropertyName("fact")]
    [Description("A natural language description of the relationship between the entities, paraphrased from the source text")]
    public required string Fact { get; init; }

    /// <summary>When the fact became true, as ISO 8601 text, if known.</summary>
    [JsonPropertyName("valid_at")]
    [Description("The date and time when the relationship or assertion described by the edge fact became true or was established. Future-tense plans/promises use the statement reference time, not the future occurrence date. Use ISO 8601 format (YYYY-MM-DDTHH:MM:SS.SSSSSSZ)")]
    public string? ValidAt { get; init; }

    /// <summary>When the fact stopped being true, as ISO 8601 text, if known.</summary>
    [JsonPropertyName("invalid_at")]
    [Description("The date and time when the relationship described by the edge fact stopped being true or ended. Soft deadlines are not invalid_at values. Use ISO 8601 format (YYYY-MM-DDTHH:MM:SS.SSSSSSZ)")]
    public string? InvalidAt { get; init; }

    /// <summary>The episodes (0-based) the fact came from.</summary>
    [JsonPropertyName("episode_indices")]
    [Description("List of episode numbers (0-indexed) that this fact was derived from. When processing a single episode, this should be [0].")]
    public IReadOnlyList<int> EpisodeIndices { get; init; } = [0];
}

/// <summary>Response of <c>extract_edges.edge</c>.</summary>
public sealed record ExtractedEdges
{
    /// <summary>The facts found.</summary>
    [JsonPropertyName("edges")]
    public required IReadOnlyList<ExtractedEdge> Edges { get; init; }
}

/// <summary>Response of <c>extract_edges.extract_timestamps</c>.</summary>
[Description("Temporal bounds extracted from a fact.")]
public sealed record EdgeTimestamps
{
    /// <summary>When the fact became true, as ISO 8601 text, if known.</summary>
    [JsonPropertyName("valid_at")]
    [Description("When the fact became true. Future-tense plans/promises use the reference time, not the future occurrence date. ISO 8601 with Z suffix (e.g., 2025-04-30T00:00:00Z)")]
    public string? ValidAt { get; init; }

    /// <summary>When the fact stopped being true, as ISO 8601 text, if known.</summary>
    [JsonPropertyName("invalid_at")]
    [Description("When the fact stopped being true. Soft deadlines are not invalid_at values. ISO 8601 with Z suffix (e.g., 2025-04-30T00:00:00Z)")]
    public string? InvalidAt { get; init; }
}

/// <summary>Response of <c>extract_edges.extract_timestamps_batch</c>.</summary>
[Description("Temporal bounds for a batch of facts.")]
public sealed record BatchEdgeTimestamps
{
    /// <summary>One entry per fact, in input order.</summary>
    [JsonPropertyName("timestamps")]
    [Description("Timestamps for each fact, in the same order as the input facts")]
    public required IReadOnlyList<EdgeTimestamps> Timestamps { get; init; }
}

/// <summary>The <c>extract_edges</c> prompts: facts, fact attributes and timestamps.</summary>
public static class ExtractEdgesPrompts
{
    /// <summary>
    /// <c>extract_edges.edge</c>: facts between extracted entities. Context: <c>previous_episodes</c>,
    /// <c>episode_content</c>, <c>nodes</c>, <c>reference_time</c>, <c>custom_extraction_instructions</c>, optional
    /// <c>edge_types</c> and <c>strict_edge_types</c>.
    /// </summary>
    /// <remarks>
    /// Upstream passes <c>reference_time</c> as a <c>datetime</c>, which renders with <c>str()</c>
    /// (<c>2025-01-03 09:30:15+00:00</c>); pass a <see cref="DateTimeOffset"/> to get the same text.
    /// </remarks>
    public static ChatPrompt Edge(IReadOnlyDictionary<string, object?> context)
    {
        var edgeTypesSection = "";
        if (PythonText.IsTruthy(GetOrDefault(context, "edge_types")))
        {
            var relationTypeRules = PythonText.IsTruthy(GetOrDefault(context, "strict_edge_types", false))
                ? """
            - Use one of the provided fact_type_name values as the relation_type.
            - If a relationship does not match any FACT_TYPE, skip it. Do not derive a new relation_type.
            """
                : """
            - If FACT_TYPES are provided and the relationship matches one of the types (considering the entity type signature), use that fact_type_name as the `relation_type`.
            - Otherwise, derive a `relation_type` from the relationship predicate in SCREAMING_SNAKE_CASE (e.g., WORKS_AT, LIVES_IN, IS_FRIENDS_WITH).
            """;
            edgeTypesSection = $"""

            <FACT_TYPES>
            {Json(context, "edge_types")}
            </FACT_TYPES>

            # RELATION TYPE RULES

            {relationTypeRules}

            """;
        }

        return new(
            "You are an expert fact extractor that extracts fact triples from text. 1. Extracted fact triples should also be extracted with relevant date information. 2. The CURRENT_MESSAGE may contain multiple episodes, each with its own timestamp. Use each episode's timestamp to resolve temporal references within that episode. REFERENCE_TIME is a fallback for when no per-episode timestamp is available.",
            $"""

            <PREVIOUS_MESSAGES>
            {Json(context, "previous_episodes")}
            </PREVIOUS_MESSAGES>

            <CURRENT_MESSAGE>
            {Str(context, "episode_content")}
            </CURRENT_MESSAGE>

            <ENTITIES>
            {Json(context, "nodes")}
            </ENTITIES>

            <REFERENCE_TIME>
            {Str(context, "reference_time")}  # ISO 8601 (UTC); used to resolve relative time mentions
            </REFERENCE_TIME>
            {edgeTypesSection}
            # TASK
            Extract all factual relationships between the given ENTITIES based on the CURRENT MESSAGE.
            Only extract facts that:
            - involve two DISTINCT ENTITIES from the ENTITIES list,
            - are clearly stated or unambiguously implied in the CURRENT MESSAGE,
                and can be represented as edges in a knowledge graph.
            - Facts should include entity names rather than pronouns whenever possible.

            You may use information from the PREVIOUS MESSAGES only to disambiguate references or support continuity.


            {Str(context, "custom_extraction_instructions")}

            # EXTRACTION RULES

            1. **Entity Name Validation**: `source_entity_name` and `target_entity_name` must use only the `name` values from the ENTITIES list provided above.
               - **CRITICAL**: Using names not in the list will cause the edge to be rejected
            2. Each fact must involve two **distinct** entities — `source_entity_name` and `target_entity_name` NEVER refer to the same entity.
            3. Prefer facts that involve two distinct entities from the ENTITIES list. When a sentence describes a specific, concrete detail about a single entity (a brand name, a specific item, a physical description, a quantity, a location, a named activity), do NOT drop it. Instead, look for a second entity in the ENTITIES list that the detail relates to and form a proper triple (e.g., Entity -> OWNS -> item-entity, Entity -> LIVES_IN -> place-entity, Entity -> HAS_ATTRIBUTE -> detail-entity). Only skip the fact when no second entity in the ENTITIES list can anchor the detail.
               - BAD: "Alice feels happy" (vague single-entity state with no concrete detail — what is Alice happy about?)
               - GOOD: "Alice feels happy about Bob's promotion" → Alice -> FEELS_HAPPY_ABOUT -> Bob's promotion
               - GOOD: "Nate plays games on a Gamecube" → Nate -> PLAYS_GAMES_ON -> Gamecube (when "Gamecube" is in ENTITIES)
               - GOOD: "Alice congratulated Bob" (relationship between two entities), "Alice lives in Paris" (relationship between entity and place)
            4. Do not emit semantically redundant facts, even across episodes within the CURRENT_MESSAGE. However, if a later episode adds specific details to a previously stated fact (e.g., adding a brand name, a count, a color, a location, or any concrete attribute), extract the more detailed version as a NEW fact — it is NOT a duplicate. Only treat facts as duplicates when they convey the same specificity.
               - NOT a duplicate: "user plays video games" (Episode 0) vs. "user plays games on a Gamecube" (Episode 1) → extract the second, more detailed fact.
               - IS a duplicate: "user plays games on a Gamecube" (Episode 0) vs. "user plays Gamecube games" (Episode 1) → extract once, list both episodes in `episode_indices`.
            5. The `fact` MUST preserve all specific details from the source text: proper nouns, brand names, product names, model numbers, quantities, counts, colors, materials, physical descriptions, specific items, named locations, and named activities. Paraphrase the sentence structure but NEVER generalize:
               - NEVER generalize "Gamecube" to "gaming console", "Ford Mustang" to "car", "wool coat" to "coat", "red and purple lighting" to "lighting", "cracked windshield" to "car damage", or "three screenplays" to "several screenplays".
               - Do not verbatim quote the original text, but every concrete noun, number, and descriptor in the source should survive into the `fact`.
            6. Use `REFERENCE_TIME` to resolve vague or relative temporal expressions (e.g., "last week"). When the CURRENT_MESSAGE contains multiple episodes with per-episode timestamps, prefer the timestamp of the specific episode the fact originates from.
            7. Do **not** hallucinate or infer temporal bounds from unrelated events.

            # DATETIME RULES

            - Use ISO 8601 with "Z" suffix (UTC) (e.g., 2025-04-30T00:00:00Z).
            - If the fact is ongoing (present tense), set `valid_at` to the timestamp of the episode the fact originates from. If no per-episode timestamp is available, use REFERENCE_TIME.
            - If the fact is a future-tense plan, promise, scheduled event, or deadline, set `valid_at` to the episode timestamp / REFERENCE_TIME. Do NOT set `valid_at` to the future occurrence date.
            - Preserve future occurrence dates and deadlines in the `fact` text. Do NOT use a soft deadline ("by March 2026", "by the end of Q3 2025") as `invalid_at`.
            - Use a future date as `invalid_at` only when the fact describes an ongoing state that ends then (for example, "is unavailable until December 25, 2024" or "works at Acme until January 15, 2027").
            - If a change/termination is expressed, set `invalid_at` to the relevant timestamp.
            - Leave both fields `null` if no explicit or resolvable time is stated.
            - If only a date is mentioned (no time), assume 00:00:00.
            - If only a year is mentioned, use January 1st at 00:00:00.
            - Examples with REFERENCE_TIME 2020-01-01T00:00:00Z:
              - "Emma will relocate to Tokyo on December 25, 2022" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.
              - "Alice will complete the project by July 30, 2024" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.
              - "The team will ship version 2.0 by the end of Q3 2025" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.
                    
            """);
    }

    /// <summary>
    /// <c>extract_edges.extract_attributes</c>: a fact's custom attributes. Context: <c>fact</c>,
    /// <c>reference_time</c>, <c>existing_attributes</c>.
    /// </summary>
    public static ChatPrompt ExtractAttributes(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are a fact attribute extraction specialist. You ONLY emit attribute values that are explicitly stated in the FACT or already present in EXISTING ATTRIBUTES. You output strictly the JSON specified by the response schema — no reasoning, no explanation, no commentary in any field.",
            $"""
            Given the following FACT, its REFERENCE TIME, and any EXISTING ATTRIBUTES, update the attributes.

            HARD RULES — violating any of these is a failure:

            1. Each attribute value MUST be one of:
               (a) a clean value copied or directly normalized from the FACT,
               (b) the existing value already in EXISTING ATTRIBUTES (preserved unchanged), or
               (c) null / omitted, when neither (a) nor (b) applies.

            2. NEVER write reasoning, justification, or commentary into any field. Specifically:
               - NEVER include parenthetical explanations like "(implied by ...)", "(Context: ...)",
                 "(not explicitly stated ...)", "(based on ...)".
               - NEVER include first-person or deliberative phrases like "I should...", "However...",
                 "Sticking to...", "Since no...", "the instruction is to...", "must be kept...".
               - NEVER list alternatives or candidates inside one field ("X, or Y, or maybe Z").
               - NEVER explain why a value is null. If unknown, set the field to null and stop.

            3. Each attribute schema description tells you the FORMAT a real value should take. The
               description text is NEVER itself a value. NEVER copy schema description text into the field.

            4. The literal strings "null", "N/A", "Not specified", "unknown", "none", "not provided",
               or any sentence describing absence are NOT valid values. If no value is supported by
               the FACT, set the field to null (or omit it) — do not write a sentence.

            5. Each attribute value must be a short, well-formed instance of the type the field
               describes. If you cannot produce a clean value of that type from the FACT, the field is null.

            6. Use REFERENCE TIME to resolve any relative temporal expressions in the fact.

            7. Preserve existing attribute values unless the FACT explicitly provides a new value.

            <FACT>
            {Str(context, "fact")}
            </FACT>

            <REFERENCE TIME>
            {Str(context, "reference_time")}
            </REFERENCE TIME>

            <EXISTING ATTRIBUTES>
            {Json(context, "existing_attributes")}
            </EXISTING ATTRIBUTES>

            """);

    /// <summary>
    /// <c>extract_edges.extract_timestamps</c>: when one fact was true. Context: <c>fact</c>,
    /// <c>reference_time</c>.
    /// </summary>
    public static ChatPrompt ExtractTimestamps(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You extract temporal bounds from facts. NEVER hallucinate dates.",
            $"""
            Given a FACT and its REFERENCE TIME, determine when the fact became true
            (valid_at) and when it stopped being true (invalid_at).

            Rules:
            - Resolve relative expressions ("last week", "2 years ago", "yesterday") using REFERENCE TIME.
            - If the fact is ongoing (present tense), set valid_at to REFERENCE TIME.
            - If the fact is a future-tense plan, promise, scheduled event, or deadline, set valid_at to REFERENCE TIME. Do NOT set valid_at to the future occurrence date.
            - Preserve future occurrence dates and deadlines in the fact text. Do NOT use a soft deadline ("by March 2026", "by the end of Q3 2025") as invalid_at.
            - Use a future date as invalid_at only when the fact describes an ongoing state that ends then (for example, "is unavailable until December 25, 2024" or "works at Acme until January 15, 2027").
            - If a change or end is expressed, set invalid_at to the relevant time.
            - Leave both null if no time is stated or resolvable.
            - If only a date is mentioned (no time), assume 00:00:00.
            - Use ISO 8601 with Z suffix (e.g., 2025-04-30T00:00:00Z).
            - Do NOT hallucinate or infer dates from unrelated events.
            - Examples with REFERENCE_TIME 2020-01-01T00:00:00Z:
              - "Emma will relocate to Tokyo on December 25, 2022" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.
              - "Alice will complete the project by July 30, 2024" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.

            <FACT>
            {Str(context, "fact")}
            </FACT>

            <REFERENCE TIME>
            {Str(context, "reference_time")}
            </REFERENCE TIME>

            """);

    /// <summary>
    /// <c>extract_edges.extract_timestamps_batch</c>: when each of several facts was true. Context: <c>facts</c>.
    /// </summary>
    public static ChatPrompt ExtractTimestampsBatch(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You extract temporal bounds from facts. NEVER hallucinate dates.",
            $"""
            Given a list of FACTS with their REFERENCE TIMES, determine when each fact
            became true (valid_at) and when it stopped being true (invalid_at).

            Rules:
            - Resolve relative expressions ("last week", "2 years ago", "yesterday") using each fact's REFERENCE TIME.
            - If the fact is ongoing (present tense), set valid_at to its REFERENCE TIME.
            - If the fact is a future-tense plan, promise, scheduled event, or deadline, set valid_at to its REFERENCE TIME. Do NOT set valid_at to the future occurrence date.
            - Preserve future occurrence dates and deadlines in the fact text. Do NOT use a soft deadline ("by March 2026", "by the end of Q3 2025") as invalid_at.
            - Use a future date as invalid_at only when the fact describes an ongoing state that ends then (for example, "is unavailable until December 25, 2024" or "works at Acme until January 15, 2027").
            - If a change or end is expressed, set invalid_at to the relevant time.
            - Leave both null if no time is stated or resolvable.
            - If only a date is mentioned (no time), assume 00:00:00.
            - Use ISO 8601 with Z suffix (e.g., 2025-04-30T00:00:00Z).
            - Do NOT hallucinate or infer dates from unrelated events.
            - Examples with REFERENCE_TIME 2020-01-01T00:00:00Z:
              - "Emma will relocate to Tokyo on December 25, 2022" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.
              - "Alice will complete the project by July 30, 2024" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.
              - "The team will ship version 2.0 by the end of Q3 2025" -> valid_at 2020-01-01T00:00:00Z, invalid_at null.

            Return one timestamps entry per fact, in the same order.

            <FACTS>
            {Json(context, "facts")}
            </FACTS>

            """);
}
