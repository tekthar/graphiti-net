// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/embedder/azure_openai.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Embedder;

/// <summary>
/// Embeds text with Azure OpenAI. Pass an <c>AzureOpenAIClient</c> (Azure.AI.OpenAI package) or an
/// <see cref="SdkOpenAIClient"/> pointed at Azure's v1 endpoint. Unlike <see cref="OpenAIEmbedder"/>, embeddings are
/// returned at full length, as upstream.
/// </summary>
public class AzureOpenAIEmbedderClient : EmbedderClient
{
    /// <summary>Creates the embedder.</summary>
    /// <param name="azureClient">The SDK client for the Azure resource.</param>
    /// <param name="model">The embedding deployment name.</param>
    public AzureOpenAIEmbedderClient(SdkOpenAIClient azureClient, string model = "text-embedding-3-small")
    {
        ArgumentNullException.ThrowIfNull(azureClient);
        AzureClient = azureClient;
        Model = model;
    }

    /// <summary>The SDK client the requests go through.</summary>
    public SdkOpenAIClient AzureClient { get; }

    /// <summary>The embedding deployment name.</summary>
    public string Model { get; }

    /// <summary>Where errors are logged.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <inheritdoc/>
    public override async Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        var embeddings = await CreateBatchAsync([inputData], cancellationToken).ConfigureAwait(false);
        return embeddings[0];
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await AzureClient.GetEmbeddingClient(Model)
                .GenerateEmbeddingsAsync(inputDataList, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Value.OrderBy(e => e.Index).Select(e => e.ToFloats().ToArray()).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ProviderLog.EmbeddingError(Logger, e, "Azure OpenAI");
            throw;
        }
    }
}
