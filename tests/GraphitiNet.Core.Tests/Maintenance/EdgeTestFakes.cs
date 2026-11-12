// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using System.Text.RegularExpressions;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Maintenance;

/// <summary>One LLM call the fake client received.</summary>
internal sealed record LlmCall(string PromptName, IReadOnlyList<Message> Messages, Type? ResponseModel,
    ModelSize ModelSize, string? GroupId, int? MaxTokens, bool AttributeExtraction)
{
    public string Text => string.Join("\n", Messages.Select(m => m.Content));
}

/// <summary>An LLM client that answers each prompt name with canned JSON and records every call.</summary>
internal sealed class FakeLlmClient : LLMClient
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Func<LlmCall, object?>> _handlers = new(StringComparer.Ordinal);

    public FakeLlmClient()
        : base(new LLMConfig())
    {
    }

    public List<LlmCall> Calls { get; } = [];

    public IReadOnlyList<LlmCall> CallsFor(string promptName)
    {
        lock (_lock)
        {
            return Calls.Where(c => c.PromptName == promptName).ToList();
        }
    }

    /// <summary>Answers <paramref name="promptName"/> with <paramref name="response"/> serialised as JSON.</summary>
    public FakeLlmClient On(string promptName, object? response) => On(promptName, _ => response);

    public FakeLlmClient On(string promptName, Func<LlmCall, object?> handler)
    {
        _handlers[promptName] = handler;
        return this;
    }

    public override Task<JsonElement> GenerateResponseAsync(
        IList<Message> messages,
        Type? responseModel = null,
        int? maxTokens = null,
        ModelSize modelSize = ModelSize.Medium,
        string? groupId = null,
        string? promptName = null,
        bool attributeExtraction = false,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var call = new LlmCall(promptName ?? "", messages.ToList(), responseModel, modelSize, groupId, maxTokens,
            attributeExtraction);
        lock (_lock)
        {
            Calls.Add(call);
        }

        if (!_handlers.TryGetValue(call.PromptName, out var handler))
        {
            throw new InvalidOperationException($"No canned response for {call.PromptName}.");
        }

        var response = handler(call);
        return Task.FromResult(response is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(response));
    }

    protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages,
        Type? responseModel,
        int maxTokens,
        ModelSize modelSize,
        string? model,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>A deterministic bag-of-words embedder: each word adds 1 to one of 64 dimensions; unit length.</summary>
internal sealed partial class FakeEmbedder : EmbedderClient
{
    public int BatchCalls { get; private set; }

    public List<IReadOnlyList<string>> Batches { get; } = [];

    public static float[] TextVector(string text)
    {
        var v = new float[64];
        foreach (Match token in Word().Matches(text.ToLowerInvariant()))
        {
            uint hash = 2166136261;
            foreach (var c in token.Value)
            {
                hash = (hash ^ c) * 16777619;
            }

            v[hash % 64] += 1;
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm == 0)
        {
            norm = 1;
        }

        return v.Select(x => x / norm).ToArray();
    }

    public int CreateCalls { get; private set; }

    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        return Task.FromResult(TextVector(inputData));
    }

    public override Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList,
        CancellationToken cancellationToken = default)
    {
        BatchCalls++;
        Batches.Add(inputDataList.ToList());
        return Task.FromResult<IReadOnlyList<float[]>>(inputDataList.Select(TextVector).ToList());
    }

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();
}

/// <summary>An edge search call the recording driver saw.</summary>
internal sealed record EdgeSearchCall(string Kind, SearchFilters Filter, IReadOnlyList<float>? Vector);

/// <summary>
/// The in-memory driver with recorded edge searches. <see cref="EdgeSearchOverride"/>, when set, answers every edge
/// full-text and similarity search instead of the store (return <see langword="null"/> to fall through).
/// </summary>
internal sealed class RecordingDriver : IGraphDriver
{
    private readonly InMemoryGraphDriver _inner = new();
    private readonly RecordingSearchOperations _search;

    public RecordingDriver() => _search = new RecordingSearchOperations(this, _inner.SearchOps);

    public InMemoryGraphDriver Inner => _inner;

    public List<EdgeSearchCall> EdgeSearches { get; } = [];

    public Func<SearchFilters, IReadOnlyList<EntityEdge>?>? EdgeSearchOverride { get; set; }

