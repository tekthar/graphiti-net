// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/names.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Prompts;

/// <summary>The prompt groups (upstream <c>PromptGroup</c>).</summary>
public static class PromptGroupNames
{
    /// <summary><c>extract_nodes</c>: entity extraction and entity summaries.</summary>
    public const string ExtractNodes = "extract_nodes";

    /// <summary><c>dedupe_nodes</c>: entity resolution.</summary>
    public const string DedupeNodes = "dedupe_nodes";

    /// <summary><c>extract_edges</c>: fact extraction, fact attributes and timestamps.</summary>
    public const string ExtractEdges = "extract_edges";

    /// <summary><c>extract_nodes_and_edges</c>: combined entity and fact extraction.</summary>
    public const string ExtractNodesAndEdges = "extract_nodes_and_edges";

    /// <summary><c>dedupe_edges</c>: fact resolution and invalidation.</summary>
    public const string DedupeEdges = "dedupe_edges";

    /// <summary><c>summarize_nodes</c>: summaries of entities and communities.</summary>
    public const string SummarizeNodes = "summarize_nodes";

    /// <summary><c>summarize_sagas</c>: saga summaries.</summary>
    public const string SummarizeSagas = "summarize_sagas";

    /// <summary><c>eval</c>: evaluation prompts.</summary>
    public const string Eval = "eval";

    /// <summary>Every group, in upstream order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ExtractNodes, DedupeNodes, ExtractEdges, ExtractNodesAndEdges, DedupeEdges, SummarizeNodes, SummarizeSagas,
        Eval,
    ];
}

/// <summary>The prompt names, <c>group.function</c> (upstream <c>PromptName</c>).</summary>
public static class PromptNames
{
    /// <summary><c>extract_nodes.extract_message</c>.</summary>
    public const string ExtractNodesExtractMessage = "extract_nodes.extract_message";

    /// <summary><c>extract_nodes.extract_json</c>.</summary>
    public const string ExtractNodesExtractJson = "extract_nodes.extract_json";

    /// <summary><c>extract_nodes.extract_text</c>.</summary>
    public const string ExtractNodesExtractText = "extract_nodes.extract_text";

    /// <summary><c>extract_nodes.classify_nodes</c>.</summary>
    public const string ExtractNodesClassifyNodes = "extract_nodes.classify_nodes";

    /// <summary><c>extract_nodes.extract_attributes</c>.</summary>
    public const string ExtractNodesExtractAttributes = "extract_nodes.extract_attributes";

    /// <summary><c>extract_nodes.extract_summary</c>.</summary>
    public const string ExtractNodesExtractSummary = "extract_nodes.extract_summary";

    /// <summary><c>extract_nodes.extract_summaries_batch</c>.</summary>
    public const string ExtractNodesExtractSummariesBatch = "extract_nodes.extract_summaries_batch";

    /// <summary><c>extract_nodes.extract_entity_summaries_from_episodes</c>.</summary>
    public const string ExtractNodesExtractEntitySummariesFromEpisodes =
        "extract_nodes.extract_entity_summaries_from_episodes";

    /// <summary><c>dedupe_nodes.node</c>.</summary>
    public const string DedupeNodesNode = "dedupe_nodes.node";

    /// <summary><c>dedupe_nodes.node_list</c>.</summary>
    public const string DedupeNodesNodeList = "dedupe_nodes.node_list";

    /// <summary><c>dedupe_nodes.nodes</c>.</summary>
    public const string DedupeNodesNodes = "dedupe_nodes.nodes";

    /// <summary><c>extract_edges.edge</c>.</summary>
    public const string ExtractEdgesEdge = "extract_edges.edge";

    /// <summary><c>extract_edges.extract_attributes</c>.</summary>
    public const string ExtractEdgesExtractAttributes = "extract_edges.extract_attributes";

    /// <summary><c>extract_edges.extract_timestamps</c>.</summary>
    public const string ExtractEdgesExtractTimestamps = "extract_edges.extract_timestamps";

    /// <summary><c>extract_edges.extract_timestamps_batch</c>.</summary>
    public const string ExtractEdgesExtractTimestampsBatch = "extract_edges.extract_timestamps_batch";

    /// <summary><c>extract_nodes_and_edges.extract_message</c>.</summary>
    public const string ExtractNodesAndEdgesExtractMessage = "extract_nodes_and_edges.extract_message";

    /// <summary><c>dedupe_edges.resolve_edge</c>.</summary>
    public const string DedupeEdgesResolveEdge = "dedupe_edges.resolve_edge";

    /// <summary><c>summarize_nodes.summarize_pair</c>.</summary>
    public const string SummarizeNodesSummarizePair = "summarize_nodes.summarize_pair";

    /// <summary><c>summarize_nodes.summarize_context</c>.</summary>
    public const string SummarizeNodesSummarizeContext = "summarize_nodes.summarize_context";

    /// <summary><c>summarize_nodes.summary_description</c>.</summary>
    public const string SummarizeNodesSummaryDescription = "summarize_nodes.summary_description";

    /// <summary><c>summarize_sagas.summarize_saga</c>.</summary>
    public const string SummarizeSagasSummarizeSaga = "summarize_sagas.summarize_saga";

    /// <summary><c>eval.query_expansion</c>.</summary>
    public const string EvalQueryExpansion = "eval.query_expansion";

    /// <summary><c>eval.qa_prompt</c>.</summary>
    public const string EvalQaPrompt = "eval.qa_prompt";

    /// <summary><c>eval.eval_prompt</c>.</summary>
    public const string EvalEvalPrompt = "eval.eval_prompt";

    /// <summary><c>eval.eval_add_episode_results</c>.</summary>
    public const string EvalEvalAddEpisodeResults = "eval.eval_add_episode_results";

    /// <summary>Every prompt name, in upstream order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ExtractNodesExtractMessage, ExtractNodesExtractJson, ExtractNodesExtractText, ExtractNodesClassifyNodes,
        ExtractNodesExtractAttributes, ExtractNodesExtractSummary, ExtractNodesExtractSummariesBatch,
        ExtractNodesExtractEntitySummariesFromEpisodes, DedupeNodesNode, DedupeNodesNodeList, DedupeNodesNodes,
        ExtractEdgesEdge, ExtractEdgesExtractAttributes, ExtractEdgesExtractTimestamps,
        ExtractEdgesExtractTimestampsBatch, ExtractNodesAndEdgesExtractMessage, DedupeEdgesResolveEdge,
        SummarizeNodesSummarizePair, SummarizeNodesSummarizeContext, SummarizeNodesSummaryDescription,
        SummarizeSagasSummarizeSaga, EvalQueryExpansion, EvalQaPrompt, EvalEvalPrompt, EvalEvalAddEpisodeResults,
    ];
}
