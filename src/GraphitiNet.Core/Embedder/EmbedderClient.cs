// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/embedder/client.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Embedder;

/// <summary>Embedder settings.</summary>
public record EmbedderConfig
{
    /// <summary>Upstream's default embedding size (<c>EMBEDDING_DIM</c>, read from the environment there).</summary>
    public const int DefaultEmbeddingDim = 1024;

    /// <summary>The number of dimensions embeddings are cut to. Must match the storage schema.</summary>
    public int EmbeddingDim { get; init; } = DefaultEmbeddingDim;
}

/// <summary>Turns text into embeddings.</summary>
public abstract class EmbedderClient
{
    /// <summary>Embeds one text.</summary>
    public abstract Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default);

    /// <summary>Embeds several texts, in order. The default calls <see cref="CreateAsync"/> for each.</summary>
    public virtual async Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputDataList);
        var results = new float[inputDataList.Count][];
        for (var i = 0; i < inputDataList.Count; i++)
        {
            results[i] = await CreateAsync(inputDataList[i], cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}
