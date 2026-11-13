// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Parity.Tests;

/// <summary>What one implementation produced for a dataset, in the shape <c>run_upstream.py</c> writes.</summary>
public sealed record ParityResult(
    IReadOnlyList<ParityEntity> Entities,
    IReadOnlyList<ParityFact> Facts,
    IReadOnlyList<ParitySearch> Searches)
{
    /// <summary>Reads <c>&lt;dataset&gt;.upstream.json</c>.</summary>
    public static ParityResult LoadUpstream(string dataset)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ParityPaths.Upstream(dataset)));
        var root = doc.RootElement;
        return new ParityResult(
            root.GetProperty("entities").EnumerateArray()
                .Select(e => new ParityEntity(
                    e.GetProperty("name").GetString()!,
                    e.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).Order(StringComparer.Ordinal).ToList(),
                    e.GetProperty("summary").GetString() ?? ""))
                .ToList(),
            root.GetProperty("facts").EnumerateArray()
                .Select(f => new ParityFact(
                    f.GetProperty("source").GetString()!,
                    f.GetProperty("name").GetString()!,
                    f.GetProperty("target").GetString()!,
                    f.GetProperty("fact").GetString()!,
                    ParseDate(f.GetProperty("valid_at")),
                    ParseDate(f.GetProperty("invalid_at")),
                    f.GetProperty("expired").GetBoolean()))
                .ToList(),
            root.GetProperty("searches").EnumerateArray()
                .Select(s => new ParitySearch(
                    s.GetProperty("query").GetString()!,
                    Strings(s.GetProperty("facts")),
                    Strings(s.GetProperty("combined_nodes")),
                    Strings(s.GetProperty("combined_edges"))))
                .ToList());
    }

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToList();

    private static DateTimeOffset? ParseDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : DateTimeOffset.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>An entity as compared.</summary>
public sealed record ParityEntity(string Name, IReadOnlyList<string> Labels, string Summary);

/// <summary>A fact as compared.</summary>
public sealed record ParityFact(
    string Source,
    string Name,
    string Target,
    string Fact,
    DateTimeOffset? ValidAt,
    DateTimeOffset? InvalidAt,
    bool Expired);

/// <summary>One query's results.</summary>
public sealed record ParitySearch(
    string Query,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> CombinedNodes,
    IReadOnlyList<string> CombinedEdges);

