// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/community_operations.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.Json;
using GraphitiNet.Driver;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Utils;

namespace GraphitiNet.Maintenance;

/// <summary>
/// Communities: clusters of closely connected entities, found with label propagation over the fact graph and
/// summarised by the LLM, merging member summaries pairwise.
/// </summary>
public static class CommunityOperations
{
    /// <summary>Upstream <c>MAX_COMMUNITY_BUILD_CONCURRENCY</c>: communities summarised at once when no
    /// <c>maxCoroutines</c> is set.</summary>
    public const int MaxCommunityBuildConcurrency = 10;

    /// <summary>
    /// Clusters the entities of each group (upstream <c>get_community_clusters</c>): label propagation over the
    /// driver's entity projection. Clusters never span groups.
    /// </summary>
    /// <param name="driver">The graph driver.</param>
    /// <param name="groupIds">The groups to cluster; <see langword="null"/> means every group that has entities.</param>
    /// <param name="maxCoroutines">Concurrency limit for loading the clusters' entities.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The clusters, each a list of entities.</returns>
    public static async Task<IReadOnlyList<IReadOnlyList<EntityNode>>> GetCommunityClustersAsync(
        IGraphDriver driver,
        IReadOnlyList<string>? groupIds,
        int? maxCoroutines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        groupIds ??= await driver.GraphOps.GetEntityGroupIdsAsync(cancellationToken).ConfigureAwait(false);

        var communityClusters = new List<IReadOnlyList<EntityNode>>();
        foreach (var groupId in groupIds)
        {
            var projection = await driver.GraphOps.GetEntityProjectionAsync(groupId, cancellationToken)
                .ConfigureAwait(false);
            var clusterUuids = LabelPropagation(projection);
            var clusters = await Concurrency.SemaphoreGatherAsync(
                    clusterUuids.Select<IReadOnlyList<string>, Func<Task<IReadOnlyList<EntityNode>>>>(cluster =>
                        () => driver.EntityNodeOps.GetByUuidsAsync(cluster, cancellationToken)),
                    maxCoroutines,
                    cancellationToken)
                .ConfigureAwait(false);
            communityClusters.AddRange(clusters);
        }

        return communityClusters;
    }

    /// <summary>
    /// Label propagation community detection, as upstream's <c>label_propagation</c>: every node starts in its own
    /// community; each round, every node takes the community with the largest total edge count among its neighbours
    /// (ties go to the higher community number); a node whose best candidate has a count of 1 or less keeps the
    /// higher of that candidate and its current community. Stops when a round changes nothing.
    /// </summary>
    /// <param name="projection">Each node's neighbours and the number of facts joining them.</param>
    /// <returns>The clusters of node ids.</returns>
    /// <remarks>The rounds update synchronously, as upstream, which can oscillate: two entities joined by two facts
    /// swap communities forever, and upstream never returns. graphiti-net stops when a round repeats an earlier
    /// assignment and uses the last assignment; on graphs where upstream terminates, the result is the same.</remarks>
    public static IReadOnlyList<IReadOnlyList<string>> LabelPropagation(
        IReadOnlyDictionary<string, IReadOnlyList<Neighbor>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var keys = projection.Keys.ToList();
        var communityMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            communityMap[keys[i]] = i;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { Signature(keys, communityMap) };
        while (true)
        {
            var noChange = true;
            var newCommunityMap = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var uuid in keys)
            {
                var neighbors = projection[uuid];
                var currentCommunity = communityMap[uuid];

                var candidates = new Dictionary<int, int>();
                foreach (var neighbor in neighbors)
                {
                    var community = communityMap[neighbor.NodeUuid];
                    candidates[community] = candidates.GetValueOrDefault(community) + neighbor.EdgeCount;
                }

                var (candidateRank, communityCandidate) = candidates.Count > 0
                    ? candidates
                        .Select(pair => (Count: pair.Value, Community: pair.Key))
                        .OrderByDescending(pair => pair.Count)
                        .ThenByDescending(pair => pair.Community)
                        .First()
                    : (0, -1);

                var newCommunity = communityCandidate != -1 && candidateRank > 1
                    ? communityCandidate
                    : Math.Max(communityCandidate, currentCommunity);

                newCommunityMap[uuid] = newCommunity;
                if (newCommunity != currentCommunity)
                {
                    noChange = false;
                }
            }

            if (noChange)
            {
                break;
            }

            communityMap = newCommunityMap;
            if (!seen.Add(Signature(keys, communityMap)))
            {
                // A cycle (upstream loops forever here).
                break;
            }
        }

        var clusters = new Dictionary<int, List<string>>();
        foreach (var uuid in keys)
        {
            var community = communityMap[uuid];
            if (!clusters.TryGetValue(community, out var cluster))
            {
                cluster = [];
                clusters[community] = cluster;
            }

            cluster.Add(uuid);
        }

