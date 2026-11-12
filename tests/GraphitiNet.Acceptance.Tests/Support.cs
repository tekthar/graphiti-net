// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>Shared helpers for the acceptance tests.</summary>
internal static class Support
{
    public static readonly DateTimeOffset Jan = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset Mar = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset Jun = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>Facts, entities and episodes, keyword and vector search, no reranking model.</summary>
    public static SearchConfig Everything => SearchConfigRecipes.CombinedHybridSearchRrf with { Limit = 50 };

    /// <summary>Keyword search only (BM25 in memory, PostgreSQL text search) over facts, entities and episodes.</summary>
    public static SearchConfig KeywordOnly => new()
    {
        EdgeConfig = new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.Bm25] },
        NodeConfig = new NodeSearchConfig { SearchMethods = [NodeSearchMethod.Bm25] },
        EpisodeConfig = new EpisodeSearchConfig { SearchMethods = [EpisodeSearchMethod.Bm25] },
        Limit = 50,
    };

    public static Task<AddEpisodeResults> AddAsync(
        this Harness harness,
        string group,
        string body,
        DateTimeOffset referenceTime,
        IDictionary<string, object?>? metadata = null,
        string? uuid = null,
        CancellationToken cancellationToken = default) =>
        harness.Graphiti.AddEpisodeAsync(
            "episode",
            body,
            "chat message",
            referenceTime,
            EpisodeType.Message,
            groupId: group,
            uuid: uuid,
            episodeMetadata: metadata,
            cancellationToken: cancellationToken);

    public static async Task<IReadOnlyList<EntityEdge>> FactsAsync(this Harness harness, params string[] groups) =>
        await harness.Graphiti.Edges.Entity.GetByGroupIdsAsync(groups);

    public static async Task<IReadOnlyList<EntityNode>> EntitiesAsync(this Harness harness, params string[] groups) =>
        await harness.Graphiti.Nodes.Entity.GetByGroupIdsAsync(groups);

    public static async Task<IReadOnlyList<EpisodicNode>> EpisodesAsync(this Harness harness, params string[] groups) =>
        await harness.Graphiti.Nodes.Episode.GetByGroupIdsAsync(groups);
}

/// <summary>An access filter that hides some episodes.</summary>
internal sealed class HideEpisodes(params string[] hidden) : IEpisodeAccessFilter
{
    public ValueTask<IReadOnlySet<string>> GetReadableEpisodeUuidsAsync(
        IReadOnlyCollection<string> episodeUuids, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlySet<string>>(
            episodeUuids.Where(uuid => !hidden.Contains(uuid)).ToHashSet(StringComparer.Ordinal));
}
