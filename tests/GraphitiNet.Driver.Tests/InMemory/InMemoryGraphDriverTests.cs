// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Driver.Tests.InMemory;

/// <summary>Behaviour specific to the in-memory driver: BM25 ranking, copies, thread safety and persistence.</summary>
public class InMemoryGraphDriverTests
{
    private const string Group = "g";
    private static readonly DateTimeOffset Created = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static EntityNode Entity(string name, string summary = "") =>
        new() { Name = name, GroupId = Group, Summary = summary, CreatedAt = Created };

    [Fact]
    public void Provider_is_memory()
    {
        Assert.Equal("memory", new InMemoryGraphDriver().Provider);
    }

    [Fact]
    public async Task Bm25_prefers_rare_terms()
    {
        await using var driver = new InMemoryGraphDriver();
        var common = Entity("apple banana");
        var rare = Entity("apple cherry");
        await driver.EntityNodeOps.SaveBulkAsync([common, rare, Entity("banana"), Entity("banana split")]);

        var result = await driver.SearchOps.NodeFulltextSearchAsync("banana cherry", SearchFilters.None);

        Assert.Equal(rare.Uuid, result[0].Uuid);
    }

    [Fact]
    public async Task Bm25_prefers_shorter_fields_and_more_occurrences()
    {
        await using var driver = new InMemoryGraphDriver();
        var shortName = Entity("otter");
        var longName = Entity("otter river valley lake mountain");
        var repeated = Entity("otter otter otter river valley lake mountain");
        await driver.EntityNodeOps.SaveBulkAsync([longName, shortName, repeated]);

        var result = await driver.SearchOps.NodeFulltextSearchAsync("otter", SearchFilters.None);

        Assert.Equal([shortName.Uuid, repeated.Uuid, longName.Uuid], result.Select(n => n.Uuid));
    }

    [Fact]
    public void Tokenizer_follows_lucene_standard_tokenizer_word_boundaries()
    {
        // Underscores join (Neo4j indexes relation names like WORKS_AT as one term); hyphens and slashes split.
        Assert.Equal(
            ["works_at", "acme", "co", "2024", "café", "naïve"],
            Bm25.Tokenize("WORKS_AT Acme-Co, 2024! Café/naïve"));

        // Apostrophes, periods and colons join letters; periods, commas and semicolons join digits.
        Assert.Equal(
            ["alice's", "can't", "u.s.a", "foo:bar", "2026.03", "1,000", "end"],
            Bm25.Tokenize("Alice's can't U.S.A foo:bar 2026.03 1,000 end."));

        // Arabic letters and their combining marks stay in one word.
        Assert.Equal(["مُحَمَّد", "العلي"], Bm25.Tokenize("مُحَمَّد العلي"));
        Assert.Empty(Bm25.Tokenize("  ...  "));
    }

    [Fact]
    public async Task Returned_records_are_copies()
    {
        await using var driver = new InMemoryGraphDriver();
        var node = Entity("Alice");
        node.Attributes["tags"] = new List<object?> { "a" };
        await driver.EntityNodeOps.SaveAsync(node);

        // Changing the saved object or a returned one does not change what is stored.
        node.Name = "Changed";
        var loaded = await driver.EntityNodeOps.GetByUuidAsync(node.Uuid);
        loaded.Summary = "changed";
        ((List<object?>)loaded.Attributes["tags"]!).Add("b");

        var again = await driver.EntityNodeOps.GetByUuidAsync(node.Uuid);
        Assert.Equal("Alice", again.Name);
        Assert.Equal("", again.Summary);
        Assert.Equal(["a"], (List<object?>)again.Attributes["tags"]!);
    }

    [Fact]
    public async Task Attribute_values_come_back_json_shaped()
    {
        await using var driver = new InMemoryGraphDriver();
        var node = Entity("Alice");
        node.Attributes["int"] = 3;
        node.Attributes["float"] = 0.1f;
        node.Attributes["array"] = new[] { 1, 2 };
        node.Attributes["date"] = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);
        node.Attributes["record"] = new { Name = "x", Count = 2 };
        await driver.EntityNodeOps.SaveAsync(node);

        var attributes = (await driver.EntityNodeOps.GetByUuidAsync(node.Uuid)).Attributes;

