// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/utils.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Embedder;

namespace GraphitiNet.Llm;

/// <summary>Helpers around the provider clients.</summary>
public static class LLMUtils
{
    /// <summary>Embeds one text, with newlines replaced by spaces first, as upstream's <c>generate_embedding</c>
    /// does.</summary>
    public static Task<float[]> GenerateEmbeddingAsync(
        EmbedderClient embedder, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(text);
        return embedder.CreateAsync(text.Replace('\n', ' '), cancellationToken);
    }
}
