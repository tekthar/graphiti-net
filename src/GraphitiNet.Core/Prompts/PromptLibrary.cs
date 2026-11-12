// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/lib.py (Graphiti v0.31.2) and changed for .NET.

using System.Collections.Frozen;

namespace GraphitiNet.Prompts;

/// <summary>
/// The prompts, by name (<c>group.function</c>, see <see cref="PromptNames"/>), with optional per-instance overrides
/// of individual prompts' text. Response schemas are fixed and cannot be overridden.
/// </summary>
/// <remarks>
/// Upstream exposes the library as attribute groups (<c>library.extract_nodes.extract_message(context)</c>), with
/// <c>create_prompt_library</c> for overrides and a legacy <c>prompt_library</c> that returns message lists. Here a
/// library is a single lookup by prompt name: <see cref="Build"/> or <see cref="GetBuilder"/>. The built-in prompts
/// are also callable directly, for example <see cref="ExtractNodesPrompts.ExtractMessage"/>.
/// </remarks>
public sealed class PromptLibrary
{
    private const string ChatPromptMigrationNote =
        "Prompt overrides must return ChatPrompt(system=..., user=...). Returning list[Message] is no longer supported.";

    private static readonly FrozenDictionary<string, ChatPromptBuilder> DefaultBuilders =
        new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = ExtractNodesPrompts.ExtractMessage,
            [PromptNames.ExtractNodesExtractJson] = ExtractNodesPrompts.ExtractJson,
            [PromptNames.ExtractNodesExtractText] = ExtractNodesPrompts.ExtractText,
            [PromptNames.ExtractNodesClassifyNodes] = ExtractNodesPrompts.ClassifyNodes,
            [PromptNames.ExtractNodesExtractAttributes] = ExtractNodesPrompts.ExtractAttributes,
            [PromptNames.ExtractNodesExtractSummary] = ExtractNodesPrompts.ExtractSummary,
            [PromptNames.ExtractNodesExtractSummariesBatch] = ExtractNodesPrompts.ExtractSummariesBatch,
            [PromptNames.ExtractNodesExtractEntitySummariesFromEpisodes] =
                ExtractNodesPrompts.ExtractEntitySummariesFromEpisodes,
            [PromptNames.DedupeNodesNode] = DedupeNodesPrompts.Node,
            [PromptNames.DedupeNodesNodeList] = DedupeNodesPrompts.NodeList,
            [PromptNames.DedupeNodesNodes] = DedupeNodesPrompts.Nodes,
            [PromptNames.ExtractEdgesEdge] = ExtractEdgesPrompts.Edge,
            [PromptNames.ExtractEdgesExtractAttributes] = ExtractEdgesPrompts.ExtractAttributes,
            [PromptNames.ExtractEdgesExtractTimestamps] = ExtractEdgesPrompts.ExtractTimestamps,
            [PromptNames.ExtractEdgesExtractTimestampsBatch] = ExtractEdgesPrompts.ExtractTimestampsBatch,
            [PromptNames.ExtractNodesAndEdgesExtractMessage] = ExtractNodesAndEdgesPrompts.ExtractMessage,
            [PromptNames.DedupeEdgesResolveEdge] = DedupeEdgesPrompts.ResolveEdge,
            [PromptNames.SummarizeNodesSummarizePair] = SummarizeNodesPrompts.SummarizePair,
            [PromptNames.SummarizeNodesSummarizeContext] = SummarizeNodesPrompts.SummarizeContext,
            [PromptNames.SummarizeNodesSummaryDescription] = SummarizeNodesPrompts.SummaryDescription,
            [PromptNames.SummarizeSagasSummarizeSaga] = SummarizeSagasPrompts.SummarizeSaga,
            [PromptNames.EvalQueryExpansion] = EvalPrompts.QueryExpansion,
            [PromptNames.EvalQaPrompt] = EvalPrompts.QaPrompt,
            [PromptNames.EvalEvalPrompt] = EvalPrompts.EvalPrompt,
            [PromptNames.EvalEvalAddEpisodeResults] = EvalPrompts.EvalAddEpisodeResults,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, ChatPromptBuilder> _overrides;

    private PromptLibrary(FrozenDictionary<string, ChatPromptBuilder> overrides)
    {
        _overrides = overrides;
    }

    /// <summary>The built-in prompts, with no overrides.</summary>
    public static PromptLibrary Default { get; } =
        new(FrozenDictionary<string, ChatPromptBuilder>.Empty);

