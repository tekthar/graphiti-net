// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Embedder;
using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

/// <summary>
/// A hand-written driver for search tests: each search primitive is a settable delegate (returning nothing by
/// default) and every call is recorded. Members search never uses throw.
/// </summary>
internal sealed class FakeGraphDriver : IGraphDriver
{
    public FakeSearchOperations Search { get; } = new();

    public FakeGraphOperations Graph { get; } = new();

    public FakeEntityNodeOperations EntityNodes { get; } = new();

    public FakeEntityEdgeOperations EntityEdges { get; } = new();

    public FakeCommunityNodeOperations CommunityNodes { get; } = new();

    public FakeEpisodeNodeOperations EpisodeNodes { get; } = new();

    public string Provider => "fake";

    public IEpisodeNodeOperations EpisodeNodeOps => EpisodeNodes;

    public IEntityNodeOperations EntityNodeOps => EntityNodes;

    public ICommunityNodeOperations CommunityNodeOps => CommunityNodes;

    public ISagaNodeOperations SagaNodeOps => throw new NotSupportedException();

    public IEpisodicEdgeOperations EpisodicEdgeOps => throw new NotSupportedException();

    public IEntityEdgeOperations EntityEdgeOps => EntityEdges;

    public ICommunityEdgeOperations CommunityEdgeOps => throw new NotSupportedException();

    public IHasEpisodeEdgeOperations HasEpisodeEdgeOps => throw new NotSupportedException();

    public INextEpisodeEdgeOperations NextEpisodeEdgeOps => throw new NotSupportedException();

    public ISearchOperations SearchOps => Search;

    public IGraphMaintenanceOperations GraphOps => Graph;

    public Task AddNodesAndEdgesBulkAsync(
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One recorded driver call.</summary>
internal sealed record Call(string Method, IReadOnlyList<object?> Args);

internal sealed class FakeSearchOperations : ISearchOperations
{
    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Func<string, IReadOnlyList<EntityNode>> NodeFulltext { get; set; } = _ => [];

    public Func<IReadOnlyList<float>, IReadOnlyList<EntityNode>> NodeSimilarity { get; set; } = _ => [];

    public Func<IReadOnlyList<string>, IReadOnlyList<EntityNode>> NodeBfs { get; set; } = _ => [];

    public Func<string, IReadOnlyList<EntityEdge>> EdgeFulltext { get; set; } = _ => [];

    public Func<IReadOnlyList<float>, IReadOnlyList<EntityEdge>> EdgeSimilarity { get; set; } = _ => [];

    public Func<IReadOnlyList<string>, IReadOnlyList<EntityEdge>> EdgeBfs { get; set; } = _ => [];

    public Func<string, IReadOnlyList<EpisodicNode>> EpisodeFulltext { get; set; } = _ => [];

    public Func<string, IReadOnlyList<CommunityNode>> CommunityFulltext { get; set; } = _ => [];

    public Func<IReadOnlyList<float>, IReadOnlyList<CommunityNode>> CommunitySimilarity { get; set; } = _ => [];

    /// <summary>Edges joining entities, for <see cref="GetConnectedNodeUuidsAsync"/>.</summary>
    public HashSet<(string, string)> Connections { get; } = [];

    public int CallCount(string method) => Calls.Count(call => call.Method == method);

    public Task<IReadOnlyList<EntityNode>> NodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(NodeFulltextSearchAsync), query, searchFilter, groupIds, limit);
        return Task.FromResult(NodeFulltext(query));
    }

