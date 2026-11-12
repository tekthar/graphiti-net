// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using Microsoft.Extensions.Logging;

namespace GraphitiNet.Tests.Maintenance;

/// <summary>
/// A deterministic embedder: a name listed in <see cref="Vectors"/> gets that vector; any other name gets
/// <see cref="DefaultVector"/>.
/// </summary>
internal sealed class VectorEmbedder : EmbedderClient
{
    public Dictionary<string, float[]> Vectors { get; } = new(StringComparer.Ordinal);

    public float[] DefaultVector { get; set; } = [1f, 0f, 0f];

    public List<string> Inputs { get; } = [];

    public Func<IReadOnlyList<string>, IReadOnlyList<float[]>>? BatchOverride { get; set; }

    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        lock (Inputs)
        {
            Inputs.Add(inputData);
        }

        return Task.FromResult(Vectors.GetValueOrDefault(inputData) ?? DefaultVector);
    }

    public override Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList, CancellationToken cancellationToken = default) =>
        BatchOverride is { } batch ? Task.FromResult(batch(inputDataList)) : base.CreateBatchAsync(inputDataList, cancellationToken);
}

/// <summary>Records the context of every prompt built.</summary>
internal sealed class PromptRecorder
{
    private readonly Lock _lock = new();

    public List<(string Name, IReadOnlyDictionary<string, object?> Context)> Built { get; } = [];

    public PromptLibrary Library => PromptLibrary.Create(PromptLibrary.BuiltinSpecs.Keys.ToDictionary(
        name => name,
        name => (ChatPromptBuilder)(context =>
        {
            lock (_lock)
            {
                Built.Add((name, context));
            }

            return PromptLibrary.GetDefaultBuilder(name)(context);
        })));

    public IReadOnlyDictionary<string, object?> Last(string name)
    {
        lock (_lock)
        {
            return Built.Last(b => b.Name == name).Context;
        }
    }
}

/// <summary>A logger that keeps formatted messages.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>Test setup: an in-memory graph, the fake LLM, the fake embedder and a prompt recorder.</summary>
internal sealed class TestHarness
{
    public TestHarness(int maxTokens = LLMConfig.DefaultMaxTokens, IGraphDriver? driver = null)
    {
        Driver = driver ?? new InMemoryGraphDriver();
        Llm = new FakeLlmClient(maxTokens);
        Clients = new GraphitiClients
        {
            Driver = Driver,
            LlmClient = Llm,
            Embedder = Embedder,
            PromptLibrary = Prompts.Library,
            Logger = Logger,
        };
    }

    public IGraphDriver Driver { get; }

    public FakeLlmClient Llm { get; }

    public VectorEmbedder Embedder { get; } = new();

    public PromptRecorder Prompts { get; } = new();

    public ListLogger Logger { get; } = new();

    public GraphitiClients Clients { get; }

    /// <summary>Stores entities with their name embeddings (from the fake embedder).</summary>
    public async Task StoreAsync(params EntityNode[] nodes)
    {
        foreach (var node in nodes)
        {
            node.NameEmbedding ??= await Embedder.CreateAsync(node.Name);
        }

        await Driver.EntityNodeOps.SaveBulkAsync(nodes);
        lock (Embedder.Inputs)
        {
            Embedder.Inputs.Clear();
        }
    }

    public static EpisodicNode Episode(
        string content = "content",
        EpisodeType source = EpisodeType.Message,
        string groupId = "group",
        DateTimeOffset? validAt = null) => new()
        {
            Name = "episode",
            GroupId = groupId,
            Source = source,
            SourceDescription = "test",
            Content = content,
            ValidAt = validAt ?? DateTimeOffset.UtcNow,
        };

    public static EntityNode Entity(
        string name,
        string groupId = "group",
        string[]? labels = null,
        string summary = "",
        string? uuid = null,
        Dictionary<string, object?>? attributes = null)
    {
        var node = uuid is null
            ? new EntityNode { Name = name, GroupId = groupId, Labels = labels ?? ["Entity"], Summary = summary }
            : new EntityNode { Uuid = uuid, Name = name, GroupId = groupId, Labels = labels ?? ["Entity"], Summary = summary };
        foreach (var (key, value) in attributes ?? [])
        {
            node.Attributes[key] = value;
        }

        return node;
    }