    /// <summary>
    /// The prompt groups and the functions in each, in upstream order (upstream <c>PROMPT_GROUPS</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PromptGroups { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [PromptGroupNames.ExtractNodes] =
            [
                "extract_message", "extract_json", "extract_text", "classify_nodes", "extract_attributes",
                "extract_summary", "extract_summaries_batch", "extract_entity_summaries_from_episodes",
            ],
            [PromptGroupNames.DedupeNodes] = ["node", "node_list", "nodes"],
            [PromptGroupNames.ExtractEdges] =
                ["edge", "extract_attributes", "extract_timestamps", "extract_timestamps_batch"],
            [PromptGroupNames.ExtractNodesAndEdges] = ["extract_message"],
            [PromptGroupNames.DedupeEdges] = ["resolve_edge"],
            [PromptGroupNames.SummarizeNodes] = ["summarize_pair", "summarize_context", "summary_description"],
            [PromptGroupNames.SummarizeSagas] = ["summarize_saga"],
            [PromptGroupNames.Eval] = ["query_expansion", "qa_prompt", "eval_prompt", "eval_add_episode_results"],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The fixed response schema of every prompt (upstream <c>BUILTIN_PROMPT_SPECS</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, PromptSpec> BuiltinSpecs { get; } =
        new PromptSpec[]
        {
            new(PromptNames.ExtractNodesExtractMessage, typeof(ExtractedEntities)),
            new(PromptNames.ExtractNodesExtractText, typeof(ExtractedEntities)),
            new(PromptNames.ExtractNodesExtractJson, typeof(ExtractedEntities)),
            new(PromptNames.ExtractNodesClassifyNodes),
            new(PromptNames.ExtractNodesExtractAttributes, DynamicSchema: true),
            new(PromptNames.ExtractNodesExtractSummary),
            new(PromptNames.ExtractNodesExtractSummariesBatch, typeof(SummarizedEntities)),
            new(PromptNames.ExtractNodesExtractEntitySummariesFromEpisodes, typeof(SummarizedEntities)),
            new(PromptNames.DedupeNodesNode, typeof(NodeDuplicate)),
            new(PromptNames.DedupeNodesNodeList),
            new(PromptNames.DedupeNodesNodes, typeof(NodeResolutions)),
            new(PromptNames.ExtractEdgesEdge, typeof(ExtractedEdges)),
            new(PromptNames.ExtractEdgesExtractAttributes, DynamicSchema: true),
            new(PromptNames.ExtractEdgesExtractTimestamps, typeof(EdgeTimestamps)),
            new(PromptNames.ExtractEdgesExtractTimestampsBatch, typeof(BatchEdgeTimestamps)),
            new(PromptNames.ExtractNodesAndEdgesExtractMessage, typeof(CombinedExtraction)),
            new(PromptNames.DedupeEdgesResolveEdge, typeof(EdgeDuplicate)),
            new(PromptNames.SummarizeNodesSummarizePair, typeof(Summary)),
            new(PromptNames.SummarizeNodesSummarizeContext, typeof(Summary)),
            new(PromptNames.SummarizeNodesSummaryDescription, typeof(SummaryDescription)),
            new(PromptNames.SummarizeSagasSummarizeSaga, typeof(SagaSummary)),
            new(PromptNames.EvalQueryExpansion, typeof(QueryExpansion)),
            new(PromptNames.EvalQaPrompt, typeof(QAResponse)),
            new(PromptNames.EvalEvalPrompt, typeof(EvalResponse)),
            new(PromptNames.EvalEvalAddEpisodeResults, typeof(EvalAddEpisodeResults)),
        }.ToFrozenDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>The names of the prompts this library overrides.</summary>
    public IReadOnlyCollection<string> OverriddenPrompts => _overrides.Keys;

    /// <summary>
    /// Creates a library from the built-in prompts with some prompts' text replaced (upstream
    /// <c>create_prompt_library</c>).
    /// </summary>
    /// <param name="overrides">Replacement builders by prompt name (<c>group.function</c>), or
    /// <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">A name is not a known prompt, or a builder is
    /// <see langword="null"/>.</exception>
    public static PromptLibrary Create(IReadOnlyDictionary<string, ChatPromptBuilder>? overrides = null)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return Default;
        }

        foreach (var (name, builder) in overrides)
        {
            ValidatePromptName(name);
            if (builder is null)
            {
                throw new ArgumentException($"Prompt override must be callable: {name}", nameof(overrides));
            }
        }