    public Task<IReadOnlyList<EntityNode>> NodeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        double minScore = 0.6,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(NodeSimilaritySearchAsync), searchVector, searchFilter, groupIds, limit, minScore);
        return Task.FromResult(NodeSimilarity(searchVector));
    }

    public Task<IReadOnlyList<EntityNode>> NodeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        SearchFilters searchFilter,
        int maxDepth,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(NodeBfsSearchAsync), originUuids, searchFilter, maxDepth, groupIds, limit);
        return Task.FromResult(NodeBfs(originUuids));
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(EdgeFulltextSearchAsync), query, searchFilter, groupIds, limit);
        return Task.FromResult(EdgeFulltext(query));
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        string? sourceNodeUuid,
        string? targetNodeUuid,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        double minScore = 0.6,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(EdgeSimilaritySearchAsync), searchVector, searchFilter, groupIds, limit, minScore);
        return Task.FromResult(EdgeSimilarity(searchVector));
    }

    public Task<IReadOnlyList<EntityEdge>> EdgeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        int maxDepth,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(EdgeBfsSearchAsync), originUuids, maxDepth, searchFilter, groupIds, limit);
        return Task.FromResult(EdgeBfs(originUuids));
    }

    public Task<IReadOnlyList<EpisodicNode>> EpisodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(EpisodeFulltextSearchAsync), query, searchFilter, groupIds, limit);
        return Task.FromResult(EpisodeFulltext(query));
    }

    public Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(
        string query,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(CommunityFulltextSearchAsync), query, groupIds, limit);
        return Task.FromResult(CommunityFulltext(query));
    }

    public Task<IReadOnlyList<CommunityNode>> CommunitySimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        IReadOnlyList<string>? groupIds = null,
        int limit = 10,
        double minScore = 0.6,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(CommunitySimilaritySearchAsync), searchVector, groupIds, limit, minScore);
        return Task.FromResult(CommunitySimilarity(searchVector));
    }

    public Task<IReadOnlySet<string>> GetConnectedNodeUuidsAsync(
        string centerNodeUuid,
        IReadOnlyList<string> candidateUuids,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(GetConnectedNodeUuidsAsync), centerNodeUuid, candidateUuids);
        IReadOnlySet<string> connected = candidateUuids
            .Where(uuid => Connections.Contains((centerNodeUuid, uuid)) || Connections.Contains((uuid, centerNodeUuid)))
            .ToHashSet();
        return Task.FromResult(connected);
    }

    private void Record(string method, params object?[] args)
    {
        lock (_calls)
        {
            _calls.Add(new Call(method, args));
        }
    }
}

internal sealed class FakeGraphOperations : IGraphMaintenanceOperations
{
    public Dictionary<string, int> MentionCounts { get; } = [];

    public List<IReadOnlyList<string>> CountEpisodeMentionsCalls { get; } = [];

    public Func<IReadOnlyList<EpisodicNode>, IReadOnlyList<EntityNode>> MentionedNodes { get; set; } = _ => [];

    public Func<IReadOnlyList<EntityNode>, IReadOnlyList<CommunityNode>> CommunitiesByNodes { get; set; } = _ => [];

    public Task<IReadOnlyDictionary<string, int>> CountEpisodeMentionsAsync(
        IReadOnlyList<string> entityUuids,
        CancellationToken cancellationToken = default)
    {
        CountEpisodeMentionsCalls.Add(entityUuids);
        IReadOnlyDictionary<string, int> counts = entityUuids.Distinct()
            .ToDictionary(uuid => uuid, uuid => MentionCounts.GetValueOrDefault(uuid));
        return Task.FromResult(counts);
    }

    public Task<IReadOnlyList<EntityNode>> GetMentionedNodesAsync(
        IReadOnlyList<EpisodicNode> episodes,
        CancellationToken cancellationToken = default) => Task.FromResult(MentionedNodes(episodes));

    public Task<IReadOnlyList<CommunityNode>> GetCommunitiesByNodesAsync(
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default) => Task.FromResult(CommunitiesByNodes(nodes));

