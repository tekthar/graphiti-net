// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Llm;

namespace GraphitiNet;

/// <summary>
/// Ambient information about the Graphiti operation that is running: which operation, which group, which episode and
/// which prompt. Every model call made on behalf of a <see cref="Graphiti"/> method runs inside one, so an
/// <see cref="LLMClient"/>, <see cref="Embedder.EmbedderClient"/> or <see cref="CrossEncoder.CrossEncoderClient"/>
/// implementation can read <see cref="Current"/> to route a call (for example, restricted data only to an approved
/// provider) or to charge its cost. A graphiti-net addition; upstream has no equivalent.
/// </summary>
/// <remarks>
/// The context flows with the async call chain (<see cref="AsyncLocal{T}"/>), so concurrent operations each see
/// their own. It is immutable: narrower scopes (a prompt name, an episode) are pushed with <see cref="Begin"/> and
/// share the parent's <see cref="Usage"/> accumulator.
/// </remarks>
public sealed record GraphitiCallContext
{
    private static readonly AsyncLocal<GraphitiCallContext?> CurrentContext = new();

    /// <summary>The context of the running operation, or <see langword="null"/> outside any Graphiti
    /// operation.</summary>
    public static GraphitiCallContext? Current => CurrentContext.Value;

    /// <summary>The operation, using upstream's method names: <c>add_episode</c>, <c>add_episode_bulk</c>,
    /// <c>add_triplet</c>, <c>remove_episode</c>, <c>search</c>, <c>summarize_saga</c>, <c>build_communities</c>
    /// and so on.</summary>
    public required string Operation { get; init; }

    /// <summary>The group the operation works in, or <see langword="null"/> when it spans several groups (a search
    /// across groups, for example).</summary>
    public string? GroupId { get; init; }

    /// <summary>The episode being processed, if any.</summary>
    public string? EpisodeUuid { get; init; }

    /// <summary>The host data on the episode being processed (<see cref="Models.EpisodicNode.EpisodeMetadata"/>), if
    /// any.</summary>
    public IDictionary<string, object?>? EpisodeMetadata { get; init; }

    /// <summary>The prompt the current LLM call runs, such as <c>extract_nodes.extract_message</c>; set around each
    /// prompt by <c>GraphitiClients.CompletePromptAsync</c> and <c>LLMClient.GenerateResponseAsync</c>.
    /// <see langword="null"/> for embedding and reranking calls.</summary>
    public string? PromptName { get; init; }

    /// <summary>Token usage of the LLM calls made in this operation so far, per prompt. Shared by every scope pushed
    /// inside the operation, so it measures this operation only, even when others run concurrently.</summary>
    public TokenUsageTracker Usage { get; init; } = new();

    /// <summary>The gate asked before each model call in this operation, if one is configured.</summary>
    public IModelCallGate? ModelCallGate { get; init; }

    /// <summary>Makes <paramref name="context"/> current until the returned scope is disposed.</summary>
    /// <param name="context">The context.</param>
    /// <returns>A scope that restores the previous context when disposed.</returns>
    public static IDisposable Begin(GraphitiCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    /// <summary>Pushes the current context with a different prompt name, if there is a current context and the name
    /// differs; otherwise does nothing.</summary>
    internal static IDisposable? BeginPrompt(string? promptName)
    {
        var current = CurrentContext.Value;
        return current is null || promptName is null || current.PromptName == promptName
            ? null
            : Begin(current with { PromptName = promptName });
    }

    /// <summary>Asks the current context's gate whether a model call may run, and throws
    /// <see cref="ModelCallRefusedException"/> if it refuses. Does nothing without a context or a gate.</summary>
    internal static async ValueTask EnsureAllowedAsync(
        ModelCallKind kind,
        string? promptName,
        CancellationToken cancellationToken)
    {
        var current = CurrentContext.Value;
        if (current?.ModelCallGate is not { } gate)
        {
            return;
        }

        var call = new ModelCallInfo(
            kind,
            promptName ?? current.PromptName,
            current.Operation,
            current.GroupId,
            current.EpisodeUuid,
            current.EpisodeMetadata);
        if (!await gate.AllowAsync(call, cancellationToken).ConfigureAwait(false))
        {
            throw new ModelCallRefusedException(call);
        }
    }

    private sealed class Scope(GraphitiCallContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentContext.Value = previous;
        }
    }
}