    public static EntityEdge Edge(string sourceUuid, string targetUuid, string fact) => new()
    {
        GroupId = "group",
        SourceNodeUuid = sourceUuid,
        TargetNodeUuid = targetUuid,
        Name = "TEST_RELATION",
        Fact = fact,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>A driver wrapper whose entity operations add an attribute-value lookup.</summary>
internal sealed class AttributeLookupDriver(InMemoryGraphDriver inner, Func<string, IReadOnlyDictionary<string, object?>, IReadOnlyList<string>?, IReadOnlyList<EntityNode>> lookup)
    : IGraphDriver
{
    public List<(string GroupId, IReadOnlyDictionary<string, object?> Attributes, IReadOnlyList<string>? Labels, int Limit)> Lookups { get; } = [];

    private IReadOnlyList<EntityNode> Lookup(string groupId, IReadOnlyDictionary<string, object?> attributes, IReadOnlyList<string>? labels) =>
        lookup(groupId, attributes, labels);

    public string Provider => inner.Provider;

    public IEpisodeNodeOperations EpisodeNodeOps => inner.EpisodeNodeOps;

    public IEntityNodeOperations EntityNodeOps => new Ops(this, inner.EntityNodeOps);

    public ICommunityNodeOperations CommunityNodeOps => inner.CommunityNodeOps;

    public ISagaNodeOperations SagaNodeOps => inner.SagaNodeOps;

    public IEpisodicEdgeOperations EpisodicEdgeOps => inner.EpisodicEdgeOps;

    public IEntityEdgeOperations EntityEdgeOps => inner.EntityEdgeOps;

    public ICommunityEdgeOperations CommunityEdgeOps => inner.CommunityEdgeOps;

    public IHasEpisodeEdgeOperations HasEpisodeEdgeOps => inner.HasEpisodeEdgeOps;

    public INextEpisodeEdgeOperations NextEpisodeEdgeOps => inner.NextEpisodeEdgeOps;

    public ISearchOperations SearchOps => inner.SearchOps;

    public IGraphMaintenanceOperations GraphOps => inner.GraphOps;

    public Task AddNodesAndEdgesBulkAsync(
        IReadOnlyList<EpisodicNode> episodicNodes, IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes, IReadOnlyList<EntityEdge> entityEdges,
        CancellationToken cancellationToken = default) =>
        inner.AddNodesAndEdgesBulkAsync(episodicNodes, episodicEdges, entityNodes, entityEdges, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private sealed class Ops(AttributeLookupDriver owner, IEntityNodeOperations ops) : IEntityNodeOperations
    {
        public Task SaveAsync(EntityNode item, CancellationToken cancellationToken = default) => ops.SaveAsync(item, cancellationToken);

        public Task SaveBulkAsync(IReadOnlyList<EntityNode> items, CancellationToken cancellationToken = default) =>
            ops.SaveBulkAsync(items, cancellationToken);

        public Task DeleteAsync(EntityNode item, CancellationToken cancellationToken = default) => ops.DeleteAsync(item, cancellationToken);

        public Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default) =>
            ops.DeleteByUuidsAsync(uuids, cancellationToken);

        public Task<EntityNode> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default) =>
            ops.GetByUuidAsync(uuid, cancellationToken);

        public Task<IReadOnlyList<EntityNode>> GetByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default) =>
            ops.GetByUuidsAsync(uuids, cancellationToken);

        public Task<IReadOnlyList<EntityNode>> GetByGroupIdsAsync(
            IReadOnlyList<string> groupIds, int? limit = null, string? uuidCursor = null,
            CancellationToken cancellationToken = default) =>
            ops.GetByGroupIdsAsync(groupIds, limit, uuidCursor, cancellationToken);

        public Task DeleteByGroupIdAsync(string groupId, CancellationToken cancellationToken = default) =>
            ops.DeleteByGroupIdAsync(groupId, cancellationToken);

        public Task LoadEmbeddingsAsync(EntityNode node, CancellationToken cancellationToken = default) =>
            ops.LoadEmbeddingsAsync(node, cancellationToken);

        public Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityNode> nodes, CancellationToken cancellationToken = default) =>
            ops.LoadEmbeddingsBulkAsync(nodes, cancellationToken);

        public Task<IReadOnlyList<EntityNode>> GetByNamesAsync(
            IReadOnlyList<string> names, IReadOnlyList<string> groupIds, CancellationToken cancellationToken = default) =>
            ops.GetByNamesAsync(names, groupIds, cancellationToken);

        public Task<IReadOnlyList<EntityNode>> GetByAttributeValuesAsync(
            string groupId, IReadOnlyDictionary<string, object?> attributes, IReadOnlyList<string>? nodeLabels = null,
            int limit = 100, CancellationToken cancellationToken = default)
        {
            owner.Lookups.Add((groupId, attributes, nodeLabels, limit));
            return Task.FromResult(owner.Lookup(groupId, attributes, nodeLabels));
        }
    }
}
