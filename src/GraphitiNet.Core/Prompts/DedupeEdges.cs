// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/dedupe_edges.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>Response of <c>dedupe_edges.resolve_edge</c>.</summary>
public sealed record EdgeDuplicate
{
    /// <summary>The idx values of existing facts the new fact duplicates.</summary>
    [JsonPropertyName("duplicate_facts")]
    [Description("List of idx values of duplicate facts (only from EXISTING FACTS range). Empty list if none.")]
    public required IReadOnlyList<int> DuplicateFacts { get; init; }

    /// <summary>The idx values of facts the new fact contradicts.</summary>
    [JsonPropertyName("contradicted_facts")]
    [Description("List of idx values of contradicted facts (from full idx range). Empty list if none.")]
    public required IReadOnlyList<int> ContradictedFacts { get; init; }
}

/// <summary>The <c>dedupe_edges</c> prompts: fact resolution.</summary>
public static class DedupeEdgesPrompts
{
    /// <summary>
    /// <c>dedupe_edges.resolve_edge</c>: finds duplicates and contradictions of a new fact. Context:
    /// <c>existing_edges</c>, <c>edge_invalidation_candidates</c>, <c>new_edge</c>.
    /// </summary>
    public static ChatPrompt ResolveEdge(IReadOnlyDictionary<string, object?> context) =>
        new(
            "You are a fact deduplication assistant. NEVER mark facts as contradicted unless the new fact explicitly makes the old fact false.",
            $"""

            NEVER mark facts as duplicates if they have key differences, particularly around numeric values, dates, or key qualifiers.

            Only mark a fact as contradicted when the NEW FACT and the existing fact cannot both be true.
            Do NOT treat additional activity, progress, status, or project-update facts as contradictions of role,
            ownership, responsibility, membership, or assignment facts. A person doing work on a project is
            compatible with that person remaining responsible for, owning, leading, or being the DRI for the project.

            IMPORTANT constraints:
            - duplicate_facts: ONLY idx values from EXISTING FACTS (NEVER include FACT INVALIDATION CANDIDATES)
            - contradicted_facts: idx values from EITHER list (EXISTING FACTS or FACT INVALIDATION CANDIDATES)
            - The idx values are continuous across both lists (INVALIDATION CANDIDATES start where EXISTING FACTS end)

            <EXISTING FACTS>
            {Str(context, "existing_edges")}
            </EXISTING FACTS>

            <FACT INVALIDATION CANDIDATES>
            {Str(context, "edge_invalidation_candidates")}
            </FACT INVALIDATION CANDIDATES>

            <NEW FACT>
            {Str(context, "new_edge")}
            </NEW FACT>

            You will receive TWO lists of facts with CONTINUOUS idx numbering across both lists.
            EXISTING FACTS are indexed first, followed by FACT INVALIDATION CANDIDATES.

            1. DUPLICATE DETECTION:
               - If the NEW FACT represents identical factual information as any fact in EXISTING FACTS, return those idx values in duplicate_facts.
               - If no duplicates, return an empty list for duplicate_facts.

            2. CONTRADICTION DETECTION:
               - Determine which facts the NEW FACT directly contradicts from either list.
               - A contradiction requires explicit evidence that the old fact stopped being true or was replaced,
                 such as clear termination, replacement, transfer, or mutually exclusive current-state language.
               - Do NOT infer contradiction from recency alone, episode dates, meeting dates, weekly snapshots,
                 section headings such as "updates" or "status changes", or a new event involving the same entities.
               - Do NOT mark a role/ownership/responsibility/assignment fact as contradicted merely because the
                 NEW FACT describes work performed, updates shipped, documents created, progress made, blockers,
                 next steps, or collaboration involving the same person and project.
               - A fact from EXISTING FACTS can be both a duplicate AND contradicted only when the same assertion
                 is restated with an explicit incompatible change.
               - Return all contradicted idx values in contradicted_facts.
               - If no contradictions, return an empty list for contradicted_facts.

            <EXAMPLE>
            EXISTING FACT: idx=0, "Alice joined Acme Corp in 2020"
            NEW FACT: "Alice joined Acme Corp in 2020"
            Result: duplicate_facts=[0], contradicted_facts=[] (identical factual information)

            EXISTING FACT: idx=1, "Alice works at Acme Corp as a software engineer"
            NEW FACT: "Alice works at Acme Corp as a senior engineer"
            Result: duplicate_facts=[], contradicted_facts=[1] (same relationship but updated title — contradiction, NOT a duplicate)

            EXISTING FACT: idx=2, "Bob ran 5 miles on Tuesday"
            NEW FACT: "Bob ran 3 miles on Wednesday"
            Result: duplicate_facts=[], contradicted_facts=[] (different events on different days — neither duplicate nor contradiction)
            </EXAMPLE>

            """);
}
