// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/extract_nodes_and_edges.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>An entity found by <c>extract_nodes_and_edges.extract_message</c>.</summary>
[Description("Entity extracted by the combined node+edge extraction prompt.")]
public sealed record CombinedEntity
{
    /// <summary>The entity's name.</summary>
    [JsonPropertyName("name")]
    [Description("Name of the extracted entity")]
    public required string Name { get; init; }

    /// <summary>The id of the entity type from the prompt's ENTITY TYPES.</summary>
    [JsonPropertyName("entity_type_id")]
    [Description("ID of the classified entity type. Must be one of the provided entity_type_id integers.")]
    public required int EntityTypeId { get; init; }
}

/// <summary>A fact found by <c>extract_nodes_and_edges.extract_message</c>.</summary>
[Description("Relationship fact extracted by the combined node+edge extraction prompt.")]
public sealed record CombinedFact
{
    /// <summary>The source entity's name, one of the extracted entities.</summary>
    [JsonPropertyName("source_entity_name")]
    [Description("The name of the source entity. MUST exactly match an entity `name` in extracted_entities (including value-like endpoints such as versions).")]
    public required string SourceEntityName { get; init; }

    /// <summary>The target entity's name, one of the extracted entities.</summary>
    [JsonPropertyName("target_entity_name")]
    [Description("The name of the target entity. MUST exactly match an entity `name` in extracted_entities (including value-like endpoints such as versions).")]
    public required string TargetEntityName { get; init; }

    /// <summary>The relationship type, in SCREAMING_SNAKE_CASE.</summary>
    [JsonPropertyName("relation_type")]
    [Description("The type of relationship between the entities, in SCREAMING_SNAKE_CASE (e.g., WORKS_AT, LIVES_IN, IS_FRIENDS_WITH)")]
    public required string RelationType { get; init; }

    /// <summary>The fact, as one self-contained sentence.</summary>
    [JsonPropertyName("fact")]
    [Description("A self-contained natural language description of the relationship, paraphrased from the source text with all specific details preserved")]
    public required string Fact { get; init; }

    /// <summary>The episodes (0-based) the fact came from.</summary>
    [JsonPropertyName("episode_indices")]
    [Description("List of episode numbers (0-indexed) that this fact was derived from. When processing a single episode, this should be [0].")]
    public IReadOnlyList<int> EpisodeIndices { get; init; } = [0];
}

/// <summary>Response of <c>extract_nodes_and_edges.extract_message</c>.</summary>
[Description("Combined node and edge extraction response.")]
public sealed record CombinedExtraction
{
    /// <summary>The entities found.</summary>
    [JsonPropertyName("extracted_entities")]
    [Description("List of extracted entities")]
    public required IReadOnlyList<CombinedEntity> Entities { get; init; }

    /// <summary>The facts found.</summary>
    [JsonPropertyName("edges")]
    [Description("List of extracted relationship facts")]
    public required IReadOnlyList<CombinedFact> Edges { get; init; }
}

/// <summary>The <c>extract_nodes_and_edges</c> prompts: entities and facts in one call.</summary>
public static class ExtractNodesAndEdgesPrompts
{
    private const string SystemPrompt = "You build the long-term memory of an AI agent. From each conversation you extract the entities and the relationship facts that the agent will need later to answer questions, personalize responses, and continue work. The original conversation will not be available later: only your entities and facts will remain, so each fact must stand alone.";

