// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/test_graphiti_mock.py (test_add_bulk) (Graphiti v0.31.2) and changed for .NET; the
// AddEpisodeBulkAsync tests are graphiti-net additions.

using System.Text.RegularExpressions;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Tests.Maintenance;

namespace GraphitiNet.Tests.GraphitiApi;

public partial class AddEpisodeBulkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly int[] First = [0];

    private static readonly string[] AliceFacts = ["Alice works at Acme.", "Alice works at Acme today."];

    /// <summary>
    /// An LLM that reads "X works at Y ..." sentences: each is an entity pair and a WORKS_AT fact (the sentence).
    /// Duplicate checks say the first candidate is a duplicate.
    /// </summary>
    private static FakeLlmClient WorksAtLlm()
    {
        var llm = new FakeLlmClient();
        llm.On(PromptNames.ExtractNodesAndEdgesExtractMessage, call =>
        {
            var current = Current(call.Text);
            var facts = WorksAt().Matches(current)
                .Select(m => (Person: m.Groups[1].Value, Org: m.Groups[2].Value, Fact: m.Value)).ToList();
            return new
            {
                extracted_entities = facts.SelectMany(f => new[] { f.Person, f.Org }).Distinct()
                    .Select(name => new { name, entity_type_id = 0 }).ToArray(),
                edges = facts.Select(f => new
                {
                    source_entity_name = f.Person,
                    target_entity_name = f.Org,
                    relation_type = "WORKS_AT",
                    fact = f.Fact,
                    episode_indices = First,
                }).ToArray(),
            };
        });
        llm.On(PromptNames.ExtractNodesExtractMessage, call => new
        {
            extracted_entities = WorksAt().Matches(Current(call.Text))
                .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).Distinct()
                .Select(name => new { name, entity_type_id = 0 }).ToArray(),
        });
        llm.On(PromptNames.ExtractEdgesEdge, call => new
        {
            edges = WorksAt().Matches(Current(call.Text)).Select(m => new
            {
                source_entity_name = m.Groups[1].Value,
                target_entity_name = m.Groups[2].Value,
                relation_type = "WORKS_AT",
                fact = m.Value,
                valid_at = (string?)null,
                invalid_at = (string?)null,
            }).ToArray(),
        });
        llm.On(PromptNames.ExtractEdgesExtractTimestampsBatch, call => new
        {
            timestamps = Enumerable.Repeat(new { valid_at = (string?)null, invalid_at = (string?)null },
                Regex.Count(call.Messages[^1].Content, "\"fact\":")).ToArray(),
        });
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, new { valid_at = (string?)null, invalid_at = (string?)null });
        llm.On(PromptNames.DedupeEdgesResolveEdge, new { duplicate_facts = First, contradicted_facts = Array.Empty<int>() });
        llm.On(PromptNames.ExtractNodesExtractSummariesBatch, new { summaries = Array.Empty<object>() });
        return llm;
    }

    // The CURRENT MESSAGES / CURRENT MESSAGE section of a prompt.
    private static string Current(string text)
    {
        var start = Math.Max(
            text.LastIndexOf("<CURRENT MESSAGE", StringComparison.Ordinal),
            text.LastIndexOf("<CURRENT_MESSAGE", StringComparison.Ordinal));
        return start < 0 ? text : text[start..];
    }

    [GeneratedRegex(@"([A-Z][a-z]+) works at ([A-Z][a-z]+)[^.]*\.")]
    private static partial Regex WorksAt();

    private static RawEpisode Raw(string name, string content, int minutes) => new()
    {
        Name = name,
        Content = content,
        SourceDescription = "chat",
        Source = EpisodeType.Message,
        ReferenceTime = T0.AddMinutes(minutes),
    };

    private static Graphiti CreateGraphiti(InMemoryGraphDriver driver, FakeLlmClient llm) =>
        new(driver, llm, new FakeEmbedder(), new LengthCrossEncoder());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddEpisodeBulk_extracts_and_merges_duplicates_across_the_batch(bool useCombinedExtraction)
    {
        await using var driver = new InMemoryGraphDriver();
        var llm = WorksAtLlm();
        await using var graphiti = CreateGraphiti(driver, llm);

        var result = await graphiti.AddEpisodeBulkAsync(
            [Raw("one", "Alice works at Acme.", 0), Raw("two", "Alice works at Acme today.", 5)],
            groupId: "bulk",
            useCombinedExtraction: useCombinedExtraction);

        Assert.Equal(2, result.Episodes.Count);
        Assert.Equal(["Acme", "Alice"], result.Nodes.Select(n => n.Name).Order());
        var fact = Assert.Single(result.Edges);
        Assert.Contains(fact.Fact, AliceFacts);
        Assert.Equal(result.Episodes.Select(e => e.Uuid).Order(), fact.Episodes.Order());
        Assert.All(result.Episodes, e => Assert.Equal([fact.Uuid], e.EntityEdges));
        Assert.Equal(4, result.EpisodicEdges.Count);
        Assert.Empty(result.Communities);

        Assert.Equal(useCombinedExtraction ? 2 : 0, llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage).Count);
        Assert.Equal(useCombinedExtraction ? 0 : 2, llm.CallsFor(PromptNames.ExtractNodesExtractMessage).Count);

        Assert.All(result.Nodes, n => Assert.NotNull(n.NameEmbedding));
        Assert.NotNull(fact.FactEmbedding);
        var savedNodes = await driver.EntityNodeOps.GetByGroupIdsAsync(["bulk"]);
        Assert.Equal(["Acme", "Alice"], savedNodes.Select(n => n.Name).Order());
        var savedFact = Assert.Single(await driver.EntityEdgeOps.GetByGroupIdsAsync(["bulk"]));
        Assert.Equal(fact.Uuid, savedFact.Uuid);
        var savedEpisodes = await driver.EpisodeNodeOps.GetByGroupIdsAsync(["bulk"]);
        Assert.Equal(["one", "two"], savedEpisodes.Select(e => e.Name).Order());
        Assert.All(savedEpisodes, e => Assert.Equal([fact.Uuid], e.EntityEdges));
        foreach (var node in savedNodes)
        {
            var mentions = await driver.EpisodeNodeOps.GetByEntityNodeUuidAsync(node.Uuid);
            Assert.Equal(2, mentions.Count);
        }
    }

    [Fact]
    public async Task AddEpisodeBulk_uses_previous_episodes_as_context()
    {
        await using var driver = new InMemoryGraphDriver();
        var llm = WorksAtLlm();
        await using var graphiti = CreateGraphiti(driver, llm);
        await graphiti.AddEpisodeBulkAsync([Raw("first", "Alice works at Acme.", 0)], groupId: "bulk");

        await graphiti.AddEpisodeBulkAsync([Raw("second", "Bob works at Initech.", 10)], groupId: "bulk");

        var lastExtraction = llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage)[^1].Text;
        Assert.Contains("\"content\": \"Alice works at Acme.\"", lastExtraction, StringComparison.Ordinal);
        var names = (await driver.EntityNodeOps.GetByGroupIdsAsync(["bulk"])).Select(n => n.Name).Order();
        Assert.Equal(["Acme", "Alice", "Bob", "Initech"], names);
    }

    // test_add_bulk: add_nodes_and_edges_bulk saves every node and edge as given.
    [Fact]
    public async Task AddNodesAndEdgesBulk_saves_and_embeds()
    {
        await using var driver = new InMemoryGraphDriver();
        var embedder = new FakeEmbedder();
        var now = DateTimeOffset.UtcNow;
        const string group = "test_group";

        EpisodicNode Episode(string name, string content) => new()
        {
            Name = name,
            GroupId = group,
            Labels = [],
            CreatedAt = now,
            Source = EpisodeType.Message,
            SourceDescription = "conversation message",
            Content = content,
            ValidAt = now,
        };

        EntityNode Entity(string name, string[] labels, int age, string location) => new()
        {
            Name = name,
            GroupId = group,
            Labels = labels,
            CreatedAt = now,
            Summary = name + " summary",
            Attributes = new Dictionary<string, object?> { ["age"] = (long)age, ["location"] = location },
        };

        var episode1 = Episode("test_episode", "Alice likes Bob");
        var episode2 = Episode("test_episode_2", "Bob adores Alice");
        var entity1 = Entity("test_entity_1", ["Entity", "Person"], 30, "New York");
        var entity2 = Entity("test_entity_2", ["Entity", "Person2"], 25, "Los Angeles");
        var entity3 = Entity("test_entity_3", ["Entity", "City", "Location"], 25, "Los Angeles");
        var entity4 = Entity("test_entity_4", ["Entity"], 25, "Los Angeles");
        entity1.NameEmbedding = await embedder.CreateAsync(entity1.Name);

        EntityEdge Fact(EntityNode source, EntityNode target, string name) => new()
        {
            SourceNodeUuid = source.Uuid,
            TargetNodeUuid = target.Uuid,
            CreatedAt = now,
            Name = name,
            Fact = $"{source.Name} relates to {target.Name}",
            ExpiredAt = now,
            ValidAt = now,
            InvalidAt = now,
            GroupId = group,
        };

        var edge1 = Fact(entity1, entity2, "likes");
        var edge2 = Fact(entity3, entity4, "relates_to");

        EpisodicEdge Mention(EpisodicNode episode, EntityNode entity) => new()
        {
            SourceNodeUuid = episode.Uuid,
            TargetNodeUuid = entity.Uuid,
            CreatedAt = now,
            GroupId = group,
        };

        var mentions = new[]
        {
            Mention(episode1, entity1), Mention(episode1, entity2), Mention(episode2, entity3), Mention(episode2, entity4),
        };
        episode1.EntityEdges.Add(edge1.Uuid);
        episode2.EntityEdges.Add(edge2.Uuid);
        edge1.Episodes.AddRange([episode1.Uuid, episode2.Uuid]);
        edge2.Episodes.Add(episode2.Uuid);

        await BulkUtils.AddNodesAndEdgesBulkAsync(driver, [episode1, episode2], mentions,
            [entity1, entity2, entity3, entity4], [edge1, edge2], embedder);

        // Only the missing embeddings were made: three names and two facts.
        Assert.Equal(1 + 5, embedder.CreateCalls);
        Assert.All(new[] { entity1, entity2, entity3, entity4 }, n => Assert.NotNull(n.NameEmbedding));
        Assert.All(new[] { edge1, edge2 }, e => Assert.NotNull(e.FactEmbedding));

        foreach (var episode in new[] { episode1, episode2 })
        {
            var saved = await driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid);
            Assert.Equal(episode.Content, saved.Content);
            Assert.Equal(episode.EntityEdges, saved.EntityEdges);
        }

        foreach (var entity in new[] { entity1, entity2, entity3, entity4 })
        {
            var saved = await driver.EntityNodeOps.GetByUuidAsync(entity.Uuid);
            Assert.Equal(entity.Name, saved.Name);
            Assert.Equal(entity.Labels.Order(), saved.Labels.Order());
            Assert.Equal(entity.Summary, saved.Summary);
            Assert.Equal(entity.Attributes["location"], saved.Attributes["location"]);
        }

        foreach (var mention in mentions)
        {
            var saved = await driver.EpisodicEdgeOps.GetByUuidAsync(mention.Uuid);
            Assert.Equal(mention.SourceNodeUuid, saved.SourceNodeUuid);
            Assert.Equal(mention.TargetNodeUuid, saved.TargetNodeUuid);
        }

        foreach (var edge in new[] { edge1, edge2 })
        {
            var saved = await driver.EntityEdgeOps.GetByUuidAsync(edge.Uuid);
            Assert.Equal(edge.Fact, saved.Fact);
            Assert.Equal(edge.Episodes, saved.Episodes);
            Assert.Equal(edge.InvalidAt, saved.InvalidAt);
            Assert.Equal(edge.ExpiredAt, saved.ExpiredAt);
        }
    }
}
