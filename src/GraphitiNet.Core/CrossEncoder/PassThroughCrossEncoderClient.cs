// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.CrossEncoder;

/// <summary>
/// A cross-encoder that does not rerank: it returns the passages in the order it was given them, the passage at rank
/// <c>i</c> (from 0) scoring <c>1 / (i + 1)</c>. Pass it to <see cref="Graphiti"/> when there is no reranking model
/// (no OpenAI account, or ranking by reciprocal rank fusion alone): the cross-encoder rerankers then keep the
/// reciprocal rank fusion order they seed from. A graphiti-net addition; upstream has no such client.
/// </summary>
/// <remarks>
/// <para>Facts and episodes reach the cross-encoder in reciprocal rank fusion order, so with this client their
/// cross-encoder rerankers return exactly what <c>Rrf</c> returns. Entities and communities reach it, as upstream, in
/// the order the search methods found them (each method's hits in turn, without duplicates); that equals the fusion
/// order when a single search method is configured.</para>
/// <para>The scores fall with rank and lie in (0, 1], so a search's <c>RerankerMinScore</c> applies to them: a minimum
/// above 0 keeps only the first <c>floor(1 / minScore)</c> passages. Duplicate passages keep each position given.</para>
/// </remarks>
public sealed class PassThroughCrossEncoderClient : CrossEncoderClient
{
    /// <summary>A shared instance; the client holds no state.</summary>
    public static PassThroughCrossEncoderClient Instance { get; } = new();

    /// <inheritdoc />
    public override Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passages);
        cancellationToken.ThrowIfCancellationRequested();
        var ranked = new RankedPassage[passages.Count];
        for (var i = 0; i < passages.Count; i++)
        {
            ranked[i] = new RankedPassage(passages[i], 1.0 / (i + 1));
        }

        return Task.FromResult<IReadOnlyList<RankedPassage>>(ranked);
    }
}
