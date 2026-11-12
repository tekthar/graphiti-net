// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/node_operations.py and create_entity_node_embeddings in
// graphiti_core/nodes.py (Graphiti v0.31.2) and changed for .NET.

using System.Diagnostics;
using System.Text.Json;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Search;
using GraphitiNet.Utils;
using Microsoft.Extensions.Logging;

namespace GraphitiNet.Maintenance;

/// <summary>Decides whether an entity's summary should be (re)generated (upstream <c>NodeSummaryFilter</c>).</summary>
/// <param name="node">The entity.</param>
/// <returns><see langword="true"/> to summarise it.</returns>
public delegate Task<bool> NodeSummaryFilter(EntityNode node);

/// <summary>
/// The entity side of ingestion: extraction, deduplication, attribute extraction and summaries (upstream
/// <c>node_operations</c>).
/// </summary>
/// <remarks>
/// <para>Custom entity types are a map from type name to a class or record (see <c>docs/custom-types.md</c>). Where
/// upstream takes an episode or a list of episodes, these functions take a list: a single episode is a one-element
/// list, which renders exactly as upstream renders a single episode.</para>
/// <para>Entities never merge across groups. Every candidate search is scoped to the extracted node's group, as
/// upstream; in addition, <c>existingNodesOverride</c> candidates from another group are ignored and an LLM choice of
/// a candidate from another group is treated as "no duplicate" (upstream does not check either case).</para>
/// </remarks>
public static class NodeOperations
{
    /// <summary>Upstream <c>MAX_NODES</c>: the most entities summarised in one LLM call.</summary>
    public const int MaxNodes = 30;

    /// <summary>Upstream <c>NODE_DEDUP_CANDIDATE_LIMIT</c>: candidates per lookup.</summary>
    public const int NodeDedupCandidateLimit = 15;

    /// <summary>Upstream <c>NODE_DEDUP_COSINE_MIN_SCORE</c>: the similarity a semantic candidate needs.</summary>
    public const double NodeDedupCosineMinScore = 0.6;

    private const int DedupeNodesMaxTokens = 16384;

    private const string DefaultEntityTypeDescription =
        "A specific, identifiable entity that does not fit any of the other listed types. Must still be a concrete, " +
        "meaningful thing — specific enough to be uniquely identifiable. GOOD: a named entity not covered by the " +
        "other types. BAD: \"luck\", \"ideas\", \"tomorrow\", \"things\", \"them\", \"everybody\", \"a sense of " +
        "wonder\", \"great times\". When in doubt, do not extract the entity.";

    private const string EpisodeAttributionInstruction =
        "\n7. **Episode Attribution**: The content contains multiple episodes labeled [Episode 0], [Episode 1], etc. " +
        "Each episode header includes a timestamp indicating when that episode occurred. For each extracted entity, " +
        "set `episode_indices` to the 0-based list of episode numbers where that entity is mentioned. An entity " +
        "appearing in Episodes 0 and 2 should have `episode_indices: [0, 2]`.";

