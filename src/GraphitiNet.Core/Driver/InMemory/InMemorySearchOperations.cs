// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Driver.InMemory;

/// <summary>Search over the in-memory graph: BM25 full-text, cosine similarity and breadth-first traversal.</summary>
internal sealed class InMemorySearchOperations(InMemoryStore store) : ISearchOperations
{
    public Task<IReadOnlyList<EntityNode>> NodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchFilter);
        return Read<IReadOnlyList<EntityNode>>(
            () => Bm25.Search(
                    query,
                    store.Entities.Values,
                    n => n.Uuid,
                    n => [n.Name, n.Summary],
                    n => FilterEvaluator.InGroups(groupIds, n.GroupId) && FilterEvaluator.MatchesNode(searchFilter, n),
                    limit)
                .Select(n => RecordCopies.Copy(n, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlyList<EntityNode>> NodeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        ArgumentNullException.ThrowIfNull(searchFilter);
        return Read<IReadOnlyList<EntityNode>>(
            () => Similar(
                    store.Entities.Values
                        .Where(n => FilterEvaluator.InGroups(groupIds, n.GroupId)
                            && FilterEvaluator.MatchesNode(searchFilter, n)),
                    n => n.NameEmbedding,
                    n => n.Uuid,
                    searchVector,
                    minScore,
                    limit)
                .Select(n => RecordCopies.Copy(n, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlyList<EntityNode>> NodeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        SearchFilters searchFilter,
        int maxDepth,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originUuids);
        ArgumentNullException.ThrowIfNull(searchFilter);
        return Read<IReadOnlyList<EntityNode>>(
            () =>
            {
                if (maxDepth < 1 || limit <= 0)
                {
                    return [];
                }

                var adjacency = BuildAdjacency();
                var found = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var origin in originUuids.Distinct(StringComparer.Ordinal))
                {
                    var originGroup = store.NodeGroup(origin);
                    if (originGroup is null || !FilterEvaluator.InGroups(groupIds, originGroup))
                    {
                        continue;
                    }

                    foreach (var (uuid, depth) in Reach(adjacency, origin, maxDepth))
                    {
                        if (!store.Entities.TryGetValue(uuid, out var node)
                            || node.GroupId != originGroup
                            || !FilterEvaluator.InGroups(groupIds, node.GroupId)
                            || !FilterEvaluator.MatchesNode(searchFilter, node))
                        {
                            continue;
                        }

                        found[uuid] = Math.Min(depth, found.GetValueOrDefault(uuid, int.MaxValue));
                    }
                }

                return found
                    .OrderBy(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(pair => RecordCopies.Copy(store.Entities[pair.Key], includeEmbedding: false))
                    .ToList();
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchFilter);
        return Read<IReadOnlyList<EntityEdge>>(
            () => Bm25.Search(
                    query,
                    store.EntityEdges.Values,
                    e => e.Uuid,
                    e => [e.Name, e.Fact],
                    e => FilterEvaluator.InGroups(groupIds, e.GroupId) && MatchesEdge(searchFilter, e),
                    limit)
                .Select(e => RecordCopies.Copy(e, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        string? sourceNodeUuid,
        string? targetNodeUuid,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        ArgumentNullException.ThrowIfNull(searchFilter);
        return Read<IReadOnlyList<EntityEdge>>(
            () => Similar(
                    store.EntityEdges.Values
                        .Where(e => FilterEvaluator.InGroups(groupIds, e.GroupId)
                            && (sourceNodeUuid is null || e.SourceNodeUuid == sourceNodeUuid)
                            && (targetNodeUuid is null || e.TargetNodeUuid == targetNodeUuid)
                            && MatchesEdge(searchFilter, e)),
                    e => e.FactEmbedding,
                    e => e.Uuid,
                    searchVector,
                    minScore,
                    limit)
                .Select(e => RecordCopies.Copy(e, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        int maxDepth,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originUuids);
        ArgumentNullException.ThrowIfNull(searchFilter);
        return Read<IReadOnlyList<EntityEdge>>(
            () =>
            {
                if (maxDepth < 1 || limit <= 0)
                {
                    return [];
                }

                var adjacency = BuildAdjacency();

                // A fact is on a path of at most maxDepth hops when one of its ends is within maxDepth - 1 hops.
                var found = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var origin in originUuids.Distinct(StringComparer.Ordinal))
                {
                    var distances = Distances(adjacency, origin, maxDepth - 1);
                    foreach (var (uuid, depth) in distances)
                    {
                        if (!adjacency.TryGetValue(uuid, out var steps))
                        {
                            continue;
                        }

                        foreach (var step in steps)
                        {
                            if (step.EdgeUuid is not { } edgeUuid)
                            {
                                continue;
                            }

                            found[edgeUuid] = Math.Min(depth + 1, found.GetValueOrDefault(edgeUuid, int.MaxValue));
                        }
                    }
                }

                return found
                    .Select(pair => (Edge: store.EntityEdges[pair.Key], Depth: pair.Value))
                    .Where(x => FilterEvaluator.InGroups(groupIds, x.Edge.GroupId) && MatchesEdge(searchFilter, x.Edge))
                    .OrderBy(x => x.Depth)
                    .ThenBy(x => x.Edge.Uuid, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(x => RecordCopies.Copy(x.Edge, includeEmbedding: false))
                    .ToList();
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<EpisodicNode>> EpisodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default) =>
        // As upstream, episode search indexes content, source and source description, and takes the filter but does
        // not apply it.
        Read<IReadOnlyList<EpisodicNode>>(
            () => Bm25.Search(
                    query,
                    store.Episodes.Values,
                    e => e.Uuid,
                    e => [e.Content, e.Source.ToStorageName(), e.SourceDescription],
                    e => FilterEvaluator.InGroups(groupIds, e.GroupId),
                    limit)
                .Select(RecordCopies.Copy)
                .ToList(),
            cancellationToken);

    public Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(
        string query,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default) =>
        Read<IReadOnlyList<CommunityNode>>(
            () => Bm25.Search(
                    query,
                    store.Communities.Values,
                    c => c.Uuid,
                    c => [c.Name],
                    c => FilterEvaluator.InGroups(groupIds, c.GroupId),
                    limit)
                .Select(c => RecordCopies.Copy(c, includeEmbedding: false))
                .ToList(),
            cancellationToken);

    public Task<IReadOnlyList<CommunityNode>> CommunitySimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchVector);
        return Read<IReadOnlyList<CommunityNode>>(
            () => Similar(
                    store.Communities.Values.Where(c => FilterEvaluator.InGroups(groupIds, c.GroupId)),
                    c => c.NameEmbedding,
                    c => c.Uuid,
                    searchVector,
                    minScore,
                    limit)
                .Select(c => RecordCopies.Copy(c, includeEmbedding: false))
                .ToList(),
            cancellationToken);
    }

    public Task<IReadOnlySet<string>> GetConnectedNodeUuidsAsync(
        string centerNodeUuid,
        IReadOnlyList<string> candidateUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateUuids);
        return Read<IReadOnlySet<string>>(
            () =>
            {
                var candidates = candidateUuids.ToHashSet(StringComparer.Ordinal);
                var connected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in store.EntityEdges.Values)
                {
                    if (edge.SourceNodeUuid == centerNodeUuid && candidates.Contains(edge.TargetNodeUuid))
                    {
                        connected.Add(edge.TargetNodeUuid);
                    }

                    if (edge.TargetNodeUuid == centerNodeUuid && candidates.Contains(edge.SourceNodeUuid))
                    {
                        connected.Add(edge.SourceNodeUuid);
                    }
                }

                return connected;
            },
            cancellationToken);
    }

    /// <summary>Cosine similarity, or <see langword="null"/> when the vectors cannot be compared.</summary>
    internal static double? Cosine(IReadOnlyList<float> a, float[] b)
    {
        if (a.Count != b.Length || a.Count == 0)
        {
            return null;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < b.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? null : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static List<T> Similar<T>(
        IEnumerable<T> items,
        Func<T, float[]?> embeddingOf,
        Func<T, string> uuidOf,
        IReadOnlyList<float> searchVector,
        double minScore,
        int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        return items
            .Select(item => (Item: item, Score: embeddingOf(item) is { } embedding ? Cosine(searchVector, embedding) : null))
            .Where(x => x.Score is { } score && score > minScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => uuidOf(x.Item), StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }

    private bool MatchesEdge(SearchFilters filters, EntityEdge edge) =>
        FilterEvaluator.MatchesEdge(
            filters,
            edge,
            store.Entities.GetValueOrDefault(edge.SourceNodeUuid),
            store.Entities.GetValueOrDefault(edge.TargetNodeUuid));

    /// <summary>
    /// The traversal graph: facts from source to target, and episode-to-entity links from the episode, as upstream's
    /// <c>-[:RELATES_TO|MENTIONS*1..depth]-&gt;</c>.
    /// <see cref="Step.EdgeUuid"/> is set for facts.
    /// </summary>
    private Dictionary<string, List<Step>> BuildAdjacency()
    {
        var adjacency = new Dictionary<string, List<Step>>(StringComparer.Ordinal);
        void Add(string from, Step step)
        {
            if (!adjacency.TryGetValue(from, out var steps))
            {
                steps = [];
                adjacency[from] = steps;
            }

            steps.Add(step);
        }

        foreach (var edge in store.EntityEdges.Values)
        {
            if (!store.Entities.ContainsKey(edge.SourceNodeUuid) || !store.Entities.ContainsKey(edge.TargetNodeUuid))
            {
                continue;
            }

            Add(edge.SourceNodeUuid, new Step(edge.TargetNodeUuid, edge.Uuid));
        }

        foreach (var edge in store.EpisodicEdges.Values)
        {
            if (store.Episodes.ContainsKey(edge.SourceNodeUuid) && store.Entities.ContainsKey(edge.TargetNodeUuid))
            {
                Add(edge.SourceNodeUuid, new Step(edge.TargetNodeUuid, null));
            }
        }

        return adjacency;
    }

    /// <summary>Hop counts from <paramref name="origin"/> (0 for the origin itself) up to <paramref name="maxDepth"/>.</summary>
    private static Dictionary<string, int> Distances(
        Dictionary<string, List<Step>> adjacency,
        string origin,
        int maxDepth)
    {
        var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [origin] = 0 };
        var frontier = new Queue<string>();
        frontier.Enqueue(origin);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            var depth = distances[current];
            if (depth >= maxDepth || !adjacency.TryGetValue(current, out var steps))
            {
                continue;
            }

            foreach (var step in steps)
            {
                if (distances.TryAdd(step.To, depth + 1))
                {
                    frontier.Enqueue(step.To);
                }
            }
        }

        return distances;
    }

    /// <summary>
    /// The nodes at the end of a path of 1 to <paramref name="maxDepth"/> hops from <paramref name="origin"/>, with the
    /// fewest hops. The origin itself is included only when a cycle leads back to it, as in Cypher.
    /// </summary>
    private static Dictionary<string, int> Reach(
        Dictionary<string, List<Step>> adjacency,
        string origin,
        int maxDepth)
    {
        var reached = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<(string Node, int Depth)>();
        frontier.Enqueue((origin, 0));
        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            if (depth >= maxDepth || !adjacency.TryGetValue(current, out var steps))
            {
                continue;
            }

            foreach (var step in steps)
            {
                if (reached.TryAdd(step.To, depth + 1))
                {
                    frontier.Enqueue((step.To, depth + 1));
                }
            }
        }

        return reached;
    }

    private Task<T> Read<T>(Func<T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (store.Gate)
        {
            return Task.FromResult(read());
        }
    }

    private readonly record struct Step(string To, string? EdgeUuid);
}
