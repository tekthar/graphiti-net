// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/edges.py (Graphiti v0.31.2) and changed for .NET.

using System.Diagnostics.CodeAnalysis;

namespace GraphitiNet.Models;

/// <summary>An edge in the graph. Storage operations live in the graph driver, not on the edge.</summary>
public abstract class Edge
{
    /// <summary>The edge's id: a UUID string, as upstream.</summary>
    public string Uuid { get; init; } = Node.NewUuid();

    /// <summary>The graph partition the edge belongs to.</summary>
    public required string GroupId { get; init; }

    /// <summary>The node the edge starts from.</summary>
    public required string SourceNodeUuid { get; set; }

    /// <summary>The node the edge points to.</summary>
    public required string TargetNodeUuid { get; set; }

    /// <summary>When graphiti-net recorded the edge. Upstream has no default for edges, so it is required.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Links an episode (source) to an entity it mentions (target).</summary>
public sealed class EpisodicEdge : Edge;

/// <summary>
/// A fact: a relationship from one entity (source) to another (target), with the time window in which it was true.
/// </summary>
/// <remarks>
/// Facts are bi-temporal. <see cref="ValidAt"/> and <see cref="InvalidAt"/> say when the fact was true in the world;
/// <see cref="Edge.CreatedAt"/> and <see cref="ExpiredAt"/> say when graphiti-net learned it and when it was
/// superseded. Facts are closed, never deleted, when contradicted.
/// </remarks>
public sealed class EntityEdge : Edge
{
    /// <summary>The relationship's name, such as <c>WORKS_AT</c>.</summary>
    public required string Name { get; set; }

    /// <summary>The fact in words, naming both entities.</summary>
    public required string Fact { get; set; }

    /// <summary>Embedding of <see cref="Fact"/>, or <see langword="null"/> if not generated or not loaded.</summary>
    [SuppressMessage("Performance", "CA1819", Justification = "An embedding is a plain vector.")]
    public float[]? FactEmbedding { get; set; }

    /// <summary>Ids of the episodes that state this fact.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "The pipeline appends to it, as upstream does.")]
    public List<string> Episodes { get; init; } = [];

    /// <summary>When graphiti-net marked the fact superseded, or <see langword="null"/>.</summary>
    public DateTimeOffset? ExpiredAt { get; set; }

    /// <summary>When the fact became true. For a plan or promise, this is the episode's reference time, not the
    /// date it is due.</summary>
    public DateTimeOffset? ValidAt { get; set; }

    /// <summary>When the fact stopped being true, or <see langword="null"/> while still true. A soft deadline is not
    /// an end date.</summary>
    public DateTimeOffset? InvalidAt { get; set; }

    /// <summary>The reference time of the episode that produced this fact.</summary>
    public DateTimeOffset? ReferenceTime { get; set; }

    /// <summary>Attributes extracted for the edge's custom type, keyed by attribute name.</summary>
    public IDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Links a community (source) to a member entity or sub-community (target).</summary>
public sealed class CommunityEdge : Edge;

/// <summary>Links a saga (source) to one of its episodes (target).</summary>
public sealed class HasEpisodeEdge : Edge;

/// <summary>Links an episode (source) to the next episode in the same saga (target).</summary>
public sealed class NextEpisodeEdge : Edge;
