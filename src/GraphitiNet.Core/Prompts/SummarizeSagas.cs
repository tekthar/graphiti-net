// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/summarize_sagas.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>Response of <c>summarize_sagas.summarize_saga</c>.</summary>
public sealed record SagaSummary
{
    /// <summary>The knowledge brief.</summary>
    [JsonPropertyName("summary")]
    [Description($"""
            Factual knowledge brief. Under {Snippets.MaxSummaryCharsText} characters.
            """)]
    public required string Summary { get; init; }
}

/// <summary>The <c>summarize_sagas</c> prompts.</summary>
public static class SummarizeSagasPrompts
{
    /// <summary>
    /// <c>summarize_sagas.summarize_saga</c>: a knowledge brief of a saga's messages. Context, all optional:
    /// <c>saga_name</c> (default <c>Unknown</c>), <c>existing_summary</c>, <c>episodes</c> (a list of strings).
    /// </summary>
    /// <exception cref="ArgumentException">An item of <c>episodes</c> is not a string.</exception>
    public static ChatPrompt SummarizeSaga(IReadOnlyDictionary<string, object?> context)
    {
        var sagaName = PythonText.Str(GetOrDefault(context, "saga_name", "Unknown"));
        var existingSummaryValue = GetOrDefault(context, "existing_summary", "");
        var episodes = GetOrDefault(context, "episodes", Array.Empty<string>());

        var episodesText = PythonText.IsTruthy(episodes) ? JoinStrings("\n---\n", episodes) : "(no messages)";

        var existingSummarySection = "";
        if (PythonText.IsTruthy(existingSummaryValue))
        {
            var existingSummary = PythonText.Str(existingSummaryValue);
            existingSummarySection = $"""

            <EXISTING_KNOWLEDGE>
            {existingSummary}
            </EXISTING_KNOWLEDGE>
            The EXISTING_KNOWLEDGE contains previously extracted facts. Merge any new facts from MESSAGES into it. When newer messages contradict older facts, prefer the newer fact. If MESSAGES add no new durable facts, return the existing knowledge unchanged.

            """;
        }

        return new(
            $"""
            You extract durable knowledge from message threads. Output a factual knowledge brief — facts, decisions, preferences, plans, entities, and relationships — that stands alone without reference to the original messages. Stay under {Snippets.MaxSummaryCharsText} characters.
            """,
            $"""
            NEVER use meta-language verbs: "mentioned", "discussed", "noted", "stated", "described", "referenced", "indicated", "reported", "talked about", "brought up" — these describe conversational dynamics, not knowledge. State facts directly instead.
            NEVER refer to the messages, conversation, thread, or participants' communicative acts. The output must read as if no conversation happened — only the facts matter.
            NEVER begin with "This conversation", "The thread", "In this thread", or "The discussion".
            NEVER infer preferences or habits from a single passing mention. When a person explicitly states a preference ("I prefer X", "I love X", "I always do X"), capture it as a stated preference attributed to that person.

            Your task: extract all durable knowledge from the MESSAGES below and produce a factual knowledge brief for the topic "{sagaName}".

            Capture explicitly stated:
            - Facts and concrete details (names, dates, numbers, locations)
            - Decisions and their outcomes
            - Preferences and requirements (when a person explicitly claims them)
            - Plans, next steps, and commitments
            - Relationships between entities (who works where, who owns what)
            - State changes (what was X, now is Y)

            Write 2-6 dense sentences. Use third person. Preserve all names, dates, counts, and temporal qualifiers. Lead with the most important fact or decision.
            {existingSummarySection}
            <MESSAGES>
            {episodesText}
            </MESSAGES>

            <EXAMPLES>
            MESSAGES: "Jordan: We decided to move the deployment to March 15 instead of March 8. The staging environment isn't ready.\n---\nPriya: Agreed. I'll update the client timeline. We also need to switch from PostgreSQL to CockroachDB for the multi-region requirement."
            GOOD: "Deployment moved from March 8 to March 15 because the staging environment is not ready. Priya owns updating the client timeline. The database is switching from PostgreSQL to CockroachDB to support the multi-region requirement."
            BAD: "Jordan mentioned moving the deployment date. Priya discussed updating the timeline and talked about switching databases. The team noted staging issues."
            </EXAMPLES>

            <EXAMPLES>
            MESSAGES: "Alex: I tried the new Thai place on Elm Street last night — the pad see ew was incredible. Definitely going back.\n---\nMina: Oh nice, I've been wanting to try that. Is it the one next to the bookstore?\n---\nAlex: Yeah, Siam Kitchen. They're open until 11 PM on weekends."
            GOOD: "Siam Kitchen is a Thai restaurant on Elm Street, next to a bookstore, open until 11 PM on weekends. Alex considers the pad see ew excellent."
            BAD: "Alex mentioned trying a new Thai place and discussed the pad see ew. Mina asked about the location. Alex noted it was Siam Kitchen and stated the weekend hours."
            </EXAMPLES>

            <EXAMPLES>
            MESSAGES: "Sam: I really prefer working in the mornings — I'm way more productive before noon.\n---\nDana: Same. I've been blocking 9-11 AM for deep work. Also, I can't stand Jira — can we move the tracker to Linear?\n---\nSam: Fine by me. I'll set up the workspace."
            GOOD: "Sam prefers morning work and reports higher productivity before noon. Dana blocks 9-11 AM for deep work. Dana prefers Linear over Jira for issue tracking. Sam is setting up the Linear workspace."
            BAD: "Sam and Dana discussed their work preferences. They talked about morning productivity and mentioned switching from Jira to Linear."
            </EXAMPLES>

            """);
    }

    /// <summary>Python's <c>separator.join(items)</c>, which only accepts strings.</summary>
    private static string JoinStrings(string separator, object? items)
    {
        var values = items switch
        {
            string s => s.Select(c => (object?)c.ToString()),
            not null when PythonText.TryGetSequence(items, out var sequence) => sequence,
            _ => throw new ArgumentException("The 'episodes' context value must be a list of strings."),
        };

        return string.Join(separator, values.Select(v => PythonText.Normalize(v) as string
            ?? throw new ArgumentException("The 'episodes' context value must be a list of strings.")));
    }
}
