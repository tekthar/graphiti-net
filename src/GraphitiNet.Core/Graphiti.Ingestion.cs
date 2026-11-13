// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/graphiti.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Llm;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Search;
using GraphitiNet.Utils;

namespace GraphitiNet;

public sealed partial class Graphiti
{
    /// <summary>
    /// Processes one episode and merges it into the graph (upstream <c>add_episode</c>): extracts entities and
    /// facts, resolves them against the graph, dates and invalidates facts, updates entity summaries, and saves
    /// everything with embeddings.
    /// </summary>
    /// <remarks>
    /// <para>Run it in the background, such as in a queue, and add the episodes of one group one after another,
    /// awaiting each. By default graphiti-net waits for any running write to the same group (see
    /// <see cref="IGroupLock"/>).</para>
    /// <para><b>Model call gate (graphiti-net addition).</b> When the instance has a <see cref="IModelCallGate"/>, a
    /// new episode is saved before any model call, so if the gate refuses a call
    /// (<see cref="ModelCallRefusedException"/>) the episode stays saved but unprocessed. Process it later by calling
    /// this method again with <paramref name="uuid"/> set to <see cref="ModelCallRefusedException.EpisodeUuid"/>: the
    /// stored episode (its content, source and reference time) is used and the other content arguments are ignored,
    /// as upstream. Without a gate, nothing is saved until the end, as upstream.</para>
    /// <para>The <paramref name="cancellationToken"/> is checked between pipeline steps.</para>
    /// </remarks>
    /// <param name="name">The episode's name.</param>
    /// <param name="episodeBody">The content.</param>
    /// <param name="sourceDescription">Where the content came from.</param>
    /// <param name="referenceTime">When the content was created; relative times in it ("last week") are read from
    /// here.</param>
    /// <param name="source">The kind of content.</param>
    /// <param name="groupId">The graph partition; <see langword="null"/> means the provider's default group
    /// (empty).</param>
    /// <param name="uuid">An existing episode to (re)process instead of creating one. It is processed in its own
    /// group: <paramref name="groupId"/> may be omitted, and naming another group throws
    /// <see cref="ArgumentException"/> (graphiti-net; upstream would mix the two groups).</param>
    /// <param name="updateCommunities">Also fold the episode's entities into their communities.</param>
    /// <param name="entityTypes">Custom entity types: names and the classes whose properties become
    /// attributes.</param>
    /// <param name="excludedEntityTypes">Entity types to leave out of the graph; may include <c>Entity</c>.</param>
    /// <param name="previousEpisodeUuids">The episodes to use as context; by default the latest ones in the
    /// group.</param>
    /// <param name="edgeTypes">Custom fact types: names and classes.</param>
    /// <param name="edgeTypeMap">Which fact types may join which entity types, keyed by (source type, target type);
    /// by default every fact type between any two entities.</param>
    /// <param name="customExtractionInstructions">Extra instructions for the extraction prompts.</param>
    /// <param name="saga">The saga to add the episode to: a name (found in the group or created) or a
    /// <see cref="SagaNode"/>.</param>
    /// <param name="sagaPreviousEpisodeUuid">The saga's previous episode, to skip looking it up; pass the previous
    /// call's <see cref="AddEpisodeResults.Episode"/> id when adding a sequence.</param>
    /// <param name="episodeMetadata">Host data stored on the episode and shown to model calls through
    /// <see cref="GraphitiCallContext"/>. A graphiti-net addition.</param>
    /// <param name="cancellationToken">Cancels between steps.</param>
    /// <returns>What was added or changed, with this call's token usage.</returns>
    /// <exception cref="EntityTypeValidationException">A custom entity type uses a protected attribute.</exception>
    /// <exception cref="ArgumentException">An excluded entity type is unknown, or the episode named by
    /// <paramref name="uuid"/> belongs to another group than <paramref name="groupId"/>.</exception>
    /// <exception cref="GroupIdValidationException">The group id has invalid characters.</exception>
    /// <exception cref="ModelCallRefusedException">The model call gate refused a call.</exception>
    public async Task<AddEpisodeResults> AddEpisodeAsync(
        string name,
        string episodeBody,
        string sourceDescription,
        DateTimeOffset referenceTime,
        EpisodeType source = EpisodeType.Message,
        string? groupId = null,
        string? uuid = null,
        bool updateCommunities = false,
        IReadOnlyDictionary<string, Type>? entityTypes = null,
        IReadOnlyList<string>? excludedEntityTypes = null,
        IReadOnlyList<string>? previousEpisodeUuids = null,
        IReadOnlyDictionary<string, Type>? edgeTypes = null,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>>? edgeTypeMap = null,
        string? customExtractionInstructions = null,
        SagaReference? saga = null,
        string? sagaPreviousEpisodeUuid = null,
        IDictionary<string, object?>? episodeMetadata = null,
        CancellationToken cancellationToken = default)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = DateTimeUtils.UtcNow();

        EntityTypesUtils.ValidateEntityTypes(entityTypes);
        EntityTypesUtils.ValidateExcludedEntityTypes(excludedEntityTypes, entityTypes);

        // graphiti-net: a stored episode is processed in its own group, so its lock is the one taken and no other
        // group's episodes become its context.
        var storedGroupId = uuid is null
            ? null
            : (await Driver.EpisodeNodeOps.GetByUuidAsync(uuid, cancellationToken).ConfigureAwait(false)).GroupId;
        var (resolvedGroupId, driver, clients) = ResolveRequestScope(groupId ?? storedGroupId);
        EnsureEpisodeInGroup(uuid, storedGroupId, resolvedGroupId);

        await using var groupLock = await GroupLock.AcquireAsync(resolvedGroupId, cancellationToken)
            .ConfigureAwait(false);
        var usage = new TokenUsageTracker();
        using var operation = BeginOperation("add_episode", resolvedGroupId, usage);
        using var activity = StartActivity("add_episode");

