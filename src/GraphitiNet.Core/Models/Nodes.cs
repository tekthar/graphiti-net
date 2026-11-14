// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/nodes.py (Graphiti v0.31.2) and changed for .NET.

using System.Diagnostics.CodeAnalysis;
using GraphitiNet.Utils;

namespace GraphitiNet.Models;

/// <summary>The kind of raw input an episode holds.</summary>
public enum EpisodeType
{
    /// <summary>A chat message, formatted as <c>actor: content</c>, for example <c>user: Hello</c>.</summary>
    Message,

    /// <summary>A JSON object.</summary>
    Json,

    /// <summary>Plain text.</summary>
    Text,

    /// <summary>A fact triple added directly, without extraction.</summary>
    FactTriple,
}

/// <summary>Converts <see cref="EpisodeType"/> to and from the names upstream stores.</summary>
public static class EpisodeTypes
{
    /// <summary>The stored name: <c>message</c>, <c>json</c>, <c>text</c> or <c>fact_triple</c>.</summary>
    public static string ToStorageName(this EpisodeType type) => type switch
    {
        EpisodeType.Message => "message",
        EpisodeType.Json => "json",
        EpisodeType.Text => "text",
        EpisodeType.FactTriple => "fact_triple",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Parses a stored name. Names are case-sensitive, as upstream.</summary>
    /// <exception cref="NotSupportedException">The name is not a known episode type.</exception>
    public static EpisodeType FromStorageName(string name) => name switch
    {
        "message" => EpisodeType.Message,
        "json" => EpisodeType.Json,
        "text" => EpisodeType.Text,
        "fact_triple" => EpisodeType.FactTriple,
        _ => throw new NotSupportedException($"Episode type: {name} not implemented"),
    };
}

/// <summary>A node in the graph. Storage operations live in the graph driver, not on the node.</summary>
public abstract class Node
{
    private IReadOnlyList<string> _labels = [];

    /// <summary>The node's id: a UUID string, as upstream.</summary>
    public string Uuid { get; init; } = NewUuid();

    /// <summary>The node's name.</summary>
    public required string Name { get; set; }

    /// <summary>The graph partition the node belongs to, for example one user or tenant.</summary>
    public required string GroupId { get; init; }

    /// <summary>Type labels, such as a custom entity type's name.</summary>
    /// <exception cref="NodeLabelValidationException">A label is not a safe identifier.</exception>
    public IReadOnlyList<string> Labels
    {
        get => _labels;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Validation.ValidateNodeLabels(value);
            _labels = [.. value];
        }
    }

    /// <summary>When graphiti-net recorded the node.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeUtils.UtcNow();

    internal static string NewUuid() => Guid.NewGuid().ToString();
}

/// <summary>An episode: one raw input, stored as ingested. Everything extracted points back to an episode.</summary>
public sealed class EpisodicNode : Node
{
    /// <summary>The kind of input.</summary>
    public required EpisodeType Source { get; init; }

    /// <summary>A description of where the input came from.</summary>
    public required string SourceDescription { get; init; }

    /// <summary>The raw input.</summary>
    public required string Content { get; set; }

    /// <summary>When the original input was created. Relative times in the content, like "last week", are read
    /// from here.</summary>
    public required DateTimeOffset ValidAt { get; init; }

    /// <summary>Ids of the facts (entity edges) this episode produced or mentioned.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "The pipeline appends to it, as upstream does.")]
    public List<string> EntityEdges { get; init; } = [];

    /// <summary>Caller-defined key-value pairs for filtering.</summary>
    public IDictionary<string, object?>? EpisodeMetadata { get; init; }

    /// <summary>
    /// The host's idempotency key for the call that created the episode, unique within <see cref="Node.GroupId"/>,
    /// or <see langword="null"/>. A call to <c>Graphiti.AddEpisodeAsync</c> or <c>AddEpisodeBulkAsync</c> that
    /// carries a key already stored in the group resumes that episode instead of creating another. A graphiti-net
    /// addition (tekthar/graphiti-net#9); upstream has no such field.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>An entity: a person, product, policy, concept and so on.</summary>
public sealed class EntityNode : Node
{
    /// <summary>Embedding of the name, or <see langword="null"/> if not generated or not loaded.</summary>
    [SuppressMessage("Performance", "CA1819", Justification = "An embedding is a plain vector.")]
    public float[]? NameEmbedding { get; set; }

    /// <summary>A summary of the entity built from its facts. It changes as episodes arrive.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Attributes extracted for the entity's custom type, keyed by attribute name.</summary>
    public IDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
}

/// <summary>A community: a cluster of related entities with a summary.</summary>
public sealed class CommunityNode : Node
{
    /// <summary>Embedding of the name, or <see langword="null"/> if not generated or not loaded.</summary>
    [SuppressMessage("Performance", "CA1819", Justification = "An embedding is a plain vector.")]
    public float[]? NameEmbedding { get; set; }

    /// <summary>A summary of the member entities.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// A saga: an ordered chain of episodes, such as one conversation, with a summary. Episodes join a saga through
/// <see cref="HasEpisodeEdge"/> and are chained in order by <see cref="NextEpisodeEdge"/>.
/// </summary>
/// <remarks>Upstream's private counters for summary diagnostics (<c>_summary_episodes_fetched</c> and so on) are
/// internal here; <c>Graphiti.SummarizeSagaAsync</c> sets them. They are not stored.</remarks>
public sealed class SagaNode : Node
{
    /// <summary>A summary of the saga's episodes.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The first episode in the saga.</summary>
    public string? FirstEpisodeUuid { get; set; }

    /// <summary>The latest episode in the saga.</summary>
    public string? LastEpisodeUuid { get; set; }

    /// <summary>When the summary was last updated (ingestion time). The next incremental summary starts from
    /// episodes created after this.</summary>
    public DateTimeOffset? LastSummarizedAt { get; set; }

    /// <summary>The latest episode reference time (<see cref="EpisodicNode.ValidAt"/>) the summary covers.</summary>
    public DateTimeOffset? LastSummarizedEpisodeValidAt { get; set; }

    /// <summary>Episodes the last summary run fetched (upstream <c>_summary_episodes_fetched</c>).</summary>
    internal int SummaryEpisodesFetched { get; set; }

    /// <summary>Episodes the last summary run sent to the LLM (upstream <c>_summary_episodes_selected</c>).</summary>
    internal int SummaryEpisodesSelected { get; set; }

    /// <summary>Fetched episodes left for a later run (upstream <c>_summary_episodes_skipped</c>).</summary>
    internal int SummaryEpisodesSkipped { get; set; }

    /// <summary>UTF-8 bytes of the selected episodes (upstream <c>_summary_selected_episode_bytes</c>).</summary>
    internal int SummarySelectedEpisodeBytes { get; set; }
}