/// <summary>A parity dataset.</summary>
public sealed record ParityDataset(string Name, string GroupId, IReadOnlyList<ParityEpisode> Episodes, IReadOnlyList<string> Queries)
{
    /// <summary>Reads <c>parity/datasets/&lt;name&gt;.json</c>.</summary>
    public static ParityDataset Load(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ParityPaths.Dataset(name)));
        var root = doc.RootElement;
        return new ParityDataset(
            root.GetProperty("name").GetString()!,
            root.GetProperty("group_id").GetString()!,
            root.GetProperty("episodes").EnumerateArray()
                .Select(e => new ParityEpisode(
                    e.GetProperty("name").GetString()!,
                    EpisodeTypes.FromStorageName(e.GetProperty("source").GetString()!),
                    e.GetProperty("source_description").GetString()!,
                    DateTimeOffset.Parse(e.GetProperty("reference_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    e.GetProperty("content").GetString()!))
                .ToList(),
            root.GetProperty("queries").EnumerateArray().Select(q => q.GetString()!).ToList());
    }
}

/// <summary>One episode of a dataset.</summary>
public sealed record ParityEpisode(
    string Name,
    EpisodeType Source,
    string SourceDescription,
    DateTimeOffset ReferenceTime,
    string Content);

/// <summary>Runs a dataset through graphiti-net on recorded upstream answers.</summary>
public static class ParityRunner
{
    /// <summary>The recording name of a dataset run in bulk mode (<c>run_upstream.py --bulk</c>).</summary>
    public static string BulkRecording(string datasetName) => datasetName + "-bulk";

    /// <summary>Ingests the dataset and runs its queries, as <c>run_upstream.py</c> does.</summary>
    /// <param name="datasetName">The dataset.</param>
    /// <param name="driver">Storage.</param>
    /// <param name="onCreated">Called with the replay client before ingestion.</param>
    /// <param name="bulk">Ingest with one <see cref="Graphiti.AddEpisodeBulkAsync"/> call, as
    /// <c>run_upstream.py --bulk</c>, replaying <c>&lt;dataset&gt;-bulk</c>.</param>
    public static async Task<(ParityResult Result, ReplayLLMClient Llm, ReplayEmbedder Embedder)> RunAsync(
        string datasetName,
        IGraphDriver driver,
        Action<ReplayLLMClient>? onCreated = null,
        bool bulk = false)
    {
        var dataset = ParityDataset.Load(datasetName);
        var recording = Recording.Load(bulk ? BulkRecording(datasetName) : datasetName);
        var llm = new ReplayLLMClient(recording);
        onCreated?.Invoke(llm);
        var embedder = new ReplayEmbedder(recording);
        LastEmbedder = embedder;
        await using var graphiti = new Graphiti(driver, llm, embedder, new NoCrossEncoder());
        await graphiti.BuildIndicesAndConstraintsAsync();
        await driver.GraphOps.ClearDataAsync([dataset.GroupId]);

        if (bulk)
        {
            await graphiti.AddEpisodeBulkAsync(
                dataset.Episodes.Select(episode => new RawEpisode
                {
                    Name = episode.Name,
                    Content = episode.Content,
                    SourceDescription = episode.SourceDescription,
                    Source = episode.Source,
                    ReferenceTime = episode.ReferenceTime,
                }).ToList(),
                groupId: dataset.GroupId);
        }
        else
        {
            foreach (var episode in dataset.Episodes)
            {
                await graphiti.AddEpisodeAsync(
                    name: episode.Name,
                    episodeBody: episode.Content,
                    sourceDescription: episode.SourceDescription,
                    referenceTime: episode.ReferenceTime,
                    source: episode.Source,
                    groupId: dataset.GroupId);
            }
        }

        var nodes = await driver.EntityNodeOps.GetByGroupIdsAsync([dataset.GroupId]);
        var names = nodes.ToDictionary(n => n.Uuid, n => n.Name);
        var edges = await driver.EntityEdgeOps.GetByGroupIdsAsync([dataset.GroupId]);

        var searches = new List<ParitySearch>();
        foreach (var query in dataset.Queries)
        {
            var facts = await graphiti.SearchAsync(query, groupIds: [dataset.GroupId], numResults: 5);
            var combined = await graphiti.SearchAsync(query, SearchConfigRecipes.CombinedHybridSearchRrf, groupIds: [dataset.GroupId]);
            searches.Add(new ParitySearch(
                query,
                facts.Select(e => e.Fact).ToList(),
                combined.Nodes.Select(n => n.Name).ToList(),
                combined.Edges.Select(e => e.Fact).ToList()));
        }

        var result = new ParityResult(
            nodes.Select(n => new ParityEntity(n.Name, n.Labels.Order(StringComparer.Ordinal).ToList(), n.Summary))
                .OrderBy(e => e.Name, StringComparer.Ordinal).ToList(),
            edges.Select(e => new ParityFact(
                    names.GetValueOrDefault(e.SourceNodeUuid, e.SourceNodeUuid),
                    e.Name,
                    names.GetValueOrDefault(e.TargetNodeUuid, e.TargetNodeUuid),
                    e.Fact,
                    e.ValidAt,
                    e.InvalidAt,
                    e.ExpiredAt is not null))
                .OrderBy(f => f.Source, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal)
                .ThenBy(f => f.Target, StringComparer.Ordinal).ThenBy(f => f.Fact, StringComparer.Ordinal)
                .ToList(),
            searches);
        return (result, llm, embedder);
    }

    /// <summary>The embedder of the latest run, for diagnostics when a run fails.</summary>
    public static ReplayEmbedder? LastEmbedder { get; private set; }

    private sealed class NoCrossEncoder : CrossEncoderClient
    {
        public override Task<IReadOnlyList<RankedPassage>> RankAsync(
            string query,
            IReadOnlyList<string> passages,
            CancellationToken cancellationToken = default) =>
            throw new UnrecordedCallException("The parity datasets don't use the cross-encoder.");
    }
}