    /// <summary>The filters of the full-text edge searches: one per <c>SearchEngine.SearchAsync</c> call.</summary>
    public IReadOnlyList<SearchFilters> SearchFiltersUsed =>
        EdgeSearches.Where(s => s.Kind == "fulltext").Select(s => s.Filter).ToList();

    public string Provider => _inner.Provider;

    public IEpisodeNodeOperations EpisodeNodeOps => _inner.EpisodeNodeOps;

    public IEntityNodeOperations EntityNodeOps => _inner.EntityNodeOps;

    public ICommunityNodeOperations CommunityNodeOps => _inner.CommunityNodeOps;

    public ISagaNodeOperations SagaNodeOps => _inner.SagaNodeOps;

    public IEpisodicEdgeOperations EpisodicEdgeOps => _inner.EpisodicEdgeOps;

    public IEntityEdgeOperations EntityEdgeOps => _inner.EntityEdgeOps;

    public ICommunityEdgeOperations CommunityEdgeOps => _inner.CommunityEdgeOps;

    public IHasEpisodeEdgeOperations HasEpisodeEdgeOps => _inner.HasEpisodeEdgeOps;

    public INextEpisodeEdgeOperations NextEpisodeEdgeOps => _inner.NextEpisodeEdgeOps;

    public ISearchOperations SearchOps => _search;

    public IGraphMaintenanceOperations GraphOps => _inner.GraphOps;

    public Task AddNodesAndEdgesBulkAsync(
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        CancellationToken cancellationToken = default) =>
        _inner.AddNodesAndEdgesBulkAsync(episodicNodes, episodicEdges, entityNodes, entityEdges, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class RecordingSearchOperations(RecordingDriver owner, ISearchOperations inner) : ISearchOperations
    {
        private readonly Lock _lock = new();

        private void Record(EdgeSearchCall call)
        {
            lock (_lock)
            {
                owner.EdgeSearches.Add(call);
            }
        }

        public Task<IReadOnlyList<EntityNode>> NodeFulltextSearchAsync(string query, SearchFilters searchFilter,
            IReadOnlyList<string>? groupIds = null, int limit = 10, CancellationToken cancellationToken = default) =>
            inner.NodeFulltextSearchAsync(query, searchFilter, groupIds, limit, cancellationToken);

        public Task<IReadOnlyList<EntityNode>> NodeSimilaritySearchAsync(IReadOnlyList<float> searchVector,
            SearchFilters searchFilter, IReadOnlyList<string>? groupIds = null, int limit = 10, double minScore = 0.6,
            CancellationToken cancellationToken = default) =>
            inner.NodeSimilaritySearchAsync(searchVector, searchFilter, groupIds, limit, minScore, cancellationToken);

        public Task<IReadOnlyList<EntityNode>> NodeBfsSearchAsync(IReadOnlyList<string> originUuids,
            SearchFilters searchFilter, int maxDepth, IReadOnlyList<string>? groupIds = null, int limit = 10,
            CancellationToken cancellationToken = default) =>
            inner.NodeBfsSearchAsync(originUuids, searchFilter, maxDepth, groupIds, limit, cancellationToken);

        public Task<IReadOnlyList<EntityEdge>> EdgeFulltextSearchAsync(string query, SearchFilters searchFilter,
            IReadOnlyList<string>? groupIds = null, int limit = 10, CancellationToken cancellationToken = default)
        {
            Record(new EdgeSearchCall("fulltext", searchFilter, null));
            return owner.EdgeSearchOverride?.Invoke(searchFilter) is { } edges
                ? Task.FromResult(edges)
                : inner.EdgeFulltextSearchAsync(query, searchFilter, groupIds, limit, cancellationToken);
        }

        public Task<IReadOnlyList<EntityEdge>> EdgeSimilaritySearchAsync(IReadOnlyList<float> searchVector,
            string? sourceNodeUuid, string? targetNodeUuid, SearchFilters searchFilter,
            IReadOnlyList<string>? groupIds = null, int limit = 10, double minScore = 0.6,
            CancellationToken cancellationToken = default)
        {
            Record(new EdgeSearchCall("similarity", searchFilter, searchVector));
            return owner.EdgeSearchOverride?.Invoke(searchFilter) is { } edges
                ? Task.FromResult(edges)
                : inner.EdgeSimilaritySearchAsync(searchVector, sourceNodeUuid, targetNodeUuid, searchFilter,
                    groupIds, limit, minScore, cancellationToken);
        }

        public Task<IReadOnlyList<EntityEdge>> EdgeBfsSearchAsync(IReadOnlyList<string> originUuids, int maxDepth,
            SearchFilters searchFilter, IReadOnlyList<string>? groupIds = null, int limit = 10,
            CancellationToken cancellationToken = default)
        {
            Record(new EdgeSearchCall("bfs", searchFilter, null));
            return inner.EdgeBfsSearchAsync(originUuids, maxDepth, searchFilter, groupIds, limit, cancellationToken);
        }

        public Task<IReadOnlyList<EpisodicNode>> EpisodeFulltextSearchAsync(string query, SearchFilters searchFilter,
            IReadOnlyList<string>? groupIds = null, int limit = 10, CancellationToken cancellationToken = default) =>
            inner.EpisodeFulltextSearchAsync(query, searchFilter, groupIds, limit, cancellationToken);

        public Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(string query,
            IReadOnlyList<string>? groupIds = null, int limit = 10, CancellationToken cancellationToken = default) =>
            inner.CommunityFulltextSearchAsync(query, groupIds, limit, cancellationToken);

        public Task<IReadOnlyList<CommunityNode>> CommunitySimilaritySearchAsync(IReadOnlyList<float> searchVector,
            IReadOnlyList<string>? groupIds = null, int limit = 10, double minScore = 0.6,
            CancellationToken cancellationToken = default) =>
            inner.CommunitySimilaritySearchAsync(searchVector, groupIds, limit, minScore, cancellationToken);

        public Task<IReadOnlySet<string>> GetConnectedNodeUuidsAsync(string centerNodeUuid,
            IReadOnlyList<string> candidateUuids, CancellationToken cancellationToken = default) =>
            inner.GetConnectedNodeUuidsAsync(centerNodeUuid, candidateUuids, cancellationToken);
    }
}

/// <summary>Builders shared by the edge tests.</summary>
internal static class EdgeTestData
{
    public static (GraphitiClients Clients, FakeLlmClient Llm, RecordingDriver Driver, FakeEmbedder Embedder) Clients()
    {
        var llm = new FakeLlmClient();
        var driver = new RecordingDriver();
        var embedder = new FakeEmbedder();
        var clients = new GraphitiClients { Driver = driver, LlmClient = llm, Embedder = embedder };
        return (clients, llm, driver, embedder);
    }