        return clusters.Values.ToList();
    }

    /// <summary>Merges two summaries into one with the LLM (upstream <c>summarize_pair</c>), cut to
    /// <see cref="TextUtils.MaxSummaryChars"/> at a sentence end.</summary>
    /// <param name="clients">The LLM, prompts and routes.</param>
    /// <param name="summaryPair">The two summaries.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<string> SummarizePairAsync(
        GraphitiClients clients,
        (string Left, string Right) summaryPair,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var context = new Dictionary<string, object?>
        {
            ["node_summaries"] = new List<Dictionary<string, object?>>
            {
                new() { ["summary"] = summaryPair.Left },
                new() { ["summary"] = summaryPair.Right },
            },
        };

        var response = await clients.CompletePromptAsync(
                PromptNames.SummarizeNodesSummarizePair,
                context,
                typeof(Summary),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return TextUtils.TruncateAtSentence(GetString(response, "summary"), TextUtils.MaxSummaryChars);
    }

    /// <summary>A one-sentence description of a community summary, used as the community's name (upstream
    /// <c>generate_summary_description</c>).</summary>
    /// <param name="clients">The LLM, prompts and routes.</param>
    /// <param name="summary">The summary.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<string> GenerateSummaryDescriptionAsync(
        GraphitiClients clients,
        string summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var context = new Dictionary<string, object?> { ["summary"] = summary };
        var response = await clients.CompletePromptAsync(
                PromptNames.SummarizeNodesSummaryDescription,
                context,
                typeof(SummaryDescription),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return GetString(response, "description");
    }

    /// <summary>
    /// Builds one community from a cluster (upstream <c>build_community</c>): merges the members' summaries pairwise
    /// until one is left, names it, and links the community to each member.
    /// </summary>
    /// <param name="clients">The LLM, prompts and routes.</param>
    /// <param name="communityCluster">The members; at least one.</param>
    /// <param name="maxCoroutines">Concurrency limit for the pairwise summaries.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The community (without a name embedding) and its membership edges, not saved.</returns>
    public static async Task<(CommunityNode Community, IReadOnlyList<CommunityEdge> Edges)> BuildCommunityAsync(
        GraphitiClients clients,
        IReadOnlyList<EntityNode> communityCluster,
        int? maxCoroutines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(communityCluster);

        var summaries = communityCluster.Select(entity => entity.Summary).ToList();
        var length = summaries.Count;
        while (length > 1)
        {
            string? oddOneOut = null;
            if (length % 2 == 1)
            {
                oddOneOut = summaries[^1];
                summaries.RemoveAt(summaries.Count - 1);
                length--;
            }

            var half = length / 2;
            var pairs = summaries.Take(half).Zip(summaries.Skip(half)).ToList();
            var newSummaries = (await Concurrency.SemaphoreGatherAsync(
                        pairs.Select<(string, string), Func<Task<string>>>(pair =>
                            () => SummarizePairAsync(clients, pair, cancellationToken)),
                        maxCoroutines,
                        cancellationToken)
                    .ConfigureAwait(false))
                .ToList();
            if (oddOneOut is not null)
            {
                newSummaries.Add(oddOneOut);
            }

            summaries = newSummaries;
            length = summaries.Count;
        }

        var summary = TextUtils.TruncateAtSentence(summaries[0], TextUtils.MaxSummaryChars);
        var name = await GenerateSummaryDescriptionAsync(clients, summary, cancellationToken).ConfigureAwait(false);
        var now = DateTimeUtils.UtcNow();
        var communityNode = new CommunityNode
        {
            Name = name,
            GroupId = communityCluster[0].GroupId,
            Labels = ["Community"],
            CreatedAt = now,
            Summary = summary,
        };
        var communityEdges = EdgeOperations.BuildCommunityEdges(communityCluster, communityNode, now);
        return (communityNode, communityEdges);
    }

    /// <summary>
    /// Clusters the groups' entities and builds a community for each cluster (upstream <c>build_communities</c>).
    /// Nothing is saved.
    /// </summary>
    /// <param name="clients">The driver, LLM, prompts and routes.</param>
    /// <param name="groupIds">The groups; <see langword="null"/> means every group.</param>
    /// <param name="maxCoroutines">Concurrency limit; when <see langword="null"/>, at most
    /// <see cref="MaxCommunityBuildConcurrency"/> communities are built at once, as upstream.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<(IReadOnlyList<CommunityNode> Communities, IReadOnlyList<CommunityEdge> Edges)>
        BuildCommunitiesAsync(
            GraphitiClients clients,
            IReadOnlyList<string>? groupIds,
            int? maxCoroutines = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var clusters = await GetCommunityClustersAsync(clients.Driver, groupIds, maxCoroutines, cancellationToken)
            .ConfigureAwait(false);

        var communities = await Concurrency.SemaphoreGatherAsync(
                clusters.Select<IReadOnlyList<EntityNode>, Func<Task<(CommunityNode, IReadOnlyList<CommunityEdge>)>>>(
                    cluster => () => BuildCommunityAsync(clients, cluster, maxCoroutines, cancellationToken)),
                maxCoroutines ?? MaxCommunityBuildConcurrency,
                cancellationToken)
            .ConfigureAwait(false);

        var communityNodes = new List<CommunityNode>();
        var communityEdges = new List<CommunityEdge>();
        foreach (var (node, edges) in communities)
        {
            communityNodes.Add(node);
            communityEdges.AddRange(edges);
        }

        return (communityNodes, communityEdges);
    }

    /// <summary>Deletes the communities, and their membership edges, of these groups; <see langword="null"/> or an
    /// empty list deletes every community (upstream <c>remove_communities</c>).</summary>
    /// <param name="driver">The graph driver.</param>
    /// <param name="groupIds">The groups.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task RemoveCommunitiesAsync(
        IGraphDriver driver,
        IReadOnlyList<string>? groupIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.GraphOps.RemoveCommunitiesAsync(groupIds, cancellationToken);
    }

    /// <summary>
    /// The community an entity belongs to (upstream <c>determine_entity_community</c>): its existing community, or
    /// else the community most of its neighbours belong to.
    /// </summary>
    /// <param name="driver">The graph driver.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The community, or <see langword="null"/> if there is none; and whether the entity still has to be
    /// added to it.</returns>
    public static async Task<(CommunityNode? Community, bool IsNew)> DetermineEntityCommunityAsync(
        IGraphDriver driver,
        EntityNode entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(entity);

        var existing = await driver.GraphOps.GetCommunitiesByNodesAsync([entity], cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return (existing[0], false);
        }

        var communities = await driver.GraphOps.GetNeighborCommunitiesAsync(entity.Uuid, cancellationToken)
            .ConfigureAwait(false);

        var communityMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var community in communities)
        {
            if (!communityMap.TryGetValue(community.Uuid, out var count))
            {
                order.Add(community.Uuid);
            }

            communityMap[community.Uuid] = count + 1;
        }

        string? communityUuid = null;
        var maxCount = 0;
        foreach (var uuid in order)
        {
            if (communityMap[uuid] > maxCount)
            {
                communityUuid = uuid;
                maxCount = communityMap[uuid];
            }
        }

        if (maxCount == 0)
        {
            return (null, false);
        }

        return (communities.First(community => community.Uuid == communityUuid), true);
    }

    /// <summary>
    /// Folds an entity into its community (upstream <c>update_community</c>): merges the entity's summary into the
    /// community's, renames it, links the entity if it is new to the community, re-embeds the name and saves.
    /// </summary>
    /// <param name="clients">The driver, LLM, embedder, prompts and routes.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The updated community (none if the entity has no community) and the new membership edge, if
    /// any.</returns>
    public static async Task<(IReadOnlyList<CommunityNode> Communities, IReadOnlyList<CommunityEdge> Edges)>
        UpdateCommunityAsync(
            GraphitiClients clients,
            EntityNode entity,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(entity);
        var driver = clients.Driver;

        var (community, isNew) = await DetermineEntityCommunityAsync(driver, entity, cancellationToken)
            .ConfigureAwait(false);
        if (community is null)
        {
            return ([], []);
        }

        var newSummary = await SummarizePairAsync(clients, (entity.Summary, community.Summary), cancellationToken)
            .ConfigureAwait(false);
        var newName = await GenerateSummaryDescriptionAsync(clients, newSummary, cancellationToken)
            .ConfigureAwait(false);

        community.Summary = newSummary;
        community.Name = newName;

        var communityEdges = new List<CommunityEdge>();
        if (isNew)
        {
            var communityEdge = EdgeOperations.BuildCommunityEdges([entity], community, DateTimeUtils.UtcNow())[0];
            await driver.CommunityEdgeOps.SaveAsync(communityEdge, cancellationToken).ConfigureAwait(false);
            communityEdges.Add(communityEdge);
        }

        community.NameEmbedding = await LLMUtils.GenerateEmbeddingAsync(clients.Embedder, community.Name, cancellationToken)
            .ConfigureAwait(false);
        await driver.CommunityNodeOps.SaveAsync(community, cancellationToken).ConfigureAwait(false);

        return ([community], communityEdges);
    }

    private static string Signature(List<string> keys, Dictionary<string, int> communityMap) =>
        string.Join(',', keys.Select(key => communityMap[key].ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // llm_response.get(key, '').
    private static string GetString(JsonElement response, string key) =>
        response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
