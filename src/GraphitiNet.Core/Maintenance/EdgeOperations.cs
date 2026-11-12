// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/edge_operations.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Search;
using GraphitiNet.Utils;
using Microsoft.Extensions.Logging;

namespace GraphitiNet.Maintenance;

/// <summary>
/// The fact (entity edge) side of ingestion: extracting facts between entities, resolving them against the graph
/// (duplicates and contradictions), dating them, and invalidating the facts they supersede.
/// </summary>
/// <remarks>
/// <para><b>Custom edge types.</b> Upstream's <c>dict[str, type[BaseModel]]</c> is an
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> of type name to a C# class or record. The class's
/// <see cref="DescriptionAttribute"/> plays the role of the Pydantic docstring (the <c>fact_type_description</c> the
/// LLM sees), and its properties are the attributes the LLM fills in, named by <see cref="System.Text.Json"/> with
/// snake_case (or <c>[JsonPropertyName]</c>). Extracted attributes land in <see cref="EntityEdge.Attributes"/> as
/// JSON-shaped values (string, long, double, bool, null, lists and dictionaries).</para>
/// <para><b>Edge type map.</b> Upstream's <c>dict[tuple[str, str], list[str]]</c> is an
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> keyed by <c>(Source, Target)</c> entity labels, listing the edge
/// type names allowed between them. Iteration order is significant (it orders the signatures shown to the LLM), so
/// pass an insertion-ordered dictionary.</para>
/// <para><b>LLM calls.</b> Every call goes through <see cref="GraphitiClients.CompletePromptAsync"/>, so prompt
/// overrides and prompt routing apply. Upstream's per-edge helpers take an <c>llm_client</c> plus optional
/// <c>clients</c>; here they take <see cref="GraphitiClients"/> first, which covers both.</para>
/// <para><b>Logging.</b> Messages go to <see cref="LLMClient.Logger"/> of <see cref="GraphitiClients.LlmClient"/>.</para>
/// </remarks>
public static partial class EdgeOperations
{
    /// <summary>Upstream <c>DEFAULT_EDGE_NAME</c>: the name a fact gets when its custom type does not fit.</summary>
    public const string DefaultEdgeName = "RELATES_TO";

    /// <summary>The output token cap of the fact extraction call (upstream <c>extract_edges_max_tokens</c>).</summary>
    public const int ExtractEdgesMaxTokens = 16384;

    /// <summary>The name of the edge marking one entity as a duplicate of another.</summary>
    public const string IsDuplicateOfEdgeName = "IS_DUPLICATE_OF";

    /// <summary>
    /// Builds episodic (MENTIONED_IN) edges from one episode to each entity (upstream <c>build_episodic_edges</c>).
    /// </summary>
    public static IReadOnlyList<EpisodicEdge> BuildEpisodicEdges(
        IReadOnlyList<EntityNode> entityNodes,
        string episodeUuid,
        DateTimeOffset createdAt) =>
        BuildEpisodicEdges(entityNodes, [episodeUuid], createdAt);