    private const string C2InstructionText = """
            # Goal
            Turn CURRENT MESSAGES into a knowledge graph: a list of entities and a list of facts (edges) between them. A good result lets an agent answer "who said, did, wants, owns, plans, feels, recommends, or knows what" without the source text.

            # What good output looks like
            - Complete: every episode, post, and speaker with real content is covered, including the later ones in a long input. Preferences, opinions, plans, commitments, requests, questions, emotions, experiences, advice, and results all count. Skip only content-free turns ("Hi!", "Thanks!", "Bye!").
            - Attributed: when a named person, author, account, or organization states, recommends, or does something, that actor is the source entity, and the fact text names the actor.
            - Self-contained: each fact is one plain sentence that uses entity names (no pronouns), keeps the specific details (numbers, dates, prices, versions, names), and makes sense with no other context.
            - Compact: one fact for one statement. Keep a list of items from one statement in one fact ("Marina plans to change into a sweater and trousers."), unless each item has its own details. Do not make a fact or an entity for each list item. Not redundant: If the same fact appears in more than one episode, emit it once and list all of its episode indices.

            # Entities
            - Speakers. Name each speaker with the most complete name available. When a message header has profile fields (first_name and last_name, given_name and family_name, user_alias, screen_name), use the full personal name, for example "Marisol Beckett". Use "User" only when no name is available.
            - Other entities: named people, organizations, places, products, files, tickets, projects, events, documents, and named or described objects, plus hobbies and activities that someone does ("road cycling", "watercolor painting"). Use the most specific form mentioned, at most 5 words, as a noun phrase. For a person's possession, pet, project, or creation, use a possessive name ("Calvin's guitar", "Marina's sweater"), not the bare noun.
            - Do not make entities from: pronouns or unresolved references ("the city", "this issue"); vague abstractions ("growth", "life", "time"); clock times, dates, quantities, prices, durations, measurements, or coordinates (keep these in the fact text, unless the value is itself an edge endpoint such as a version string); answer options or template tokens ("Agree", "Strongly disagree"); imperative tips ("Buy in bulk" becomes the topic "bulk buying"); quoted slogans or idioms; tutorial example values ("/path/to/source", "file.txt").
            - Each entity appears once. Classify it with an entity_type_id from ENTITY TYPES. Extract entities only from CURRENT MESSAGES; use PREVIOUS MESSAGES and [CONTEXT EPISODE] messages only to resolve references.

            # Facts
            - source_entity_name and target_entity_name must be names from extracted_entities. If an endpoint is a value such as a version, add it as an entity.
            - Connect the actor directly to the thing: "Calvin -> PERFORMED_IN -> Tokyo", "Mira Sen -> RECOMMENDS_METRICS_FOR -> Ember & Oak". Do not route a relationship through descriptive nouns ("city lights -> LOCATED_IN -> Tokyo"), and do not turn an actor's claim into a fact between two nouns that the actor mentioned.
            - Use a self-referencing fact (source and target are the same entity) only when a statement is about one entity and no other extracted entity fits, for example a routine, a feeling, or a health state ("Evan has a knee injury.").
            - When a fact involves two extracted entities, use both as source and target.
            - Use PREVIOUS MESSAGES and [CONTEXT EPISODE] messages to resolve what the current message refers to, and write the resolved fact ("all the hard work paid off" becomes what paid off). [CONTEXT EPISODE] messages are conversation turns between the numbered episodes, shown so you can read the conversation in order.
            - Set episode_indices to the 0-based [Episode N] numbers that the fact comes from. Messages headed [CONTEXT EPISODE] are not numbered episodes: do not extract facts from them and do not give them an episode index.
            - Write each entity name and each fact in the dominant language of its source episode. Keep proper names as written. Write relation_type as an English SCREAMING_SNAKE_CASE label.
            - A fact states what the source says. Do not add reasoning, hedges ("appears to", "suggests"), or commentary.

            # Output
            Return only the JSON object: {"extracted_entities":[{"name":"...","entity_type_id":0}],"edges":[{"source_entity_name":"...","target_entity_name":"...","relation_type":"...","fact":"...","episode_indices":[0]}]}.
            """;

    /// <summary>
    /// <c>extract_nodes_and_edges.extract_message</c>: entities and the facts between them. Context:
    /// <c>entity_types</c>, <c>previous_episodes</c>, <c>episode_content</c>, optional <c>edge_types</c>,
    /// <c>strict_edge_types</c> and <c>custom_extraction_instructions</c>.
    /// </summary>
    public static ChatPrompt ExtractMessage(IReadOnlyDictionary<string, object?> context) =>
        new(SystemPrompt, BuildExtractUserPrompt(context));

    private static string BuildExtractUserPrompt(IReadOnlyDictionary<string, object?> context) =>
        $"""
            {C2InstructionText}

            <ENTITY TYPES>
            {Str(context, "entity_types")}
            </ENTITY TYPES>
            {BuildEdgeTypesSection(GetOrDefault(context, "edge_types"), GetOrDefault(context, "strict_edge_types", false))}
            {BuildCustomInstructionsSection(GetOrDefault(context, "custom_extraction_instructions"))}
            <PREVIOUS MESSAGES>
            {Json(context, "previous_episodes")}
            </PREVIOUS MESSAGES>

            <CURRENT MESSAGES>
            {Str(context, "episode_content")}
            </CURRENT MESSAGES>

            """;

    /// <summary>The optional FACT TYPES section.</summary>
    private static string BuildEdgeTypesSection(object? edgeTypes, object? strictEdgeTypes)
    {
        if (!PythonText.IsTruthy(edgeTypes))
        {
            return "";
        }

        var relationTypeRules = PythonText.IsTruthy(strictEdgeTypes)
            ? """
            - Use one of the provided fact_type_name values as the relation_type.
            - If a relationship does not match any FACT TYPE, skip it. Do not derive a new relation_type.
            """
            : """
            - When a relationship matches a FACT TYPE, use that fact_type_name as the relation_type.
            - If no FACT TYPE fits, derive a relation_type in SCREAMING_SNAKE_CASE
              (e.g., WORKS_AT, LIVES_IN, IS_FRIENDS_WITH).
            """;
        return $"""

            <FACT TYPES>
            {PromptHelpers.ToPromptJson(edgeTypes)}
            </FACT TYPES>
            RELATION TYPE RULES:
            {relationTypeRules}

            """;
    }

    /// <summary>The optional CUSTOM INSTRUCTIONS section.</summary>
    private static string BuildCustomInstructionsSection(object? instructions)
    {
        if (!PythonText.IsTruthy(instructions))
        {
            return "";
        }

        var customExtractionInstructions = PythonText.Str(instructions);
        return $"""

            <CUSTOM INSTRUCTIONS>
            {customExtractionInstructions}
            </CUSTOM INSTRUCTIONS>
            CUSTOM INSTRUCTION RULES:
            - CUSTOM INSTRUCTIONS come from the owner of this graph. Apply them. When a CUSTOM INSTRUCTION conflicts with a rule above, the CUSTOM INSTRUCTION wins.

            """;
    }
}
