// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/dedupe_nodes.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>The resolution of one extracted entity against existing entities.</summary>
public sealed record NodeDuplicate
{
    /// <summary>The id of the entity in the prompt's ENTITIES.</summary>
    [JsonPropertyName("id")]
    [Description("integer id of the entity")]
    public required int Id { get; init; }

    /// <summary>The best full name for the entity.</summary>
    [JsonPropertyName("name")]
    [Description("Name of the entity. Should be the most complete and descriptive name of the entity. Do not include any JSON formatting in the Entity name such as {}.")]
    public required string Name { get; init; }

    /// <summary>The <c>candidate_id</c> of the matching existing entity, or -1 when there is none.</summary>
    [JsonPropertyName("duplicate_candidate_id")]
    [Description("candidate_id of the matching EXISTING ENTITY, or -1 if no duplicate exists.")]
    public required int DuplicateCandidateId { get; init; }
}

/// <summary>Response of <c>dedupe_nodes.nodes</c>.</summary>
public sealed record NodeResolutions
{
    /// <summary>One resolution per extracted entity.</summary>
    [JsonPropertyName("entity_resolutions")]
    [Description("List of resolved nodes")]
    public required IReadOnlyList<NodeDuplicate> EntityResolutions { get; init; }
}

/// <summary>The <c>dedupe_nodes</c> prompts: entity resolution.</summary>
public static class DedupeNodesPrompts
{
    /// <summary>
    /// <c>dedupe_nodes.node</c>: resolves one entity. Context: <c>previous_episodes</c>, <c>episode_content</c>,
    /// <c>extracted_node</c>, <c>entity_type_description</c>, <c>existing_nodes</c>.
    /// </summary>
    public static ChatPrompt Node(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are an entity deduplication assistant. NEVER fabricate entity names or mark distinct entities as duplicates.",
            $$"""

            <PREVIOUS MESSAGES>
            {{Json(context, "previous_episodes")}}
            </PREVIOUS MESSAGES>

            <CURRENT MESSAGE>
            {{Str(context, "episode_content")}}
            </CURRENT MESSAGE>

            <NEW ENTITY>
            {{Json(context, "extracted_node")}}
            </NEW ENTITY>

            <ENTITY TYPE DESCRIPTION>
            {{Json(context, "entity_type_description")}}
            </ENTITY TYPE DESCRIPTION>

            <EXISTING ENTITIES>
            {{Json(context, "existing_nodes")}}
            </EXISTING ENTITIES>

            Entities should only be considered duplicates if they refer to the *same real-world object or concept*.
            Semantic Equivalence: if a descriptive label in EXISTING ENTITIES clearly refers to a named entity in context, treat them as duplicates.

            NEVER mark entities as duplicates if:
            - They are related but distinct.
            - They have similar names or purposes but refer to separate instances or concepts.

            Task:
            1. Compare the NEW ENTITY against each EXISTING ENTITY (identified by `candidate_id`).
            2. If it refers to the same real-world object or concept, return the `candidate_id` of that match.
            3. Return `duplicate_candidate_id = -1` when there is no match or you are unsure.

            <EXAMPLE>
            NEW ENTITY: "Sam" (Person)
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "Sam", "entity_types": ["Person"], "summary": "Sam enjoys hiking and photography"}]
            Result: duplicate_candidate_id = 0 (same person referenced in conversation)

            NEW ENTITY: "NYC"
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "New York City", "entity_types": ["Location"]}, {"candidate_id": 1, "name": "New York Knicks", "entity_types": ["Organization"]}]
            Result: duplicate_candidate_id = 0 (same location, abbreviated name)

            NEW ENTITY: "Java" (programming language)
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "Java", "entity_types": ["Location"], "summary": "An island in Indonesia"}]
            Result: duplicate_candidate_id = -1 (same name but distinct real-world things)

            NEW ENTITY: "Marco's car"
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "Marco's vehicle", "entity_types": ["Entity"], "summary": "Marco drives a red sedan."}]
            Result: duplicate_candidate_id = 0 (synonym — "car" and "vehicle" refer to the same thing, same possessor)
            </EXAMPLE>

            """);

