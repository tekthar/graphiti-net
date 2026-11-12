// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/namespaces/edges.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;

namespace GraphitiNet.Namespaces;

/// <summary>Edge operations, accessed as <c>graphiti.Edges</c> (upstream <c>graphiti.edges</c>).</summary>
public sealed class EdgeNamespace
{
    internal EdgeNamespace(IGraphDriver driver, EmbedderClient embedder)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(embedder);
        Entity = new EntityEdgeNamespace(driver.EntityEdgeOps, embedder);
        Episodic = new EpisodicEdgeNamespace(driver.EpisodicEdgeOps);
        Community = new CommunityEdgeNamespace(driver.CommunityEdgeOps);
        HasEpisode = new HasEpisodeEdgeNamespace(driver.HasEpisodeEdgeOps);
        NextEpisode = new NextEpisodeEdgeNamespace(driver.NextEpisodeEdgeOps);
    }

    /// <summary>Facts (upstream <c>graphiti.edges.entity</c>).</summary>
    public EntityEdgeNamespace Entity { get; }

    /// <summary>Episode-to-entity links (upstream <c>graphiti.edges.episodic</c>).</summary>
    public EpisodicEdgeNamespace Episodic { get; }

    /// <summary>Community memberships (upstream <c>graphiti.edges.community</c>).</summary>
    public CommunityEdgeNamespace Community { get; }

    /// <summary>Saga-to-episode links (upstream <c>graphiti.edges.has_episode</c>).</summary>
    public HasEpisodeEdgeNamespace HasEpisode { get; }

    /// <summary>Episode chain links (upstream <c>graphiti.edges.next_episode</c>).</summary>
    public NextEpisodeEdgeNamespace NextEpisode { get; }
}

/// <summary>Fact operations (upstream <c>EntityEdgeNamespace</c>).</summary>
public sealed class EntityEdgeNamespace : RecordNamespace<EntityEdge>
{
    private readonly IEntityEdgeOperations _ops;
    private readonly EmbedderClient _embedder;

    internal EntityEdgeNamespace(IEntityEdgeOperations ops, EmbedderClient embedder)
        : base(ops)
    {
        _ops = ops;
        _embedder = embedder;
    }

    /// <summary>Embeds the fact, then saves it (upstream <c>save</c>).</summary>
    public override async Task<EntityEdge> SaveAsync(EntityEdge item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.FactEmbedding = await LLMUtils.GenerateEmbeddingAsync(_embedder, item.Fact, cancellationToken)
            .ConfigureAwait(false);
        return await base.SaveAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Facts from one entity to another, that direction only (upstream <c>get_between_nodes</c>).</summary>
    public Task<IReadOnlyList<EntityEdge>> GetBetweenNodesAsync(
        string sourceNodeUuid,
        string targetNodeUuid,
        CancellationToken cancellationToken = default) =>
        _ops.GetBetweenNodesAsync(sourceNodeUuid, targetNodeUuid, cancellationToken);

    /// <summary>Facts that start or end at an entity (upstream <c>get_by_node_uuid</c>).</summary>
    public Task<IReadOnlyList<EntityEdge>> GetByNodeUuidAsync(
        string nodeUuid,
        CancellationToken cancellationToken = default) =>
        _ops.GetByNodeUuidAsync(nodeUuid, cancellationToken);

    /// <summary>Loads the fact embedding (upstream <c>load_embeddings</c>).</summary>
    public Task LoadEmbeddingsAsync(EntityEdge edge, CancellationToken cancellationToken = default) =>
        _ops.LoadEmbeddingsAsync(edge, cancellationToken);

    /// <summary>Loads each fact's embedding (upstream <c>load_embeddings_bulk</c>).</summary>
    public Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityEdge> edges, CancellationToken cancellationToken = default) =>
        _ops.LoadEmbeddingsBulkAsync(edges, cancellationToken);
}

/// <summary>Episode-to-entity link operations (upstream <c>EpisodicEdgeNamespace</c>).</summary>
public sealed class EpisodicEdgeNamespace : RecordNamespace<EpisodicEdge>
{
    internal EpisodicEdgeNamespace(IEpisodicEdgeOperations ops)
        : base(ops)
    {
    }
}

/// <summary>Community membership operations (upstream <c>CommunityEdgeNamespace</c>).</summary>
public sealed class CommunityEdgeNamespace : RecordNamespace<CommunityEdge>
{
    internal CommunityEdgeNamespace(ICommunityEdgeOperations ops)
        : base(ops)
    {
    }
}

/// <summary>Saga-to-episode link operations (upstream <c>HasEpisodeEdgeNamespace</c>).</summary>
public sealed class HasEpisodeEdgeNamespace : RecordNamespace<HasEpisodeEdge>
{
    internal HasEpisodeEdgeNamespace(IHasEpisodeEdgeOperations ops)
        : base(ops)
    {
    }
}

/// <summary>Episode chain link operations (upstream <c>NextEpisodeEdgeNamespace</c>).</summary>
public sealed class NextEpisodeEdgeNamespace : RecordNamespace<NextEpisodeEdge>
{
    internal NextEpisodeEdgeNamespace(INextEpisodeEdgeOperations ops)
        : base(ops)
    {
    }
}
