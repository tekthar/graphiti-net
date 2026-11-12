// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Extensions.AI;

namespace GraphitiNet.Embedder;

/// <summary>
/// An embedder over any Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>, so every
/// provider with an M.E.AI adapter (OpenAI, Azure OpenAI, Ollama, Gemini and others) works. Embeddings are cut to
/// <see cref="EmbedderConfig.EmbeddingDim"/> dimensions, as <see cref="OpenAIEmbedder"/> does.
/// </summary>
public class EmbeddingGeneratorEmbedder : EmbedderClient
{
    private readonly EmbeddingGenerationOptions? _options;

    /// <summary>Creates the embedder.</summary>
    /// <param name="generator">The embedding generator.</param>
    /// <param name="config">The settings; <see langword="null"/> means defaults.</param>
    /// <param name="options">Options passed with every request, such as a model id or dimensions.</param>
    public EmbeddingGeneratorEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbedderConfig? config = null,
        EmbeddingGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Generator = generator;
        Config = config ?? new EmbedderConfig();
        _options = options;
    }

    /// <summary>The embedding generator.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

    /// <summary>The settings.</summary>
    public EmbedderConfig Config { get; }

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
        ArgumentNullException.ThrowIfNull(inputDataList);
        if (inputDataList.Count == 0)
        {
            return [];
        }

        var embeddings = await Generator.GenerateAsync(inputDataList, _options, cancellationToken)
            .ConfigureAwait(false);
        if (embeddings.Count != inputDataList.Count)
        {
            throw new InvalidOperationException(
                $"The embedding generator returned {embeddings.Count} embeddings for {inputDataList.Count} inputs.");
        }

        return embeddings.Select(e => Truncate(e.Vector)).ToList();
    }

    private float[] Truncate(ReadOnlyMemory<float> vector) =>
        vector.Length > Config.EmbeddingDim ? vector[..Config.EmbeddingDim].ToArray() : vector.ToArray();
}
