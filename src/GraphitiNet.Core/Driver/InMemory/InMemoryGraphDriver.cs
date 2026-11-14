// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using System.Text.Json.Serialization;
using GraphitiNet.Models;

namespace GraphitiNet.Driver.InMemory;

/// <summary>
/// A graph driver that keeps the whole graph in process memory: the zero-install option for tests, demos and small
/// apps. It implements the full <see cref="IGraphDriver"/> contract and is thread-safe.
/// </summary>
/// <remarks>
/// <para>Full-text search ranks with Okapi BM25 (k1 = 1.2, b = 0.75) over lower-cased terms split on every character
/// that is not a letter or digit, scoring each indexed field separately and summing, as Lucene does for upstream's
/// Neo4j indexes. That makes its keyword ranking the closest to upstream of the graphiti-net drivers. Similarity
/// search is exact cosine similarity over every stored vector.</para>
/// <para>Every call takes one lock, so readers and writers never see a half-applied change; searches scan the whole
/// graph, which suits graphs up to tens of thousands of records. The graph can be saved to and loaded from a JSON
/// file with <see cref="SaveToFileAsync"/> and <see cref="LoadFromFileAsync"/>.</para>
/// </remarks>
public sealed class InMemoryGraphDriver : IGraphDriver
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly InMemoryStore _store = new();

    /// <summary>Creates an empty graph.</summary>
    public InMemoryGraphDriver()
    {
        EpisodeNodeOps = new InMemoryEpisodeNodeOperations(_store);
        EntityNodeOps = new InMemoryEntityNodeOperations(_store);
        CommunityNodeOps = new InMemoryCommunityNodeOperations(_store);
        SagaNodeOps = new InMemorySagaNodeOperations(_store);
        EpisodicEdgeOps = new InMemoryEpisodicEdgeOperations(_store);
        EntityEdgeOps = new InMemoryEntityEdgeOperations(_store);
        CommunityEdgeOps = new InMemoryCommunityEdgeOperations(_store);
        HasEpisodeEdgeOps = new InMemoryHasEpisodeEdgeOperations(_store);
        NextEpisodeEdgeOps = new InMemoryNextEpisodeEdgeOperations(_store);
        SearchOps = new InMemorySearchOperations(_store);
        GraphOps = new InMemoryGraphMaintenanceOperations(_store);
    }

    /// <summary>The provider name, <c>memory</c>.</summary>
    public const string ProviderName = "memory";

    /// <inheritdoc />
    public string Provider => ProviderName;

    /// <inheritdoc />
    public IEpisodeNodeOperations EpisodeNodeOps { get; }

    /// <inheritdoc />
    public IEntityNodeOperations EntityNodeOps { get; }

    /// <inheritdoc />
    public ICommunityNodeOperations CommunityNodeOps { get; }

    /// <inheritdoc />
    public ISagaNodeOperations SagaNodeOps { get; }

    /// <inheritdoc />
    public IEpisodicEdgeOperations EpisodicEdgeOps { get; }

    /// <inheritdoc />
    public IEntityEdgeOperations EntityEdgeOps { get; }

    /// <inheritdoc />
    public ICommunityEdgeOperations CommunityEdgeOps { get; }

    /// <inheritdoc />
    public IHasEpisodeEdgeOperations HasEpisodeEdgeOps { get; }

    /// <inheritdoc />
    public INextEpisodeEdgeOperations NextEpisodeEdgeOps { get; }

    /// <inheritdoc />
    public ISearchOperations SearchOps { get; }

    /// <inheritdoc />
    public IGraphMaintenanceOperations GraphOps { get; }

    /// <summary>Saves every record under one lock, so readers see all of them or none.</summary>
    public Task AddNodesAndEdgesBulkAsync(
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodicNodes);
        ArgumentNullException.ThrowIfNull(episodicEdges);
        ArgumentNullException.ThrowIfNull(entityNodes);
        ArgumentNullException.ThrowIfNull(entityEdges);
        cancellationToken.ThrowIfCancellationRequested();

        var episodes = episodicNodes.Select(RecordCopies.Copy).ToList();
        var entities = entityNodes.Select(n => RecordCopies.Copy(n, includeEmbedding: true)).ToList();
        var mentions = episodicEdges.Select(RecordCopies.Copy).ToList();
        var facts = entityEdges.Select(e => RecordCopies.Copy(e, includeEmbedding: true)).ToList();
        lock (_store.Gate)
        {
            _store.CheckIdempotencyKeys(episodes);
            Put(_store.Episodes, episodes, n => n.Uuid);
            Put(_store.Entities, entities, n => n.Uuid);
            Put(_store.EpisodicEdges, mentions, e => e.Uuid);
            Put(_store.EntityEdges, facts, e => e.Uuid);
        }

        return Task.CompletedTask;
    }

    /// <summary>Writes the whole graph, embeddings included, to a JSON stream.</summary>
    public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Snapshot snapshot;
        lock (_store.Gate)
        {
            snapshot = new Snapshot
            {
                Episodes = [.. _store.Episodes.Values.Select(RecordCopies.Copy)],
                Entities = [.. _store.Entities.Values.Select(n => RecordCopies.Copy(n, includeEmbedding: true))],
                Communities = [.. _store.Communities.Values.Select(n => RecordCopies.Copy(n, includeEmbedding: true))],
                Sagas = [.. _store.Sagas.Values.Select(RecordCopies.Copy)],
                EpisodicEdges = [.. _store.EpisodicEdges.Values.Select(RecordCopies.Copy)],
                EntityEdges = [.. _store.EntityEdges.Values.Select(e => RecordCopies.Copy(e, includeEmbedding: true))],
                CommunityEdges = [.. _store.CommunityEdges.Values.Select(RecordCopies.Copy)],
                HasEpisodeEdges = [.. _store.HasEpisodeEdges.Values.Select(RecordCopies.Copy)],
                NextEpisodeEdges = [.. _store.NextEpisodeEdges.Values.Select(RecordCopies.Copy)],
            };
        }

        await JsonSerializer.SerializeAsync(stream, snapshot, SnapshotOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the whole graph, embeddings included, to a JSON file, replacing it if it exists. The file is
    /// written next to <paramref name="path"/> first and then moved over it, so a failed or cancelled save leaves the
    /// previous file intact.</summary>
    public async Task SaveToFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var temporary = path + ".tmp";
        try
        {
            var stream = File.Create(temporary);
            await using (stream.ConfigureAwait(false))
            {
                await SaveAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Reads a graph written by <see cref="SaveAsync"/>.</summary>
    public static async Task<InMemoryGraphDriver> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(stream, SnapshotOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The stream holds no graph.");
        if (snapshot.Version != Snapshot.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported in-memory graph file version {snapshot.Version}.");
        }

        var driver = new InMemoryGraphDriver();
        var store = driver._store;
        lock (store.Gate)
        {
            Put(store.Episodes, snapshot.Episodes.Select(RecordCopies.Copy), n => n.Uuid);
            Put(store.Entities, snapshot.Entities.Select(n => RecordCopies.Copy(n, includeEmbedding: true)), n => n.Uuid);
            Put(
                store.Communities,
                snapshot.Communities.Select(n => RecordCopies.Copy(n, includeEmbedding: true)),
                n => n.Uuid);
            Put(store.Sagas, snapshot.Sagas.Select(RecordCopies.Copy), n => n.Uuid);
            Put(store.EpisodicEdges, snapshot.EpisodicEdges.Select(RecordCopies.Copy), e => e.Uuid);
            Put(
                store.EntityEdges,
                snapshot.EntityEdges.Select(e => RecordCopies.Copy(e, includeEmbedding: true)),
                e => e.Uuid);
            Put(store.CommunityEdges, snapshot.CommunityEdges.Select(RecordCopies.Copy), e => e.Uuid);
            Put(store.HasEpisodeEdges, snapshot.HasEpisodeEdges.Select(RecordCopies.Copy), e => e.Uuid);
            Put(store.NextEpisodeEdges, snapshot.NextEpisodeEdges.Select(RecordCopies.Copy), e => e.Uuid);
        }

        return driver;
    }

    /// <summary>Reads a graph written by <see cref="SaveToFileAsync"/>.</summary>
    public static async Task<InMemoryGraphDriver> LoadFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return await LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Nothing to release.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void Put<T>(Dictionary<string, T> items, IEnumerable<T> values, Func<T, string> uuidOf)
    {
        foreach (var value in values)
        {
            items[uuidOf(value)] = value;
        }
    }

    private sealed class Snapshot
    {
        public const int CurrentVersion = 1;

        public int Version { get; init; } = CurrentVersion;

        public List<EpisodicNode> Episodes { get; init; } = [];

        public List<EntityNode> Entities { get; init; } = [];

        public List<CommunityNode> Communities { get; init; } = [];

        public List<SagaNode> Sagas { get; init; } = [];

        public List<EpisodicEdge> EpisodicEdges { get; init; } = [];

        public List<EntityEdge> EntityEdges { get; init; } = [];

        public List<CommunityEdge> CommunityEdges { get; init; } = [];

        public List<HasEpisodeEdge> HasEpisodeEdges { get; init; } = [];

        public List<NextEpisodeEdge> NextEpisodeEdges { get; init; } = [];
    }
}
