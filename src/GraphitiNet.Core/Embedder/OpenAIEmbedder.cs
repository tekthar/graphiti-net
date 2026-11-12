// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/embedder/openai.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Llm;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Embedder;

/// <summary>Settings for <see cref="OpenAIEmbedder"/>.</summary>
public record OpenAIEmbedderConfig : EmbedderConfig
{
    /// <summary>Upstream's <c>DEFAULT_EMBEDDING_MODEL</c>.</summary>
    public const string DefaultEmbeddingModel = "text-embedding-3-small";

    /// <summary>The embedding model.</summary>
    public string EmbeddingModel { get; init; } = DefaultEmbeddingModel;

    /// <summary>The API key. Falls back to <c>OPENAI_API_KEY</c>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The API's base URL. Falls back to <c>OPENAI_BASE_URL</c>, then to OpenAI.</summary>
    public string? BaseUrl { get; init; }
}

/// <summary>
/// Embeds text with OpenAI's embeddings API. Embeddings are cut to <see cref="EmbedderConfig.EmbeddingDim"/>
/// dimensions; no <c>dimensions</c> parameter is sent, as upstream. Works with Azure OpenAI too when given an
/// <c>AzureOpenAIClient</c>.
/// </summary>
public class OpenAIEmbedder : EmbedderClient
{
    /// <summary>Creates the embedder.</summary>
    /// <param name="config">The settings; <see langword="null"/> means defaults.</param>
    /// <param name="client">An SDK client to use instead of creating one from <paramref name="config"/>.</param>
    public OpenAIEmbedder(OpenAIEmbedderConfig? config = null, SdkOpenAIClient? client = null)
    {
        Config = config ?? new OpenAIEmbedderConfig();
        Client = client ?? OpenAIWire.CreateClient(Config.ApiKey, Config.BaseUrl);
    }

    /// <summary>The settings.</summary>
    public OpenAIEmbedderConfig Config { get; }

    /// <summary>The OpenAI SDK client the requests go through.</summary>
    public SdkOpenAIClient Client { get; }

    /// <inheritdoc/>
    public override async Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        var result = await Client.GetEmbeddingClient(Config.EmbeddingModel)
            .GenerateEmbeddingAsync(inputData, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Truncate(result.Value.ToFloats());
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputDataList);
        var result = await Client.GetEmbeddingClient(Config.EmbeddingModel)
            .GenerateEmbeddingsAsync(inputDataList, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Value.OrderBy(e => e.Index).Select(e => Truncate(e.ToFloats())).ToList();
    }

    private float[] Truncate(ReadOnlyMemory<float> embedding) =>
        embedding.Length > Config.EmbeddingDim ? embedding[..Config.EmbeddingDim].ToArray() : embedding.ToArray();
}
