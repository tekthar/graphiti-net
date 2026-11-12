// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static GraphitiNet.Driver.PostgreSql.Tests.GraphBuilders;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>
/// PostgreSQL-specific search and storage behaviour beyond the shared conformance suite: per-group text search
/// languages, table prefixes, hostile full-text input, bulk saves and time zones.
/// </summary>
public sealed class PostgresSearchTests : IAsyncLifetime
{
    private readonly string _schema = TestDatabase.NewSchema();
    private readonly List<PostgresDriver> _drivers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var driver in _drivers)
        {
            await driver.DisposeAsync();
        }

        if (TestDatabase.SkipReason is null)
        {
            await TestDatabase.DropSchemaAsync(_schema);
        }
    }

    private async Task<PostgresDriver> CreateAsync(PostgresGraphOptions? options = null)
    {
        var driver = new PostgresDriver(
            TestDatabase.ConnectionString,
            (options ?? new PostgresGraphOptions()) with { Schema = _schema, EmbeddingDimension = 4 });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        _drivers.Add(driver);
        return driver;
    }

    [PostgresFact]
    public async Task Arabic_records_are_found_with_the_arabic_configuration()
    {
        var driver = await CreateAsync(new PostgresGraphOptions
        {
            TextSearchConfigs = new Dictionary<string, string> { ["tenant-ar"] = "arabic" },
        });
        const string group = "tenant-ar";
        var engineers = Entity("المهندسون", group, summary: "المهندسون يعملون في المشاريع");
        var company = Entity("الشركة", group, summary: "شركة برمجيات");
        await driver.EntityNodeOps.SaveBulkAsync([engineers, company]);
        await driver.EntityEdgeOps.SaveAsync(Fact(engineers, company, "المهندسون يعملون في الشركة", name: "WORKS_AT"));
        await driver.EpisodeNodeOps.SaveAsync(Episode("المهندسون يعملون في المشاريع الكبيرة", group));

        // "مهندس" (engineer), "يعمل" (works) and "الكبير" (the big) only match through Arabic stemming.
        var nodes = await driver.SearchOps.NodeFulltextSearchAsync("مهندس", SearchFilters.None, [group]);
        Assert.Equal([engineers.Uuid], nodes.Select(n => n.Uuid));
        var facts = await driver.SearchOps.EdgeFulltextSearchAsync("يعمل", SearchFilters.None, [group]);
        Assert.Single(facts);
        var episodes = await driver.SearchOps.EpisodeFulltextSearchAsync("الكبير", SearchFilters.None, [group]);
        Assert.Single(episodes);
    }

    [PostgresFact]
    public async Task Groups_with_different_configurations_each_search_correctly()
    {
        var driver = await CreateAsync(new PostgresGraphOptions
        {
            DefaultTextSearchConfig = "simple",
            TextSearchConfigSelector = group => group.StartsWith("en-", StringComparison.Ordinal) ? "english" : null,
            TextSearchConfigs = new Dictionary<string, string> { ["ar-1"] = "arabic" },
        });
        var english = Entity("Runner", "en-1", summary: "She was running marathons");
        var simple = Entity("Runner", "plain-1", summary: "She was running marathons");
        var arabic = Entity("المهندسون", "ar-1", summary: "يعملون");
        await driver.EntityNodeOps.SaveBulkAsync([english, simple, arabic]);

        // Stemming: "runs" matches "running" in the english group only.
        Assert.Equal([english.Uuid],
            (await driver.SearchOps.NodeFulltextSearchAsync("runs", SearchFilters.None, ["en-1", "plain-1"]))
            .Select(n => n.Uuid));
        // An exact word matches in both; each row is matched with its own group's configuration.
        Assert.Equal(2, (await driver.SearchOps.NodeFulltextSearchAsync("running", SearchFilters.None)).Count);
        Assert.Equal([arabic.Uuid],
            (await driver.SearchOps.NodeFulltextSearchAsync("مهندس", SearchFilters.None)).Select(n => n.Uuid));
        Assert.Empty(await driver.SearchOps.NodeFulltextSearchAsync("مهندس", SearchFilters.None, ["plain-1"]));
    }

    [PostgresFact]
    public async Task Default_configuration_is_simple_with_no_stemming()
    {
        var driver = await CreateAsync();
        var node = Entity("Runner", summary: "running");
        await driver.EntityNodeOps.SaveAsync(node);

        Assert.Empty(await driver.SearchOps.NodeFulltextSearchAsync("runs", SearchFilters.None, [Group]));
        Assert.Single(await driver.SearchOps.NodeFulltextSearchAsync("RUNNING", SearchFilters.None, [Group]));
        // simple keeps stop words, as Lucene's standard analyzer without a stop list does.
        Assert.Single(await driver.SearchOps.NodeFulltextSearchAsync("the runner", SearchFilters.None, [Group]));
    }

    [PostgresFact]
    public async Task Fulltext_treats_operators_and_quotes_as_text()
    {
        var driver = await CreateAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("O'Reilly", summary: "publisher of books"));

        var hostile = await driver.SearchOps.NodeFulltextSearchAsync(
            "O'Reilly & | ! :* <-> ( \\ '; DROP TABLE entities; --", SearchFilters.None, [Group]);

        Assert.Single(hostile);
        Assert.Single(await driver.SearchOps.NodeFulltextSearchAsync("books", SearchFilters.None, [Group]));
    }

    [PostgresFact]
    public async Task Table_prefix_puts_every_table_and_index_under_the_prefix()
    {
        var driver = await CreateAsync(new PostgresGraphOptions { TablePrefix = "AppGraph_" });
        await driver.EntityNodeOps.SaveAsync(Entity("Alice"));

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE tablename LIKE 'AppGraph\\_%'), count(*) FROM pg_tables WHERE schemaname = $1",
            connection);
        command.Parameters.AddWithValue(_schema);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            Assert.Equal(9L, reader.GetInt64(0));
            Assert.Equal(9L, reader.GetInt64(1));
        }

        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
        Assert.Single(await driver.SearchOps.NodeFulltextSearchAsync("alice", SearchFilters.None, [Group]));

        await using var context = new GraphitiDbContext(
            TestDatabase.ConnectionString,
            new PostgresGraphOptions { Schema = _schema, TablePrefix = "AppGraph_", EmbeddingDimension = 4 });
        Assert.Equal("Alice", (await context.Entities.SingleAsync()).Name);
    }

    [PostgresFact]
    public async Task Entities_always_carry_the_entity_label()
    {
        var driver = await CreateAsync();
        var node = Entity("Alice", labels: ["Person"]);
        await driver.EntityNodeOps.SaveAsync(node);

        var loaded = await driver.EntityNodeOps.GetByUuidAsync(node.Uuid);

        Assert.Equal(["Entity", "Person"], loaded.Labels.Order());
    }

    [PostgresFact]
    public async Task Save_bulk_spans_several_batches_and_keeps_the_last_duplicate()
    {
        var driver = await CreateAsync(new PostgresGraphOptions { BulkBatchSize = 100 });
        var nodes = Enumerable.Range(0, 1203).Select(i => Entity($"n{i}", embedding: [i, 1, 0, 0])).ToList();
        var duplicate = Entity("renamed", uuid: nodes[0].Uuid);

        await driver.EntityNodeOps.SaveBulkAsync([.. nodes, duplicate]);

        Assert.Equal(1203, (await driver.EntityNodeOps.GetByGroupIdsAsync([Group])).Count);
        Assert.Equal("renamed", (await driver.EntityNodeOps.GetByUuidAsync(nodes[0].Uuid)).Name);
    }

    [PostgresFact]
    public async Task Times_with_offsets_come_back_as_the_same_instant_in_utc()
    {
        var driver = await CreateAsync();
        var local = new DateTimeOffset(2025, 3, 4, 10, 30, 0, TimeSpan.FromHours(2));
        var episode = new EpisodicNode
        {
            Name = "ep",
            GroupId = Group,
            Source = EpisodeType.Json,
            SourceDescription = "crm",
            Content = "{}",
            ValidAt = local,
            CreatedAt = local,
        };
        await driver.EpisodeNodeOps.SaveAsync(episode);

        var loaded = await driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid);

        Assert.Equal(local, loaded.ValidAt);
        Assert.Equal(TimeSpan.Zero, loaded.ValidAt.Offset);
    }
}