    public Task ClearDataAsync(IReadOnlyList<string>? groupIds = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task BuildIndicesAndConstraintsAsync(
        bool deleteExisting = false,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>> GetEntityProjectionAsync(
        string groupId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> GetEntityGroupIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task RemoveCommunitiesAsync(
        IReadOnlyList<string>? groupIds = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<CommunityNode>> GetNeighborCommunitiesAsync(
        string entityUuid,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Record operations that throw unless overridden.</summary>
internal abstract class ThrowingRecordOperations<T> : IRecordOperations<T>
{
    public virtual Task SaveAsync(T item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task SaveBulkAsync(IReadOnlyList<T> items, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task DeleteAsync(T item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<T> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<T>> GetByUuidsAsync(
        IReadOnlyList<string> uuids,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public virtual Task<IReadOnlyList<T>> GetByGroupIdsAsync(
        IReadOnlyList<string> groupIds,
        int? limit = null,
        string? uuidCursor = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteByGroupIdAsync(string groupId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeEntityNodeOperations : ThrowingRecordOperations<EntityNode>, IEntityNodeOperations
{
    public Dictionary<string, float[]> Embeddings { get; } = [];

    public Task LoadEmbeddingsAsync(EntityNode node, CancellationToken cancellationToken = default)
    {
        node.NameEmbedding = Embeddings.GetValueOrDefault(node.Uuid);
        return Task.CompletedTask;
    }

    public async Task LoadEmbeddingsBulkAsync(
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default)
    {
        foreach (var node in nodes)
        {
            await LoadEmbeddingsAsync(node, cancellationToken);
        }
    }
}

internal sealed class FakeEntityEdgeOperations : ThrowingRecordOperations<EntityEdge>, IEntityEdgeOperations
{
    public Dictionary<string, float[]> Embeddings { get; } = [];

    public Task<IReadOnlyList<EntityEdge>> GetBetweenNodesAsync(
        string sourceNodeUuid,
        string targetNodeUuid,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<EntityEdge>> GetByNodeUuidAsync(
        string nodeUuid,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task LoadEmbeddingsAsync(EntityEdge edge, CancellationToken cancellationToken = default)
    {
        edge.FactEmbedding = Embeddings.GetValueOrDefault(edge.Uuid);
        return Task.CompletedTask;
    }

    public async Task LoadEmbeddingsBulkAsync(
        IReadOnlyList<EntityEdge> edges,
        CancellationToken cancellationToken = default)
    {
        foreach (var edge in edges)
        {
            await LoadEmbeddingsAsync(edge, cancellationToken);
        }
    }
}

internal sealed class FakeCommunityNodeOperations : ThrowingRecordOperations<CommunityNode>, ICommunityNodeOperations
{
    public Dictionary<string, float[]> Embeddings { get; } = [];

    public Task LoadNameEmbeddingAsync(CommunityNode node, CancellationToken cancellationToken = default)
    {
        node.NameEmbedding = Embeddings.GetValueOrDefault(node.Uuid);
        return Task.CompletedTask;
    }
}

internal sealed class FakeEpisodeNodeOperations : ThrowingRecordOperations<EpisodicNode>, IEpisodeNodeOperations
{
    public Dictionary<string, EpisodicNode> Episodes { get; } = [];

    public List<IReadOnlyList<string>> GetByUuidsCalls { get; } = [];

    public override Task<IReadOnlyList<EpisodicNode>> GetByUuidsAsync(
        IReadOnlyList<string> uuids,
        CancellationToken cancellationToken = default)
    {
        GetByUuidsCalls.Add(uuids);
        IReadOnlyList<EpisodicNode> found = [.. uuids.Where(Episodes.ContainsKey).Select(uuid => Episodes[uuid])];
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<EpisodicNode>> GetByEntityNodeUuidAsync(
        string entityNodeUuid,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        DateTimeOffset referenceTime,
        int lastN = 3,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<EpisodicNode>> GetBySagaAsync(
        string sagaUuid,
        DateTimeOffset? createdAfter = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>An embedder that returns a fixed vector and records its inputs.</summary>
internal sealed class FakeEmbedder(float[] vector) : EmbedderClient
{
    public List<string> Inputs { get; } = [];

    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        Inputs.Add(inputData);
        return Task.FromResult(vector);
    }
}

/// <summary>A cross-encoder scoring passages with a function, best first, recording what it saw.</summary>
internal sealed class FakeCrossEncoder(Func<string, double> score) : CrossEncoderClient
{
    public List<string> RankedPassages { get; } = [];

    public override Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default)
    {
        RankedPassages.AddRange(passages);
        IReadOnlyList<RankedPassage> ranked =
        [
            .. passages.Select(passage => new RankedPassage(passage, score(passage)))
                .OrderByDescending(passage => passage.Score),
        ];
        return Task.FromResult(ranked);
    }
}

/// <summary>Builders for test records.</summary>
internal static class Make
{
    public static EntityEdge Edge(
        string uuid,
        string fact,
        string source = "source",
        string target = "target",
        params string[] episodes) => new()
        {
            Uuid = uuid,
            SourceNodeUuid = source,
            TargetNodeUuid = target,
            Name = "relates_to",
            GroupId = "group_1",
            Fact = fact,
            CreatedAt = DateTimeOffset.UtcNow,
            Episodes = [.. episodes],
        };

    public static EntityNode Node(string uuid, string name = "", string groupId = "1") =>
        new() { Uuid = uuid, Name = name.Length == 0 ? uuid : name, GroupId = groupId, Labels = ["Entity"] };

    public static EpisodicNode Episode(string uuid, string content) => new()
    {
        Uuid = uuid,
        Name = uuid,
        GroupId = "group_1",
        Source = EpisodeType.Text,
        SourceDescription = "test",
        Content = content,
        ValidAt = DateTimeOffset.UtcNow,
    };

    public static CommunityNode Community(string uuid, string name) =>
        new() { Uuid = uuid, Name = name, GroupId = "group_1" };
}
