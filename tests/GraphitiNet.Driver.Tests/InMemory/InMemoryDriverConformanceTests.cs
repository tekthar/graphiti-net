// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.Tests.Conformance;
using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.InMemory;

/// <summary>The conformance suite against a fresh in-memory driver per test.</summary>
public sealed class InMemoryDriverConformanceTests : GraphDriverConformanceTests
{
    protected override Task<IGraphDriver> CreateDriverAsync() => Task.FromResult<IGraphDriver>(new InMemoryGraphDriver());
}

/// <summary>
/// The conformance suite against one in-memory driver shared by every test and pre-filled with look-alike records in
/// another group, as a shared database would be. This checks the suite itself copes with a shared store.
/// </summary>
public sealed class InMemorySharedDriverConformanceTests : GraphDriverConformanceTests
{
    private static readonly Lazy<Task<IGraphDriver>> Shared = new(CreateSharedAsync);

    protected override bool IsIsolated => false;

    protected override int EmbeddingDimension => 4;

    protected override Task<IGraphDriver> CreateDriverAsync() => Shared.Value;

    private static async Task<IGraphDriver> CreateSharedAsync()
    {
        var driver = new InMemoryGraphDriver();
        const string noise = "noise";
        var created = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        float[][] vectors = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0.8f, 0.6f, 0, 0]];
        var entities = vectors.Select((v, i) => new EntityNode
        {
            Name = $"keyword quokka zebra Alice {i}",
            GroupId = noise,
            Summary = "keyword marsupial crossing zanzibar",
            Labels = ["Person", "Place", "Employee"],
            NameEmbedding = v,
            CreatedAt = created,
            Attributes = new Dictionary<string, object?> { ["age"] = 30L, ["weight"] = 5L, ["colour"] = "red" },
        }).ToList();
        await driver.EntityNodeOps.SaveBulkAsync(entities);
        await driver.EntityEdgeOps.SaveBulkAsync(vectors.Select((v, i) => new EntityEdge
        {
            GroupId = noise,
            SourceNodeUuid = entities[i].Uuid,
            TargetNodeUuid = entities[(i + 1) % entities.Count].Uuid,
            Name = "LIKES",
            Fact = "keyword mentors pottery chess",
            FactEmbedding = v,
            CreatedAt = created,
            ValidAt = created,
        }).ToList());
        await driver.EpisodeNodeOps.SaveBulkAsync(vectors.Select((_, i) => new EpisodicNode
        {
            Name = $"noise {i}",
            GroupId = noise,
            Content = "keyword lighthouse budget",
            Source = EpisodeType.Text,
            SourceDescription = "lighthouse",
            ValidAt = created.AddDays(i),
            CreatedAt = created,
        }).ToList());
        await driver.CommunityNodeOps.SaveBulkAsync(vectors.Select(v => new CommunityNode
        {
            Name = "Glacier Hikers keyword",
            GroupId = noise,
            NameEmbedding = v,
            CreatedAt = created,
        }).ToList());
        return driver;
    }
}
