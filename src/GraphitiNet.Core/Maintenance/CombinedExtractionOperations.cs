// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/combined_extraction.py (Graphiti v0.31.2) and changed for .NET.

using System.Diagnostics;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Utils;
using Microsoft.Extensions.Logging;

namespace GraphitiNet.Maintenance;

/// <summary>
/// Extracts entities and the facts between them with one LLM call (upstream <c>combined_extraction</c>), the default
/// extraction of <see cref="Graphiti.AddEpisodeBulkAsync"/>.
/// </summary>
/// <remarks>
/// Seeing both tasks at once lets the model give every entity at least one fact; entities left without a fact are
/// dropped. The facts are then dated in batches of <see cref="TimestampBatchSize"/> with the
/// <c>extract_edges.extract_timestamps_batch</c> prompt.
/// </remarks>
public static partial class CombinedExtractionOperations
{
    /// <summary>Upstream <c>TIMESTAMP_BATCH_SIZE</c>: facts dated per LLM call.</summary>
    public const int TimestampBatchSize = 20;

    /// <summary>Upstream <c>TIMESTAMP_BATCH_ATTEMPTS</c>: tries per batch before its facts stay undated.</summary>
    public const int TimestampBatchAttempts = 2;

    /// <summary>Extracts from one episode. See the overload taking several episodes.</summary>
    public static Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyList<EntityEdge> Edges,
            IReadOnlyDictionary<string, IReadOnlyList<int>> NodeEpisodeIndexMap)>
        ExtractNodesAndEdgesAsync(
            GraphitiClients clients,
            EpisodicNode episode,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            IReadOnlyList<string>? excludedEntityTypes = null,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>>? edgeTypeMap = null,
            IReadOnlyDictionary<string, Type>? edgeTypes = null,
            bool strictEdgeTypes = false,
            string? customExtractionInstructions = null,
            IReadOnlyList<EpisodicNode>? timeline = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return ExtractNodesAndEdgesAsync(clients, [episode], previousEpisodes, entityTypes, excludedEntityTypes,
            edgeTypeMap, edgeTypes, strictEdgeTypes, customExtractionInstructions, timeline, cancellationToken);
    }

    /// <summary>
    /// Extracts entities and facts from one or more episodes in a single <c>extract_nodes_and_edges.extract_message</c>
    /// call (upstream <c>extract_nodes_and_edges</c>).
    /// </summary>
    /// <remarks>
    /// <para>A fact naming an endpoint that is not among the extracted entities keeps it: the endpoint becomes a new
    /// entity, unless it was extracted with an excluded type, in which case the fact is dropped.</para>
    /// <para>Each entity is attributed to the episodes of the facts it takes part in; entities without facts are
    /// dropped.</para>
    /// </remarks>
    /// <param name="clients">The services.</param>
    /// <param name="episodes">The episodes, at least one; the first is the primary episode (group, fallback
    /// reference time).</param>
    /// <param name="previousEpisodes">Earlier episodes, as context; nothing is extracted from them.</param>
    /// <param name="entityTypes">Custom entity types by name.</param>
    /// <param name="excludedEntityTypes">Type names (including <c>Entity</c>) not to extract.</param>
    /// <param name="edgeTypeMap">Which custom edge types may join which entity labels.</param>
    /// <param name="edgeTypes">Custom edge types by name.</param>
    /// <param name="strictEdgeTypes">Keep only facts of a custom edge type.</param>
    /// <param name="customExtractionInstructions">Extra instructions for the prompt.</param>
    /// <param name="timeline">The episodes in order with context-only turns between them, or
    /// <see langword="null"/> to render the episodes alone.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The entities, the facts, and each entity's 0-based episode positions.</returns>
    public static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyList<EntityEdge> Edges,
            IReadOnlyDictionary<string, IReadOnlyList<int>> NodeEpisodeIndexMap)>
        ExtractNodesAndEdgesAsync(
            GraphitiClients clients,
            IReadOnlyList<EpisodicNode> episodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            IReadOnlyList<string>? excludedEntityTypes = null,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>>? edgeTypeMap = null,
            IReadOnlyDictionary<string, Type>? edgeTypes = null,
            bool strictEdgeTypes = false,
            string? customExtractionInstructions = null,
            IReadOnlyList<EpisodicNode>? timeline = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(previousEpisodes);
        ArgumentOutOfRangeException.ThrowIfZero(episodes.Count, nameof(episodes));
        var primaryEpisode = episodes[0];
        var logger = clients.Logger;
        var start = Stopwatch.GetTimestamp();

        var entityTypesContext = NodeOperations.FilterEntityTypesContext(
            NodeOperations.BuildEntityTypesContext(entityTypes), excludedEntityTypes);
        if (entityTypesContext.Count == 0)
        {
            Log.NoEntityTypes(logger);
            return ([], [], new Dictionary<string, IReadOnlyList<int>>());
        }

        // Edge types context, in the same shape as the separate extraction path.
        var edgeTypesContext = new List<Dictionary<string, object?>>();
        if (edgeTypes is { Count: > 0 } && edgeTypeMap is { Count: > 0 })
        {
            var edgeTypeSignaturesMap = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            foreach (var (signature, typeNames) in edgeTypeMap)
            {
                foreach (var typeName in typeNames)
                {
                    if (!edgeTypeSignaturesMap.TryGetValue(typeName, out var signatures))
                    {
                        edgeTypeSignaturesMap[typeName] = signatures = [];
                    }

                    signatures.Add([signature.Source, signature.Target]);
                }
            }

            foreach (var (typeName, typeModel) in edgeTypes)
            {
                edgeTypesContext.Add(new Dictionary<string, object?>
                {
                    ["fact_type_name"] = typeName,
                    ["fact_type_signatures"] = edgeTypeSignaturesMap.TryGetValue(typeName, out var signatures)
                        ? signatures
                        : new List<string[]> { new[] { DedupHelpers.EntityBaseLabel, DedupHelpers.EntityBaseLabel } },
                    ["fact_type_description"] = EntityTypesUtils.GetDescription(typeModel),
                });
            }
        }

        if (strictEdgeTypes && edgeTypesContext.Count == 0)
        {
            Log.NoEdgeTypesForStrict(logger);
            return ([], [], new Dictionary<string, IReadOnlyList<int>>());
        }

        var allowedEdgeTypeNames = strictEdgeTypes
            ? (edgeTypes?.Keys ?? []).ToHashSet(StringComparer.Ordinal)
            : null;

        var context = new Dictionary<string, object?>
        {
            ["episode_content"] = timeline is { Count: > 0 }
                ? TextUtils.ConcatenateTimeline(episodes, timeline)
                : TextUtils.ConcatenateEpisodes(episodes),
            ["previous_episodes"] = previousEpisodes.Select(ep => new Dictionary<string, object?>
            {
                ["content"] = ep.Content,
                ["timestamp"] = PythonText.IsoFormat(ep.ValidAt),
            }).ToList(),
            ["custom_extraction_instructions"] = customExtractionInstructions ?? "",
            ["entity_types"] = entityTypesContext,
            ["edge_types"] = edgeTypesContext,
            ["strict_edge_types"] = strictEdgeTypes,
        };

        var response = await clients.CompletePromptAsync<CombinedExtraction>(
            PromptNames.ExtractNodesAndEdgesExtractMessage,
            context,
            groupId: primaryEpisode.GroupId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Log.Extracted(logger, response.Entities.Count, response.Edges.Count, elapsedMs);

        // --- Nodes ---
        var extractedNodes = new List<EntityNode>();
        var excludedEntityNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in response.Entities.Where(e => e.Name.Trim().Length > 0))
        {
            var typeId = entity.EntityTypeId;
            var typeName = typeId >= 0 && typeId < entityTypesContext.Count
                ? PythonText.Str(entityTypesContext[typeId].GetValueOrDefault("entity_type_name"))
                : DedupHelpers.EntityBaseLabel;

            if (excludedEntityTypes is { Count: > 0 } && excludedEntityTypes.Contains(typeName))
            {
                excludedEntityNames.Add(DedupHelpers.NormalizeStringExact(entity.Name));
                Log.ExcludingEntity(logger, typeName);
                continue;
            }

            // Upstream builds the labels from a set; here Entity always comes first.
            List<string> labels = typeName == DedupHelpers.EntityBaseLabel
                ? [DedupHelpers.EntityBaseLabel]
                : [DedupHelpers.EntityBaseLabel, typeName];
            extractedNodes.Add(new EntityNode
            {
                Name = entity.Name,
                GroupId = primaryEpisode.GroupId,
                Labels = labels,
                Summary = "",
                CreatedAt = DateTimeUtils.UtcNow(),
            });
        }

        // Collapse exact same-name nodes; episode attribution comes from the facts below.
        var nodeEpisodeIndexMap = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        extractedNodes = NodeOperations.CollapseExactDuplicateExtractedNodes(extractedNodes, nodeEpisodeIndexMap);

        // --- Edges ---
        var nameToNode = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        foreach (var node in extractedNodes)
        {
            nameToNode[DedupHelpers.NormalizeStringExact(node.Name)] = node;
        }

        var extractedEdges = new List<EntityEdge>();
        var materializedEndpointUuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edgeData in response.Edges)
        {
            if (edgeData.Fact.Trim().Length == 0)
            {
                Log.EmptyFact(logger);
                continue;
            }

            var edgeEpisodeUuids = EpisodeUuidsForFact(edgeData, episodes);
            var edgeReferenceTime =
                edgeData.EpisodeIndices.Count > 0 && edgeData.EpisodeIndices[0] >= 0
                                                  && edgeData.EpisodeIndices[0] < episodes.Count
                    ? episodes[edgeData.EpisodeIndices[0]].ValidAt
                    : primaryEpisode.ValidAt;

            var sourceName = edgeData.SourceEntityName.Trim();
            var targetName = edgeData.TargetEntityName.Trim();
            if (sourceName.Length == 0 || targetName.Length == 0)
            {
                Log.EmptyEndpointName(logger);
                continue;
            }

            if (allowedEdgeTypeNames is not null && !allowedEdgeTypeNames.Contains(edgeData.RelationType))
            {
                Log.NotInStrictOntology(logger, edgeData.RelationType);
                continue;
            }

            var sourceKey = DedupHelpers.NormalizeStringExact(sourceName);
            var targetKey = DedupHelpers.NormalizeStringExact(targetName);
            if (excludedEntityNames.Contains(sourceKey) || excludedEntityNames.Contains(targetKey))
            {
                Log.ExcludedEndpoint(logger);
                continue;
            }

            var sourceNode = ResolveEndpoint(sourceName, sourceKey);
            var targetNode = ResolveEndpoint(targetName, targetKey);

            extractedEdges.Add(new EntityEdge
            {
                SourceNodeUuid = sourceNode.Uuid,
                TargetNodeUuid = targetNode.Uuid,
                Name = edgeData.RelationType,
                GroupId = primaryEpisode.GroupId,
                Fact = edgeData.Fact,
                Episodes = edgeEpisodeUuids,
                CreatedAt = DateTimeUtils.UtcNow(),
                ReferenceTime = edgeReferenceTime,
            });
        }

        if (extractedEdges.Count > 0)
        {
            await DateFactsAsync(clients, extractedEdges, cancellationToken).ConfigureAwait(false);
        }

        // --- Node attribution from facts; nodes without facts are dropped ---
        var episodeUuidToIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < episodes.Count; i++)
        {
            episodeUuidToIdx[episodes[i].Uuid] = i;
        }

        var connectedNodeUuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in extractedEdges)
        {
            connectedNodeUuids.Add(edge.SourceNodeUuid);
            connectedNodeUuids.Add(edge.TargetNodeUuid);
        }

        var orphanCount = extractedNodes.Count(n => !connectedNodeUuids.Contains(n.Uuid));
        if (orphanCount > 0)
        {
            Log.DroppingOrphans(logger, orphanCount);
        }

        extractedNodes = extractedNodes.Where(n => connectedNodeUuids.Contains(n.Uuid)).ToList();
        var survivingMaterialized = materializedEndpointUuids.Count(connectedNodeUuids.Contains);
        if (survivingMaterialized > 0)
        {
            Log.MaterializedEndpoints(logger, survivingMaterialized, primaryEpisode.GroupId);
        }

        foreach (var edge in extractedEdges)
        {
            foreach (var nodeUuid in new[] { edge.SourceNodeUuid, edge.TargetNodeUuid })
            {
                var positions = edge.Episodes.Where(episodeUuidToIdx.ContainsKey).Select(u => episodeUuidToIdx[u]);
                var existing = nodeEpisodeIndexMap.GetValueOrDefault(nodeUuid) ?? [];
                nodeEpisodeIndexMap[nodeUuid] = [.. existing.Concat(positions).Distinct().Order()];
            }
        }

        Log.CombinedExtractionFinal(logger, extractedNodes.Count, extractedEdges.Count, response.Edges.Count);

        return (extractedNodes, extractedEdges, nodeEpisodeIndexMap.ToDictionary(
            e => e.Key, e => (IReadOnlyList<int>)e.Value, StringComparer.Ordinal));

        EntityNode ResolveEndpoint(string name, string key)
        {
            if (nameToNode.TryGetValue(key, out var node))
            {
                return node;
            }

            var before = extractedNodes.Count;
            node = DanglingEndpoints.MaterializeDanglingEndpointNode(name, primaryEpisode.GroupId, nameToNode,
                extractedNodes);
            if (extractedNodes.Count > before)
            {
                materializedEndpointUuids.Add(node.Uuid);
            }

            return node;
        }
    }

    // _episode_uuids_for_fact: the fact's episodes by index; every episode when none is in range.
    private static List<string> EpisodeUuidsForFact(CombinedFact edgeData, IReadOnlyList<EpisodicNode> episodes)
    {
        var uuids = edgeData.EpisodeIndices.Where(idx => idx >= 0 && idx < episodes.Count)
            .Select(idx => episodes[idx].Uuid)
            .ToList();
        return uuids.Count > 0 ? uuids : episodes.Select(ep => ep.Uuid).ToList();
    }

    // Dates the facts in groups of TimestampBatchSize, concurrently. A group whose calls all fail stays undated.
    private static async Task DateFactsAsync(
        GraphitiClients clients,
        List<EntityEdge> edges,
        CancellationToken cancellationToken)
    {
        var groups = edges.Chunk(TimestampBatchSize).ToList();
        var results = await Concurrency.SemaphoreGatherAsync(
                groups.Select<EntityEdge[], Func<Task<BatchEdgeTimestamps?>>>(group =>
                    () => ExtractGroupTimestampsAsync(clients, group, cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var batchResult = results[g];
            if (batchResult is null)
            {
                continue;
            }

            if (batchResult.Timestamps.Count != group.Length)
            {
                Log.TimestampCountMismatch(clients.Logger, batchResult.Timestamps.Count, group.Length);
            }

            for (var i = 0; i < Math.Min(group.Length, batchResult.Timestamps.Count); i++)
            {
                var timestamps = batchResult.Timestamps[i];
                if (timestamps is not null)
                {
                    TemporalEdgeUtils.ApplyExtractedTimestamps(
                        group[i], timestamps.ValidAt, timestamps.InvalidAt, group[i].ReferenceTime);
                }
            }
        }
    }

    private static async Task<BatchEdgeTimestamps?> ExtractGroupTimestampsAsync(
        GraphitiClients clients,
        EntityEdge[] group,
        CancellationToken cancellationToken)
    {
        var factsWithReference = group.Select(edge => new Dictionary<string, object?>
        {
            ["fact"] = edge.Fact,
            ["reference_time"] = edge.ReferenceTime is { } reference ? PythonText.IsoFormat(reference) : "unknown",
        }).ToList();

        for (var attempt = 1; attempt <= TimestampBatchAttempts; attempt++)
        {
            try
            {
                // The prompt is built again for each attempt, as upstream.
                return await clients.CompletePromptAsync<BatchEdgeTimestamps>(
                    PromptNames.ExtractEdgesExtractTimestampsBatch,
                    new Dictionary<string, object?> { ["facts"] = factsWithReference },
                    modelSize: ModelSize.Small,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.TimestampBatchFailed(clients.Logger, e, group.Length, attempt, TimestampBatchAttempts);
            }
        }

        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "No entity types available for combined extraction after exclusions")]
        public static partial void NoEntityTypes(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "No edge types available for strict combined extraction")]
        public static partial void NoEdgeTypesForStrict(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Combined extraction: {Entities} entities, {Edges} edges in {Milliseconds:F0} ms")]
        public static partial void Extracted(ILogger logger, int entities, int edges, double milliseconds);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Excluding entity of type \"{TypeName}\"")]
        public static partial void ExcludingEntity(ILogger logger, string typeName);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping edge with empty fact")]
        public static partial void EmptyFact(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping edge with empty source or target entity name")]
        public static partial void EmptyEndpointName(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Skipping edge with relation type \"{RelationType}\" not in strict edge ontology")]
        public static partial void NotInStrictOntology(ILogger logger, string relationType);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping edge with excluded entity endpoint")]
        public static partial void ExcludedEndpoint(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Failed to extract batch timestamps for {Count} facts (attempt {Attempt} of {Attempts})")]
        public static partial void TimestampBatchFailed(ILogger logger, Exception exception, int count, int attempt, int attempts);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Batch timestamp count mismatch: got {Got} timestamps for {Expected} facts")]
        public static partial void TimestampCountMismatch(ILogger logger, int got, int expected);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Dropping {Count} orphan node(s) with no connecting edges")]
        public static partial void DroppingOrphans(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Materialized {Count} dangling edge endpoint node(s) for group_id={GroupId}")]
        public static partial void MaterializedEndpoints(ILogger logger, int count, string groupId);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Combined extraction final: {Nodes} nodes, {Edges} edges (from {Raw} raw)")]
        public static partial void CombinedExtractionFinal(ILogger logger, int nodes, int edges, int raw);
    }
}
