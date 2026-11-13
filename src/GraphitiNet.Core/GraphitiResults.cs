// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/graphiti.py (AddEpisodeResults, AddBulkEpisodeResults, AddTripletResults) and
// graphiti_core/utils/bulk_utils.py (RawEpisode) (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Llm;
using GraphitiNet.Models;

namespace GraphitiNet;

/// <summary>What <see cref="Graphiti.AddEpisodeAsync"/> added or changed (upstream <c>AddEpisodeResults</c>).</summary>
public sealed record AddEpisodeResults
{
    /// <summary>The episode.</summary>
    public required EpisodicNode Episode { get; init; }

    /// <summary>The links from the episode to the entities it mentions.</summary>
    public required IReadOnlyList<EpisodicEdge> EpisodicEdges { get; init; }

    /// <summary>The entities the episode mentions, resolved against the graph and with updated summaries.</summary>
    public required IReadOnlyList<EntityNode> Nodes { get; init; }

    /// <summary>The facts the episode states, followed by the existing facts it invalidated.</summary>
    public required IReadOnlyList<EntityEdge> Edges { get; init; }

    /// <summary>Communities updated, when <c>updateCommunities</c> was set.</summary>
    public required IReadOnlyList<CommunityNode> Communities { get; init; }

    /// <summary>Community memberships added, when <c>updateCommunities</c> was set.</summary>
    public required IReadOnlyList<CommunityEdge> CommunityEdges { get; init; }

    /// <summary>Tokens the LLM calls of this call used, in total. Measured for this call only, even when other calls
    /// run concurrently. A graphiti-net addition.</summary>
    public TokenUsage TokenUsage { get; init; }

    /// <summary>Tokens per prompt name for this call. A graphiti-net addition.</summary>
    public IReadOnlyDictionary<string, PromptTokenUsage> TokenUsageByPrompt { get; init; } =
        new Dictionary<string, PromptTokenUsage>();
}

/// <summary>What <see cref="Graphiti.AddEpisodeBulkAsync"/> added or changed (upstream
/// <c>AddBulkEpisodeResults</c>).</summary>
public sealed record AddBulkEpisodeResults
{
    /// <summary>The episodes.</summary>
    public required IReadOnlyList<EpisodicNode> Episodes { get; init; }

    /// <summary>The links from the episodes to the entities they mention.</summary>
    public required IReadOnlyList<EpisodicEdge> EpisodicEdges { get; init; }

    /// <summary>The entities the episodes mention.</summary>
    public required IReadOnlyList<EntityNode> Nodes { get; init; }

    /// <summary>The facts the episodes state, followed by the existing facts they invalidated.</summary>
    public required IReadOnlyList<EntityEdge> Edges { get; init; }

    /// <summary>Always empty, as upstream: bulk ingestion does not update communities.</summary>
    public required IReadOnlyList<CommunityNode> Communities { get; init; }

    /// <summary>Always empty, as upstream.</summary>
    public required IReadOnlyList<CommunityEdge> CommunityEdges { get; init; }

    /// <summary>Tokens the LLM calls of this call used, in total. A graphiti-net addition.</summary>
    public TokenUsage TokenUsage { get; init; }

    /// <summary>Tokens per prompt name for this call. A graphiti-net addition.</summary>
    public IReadOnlyDictionary<string, PromptTokenUsage> TokenUsageByPrompt { get; init; } =
        new Dictionary<string, PromptTokenUsage>();
}

/// <summary>What <see cref="Graphiti.AddTripletAsync"/> saved (upstream <c>AddTripletResults</c>).</summary>
public sealed record AddTripletResults
{
    /// <summary>The source and target entities, as saved.</summary>
    public required IReadOnlyList<EntityNode> Nodes { get; init; }

    /// <summary>The fact, as resolved, followed by the facts it invalidated.</summary>
    public required IReadOnlyList<EntityEdge> Edges { get; init; }
}

/// <summary>One episode for <see cref="Graphiti.AddEpisodeBulkAsync"/> (upstream <c>RawEpisode</c>).</summary>
public sealed record RawEpisode
{
    /// <summary>The episode's name.</summary>
    public required string Name { get; init; }

    /// <summary>An existing episode's id, to reprocess it; its stored content is used.</summary>
    public string? Uuid { get; init; }

    /// <summary>The content.</summary>
    public required string Content { get; init; }

    /// <summary>Where the content came from.</summary>
    public required string SourceDescription { get; init; }

    /// <summary>The kind of content.</summary>
    public required EpisodeType Source { get; init; }

    /// <summary>When the content was created; relative times in it are read from here.</summary>
    public required DateTimeOffset ReferenceTime { get; init; }

    /// <summary>Host data stored on the episode (<see cref="EpisodicNode.EpisodeMetadata"/>). A graphiti-net
    /// addition; upstream's <c>RawEpisode</c> has no such field.</summary>
    public IDictionary<string, object?>? EpisodeMetadata { get; init; }
}

/// <summary>
/// The saga an episode joins: either a saga name, looked up in the episode's group and created if missing, or a
/// saga node. Upstream's <c>saga: str | SagaNode | None</c>; strings and saga nodes convert implicitly, so
/// <c>saga: "support-chat"</c> works.
/// </summary>
public sealed class SagaReference
{
    private SagaReference(string? name, SagaNode? node)
    {
        Name = name;
        Node = node;
    }

    /// <summary>The saga's name, when given by name.</summary>
    public string? Name { get; }

    /// <summary>The saga node, when given as a node.</summary>
    public SagaNode? Node { get; }

    /// <summary>A saga given by name.</summary>
    public static SagaReference FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new SagaReference(name, null);
    }

    /// <summary>A saga given as a node.</summary>
    public static SagaReference FromNode(SagaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new SagaReference(null, node);
    }

    /// <summary>A saga given by name.</summary>
    public static implicit operator SagaReference(string name) => FromName(name);

    /// <summary>A saga given as a node.</summary>
    public static implicit operator SagaReference(SagaNode node) => FromNode(node);
}