    // ------------------------------------------------------------------------------------------------------------
    // Extraction
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Extracts entities from one episode. See the list overload.</summary>
    public static Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, IReadOnlyList<int>> NodeEpisodeIndexMap)>
        ExtractNodesAsync(
            GraphitiClients clients,
            EpisodicNode episode,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            IReadOnlyList<string>? excludedEntityTypes = null,
            string? customExtractionInstructions = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return ExtractNodesAsync(
            clients, [episode], previousEpisodes, entityTypes, excludedEntityTypes, customExtractionInstructions,
            cancellationToken);
    }

    /// <summary>
    /// Extracts entities from one or more episodes with one LLM call (upstream <c>extract_nodes</c>). Several
    /// episodes are concatenated with <c>[Episode N]</c> headers; the first one supplies the source type, group and
    /// timestamp.
    /// </summary>
    /// <param name="clients">The services.</param>
    /// <param name="episodes">The episodes; at least one.</param>
    /// <param name="previousEpisodes">Earlier episodes, for context.</param>
    /// <param name="entityTypes">Custom entity types by name.</param>
    /// <param name="excludedEntityTypes">Type names (including <c>Entity</c>) not to extract.</param>
    /// <param name="customExtractionInstructions">Extra instructions for the prompt.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new, unresolved entities (exact same-name duplicates collapsed) and, for each, the 0-based indexes
    /// of the episodes it came from.</returns>
    public static async Task<(IReadOnlyList<EntityNode> Nodes, IReadOnlyDictionary<string, IReadOnlyList<int>> NodeEpisodeIndexMap)>
        ExtractNodesAsync(
            GraphitiClients clients,
            IReadOnlyList<EpisodicNode> episodes,
            IReadOnlyList<EpisodicNode> previousEpisodes,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            IReadOnlyList<string>? excludedEntityTypes = null,
            string? customExtractionInstructions = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(previousEpisodes);
        ArgumentOutOfRangeException.ThrowIfZero(episodes.Count, nameof(episodes));
        cancellationToken.ThrowIfCancellationRequested();
        var primaryEpisode = episodes[0];
        var start = Stopwatch.GetTimestamp();

        var entityTypesContext = FilterEntityTypesContext(BuildEntityTypesContext(entityTypes), excludedEntityTypes);
        if (entityTypesContext.Count == 0)
        {
            MaintenanceLog.NoEntityTypesAfterExclusions(clients.Logger);
            return ([], new Dictionary<string, IReadOnlyList<int>>());
        }

        var episodeAttribution = episodes.Count > 1 ? EpisodeAttributionInstruction : "";

        var context = new Dictionary<string, object?>
        {
            ["episode_content"] = TextUtils.ConcatenateEpisodes(episodes),
            ["episode_timestamp"] = PythonText.IsoFormat(primaryEpisode.ValidAt),
            ["previous_episodes"] = PreviousEpisodesContext(previousEpisodes),
            ["custom_extraction_instructions"] = (customExtractionInstructions ?? "") + episodeAttribution,
            ["entity_types"] = entityTypesContext,
            ["source_description"] = primaryEpisode.SourceDescription,
        };

        var extractedEntities = await ExtractNodesSingleAsync(clients, primaryEpisode, context, cancellationToken)
            .ConfigureAwait(false);

        var filteredEntities = extractedEntities.Where(e => e.Name.Trim().Length > 0).ToList();
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        MaintenanceLog.ExtractedEntities(clients.Logger, filteredEntities.Count, elapsedMs);

        var (extractedNodes, nodeEpisodeIndexMap) = CreateEntityNodes(
            filteredEntities, entityTypesContext, excludedEntityTypes, episodes);
        extractedNodes = CollapseExactDuplicateExtractedNodes(extractedNodes, nodeEpisodeIndexMap);

        return (extractedNodes, nodeEpisodeIndexMap.ToDictionary(
            e => e.Key, e => (IReadOnlyList<int>)e.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// The prompt's ENTITY TYPES: <c>Entity</c> as id 0, then each custom type in order, with its description
    /// (upstream <c>_build_entity_types_context</c>).
    /// </summary>
    internal static List<Dictionary<string, object?>> BuildEntityTypesContext(IReadOnlyDictionary<string, Type>? entityTypes)
    {
        var context = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["entity_type_id"] = 0,
                ["entity_type_name"] = DedupHelpers.EntityBaseLabel,
                ["entity_type_description"] = DefaultEntityTypeDescription,
            },
        };

        if (entityTypes is not null)
        {
            var i = 0;
            foreach (var (typeName, typeModel) in entityTypes)
            {
                context.Add(new Dictionary<string, object?>
                {
                    ["entity_type_id"] = i + 1,
                    ["entity_type_name"] = typeName,
                    ["entity_type_description"] = EntityTypesUtils.GetDescription(typeModel),
                });
                i++;
            }
        }

        return context;
    }

    /// <summary>Removes excluded types from the ENTITY TYPES context and renumbers the rest from 0 (upstream
    /// <c>_filter_entity_types_context</c>).</summary>
    internal static List<Dictionary<string, object?>> FilterEntityTypesContext(
        List<Dictionary<string, object?>> entityTypesContext,
        IReadOnlyList<string>? excludedEntityTypes)
    {
        if (excludedEntityTypes is null || excludedEntityTypes.Count == 0)
        {
            return entityTypesContext;
        }

        var excluded = excludedEntityTypes.ToHashSet(StringComparer.Ordinal);
        return entityTypesContext
            .Where(t => !(t.GetValueOrDefault("entity_type_name") is string name && excluded.Contains(name)))
            .Select((t, id) =>
            {
                var copy = new Dictionary<string, object?>(t) { ["entity_type_id"] = id };
                return copy;
            })
            .ToList();
    }

    /// <summary>The description of the node's first custom-type label, or <c>Default Entity Type</c> (upstream
    /// <c>_get_entity_type_description</c>).</summary>
    internal static string GetEntityTypeDescription(IReadOnlyList<string> labels, IReadOnlyDictionary<string, Type>? entityTypes)
    {
        var typeName = entityTypes is null ? null : labels.FirstOrDefault(entityTypes.ContainsKey);
        var typeModel = typeName is null ? null : entityTypes!.GetValueOrDefault(typeName);
        var description = typeModel is null ? null : EntityTypesUtils.GetDescription(typeModel);
        return string.IsNullOrEmpty(description) ? "Default Entity Type" : description;
    }

    /// <summary>
    /// A short type description for summary prompts: the first paragraph, at most three sentences (upstream
    /// <c>_truncate_type_description</c>). Drops GOOD/BAD examples meant for extraction.
    /// </summary>
    internal static string TruncateTypeDescription(string docstring)
    {
        var paragraphLines = new List<string>();
        foreach (var line in PythonSplitLines(docstring))
        {
            if (line.Trim().Length == 0)
            {
                if (paragraphLines.Count > 0)
                {
                    break;
                }

                continue;
            }

            paragraphLines.Add(line);
        }

        var remaining = string.Join(' ', paragraphLines.Select(l => l.Trim()));
        var sentences = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var idx = FindSentenceEnd(remaining);
            if (idx == -1)
            {
                sentences.Add(remaining);
                remaining = "";
                break;
            }

            sentences.Add(remaining[..(idx + 1)]);
            remaining = remaining[(idx + 1)..].TrimStart();
        }

        return string.Join(' ', sentences).Trim();
    }

    /// <summary>The index of the first sentence end: <c>.</c>, <c>!</c> or <c>?</c> at the end of the text or
    /// followed by a space and an uppercase letter; -1 if none (upstream <c>_find_sentence_end</c>).</summary>
    internal static int FindSentenceEnd(string text)
    {
        var n = text.Length;
        for (var i = 0; i < n; i++)
        {
            if (text[i] is not ('.' or '!' or '?'))
            {
                continue;
            }

            if (i + 1 >= n)
            {
                return i;
            }

            if (text[i + 1] == ' ' && i + 2 < n && char.IsUpper(text[i + 2]))
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task<IReadOnlyList<ExtractedEntity>> ExtractNodesSingleAsync(
        GraphitiClients clients,
        EpisodicNode episode,
        Dictionary<string, object?> context,
        CancellationToken cancellationToken)
    {
        var promptName = episode.Source switch
        {
            EpisodeType.Message => PromptNames.ExtractNodesExtractMessage,
            EpisodeType.Text => PromptNames.ExtractNodesExtractText,
            EpisodeType.Json => PromptNames.ExtractNodesExtractJson,
            _ => PromptNames.ExtractNodesExtractText,
        };

        var response = await clients.CompletePromptAsync<ExtractedEntities>(
            promptName, context, groupId: episode.GroupId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Entities;
    }

    /// <summary>
    /// Turns extracted entities into new nodes labelled <c>Entity</c> plus their type, skipping excluded types, and
    /// maps each to its episode indexes (out-of-range indexes dropped; none left means all episodes) (upstream
    /// <c>_create_entity_nodes</c>).
    /// </summary>
    /// <remarks>Upstream builds the labels from a Python set, so their order varies between runs; here it is always
    /// <c>Entity</c> first.</remarks>
    internal static (List<EntityNode> Nodes, Dictionary<string, List<int>> NodeEpisodeIndexMap) CreateEntityNodes(
        IReadOnlyList<ExtractedEntity> extractedEntities,
        IReadOnlyList<Dictionary<string, object?>> entityTypesContext,
        IReadOnlyList<string>? excludedEntityTypes,
        IReadOnlyList<EpisodicNode> episodes)
    {
        var primaryEpisode = episodes[0];
        var nodes = new List<EntityNode>();
        var indexMap = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var extracted in extractedEntities)
        {
            var typeId = extracted.EntityTypeId;
            var typeName = typeId >= 0 && typeId < entityTypesContext.Count
                ? PythonText.Str(entityTypesContext[typeId].GetValueOrDefault("entity_type_name"))
                : DedupHelpers.EntityBaseLabel;

            if (excludedEntityTypes is { Count: > 0 } && excludedEntityTypes.Contains(typeName))
            {
                continue;
            }

            List<string> labels = typeName == DedupHelpers.EntityBaseLabel
                ? [DedupHelpers.EntityBaseLabel]
                : [DedupHelpers.EntityBaseLabel, typeName];

            var node = new EntityNode
            {
                Name = extracted.Name,
                GroupId = primaryEpisode.GroupId,
                Labels = labels,
                Summary = "",
                CreatedAt = DateTimeUtils.UtcNow(),
            };
            nodes.Add(node);

            var indices = extracted.EpisodeIndices.Where(i => i >= 0 && i < episodes.Count).ToList();
            if (indices.Count == 0)
            {
                indices = [.. Enumerable.Range(0, episodes.Count)];
            }

            indexMap[node.Uuid] = indices;
        }

        return (nodes, indexMap);
    }

    /// <summary>
    /// Collapses extracted nodes with the same normalised name, keeping the one with more specific labels (or, on a
    /// tie, the longer name) and merging episode indexes into it (upstream
    /// <c>_collapse_exact_duplicate_extracted_nodes</c>).
    /// </summary>
    internal static List<EntityNode> CollapseExactDuplicateExtractedNodes(
        List<EntityNode> extractedNodes,
        Dictionary<string, List<int>>? nodeEpisodeIndexMap = null)
    {
        if (extractedNodes.Count < 2)
        {
            return extractedNodes;
        }

        var canonicalByName = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        var orderedNames = new List<string>();
        foreach (var node in extractedNodes)
        {
            var normalized = DedupHelpers.NormalizeStringExact(node.Name);
            if (!canonicalByName.TryGetValue(normalized, out var existing))
            {
                canonicalByName[normalized] = node;
                orderedNames.Add(normalized);
                continue;
            }

            var existingSpecific = existing.Labels.Where(l => l != DedupHelpers.EntityBaseLabel).ToHashSet(StringComparer.Ordinal).Count;
            var nodeSpecific = node.Labels.Where(l => l != DedupHelpers.EntityBaseLabel).ToHashSet(StringComparer.Ordinal).Count;
            if (nodeSpecific > existingSpecific ||
                (nodeSpecific == existingSpecific &&
                 DedupHelpers.CodePointLength(node.Name.Trim()) > DedupHelpers.CodePointLength(existing.Name.Trim())))
            {
                canonicalByName[normalized] = node;
                if (nodeEpisodeIndexMap is not null)
                {
                    var oldIndices = Pop(nodeEpisodeIndexMap, existing.Uuid);
                    var newIndices = nodeEpisodeIndexMap.GetValueOrDefault(node.Uuid) ?? [];
                    nodeEpisodeIndexMap[node.Uuid] = [.. newIndices.Concat(oldIndices).Distinct().Order()];
                }
            }
            else if (nodeEpisodeIndexMap is not null)
            {
                var discarded = Pop(nodeEpisodeIndexMap, node.Uuid);
                var canonical = nodeEpisodeIndexMap.GetValueOrDefault(existing.Uuid) ?? [];
                nodeEpisodeIndexMap[existing.Uuid] = [.. canonical.Concat(discarded).Distinct().Order()];
            }
        }

        return [.. orderedNames.Select(name => canonicalByName[name])];

        static List<int> Pop(Dictionary<string, List<int>> map, string key) =>
            map.Remove(key, out var value) ? value : [];
    }

    // ------------------------------------------------------------------------------------------------------------
    // Resolution
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves extracted entities against the graph (upstream <c>resolve_extracted_nodes</c>): collects candidates
    /// per node (identity-property lookup, exact names, then embedding similarity, all within the node's group),
    /// resolves what it can deterministically (identity properties, exact and fuzzy names), and sends the rest to the
    /// <c>dedupe_nodes.nodes</c> prompt in one call.
    /// </summary>
    /// <param name="clients">The services.</param>
    /// <param name="extractedNodes">The new entities.</param>
    /// <param name="episode">The current episode, for the LLM's context.</param>
    /// <param name="previousEpisodes">Earlier episodes, for the LLM's context.</param>
    /// <param name="entityTypes">Custom entity types, for type descriptions.</param>
    /// <param name="existingNodesOverride">Extra candidates for every node (only those in the node's group are
    /// used).</param>
    /// <param name="identityProperties">Per entity type, the attributes that identify an entity of that type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The resolved node for each extracted node, in order; extracted id to resolved id; and the
    /// (extracted, canonical) pairs that were merged.</returns>
    public static async Task<(IReadOnlyList<EntityNode> ResolvedNodes, IReadOnlyDictionary<string, string> UuidMap, IReadOnlyList<(EntityNode Extracted, EntityNode Resolved)> DuplicatePairs)>
        ResolveExtractedNodesAsync(
            GraphitiClients clients,
            IReadOnlyList<EntityNode> extractedNodes,
            EpisodicNode? episode = null,
            IReadOnlyList<EpisodicNode>? previousEpisodes = null,
            IReadOnlyDictionary<string, Type>? entityTypes = null,
            IReadOnlyList<EntityNode>? existingNodesOverride = null,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? identityProperties = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(extractedNodes);
        var candidatesByExtracted = await CollectCandidateNodesAsync(
            clients, extractedNodes, existingNodesOverride, identityProperties, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new DedupResolutionState(extractedNodes.Count);
        for (var idx = 0; idx < extractedNodes.Count; idx++)
        {
            var node = extractedNodes[idx];
            var candidates = candidatesByExtracted[idx];
            if (candidates.Count == 0)
            {
                continue;
            }

            var indexes = DedupHelpers.BuildCandidateIndexes(candidates);
            var local = new DedupResolutionState(1);
            DedupHelpers.ResolveWithIdentityProperties([node], indexes, local, identityProperties);
            if (local.ResolvedNodes[0] is not null)
            {
                CommitResolution(state, local, idx);
                continue;
            }

            DedupHelpers.ResolveWithSimilarity([node], indexes, local);
            if (local.ResolvedNodes[0] is not null)
            {
                CommitResolution(state, local, idx);
                continue;
            }

            state.UnresolvedIndices.Add(idx);
        }

        if (state.UnresolvedIndices.Count > 0)
        {
            var llmCandidates = MergeCandidateNodes(
                [.. state.UnresolvedIndices.SelectMany(i => candidatesByExtracted[i])]);
            await ResolveWithLlmAsync(
                clients,
                extractedNodes,
                DedupHelpers.BuildCandidateIndexes(llmCandidates),
                state,
                episode,
                previousEpisodes,
                entityTypes,
                cancellationToken).ConfigureAwait(false);
        }

        for (var idx = 0; idx < extractedNodes.Count; idx++)
        {
            if (state.ResolvedNodes[idx] is null)
            {
                var node = extractedNodes[idx];
                state.ResolvedNodes[idx] = node;
                state.UuidMap[node.Uuid] = node.Uuid;
            }
        }

        return (
            [.. state.ResolvedNodes.OfType<EntityNode>()],
            new Dictionary<string, string>(state.UuidMap, StringComparer.Ordinal),
            [.. state.DuplicatePairs]);
    }

    /// <summary>Concatenates candidate lists, dropping repeated ids and keeping first occurrences in order (upstream
    /// <c>_merge_candidate_nodes</c>).</summary>
    internal static List<EntityNode> MergeCandidateNodes(
        IReadOnlyList<EntityNode> candidateNodes,
        IReadOnlyList<EntityNode>? existingNodesOverride = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. candidateNodes.Concat(existingNodesOverride ?? []).Where(c => seen.Add(c.Uuid))];
    }

    /// <summary>
    /// Candidates for each extracted node, in order: identity-property matches, exact-name matches, semantic matches,
    /// then <paramref name="existingNodesOverride"/> (upstream <c>_collect_candidate_nodes</c>). All are in the
    /// node's group.
    /// </summary>
    /// <remarks>Unlike upstream, override candidates from a different group than the node are dropped, so entities
    /// never merge across groups.</remarks>
    internal static async Task<List<List<EntityNode>>> CollectCandidateNodesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> extractedNodes,
        IReadOnlyList<EntityNode>? existingNodesOverride = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? identityProperties = null,
        CancellationToken cancellationToken = default)
    {
        var identityResults = await CollectIdentityPropertyCandidateNodesAsync(
            clients, extractedNodes, identityProperties, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var exactNameResults = await CollectExactNameCandidateNodesAsync(clients, extractedNodes, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var searchResults = await SemanticCandidateSearchAsync(clients, extractedNodes, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<List<EntityNode>>(extractedNodes.Count);
        for (var i = 0; i < extractedNodes.Count; i++)
        {
            var groupId = extractedNodes[i].GroupId;
            var overrides = existingNodesOverride?.Where(n => n.GroupId == groupId).ToList();
            result.Add(MergeCandidateNodes(
                [.. identityResults[i], .. exactNameResults[i], .. searchResults[i]],
                overrides));
        }

        return result;
    }

    /// <summary>
    /// For each node with a complete identity key, the entities in its group with the same identity values, from
    /// <see cref="Driver.IEntityNodeOperations.GetByAttributeValuesAsync"/> (upstream
    /// <c>_collect_identity_property_candidate_nodes</c>). A driver without the lookup, or a failing lookup, yields
    /// no candidates.
    /// </summary>
    internal static async Task<List<List<EntityNode>>> CollectIdentityPropertyCandidateNodesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> extractedNodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? identityProperties,
        CancellationToken cancellationToken = default)
    {
        if (extractedNodes.Count == 0)
        {
            return [];
        }

        if (identityProperties is null || identityProperties.Count == 0)
        {
            return [.. extractedNodes.Select(_ => new List<EntityNode>())];
        }

        async Task<List<EntityNode>> LookupAsync(EntityNode node)
        {
            var identityType = DedupHelpers.IdentityTypeForNode(node, identityProperties);
            var identityKey = DedupHelpers.IdentityKeyForNode(node, identityProperties);
            if (identityType is null || identityKey is null)
            {
                return [];
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in identityKey.Values)
            {
                values[key] = value;
            }

            try
            {
                var found = await clients.Driver.EntityNodeOps.GetByAttributeValuesAsync(
                    node.GroupId, values, [identityType], NodeDedupCandidateLimit, cancellationToken).ConfigureAwait(false);
                return [.. found];
            }
            catch (NotSupportedException)
            {
                return [];
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                MaintenanceLog.IdentityLookupFailed(clients.Logger, e, node.Uuid, node.GroupId, identityType);
                return [];
            }
        }

        var results = await Concurrency.SemaphoreGatherAsync(
            extractedNodes.Select<EntityNode, Func<Task<List<EntityNode>>>>(node => () => LookupAsync(node)),
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);
        return [.. results];
    }

    /// <summary>
    /// For each node, the stored entities in its group with the same normalised name, from one
    /// <see cref="Driver.IEntityNodeOperations.GetByNamesAsync"/> call (upstream
    /// <c>_collect_exact_name_candidate_nodes</c>). A failing lookup yields no candidates.
    /// </summary>
    internal static async Task<List<List<EntityNode>>> CollectExactNameCandidateNodesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> extractedNodes,
        CancellationToken cancellationToken = default)
    {
        if (extractedNodes.Count == 0)
        {
            return [];
        }

        var names = extractedNodes.Select(n => n.Name).Where(n => n.Length > 0).ToList();
        var groupIds = extractedNodes.Select(n => n.GroupId).Where(g => !string.IsNullOrEmpty(g)).ToList();
        if (names.Count == 0 || groupIds.Count == 0)
        {
            return [.. extractedNodes.Select(_ => new List<EntityNode>())];
        }

        IReadOnlyList<EntityNode> candidateNodes;
        try
        {
            candidateNodes = await clients.Driver.EntityNodeOps.GetByNamesAsync(names, groupIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            MaintenanceLog.ExactNameLookupFailed(
                clients.Logger, e, extractedNodes.Count, groupIds.Distinct(StringComparer.Ordinal).Count());
            return [.. extractedNodes.Select(_ => new List<EntityNode>())];
        }

        var byGroupAndName = new Dictionary<(string GroupId, string Name), List<EntityNode>>();
        foreach (var candidate in candidateNodes)
        {
            var nameKey = DedupHelpers.NormalizeStringExact(candidate.Name);
            if (nameKey.Length == 0)
            {
                continue;
            }

            var key = (candidate.GroupId, nameKey);
            if (!byGroupAndName.TryGetValue(key, out var list))
            {
                byGroupAndName[key] = list = [];
            }

            list.Add(candidate);
        }

        return [.. extractedNodes.Select(node =>
            new List<EntityNode>(byGroupAndName.GetValueOrDefault((node.GroupId, DedupHelpers.NormalizeStringExact(node.Name))) ?? []))];
    }

    /// <summary>
    /// For each node with a non-blank name, the entities in its group whose name embedding has cosine similarity of at
    /// least <see cref="NodeDedupCosineMinScore"/> with the name's, at most <see cref="NodeDedupCandidateLimit"/>,
    /// without reranking (upstream <c>_semantic_candidate_search</c>). Names are embedded in one batch, or one by one
    /// if the embedder does not support batches.
    /// </summary>
    /// <exception cref="InvalidOperationException">The embedder returned the wrong number of vectors (upstream
    /// raises <c>ValueError</c>).</exception>
    internal static async Task<List<List<EntityNode>>> SemanticCandidateSearchAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> extractedNodes,
        CancellationToken cancellationToken = default)
    {
        if (extractedNodes.Count == 0)
        {
            return [];
        }

        var queryIndices = Enumerable.Range(0, extractedNodes.Count)
            .Where(i => extractedNodes[i].Name.Replace('\n', ' ').Trim().Length > 0)
            .ToList();
        var queries = queryIndices.Select(i => extractedNodes[i].Name.Replace('\n', ' ')).ToList();
        if (queries.Count == 0)
        {
            return [.. extractedNodes.Select(_ => new List<EntityNode>())];
        }

        IReadOnlyList<float[]> queryVectors;
        try
        {
            queryVectors = await clients.Embedder.CreateBatchAsync(queries, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            queryVectors = await Concurrency.SemaphoreGatherAsync(
                queries.Select<string, Func<Task<float[]>>>(q => () => clients.Embedder.CreateAsync(q, cancellationToken)),
                clients.MaxCoroutines,
                cancellationToken).ConfigureAwait(false);
        }

        if (queryVectors.Count != queries.Count)
        {
            throw new InvalidOperationException(
                $"embedder returned {queryVectors.Count} vectors for {queries.Count} queries");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var searchResults = await Concurrency.SemaphoreGatherAsync(
            queryIndices.Zip(queryVectors).Select<(int Index, float[] Vector), Func<Task<IReadOnlyList<EntityNode>>>>(
                pair => () => clients.Driver.SearchOps.NodeSimilaritySearchAsync(
                    pair.Vector,
                    new SearchFilters(),
                    [extractedNodes[pair.Index].GroupId],
                    NodeDedupCandidateLimit,
                    NodeDedupCosineMinScore,
                    cancellationToken)),
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);

        var results = extractedNodes.Select(_ => new List<EntityNode>()).ToList();
        foreach (var (index, found) in queryIndices.Zip(searchResults))
        {
            results[index] = [.. found];
        }

        return results;
    }

    private static void CommitResolution(DedupResolutionState state, DedupResolutionState local, int index)
    {
        if (local.ResolvedNodes[0] is { } resolved)
        {
            state.ResolvedNodes[index] = resolved;
        }

        foreach (var (key, value) in local.UuidMap)
        {
            state.UuidMap[key] = value;
        }

        foreach (var pair in local.DuplicatePairs)
        {
            state.DuplicatePairs.Add(pair);
        }
    }

    /// <summary>
    /// Sends the unresolved nodes to the <c>dedupe_nodes.nodes</c> prompt so the LLM can pick a duplicate or none
    /// (upstream <c>_resolve_with_llm</c>). Malformed or repeated answers are ignored, so the result stays
    /// deterministic.
    /// </summary>
    /// <remarks>Unlike upstream, a chosen candidate from a different group than the node is treated as no duplicate,
    /// so entities never merge across groups.</remarks>
    internal static async Task ResolveWithLlmAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> extractedNodes,
        DedupCandidateIndexes indexes,
        DedupResolutionState state,
        EpisodicNode? episode,
        IReadOnlyList<EpisodicNode>? previousEpisodes,
        IReadOnlyDictionary<string, Type>? entityTypes,
        CancellationToken cancellationToken = default)
    {
        if (state.UnresolvedIndices.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var logger = clients.Logger;
        var llmExtractedNodes = state.UnresolvedIndices.Select(i => extractedNodes[i]).ToList();

        var extractedNodesContext = llmExtractedNodes.Select((node, i) => new Dictionary<string, object?>
        {
            ["id"] = i,
            ["name"] = node.Name,
            ["entity_type"] = node.Labels.ToList(),
            ["entity_type_description"] = GetEntityTypeDescription(node.Labels, entityTypes),
        }).ToList();

        var existingNodesContext = indexes.ExistingNodes.Select((candidate, i) =>
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in candidate.Attributes)
            {
                entry[key] = value;
            }

            entry["candidate_id"] = i;
            entry["name"] = candidate.Name;
            entry["entity_types"] = candidate.Labels.ToList();
            entry["summary"] = candidate.Summary.Length > 0 ? PySlice(candidate.Summary, 120) : "";
            return entry;
        }).ToList();

        var context = new Dictionary<string, object?>
        {
            ["extracted_nodes"] = extractedNodesContext,
            ["existing_nodes"] = existingNodesContext,
            ["episode_content"] = episode?.Content ?? "",
            ["previous_episodes"] = PreviousEpisodesContext(previousEpisodes),
        };

        var clientMaxTokens = clients.LlmClient.MaxTokens;
        var maxTokens = Math.Min(clientMaxTokens > 0 ? clientMaxTokens : DedupeNodesMaxTokens, DedupeNodesMaxTokens);
        var response = await clients.CompletePromptAsync<NodeResolutions>(
            PromptNames.DedupeNodesNodes, context, maxTokens: maxTokens, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var resolutions = response.EntityResolutions;

        var unresolvedCount = state.UnresolvedIndices.Count;
        var receivedIds = resolutions.Select(r => r.Id).ToHashSet();
        var missingIds = Enumerable.Range(0, unresolvedCount).Where(i => !receivedIds.Contains(i)).ToList();
        var extraIds = receivedIds.Where(i => i < 0 || i >= unresolvedCount).Order().ToList();
        if (missingIds.Count > 0)
        {
            MaintenanceLog.MissingResolutions(logger, PythonText.Repr(missingIds));
        }

        if (extraIds.Count > 0)
        {
            MaintenanceLog.ExtraResolutions(
                logger, unresolvedCount - 1, PythonText.Repr(extraIds), PythonText.Repr(receivedIds.Order().ToList()));
        }

        var processed = new HashSet<int>();
        foreach (var resolution in resolutions)
        {
            var relativeId = resolution.Id;
            var duplicateCandidateId = resolution.DuplicateCandidateId;
            if (relativeId < 0 || relativeId >= unresolvedCount)
            {
                MaintenanceLog.InvalidDedupeId(logger, relativeId, unresolvedCount - 1, resolutions.Count);
                continue;
            }

            if (!processed.Add(relativeId))
            {
                MaintenanceLog.DuplicateDedupeId(logger, relativeId);
                continue;
            }

            var originalIndex = state.UnresolvedIndices[relativeId];
            var extractedNode = extractedNodes[originalIndex];

            EntityNode resolvedNode;
            if (duplicateCandidateId < 0)
            {
                resolvedNode = extractedNode;
            }
            else if (duplicateCandidateId < indexes.ExistingNodes.Count)
            {
                var candidate = indexes.ExistingNodes[duplicateCandidateId];
                if (candidate.GroupId != extractedNode.GroupId)
                {
                    MaintenanceLog.CrossGroupDuplicate(logger, duplicateCandidateId, extractedNode.Uuid);
                    resolvedNode = extractedNode;
                }
                else
                {
                    resolvedNode = DedupHelpers.PromoteResolvedNode(extractedNode, candidate);
                }
            }
            else
            {
                MaintenanceLog.InvalidDuplicateCandidateId(logger, duplicateCandidateId, extractedNode.Uuid);
                resolvedNode = extractedNode;
            }

            state.ResolvedNodes[originalIndex] = resolvedNode;
            state.UuidMap[extractedNode.Uuid] = resolvedNode.Uuid;
            if (resolvedNode.Uuid != extractedNode.Uuid)
            {
                state.DuplicatePairs.Add((extractedNode, resolvedNode));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Attributes and summaries
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Maps each node id to the edges that start or end at it (upstream <c>_build_edges_by_node</c>).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<EntityEdge>> BuildEdgesByNode(IReadOnlyList<EntityEdge>? edges)
    {
        var byNode = new Dictionary<string, List<EntityEdge>>(StringComparer.Ordinal);
        foreach (var edge in edges ?? [])
        {
            Add(edge.SourceNodeUuid, edge);
            Add(edge.TargetNodeUuid, edge);
        }

        return byNode.ToDictionary(e => e.Key, e => (IReadOnlyList<EntityEdge>)e.Value, StringComparer.Ordinal);

        void Add(string uuid, EntityEdge edge)
        {
            if (!byNode.TryGetValue(uuid, out var list))
            {
                byNode[uuid] = list = [];
            }

            list.Add(edge);
        }
    }

    /// <summary>
    /// Extracts custom-type attributes for nodes whose labels include a custom type with at least one attribute
    /// (upstream <c>extract_attributes_only_from_nodes</c>). Nodes are changed in place and returned.
    /// </summary>
    /// <param name="clients">The services.</param>
    /// <param name="nodes">The nodes.</param>
    /// <param name="episodes">The current episode(s), or <see langword="null"/>.</param>
    /// <param name="previousEpisodes">Earlier episodes, for context.</param>
    /// <param name="entityTypes">Custom entity types.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<IReadOnlyList<EntityNode>> ExtractAttributesOnlyFromNodesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EpisodicNode>? episodes = null,
        IReadOnlyList<EpisodicNode>? previousEpisodes = null,
        IReadOnlyDictionary<string, Type>? entityTypes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0 || entityTypes is null || entityTypes.Count == 0)
        {
            return nodes;
        }

        var inputs = new List<(EntityNode Node, Type EntityType)>();
        foreach (var node in nodes)
        {
            var entityType = EntityTypeForNode(node, entityTypes);
            if (entityType is null || EntityTypesUtils.GetFields(entityType).Count == 0)
            {
                continue;
            }

            inputs.Add((node, entityType));
        }

        if (inputs.Count == 0)
        {
            return nodes;
        }

        var results = await Concurrency.SemaphoreGatherAsync(
            inputs.Select<(EntityNode Node, Type EntityType), Func<Task<Dictionary<string, object?>>>>(input => () =>
                ExtractEntityAttributesAsync(clients, input.Node, episodes, previousEpisodes, input.EntityType, cancellationToken)),
            clients.MaxCoroutines,
            cancellationToken).ConfigureAwait(false);

        foreach (var ((node, _), attributes) in inputs.Zip(results))
        {
            node.Attributes.Clear();
            foreach (var (key, value) in attributes)
            {
                node.Attributes[key] = value;
            }
        }

        return nodes;
    }

    /// <summary>
    /// Extracts attributes (unless skipped), updates summaries and embeds names (upstream
    /// <c>extract_attributes_from_nodes</c>). Nodes are changed in place and returned.
    /// </summary>
    /// <param name="clients">The services.</param>
    /// <param name="nodes">The nodes.</param>
    /// <param name="episodes">The current episode(s), or <see langword="null"/>.</param>
    /// <param name="previousEpisodes">Earlier episodes, for context.</param>
    /// <param name="entityTypes">Custom entity types.</param>
    /// <param name="shouldSummarizeNode">Decides which nodes get a summary update; all by default.</param>
    /// <param name="edges">Facts whose text short summaries are extended with.</param>
    /// <param name="skipFactAppending">Summarise every node from the episodes with the LLM instead of appending
    /// facts.</param>
    /// <param name="includeTypeDescriptions">Give the summary prompt the custom types' descriptions.</param>
    /// <param name="skipAttributeExtraction">Skip attribute extraction.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<IReadOnlyList<EntityNode>> ExtractAttributesFromNodesAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EpisodicNode>? episodes = null,
        IReadOnlyList<EpisodicNode>? previousEpisodes = null,
        IReadOnlyDictionary<string, Type>? entityTypes = null,
        NodeSummaryFilter? shouldSummarizeNode = null,
        IReadOnlyList<EntityEdge>? edges = null,
        bool skipFactAppending = false,
        bool includeTypeDescriptions = false,
        bool skipAttributeExtraction = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(nodes);
        var edgesByNode = BuildEdgesByNode(edges);

        if (!skipAttributeExtraction)
        {
            await ExtractAttributesOnlyFromNodesAsync(
                clients, nodes, episodes, previousEpisodes, entityTypes, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ExtractEntitySummariesBatchAsync(
            clients,
            nodes,
            episodes,
            previousEpisodes,
            shouldSummarizeNode,
            edgesByNode,
            skipFactAppending,
            includeTypeDescriptions ? entityTypes : null,
            clients.MaxCoroutines,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await CreateEntityNodeEmbeddingsAsync(clients.Embedder, nodes, cancellationToken).ConfigureAwait(false);
        return nodes;
    }

    /// <summary>
    /// Embeds the names of the nodes that have one, in one batch (upstream <c>create_entity_node_embeddings</c> in
    /// <c>nodes.py</c>).
    /// </summary>
    public static async Task CreateEntityNodeEmbeddingsAsync(
        EmbedderClient embedder,
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(nodes);
        var filtered = nodes.Where(n => n.Name.Length > 0).ToList();
        if (filtered.Count == 0)
        {
            return;
        }

        var embeddings = await embedder.CreateBatchAsync([.. filtered.Select(n => n.Name)], cancellationToken)
            .ConfigureAwait(false);
        if (embeddings.Count != filtered.Count)
        {
            throw new InvalidOperationException(
                $"embedder returned {embeddings.Count} vectors for {filtered.Count} nodes");
        }

        foreach (var (node, embedding) in filtered.Zip(embeddings))
        {
            node.NameEmbedding = embedding;
        }
    }

    private static Type? EntityTypeForNode(EntityNode node, IReadOnlyDictionary<string, Type>? entityTypes)
    {
        if (entityTypes is null)
        {
            return null;
        }

        var typeName = node.Labels.FirstOrDefault(entityTypes.ContainsKey);
        return typeName is null ? null : entityTypes[typeName];
    }

    /// <summary>
    /// Runs <c>extract_nodes.extract_attributes</c> for one node with its type as the response model, caps over-long
    /// strings and overlays the result on the node's prior attributes (upstream <c>_extract_entity_attributes</c>).
    /// Returns the merged attributes; the node is not changed.
    /// </summary>
    /// <exception cref="JsonException">The merged attributes do not fit the type (upstream's Pydantic validation
    /// error).</exception>
    internal static async Task<Dictionary<string, object?>> ExtractEntityAttributesAsync(
        GraphitiClients clients,
        EntityNode node,
        IReadOnlyList<EpisodicNode>? episodes,
        IReadOnlyList<EpisodicNode>? previousEpisodes,
        Type? entityType,
        CancellationToken cancellationToken = default)
    {
        var prior = new Dictionary<string, object?>(node.Attributes, StringComparer.Ordinal);
        if (entityType is null || EntityTypesUtils.GetFields(entityType).Count == 0)
        {
            // No applicable type means nothing to extract: keep the prior attributes.
            return prior;
        }

        var attributesContext = BuildEpisodeContext(
            new Dictionary<string, object?>
            {
                ["name"] = node.Name,
                ["entity_types"] = node.Labels.ToList(),
                ["attributes"] = node.Attributes,
            },
            episodes,
            previousEpisodes);

        var response = await clients.CompletePromptAsync(
            PromptNames.ExtractNodesExtractAttributes,
            attributesContext,
            entityType,
            ModelSize.Small,
            node.GroupId,
            attributeExtraction: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var (merged, _) = AttributeUtils.ApplyCappedAttributes(
            AttributeUtils.FromJsonResponse(response),
            entityType,
            prior,
            AttributeMergeMode.Overlay,
            promptName: PromptNames.ExtractNodesExtractAttributes,
            entityUuid: node.Uuid,
            groupId: node.GroupId,
            logger: clients.Logger);

        // Shape validation only, as upstream: the instance is discarded so defaults don't overwrite prior values.
        ValidateAttributes(merged, entityType);
        return merged;
    }

    private static void ValidateAttributes(Dictionary<string, object?> attributes, Type entityType)
    {
        var json = JsonSerializer.SerializeToElement(attributes, ResponseModelSchema.SerializerOptions);
        _ = json.Deserialize(entityType, ResponseModelSchema.SerializerOptions)
            ?? throw new JsonException($"The attributes do not form a {entityType.Name}.");
    }

    /// <summary>
    /// Updates summaries in batched LLM calls (upstream <c>_extract_entity_summaries_batch</c>). Without
    /// <paramref name="skipFactAppending"/>, a node whose summary plus its edges' facts fits in twice
    /// <see cref="TextUtils.MaxSummaryChars"/> just gets the facts appended; the rest go to
    /// <c>extract_nodes.extract_summaries_batch</c> in flights of <see cref="MaxNodes"/>. With it, every node with an
    /// episode or a summary goes to <c>extract_nodes.extract_entity_summaries_from_episodes</c>.
    /// </summary>
    /// <param name="clients">The services.</param>
    /// <param name="nodes">The nodes, changed in place.</param>
    /// <param name="episodes">The current episode(s), or <see langword="null"/>.</param>
    /// <param name="previousEpisodes">Earlier episodes, for context.</param>
    /// <param name="shouldSummarizeNode">Decides which nodes are summarised; all by default.</param>
    /// <param name="edgesByNode">Each node's facts, from <see cref="BuildEdgesByNode"/>.</param>
    /// <param name="skipFactAppending">Route every node through the episode-based prompt.</param>
    /// <param name="entityTypes">Custom types whose descriptions the prompt gets, or <see langword="null"/>.</param>
    /// <param name="maxCoroutines">Concurrent flights; <see langword="null"/> for the default.</param>
    /// <param name="flightSize">Nodes per LLM call; <see cref="MaxNodes"/> by default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task ExtractEntitySummariesBatchAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EpisodicNode>? episodes,
        IReadOnlyList<EpisodicNode>? previousEpisodes,
        NodeSummaryFilter? shouldSummarizeNode,
        IReadOnlyDictionary<string, IReadOnlyList<EntityEdge>> edgesByNode,
        bool skipFactAppending = false,
        IReadOnlyDictionary<string, Type>? entityTypes = null,
        int? maxCoroutines = null,
        int flightSize = MaxNodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edgesByNode);
        ArgumentOutOfRangeException.ThrowIfLessThan(flightSize, 1);
        var nodesNeedingLlm = new List<EntityNode>();

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldSummarizeNode is not null && !await shouldSummarizeNode(node).ConfigureAwait(false))
            {
                continue;
            }

            if (skipFactAppending)
            {
                if (episodes is not null || node.Summary.Length > 0)
                {
                    nodesNeedingLlm.Add(node);
                }

                continue;
            }

            var nodeEdges = edgesByNode.GetValueOrDefault(node.Uuid) ?? [];
            var summaryWithEdges = node.Summary;
            if (nodeEdges.Count > 0)
            {
                var edgeFacts = string.Join('\n', nodeEdges.Where(e => e.Fact.Length > 0).Select(e => e.Fact));
                summaryWithEdges = $"{summaryWithEdges}\n{edgeFacts}".Trim();
            }

            if (summaryWithEdges.Length > 0 &&
                DedupHelpers.CodePointLength(summaryWithEdges) <= TextUtils.MaxSummaryChars * 2)
            {
                node.Summary = summaryWithEdges;
                continue;
            }

            if (summaryWithEdges.Length == 0 && episodes is null)
            {
                continue;
            }

            nodesNeedingLlm.Add(node);
        }

        if (nodesNeedingLlm.Count == 0)
        {
            return;
        }

        var flights = nodesNeedingLlm.Chunk(flightSize).ToList();
        await Concurrency.SemaphoreGatherAsync(
            flights.Select<EntityNode[], Func<Task>>(flight => () => ProcessSummaryFlightAsync(
                clients, flight, episodes, previousEpisodes, skipFactAppending, entityTypes, cancellationToken)),
            maxCoroutines,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Summarises one flight of nodes with one LLM call and applies the summaries by case-insensitive name
    /// (upstream <c>_process_summary_flight</c>).</summary>
    private static async Task ProcessSummaryFlightAsync(
        GraphitiClients clients,
        EntityNode[] nodes,
        IReadOnlyList<EpisodicNode>? episodes,
        IReadOnlyList<EpisodicNode>? previousEpisodes,
        bool useEpisodePrompt,
        IReadOnlyDictionary<string, Type>? entityTypes,
        CancellationToken cancellationToken)
    {
        var entityTypeDescriptions = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (entityTypes is not null)
        {
            foreach (var (typeName, typeModel) in entityTypes)
            {
                if (EntityTypesUtils.GetDescription(typeModel) is { Length: > 0 } description)
                {
                    entityTypeDescriptions[typeName] = TruncateTypeDescription(description);
                }
            }
        }

        var entitiesContext = nodes.Select(node => new Dictionary<string, object?>
        {
            ["name"] = node.Name,
            ["summary"] = node.Summary,
            ["entity_types"] = node.Labels.ToList(),
            ["attributes"] = node.Attributes,
        }).ToList();

        var batchContext = new Dictionary<string, object?>
        {
            ["entities"] = entitiesContext,
            ["episode_content"] = EpisodeContent(episodes),
            ["previous_episodes"] = PreviousEpisodesContext(previousEpisodes),
            ["entity_type_descriptions"] = entityTypeDescriptions,
        };

        var groupId = nodes.Length > 0 ? nodes[0].GroupId : null;
        var promptName = useEpisodePrompt
            ? PromptNames.ExtractNodesExtractEntitySummariesFromEpisodes
            : PromptNames.ExtractNodesExtractSummariesBatch;

        var response = await clients.CompletePromptAsync<SummarizedEntities>(
            promptName, batchContext, ModelSize.Small, groupId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var nameToNodes = new Dictionary<string, List<EntityNode>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var key = node.Name.ToLowerInvariant();
            if (!nameToNodes.TryGetValue(key, out var list))
            {
                nameToNodes[key] = list = [];
            }

            list.Add(node);
        }

        foreach (var summarized in response.Summaries)
        {
            if (nameToNodes.TryGetValue(summarized.Name.ToLowerInvariant(), out var matching))
            {
                var truncated = TextUtils.TruncateAtSentence(summarized.Summary, TextUtils.MaxSummaryChars);
                foreach (var node in matching)
                {
                    node.Summary = truncated;
                }
            }
            else
            {
                MaintenanceLog.UnknownSummaryEntity(clients.Logger, PySlice(summarized.Name, 30));
            }
        }
    }

    /// <summary>The context of <c>extract_nodes.extract_attributes</c> (upstream <c>_build_episode_context</c>).</summary>
    internal static Dictionary<string, object?> BuildEpisodeContext(
        Dictionary<string, object?> nodeData,
        IReadOnlyList<EpisodicNode>? episodes,
        IReadOnlyList<EpisodicNode>? previousEpisodes) => new()
        {
            ["node"] = nodeData,
            ["episode_content"] = EpisodeContent(episodes),
            ["previous_episodes"] = PreviousEpisodesContext(previousEpisodes),
        };

    private static string EpisodeContent(IReadOnlyList<EpisodicNode>? episodes) =>
        episodes is null ? "" : episodes.Count == 0 ? "" : TextUtils.ConcatenateEpisodes(episodes);

    private static List<Dictionary<string, object?>> PreviousEpisodesContext(IReadOnlyList<EpisodicNode>? previousEpisodes) =>
        previousEpisodes is null
            ? []
            : [.. previousEpisodes.Select(ep => new Dictionary<string, object?>
            {
                ["content"] = ep.Content,
                ["timestamp"] = PythonText.IsoFormat(ep.ValidAt),
            })];

    // Python's text[:n], which counts code points.
    private static string PySlice(string text, int maxCodePoints)
    {
        var index = 0;
        for (var count = 0; count < maxCodePoints && index < text.Length; count++)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
        }

        return text[..index];
    }

    // Python's str.splitlines() for the line breaks a description realistically contains.
    private static string[] PythonSplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split(['\n', '\r', '\v', '\f', '\u001c', '\u001d', '\u001e', '\u0085', '\u2028', '\u2029']) is var parts &&
        parts.Length > 0 && parts[^1].Length == 0
            ? parts[..^1]
            : parts;
}