    /// <summary>
    /// <c>dedupe_nodes.nodes</c>: resolves several entities at once. Context: <c>previous_episodes</c>,
    /// <c>episode_content</c>, <c>extracted_nodes</c> (a list), <c>existing_nodes</c>.
    /// </summary>
    public static ChatPrompt Nodes(IReadOnlyDictionary<string, object?> context)
    {
        var count = PythonText.Len(Get(context, "extracted_nodes"));
        return new(
            "You are an entity deduplication assistant. NEVER fabricate entity names or mark distinct entities as duplicates.",
            $$"""

            <PREVIOUS MESSAGES>
            {{Json(context, "previous_episodes")}}
            </PREVIOUS MESSAGES>

            <CURRENT MESSAGE>
            {{Str(context, "episode_content")}}
            </CURRENT MESSAGE>

            <ENTITIES>
            {{Json(context, "extracted_nodes")}}
            </ENTITIES>

            <EXISTING ENTITIES>
            {{Json(context, "existing_nodes")}}
            </EXISTING ENTITIES>

            Each of the above ENTITIES was extracted from the CURRENT MESSAGE.
            For each entity, determine if it is a duplicate of any EXISTING ENTITY.
            Entities should only be considered duplicates if they refer to the *same real-world object or concept*.

            NEVER mark entities as duplicates if:
            - They are related but distinct.
            - They have similar names or purposes but refer to separate instances or concepts.

            Task:
            ENTITIES contains {{count}} entities with IDs 0 through {{count - 1}}.
            Your response MUST include EXACTLY {{count}} resolutions with IDs 0 through {{count - 1}}. Do not skip or add IDs.

            For every entity, provide:
            - `id`: integer id from ENTITIES
            - `name`: the best full name for the entity (preserve the original name unless a duplicate has a more complete name)
            - `duplicate_candidate_id`: the `candidate_id` of the EXISTING ENTITY that is the best duplicate match, or -1 if there is no duplicate

            <EXAMPLE>
            ENTITY: "Sam" (Person)
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "Sam", "entity_types": ["Person"], "summary": "Sam enjoys hiking and photography"}]
            Result: duplicate_candidate_id = 0 (same person referenced in conversation)

            ENTITY: "NYC"
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "New York City", "entity_types": ["Location"]}, {"candidate_id": 1, "name": "New York Knicks", "entity_types": ["Organization"]}]
            Result: duplicate_candidate_id = 0 (same location, abbreviated name)

            ENTITY: "Java" (programming language)
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "Java", "entity_types": ["Location"], "summary": "An island in Indonesia"}]
            Result: duplicate_candidate_id = -1 (same name but distinct real-world things)

            ENTITY: "Marco's car"
            EXISTING ENTITIES: [{"candidate_id": 0, "name": "Marco's vehicle", "entity_types": ["Entity"], "summary": "Marco drives a red sedan."}]
            Result: duplicate_candidate_id = 0 (synonym — "car" and "vehicle" refer to the same thing, same possessor)
            </EXAMPLE>

            """);
    }

    /// <summary><c>dedupe_nodes.node_list</c>: groups duplicate nodes by uuid. Context: <c>nodes</c>.</summary>
    public static ChatPrompt NodeList(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are an entity deduplication assistant that groups duplicate nodes by UUID.",
            $$"""

            Given the following context, deduplicate a list of nodes:

            <NODES>
            {{Json(context, "nodes")}}
            </NODES>

            Task:
            1. Group nodes together such that all duplicate nodes are in the same list of uuids.
            2. All duplicate uuids should be grouped together in the same list.
            3. Also return a new summary that synthesizes the summaries into a new short summary.

            Guidelines:
            1. Each uuid from the list of nodes should appear EXACTLY once in your response.
            2. If a node has no duplicates, it should appear in the response in a list of only one uuid.

            <EXAMPLE>
            Input nodes:
            [
              {"uuid": "a1", "name": "NYC", "summary": "New York City"},
              {"uuid": "b2", "name": "New York City", "summary": "The city of New York"},
              {"uuid": "c3", "name": "Los Angeles", "summary": "City in California"}
            ]

            Result:
            [
              {"uuids": ["a1", "b2"], "summary": "New York City, also known as NYC"},
              {"uuids": ["c3"], "summary": "City in California"}
            ]
            </EXAMPLE>

            """);
}
