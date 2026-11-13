// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.CrossEncoder;
using GraphitiNet.Embedder;

namespace GraphitiNet;

/// <summary>The kind of model call a <see cref="IModelCallGate"/> is asked about.</summary>
public enum ModelCallKind
{
    /// <summary>An LLM completion.</summary>
    Llm,

    /// <summary>An embedding.</summary>
    Embedding,

    /// <summary>A cross-encoder rerank.</summary>
    Rerank,
}

/// <summary>A model call about to be made, as shown to a <see cref="IModelCallGate"/>.</summary>
/// <param name="Kind">LLM, embedding or rerank.</param>
/// <param name="PromptName">The prompt, for LLM calls.</param>
/// <param name="Operation">The Graphiti operation, such as <c>add_episode</c>.</param>
/// <param name="GroupId">The group the operation works in.</param>
/// <param name="EpisodeUuid">The episode being processed, if any.</param>
/// <param name="EpisodeMetadata">The episode's host data, if any.</param>
public sealed record ModelCallInfo(
    ModelCallKind Kind,
    string? PromptName,
    string Operation,
    string? GroupId,
    string? EpisodeUuid,
    IDictionary<string, object?>? EpisodeMetadata);

/// <summary>
/// Asked before each LLM, embedding and rerank call a <see cref="Graphiti"/> operation makes; it can refuse the
/// call, for example when a budget is spent. A refusal throws <see cref="ModelCallRefusedException"/> out of the
/// operation. A graphiti-net addition.
/// </summary>
/// <remarks>
/// LLM calls are checked in <see cref="Llm.LLMClient"/>'s <c>GenerateResponseAsync</c>, after a cache miss and
/// before the provider is called (an <see cref="Llm.LLMClient"/> subclass that overrides <c>GenerateResponseAsync</c>
/// itself bypasses the check). Embedding and rerank calls are checked by wrappers <see cref="Graphiti"/> puts around
/// its embedder and cross-encoder when a gate is configured.
/// </remarks>
public interface IModelCallGate
{
    /// <summary>Whether the call may run.</summary>
    /// <param name="modelCall">The call about to be made.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> to allow the call; <see langword="false"/> to refuse it.</returns>
    ValueTask<bool> AllowAsync(ModelCallInfo modelCall, CancellationToken cancellationToken);
}

/// <summary>A <see cref="IModelCallGate"/> backed by a delegate.</summary>
/// <param name="allow">Decides whether a call may run.</param>
public sealed class DelegateModelCallGate(Func<ModelCallInfo, CancellationToken, ValueTask<bool>> allow) : IModelCallGate
{
    private readonly Func<ModelCallInfo, CancellationToken, ValueTask<bool>> _allow =
        allow ?? throw new ArgumentNullException(nameof(allow));

    /// <inheritdoc/>
    public ValueTask<bool> AllowAsync(ModelCallInfo modelCall, CancellationToken cancellationToken) =>
        _allow(modelCall, cancellationToken);
}

/// <summary>
/// A <see cref="IModelCallGate"/> refused a model call. When it happens in <see cref="Graphiti.AddEpisodeAsync"/>,
/// the episode is already saved, unprocessed, and <see cref="EpisodeUuid"/> names it: pass it as <c>uuid</c> to
/// <see cref="Graphiti.AddEpisodeAsync"/> later to process it.
/// </summary>
public sealed class ModelCallRefusedException : GraphitiException
{
    /// <summary>Creates the exception for a refused call.</summary>
    /// <param name="call">The refused call.</param>
    public ModelCallRefusedException(ModelCallInfo call)
        : base(Describe(call))
    {
        Call = call;
    }

    /// <summary>The refused call.</summary>
    public ModelCallInfo Call { get; }

    /// <summary>The episode being processed when the call was refused, if any.</summary>
    public string? EpisodeUuid => Call.EpisodeUuid;

    private static string Describe(ModelCallInfo call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var prompt = call.PromptName is null ? "" : $" ({call.PromptName})";
        return $"The model call gate refused a {call.Kind} call{prompt} in {call.Operation}.";
    }
}

/// <summary>Asks the call context's gate before each embedding call.</summary>
internal sealed class GatedEmbedderClient(EmbedderClient inner) : EmbedderClient
{
    public EmbedderClient Inner { get; } = inner;

    public override async Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        await GraphitiCallContext.EnsureAllowedAsync(ModelCallKind.Embedding, null, cancellationToken)
            .ConfigureAwait(false);
        return await Inner.CreateAsync(inputData, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList,
        CancellationToken cancellationToken = default)
    {
        await GraphitiCallContext.EnsureAllowedAsync(ModelCallKind.Embedding, null, cancellationToken)
            .ConfigureAwait(false);
        return await Inner.CreateBatchAsync(inputDataList, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Asks the call context's gate before each rerank call.</summary>
internal sealed class GatedCrossEncoderClient(CrossEncoderClient inner) : CrossEncoderClient
{
    public CrossEncoderClient Inner { get; } = inner;

    public override async Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default)
    {
        await GraphitiCallContext.EnsureAllowedAsync(ModelCallKind.Rerank, null, cancellationToken)
            .ConfigureAwait(false);
        return await Inner.RankAsync(query, passages, cancellationToken).ConfigureAwait(false);
    }
}
