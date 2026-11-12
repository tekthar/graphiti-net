// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/cross_encoder/client.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.CrossEncoder;

/// <summary>A passage and its relevance score.</summary>
/// <param name="Passage">The passage text.</param>
/// <param name="Score">Relevance to the query; higher is more relevant.</param>
public readonly record struct RankedPassage(string Passage, double Score);

/// <summary>Reranks passages by relevance to a query.</summary>
public abstract class CrossEncoderClient
{
    /// <summary>Scores each passage against the query and returns them most relevant first.</summary>
    public abstract Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default);
}