        Assert.Equal(3L, attributes["int"]);
        Assert.Equal(0.1, attributes["float"]);
        Assert.Equal(new List<object?> { 1L, 2L }, (List<object?>)attributes["array"]!);
        Assert.Equal("2025-01-02T00:00:00+00:00", attributes["date"]);
        var record = Assert.IsType<Dictionary<string, object?>>(attributes["record"]);
        Assert.Equal("x", record["Name"]);
        Assert.Equal(2L, record["Count"]);
    }

    [Fact]
    public async Task Concurrent_writes_and_reads_are_safe()
    {
        await using var driver = new InMemoryGraphDriver();
        var hub = Entity("hub");
        await driver.EntityNodeOps.SaveAsync(hub);

        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            for (var j = 0; j < 50; j++)
            {
                var node = Entity($"node {i} {j}", "parallel worker");
                node.NameEmbedding = [1, i, j];
                await driver.EntityNodeOps.SaveAsync(node);
                await driver.EntityEdgeOps.SaveAsync(new EntityEdge
                {
                    GroupId = Group,
                    SourceNodeUuid = hub.Uuid,
                    TargetNodeUuid = node.Uuid,
                    Name = "LINKS",
                    Fact = "hub links node",
                    CreatedAt = Created,
                });
                await driver.SearchOps.NodeFulltextSearchAsync("parallel", SearchFilters.None);
                await driver.SearchOps.NodeSimilaritySearchAsync([1, 0, 0], SearchFilters.None);
                await driver.SearchOps.EdgeBfsSearchAsync([hub.Uuid], 2, SearchFilters.None);
                if (j % 10 == 0)
                {
                    await driver.EntityNodeOps.DeleteAsync(node);
                }
            }
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(1 + (32 * 45), (await driver.EntityNodeOps.GetByGroupIdsAsync([Group])).Count);
        Assert.Equal(32 * 45, (await driver.EntityEdgeOps.GetByGroupIdsAsync([Group])).Count);
    }

    [Fact]
    public async Task The_graph_round_trips_through_a_json_file()
    {
        await using var driver = new InMemoryGraphDriver();
        var alice = Entity("Alice", "An engineer.");
        alice.Labels = ["Person"];
        alice.NameEmbedding = [1, 0, 0];
        alice.Attributes["nested"] = new Dictionary<string, object?> { ["list"] = new List<object?> { 1L, "x", null } };
        var bob = Entity("Bob");
        var episode = new EpisodicNode
        {
            Name = "episode",
            GroupId = Group,
            Content = "Alice met Bob.",
            Source = EpisodeType.Message,
            SourceDescription = "chat",
            ValidAt = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            CreatedAt = Created,
            EpisodeMetadata = new Dictionary<string, object?> { ["k"] = "v" },
        };
        var fact = new EntityEdge
        {
            GroupId = Group,
            SourceNodeUuid = alice.Uuid,
            TargetNodeUuid = bob.Uuid,
            Name = "MET",
            Fact = "Alice met Bob.",
            FactEmbedding = [0, 1, 0],
            Episodes = [episode.Uuid],
            CreatedAt = Created,
            ValidAt = Created,
        };
        var mention = new EpisodicEdge
        {
            GroupId = Group,
            SourceNodeUuid = episode.Uuid,
            TargetNodeUuid = alice.Uuid,
            CreatedAt = Created,
        };
        await driver.AddNodesAndEdgesBulkAsync([episode], [mention], [alice, bob], [fact]);
        var community = new CommunityNode { Name = "c", GroupId = Group, NameEmbedding = [0, 0, 1], CreatedAt = Created };
        await driver.CommunityNodeOps.SaveAsync(community);
        var saga = new SagaNode { Name = "s", GroupId = Group, CreatedAt = Created, LastSummarizedAt = Created };
        await driver.SagaNodeOps.SaveAsync(saga);
        await driver.HasEpisodeEdgeOps.SaveAsync(new HasEpisodeEdge
        {
            GroupId = Group,
            SourceNodeUuid = saga.Uuid,
            TargetNodeUuid = episode.Uuid,
            CreatedAt = Created,
        });

        var path = Path.Combine(Path.GetTempPath(), $"graphiti-net-{Guid.NewGuid():N}.json");
        try
        {
            await driver.SaveToFileAsync(path);
            await using var loaded = await InMemoryGraphDriver.LoadFromFileAsync(path);

            var loadedAlice = await loaded.EntityNodeOps.GetByUuidAsync(alice.Uuid);
            Assert.Equal("An engineer.", loadedAlice.Summary);
            Assert.Equal(["Person", "Entity"], loadedAlice.Labels);
            var nested = Assert.IsType<Dictionary<string, object?>>(loadedAlice.Attributes["nested"]);
            Assert.Equal(new List<object?> { 1L, "x", null }, (List<object?>)nested["list"]!);
            await loaded.EntityNodeOps.LoadEmbeddingsAsync(loadedAlice);
            Assert.Equal([1f, 0f, 0f], loadedAlice.NameEmbedding!);

            var loadedEpisode = await loaded.EpisodeNodeOps.GetByUuidAsync(episode.Uuid);
            Assert.Equal(EpisodeType.Message, loadedEpisode.Source);
            Assert.Equal(episode.ValidAt, loadedEpisode.ValidAt);
            Assert.Equal(TimeSpan.Zero, loadedEpisode.ValidAt.Offset);
            Assert.Equal("v", loadedEpisode.EpisodeMetadata!["k"]);

            var loadedFact = await loaded.EntityEdgeOps.GetByUuidAsync(fact.Uuid);
            Assert.Equal([episode.Uuid], loadedFact.Episodes);
            await loaded.EntityEdgeOps.LoadEmbeddingsAsync(loadedFact);
            Assert.Equal([0f, 1f, 0f], loadedFact.FactEmbedding!);

            Assert.Single(await loaded.EpisodicEdgeOps.GetByGroupIdsAsync([Group]));
            Assert.Single(await loaded.HasEpisodeEdgeOps.GetByGroupIdsAsync([Group]));
            Assert.Equal(Created, (await loaded.SagaNodeOps.GetByUuidAsync(saga.Uuid)).LastSummarizedAt);
            Assert.Single(await loaded.SearchOps.CommunitySimilaritySearchAsync([0, 0, 1]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Loading_rejects_an_unknown_file_version()
    {
        using var stream = new MemoryStream("{\"Version\": 99}"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryGraphDriver.LoadAsync(stream));
    }
}
