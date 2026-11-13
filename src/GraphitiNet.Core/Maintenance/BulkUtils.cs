// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/bulk_utils.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Search;
using GraphitiNet.Utils;
using Microsoft.Extensions.Logging;

namespace GraphitiNet.Maintenance;

/// <summary>Helpers for bulk ingestion and for re-pointing edges after deduplication.</summary>
public static partial class BulkUtils
{
    /// <summary>Upstream <c>CHUNK_SIZE</c>.</summary>
    public const int ChunkSize = 10;

    /// <summary>
    /// Collapses alias → canonical chains while keeping their direction (upstream <c>_build_directed_uuid_map</c>).
    /// </summary>
    /// <remarks>
    /// The pairs are directed mappings found during node deduplication. A union-find with path compression makes
    /// every source resolve to its final canonical target, whatever the ids' lexicographic order.
    /// </remarks>
    /// <param name="pairs">(alias, canonical) pairs.</param>
    /// <returns>Every id seen, mapped to its canonical id (canonical ids map to themselves).</returns>
    public static IReadOnlyDictionary<string, string> BuildDirectedUuidMap(
        IEnumerable<(string Source, string Target)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);

        string Find(string uuid)
        {
            parent.TryAdd(uuid, uuid);
            var root = uuid;
            while (parent[root] != root)
            {
                root = parent[root];
            }

            while (parent[uuid] != root)
            {
                var next = parent[uuid];
                parent[uuid] = root;
                uuid = next;
            }

            return root;
        }

        foreach (var (source, target) in pairs)
        {
            parent.TryAdd(source, source);
            parent.TryAdd(target, target);
            parent[Find(source)] = Find(target);
        }

        var result = new Dictionary<string, string>(parent.Count, StringComparer.Ordinal);
        foreach (var uuid in parent.Keys.ToList())
        {
            result[uuid] = Find(uuid);
        }