        return new PromptLibrary(overrides.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Returns a copy of this library with more prompts overridden; later overrides win.</summary>
    /// <param name="overrides">Replacement builders by prompt name.</param>
    /// <exception cref="ArgumentException">A name is not a known prompt, or a builder is
    /// <see langword="null"/>.</exception>
    public PromptLibrary WithOverrides(IReadOnlyDictionary<string, ChatPromptBuilder> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var merged = new Dictionary<string, ChatPromptBuilder>(_overrides, StringComparer.Ordinal);
        foreach (var (name, builder) in overrides)
        {
            merged[name] = builder;
        }

        return Create(merged);
    }

    /// <summary>Whether this library overrides the prompt.</summary>
    public bool IsOverridden(string promptName) => _overrides.ContainsKey(promptName);

    /// <summary>
    /// The builder for a prompt: the override if there is one, else the built-in prompt (upstream
    /// <c>get_prompt_builder</c>). An override that returns <see langword="null"/> throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="promptName">The prompt name, such as <c>extract_nodes.extract_message</c>.</param>
    /// <exception cref="ArgumentException">The name is not a known prompt.</exception>
    public ChatPromptBuilder GetBuilder(string promptName)
    {
        ValidatePromptName(promptName);
        if (!_overrides.TryGetValue(promptName, out var builder))
        {
            return DefaultBuilders[promptName];
        }

        return context => builder(context) ?? throw new InvalidOperationException(
            $"{promptName} must return ChatPrompt, got null. {ChatPromptMigrationNote}");
    }

    /// <summary>Builds a prompt from a context.</summary>
    /// <param name="promptName">The prompt name, such as <c>extract_nodes.extract_message</c>.</param>
    /// <param name="context">The values the prompt is built from, by upstream's snake_case keys.</param>
    /// <exception cref="ArgumentException">The name is not a known prompt.</exception>
    /// <exception cref="KeyNotFoundException">The context lacks a value the prompt needs.</exception>
    public ChatPrompt Build(string promptName, IReadOnlyDictionary<string, object?> context) =>
        GetBuilder(promptName)(context);

    /// <summary>The built-in builder for a prompt, ignoring overrides; useful to wrap the default text.</summary>
    /// <exception cref="ArgumentException">The name is not a known prompt.</exception>
    public static ChatPromptBuilder GetDefaultBuilder(string promptName)
    {
        ValidatePromptName(promptName);
        return DefaultBuilders[promptName];
    }

    /// <summary>
    /// The response model for a prompt (upstream <c>resolve_response_model</c>): the fixed schema, or the caller's
    /// for a prompt whose schema is dynamic.
    /// </summary>
    /// <param name="promptName">The prompt name.</param>
    /// <param name="responseModel">The caller's response model. Required for a dynamic-schema prompt; otherwise it
    /// may only repeat the fixed one.</param>
    /// <returns>The response model, or <see langword="null"/> when the prompt has none.</returns>
    /// <exception cref="ArgumentException">The name is unknown, a dynamic-schema prompt got no model, or the model
    /// differs from the fixed one.</exception>
    public static Type? ResolveResponseModel(string promptName, Type? responseModel = null)
    {
        ArgumentNullException.ThrowIfNull(promptName);
        if (!BuiltinSpecs.TryGetValue(promptName, out var spec))
        {
            throw new ArgumentException($"Unknown prompt_name: {promptName}", nameof(promptName));
        }

        if (spec.DynamicSchema)
        {
            return responseModel ?? throw new ArgumentException(
                $"prompt {promptName} has dynamic_schema=True; response_model is required", nameof(responseModel));
        }

        if (responseModel is not null && responseModel != spec.ResponseModel)
        {
            throw new ArgumentException(
                $"Prompt schema overrides are not allowed for {promptName}: got {responseModel.Name}, expected " +
                $"{spec.ResponseModel?.Name ?? "None"}",
                nameof(responseModel));
        }

        return spec.ResponseModel;
    }

    private static void ValidatePromptName(string promptName)
    {
        ArgumentNullException.ThrowIfNull(promptName);
        var dot = promptName.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            throw new ArgumentException(
                $"Invalid prompt_name (expected group.method): {promptName}", nameof(promptName));
        }

        var group = promptName[..dot];
        var function = promptName[(dot + 1)..];
        if (!PromptGroups.TryGetValue(group, out var functions))
        {
            throw new ArgumentException($"Unknown prompt group: {group}", nameof(promptName));
        }

        if (!functions.Contains(function))
        {
            throw new ArgumentException(
                $"Unknown prompt function for group {group}: {function}", nameof(promptName));
        }
    }
}
