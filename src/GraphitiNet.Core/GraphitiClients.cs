// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/graphiti_types.py (Graphiti v0.31.2) and changed for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet;

/// <summary>
/// The services one Graphiti call uses: storage, LLM, embedder, cross-encoder and prompts. Upstream's
/// <c>GraphitiClients</c>; the pipeline functions take it instead of the whole <c>Graphiti</c> instance.
/// </summary>
public sealed record GraphitiClients
{
    /// <summary>Storage.</summary>
    public required IGraphDriver Driver { get; init; }

    /// <summary>The default LLM.</summary>
    public required LLMClient LlmClient { get; init; }

    /// <summary>The embedder.</summary>
    public required EmbedderClient Embedder { get; init; }

    /// <summary>The reranker, or <see langword="null"/> if cross-encoder reranking is not used.</summary>
    public CrossEncoderClient? CrossEncoder { get; init; }

    /// <summary>Prompts, with any per-instance overrides. Defaults to <see cref="Prompts.PromptLibrary.Default"/>.</summary>
    public PromptLibrary PromptLibrary { get; init; } = PromptLibrary.Default;

    /// <summary>
    /// Optional routing of individual prompts (by name, for example <c>dedupe_nodes.nodes</c>) to other LLM clients,
    /// such as a cheaper model for deduplication. Prompts not listed use <see cref="LlmClient"/>.
    /// </summary>
    public IReadOnlyDictionary<string, LLMClient>? PromptRoutes { get; init; }

    /// <summary>The cap on concurrent operations per gather (upstream <c>max_coroutines</c>); <see langword="null"/>
    /// uses <c>SEMAPHORE_LIMIT</c> or 20.</summary>
    public int? MaxCoroutines { get; init; }

    /// <summary>Where the pipeline functions log (upstream's module loggers). Defaults to no logging.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>The LLM client a prompt runs on.</summary>
    public LLMClient ClientFor(string promptName) =>
        PromptRoutes is not null && PromptRoutes.TryGetValue(promptName, out var routed) ? routed : LlmClient;

    /// <summary>
    /// Builds a prompt from the library and runs it, as upstream's <c>generate_prompt_response</c> /
    /// <c>complete_prompt</c>. The response model is the prompt's fixed schema unless one is given.
    /// </summary>
    /// <param name="promptName">A name from <see cref="PromptNames"/>.</param>
    /// <param name="context">The prompt context, keyed by upstream's snake_case names.</param>
    /// <param name="responseModel">The response type; defaults to the prompt's registered model.</param>
    /// <param name="modelSize">Which configured model to use.</param>
    /// <param name="groupId">The group, passed to the language instruction.</param>
    /// <param name="maxTokens">Output token cap; defaults to the client's.</param>
    /// <param name="attributeExtraction">Whether to add the strict attribute-extraction preamble.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed JSON response.</returns>
    public async Task<JsonElement> CompletePromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?> context,
        Type? responseModel = null,
        ModelSize modelSize = ModelSize.Medium,
        string? groupId = null,
        int? maxTokens = null,
        bool attributeExtraction = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = PromptLibrary.ResolveResponseModel(promptName, responseModel);
        var messages = PromptLibrary.Build(promptName, context).AsMessages().ToList();

        // graphiti-net addition: the prompt name is visible in GraphitiCallContext.Current during the call, also to
        // LLM clients that override GenerateResponseAsync.
        using var promptScope = GraphitiCallContext.BeginPrompt(promptName);
        return await ClientFor(promptName).GenerateResponseAsync(
            messages,
            resolved,
            maxTokens,
            modelSize,
            groupId,
            promptName,
            attributeExtraction,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="CompletePromptAsync"/>, deserialising the response into
    /// <typeparamref name="T"/>.</summary>
    public async Task<T> CompletePromptAsync<T>(
        string promptName,
        IReadOnlyDictionary<string, object?> context,
        ModelSize modelSize = ModelSize.Medium,
        string? groupId = null,
        int? maxTokens = null,
        bool attributeExtraction = false,
        CancellationToken cancellationToken = default)
    {
        var element = await CompletePromptAsync(
            promptName, context, typeof(T), modelSize, groupId, maxTokens, attributeExtraction, cancellationToken)
            .ConfigureAwait(false);
        return element.Deserialize<T>(ResponseModelSchema.SerializerOptions)
            ?? throw new JsonException($"The {promptName} response was null.");
    }
}
