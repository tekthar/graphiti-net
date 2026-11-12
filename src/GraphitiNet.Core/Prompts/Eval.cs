// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/eval.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using static GraphitiNet.Prompts.PromptContext;

namespace GraphitiNet.Prompts;

/// <summary>Response of <c>eval.query_expansion</c>.</summary>
public sealed record QueryExpansion
{
    /// <summary>The rephrased query.</summary>
    [JsonPropertyName("query")]
    [Description("query optimized for database search")]
    public required string Query { get; init; }
}

/// <summary>Response of <c>eval.qa_prompt</c>.</summary>
public sealed record QAResponse
{
    /// <summary>The answer.</summary>
    [JsonPropertyName("ANSWER")]
    [Description("how Alice would answer the question")]
    public required string Answer { get; init; }
}

/// <summary>Response of <c>eval.eval_prompt</c>.</summary>
public sealed record EvalResponse
{
    /// <summary>Whether the answer is correct.</summary>
    [JsonPropertyName("is_correct")]
    [Description("boolean if the answer is correct or incorrect")]
    public required bool IsCorrect { get; init; }

    /// <summary>Why.</summary>
    [JsonPropertyName("reasoning")]
    [Description("why you determined the response was correct or incorrect")]
    public required string Reasoning { get; init; }
}

/// <summary>Response of <c>eval.eval_add_episode_results</c>.</summary>
public sealed record EvalAddEpisodeResults
{
    /// <summary>Whether the baseline extraction is better than the candidate.</summary>
    [JsonPropertyName("candidate_is_worse")]
    [Description("boolean if the baseline extraction is higher quality than the candidate extraction.")]
    public required bool CandidateIsWorse { get; init; }

    /// <summary>Why.</summary>
    [JsonPropertyName("reasoning")]
    [Description("why you determined the response was correct or incorrect")]
    public required string Reasoning { get; init; }
}

/// <summary>The <c>eval</c> prompts, used by the evaluation harness.</summary>
public static class EvalPrompts
{
    /// <summary><c>eval.query_expansion</c>: rephrases a question. Context: <c>query</c>.</summary>
    public static ChatPrompt QueryExpansion(IReadOnlyDictionary<string, object?> context)
    {
        var sysPrompt = "You are an expert at rephrasing questions into queries used in a database retrieval system";

        var userPrompt = $"""

                Bob is asking Alice a question, are you able to rephrase the question into a simpler one about Alice in the third person
                that maintains the relevant context?
                <QUESTION>
                {Json(context, "query")}
                </QUESTION>
                
            """;
        return new ChatPrompt(sysPrompt, userPrompt);
    }

    /// <summary>
    /// <c>eval.qa_prompt</c>: answers a question as Alice. Context: <c>entity_summaries</c>, <c>facts</c>,
    /// <c>query</c>.
    /// </summary>
    public static ChatPrompt QaPrompt(IReadOnlyDictionary<string, object?> context)
    {
        var sysPrompt = "You are Alice and should respond to all questions from the first person perspective of Alice";

        var userPrompt = $"""

                Your task is to briefly answer the question in the way that you think Alice would answer the question.
                You are given the following entity summaries and facts to help you determine the answer to your question.
                <ENTITY_SUMMARIES>
                {Json(context, "entity_summaries")}
                </ENTITY_SUMMARIES>
                <FACTS>
                {Json(context, "facts")}
                </FACTS>
                <QUESTION>
                {Str(context, "query")}
                </QUESTION>
                
            """;
        return new ChatPrompt(sysPrompt, userPrompt);
    }

    /// <summary>
    /// <c>eval.eval_prompt</c>: grades a response against a gold answer. Context: <c>query</c>, <c>answer</c>,
    /// <c>response</c>.
    /// </summary>
    public static ChatPrompt EvalPrompt(IReadOnlyDictionary<string, object?> context)
    {
        var sysPrompt = "You are a judge that determines if answers to questions match a gold standard answer";

        var userPrompt = $"""

                Given the QUESTION and the gold standard ANSWER determine if the RESPONSE to the question is correct or incorrect.
                Although the RESPONSE may be more verbose, mark it as correct as long as it references the same topic 
                as the gold standard ANSWER. Also include your reasoning for the grade.
                <QUESTION>
                {Str(context, "query")}
                </QUESTION>
                <ANSWER>
                {Str(context, "answer")}
                </ANSWER>
                <RESPONSE>
                {Str(context, "response")}
                </RESPONSE>
                
            """;
        return new ChatPrompt(sysPrompt, userPrompt);
    }

    /// <summary>
    /// <c>eval.eval_add_episode_results</c>: compares a baseline and a candidate graph extraction. Context:
    /// <c>previous_messages</c>, <c>message</c>, <c>baseline</c>, <c>candidate</c>.
    /// </summary>
    public static ChatPrompt EvalAddEpisodeResults(IReadOnlyDictionary<string, object?> context)
    {
        var sysPrompt = """
            You are a judge that determines whether a baseline graph building result from a list of messages is better
                    than a candidate graph building result based on the same messages.
            """;

        var userPrompt = $"""

                Given the following PREVIOUS MESSAGES and MESSAGE, determine if the BASELINE graph data extracted from the 
                conversation is higher quality than the CANDIDATE graph data extracted from the conversation.
                
                Return False if the BASELINE extraction is better, and True otherwise. If the CANDIDATE extraction and
                BASELINE extraction are nearly identical in quality, return True. Add your reasoning for your decision to the reasoning field
                
                <PREVIOUS MESSAGES>
                {Str(context, "previous_messages")}
                </PREVIOUS MESSAGES>
                <MESSAGE>
                {Str(context, "message")}
                </MESSAGE>
                
                <BASELINE>
                {Str(context, "baseline")}
                </BASELINE>
                
                <CANDIDATE>
                {Str(context, "candidate")}
                </CANDIDATE>
                
            """;
        return new ChatPrompt(sysPrompt, userPrompt);
    }
}
