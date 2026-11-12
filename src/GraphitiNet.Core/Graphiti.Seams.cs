// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/graphiti.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet;

// The calls from Graphiti into the extraction and resolution pipeline (Maintenance/NodeOperations,
// Maintenance/EdgeOperations, Maintenance/BulkUtils). Each seam is one call; the comment above it names the upstream
// function and the arguments upstream passes. Seams that throw NotImplementedException are wired once those
// operations are merged.
public sealed partial class Graphiti
{
    private const string NotWired = "wired after NodeOperations/EdgeOperations merge";

    // node_operations.extract_nodes(clients, episode, previous_episodes, entity_types, excluded_entity_types,
    //     custom_extraction_instructions) -> (extracted_nodes, node_episode_index_map)
    // add_episode passes its single episode (here episodes[0]).
    private static Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, IReadOnlyList<int>>? NodeEpisodeIndexMap)>
        ExtractNodesAsync(
            GraphitiClients clients,
            IReadOnlyList<EpisodicNode> episodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyList<string>? excludedEntityTypes,
            string? customExtractionInstructions,
            CancellationToken cancellationToken) =>
        WireExtractNodesAsync(clients, episodes, previousEpisodes, entityTypes, excludedEntityTypes,
            customExtractionInstructions, cancellationToken);

    // node_operations.resolve_extracted_nodes(clients, extracted_nodes, episode, previous_episodes, entity_types)
    //     -> (resolved_nodes, uuid_map, duplicate_pairs)
    // add_triplet passes only (clients, [node]): episode, previous_episodes and entity_types are None.
    private static Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, string> UuidMap,
            IReadOnlyList<(EntityNode, EntityNode)> Duplicates)>
        ResolveExtractedNodesAsync(
            GraphitiClients clients,
            IReadOnlyList<EntityNode> extractedNodes,
            EpisodicNode? episode,
            IReadOnlyList<EpisodicNode>? previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes,
            CancellationToken cancellationToken) =>
        WireResolveExtractedNodesAsync(clients, extractedNodes, episode, previousEpisodes, entityTypes, cancellationToken);

    // edge_operations.extract_edges(clients, episode, nodes, previous_episodes, edge_type_map, group_id, edge_types,
    //     strict_edge_types, custom_extraction_instructions) -> (extracted_edges, materialized_nodes)
    // `nodes` is the pre-resolution extracted node list, as upstream. add_episode passes its single episode.
    private static Task<(IReadOnlyList<EntityEdge> Edges, IReadOnlyList<EntityNode> MaterializedNodes)>
        ExtractEdgesAsync(
            GraphitiClients clients,
            IReadOnlyList<EpisodicNode> episodes,
            IReadOnlyList<EntityNode> nodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            string groupId,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            bool strictEdgeTypes,
            string? customExtractionInstructions,
            CancellationToken cancellationToken) =>
        Maintenance.EdgeOperations.ExtractEdgesAsync(clients, episodes, nodes.ToList(), previousEpisodes, edgeTypeMap,
            groupId, edgeTypes, strictEdgeTypes, customExtractionInstructions, cancellationToken);

    // bulk_utils.resolve_edge_pointers(edges, uuid_map) -> surviving edges (endpoints rewritten in place; edges whose
    // distinct endpoints collapse onto one node are dropped). Used for entity edges and episodic edges.
    private static List<TEdge> ResolveEdgePointers<TEdge>(
        IReadOnlyList<TEdge> edges,
        IReadOnlyDictionary<string, string> uuidMap)
        where TEdge : Edge =>
        Maintenance.BulkUtils.ResolveEdgePointers(edges, uuidMap).ToList();

    // edge_operations.resolve_extracted_edges(clients, extracted_edges, episode, entities, edge_types or {},
    //     edge_type_map) -> (resolved_edges, invalidated_edges, new_edges)
    private static Task<(IReadOnlyList<EntityEdge> Resolved, IReadOnlyList<EntityEdge> Invalidated,
            IReadOnlyList<EntityEdge> New)>
        ResolveExtractedEdgesAsync(
            GraphitiClients clients,
            IReadOnlyList<EntityEdge> extractedEdges,
            EpisodicNode episode,
            IReadOnlyList<EntityNode> entities,
            IReadOnlyDictionary<string, Type> edgeTypes,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            CancellationToken cancellationToken) =>
        Maintenance.EdgeOperations.ResolveExtractedEdgesAsync(clients, extractedEdges, episode, entities, edgeTypes,
            edgeTypeMap, cancellationToken: cancellationToken);

    // node_operations.extract_attributes_from_nodes(clients, nodes, episode, previous_episodes, entity_types,
    //     edges=new_edges) -> hydrated nodes. add_episode passes its single episode and edges=new_edges; the bulk
    // path passes edges=None.
    private static Task<IReadOnlyList<EntityNode>> ExtractAttributesFromNodesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EpisodicNode> episodes,
        IReadOnlyList<EpisodicNode> previousEpisodes,
        IReadOnlyDictionary<string, Type>? entityTypes,
        IReadOnlyList<EntityEdge>? edges,
        CancellationToken cancellationToken) =>
        Maintenance.NodeOperations.ExtractAttributesFromNodesAsync(clients, nodes, episodes, previousEpisodes,
            entityTypes, edges: edges, cancellationToken: cancellationToken);

    // edge_operations.build_episodic_edges(entity_nodes, episode_uuids, created_at, node_episode_index_map)
    private static IReadOnlyList<EpisodicEdge> BuildEpisodicEdges(
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<string> episodeUuids,
        DateTimeOffset createdAt,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? nodeEpisodeIndexMap) =>
        Maintenance.EdgeOperations.BuildEpisodicEdges(entityNodes, episodeUuids, createdAt, nodeEpisodeIndexMap);

    // edge_operations.resolve_extracted_edge(llm_client, extracted_edge, related_edges, existing_edges, episode,
    //     None, clients=clients) -> (resolved_edge, invalidated_edges, duplicates); add_triplet ignores the third.
    private static Task<(EntityEdge Resolved, IReadOnlyList<EntityEdge> Invalidated)> ResolveExtractedEdgeAsync(
        GraphitiClients clients,
        EntityEdge extractedEdge,
        IReadOnlyList<EntityEdge> relatedEdges,
        IReadOnlyList<EntityEdge> existingEdges,
        EpisodicNode episode,
        CancellationToken cancellationToken) =>
        WireResolveExtractedEdgeAsync(clients, extractedEdge, relatedEdges, existingEdges, episode, cancellationToken);

    // edge_operations._pair_search_filter(edge): SearchFilters(edge_source_node_uuids=[src, tgt],
    //     edge_target_node_uuids=[src, tgt]).
    private static SearchFilters PairSearchFilter(EntityEdge edge) =>
        Maintenance.EdgeOperations.PairSearchFilter(edge);

    // edge_operations._same_pair_edges(extracted_edge, candidates): candidates between the same two endpoints, either
    // direction.
    private static List<EntityEdge> SamePairEdges(EntityEdge edge, IReadOnlyList<EntityEdge> candidates) =>
        Maintenance.EdgeOperations.SamePairEdges(edge, candidates);

    // node_operations._extract_entity_summaries_batch(llm_client, [node], episode_batch, None, None, {},
    //     skip_fact_appending=True, clients=clients): previous_episodes=None, should_summarize_node=None,
    //     edges_by_node={}. Rebuilds node.Summary in place from the batch.
    // Target name: NodeOperations.ExtractEntitySummariesBatchAsync(clients, nodes, episodes, previousEpisodes: null,
    //     edges: null, entityTypes: null, skipFactAppending: true, ct).
    private static Task ExtractEntitySummariesBatchAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EpisodicNode> episodeBatch,
        CancellationToken cancellationToken) =>
        Maintenance.NodeOperations.ExtractEntitySummariesBatchAsync(clients, nodes, episodeBatch, null, null,
            new Dictionary<string, IReadOnlyList<EntityEdge>>(), skipFactAppending: true,
            cancellationToken: cancellationToken);

    // bulk_utils.retrieve_previous_episodes_bulk(driver, episodes, max_coroutines=self.max_coroutines)
    //     -> [(episode, previous_episodes)]
    private Task<IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)>>
        RetrievePreviousEpisodesBulkAsync(
            IGraphDriver driver,
            IReadOnlyList<EpisodicNode> episodes,
            CancellationToken cancellationToken) =>
        throw new NotImplementedException(NotWired);

    // bulk_utils.extract_nodes_and_edges_bulk(clients, episode_context, edge_type_map=..., entity_types=...,
    //     excluded_entity_types=..., edge_types=..., custom_extraction_instructions=...,
    //     use_combined_extraction=..., strict_edge_types=...) -> (nodes per episode, edges per episode)
    private static Task<(IReadOnlyList<IReadOnlyList<EntityNode>> Nodes, IReadOnlyList<IReadOnlyList<EntityEdge>> Edges)>
        ExtractNodesAndEdgesBulkAsync(
            GraphitiClients clients,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeContext,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyList<string>? excludedEntityTypes,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            string? customExtractionInstructions,
            bool useCombinedExtraction,
            bool strictEdgeTypes,
            CancellationToken cancellationToken) =>
        throw new NotImplementedException(NotWired);

    // bulk_utils.dedupe_nodes_bulk(clients, extracted_nodes, episode_context, entity_types)
    //     -> (nodes_by_episode uuid, uuid_map)
    private static Task<(IReadOnlyDictionary<string, IReadOnlyList<EntityNode>> NodesByEpisode,
            IReadOnlyDictionary<string, string> UuidMap)>
        DedupeNodesBulkAsync(
            GraphitiClients clients,
            IReadOnlyList<IReadOnlyList<EntityNode>> extractedNodes,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeContext,
            IReadOnlyDictionary<string, Type>? entityTypes,
            CancellationToken cancellationToken) =>
        throw new NotImplementedException(NotWired);

    // bulk_utils.dedupe_edges_bulk(clients, extracted_edges, episode_context, [], edge_types or {}, edge_type_map)
    //     -> edges_by_episode uuid
    private static Task<IReadOnlyDictionary<string, IReadOnlyList<EntityEdge>>> DedupeEdgesBulkAsync(
        GraphitiClients clients,
        IReadOnlyList<IReadOnlyList<EntityEdge>> extractedEdges,
        IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeContext,
        IReadOnlyList<EntityNode> entities,
        IReadOnlyDictionary<string, Type> edgeTypes,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException(NotWired);

    // bulk_utils.add_nodes_and_edges_bulk(driver, episodic_nodes, episodic_edges, entity_nodes, entity_edges,
    //     embedder): embeds entity names and facts that have no embedding yet, then saves everything in one
    // transaction. Implemented here so episodes and triplets can be saved now; switch to BulkUtils'
    // AddNodesAndEdgesBulkAsync once merged if it differs.
    private static async Task AddNodesAndEdgesBulkAsync(
        IGraphDriver driver,
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        EmbedderClient embedder,
        CancellationToken cancellationToken)
    {
        foreach (var node in entityNodes)
        {
            node.NameEmbedding ??= await Llm.LLMUtils.GenerateEmbeddingAsync(embedder, node.Name, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var edge in entityEdges)
        {
            edge.FactEmbedding ??= await Llm.LLMUtils.GenerateEmbeddingAsync(embedder, edge.Fact, cancellationToken)
                .ConfigureAwait(false);
        }

        await driver.AddNodesAndEdgesBulkAsync(episodicNodes, episodicEdges, entityNodes, entityEdges,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, IReadOnlyList<int>>? NodeEpisodeIndexMap)>
        WireExtractNodesAsync(
            GraphitiClients clients,
            IReadOnlyList<EpisodicNode> episodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyList<string>? excludedEntityTypes,
            string? customExtractionInstructions,
            CancellationToken cancellationToken)
    {
        var (nodes, map) = await Maintenance.NodeOperations.ExtractNodesAsync(
            clients, episodes, previousEpisodes, entityTypes, excludedEntityTypes, customExtractionInstructions,
            cancellationToken).ConfigureAwait(false);
        return (nodes, map);
    }

    private static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, string> UuidMap,
            IReadOnlyList<(EntityNode, EntityNode)> Duplicates)>
        WireResolveExtractedNodesAsync(
            GraphitiClients clients,
            IReadOnlyList<EntityNode> extractedNodes,
            EpisodicNode? episode,
            IReadOnlyList<EpisodicNode>? previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes,
            CancellationToken cancellationToken)
    {
        var (nodes, uuidMap, duplicates) = await Maintenance.NodeOperations.ResolveExtractedNodesAsync(
            clients, extractedNodes, episode, previousEpisodes, entityTypes, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (nodes, uuidMap, duplicates.Select(d => (d.Extracted, d.Resolved)).ToList());
    }

    private static async Task<(EntityEdge Resolved, IReadOnlyList<EntityEdge> Invalidated)> WireResolveExtractedEdgeAsync(
        GraphitiClients clients,
        EntityEdge extractedEdge,
        IReadOnlyList<EntityEdge> relatedEdges,
        IReadOnlyList<EntityEdge> existingEdges,
        EpisodicNode episode,
        CancellationToken cancellationToken)
    {
        var (resolved, invalidated, _) = await Maintenance.EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extractedEdge, relatedEdges, existingEdges, episode, null, cancellationToken)
            .ConfigureAwait(false);
        return (resolved, invalidated);
    }
}