    public static EntityEdge Edge(
        string source,
        string target,
        string fact,
        string name = "test_edge",
        string groupId = "group_1",
        string? uuid = null,
        IEnumerable<string>? episodes = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? validAt = null,
        DateTimeOffset? invalidAt = null,
        IDictionary<string, object?>? attributes = null)
    {
        var edge = new EntityEdge
        {
            Uuid = uuid ?? Guid.NewGuid().ToString(),
            SourceNodeUuid = source,
            TargetNodeUuid = target,
            Name = name,
            GroupId = groupId,
            Fact = fact,
            Episodes = episodes?.ToList() ?? [],
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            ValidAt = validAt,
            InvalidAt = invalidAt,
            Attributes = attributes is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(attributes),
        };
        return edge;
    }

    public static EpisodicNode Episode(
        string uuid = "episode_uuid",
        string content = "Episode content",
        DateTimeOffset? validAt = null,
        string groupId = "group_1") => new()
    {
        Uuid = uuid,
        Name = "Episode",
        GroupId = groupId,
        Source = EpisodeType.Message,
        SourceDescription = "desc",
        Content = content,
        ValidAt = validAt ?? DateTimeOffset.UtcNow,
    };

    public static EntityNode Node(string uuid, string name, params string[] labels) => new()
    {
        Uuid = uuid,
        Name = name,
        GroupId = "group_1",
        Labels = labels.Length == 0 ? ["Entity"] : labels,
    };

    public static object NoDuplicates => new { duplicate_facts = Array.Empty<int>(), contradicted_facts = Array.Empty<int>() };

    public static object NoTimestamps => new { valid_at = (string?)null, invalid_at = (string?)null };

    public static readonly IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>> EmptyMap =
        new Dictionary<(string Source, string Target), IReadOnlyList<string>>();

    public static readonly IReadOnlyDictionary<string, Type> NoTypes = new Dictionary<string, Type>();
}
