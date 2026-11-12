// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/namespaces/nodes.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;

namespace GraphitiNet.Namespaces;

/// <summary>Node operations, accessed as <c>graphiti.Nodes</c> (upstream <c>graphiti.nodes</c>).</summary>
public sealed class NodeNamespace
{
    internal NodeNamespace(IGraphDriver driver, EmbedderClient embedder)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(embedder);
        Entity = new EntityNodeNamespace(driver.EntityNodeOps, embedder);
        Episode = new EpisodeNodeNamespace(driver.EpisodeNodeOps);
        Community = new CommunityNodeNamespace(driver.CommunityNodeOps, embedder);
        Saga = new SagaNodeNamespace(driver.SagaNodeOps);
    }

    /// <summary>Entities (upstream <c>graphiti.nodes.entity</c>).</summary>
    public EntityNodeNamespace Entity { get; }

    /// <summary>Episodes (upstream <c>graphiti.nodes.episode</c>).</summary>
    public EpisodeNodeNamespace Episode { get; }

    /// <summary>Communities (upstream <c>graphiti.nodes.community</c>).</summary>
    public CommunityNodeNamespace Community { get; }

    /// <summary>Sagas (upstream <c>graphiti.nodes.saga</c>).</summary>
    public SagaNodeNamespace Saga { get; }
}

/// <summary>Entity operations (upstream <c>EntityNodeNamespace</c>).</summary>
public sealed class EntityNodeNamespace : NodeRecordNamespace<EntityNode>
{
    private readonly IEntityNodeOperations _ops;
    private readonly EmbedderClient _embedder;

    internal EntityNodeNamespace(IEntityNodeOperations ops, EmbedderClient embedder)
        : base(ops)
    {
        _ops = ops;
        _embedder = embedder;
    }

    /// <summary>Embeds the entity's name, then saves it (upstream <c>save</c>).</summary>
    public override async Task<EntityNode> SaveAsync(EntityNode item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.NameEmbedding = await LLMUtils.GenerateEmbeddingAsync(_embedder, item.Name, cancellationToken)
            .ConfigureAwait(false);
        return await base.SaveAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the entity's name embedding (upstream <c>load_embeddings</c>).</summary>
    public Task LoadEmbeddingsAsync(EntityNode node, CancellationToken cancellationToken = default) =>
        _ops.LoadEmbeddingsAsync(node, cancellationToken);

    /// <summary>Loads each entity's name embedding (upstream <c>load_embeddings_bulk</c>).</summary>
    public Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityNode> nodes, CancellationToken cancellationToken = default) =>
        _ops.LoadEmbeddingsBulkAsync(nodes, cancellationToken);
}

/// <summary>Episode operations (upstream <c>EpisodeNodeNamespace</c>).</summary>
public sealed class EpisodeNodeNamespace : NodeRecordNamespace<EpisodicNode>
{
    private readonly IEpisodeNodeOperations _ops;

    internal EpisodeNodeNamespace(IEpisodeNodeOperations ops)
        : base(ops)
    {
        _ops = ops;
    }

    /// <summary>The episodes that mention an entity (upstream <c>get_by_entity_node_uuid</c>).</summary>
    public Task<IReadOnlyList<EpisodicNode>> GetByEntityNodeUuidAsync(
        string entityNodeUuid,
        CancellationToken cancellationToken = default) =>
        _ops.GetByEntityNodeUuidAsync(entityNodeUuid, cancellationToken);

    /// <summary>The <paramref name="lastN"/> latest episodes at or before <paramref name="referenceTime"/>, oldest
    /// first (upstream <c>retrieve_episodes</c>; note the default of 3 here, as upstream's namespace).</summary>
    public Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        DateTimeOffset referenceTime,
        int lastN = 3,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default) =>
        _ops.RetrieveEpisodesAsync(referenceTime, lastN, groupIds, source, saga, cancellationToken);
}

/// <summary>Community operations (upstream <c>CommunityNodeNamespace</c>).</summary>
public sealed class CommunityNodeNamespace : NodeRecordNamespace<CommunityNode>
{
    private readonly ICommunityNodeOperations _ops;
    private readonly EmbedderClient _embedder;

    internal CommunityNodeNamespace(ICommunityNodeOperations ops, EmbedderClient embedder)
        : base(ops)
    {
        _ops = ops;
        _embedder = embedder;
    }

    /// <summary>Embeds the community's name, then saves it (upstream <c>save</c>).</summary>
    public override async Task<CommunityNode> SaveAsync(
        CommunityNode item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.NameEmbedding = await LLMUtils.GenerateEmbeddingAsync(_embedder, item.Name, cancellationToken)
            .ConfigureAwait(false);
        return await base.SaveAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the community's name embedding (upstream <c>load_name_embedding</c>).</summary>
    public Task LoadNameEmbeddingAsync(CommunityNode node, CancellationToken cancellationToken = default) =>
        _ops.LoadNameEmbeddingAsync(node, cancellationToken);
}

/// <summary>Saga operations (upstream <c>SagaNodeNamespace</c>).</summary>
public sealed class SagaNodeNamespace : NodeRecordNamespace<SagaNode>
{
    internal SagaNodeNamespace(ISagaNodeOperations ops)
        : base(ops)
    {
    }
}