        try
        {
            var previousEpisodes = previousEpisodeUuids is null
                ? await RetrieveEpisodesWithDriverAsync(
                        referenceTime,
                        SearchDefaults.RelevantSchemaLimit,
                        [resolvedGroupId],
                        source,
                        null,
                        driver,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await driver.EpisodeNodeOps.GetByUuidsAsync(previousEpisodeUuids, cancellationToken)
                    .ConfigureAwait(false);

            var episode = uuid is not null
                ? await driver.EpisodeNodeOps.GetByUuidAsync(uuid, cancellationToken).ConfigureAwait(false)
                : new EpisodicNode
                {
                    Name = name,
                    GroupId = resolvedGroupId,
                    Labels = [],
                    Source = source,
                    Content = episodeBody,
                    SourceDescription = sourceDescription,
                    CreatedAt = now,
                    ValidAt = referenceTime,
                    EpisodeMetadata = episodeMetadata,
                };

            if (uuid is not null)
            {
                // Read again under the lock; a write may have changed it meanwhile.
                EnsureEpisodeInGroup(uuid, episode.GroupId, resolvedGroupId);

                // Reprocessing a stored episode: it must not be its own context (upstream would include it).
                previousEpisodes = previousEpisodes.Where(previous => previous.Uuid != episode.Uuid).ToList();
            }
            else if (ModelCallGate is not null)
            {
                // graphiti-net addition: with a gate, keep the episode even if a model call is refused later.
                await driver.EpisodeNodeOps.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            }

            using var episodeScope = BeginEpisode(episode);
            cancellationToken.ThrowIfCancellationRequested();

            var edgeTypeMapDefault = DefaultEdgeTypeMap(edgeTypes);
            var effectiveEdgeTypeMap = edgeTypeMap is { Count: > 0 } ? edgeTypeMap : edgeTypeMapDefault;

            // Extract and resolve nodes.
            var (extractedNodes, nodeEpisodeIndexMap) = await ExtractNodesAsync(
                    clients,
                    [episode],
                    previousEpisodes,
                    entityTypes,
                    excludedEntityTypes,
                    customExtractionInstructions,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var (resolvedNodes, resolvedUuidMap, _) = await ResolveExtractedNodesAsync(
                    clients,
                    extractedNodes,
                    episode,
                    previousEpisodes,
                    entityTypes,
                    cancellationToken)
                .ConfigureAwait(false);
            var nodes = resolvedNodes.ToList();
            var uuidMap = new Dictionary<string, string>(resolvedUuidMap, StringComparer.Ordinal);
            cancellationToken.ThrowIfCancellationRequested();

            // Extract and resolve edges.
            var (resolvedEdges, invalidatedEdges, newEdges) = await ExtractAndResolveEdgesAsync(
                    [episode],
                    extractedNodes,
                    previousEpisodes,
                    effectiveEdgeTypeMap,
                    resolvedGroupId,
                    edgeTypes,
                    nodes,
                    uuidMap,
                    strictEdgeTypes: false,
                    customExtractionInstructions,
                    entityTypes,
                    clients,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var entityEdges = resolvedEdges.Concat(invalidatedEdges).ToList();

            // Only new edges feed the summaries, so facts already in the graph are not repeated.
            var hydratedNodes = await ExtractAttributesFromNodesAsync(
                    clients,
                    nodes,
                    [episode],
                    previousEpisodes,
                    entityTypes,
                    newEdges,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var (episodicEdges, savedEpisode) = await ProcessEpisodeDataAsync(
                    [episode],
                    hydratedNodes,
                    entityEdges,
                    now,
                    resolvedGroupId,
                    saga,
                    sagaPreviousEpisodeUuid,
                    nodeEpisodeIndexMap,
                    clients,
                    cancellationToken)
                .ConfigureAwait(false);

            List<CommunityNode> communities = [];
            List<CommunityEdge> communityEdges = [];
            if (updateCommunities)
            {
                // Upstream unpacks the gathered per-node tuples into two names, which only works for exactly two
                // nodes; here the per-node results are concatenated.
                var updates = await Concurrency.SemaphoreGatherAsync(
                        nodes.Select<EntityNode, Func<Task<(IReadOnlyList<CommunityNode>, IReadOnlyList<CommunityEdge>)>>>(
                            node => () => CommunityOperations.UpdateCommunityAsync(clients, node, cancellationToken)),
                        MaxCoroutines,
                        cancellationToken)
                    .ConfigureAwait(false);
                communities = updates.SelectMany(update => update.Item1).ToList();
                communityEdges = updates.SelectMany(update => update.Item2).ToList();
            }

            var durationMs = ElapsedMilliseconds(start);
            if (activity is not null)
            {
                activity.SetTag("episode.uuid", savedEpisode.Uuid);
                activity.SetTag("episode.source", source.ToStorageName());
                activity.SetTag("episode.reference_time", referenceTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                activity.SetTag("group_id", resolvedGroupId);
                activity.SetTag("node.count", hydratedNodes.Count);
                activity.SetTag("edge.count", entityEdges.Count);
                activity.SetTag("edge.invalidated_count", invalidatedEdges.Count);
                activity.SetTag("previous_episodes.count", previousEpisodes.Count);
                activity.SetTag("entity_types.count", entityTypes?.Count ?? 0);
                activity.SetTag("edge_types.count", edgeTypes?.Count ?? 0);
                activity.SetTag("update_communities", updateCommunities);
                activity.SetTag("communities.count", updateCommunities ? communities.Count : 0);
                activity.SetTag("duration_ms", durationMs);
            }

            GraphitiLog.Completed(Logger, "add_episode", durationMs);

            return new AddEpisodeResults
            {
                Episode = savedEpisode,
                EpisodicEdges = episodicEdges,
                Nodes = hydratedNodes,
                Edges = entityEdges,
                Communities = communities,
                CommunityEdges = communityEdges,
                TokenUsage = usage.GetTotalUsage(),
                TokenUsageByPrompt = usage.GetUsage(),
            };
        }
        catch (Exception e)
        {
            RecordError(activity, e);
            throw;
        }
    }

    /// <summary>
    /// Processes many episodes together (upstream <c>add_episode_bulk</c>): extracts from all of them in parallel,
    /// deduplicates within the batch, then resolves the batch against the graph in one pass. Much faster for
    /// backfills, but episodes in one batch do not see each other's facts as context.
    /// </summary>
    /// <remarks>
    /// As upstream, bulk ingestion keeps episode content even when <see cref="StoreRawEpisodeContent"/> is
    /// <see langword="false"/>, does not update communities, and saves the episodes first.
    /// </remarks>
    /// <param name="bulkEpisodes">The episodes. An episode with a <see cref="RawEpisode.Uuid"/> is read from storage
    /// and must belong to the group (<see cref="ArgumentException"/> otherwise).</param>
    /// <param name="groupId">The graph partition; <see langword="null"/> means the default group.</param>
    /// <param name="entityTypes">Custom entity types.</param>
    /// <param name="excludedEntityTypes">Entity types to leave out.</param>
    /// <param name="edgeTypes">Custom fact types.</param>
    /// <param name="edgeTypeMap">Which fact types may join which entity types.</param>
    /// <param name="strictEdgeTypes">Discard facts that do not use one of <paramref name="edgeTypes"/>.</param>
    /// <param name="customExtractionInstructions">Extra instructions for the extraction prompts.</param>
    /// <param name="saga">The saga to add every episode to, chained by reference time.</param>
    /// <param name="useCombinedExtraction">One combined entity-and-fact LLM call per episode (the default), or
    /// separate calls.</param>
    /// <param name="cancellationToken">Cancels between steps.</param>
    /// <returns>What was added or changed, with this call's token usage.</returns>
    public async Task<AddBulkEpisodeResults> AddEpisodeBulkAsync(
        IReadOnlyList<RawEpisode> bulkEpisodes,
        string? groupId = null,
        IReadOnlyDictionary<string, Type>? entityTypes = null,
        IReadOnlyList<string>? excludedEntityTypes = null,
        IReadOnlyDictionary<string, Type>? edgeTypes = null,
        IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>>? edgeTypeMap = null,
        bool strictEdgeTypes = false,
        string? customExtractionInstructions = null,
        SagaReference? saga = null,
        bool useCombinedExtraction = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bulkEpisodes);
        using var activity = StartActivity("add_episode_bulk");
        activity?.SetTag("episode.count", bulkEpisodes.Count);

        try
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = DateTimeUtils.UtcNow();

            var (resolvedGroupId, driver, clients) = ResolveRequestScope(groupId);
            await using var groupLock = await GroupLock.AcquireAsync(resolvedGroupId, cancellationToken)
                .ConfigureAwait(false);
            var usage = new TokenUsageTracker();
            using var operation = BeginOperation("add_episode_bulk", resolvedGroupId, usage);

            var edgeTypeMapDefault = DefaultEdgeTypeMap(edgeTypes);
            var effectiveEdgeTypeMap = edgeTypeMap is { Count: > 0 } ? edgeTypeMap : edgeTypeMapDefault;

            var episodes = new List<EpisodicNode>();
            foreach (var raw in bulkEpisodes)
            {
                EpisodicNode? stored = null;
                if (raw.Uuid is not null)
                {
                    stored = await driver.EpisodeNodeOps.GetByUuidAsync(raw.Uuid, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureEpisodeInGroup(raw.Uuid, stored.GroupId, resolvedGroupId);
                }

                episodes.Add(stored ?? new EpisodicNode
                {
                    Name = raw.Name,
                    Labels = [],
                    Source = raw.Source,
                    Content = raw.Content,
                    SourceDescription = raw.SourceDescription,
                    GroupId = resolvedGroupId,
                    CreatedAt = now,
                    ValidAt = raw.ReferenceTime,
                    EpisodeMetadata = raw.EpisodeMetadata,
                });
            }

            // Save all episodes.
            await AddNodesAndEdgesBulkAsync(driver, episodes, [], [], [], clients.Embedder, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var episodeContext = await RetrievePreviousEpisodesBulkAsync(driver, episodes, cancellationToken)
                .ConfigureAwait(false);

            var (nodesByEpisode, uuidMap, extractedEdgesBulk) = await ExtractAndDedupeNodesBulkAsync(
                    episodeContext,
                    effectiveEdgeTypeMap,
                    edgeTypes,
                    entityTypes,
                    excludedEntityTypes,
                    strictEdgeTypes,
                    customExtractionInstructions,
                    useCombinedExtraction,
                    clients,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var episodicEdges = new List<EpisodicEdge>();
            foreach (var (episodeUuid, nodes) in nodesByEpisode)
            {
                episodicEdges.AddRange(BuildEpisodicEdges(nodes, [episodeUuid], now, null));
            }

            var extractedEdgesBulkUpdated = extractedEdgesBulk
                .Select(edges => (IReadOnlyList<EntityEdge>)ResolveEdgePointers(edges, uuidMap))
                .ToList();

            var edgesByEpisode = await DedupeEdgesBulkAsync(
                    clients,
                    extractedEdgesBulkUpdated,
                    episodeContext,
                    [],
                    edgeTypes ?? new Dictionary<string, Type>(),
                    effectiveEdgeTypeMap,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var (finalHydratedNodes, resolvedEdges, invalidatedEdges, finalUuidMap) =
                await ResolveNodesAndEdgesBulkAsync(
                        nodesByEpisode,
                        edgesByEpisode,
                        episodeContext,
                        entityTypes,
                        edgeTypes,
                        effectiveEdgeTypeMap,
                        episodes,
                        clients,
                        cancellationToken)
                    .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedEpisodicEdges = ResolveEdgePointers(episodicEdges, finalUuidMap);

            var edgesToSave = resolvedEdges.Concat(invalidatedEdges).ToList();
            foreach (var episode in episodes)
            {
                var uuids = edgesToSave.Where(edge => edge.Episodes.Contains(episode.Uuid))
                    .Select(edge => edge.Uuid)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                episode.EntityEdges.Clear();
                episode.EntityEdges.AddRange(uuids);
            }

            await AddNodesAndEdgesBulkAsync(
                    driver,
                    episodes,
                    resolvedEpisodicEdges,
                    finalHydratedNodes,
                    edgesToSave,
                    clients.Embedder,
                    cancellationToken)
                .ConfigureAwait(false);

            if (saga is not null)
            {
                SagaNode sagaNode;
                if (saga.Name is not null)
                {
                    // Anchor a new saga to the earliest reference time in the batch.
                    var sagaCreatedAt = episodes.Count > 0 ? episodes.Min(episode => episode.ValidAt) : now;
                    sagaNode = await GetOrCreateSagaAsync(saga.Name, resolvedGroupId, sagaCreatedAt, driver,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    sagaNode = saga.Node!;
                }

                var sortedEpisodes = episodes.OrderBy(episode => episode.ValidAt).ToList();
                var previousEpisodeUuid = await SagaGetPreviousEpisodeUuidAsync(sagaNode.Uuid, "", driver,
                    cancellationToken).ConfigureAwait(false);

                foreach (var episode in sortedEpisodes)
                {
                    if (previousEpisodeUuid is not null)
                    {
                        await driver.NextEpisodeEdgeOps.SaveAsync(
                                new NextEpisodeEdge
                                {
                                    SourceNodeUuid = previousEpisodeUuid,
                                    TargetNodeUuid = episode.Uuid,
                                    GroupId = resolvedGroupId,
                                    CreatedAt = now,
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await driver.HasEpisodeEdgeOps.SaveAsync(
                            new HasEpisodeEdge
                            {
                                SourceNodeUuid = sagaNode.Uuid,
                                TargetNodeUuid = episode.Uuid,
                                GroupId = resolvedGroupId,
                                CreatedAt = now,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    previousEpisodeUuid = episode.Uuid;
                }

                if (sortedEpisodes.Count > 0)
                {
                    sagaNode.FirstEpisodeUuid ??= sortedEpisodes[0].Uuid;
                    sagaNode.LastEpisodeUuid = sortedEpisodes[^1].Uuid;
                    await driver.SagaNodeOps.SaveAsync(sagaNode, cancellationToken).ConfigureAwait(false);
                }
            }

            var durationMs = ElapsedMilliseconds(start);
            activity?.SetTag("group_id", resolvedGroupId);
            activity?.SetTag("node.count", finalHydratedNodes.Count);
            activity?.SetTag("edge.count", edgesToSave.Count);
            activity?.SetTag("duration_ms", durationMs);
            GraphitiLog.Completed(Logger, "add_episode_bulk", durationMs);

            return new AddBulkEpisodeResults
            {
                Episodes = episodes,
                EpisodicEdges = resolvedEpisodicEdges,
                Nodes = finalHydratedNodes,
                Edges = edgesToSave,
                Communities = [],
                CommunityEdges = [],
                TokenUsage = usage.GetTotalUsage(),
                TokenUsageByPrompt = usage.GetUsage(),
            };
        }
        catch (Exception e)
        {
            RecordError(activity, e);
            throw;
        }
    }

    /// <summary>
    /// Adds one fact between two entities directly, without extraction (upstream <c>add_triplet</c>). Entities that
    /// exist (by id) are reused and updated with the given attributes, summary and labels; new ones are resolved
    /// against the graph. The fact is deduplicated against facts between the same two entities and may invalidate
    /// contradicted facts, as in <see cref="AddEpisodeAsync"/>.
    /// </summary>
    /// <remarks>Upstream changes <paramref name="edge"/>'s id in place when the id already belongs to a fact between
    /// other entities; ids are immutable here, so a copy with a new id is saved instead and returned in
    /// <see cref="AddTripletResults.Edges"/>.</remarks>
    /// <param name="sourceNode">The source entity.</param>
    /// <param name="edge">The fact; its endpoints are set to the resolved entities.</param>
    /// <param name="targetNode">The target entity.</param>
    /// <param name="episode">The episode the fact comes from, used for deduplication and dating; by default an empty
    /// text episode at the fact's valid time.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The saved entities and facts.</returns>
    public async Task<AddTripletResults> AddTripletAsync(
        EntityNode sourceNode,
        EntityEdge edge,
        EntityNode targetNode,
        EpisodicNode? episode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceNode);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(targetNode);
        Validation.ValidateGroupId(edge.GroupId);

        // graphiti-net keeps every write inside one group: upstream would link entities across groups here.
        if (sourceNode.GroupId != edge.GroupId || targetNode.GroupId != edge.GroupId)
        {
            throw new ArgumentException(
                $"The source and target entities must be in the fact's group '{edge.GroupId}'.", nameof(edge));
        }

        await using var groupLock = await GroupLock.AcquireAsync(edge.GroupId, cancellationToken)
            .ConfigureAwait(false);
        using var operation = BeginOperation("add_triplet", edge.GroupId, episode: episode);

        sourceNode.NameEmbedding ??= await LLMUtils
            .GenerateEmbeddingAsync(Clients.Embedder, sourceNode.Name, cancellationToken).ConfigureAwait(false);
        targetNode.NameEmbedding ??= await LLMUtils
            .GenerateEmbeddingAsync(Clients.Embedder, targetNode.Name, cancellationToken).ConfigureAwait(false);
        edge.FactEmbedding ??= await LLMUtils
            .GenerateEmbeddingAsync(Clients.Embedder, edge.Fact, cancellationToken).ConfigureAwait(false);

        var resolvedSource = await GetOrResolveNodeAsync(sourceNode, cancellationToken).ConfigureAwait(false);
        var resolvedTarget = await GetOrResolveNodeAsync(targetNode, cancellationToken).ConfigureAwait(false);
        List<EntityNode> nodes = [resolvedSource, resolvedTarget];

        MergeUserProperties(sourceNode, resolvedSource);
        MergeUserProperties(targetNode, resolvedTarget);

        edge.SourceNodeUuid = resolvedSource.Uuid;
        edge.TargetNodeUuid = resolvedTarget.Uuid;

        try
        {
            var existingEdge = await Driver.EntityEdgeOps.GetByUuidAsync(edge.Uuid, cancellationToken)
                .ConfigureAwait(false);
            if (existingEdge.SourceNodeUuid != edge.SourceNodeUuid
                || existingEdge.TargetNodeUuid != edge.TargetNodeUuid)
            {
                var oldUuid = edge.Uuid;
                edge = CopyWithNewUuid(edge);
                GraphitiLog.EdgeUuidRegenerated(Logger, oldUuid, edge.Uuid);
            }
        }
        catch (EdgeNotFoundException)
        {
            // A new fact.
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Duplicate candidates: facts between the same two entities, either direction; then other facts as
        // invalidation candidates, without the duplicates. Mirrors resolve_extracted_edges.
        var pairResults = await SearchEngine.SearchAsync(
                Driver,
                Clients.Embedder,
                Clients.CrossEncoder,
                edge.Fact,
                [edge.GroupId],
                SearchConfigRecipes.EdgeHybridSearchRrf,
                PairSearchFilter(edge),
                queryVector: edge.FactEmbedding,
                maxCoroutines: MaxCoroutines,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var relatedEdges = SamePairEdges(edge, pairResults.Edges);

        var broadResults = await SearchEngine.SearchAsync(
                Driver,
                Clients.Embedder,
                Clients.CrossEncoder,
                edge.Fact,
                [edge.GroupId],
                SearchConfigRecipes.EdgeHybridSearchRrf,
                new SearchFilters(),
                queryVector: edge.FactEmbedding,
                maxCoroutines: MaxCoroutines,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var relatedUuids = relatedEdges.Select(related => related.Uuid).ToHashSet(StringComparer.Ordinal);
        var existingEdges = broadResults.Edges.Where(candidate => !relatedUuids.Contains(candidate.Uuid)).ToList();

        var dedupeEpisode = episode ?? new EpisodicNode
        {
            Name = "",
            Source = EpisodeType.Text,
            SourceDescription = "",
            Content = "",
            ValidAt = edge.ValidAt ?? DateTimeUtils.UtcNow(),
            EntityEdges = [],
            GroupId = edge.GroupId,
        };

        var (resolvedEdge, invalidatedEdges) = await ResolveExtractedEdgeAsync(
                Clients,
                edge,
                relatedEdges,
                existingEdges,
                dedupeEpisode,
                cancellationToken)
            .ConfigureAwait(false);

        List<EntityEdge> edges = [resolvedEdge, .. invalidatedEdges];

        await CreateEntityEdgeEmbeddingsAsync(Clients.Embedder, edges, cancellationToken).ConfigureAwait(false);
        await CreateEntityNodeEmbeddingsAsync(Clients.Embedder, nodes, cancellationToken).ConfigureAwait(false);

        await AddNodesAndEdgesBulkAsync(Driver, [], [], nodes, edges, Clients.Embedder, cancellationToken)
            .ConfigureAwait(false);
        return new AddTripletResults { Edges = edges, Nodes = nodes };
    }

    /// <summary>
    /// Removes an episode and what only it produced (upstream <c>remove_episode</c>): facts no other episode states
    /// are deleted, facts other episodes also state lose this episode, entities only this episode mentions are
    /// deleted, and the summaries of the other entities it mentions are rebuilt from their remaining episodes.
    /// </summary>
    /// <remarks>
    /// <para>A failed summary rebuild is logged and leaves the entity's stored summary as it was, as upstream.</para>
    /// <para><b>Strict mode (graphiti-net addition)</b>, for erasure: no text from the removed episode is left in
    /// the graph. In addition to the default removal:</para>
    /// <list type="bullet">
    /// <item>every fact that lists the episode is deleted, including facts other episodes also state (their merged
    /// text may carry the removed episode's details; re-deriving them would need extraction). Facts listing the
    /// episode are found through the episode's fact ids and the facts of every entity it mentions;</item>
    /// <item>the custom attributes of the surviving mentioned entities are cleared (they are re-extracted the next
    /// time an episode mentions the entity), and their summaries are rebuilt from the remaining episodes; if a
    /// rebuild fails, the summary is cleared instead of kept;</item>
    /// <item>communities with any mentioned entity as a member are deleted (their summaries aggregate member
    /// summaries; <see cref="BuildCommunitiesAsync"/> recreates them);</item>
    /// <item>sagas the episode belongs to get their summary and watermarks reset, so the next
    /// <see cref="SummarizeSagaAsync"/> rebuilds the summary from the remaining episodes, and their first and last
    /// episode pointers are recomputed if they named the removed episode.</item>
    /// </list>
    /// <para>Not changed: the validity dates the episode set on facts it invalidated (they are dates, not
    /// text).</para>
    /// </remarks>
    /// <param name="episodeUuid">The episode.</param>
    /// <param name="strict">Also delete or rebuild everything the episode contributed to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="NodeNotFoundException">No episode has this id.</exception>
    public async Task RemoveEpisodeAsync(
        string episodeUuid,
        bool strict = false,
        CancellationToken cancellationToken = default)
    {
        var found = await Driver.EpisodeNodeOps.GetByUuidAsync(episodeUuid, cancellationToken).ConfigureAwait(false);
        await using var groupLock = await GroupLock.AcquireAsync(found.GroupId, cancellationToken)
            .ConfigureAwait(false);

        // Read again under the lock, in case a write changed it meanwhile.
        var episode = await Driver.EpisodeNodeOps.GetByUuidAsync(episodeUuid, cancellationToken).ConfigureAwait(false);
        using var operation = BeginOperation(strict ? "remove_episode_strict" : "remove_episode", episode.GroupId,
            episode: episode);

        var edges = (await Driver.EntityEdgeOps.GetByUuidsAsync(episode.EntityEdges, cancellationToken)
            .ConfigureAwait(false)).ToList();

        var nodes = await SearchUtils.GetMentionedNodesAsync(Driver, [episode], cancellationToken)
            .ConfigureAwait(false);

        if (strict)
        {
            var known = edges.Select(edge => edge.Uuid).ToHashSet(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                foreach (var edge in await Driver.EntityEdgeOps.GetByNodeUuidAsync(node.Uuid, cancellationToken)
                             .ConfigureAwait(false))
                {
                    if (edge.Episodes.Contains(episode.Uuid) && known.Add(edge.Uuid))
                    {
                        edges.Add(edge);
                    }
                }
            }
        }

        var edgesToDelete = new List<EntityEdge>();
        var edgesToUpdate = new List<EntityEdge>();
        foreach (var edge in edges)
        {
            if (!edge.Episodes.Contains(episode.Uuid))
            {
                continue;
            }

            edge.Episodes.RemoveAll(uuid => uuid == episode.Uuid);
            if (edge.Episodes.Count > 0 && !strict)
            {
                edgesToUpdate.Add(edge);
            }
            else
            {
                edgesToDelete.Add(edge);
            }
        }

        // Entities only this episode mentions are deleted.
        var mentionCounts = await Driver.GraphOps
            .CountEpisodeMentionsAsync(nodes.Select(node => node.Uuid).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var nodesToDelete = nodes.Where(node => mentionCounts.GetValueOrDefault(node.Uuid) == 1).ToList();
        var deletedNodeUuids = nodesToDelete.Select(node => node.Uuid).ToHashSet(StringComparer.Ordinal);
        var survivingNodes = nodes.Where(node => !deletedNodeUuids.Contains(node.Uuid)).ToList();

        IReadOnlyList<CommunityNode> communitiesToDelete = [];
        List<SagaNode> sagasToReset = [];
        if (strict)
        {
            communitiesToDelete = await Driver.GraphOps.GetCommunitiesByNodesAsync(nodes, cancellationToken)
                .ConfigureAwait(false);
            sagasToReset = await GetSagasOfEpisodeAsync(episode, cancellationToken).ConfigureAwait(false);
        }

        foreach (var edge in edgesToUpdate)
        {
            await Driver.EntityEdgeOps.LoadEmbeddingsAsync(edge, cancellationToken).ConfigureAwait(false);
            await Driver.EntityEdgeOps.SaveAsync(edge, cancellationToken).ConfigureAwait(false);
        }

        await Driver.EntityEdgeOps.DeleteByUuidsAsync(edgesToDelete.Select(edge => edge.Uuid).ToList(),
            cancellationToken).ConfigureAwait(false);
        await Driver.EntityNodeOps.DeleteByUuidsAsync(nodesToDelete.Select(node => node.Uuid).ToList(),
            cancellationToken).ConfigureAwait(false);
        await Driver.EpisodeNodeOps.DeleteAsync(episode, cancellationToken).ConfigureAwait(false);

        if (strict)
        {
            await Driver.CommunityNodeOps.DeleteByUuidsAsync(
                    communitiesToDelete.Select(community => community.Uuid).ToList(),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var saga in sagasToReset)
            {
                await ResetSagaAfterRemovalAsync(saga, episode.Uuid, cancellationToken).ConfigureAwait(false);
            }
        }

        await Concurrency.SemaphoreGatherAsync(
                survivingNodes.Select<EntityNode, Func<Task>>(node =>
                    () => RebuildNodeSummaryAsync(node, episode.Uuid, strict, cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RebuildNodeSummaryAsync(
        EntityNode node,
        string removedEpisodeUuid,
        bool strict,
        CancellationToken cancellationToken)
    {
        var oldSummary = node.Summary;
        if (strict)
        {
            node.Attributes.Clear();
        }

        try
        {
            var episodes = await Driver.EpisodeNodeOps.GetByEntityNodeUuidAsync(node.Uuid, cancellationToken)
                .ConfigureAwait(false);
            var remaining = episodes
                .Where(remainingEpisode => remainingEpisode.Uuid != removedEpisodeUuid)
                .OrderBy(remainingEpisode => remainingEpisode.ValidAt)
                .ToList();
            node.Summary = "";
            for (var batchStart = 0; batchStart < remaining.Count; batchStart += SummaryRebuildBatch)
            {
                var batch = remaining.Skip(batchStart).Take(SummaryRebuildBatch).ToList();
                await ExtractEntitySummariesBatchAsync(Clients, [node], batch, cancellationToken)
                    .ConfigureAwait(false);
            }

            await Driver.EntityNodeOps.LoadEmbeddingsAsync(node, cancellationToken).ConfigureAwait(false);
            await Driver.EntityNodeOps.SaveAsync(node, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GraphitiLog.SummaryRebuildFailed(Logger, e, node.Uuid, removedEpisodeUuid);
            if (!strict)
            {
                // As upstream: the stored summary stays as it was.
                node.Summary = oldSummary;
                return;
            }

            // Strict: nothing from the removed episode may survive, so drop the summary rather than keep it.
            node.Summary = "";
            await Driver.EntityNodeOps.LoadEmbeddingsAsync(node, cancellationToken).ConfigureAwait(false);
            await Driver.EntityNodeOps.SaveAsync(node, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<SagaNode>> GetSagasOfEpisodeAsync(EpisodicNode episode, CancellationToken cancellationToken)
    {
        var links = await Driver.HasEpisodeEdgeOps.GetByGroupIdsAsync([episode.GroupId],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var sagaUuids = links.Where(link => link.TargetNodeUuid == episode.Uuid)
            .Select(link => link.SourceNodeUuid)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return (await Driver.SagaNodeOps.GetByUuidsAsync(sagaUuids, cancellationToken).ConfigureAwait(false)).ToList();
    }

    private async Task ResetSagaAfterRemovalAsync(SagaNode saga, string removedEpisodeUuid,
        CancellationToken cancellationToken)
    {
        saga.Summary = "";
        saga.LastSummarizedAt = null;
        saga.LastSummarizedEpisodeValidAt = null;
        if (saga.FirstEpisodeUuid == removedEpisodeUuid || saga.LastEpisodeUuid == removedEpisodeUuid)
        {
            var remaining = await Driver.EpisodeNodeOps.GetBySagaAsync(saga.Uuid, null, cancellationToken)
                .ConfigureAwait(false);
            var ordered = remaining.Where(e => e.Uuid != removedEpisodeUuid)
                .OrderBy(e => e.ValidAt)
                .ThenBy(e => e.CreatedAt)
                .ToList();
            saga.FirstEpisodeUuid = ordered.Count > 0 ? ordered[0].Uuid : null;
            saga.LastEpisodeUuid = ordered.Count > 0 ? ordered[^1].Uuid : null;
        }

        await Driver.SagaNodeOps.SaveAsync(saga, cancellationToken).ConfigureAwait(false);
    }

    // _process_episode_data: link the episodes to their entities, save everything, and join the saga.
    private async Task<(IReadOnlyList<EpisodicEdge> EpisodicEdges, EpisodicNode Episode)> ProcessEpisodeDataAsync(
        IReadOnlyList<EpisodicNode> episodes,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EntityEdge> entityEdges,
        DateTimeOffset now,
        string groupId,
        SagaReference? saga,
        string? sagaPreviousEpisodeUuid,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? nodeEpisodeIndexMap,
        GraphitiClients clients,
        CancellationToken cancellationToken)
    {
        var driver = clients.Driver;
        var episodeUuids = episodes.Select(episode => episode.Uuid).ToList();

        var episodicEdges = BuildEpisodicEdges(nodes, episodeUuids, now, nodeEpisodeIndexMap);
        foreach (var episode in episodes)
        {
            episode.EntityEdges.Clear();
            episode.EntityEdges.AddRange(entityEdges.Select(edge => edge.Uuid));
            if (!StoreRawEpisodeContent)
            {
                episode.Content = "";
            }
        }

        await AddNodesAndEdgesBulkAsync(driver, episodes, episodicEdges, nodes, entityEdges, clients.Embedder,
            cancellationToken).ConfigureAwait(false);

        var primaryEpisode = episodes[0];
        if (saga is not null)
        {
            var sagaNode = saga.Name is not null
                // A new saga is stamped with the reference time of the episode that created it.
                ? await GetOrCreateSagaAsync(saga.Name, groupId, primaryEpisode.ValidAt, driver, cancellationToken)
                    .ConfigureAwait(false)
                : saga.Node!;

            var previousEpisodeUuid = sagaPreviousEpisodeUuid
                                      ?? await SagaGetPreviousEpisodeUuidAsync(sagaNode.Uuid, primaryEpisode.Uuid,
                                          driver, cancellationToken).ConfigureAwait(false);

            if (previousEpisodeUuid is not null)
            {
                await driver.NextEpisodeEdgeOps.SaveAsync(
                        new NextEpisodeEdge
                        {
                            SourceNodeUuid = previousEpisodeUuid,
                            TargetNodeUuid = primaryEpisode.Uuid,
                            GroupId = groupId,
                            CreatedAt = now,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await driver.HasEpisodeEdgeOps.SaveAsync(
                    new HasEpisodeEdge
                    {
                        SourceNodeUuid = sagaNode.Uuid,
                        TargetNodeUuid = primaryEpisode.Uuid,
                        GroupId = groupId,
                        CreatedAt = now,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            sagaNode.FirstEpisodeUuid ??= primaryEpisode.Uuid;
            sagaNode.LastEpisodeUuid = primaryEpisode.Uuid;
            await driver.SagaNodeOps.SaveAsync(sagaNode, cancellationToken).ConfigureAwait(false);
        }

        return (episodicEdges, primaryEpisode);
    }

    // _extract_and_resolve_edges.
    private static async Task<(IReadOnlyList<EntityEdge> Resolved, IReadOnlyList<EntityEdge> Invalidated,
            IReadOnlyList<EntityEdge> New)>
        ExtractAndResolveEdgesAsync(
            IReadOnlyList<EpisodicNode> episodes,
            IReadOnlyList<EntityNode> extractedNodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            string groupId,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            List<EntityNode> nodes,
            Dictionary<string, string> uuidMap,
            bool strictEdgeTypes,
            string? customExtractionInstructions,
            IReadOnlyDictionary<string, Type>? entityTypes,
            GraphitiClients clients,
            CancellationToken cancellationToken)
    {
        var primaryEpisode = episodes[0];

        var (extractedEdges, materializedNodes) = await ExtractEdgesAsync(
                clients,
                episodes,
                extractedNodes,
                previousEpisodes,
                edgeTypeMap,
                groupId,
                edgeTypes,
                strictEdgeTypes,
                customExtractionInstructions,
                cancellationToken)
            .ConfigureAwait(false);

        // Merge only the endpoints extract_edges newly materialised, never the full pre-resolution list, whose
        // uuid-map remaps must survive for ResolveEdgePointers.
        if (materializedNodes.Count > 0)
        {
            await MergeMaterializedEndpointNodesAsync(
                    materializedNodes,
                    primaryEpisode,
                    previousEpisodes,
                    entityTypes,
                    nodes,
                    uuidMap,
                    clients,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var edges = ResolveEdgePointers(extractedEdges, uuidMap);

        return await ResolveExtractedEdgesAsync(
                clients,
                edges,
                primaryEpisode,
                nodes,
                edgeTypes ?? new Dictionary<string, Type>(),
                edgeTypeMap,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // _merge_materialized_endpoint_nodes: endpoints extract_edges created never went through node extraction, so
    // they are deduplicated here; nodes resolved for this episode are not saved yet, so they are matched by name
    // first. `nodes` and `uuidMap` are updated in place.
    private static async Task MergeMaterializedEndpointNodesAsync(
        IReadOnlyList<EntityNode> materializedNodes,
        EpisodicNode episode,
        IReadOnlyList<EpisodicNode> previousEpisodes,
        IReadOnlyDictionary<string, Type>? entityTypes,
        List<EntityNode> nodes,
        Dictionary<string, string> uuidMap,
        GraphitiClients clients,
        CancellationToken cancellationToken)
    {
        var nodesByName = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            nodesByName[DedupHelpers.NormalizeStringExact(node.Name)] = node;
        }

        var knownUuids = nodes.Select(node => node.Uuid).ToHashSet(StringComparer.Ordinal);

        var unresolved = new List<EntityNode>();
        foreach (var materializedNode in materializedNodes)
        {
            if (knownUuids.Contains(materializedNode.Uuid))
            {
                continue;
            }

            if (nodesByName.TryGetValue(DedupHelpers.NormalizeStringExact(materializedNode.Name), out var existing))
            {
                uuidMap[materializedNode.Uuid] = existing.Uuid;
                continue;
            }

            unresolved.Add(materializedNode);
        }

        if (unresolved.Count == 0)
        {
            return;
        }

        var (resolvedNodes, materializedUuidMap, _) = await ResolveExtractedNodesAsync(
                clients,
                unresolved,
                episode,
                previousEpisodes,
                entityTypes,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var (key, value) in materializedUuidMap)
        {
            uuidMap[key] = value;
        }

        foreach (var resolvedNode in resolvedNodes)
        {
            if (knownUuids.Add(resolvedNode.Uuid))
            {
                nodes.Add(resolvedNode);
            }
        }
    }

    // _extract_and_dedupe_nodes_bulk.
    private static async Task<(IReadOnlyDictionary<string, IReadOnlyList<EntityNode>> NodesByEpisode,
            IReadOnlyDictionary<string, string> UuidMap, IReadOnlyList<IReadOnlyList<EntityEdge>> ExtractedEdges)>
        ExtractAndDedupeNodesBulkAsync(
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeContext,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyList<string>? excludedEntityTypes,
            bool strictEdgeTypes,
            string? customExtractionInstructions,
            bool useCombinedExtraction,
            GraphitiClients clients,
            CancellationToken cancellationToken)
    {
        var (extractedNodesBulk, extractedEdgesBulk) = await ExtractNodesAndEdgesBulkAsync(
                clients,
                episodeContext,
                edgeTypeMap,
                entityTypes,
                excludedEntityTypes,
                edgeTypes,
                customExtractionInstructions,
                useCombinedExtraction,
                strictEdgeTypes,
                cancellationToken)
            .ConfigureAwait(false);

        var (nodesByEpisode, uuidMap) = await DedupeNodesBulkAsync(
                clients,
                extractedNodesBulk,
                episodeContext,
                entityTypes,
                cancellationToken)
            .ConfigureAwait(false);

        return (nodesByEpisode, uuidMap, extractedEdgesBulk);
    }

    // _resolve_nodes_and_edges_bulk.
    private async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyList<EntityEdge> ResolvedEdges,
            IReadOnlyList<EntityEdge> InvalidatedEdges, IReadOnlyDictionary<string, string> UuidMap)>
        ResolveNodesAndEdgesBulkAsync(
            IReadOnlyDictionary<string, IReadOnlyList<EntityNode>> nodesByEpisode,
            IReadOnlyDictionary<string, IReadOnlyList<EntityEdge>> edgesByEpisode,
            IReadOnlyList<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes)> episodeContext,
            IReadOnlyDictionary<string, Type>? entityTypes,
            IReadOnlyDictionary<string, Type>? edgeTypes,
            IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> edgeTypeMap,
            IReadOnlyList<EpisodicNode> episodes,
            GraphitiClients clients,
            CancellationToken cancellationToken)
    {
        var nodesByUuid = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        foreach (var node in nodesByEpisode.Values.SelectMany(nodes => nodes))
        {
            nodesByUuid[node.Uuid] = node;
        }

        // Unique nodes per episode.
        var nodesByEpisodeUnique = new Dictionary<string, List<EntityNode>>(StringComparer.Ordinal);
        var nodeUuidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (episode, _) in episodeContext)
        {
            var unique = new List<EntityNode>();
            nodesByEpisodeUnique[episode.Uuid] = unique;
            foreach (var node in nodesByEpisode[episode.Uuid].Select(node => nodesByUuid[node.Uuid]))
            {
                if (nodeUuidSet.Add(node.Uuid))
                {
                    unique.Add(node);
                }
            }
        }

        // Resolve nodes.
        var nodeResults = await Concurrency.SemaphoreGatherAsync(
                episodeContext.Select<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes),
                    Func<Task<(IReadOnlyList<EntityNode>, IReadOnlyDictionary<string, string>,
                        IReadOnlyList<(EntityNode, EntityNode)>)>>>(context =>
                    () => ResolveExtractedNodesAsync(
                        clients,
                        nodesByEpisodeUnique[context.Episode.Uuid],
                        context.Episode,
                        context.PreviousEpisodes,
                        entityTypes,
                        cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var resolvedNodes = new List<EntityNode>();
        var uuidMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (nodes, map, _) in nodeResults)
        {
            resolvedNodes.AddRange(nodes);
            foreach (var (key, value) in map)
            {
                uuidMap[key] = value;
            }
        }

        foreach (var resolvedNode in resolvedNodes)
        {
            nodesByUuid[resolvedNode.Uuid] = resolvedNode;
        }

        foreach (var episodeUuid in nodesByEpisodeUnique.Keys.ToList())
        {
            nodesByEpisodeUnique[episodeUuid] = nodesByEpisodeUnique[episodeUuid]
                .Select(node => nodesByUuid[uuidMap.GetValueOrDefault(node.Uuid, node.Uuid)])
                .ToList();
        }

        // Attributes and summaries for the resolved nodes.
        var hydratedNodesResults = await Concurrency.SemaphoreGatherAsync(
                episodeContext.Select<(EpisodicNode Episode, IReadOnlyList<EpisodicNode> PreviousEpisodes),
                    Func<Task<IReadOnlyList<EntityNode>>>>(context =>
                    () => ExtractAttributesFromNodesAsync(
                        clients,
                        nodesByEpisodeUnique[context.Episode.Uuid],
                        [context.Episode],
                        context.PreviousEpisodes,
                        entityTypes,
                        null,
                        cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var finalHydratedNodes = hydratedNodesResults.SelectMany(nodes => nodes).ToList();

        // Resolve edges with updated pointers.
        var edgesByEpisodeUnique = new Dictionary<string, List<EntityEdge>>(StringComparer.Ordinal);
        var edgeUuidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (episodeUuid, edges) in edgesByEpisode)
        {
            var unique = new List<EntityEdge>();
            edgesByEpisodeUnique[episodeUuid] = unique;
            foreach (var edge in ResolveEdgePointers(edges, uuidMap))
            {
                if (edgeUuidSet.Add(edge.Uuid))
                {
                    unique.Add(edge);
                }
            }
        }

        var edgeResults = await Concurrency.SemaphoreGatherAsync(
                episodes.Select<EpisodicNode, Func<Task<(IReadOnlyList<EntityEdge> Resolved,
                    IReadOnlyList<EntityEdge> Invalidated, IReadOnlyList<EntityEdge> New)>>>(episode =>
                    () => ResolveExtractedEdgesAsync(
                        clients,
                        edgesByEpisodeUnique.GetValueOrDefault(episode.Uuid) ?? [],
                        episode,
                        finalHydratedNodes,
                        edgeTypes ?? new Dictionary<string, Type>(),
                        edgeTypeMap,
                        cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var resolvedEdges = edgeResults.SelectMany(result => result.Resolved).ToList();
        var invalidatedEdges = edgeResults.SelectMany(result => result.Invalidated).ToList();
        return (finalHydratedNodes, resolvedEdges, invalidatedEdges, uuidMap);
    }

    // graphiti-net: a stored episode is only (re)processed in its own group. Upstream would extract it with another
    // group's episodes as context and write its facts to that group.
    private static void EnsureEpisodeInGroup(string? uuid, string? storedGroupId, string groupId)
    {
        if (uuid is not null && !string.Equals(storedGroupId, groupId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Episode {uuid} belongs to group '{storedGroupId}', not '{groupId}'.", nameof(uuid));
        }
    }

    private async Task<EntityNode> GetOrResolveNodeAsync(EntityNode node, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await Driver.EntityNodeOps.GetByUuidAsync(node.Uuid, cancellationToken).ConfigureAwait(false);
            if (stored.GroupId != node.GroupId)
            {
                throw new ArgumentException(
                    $"Entity {node.Uuid} is stored in another group than '{node.GroupId}'.", nameof(node));
            }

            return stored;
        }
        catch (NodeNotFoundException)
        {
            var (resolved, _, _) = await ResolveExtractedNodesAsync(Clients, [node], null, null, null,
                cancellationToken).ConfigureAwait(false);
            return resolved[0];
        }
    }

    // add_triplet: the caller's attributes, summary and labels win over what is stored.
    private static void MergeUserProperties(EntityNode given, EntityNode resolved)
    {
        if (ReferenceEquals(given, resolved))
        {
            return;
        }

        foreach (var (key, value) in given.Attributes)
        {
            resolved.Attributes[key] = value;
        }

        if (!string.IsNullOrEmpty(given.Summary))
        {
            resolved.Summary = given.Summary;
        }

        if (given.Labels.Count > 0)
        {
            resolved.Labels = resolved.Labels.Union(given.Labels, StringComparer.Ordinal).ToList();
        }
    }

    private static EntityEdge CopyWithNewUuid(EntityEdge edge) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        GroupId = edge.GroupId,
        SourceNodeUuid = edge.SourceNodeUuid,
        TargetNodeUuid = edge.TargetNodeUuid,
        CreatedAt = edge.CreatedAt,
        Name = edge.Name,
        Fact = edge.Fact,
        FactEmbedding = edge.FactEmbedding,
        Episodes = [.. edge.Episodes],
        ExpiredAt = edge.ExpiredAt,
        ValidAt = edge.ValidAt,
        InvalidAt = edge.InvalidAt,
        ReferenceTime = edge.ReferenceTime,
        Attributes = new Dictionary<string, object?>(edge.Attributes),
    };

    private static Dictionary<(string Source, string Target), IReadOnlyList<string>> DefaultEdgeTypeMap(
        IReadOnlyDictionary<string, Type>? edgeTypes) =>
        new Dictionary<(string Source, string Target), IReadOnlyList<string>>
        {
            [("Entity", "Entity")] = edgeTypes is not null ? edgeTypes.Keys.ToList() : [],
        };

    // create_entity_node_embeddings: embeds every named node's name in one batch.
    private static async Task CreateEntityNodeEmbeddingsAsync(
        Embedder.EmbedderClient embedder,
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken)
    {
        var filtered = nodes.Where(node => !string.IsNullOrEmpty(node.Name)).ToList();
        if (filtered.Count == 0)
        {
            return;
        }

        var embeddings = await embedder.CreateBatchAsync(filtered.Select(node => node.Name).ToList(), cancellationToken)
            .ConfigureAwait(false);
        for (var i = 0; i < filtered.Count; i++)
        {
            filtered[i].NameEmbedding = embeddings[i];
        }
    }

    // create_entity_edge_embeddings: embeds every fact's text in one batch.
    private static async Task CreateEntityEdgeEmbeddingsAsync(
        Embedder.EmbedderClient embedder,
        IReadOnlyList<EntityEdge> edges,
        CancellationToken cancellationToken)
    {
        var filtered = edges.Where(edge => !string.IsNullOrEmpty(edge.Fact)).ToList();
        if (filtered.Count == 0)
        {
            return;
        }

        var embeddings = await embedder.CreateBatchAsync(filtered.Select(edge => edge.Fact).ToList(), cancellationToken)
            .ConfigureAwait(false);
        for (var i = 0; i < filtered.Count; i++)
        {
            filtered[i].FactEmbedding = embeddings[i];
        }
    }
}
