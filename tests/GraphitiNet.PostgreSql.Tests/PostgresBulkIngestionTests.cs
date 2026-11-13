// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary><see cref="Graphiti.AddEpisodeBulkAsync"/> end to end on PostgreSQL, with canned model answers.</summary>
public sealed class PostgresBulkIngestionTests : IAsyncLifetime
{
    private readonly string _schema = TestDatabase.NewSchema();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => TestDatabase.SkipReason is null ? TestDatabase.DropSchemaAsync(_schema) : Task.CompletedTask;

    [PostgresFact]
    public async Task Bulk_ingestion_saves_deduplicated_entities_facts_and_mentions()
    {
        await using var driver = new PostgresDriver(
            TestDatabase.ConnectionString, new PostgresGraphOptions { Schema = _schema, EmbeddingDimension = 8 });
        var llm = new CannedLlmClient();
        await using var graphiti = new Graphiti(driver, llm, new CharEmbedder(), new NoCrossEncoder());
        await graphiti.BuildIndicesAndConstraintsAsync();
        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        var result = await graphiti.AddEpisodeBulkAsync(
            [
                new RawEpisode
                {
                    Name = "one", Content = "Alice works at Acme.", SourceDescription = "chat",
                    Source = EpisodeType.Message, ReferenceTime = t0,
                },
                new RawEpisode
                {
                    Name = "two", Content = "Alice is employed at Acme.", SourceDescription = "chat",
                    Source = EpisodeType.Message, ReferenceTime = t0.AddMinutes(5),
                },
            ],
            groupId: "bulk");

        // Both episodes name Alice and Acme: one entity each. The two facts are duplicates: one fact, both episodes.
        Assert.Equal(["Acme", "Alice"], result.Nodes.Select(n => n.Name).Order());
        var fact = Assert.Single(result.Edges);
        Assert.Equal(result.Episodes.Select(e => e.Uuid).Order(), fact.Episodes.Order());
        Assert.Equal(t0, fact.ValidAt);

        var nodes = await driver.EntityNodeOps.GetByGroupIdsAsync(["bulk"]);
        Assert.Equal(["Acme", "Alice"], nodes.Select(n => n.Name).Order());
        var edges = await driver.EntityEdgeOps.GetByGroupIdsAsync(["bulk"]);
        Assert.Equal(fact.Uuid, Assert.Single(edges).Uuid);
        Assert.Equal(fact.Episodes.Order(), edges[0].Episodes.Order());
        var episodes = await driver.EpisodeNodeOps.GetByGroupIdsAsync(["bulk"]);
        Assert.Equal(["one", "two"], episodes.Select(e => e.Name).Order());
        Assert.All(episodes, e => Assert.Equal([fact.Uuid], e.EntityEdges));
        Assert.Equal(4, await TestDatabase.CountAsync(_schema, "episodic_edges"));

        // The saved graph is searchable.
        var found = await graphiti.SearchAsync("Alice Acme", groupIds: ["bulk"]);
        Assert.Contains(found, e => e.Uuid == fact.Uuid);
    }

    /// <summary>Answers each prompt by name; the facts of both episodes are duplicates of each other.</summary>
    private sealed class CannedLlmClient() : LLMClient(new LLMConfig(model: "canned"))
    {
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
            var user = messages[^1].Content;
            object answer = promptName switch
            {
                PromptNames.ExtractNodesAndEdgesExtractMessage => new
                {
                    extracted_entities = new[] { new { name = "Alice", entity_type_id = 0 }, new { name = "Acme", entity_type_id = 0 } },
                    edges = new[]
                    {
                        new
                        {
                            source_entity_name = "Alice",
                            target_entity_name = "Acme",
                            relation_type = "WORKS_AT",
                            fact = user.Contains("is employed at", StringComparison.Ordinal)
                                ? "Alice is employed at Acme."
                                : "Alice works at Acme.",
                            episode_indices = new[] { 0 },
                        },
                    },
                },
                PromptNames.ExtractEdgesExtractTimestampsBatch => new
                {
                    timestamps = new[] { new { valid_at = "2026-03-01T09:00:00Z", invalid_at = (string?)null } },
                },
                PromptNames.ExtractEdgesExtractTimestamps => new { valid_at = (string?)null, invalid_at = (string?)null },
                PromptNames.DedupeEdgesResolveEdge => new { duplicate_facts = new[] { 0 }, contradicted_facts = Array.Empty<int>() },
                PromptNames.ExtractNodesExtractSummariesBatch => new { summaries = Array.Empty<object>() },
                _ => throw new InvalidOperationException($"Unexpected prompt {promptName}."),
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(answer));
        }

        protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
            IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize, string? model,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A small deterministic embedding from the characters of the text.</summary>
    private sealed class CharEmbedder : EmbedderClient
    {
        public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
        {
            var vector = new float[8];
            for (var i = 0; i < inputData.Length; i++)
            {
                vector[i % 8] += char.ToLowerInvariant(inputData[i]) % 17;
            }

            vector[0] += 1;
            var norm = MathF.Sqrt(vector.Sum(v => v * v));
            return Task.FromResult(vector.Select(v => v / norm).ToArray());
        }
    }

    private sealed class NoCrossEncoder : CrossEncoderClient
    {
        public override Task<IReadOnlyList<RankedPassage>> RankAsync(
            string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RankedPassage>>(passages.Select(p => new RankedPassage(p, 0)).ToList());
    }
}