        return result;
    }

    /// <summary>
    /// Groups ids joined by duplicate pairs and maps each to the lexicographically smallest id in its group (upstream
    /// <c>compress_uuid_map</c>). Direction is ignored.
    /// </summary>
    /// <param name="duplicatePairs">Pairs of ids that are duplicates of each other.</param>
    /// <returns>Every id in a pair, mapped to its group's smallest id (by code point, as Python compares strings).</returns>
    public static IReadOnlyDictionary<string, string> CompressUuidMap(
        IEnumerable<(string First, string Second)> duplicatePairs)
    {
        ArgumentNullException.ThrowIfNull(duplicatePairs);
        var pairs = duplicatePairs.ToList();
        var allUuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (a, b) in pairs)
        {
            allUuids.Add(a);
            allUuids.Add(b);
        }

        var unionFind = new UnionFind(allUuids);
        foreach (var (a, b) in pairs)
        {
            unionFind.Union(a, b);
        }

        return allUuids.ToDictionary(uuid => uuid, unionFind.Find, StringComparer.Ordinal);
    }

    /// <summary>
    /// Re-points edge endpoints through <paramref name="uuidMap"/>, in place, and drops edges that collapse onto one
    /// node (upstream <c>resolve_edge_pointers</c>).
    /// </summary>
    /// <remarks>
    /// An edge whose two endpoints were different before remapping and are the same after it is left out of the
    /// result: deduplication can map an endpoint onto the edge's other endpoint. Edges extracted as self-references
    /// (source already equal to target) are kept. Use the returned list, not the input.
    /// </remarks>
    /// <typeparam name="TEdge">The edge type.</typeparam>
    /// <param name="edges">The edges to re-point.</param>
    /// <param name="uuidMap">Old id → new id; ids not in the map stay.</param>
    /// <returns>The surviving edges, in input order.</returns>
    public static IReadOnlyList<TEdge> ResolveEdgePointers<TEdge>(
        IEnumerable<TEdge> edges,
        IReadOnlyDictionary<string, string> uuidMap)
        where TEdge : Edge
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(uuidMap);
        var surviving = new List<TEdge>();
        foreach (var edge in edges)
        {
            var sourceUuid = edge.SourceNodeUuid;
            var targetUuid = edge.TargetNodeUuid;
            edge.SourceNodeUuid = uuidMap.GetValueOrDefault(sourceUuid, sourceUuid);
            edge.TargetNodeUuid = uuidMap.GetValueOrDefault(targetUuid, targetUuid);

            if (sourceUuid != targetUuid && edge.SourceNodeUuid == edge.TargetNodeUuid)
            {
                continue;
            }

            surviving.Add(edge);
        }

        return surviving;
    }

    /// <summary>
    /// Fetches each episode's previous episodes (the last <see cref="GraphDataOperations.EpisodeWindowLen"/> valid at
    /// or before it, in its group), concurrently (upstream <c>retrieve_previous_episodes_bulk</c>).
    /// </summary>
    /// <param name="driver">Storage.</param>
    /// <param name="episodes">The episodes.</param>
    /// <param name="maxCoroutines">The concurrency cap; <see langword="null"/> uses the default.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>Each episode with its previous episodes, in input order.</returns>
    public static async Task<IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)>>
        RetrievePreviousEpisodesBulkAsync(
            IGraphDriver driver,
            IReadOnlyList<EpisodicNode> episodes,
            int? maxCoroutines = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(episodes);
        var previousEpisodesList = await Concurrency.SemaphoreGatherAsync(
                episodes.Select<EpisodicNode, Func<Task<IReadOnlyList<EpisodicNode>>>>(episode =>
                    () => GraphDataOperations.RetrieveEpisodesAsync(
                        driver,
                        episode.ValidAt,
                        lastN: GraphDataOperations.EpisodeWindowLen,
                        groupIds: [episode.GroupId],
                        cancellationToken: cancellationToken)),
                maxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        return episodes.Select((episode, i) => (episode, previousEpisodesList[i])).ToList();
    }

    /// <summary>
    /// Embeds entity names and facts that have no embedding yet, then saves episodes, episodic edges, entities and
    /// facts in one driver call (upstream <c>add_nodes_and_edges_bulk</c> / <c>add_nodes_and_edges_bulk_tx</c>).
    /// </summary>
    /// <remarks>Names and facts are embedded one at a time, newlines replaced by spaces, as upstream's
    /// <c>generate_name_embedding</c> and <c>generate_embedding</c>.</remarks>
    /// <param name="driver">Storage.</param>
    /// <param name="episodicNodes">Episodes to save.</param>
    /// <param name="episodicEdges">MENTIONS edges to save.</param>
    /// <param name="entityNodes">Entities to save.</param>
    /// <param name="entityEdges">Facts to save.</param>
    /// <param name="embedder">The embedder for missing embeddings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task AddNodesAndEdgesBulkAsync(
        IGraphDriver driver,
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        EmbedderClient embedder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(episodicNodes);
        ArgumentNullException.ThrowIfNull(episodicEdges);
        ArgumentNullException.ThrowIfNull(entityNodes);
        ArgumentNullException.ThrowIfNull(entityEdges);
        ArgumentNullException.ThrowIfNull(embedder);
        foreach (var node in entityNodes)
        {
            node.NameEmbedding ??= await LLMUtils.GenerateEmbeddingAsync(embedder, node.Name, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var edge in entityEdges)
        {
            edge.FactEmbedding ??= await LLMUtils.GenerateEmbeddingAsync(embedder, edge.Fact, cancellationToken)
                .ConfigureAwait(false);
        }

        await driver.AddNodesAndEdgesBulkAsync(episodicNodes, episodicEdges, entityNodes, entityEdges,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts entities and facts from every episode of a batch, concurrently (upstream
    /// <c>extract_nodes_and_edges_bulk</c>).
    /// </summary>
    /// <remarks>
    /// With <paramref name="useCombinedExtraction"/> (the default) each episode gets one
    /// <see cref="CombinedExtractionOperations"/> call. Otherwise entities are extracted for every episode with
    /// <c>NodeOperations.ExtractNodesAsync</c>, then facts with <c>EdgeOperations.ExtractEdgesAsync</c>.
    /// </remarks>
    /// <param name="clients">The services.</param>
    /// <param name="episodeTuples">Each episode with its previous episodes.</param>
    /// <param name="edgeTypeMap">Which custom edge types may join which entity labels.</param>
    /// <param name="entityTypes">Custom entity types by name.</param>
    /// <param name="excludedEntityTypes">Entity type names not to extract.</param>
    /// <param name="edgeTypes">Custom edge types by name.</param>
    /// <param name="customExtractionInstructions">Extra instructions for the prompts.</param>
    /// <param name="useCombinedExtraction">One combined LLM call per episode, or separate entity and fact calls.</param>
    /// <param name="strictEdgeTypes">Keep only facts of a custom edge type.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The entities and the facts of each episode, in input order.</returns>
    public static Task<(IReadOnlyList<IReadOnlyList<EntityNode>> Nodes, IReadOnlyList<IReadOnlyList<EntityEdge>> Edges)>
        ExtractNodesAndEdgesBulkAsync(
            GraphitiClients clients,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            IReadOnlyList<string>? excludedEntityTypes = null,
            IReadOnlyDictionary<string, Type>? edgeTypes = null,
            string? customExtractionInstructions = null,
            bool useCombinedExtraction = true,
            bool strictEdgeTypes = false,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(episodeTuples);
        ArgumentNullException.ThrowIfNull(edgeTypeMap);
        return useCombinedExtraction
            ? ExtractNodesAndEdgesBulkCombinedAsync(clients, episodeTuples, edgeTypeMap, entityTypes,
                excludedEntityTypes, edgeTypes, strictEdgeTypes, customExtractionInstructions, cancellationToken)
            : ExtractNodesAndEdgesBulkSeparateAsync(clients, episodeTuples, edgeTypeMap, entityTypes,
                excludedEntityTypes, edgeTypes, strictEdgeTypes, customExtractionInstructions, cancellationToken);
    }

    // _extract_nodes_and_edges_bulk_combined: one LLM call per episode for both nodes and edges.
    private static async Task<(IReadOnlyList<IReadOnlyList<EntityNode>> Nodes, IReadOnlyList<IReadOnlyList<EntityEdge>> Edges)>
        ExtractNodesAndEdgesBulkCombinedAsync(
            GraphitiClients clients,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyList<string>? excludedEntityTypes,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            bool strictEdgeTypes,
            string? customExtractionInstructions,
            CancellationToken cancellationToken)
    {
        var results = await Concurrency.SemaphoreGatherAsync(
                episodeTuples.Select<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes),
                    Func<Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyList<EntityEdge> Edges,
                        IReadOnlyDictionary<string, IReadOnlyList<int>> NodeEpisodeIndexMap)>>>(tuple =>
                    () => CombinedExtractionOperations.ExtractNodesAndEdgesAsync(
                        clients,
                        tuple.Episode,
                        tuple.PreviousEpisodes,
                        entityTypes: entityTypes,
                        excludedEntityTypes: excludedEntityTypes,
                        edgeTypeMap: edgeTypeMap,
                        edgeTypes: edgeTypes,
                        strictEdgeTypes: strictEdgeTypes,
                        customExtractionInstructions: customExtractionInstructions,
                        cancellationToken: cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        return (results.Select(r => r.Nodes).ToList(), results.Select(r => r.Edges).ToList());
    }

    // _extract_nodes_and_edges_bulk_separate: extract_nodes for every episode, then extract_edges.
    private static async Task<(IReadOnlyList<IReadOnlyList<EntityNode>> Nodes, IReadOnlyList<IReadOnlyList<EntityEdge>> Edges)>
        ExtractNodesAndEdgesBulkSeparateAsync(
            GraphitiClients clients,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyList<string>? excludedEntityTypes,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            bool strictEdgeTypes,
            string? customExtractionInstructions,
            CancellationToken cancellationToken)
    {
        var extractedResults = await Concurrency.SemaphoreGatherAsync(
                episodeTuples.Select<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes),
                    Func<Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, IReadOnlyList<int>> Map)>>>(tuple =>
                    () => NodeOperations.ExtractNodesAsync(
                        clients,
                        tuple.Episode,
                        tuple.PreviousEpisodes,
                        entityTypes,
                        excludedEntityTypes,
                        customExtractionInstructions,
                        cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);
        var extractedNodesBulk = extractedResults.Select(r => r.Nodes.ToList()).ToList();

        var edgeResults = await Concurrency.SemaphoreGatherAsync(
                episodeTuples.Select<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes),
                    Func<Task<(IReadOnlyList<EntityEdge> Edges, IReadOnlyList<EntityNode> MaterializedNodes)>>>((tuple, i) =>
                    () => EdgeOperations.ExtractEdgesAsync(
                        clients,
                        tuple.Episode,
                        extractedNodesBulk[i],
                        tuple.PreviousEpisodes,
                        edgeTypeMap,
                        groupId: tuple.Episode.GroupId,
                        edgeTypes: edgeTypes,
                        strictEdgeTypes: strictEdgeTypes,
                        customExtractionInstructions: customExtractionInstructions,
                        cancellationToken: cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var extractedEdgesBulk = new List<IReadOnlyList<EntityEdge>>();
        for (var i = 0; i < edgeResults.Count; i++)
        {
            extractedEdgesBulk.Add(edgeResults[i].Edges);

            // As upstream: extract_edges has already appended the materialised endpoints to this list, and they are
            // appended once more here. Batch node deduplication drops the repeats.
            extractedNodesBulk[i].AddRange(edgeResults[i].MaterializedNodes);
        }

        return (extractedNodesBulk, extractedEdgesBulk);
    }

    /// <summary>
    /// Resolves the entities of a batch (upstream <c>dedupe_nodes_bulk</c>) in two passes: each episode's entities
    /// against the graph, as <see cref="NodeOperations.ResolveExtractedNodesAsync"/> does for a single episode, then
    /// the deterministic name heuristics across the union of the results, to catch duplicates that only occur within
    /// this batch.
    /// </summary>
    /// <param name="clients">The services.</param>
    /// <param name="extractedNodes">Each episode's extracted entities, in the order of
    /// <paramref name="episodeTuples"/>.</param>
    /// <param name="episodeTuples">Each episode with its previous episodes.</param>
    /// <param name="entityTypes">Custom entity types by name.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The canonical entities of each episode (by episode id, each entity once), and every id mapped to its
    /// canonical id, for re-pointing facts.</returns>
    public static Task<(IReadOnlyDictionary<string, IReadOnlyList<EntityNode>> NodesByEpisode,
            IReadOnlyDictionary<string, string> UuidMap)>
        DedupeNodesBulkAsync(
            GraphitiClients clients,
            IReadOnlyList<IReadOnlyList<EntityNode>> extractedNodes,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            CancellationToken cancellationToken = default) =>
        DedupeNodesBulkAsync(clients, extractedNodes, episodeTuples, entityTypes,
            (c, nodes, episode, previous, types, ct) =>
                NodeOperations.ResolveExtractedNodesAsync(c, nodes, episode, previous, types, cancellationToken: ct),
            cancellationToken);

    /// <summary>Resolves one episode's extracted entities: the shape of
    /// <see cref="NodeOperations.ResolveExtractedNodesAsync"/>. Tests substitute it.</summary>
    internal delegate Task<(IReadOnlyList<EntityNode> ResolvedNodes, IReadOnlyDictionary<string, string> UuidMap,
            IReadOnlyList<(EntityNode Extracted, EntityNode Resolved)> DuplicatePairs)>
        ExtractedNodesResolver(
            GraphitiClients clients,
            IReadOnlyList<EntityNode> extractedNodes,
            EpisodicNode episode,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes,
            CancellationToken cancellationToken);

    internal static async Task<(IReadOnlyDictionary<string, IReadOnlyList<EntityNode>> NodesByEpisode,
            IReadOnlyDictionary<string, string> UuidMap)>
        DedupeNodesBulkAsync(
            GraphitiClients clients,
            IReadOnlyList<IReadOnlyList<EntityNode>> extractedNodes,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
            IReadOnlyDictionary<string, Type>? entityTypes,
            ExtractedNodesResolver resolveExtractedNodes,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(extractedNodes);
        ArgumentNullException.ThrowIfNull(episodeTuples);
        if (extractedNodes.Count != episodeTuples.Count)
        {
            throw new ArgumentException("There must be one list of extracted nodes per episode.", nameof(extractedNodes));
        }

        var firstPassResults = await Concurrency.SemaphoreGatherAsync(
                extractedNodes.Select<IReadOnlyList<EntityNode>, Func<Task<(IReadOnlyList<EntityNode> ResolvedNodes,
                    IReadOnlyDictionary<string, string> UuidMap,
                    IReadOnlyList<(EntityNode Extracted, EntityNode Resolved)> DuplicatePairs)>>>((nodes, i) =>
                    () => resolveExtractedNodes(clients, nodes, episodeTuples[i].Episode,
                        episodeTuples[i].PreviousEpisodes, entityTypes, cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var episodeResolutions = new List<(string EpisodeUuid, IReadOnlyList<EntityNode> ResolvedNodes)>();
        var perEpisodeUuidMaps = new List<IReadOnlyDictionary<string, string>>();
        var duplicatePairs = new List<(string, string)>();
        for (var i = 0; i < firstPassResults.Count; i++)
        {
            var (resolvedNodes, uuidMap, duplicates) = firstPassResults[i];
            episodeResolutions.Add((episodeTuples[i].Episode.Uuid, resolvedNodes));
            perEpisodeUuidMaps.Add(uuidMap);
            duplicatePairs.AddRange(duplicates.Select(d => (d.Extracted.Uuid, d.Resolved.Uuid)));
        }

        // Second pass: the deterministic heuristics across the batch. Rebuilding the index for every node is
        // quadratic, as upstream; batches are small (CHUNK_SIZE).
        var canonicalNodes = new OrderedDictionary<string, EntityNode>(StringComparer.Ordinal);
        foreach (var (_, resolvedNodes) in episodeResolutions)
        {
            foreach (var node in resolvedNodes)
            {
                if (canonicalNodes.Count == 0)
                {
                    canonicalNodes[node.Uuid] = node;
                    continue;
                }

                var existingCandidates = canonicalNodes.Values.ToList();
                var normalized = DedupHelpers.NormalizeStringExact(node.Name);
                var exactMatch = existingCandidates.FirstOrDefault(
                    candidate => DedupHelpers.NormalizeStringExact(candidate.Name) == normalized);
                if (exactMatch is not null)
                {
                    if (exactMatch.Uuid != node.Uuid)
                    {
                        duplicatePairs.Add((node.Uuid, exactMatch.Uuid));
                    }

                    continue;
                }

                var indexes = DedupHelpers.BuildCandidateIndexes(existingCandidates);
                var state = new DedupResolutionState(1);
                DedupHelpers.ResolveWithSimilarity([node], indexes, state);

                var resolved = state.ResolvedNodes[0];
                if (resolved is null)
                {
                    canonicalNodes[node.Uuid] = node;
                    continue;
                }

                canonicalNodes.TryAdd(resolved.Uuid, resolved);
                if (resolved.Uuid != node.Uuid)
                {
                    duplicatePairs.Add((node.Uuid, resolved.Uuid));
                }
            }
        }

        var unionPairs = new List<(string, string)>();
        foreach (var uuidMap in perEpisodeUuidMaps)
        {
            unionPairs.AddRange(uuidMap.Select(e => (e.Key, e.Value)));
        }

        unionPairs.AddRange(duplicatePairs);
        var compressedMap = BuildDirectedUuidMap(unionPairs);

        var nodesByEpisode = new Dictionary<string, IReadOnlyList<EntityNode>>(StringComparer.Ordinal);
        foreach (var (episodeUuid, resolvedNodes) in episodeResolutions)
        {
            var dedupedNodes = new List<EntityNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in resolvedNodes)
            {
                var canonicalUuid = compressedMap.GetValueOrDefault(node.Uuid, node.Uuid);
                if (!seen.Add(canonicalUuid))
                {
                    continue;
                }

                if (!canonicalNodes.TryGetValue(canonicalUuid, out var canonicalNode))
                {
                    Log.CanonicalNodeMissing(clients.Logger, canonicalUuid, node.Uuid);
                    canonicalNode = node;
                }

                dedupedNodes.Add(canonicalNode);
            }

            nodesByEpisode[episodeUuid] = dedupedNodes;
        }

        return (nodesByEpisode, compressedMap);
    }

    /// <summary>
    /// Resolves the facts of a batch against each other (upstream <c>dedupe_edges_bulk</c>).
    /// </summary>
    /// <remarks>
    /// <para>Every fact is embedded, then compared with every other fact of the batch (its own episode's included)
    /// between the same source and target: one that shares a lowercased word with it, or whose embedding has a dot
    /// product of at least 0.6 with its own after L2 normalisation, is a candidate. Each fact is resolved against its
    /// candidates with <see cref="EdgeOperations.ResolveExtractedEdgeAsync"/>, and duplicates are merged into the fact
    /// with the smallest id, which gains their episodes.</para>
    /// <para>Invalidations found here are not applied, as upstream; the facts are resolved against the graph
    /// afterwards.</para>
    /// </remarks>
    /// <param name="clients">The services.</param>
    /// <param name="extractedEdges">Each episode's facts, in the order of <paramref name="episodeTuples"/>.</param>
    /// <param name="episodeTuples">Each episode with its previous episodes.</param>
    /// <param name="entities">Unused, as upstream.</param>
    /// <param name="edgeTypes">Custom edge types by name, for attribute extraction.</param>
    /// <param name="edgeTypeMap">Unused, as upstream.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>Each episode's facts (by episode id), duplicates replaced by their canonical fact.</returns>
    public static Task<IReadOnlyDictionary<string, IReadOnlyList<EntityEdge>>> DedupeEdgesBulkAsync(
        GraphitiClients clients,
        IReadOnlyList<IReadOnlyList<EntityEdge>> extractedEdges,
        IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
        IReadOnlyList<EntityNode> entities,
        IReadOnlyDictionary<string, Type> edgeTypes,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        return DedupeEdgesBulkAsync(clients, extractedEdges, episodeTuples, edgeTypes,
            (edges, ct) => EdgeOperations.CreateEntityEdgeEmbeddingsAsync(clients.Embedder, edges, ct),
            async (c, edge, related, existing, episode, types, ct) =>
            {
                var (resolved, invalidated, duplicates) = await EdgeOperations.ResolveExtractedEdgeAsync(
                    c, edge, related, existing, episode, types, ct).ConfigureAwait(false);
                return (resolved, invalidated, duplicates);
            },
            cancellationToken);
    }

    /// <summary>Resolves one fact against candidates: the shape of
    /// <see cref="EdgeOperations.ResolveExtractedEdgeAsync"/>. Tests substitute it.</summary>
    internal delegate Task<(EntityEdge Resolved, IReadOnlyList<EntityEdge> Invalidated, IReadOnlyList<EntityEdge> Duplicates)>
        ExtractedEdgeResolver(
            GraphitiClients clients,
            EntityEdge extractedEdge,
            IReadOnlyList<EntityEdge> relatedEdges,
            IReadOnlyList<EntityEdge> existingEdges,
            EpisodicNode episode,
            IReadOnlyDictionary<string, Type>? edgeTypeCandidates,
            CancellationToken cancellationToken);

    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<EntityEdge>>> DedupeEdgesBulkAsync(
        GraphitiClients clients,
        IReadOnlyList<IReadOnlyList<EntityEdge>> extractedEdges,
        IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeTuples,
        IReadOnlyDictionary<string, Type> edgeTypes,
        Func<IReadOnlyList<EntityEdge>, CancellationToken, Task> createEmbeddings,
        ExtractedEdgeResolver resolveExtractedEdge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(extractedEdges);
        ArgumentNullException.ThrowIfNull(episodeTuples);
        ArgumentNullException.ThrowIfNull(edgeTypes);
        const double minScore = 0.6;

        await Concurrency.SemaphoreGatherAsync(
                extractedEdges.Select<IReadOnlyList<EntityEdge>, Func<Task>>(edges =>
                    () => createEmbeddings(edges, cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var allEdges = extractedEdges.SelectMany(edges => edges).ToList();
        var dedupeTuples = new List<(EpisodicNode Episode, EntityEdge Edge, List<EntityEdge> Candidates)>();
        for (var i = 0; i < extractedEdges.Count; i++)
        {
            foreach (var edge in extractedEdges[i])
            {
                var candidates = new List<EntityEdge>();
                var edgeWords = Words(edge.Fact);
                foreach (var existingEdge in allEdges)
                {
                    if (edge.Uuid == existingEdge.Uuid)
                    {
                        continue;
                    }

                    // Word overlap approximates BM25 and casts a wider net, which suits deduplication.
                    if (edge.SourceNodeUuid != existingEdge.SourceNodeUuid
                        || edge.TargetNodeUuid != existingEdge.TargetNodeUuid)
                    {
                        continue;
                    }

                    if (edgeWords.Overlaps(Words(existingEdge.Fact)))
                    {
                        candidates.Add(existingEdge);
                        continue;
                    }

                    var similarity = SearchUtils.Dot(
                        SearchUtils.NormalizeL2(edge.FactEmbedding ?? []),
                        SearchUtils.NormalizeL2(existingEdge.FactEmbedding ?? []));
                    if (similarity >= minScore)
                    {
                        candidates.Add(existingEdge);
                    }
                }

                dedupeTuples.Add((episodeTuples[i].Episode, edge, candidates));
            }
        }

        var bulkEdgeResolutions = await Concurrency.SemaphoreGatherAsync(
                dedupeTuples.Select<(EpisodicNode Episode, EntityEdge Edge, List<EntityEdge> Candidates),
                    Func<Task<(EntityEdge Resolved, IReadOnlyList<EntityEdge> Invalidated,
                        IReadOnlyList<EntityEdge> Duplicates)>>>(tuple =>
                    () => resolveExtractedEdge(clients, tuple.Edge, tuple.Candidates, tuple.Candidates, tuple.Episode,
                        edgeTypes, cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        // Invalidations are not tracked here.
        var duplicatePairs = new List<(string, string)>();
        for (var i = 0; i < bulkEdgeResolutions.Count; i++)
        {
            var edge = dedupeTuples[i].Edge;
            duplicatePairs.AddRange(bulkEdgeResolutions[i].Duplicates.Select(duplicate => (edge.Uuid, duplicate.Uuid)));
        }

        // 3 -> 2 and 2 -> 1 become 3 -> 1 (the smallest id).
        var compressedMap = CompressUuidMap(duplicatePairs);

        var edgeUuidMap = new Dictionary<string, EntityEdge>(StringComparer.Ordinal);
        foreach (var edge in allEdges)
        {
            edgeUuidMap[edge.Uuid] = edge;
        }

        foreach (var edge in allEdges)
        {
            var canonicalUuid = compressedMap.GetValueOrDefault(edge.Uuid, edge.Uuid);
            if (canonicalUuid == edge.Uuid)
            {
                continue;
            }

            var canonicalEdge = edgeUuidMap[canonicalUuid];
            foreach (var episodeUuid in edge.Episodes)
            {
                if (!canonicalEdge.Episodes.Contains(episodeUuid))
                {
                    canonicalEdge.Episodes.Add(episodeUuid);
                }
            }
        }

        var edgesByEpisode = new Dictionary<string, IReadOnlyList<EntityEdge>>(StringComparer.Ordinal);
        for (var i = 0; i < extractedEdges.Count; i++)
        {
            edgesByEpisode[episodeTuples[i].Episode.Uuid] = extractedEdges[i]
                .Select(edge => edgeUuidMap[compressedMap.GetValueOrDefault(edge.Uuid, edge.Uuid)])
                .ToList();
        }

        return edgesByEpisode;
    }

    // set(text.lower().split()): the distinct whitespace-separated words, lowercased.
    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error,
            Message = "Canonical node {CanonicalUuid} missing during batch dedupe; falling back to {NodeUuid}")]
        public static partial void CanonicalNodeMissing(ILogger logger, string canonicalUuid, string nodeUuid);
    }

    /// <summary>Union-find that roots each set at its lexicographically smallest id (upstream <c>UnionFind</c>).</summary>
    internal sealed class UnionFind
    {
        private readonly Dictionary<string, string> _parent;

        public UnionFind(IEnumerable<string> elements) =>
            _parent = elements.ToDictionary(e => e, e => e, StringComparer.Ordinal);

        public string Find(string x)
        {
            var parent = _parent[x];
            if (parent != x)
            {
                parent = Find(parent);
                _parent[x] = parent;
            }

            return parent;
        }

        public void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
            {
                return;
            }

            // Attach the lexicographically larger root under the smaller, comparing code points as Python does.
            if (PythonText.CodePointOrder.Compare(ra, rb) < 0)
            {
                _parent[rb] = ra;
            }
            else
            {
                _parent[ra] = rb;
            }
        }
    }
}