    /// <summary>
    /// Builds episodic (MENTIONED_IN) edges between episodes and entities (upstream <c>build_episodic_edges</c>).
    /// </summary>
    /// <param name="entityNodes">The entities to link.</param>
    /// <param name="episodeUuids">The episodes' ids.</param>
    /// <param name="createdAt">The edges' creation time.</param>
    /// <param name="nodeEpisodeIndexMap">Optional entity id → 0-based positions in <paramref name="episodeUuids"/>.
    /// An entity listed here is linked only to those episodes (out-of-range positions are skipped); every other
    /// entity is linked to every episode.</param>
    /// <returns>The edges, entity by entity.</returns>
    public static IReadOnlyList<EpisodicEdge> BuildEpisodicEdges(
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<string> episodeUuids,
        DateTimeOffset createdAt,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? nodeEpisodeIndexMap = null)
    {
        ArgumentNullException.ThrowIfNull(entityNodes);
        ArgumentNullException.ThrowIfNull(episodeUuids);
        var episodicEdges = new List<EpisodicEdge>();
        foreach (var node in entityNodes)
        {
            IEnumerable<int> indices =
                nodeEpisodeIndexMap is { Count: > 0 } && nodeEpisodeIndexMap.TryGetValue(node.Uuid, out var mapped)
                    ? mapped
                    : Enumerable.Range(0, episodeUuids.Count);

            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < episodeUuids.Count)
                {
                    episodicEdges.Add(new EpisodicEdge
                    {
                        SourceNodeUuid = episodeUuids[idx],
                        TargetNodeUuid = node.Uuid,
                        CreatedAt = createdAt,
                        GroupId = node.GroupId,
                    });
                }
            }
        }

        return episodicEdges;
    }

    /// <summary>Builds membership edges from a community to each entity (upstream <c>build_community_edges</c>).</summary>
    public static IReadOnlyList<CommunityEdge> BuildCommunityEdges(
        IReadOnlyList<EntityNode> entityNodes,
        CommunityNode communityNode,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(entityNodes);
        ArgumentNullException.ThrowIfNull(communityNode);
        return entityNodes.Select(node => new CommunityEdge
        {
            SourceNodeUuid = communityNode.Uuid,
            TargetNodeUuid = node.Uuid,
            CreatedAt = createdAt,
            GroupId = communityNode.GroupId,
        }).ToList();
    }

    /// <summary>Extracts facts from one episode. See the overload taking several episodes.</summary>
    [SuppressMessage("Design", "CA1002", Justification = "Dangling endpoints are added to and pruned from the caller's list, as upstream.")]
    public static Task<(IReadOnlyList<EntityEdge> Edges, IReadOnlyList<EntityNode> MaterializedNodes)> ExtractEdgesAsync(
        GraphitiClients clients,
        EpisodicNode episode,
        List<EntityNode> nodes,
        IReadOnlyList<EpisodicNode> previousEpisodes,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
        string groupId = "",
        IReadOnlyDictionary<string, Type>? edgeTypes = null,
        bool strictEdgeTypes = false,
        string? customExtractionInstructions = null,
        CancellationToken cancellationToken = default) =>
        ExtractEdgesAsync(clients, [episode], nodes, previousEpisodes, edgeTypeMap, groupId, edgeTypes,
            strictEdgeTypes, customExtractionInstructions, cancellationToken);

    /// <summary>
    /// Extracts facts between <paramref name="nodes"/> from one or more episodes with the <c>extract_edges.edge</c>
    /// prompt (upstream <c>extract_edges</c>).
    /// </summary>
    /// <remarks>
    /// <para>Several episodes are concatenated with <c>[Episode N]</c> headers and the LLM attributes each fact to
    /// them; each fact is linked to its episodes (all of them when it names none in range).</para>
    /// <para>A fact naming an endpoint that is not in <paramref name="nodes"/> keeps that endpoint: a new entity is
    /// materialised and appended to <paramref name="nodes"/>. Materialised entities whose facts are all filtered out
    /// are removed from <paramref name="nodes"/> again.</para>
    /// <para>With <paramref name="strictEdgeTypes"/>, only facts named after one of <paramref name="edgeTypes"/> are
    /// kept, and nothing is extracted when there are no edge types.</para>
    /// </remarks>
    /// <param name="clients">The services.</param>
    /// <param name="episodes">The episodes, at least one; the first is the primary episode.</param>
    /// <param name="nodes">The resolved entities of these episodes. Materialised endpoints are added to it.</param>
    /// <param name="previousEpisodes">Earlier episodes, as context.</param>
    /// <param name="edgeTypeMap">Which custom edge types may join which entity labels.</param>
    /// <param name="groupId">The group of the new facts; empty uses the primary episode's group.</param>
    /// <param name="edgeTypes">Custom edge types by name, or <see langword="null"/>.</param>
    /// <param name="strictEdgeTypes">Keep only facts of a custom edge type.</param>
    /// <param name="customExtractionInstructions">Extra instructions for the prompt.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The facts, and the endpoint entities materialised onto <paramref name="nodes"/>.</returns>
    [SuppressMessage("Design", "CA1002", Justification = "Dangling endpoints are added to and pruned from the caller's list, as upstream.")]
    public static async Task<(IReadOnlyList<EntityEdge> Edges, IReadOnlyList<EntityNode> MaterializedNodes)>
        ExtractEdgesAsync(
            GraphitiClients clients,
            IReadOnlyList<EpisodicNode> episodes,
            List<EntityNode> nodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            string groupId = "",
            IReadOnlyDictionary<string, Type>? edgeTypes = null,
            bool strictEdgeTypes = false,
            string? customExtractionInstructions = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(previousEpisodes);
        ArgumentNullException.ThrowIfNull(edgeTypeMap);
        if (episodes.Count == 0)
        {
            throw new ArgumentException("At least one episode is required.", nameof(episodes));
        }

        var logger = clients.LlmClient.Logger;
        var primaryEpisode = episodes[0];
        var initialNodeUuids = nodes.Select(n => n.Uuid).ToHashSet(StringComparer.Ordinal);

        // Edge type name → every signature that allows it, in map order.
        var edgeTypeSignaturesMap = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        foreach (var (signature, edgeTypeNames) in edgeTypeMap)
        {
            foreach (var edgeType in edgeTypeNames)
            {
                if (!edgeTypeSignaturesMap.TryGetValue(edgeType, out var signatures))
                {
                    signatures = [];
                    edgeTypeSignaturesMap[edgeType] = signatures;
                }

                signatures.Add([signature.Source, signature.Target]);
            }
        }

        var edgeTypesContext = new List<Dictionary<string, object?>>();
        if (edgeTypes is not null)
        {
            foreach (var (typeName, typeModel) in edgeTypes)
            {
                edgeTypesContext.Add(new Dictionary<string, object?>
                {
                    ["fact_type_name"] = typeName,
                    ["fact_type_signatures"] = edgeTypeSignaturesMap.TryGetValue(typeName, out var signatures)
                        ? signatures
                        : new List<string[]> { new[] { DedupHelpers.EntityBaseLabel, DedupHelpers.EntityBaseLabel } },
                    ["fact_type_description"] = TypeDescription(typeModel),
                });
            }
        }

        if (strictEdgeTypes && edgeTypesContext.Count == 0)
        {
            return ([], []);
        }

        var allowedEdgeTypeNames = strictEdgeTypes
            ? (edgeTypes ?? new Dictionary<string, Type>()).Keys.ToHashSet(StringComparer.Ordinal)
            : null;

        // Normalised name → node, for validation and materialisation. Later nodes win, as a dict comprehension.
        var nameToNode = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            nameToNode[DedupHelpers.NormalizeStringExact(node.Name)] = node;
        }

        var episodeAttribution = "";
        if (episodes.Count > 1)
        {
            episodeAttribution =
                "\n8. **Episode Attribution**: The CURRENT_MESSAGE contains multiple episodes labeled " +
                "[Episode 0], [Episode 1], etc. Each episode header includes a timestamp indicating " +
                "when that episode occurred. Use the per-episode timestamp to resolve relative time " +
                "mentions within each episode rather than relying solely on REFERENCE_TIME. " +
                "For each extracted fact, set `episode_indices` " +
                "to the 0-based list of episode numbers that the fact was derived from. " +
                "A fact sourced from Episodes 0 and 1 should have `episode_indices: [0, 1]`.";
        }

        // The latest episode's timestamp is the primary reference time (the first one on ties, as Python's max).
        var latestEpisode = episodes[0];
        foreach (var ep in episodes)
        {
            if (ep.ValidAt > latestEpisode.ValidAt)
            {
                latestEpisode = ep;
            }
        }

        var context = new Dictionary<string, object?>
        {
            ["episode_content"] = TextUtils.ConcatenateEpisodes(episodes),
            ["nodes"] = nodes.Select(node => new Dictionary<string, object?>
            {
                ["name"] = node.Name,
                ["entity_types"] = node.Labels.ToList(),
            }).ToList(),
            ["previous_episodes"] = previousEpisodes.Select(ep => new Dictionary<string, object?>
            {
                ["content"] = ep.Content,
                ["timestamp"] = PythonText.IsoFormat(ep.ValidAt),
            }).ToList(),
            ["reference_time"] = latestEpisode.ValidAt,
            ["edge_types"] = edgeTypesContext,
            ["strict_edge_types"] = strictEdgeTypes,
            ["custom_extraction_instructions"] = (customExtractionInstructions ?? "") + episodeAttribution,
        };

        var edgeGroupId = string.IsNullOrEmpty(groupId) ? primaryEpisode.GroupId : groupId;
        var llmResponse = await clients.CompletePromptAsync<ExtractedEdges>(
            PromptNames.ExtractEdgesEdge,
            context,
            maxTokens: ExtractEdgesMaxTokens,
            groupId: edgeGroupId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Validate entity names; materialise dangling endpoints only for facts that are kept.
        var edgesData = new List<(ExtractedEdge Data, string SourceName, string TargetName)>();
        foreach (var edgeData in llmResponse.Edges)
        {
            var sourceName = edgeData.SourceEntityName.Trim();
            var targetName = edgeData.TargetEntityName.Trim();
            if (sourceName.Length == 0 || targetName.Length == 0)
            {
                Log.EmptyEndpointName(logger);
                continue;
            }

            if (edgeData.Fact.Trim().Length == 0)
            {
                continue;
            }

            if (allowedEdgeTypeNames is not null && !allowedEdgeTypeNames.Contains(edgeData.RelationType))
            {
                Log.NotInStrictOntology(logger, edgeData.RelationType);
                continue;
            }

            if (!nameToNode.TryGetValue(DedupHelpers.NormalizeStringExact(sourceName), out var sourceNode))
            {
                sourceNode = DanglingEndpoints.MaterializeDanglingEndpointNode(sourceName, edgeGroupId, nameToNode, nodes);
            }

            if (!nameToNode.TryGetValue(DedupHelpers.NormalizeStringExact(targetName), out var targetNode))
            {
                targetNode = DanglingEndpoints.MaterializeDanglingEndpointNode(targetName, edgeGroupId, nameToNode, nodes);
            }

            // Keep the canonical names for the conversion loop below.
            edgesData.Add((edgeData, sourceNode.Name, targetNode.Name));
        }

        if (edgesData.Count == 0)
        {
            var pruned = DanglingEndpoints.PruneUnreferencedMaterializedNodes(
                nodes, initialNodeUuids, new HashSet<string>(StringComparer.Ordinal));
            return ([], pruned);
        }

        var edges = new List<EntityEdge>();
        foreach (var (edgeData, canonicalSource, canonicalTarget) in edgesData)
        {
            nameToNode.TryGetValue(DedupHelpers.NormalizeStringExact(canonicalSource), out var sourceNode);
            nameToNode.TryGetValue(DedupHelpers.NormalizeStringExact(canonicalTarget), out var targetNode);
            if (sourceNode is null || targetNode is null)
            {
                Log.EndpointNotFound(logger);
                continue;
            }

            DateTimeOffset? validAt = null;
            DateTimeOffset? invalidAt = null;
            if (!string.IsNullOrEmpty(edgeData.ValidAt))
            {
                if (TemporalEdgeUtils.TryParseIsoDateTime(edgeData.ValidAt, out var parsed))
                {
                    validAt = parsed;
                }
                else
                {
                    Log.ValidAtParseError(logger);
                }
            }

            if (!string.IsNullOrEmpty(edgeData.InvalidAt))
            {
                if (TemporalEdgeUtils.TryParseIsoDateTime(edgeData.InvalidAt, out var parsed))
                {
                    invalidAt = parsed;
                }
                else
                {
                    Log.InvalidAtParseError(logger, edgeData.InvalidAt);
                }
            }

            // Map episode_indices (0-based) to episode ids; fall back to every episode when none is in range.
            var edgeEpisodeUuids = new List<string>();
            foreach (var idx in edgeData.EpisodeIndices)
            {
                if (idx >= 0 && idx < episodes.Count)
                {
                    edgeEpisodeUuids.Add(episodes[idx].Uuid);
                }
            }

            if (edgeEpisodeUuids.Count == 0)
            {
                edgeEpisodeUuids = episodes.Select(ep => ep.Uuid).ToList();
            }

            var edgeReferenceTime =
                edgeData.EpisodeIndices.Count > 0 && edgeData.EpisodeIndices[0] >= 0
                                                  && edgeData.EpisodeIndices[0] < episodes.Count
                    ? episodes[edgeData.EpisodeIndices[0]].ValidAt
                    : primaryEpisode.ValidAt;
            (validAt, invalidAt) = TemporalEdgeUtils.NormalizeFutureTemporalBounds(
                edgeData.Fact, edgeReferenceTime, validAt, invalidAt);

            edges.Add(new EntityEdge
            {
                SourceNodeUuid = sourceNode.Uuid,
                TargetNodeUuid = targetNode.Uuid,
                Name = edgeData.RelationType,
                GroupId = edgeGroupId,
                Fact = edgeData.Fact,
                Episodes = edgeEpisodeUuids,
                CreatedAt = DateTimeUtils.UtcNow(),
                ValidAt = validAt,
                InvalidAt = invalidAt,
                ReferenceTime = edgeReferenceTime,
            });
        }

        var referencedUuids = edges.Select(e => e.SourceNodeUuid)
            .Concat(edges.Select(e => e.TargetNodeUuid))
            .ToHashSet(StringComparer.Ordinal);
        var materializedNodes = DanglingEndpoints.PruneUnreferencedMaterializedNodes(
            nodes, initialNodeUuids, referencedUuids);
        if (materializedNodes.Count > 0)
        {
            Log.MaterializedEndpoints(logger, materializedNodes.Count, edgeGroupId);
        }

        return (edges, materializedNodes);
    }

    /// <summary>
    /// Resolves extracted facts against the graph (upstream <c>resolve_extracted_edges</c>): drops or renames facts
    /// that break their custom type's signature, removes in-batch duplicates, finds duplicate candidates (same
    /// endpoints, either direction) and invalidation candidates (anywhere in the group) by hybrid search, asks the
    /// LLM which are duplicates or contradictions, dates new facts, and invalidates the facts they supersede.
    /// </summary>
    /// <remarks>
    /// A fact named after a custom edge type must join the entity types that type's signature declares in
    /// <paramref name="edgeTypeMap"/>; a mismatched fact is dropped when <paramref name="strictEdgeTypes"/> is set
    /// and renamed to <see cref="DefaultEdgeName"/> otherwise.
    /// </remarks>
    /// <param name="clients">The services.</param>
    /// <param name="extractedEdges">Facts from <see cref="ExtractEdgesAsync(GraphitiClients, IReadOnlyList{EpisodicNode}, List{EntityNode}, IReadOnlyList{EpisodicNode}, IReadOnlyDictionary{ValueTuple{string, string}, IReadOnlyList{string}}, string, IReadOnlyDictionary{string, Type}?, bool, string?, CancellationToken)"/>,
    /// with endpoints already pointing at resolved entities.</param>
    /// <param name="episode">The episode being ingested.</param>
    /// <param name="entities">The resolved entities; endpoints not among them are loaded from storage (in the facts'
    /// group).</param>
    /// <param name="edgeTypes">Custom edge types by name.</param>
    /// <param name="edgeTypeMap">Which custom edge types may join which entity labels.</param>
    /// <param name="existingEdgesOverride">Extra duplicate candidates, added to the search results of facts with the
    /// same source and target (same direction) and group.</param>
    /// <param name="strictEdgeTypes">Drop, rather than rename, facts that break their type's signature.</param>
    /// <param name="invalidatedBy">When given, filled with the id of each invalidated fact mapped to the ids of the
    /// resolved facts that invalidated it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>All resolved facts (existing facts where a duplicate was found), the existing facts invalidated, and
    /// the resolved facts that are new to the graph.</returns>
    public static async Task<(IReadOnlyList<EntityEdge> ResolvedEdges, IReadOnlyList<EntityEdge> InvalidatedEdges,
        IReadOnlyList<EntityEdge> NewEdges)> ResolveExtractedEdgesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityEdge> extractedEdges,
        EpisodicNode episode,
        IReadOnlyList<EntityNode> entities,
        IReadOnlyDictionary<string, Type> edgeTypes,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
        IReadOnlyList<EntityEdge>? existingEdgesOverride = null,
        bool strictEdgeTypes = false,
        IDictionary<string, List<string>>? invalidatedBy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(extractedEdges);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(edgeTypes);
        ArgumentNullException.ThrowIfNull(edgeTypeMap);

        var driver = clients.Driver;
        var embedder = clients.Embedder;

        var uuidEntityMap = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            uuidEntityMap[entity.Uuid] = entity;
        }

        // Load endpoints that are not among the given entities, limited to the facts' group.
        var referencedNodeUuids = new List<string>();
        foreach (var extractedEdge in extractedEdges)
        {
            foreach (var uuid in new[] { extractedEdge.SourceNodeUuid, extractedEdge.TargetNodeUuid })
            {
                if (!uuidEntityMap.ContainsKey(uuid) && !referencedNodeUuids.Contains(uuid))
                {
                    referencedNodeUuids.Add(uuid);
                }
            }
        }

        if (referencedNodeUuids.Count > 0)
        {
            var edgeGroupId = extractedEdges[0].GroupId;
            var missingNodes = await driver.EntityNodeOps.GetByUuidsAsync(referencedNodeUuids, cancellationToken)
                .ConfigureAwait(false);
            foreach (var node in missingNodes.Where(n => n.GroupId == edgeGroupId))
            {
                uuidEntityMap[node.Uuid] = node;
            }
        }

        // The custom edge types whose signature matches each fact's endpoints.
        var edgeTypesLst = extractedEdges
            .Select(edge => EdgeTypesForEndpoints(edge, uuidEntityMap, edgeTypes, edgeTypeMap))
            .ToList();

        var (keptEdges, keptEdgeTypes) = EnforceEdgeTypeSignatures(
            extractedEdges, edgeTypesLst, edgeTypeMap, strictEdgeTypes);

        // Fast path: drop same-direction duplicates, and reverse-direction duplicates with the same name.
        var seen = new Dictionary<(string, string, string), EntityEdge>();
        var edges = new List<EntityEdge>();
        var edgeTypesPerEdge = new List<IReadOnlyDictionary<string, Type>>();
        for (var i = 0; i < keptEdges.Count; i++)
        {
            var edge = keptEdges[i];
            var normalizedFact = DedupHelpers.NormalizeStringExact(edge.Fact);
            var key = (edge.SourceNodeUuid, edge.TargetNodeUuid, normalizedFact);
            if (seen.ContainsKey(key))
            {
                continue;
            }

            var reverseKey = (edge.TargetNodeUuid, edge.SourceNodeUuid, normalizedFact);
            if (seen.TryGetValue(reverseKey, out var reverseEdge) && reverseEdge.Name == edge.Name)
            {
                continue;
            }

            seen[key] = edge;
            edges.Add(edge);
            edgeTypesPerEdge.Add(keptEdgeTypes[i]);
        }

        await CreateEntityEdgeEmbeddingsAsync(embedder, edges, cancellationToken).ConfigureAwait(false);

        // Duplicate candidates share the endpoints, in either direction. The endpoint filter narrows the search;
        // the post-filter drops self-loops it admits. Each search reuses the fact's embedding.
        var relatedEdgesResults = await Concurrency.SemaphoreGatherAsync(
            edges.Select<EntityEdge, Func<Task<SearchResults>>>(edge => () => SearchAsync(
                clients, edge, PairSearchFilter(edge), cancellationToken)),
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);
        var relatedEdgesLists = edges
            .Select((edge, i) => SamePairEdges(edge, relatedEdgesResults[i].Edges))
            .ToList();

        if (existingEdgesOverride is { Count: > 0 })
        {
            var overrideByPair = new Dictionary<(string, string), List<EntityEdge>>();
            foreach (var edge in existingEdgesOverride)
            {
                var pair = (edge.SourceNodeUuid, edge.TargetNodeUuid);
                if (!overrideByPair.TryGetValue(pair, out var list))
                {
                    list = [];
                    overrideByPair[pair] = list;
                }

                list.Add(edge);
            }

            for (var i = 0; i < edges.Count; i++)
            {
                var extractedEdge = edges[i];
                var relatedEdges = relatedEdgesLists[i];
                var existingUuids = relatedEdges.Select(e => e.Uuid).ToHashSet(StringComparer.Ordinal);
                if (!overrideByPair.TryGetValue((extractedEdge.SourceNodeUuid, extractedEdge.TargetNodeUuid),
                        out var overrides))
                {
                    continue;
                }

                foreach (var edge in overrides)
                {
                    if (edge.GroupId == extractedEdge.GroupId && existingUuids.Add(edge.Uuid))
                    {
                        relatedEdges.Add(edge);
                    }
                }
            }
        }

        var invalidationResults = await Concurrency.SemaphoreGatherAsync(
            edges.Select<EntityEdge, Func<Task<SearchResults>>>(edge => () => SearchAsync(
                clients, edge, new SearchFilters(), cancellationToken)),
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);

        var invalidationCandidates = new List<List<EntityEdge>>();
        for (var i = 0; i < edges.Count; i++)
        {
            var relatedUuids = relatedEdgesLists[i].Select(e => e.Uuid).ToHashSet(StringComparer.Ordinal);
            invalidationCandidates.Add(invalidationResults[i].Edges.Where(e => !relatedUuids.Contains(e.Uuid)).ToList());
        }

        var dedupeResults = await Concurrency.SemaphoreGatherAsync(
            edges.Select<EntityEdge, Func<Task<DedupeResult>>>((edge, i) => () => DedupeExtractedEdgeAsync(
                clients, edge, relatedEdgesLists[i], invalidationCandidates[i], episode, edgeTypesPerEdge[i],
                cancellationToken)),
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);

        var resolvedEdges = new List<EntityEdge>();
        var invalidatedEdges = new List<EntityEdge>();
        var newEdges = new List<EntityEdge>();
        for (var i = 0; i < edges.Count; i++)
        {
            var resolvedEdge = dedupeResults[i].ResolvedEdge;
            resolvedEdges.Add(resolvedEdge);

            // A fact is new when it resolved to itself rather than to an existing fact.
            if (resolvedEdge.Uuid == edges[i].Uuid)
            {
                newEdges.Add(resolvedEdge);
            }
        }

        foreach (var result in dedupeResults)
        {
            if (result.InvalidationCandidates is null)
            {
                continue;
            }

            var invalidated = ApplyTemporalInvalidation(result.ResolvedEdge, result.InvalidationCandidates);
            invalidatedEdges.AddRange(invalidated);
            if (invalidatedBy is not null)
            {
                foreach (var edge in invalidated)
                {
                    if (!invalidatedBy.TryGetValue(edge.Uuid, out var by))
                    {
                        by = [];
                        invalidatedBy[edge.Uuid] = by;
                    }

                    by.Add(result.ResolvedEdge.Uuid);
                }
            }
        }

        await Concurrency.SemaphoreGatherAsync(
            new Func<Task>[]
            {
                () => CreateEntityEdgeEmbeddingsAsync(embedder, resolvedEdges, cancellationToken),
                () => CreateEntityEdgeEmbeddingsAsync(embedder, invalidatedEdges, cancellationToken),
            },
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);

        return (resolvedEdges, invalidatedEdges, newEdges);
    }

    /// <summary>
    /// The custom edge types whose signature in <paramref name="edgeTypeMap"/> matches the labels of the fact's
    /// endpoints (each endpoint also counts as <c>Entity</c>; an unknown endpoint is only <c>Entity</c>). Upstream
    /// <c>_edge_types_for_endpoints</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, Type> EdgeTypesForEndpoints(
        EntityEdge extractedEdge,
        IReadOnlyDictionary<string, EntityNode> uuidEntityMap,
        IReadOnlyDictionary<string, Type> edgeTypes,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap)
    {
        ArgumentNullException.ThrowIfNull(extractedEdge);
        ArgumentNullException.ThrowIfNull(uuidEntityMap);
        ArgumentNullException.ThrowIfNull(edgeTypes);
        ArgumentNullException.ThrowIfNull(edgeTypeMap);

        static List<string> LabelsOf(EntityNode? node) =>
            node is null ? [DedupHelpers.EntityBaseLabel] : [.. node.Labels, DedupHelpers.EntityBaseLabel];

        var sourceLabels = LabelsOf(uuidEntityMap.GetValueOrDefault(extractedEdge.SourceNodeUuid));
        var targetLabels = LabelsOf(uuidEntityMap.GetValueOrDefault(extractedEdge.TargetNodeUuid));

        var matching = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var sourceLabel in sourceLabels)
        {
            foreach (var targetLabel in targetLabels)
            {
                if (!edgeTypeMap.TryGetValue((sourceLabel, targetLabel), out var typeNames))
                {
                    continue;
                }

                foreach (var typeName in typeNames)
                {
                    if (edgeTypes.TryGetValue(typeName, out var typeModel))
                    {
                        matching[typeName] = typeModel;
                    }
                }
            }
        }

        return matching;
    }

    /// <summary>
    /// Applies custom edge type signatures to extracted facts (upstream <c>enforce_edge_type_signatures</c>).
    /// </summary>
    /// <remarks>
    /// <paramref name="edgeTypesLst"/>[i] holds the custom edge types whose signature matches the endpoints of
    /// <paramref name="extractedEdges"/>[i] (see <see cref="EdgeTypesForEndpoints"/>). A fact named after an edge type
    /// that declares at least one signature in <paramref name="edgeTypeMap"/>, but that is not in its matching set,
    /// breaks the signature: it is dropped when <paramref name="strictEdgeTypes"/> is set, and otherwise renamed to
    /// <see cref="DefaultEdgeName"/> with its attributes cleared. Facts named after a type with no declared
    /// signature pass unchanged, because such a type applies between any entity types.
    /// </remarks>
    /// <returns>The kept facts and their matching edge types, in input order.</returns>
    public static (IReadOnlyList<EntityEdge> Edges, IReadOnlyList<IReadOnlyDictionary<string, Type>> EdgeTypes)
        EnforceEdgeTypeSignatures(
            IReadOnlyList<EntityEdge> extractedEdges,
            IReadOnlyList<IReadOnlyDictionary<string, Type>> edgeTypesLst,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            bool strictEdgeTypes)
    {
        ArgumentNullException.ThrowIfNull(extractedEdges);
        ArgumentNullException.ThrowIfNull(edgeTypesLst);
        ArgumentNullException.ThrowIfNull(edgeTypeMap);
        if (extractedEdges.Count != edgeTypesLst.Count)
        {
            throw new ArgumentException("Each edge needs its list of matching edge types.", nameof(edgeTypesLst));
        }

        var signedEdgeTypes = edgeTypeMap.Values.SelectMany(names => names).ToHashSet(StringComparer.Ordinal);
        var keptEdges = new List<EntityEdge>();
        var keptEdgeTypes = new List<IReadOnlyDictionary<string, Type>>();
        for (var i = 0; i < extractedEdges.Count; i++)
        {
            var extractedEdge = extractedEdges[i];
            var matchingEdgeTypes = edgeTypesLst[i];
            if (signedEdgeTypes.Contains(extractedEdge.Name) && !matchingEdgeTypes.ContainsKey(extractedEdge.Name))
            {
                if (strictEdgeTypes)
                {
                    continue;
                }

                extractedEdge.Name = DefaultEdgeName;
                extractedEdge.Attributes.Clear();
            }

            keptEdges.Add(extractedEdge);
            keptEdgeTypes.Add(matchingEdgeTypes);
        }

        return (keptEdges, keptEdgeTypes);
    }

    /// <summary>
    /// Expires the resolved fact and any candidate it contradicts (upstream <c>_apply_temporal_invalidation</c>).
    /// </summary>
    /// <remarks>
    /// Every check compares dates, so date the fact first. A fact that arrived with an end date is expired at once.
    /// Otherwise, if a candidate starts later than the fact, the fact is the outdated one: it ends when the earliest
    /// such candidate starts, and is expired. Then <see cref="ResolveEdgeContradictions"/> closes the candidates.
    /// <paramref name="invalidationCandidates"/> is sorted in place by start date (undated last), stably.
    /// </remarks>
    /// <returns>The candidates this fact invalidated.</returns>
    public static IReadOnlyList<EntityEdge> ApplyTemporalInvalidation(
        EntityEdge resolvedEdge,
        IList<EntityEdge> invalidationCandidates)
    {
        ArgumentNullException.ThrowIfNull(resolvedEdge);
        ArgumentNullException.ThrowIfNull(invalidationCandidates);
        var now = DateTimeUtils.UtcNow();

        // The fact arrived with an end date, so it is already historical.
        if (resolvedEdge.InvalidAt is not null && resolvedEdge.ExpiredAt is null)
        {
            resolvedEdge.ExpiredAt = now;
        }

        // A candidate that starts later supersedes this fact, so expire this one instead.
        if (resolvedEdge.ExpiredAt is null)
        {
            var sorted = invalidationCandidates
                .OrderBy(c => c.ValidAt is null)
                .ThenBy(c => c.ValidAt)
                .ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                invalidationCandidates[i] = sorted[i];
            }

            foreach (var candidate in invalidationCandidates)
            {
                if (candidate.ValidAt is { } candidateValidAt
                    && resolvedEdge.ValidAt is { } resolvedValidAt
                    && candidateValidAt > resolvedValidAt)
                {
                    resolvedEdge.InvalidAt = candidate.ValidAt;
                    resolvedEdge.ExpiredAt = now;
                    break;
                }
            }
        }

        return ResolveEdgeContradictions(resolvedEdge, invalidationCandidates);
    }

    /// <summary>
    /// Closes the candidates the resolved fact supersedes (upstream <c>resolve_edge_contradictions</c>).
    /// </summary>
    /// <remarks>
    /// A candidate that ended at or before the fact started, or started at or after the fact ended, does not
    /// conflict. A candidate that started before the fact gets the fact's start as its end, and is expired (keeping
    /// an earlier expiry). Undated candidates are left alone.
    /// </remarks>
    /// <returns>The candidates closed.</returns>
    public static IReadOnlyList<EntityEdge> ResolveEdgeContradictions(
        EntityEdge resolvedEdge,
        IEnumerable<EntityEdge> invalidationCandidates)
    {
        ArgumentNullException.ThrowIfNull(resolvedEdge);
        ArgumentNullException.ThrowIfNull(invalidationCandidates);
        var invalidatedEdges = new List<EntityEdge>();
        foreach (var edge in invalidationCandidates)
        {
            var edgeInvalidAt = edge.InvalidAt;
            var resolvedValidAt = resolvedEdge.ValidAt;
            var edgeValidAt = edge.ValidAt;
            var resolvedInvalidAt = resolvedEdge.InvalidAt;

            if ((edgeInvalidAt is not null && resolvedValidAt is not null && edgeInvalidAt <= resolvedValidAt)
                || (edgeValidAt is not null && resolvedInvalidAt is not null && resolvedInvalidAt <= edgeValidAt))
            {
                continue;
            }

            if (edgeValidAt is not null && resolvedValidAt is not null && edgeValidAt < resolvedValidAt)
            {
                edge.InvalidAt = resolvedEdge.ValidAt;
                edge.ExpiredAt ??= DateTimeUtils.UtcNow();
                invalidatedEdges.Add(edge);
            }
        }

        return invalidatedEdges;
    }

    /// <summary>
    /// Fully resolves one fact on its own: deduplicates it against <paramref name="relatedEdges"/>, dates it, then
    /// applies its contradictions (upstream <c>resolve_extracted_edge</c>).
    /// </summary>
    /// <param name="clients">The services; LLM calls use its prompt library and routing.</param>
    /// <param name="extractedEdge">The new fact.</param>
    /// <param name="relatedEdges">Duplicate candidates: facts with the same endpoints.</param>
    /// <param name="existingEdges">Further facts that may be contradicted.</param>
    /// <param name="episode">The episode being ingested.</param>
    /// <param name="edgeTypeCandidates">Custom edge types allowed for this fact's endpoints, or
    /// <see langword="null"/>. When given and the fact's type is not among them, its attributes are cleared.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The resolved fact (an existing one when a duplicate was found), the facts it invalidated, and the
    /// duplicates found.</returns>
    public static async Task<(EntityEdge ResolvedEdge, IReadOnlyList<EntityEdge> InvalidatedEdges,
        IReadOnlyList<EntityEdge> DuplicateEdges)> ResolveExtractedEdgeAsync(
        GraphitiClients clients,
        EntityEdge extractedEdge,
        IReadOnlyList<EntityEdge> relatedEdges,
        IReadOnlyList<EntityEdge> existingEdges,
        EpisodicNode episode,
        IReadOnlyDictionary<string, Type>? edgeTypeCandidates = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DedupeExtractedEdgeAsync(
            clients, extractedEdge, relatedEdges, existingEdges, episode, edgeTypeCandidates, cancellationToken)
            .ConfigureAwait(false);
        if (result.InvalidationCandidates is null)
        {
            return (result.ResolvedEdge, [], result.DuplicateEdges);
        }

        var invalidated = ApplyTemporalInvalidation(result.ResolvedEdge, result.InvalidationCandidates);
        return (result.ResolvedEdge, invalidated, result.DuplicateEdges);
    }

    /// <summary>
    /// Finds facts between source and target that are already marked <c>IS_DUPLICATE_OF</c> and returns only the
    /// pairs that are not (upstream <c>filter_existing_duplicate_of_edges</c>).
    /// </summary>
    /// <remarks>
    /// Upstream runs one Cypher query; this asks <see cref="IEntityEdgeOperations.GetBetweenNodesAsync"/> per pair
    /// (source → target only) and checks the fact names. Pairs with the same ids collapse to one, keeping the last
    /// pair's nodes at the first pair's position, as a Python dict does.
    /// </remarks>
    /// <param name="driver">Storage.</param>
    /// <param name="duplicatesNodeTuples">(duplicate, canonical) entity pairs.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pairs with no <c>IS_DUPLICATE_OF</c> fact yet.</returns>
    public static async Task<IReadOnlyList<(EntityNode Source, EntityNode Target)>> FilterExistingDuplicateOfEdgesAsync(
        IGraphDriver driver,
        IReadOnlyList<(EntityNode Source, EntityNode Target)> duplicatesNodeTuples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(duplicatesNodeTuples);
        if (duplicatesNodeTuples.Count == 0)
        {
            return [];
        }

        var keys = new List<(string, string)>();
        var duplicateNodesMap = new Dictionary<(string, string), (EntityNode Source, EntityNode Target)>();
        foreach (var (source, target) in duplicatesNodeTuples)
        {
            var key = (source.Uuid, target.Uuid);
            if (!duplicateNodesMap.ContainsKey(key))
            {
                keys.Add(key);
            }

            duplicateNodesMap[key] = (source, target);
        }

        var remaining = new List<(EntityNode Source, EntityNode Target)>();
        foreach (var key in keys)
        {
            var between = await driver.EntityEdgeOps.GetBetweenNodesAsync(key.Item1, key.Item2, cancellationToken)
                .ConfigureAwait(false);
            if (!between.Any(edge => edge.Name == IsDuplicateOfEdgeName))
            {
                remaining.Add(duplicateNodesMap[key]);
            }
        }

        return remaining;
    }

    /// <summary>
    /// Embeds the fact text of each edge that has one (upstream <c>create_entity_edge_embeddings</c>, from
    /// <c>edges.py</c>).
    /// </summary>
    public static async Task CreateEntityEdgeEmbeddingsAsync(
        EmbedderClient embedder,
        IReadOnlyList<EntityEdge> edges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(edges);
        var filtered = edges.Where(e => !string.IsNullOrEmpty(e.Fact)).ToList();
        if (filtered.Count == 0)
        {
            return;
        }

        var embeddings = await embedder.CreateBatchAsync(filtered.Select(e => e.Fact).ToList(), cancellationToken)
            .ConfigureAwait(false);
        if (embeddings.Count != filtered.Count)
        {
            throw new InvalidOperationException(
                $"The embedder returned {embeddings.Count} embeddings for {filtered.Count} facts.");
        }

        for (var i = 0; i < filtered.Count; i++)
        {
            filtered[i].FactEmbedding = embeddings[i];
        }
    }

    /// <summary>
    /// Restricts an edge search to the fact's endpoints, either direction (upstream <c>_pair_search_filter</c>).
    /// <c>source IN {a, b} AND target IN {a, b}</c> also admits self-loops on either endpoint;
    /// <see cref="SamePairEdges"/> drops them.
    /// </summary>
    internal static SearchFilters PairSearchFilter(EntityEdge edge)
    {
        string[] endpoints = [edge.SourceNodeUuid, edge.TargetNodeUuid];
        return new SearchFilters
        {
            EdgeSourceNodeUuids = endpoints,
            EdgeTargetNodeUuids = endpoints,
        };
    }

    /// <summary>Keeps only facts between the extracted fact's endpoints, either direction (upstream
    /// <c>_same_pair_edges</c>).</summary>
    internal static List<EntityEdge> SamePairEdges(EntityEdge extractedEdge, IEnumerable<EntityEdge> candidates)
    {
        var forward = (extractedEdge.SourceNodeUuid, extractedEdge.TargetNodeUuid);
        var reverse = (extractedEdge.TargetNodeUuid, extractedEdge.SourceNodeUuid);
        return candidates
            .Where(edge => (edge.SourceNodeUuid, edge.TargetNodeUuid) == forward
                           || (edge.SourceNodeUuid, edge.TargetNodeUuid) == reverse)
            .ToList();
    }

    /// <summary>
    /// The outcome of <see cref="DedupeExtractedEdgeAsync"/>. <see cref="InvalidationCandidates"/> is
    /// <see langword="null"/> when the fact took an early return and must skip contradiction entirely; an empty list
    /// means "no candidates, but still run the checks".
    /// </summary>
    internal sealed record DedupeResult(
        EntityEdge ResolvedEdge,
        IReadOnlyList<EntityEdge> DuplicateEdges,
        List<EntityEdge>? InvalidationCandidates);

    /// <summary>
    /// Deduplicates an extracted fact and gathers the facts it may contradict, stopping before contradiction so the
    /// caller can invalidate after dating (upstream <c>_dedupe_extracted_edge</c>).
    /// </summary>
    internal static async Task<DedupeResult> DedupeExtractedEdgeAsync(
        GraphitiClients clients,
        EntityEdge extractedEdge,
        IReadOnlyList<EntityEdge> relatedEdges,
        IReadOnlyList<EntityEdge> existingEdges,
        EpisodicNode episode,
        IReadOnlyDictionary<string, Type>? edgeTypeCandidates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(extractedEdge);
        ArgumentNullException.ThrowIfNull(relatedEdges);
        ArgumentNullException.ThrowIfNull(existingEdges);
        ArgumentNullException.ThrowIfNull(episode);
        var logger = clients.LlmClient.Logger;

        void AppendEpisodeProvenance(EntityEdge resolved)
        {
            foreach (var episodeUuid in extractedEdge.Episodes.Append(episode.Uuid).ToList())
            {
                if (!resolved.Episodes.Contains(episodeUuid))
                {
                    resolved.Episodes.Add(episodeUuid);
                }
            }
        }

        // Nothing to compare against: no duplicate and nothing to contradict.
        if (relatedEdges.Count == 0 && existingEdges.Count == 0)
        {
            // Still extract custom attributes and timestamps.
            var model = edgeTypeCandidates is { Count: > 0 }
                ? edgeTypeCandidates.GetValueOrDefault(extractedEdge.Name)
                : null;
            if (model is not null && EdgeAttributeMerge.FieldCount(model) != 0)
            {
                await ExtractEdgeAttributesAsync(clients, extractedEdge, episode, model, cancellationToken)
                    .ConfigureAwait(false);
            }

            await ExtractEdgeTimestampsAsync(clients, extractedEdge, episode, cancellationToken).ConfigureAwait(false);
            return new DedupeResult(extractedEdge, [], null);
        }

        // Fast path: the same fact text between the same endpoints (same direction) already exists.
        var normalizedFact = DedupHelpers.NormalizeStringExact(extractedEdge.Fact);
        foreach (var edge in relatedEdges)
        {
            if (edge.SourceNodeUuid == extractedEdge.SourceNodeUuid
                && edge.TargetNodeUuid == extractedEdge.TargetNodeUuid
                && DedupHelpers.NormalizeStringExact(edge.Fact) == normalizedFact)
            {
                AppendEpisodeProvenance(edge);
                return new DedupeResult(edge, [], null);
            }
        }

        // Continuous indexing: invalidation candidates start where duplicate candidates end.
        var relatedEdgesContext = relatedEdges
            .Select((edge, i) => new Dictionary<string, object?> { ["idx"] = i, ["fact"] = edge.Fact })
            .ToList();
        var invalidationIdxOffset = relatedEdges.Count;
        var invalidationContext = existingEdges
            .Select((edge, i) => new Dictionary<string, object?>
            {
                ["idx"] = invalidationIdxOffset + i,
                ["fact"] = edge.Fact,
            })
            .ToList();

        var context = new Dictionary<string, object?>
        {
            ["existing_edges"] = relatedEdgesContext,
            ["new_edge"] = extractedEdge.Fact,
            ["edge_invalidation_candidates"] = invalidationContext,
        };

        var response = await clients.CompletePromptAsync<EdgeDuplicate>(
            PromptNames.DedupeEdgesResolveEdge,
            context,
            modelSize: ModelSize.Small,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var invalidDuplicates = response.DuplicateFacts.Where(i => i < 0 || i >= relatedEdges.Count).ToList();
        if (invalidDuplicates.Count > 0)
        {
            Log.InvalidDuplicateIdx(logger, string.Join(", ", invalidDuplicates), relatedEdges.Count - 1);
        }

        var duplicateFactIds = response.DuplicateFacts.Where(i => i >= 0 && i < relatedEdges.Count).ToList();
        var resolvedEdge = duplicateFactIds.Count > 0 ? relatedEdges[duplicateFactIds[0]] : extractedEdge;
        if (duplicateFactIds.Count > 0)
        {
            AppendEpisodeProvenance(resolvedEdge);
        }

        // Contradictions use the continuous index across both lists.
        var invalidationCandidates = new List<EntityEdge>();
        var maxValidIdx = relatedEdges.Count + existingEdges.Count - 1;
        var invalidContradictions = response.ContradictedFacts.Where(i => i < 0 || i > maxValidIdx).ToList();
        if (invalidContradictions.Count > 0)
        {
            Log.InvalidContradictionIdx(logger, string.Join(", ", invalidContradictions), maxValidIdx);
        }

        foreach (var idx in response.ContradictedFacts)
        {
            if (idx >= 0 && idx < relatedEdges.Count)
            {
                invalidationCandidates.Add(relatedEdges[idx]);
            }
            else if (idx >= invalidationIdxOffset && idx <= maxValidIdx)
            {
                invalidationCandidates.Add(existingEdges[idx - invalidationIdxOffset]);
            }
        }

        // Structured attributes only when the fact's name is a custom type allowed for these endpoints.
        var edgeModel = edgeTypeCandidates is { Count: > 0 }
            ? edgeTypeCandidates.GetValueOrDefault(resolvedEdge.Name)
            : null;
        if (edgeModel is not null && EdgeAttributeMerge.FieldCount(edgeModel) != 0)
        {
            await ExtractEdgeAttributesAsync(clients, resolvedEdge, episode, edgeModel, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (edgeTypeCandidates is not null)
        {
            // A schema map was given but this relation has no model: clear attributes from a prior schema. Without
            // a map (add_triplet), the caller's attributes stay.
            resolvedEdge.Attributes.Clear();
        }

        // Date new facts; duplicates keep their timestamps.
        if (resolvedEdge.Uuid == extractedEdge.Uuid)
        {
            await ExtractEdgeTimestampsAsync(clients, resolvedEdge, episode, cancellationToken).ConfigureAwait(false);
        }

        var duplicateEdges = duplicateFactIds.Select(idx => relatedEdges[idx]).ToList();
        return new DedupeResult(resolvedEdge, duplicateEdges, invalidationCandidates);
    }

    /// <summary>
    /// Extracts <c>valid_at</c> / <c>invalid_at</c> for a fact with one small LLM call (upstream
    /// <c>_extract_edge_timestamps</c>). Modifies the fact in place. Skipped when the fact already has a timestamp or
    /// there is no reference time (the fact's, else the episode's). A failed call is logged and ignored.
    /// </summary>
    internal static async Task ExtractEdgeTimestampsAsync(
        GraphitiClients clients,
        EntityEdge edge,
        EpisodicNode? episode,
        CancellationToken cancellationToken = default)
    {
        if (edge.ValidAt is not null || edge.InvalidAt is not null)
        {
            return;
        }

        var referenceTime = DateTimeUtils.EnsureUtc(edge.ReferenceTime ?? episode?.ValidAt);
        if (referenceTime is not { } reference)
        {
            return;
        }

        var context = new Dictionary<string, object?>
        {
            ["fact"] = edge.Fact,
            ["reference_time"] = PythonText.IsoFormat(reference),
        };
        try
        {
            var timestamps = await clients.CompletePromptAsync<EdgeTimestamps>(
                PromptNames.ExtractEdgesExtractTimestamps,
                context,
                modelSize: ModelSize.Small,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            TemporalEdgeUtils.ApplyExtractedTimestamps(edge, timestamps.ValidAt, timestamps.InvalidAt, reference);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.TimestampExtractionFailed(clients.LlmClient.Logger, e, edge.Uuid);
        }
    }

    private static async Task ExtractEdgeAttributesAsync(
        GraphitiClients clients,
        EntityEdge edge,
        EpisodicNode? episode,
        Type edgeModel,
        CancellationToken cancellationToken)
    {
        var context = new Dictionary<string, object?>
        {
            ["fact"] = edge.Fact,
            ["reference_time"] = episode?.ValidAt,
            ["existing_attributes"] = edge.Attributes,
        };
        var response = await clients.CompletePromptAsync(
            PromptNames.ExtractEdgesExtractAttributes,
            context,
            responseModel: edgeModel,
            modelSize: ModelSize.Small,
            attributeExtraction: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var merged = EdgeAttributeMerge.ApplyCappedReplace(
            EdgeAttributeMerge.ToAttributes(response), edgeModel, edge.Attributes);
        edge.Attributes.Clear();
        foreach (var (key, value) in merged)
        {
            edge.Attributes[key] = value;
        }
    }

    private static Task<SearchResults> SearchAsync(
        GraphitiClients clients,
        EntityEdge edge,
        SearchFilters searchFilter,
        CancellationToken cancellationToken) =>
        SearchEngine.SearchAsync(
            clients.Driver,
            clients.Embedder,
            clients.CrossEncoder,
            edge.Fact,
            [edge.GroupId],
            SearchConfigRecipes.EdgeHybridSearchRrf,
            searchFilter,
            queryVector: edge.FactEmbedding,
            maxCoroutines: clients.MaxCoroutines,
            cancellationToken: cancellationToken);

    // Upstream uses the Pydantic model's docstring; a C# type carries it as [Description].
    private static string? TypeDescription(Type type) => type.GetCustomAttribute<DescriptionAttribute>()?.Description;

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping edge with empty source or target entity name")]
        public static partial void EmptyEndpointName(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Skipping edge with relation type \"{RelationType}\" not in strict edge ontology")]
        public static partial void NotInStrictOntology(ILogger logger, string relationType);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not find source or target node for extracted edge")]
        public static partial void EndpointNotFound(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error parsing valid_at date, skipping")]
        public static partial void ValidAtParseError(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Error parsing invalid_at date. Input: {InvalidAt}")]
        public static partial void InvalidAtParseError(ILogger logger, string invalidAt);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Materialized {Count} dangling edge endpoint node(s) for group_id={GroupId}")]
        public static partial void MaterializedEndpoints(ILogger logger, int count, string groupId);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "LLM returned invalid duplicate_facts idx values [{Values}] (valid range: 0-{Max} for EXISTING FACTS)")]
        public static partial void InvalidDuplicateIdx(ILogger logger, string values, int max);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "LLM returned invalid contradicted_facts idx values [{Values}] (valid range: 0-{Max})")]
        public static partial void InvalidContradictionIdx(ILogger logger, string values, int max);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to extract timestamps for edge {EdgeUuid}")]
        public static partial void TimestampExtractionFailed(ILogger logger, Exception exception, string edgeUuid);
    }
}
