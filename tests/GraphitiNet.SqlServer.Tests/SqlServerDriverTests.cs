// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.SqlServer.Entities;
using GraphitiNet.Models;
using GraphitiNet.Search;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using static GraphitiNet.Driver.SqlServer.Tests.GraphBuilders;

namespace GraphitiNet.Driver.SqlServer.Tests;

/// <summary>SQL-Server-specific behaviour: schema creation, hosting modes, transactions, Unicode and ranking.</summary>
public sealed class SqlServerDriverTests : IAsyncLifetime
{
    private readonly string _schema = TestDatabase.NewSchema();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() =>
        TestDatabase.SkipReason is null ? TestDatabase.DropSchemaAsync(_schema) : Task.CompletedTask;

    private SqlServerGraphOptions Options => new() { Schema = _schema, EmbeddingDimension = 4 };

    [SqlServerFact]
    public async Task Build_creates_tables_vector_columns_and_indexes_and_is_idempotent()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options with { EmbeddingDimension = 6 });

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("kept", embedding: [1, 0, 0, 0, 0, 0]));
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();

        var tables = await TestDatabase.ListAsync(
            "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @schema ORDER BY t.name",
            ("@schema", _schema));
        Assert.Equal(
            ["communities", "community_edges", "entities", "entity_edges", "episodes", "episodic_edges",
                "has_episode_edges", "next_episode_edges", "sagas", "search_fields", "search_terms"],
            tables);

        var vectors = await TestDatabase.ListAsync(
            "SELECT OBJECT_NAME(c.object_id) + '.' + c.name FROM sys.columns c "
            + "JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @schema AND TYPE_NAME(c.user_type_id) = 'vector' AND c.vector_dimensions = 6 "
            + "ORDER BY 1",
            ("@schema", _schema));
        Assert.Equal(["communities.name_embedding", "entities.name_embedding", "entity_edges.fact_embedding"], vectors);

        // The data written between the two builds is still there.
        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
    }

    [SqlServerFact]
    public async Task Build_recreates_a_dropped_table_and_index_without_touching_the_others()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("kept"));
        await TestDatabase.ExecuteAsync($"DROP TABLE [{_schema}].[community_edges]");
        await TestDatabase.ExecuteAsync($"DROP INDEX [ix_entities_created_at] ON [{_schema}].[entities]");

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();

        Assert.Equal(1, await TestDatabase.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_entities_created_at' "
            + "AND object_id = OBJECT_ID(@table)",
            ("@table", $"[{_schema}].[entities]")));
        Assert.Empty(await driver.CommunityEdgeOps.GetByGroupIdsAsync([Group]));
        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
    }

    [SqlServerFact]
    public async Task Concurrent_builds_do_not_race()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => driver.GraphOps.BuildIndicesAndConstraintsAsync()));

        await driver.EntityNodeOps.SaveAsync(Entity("Alice"));
        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
    }

    [SqlServerFact]
    public async Task Bulk_ingestion_is_one_transaction()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        var episode = Episode("Alice met Bob");
        var alice = Entity("Alice", embedding: [1, 0, 0, 0]);
        var bob = Entity("Bob");
        // The last write fails: a 3-dimensional embedding does not fit vector(4).
        var badFact = Fact(alice, bob, embedding: [1, 2, 3]);

        await Assert.ThrowsAsync<SqlException>(
            () => driver.AddNodesAndEdgesBulkAsync([episode], [Mention(episode, alice)], [alice, bob], [badFact]));

        foreach (var table in new[] { "episodes", "entities", "episodic_edges", "entity_edges", "search_terms", "search_fields" })
        {
            Assert.Equal(0, await TestDatabase.ScalarAsync<int>($"SELECT COUNT(*) FROM [{_schema}].[{table}]"));
        }
    }

    [SqlServerFact]
    public async Task Bulk_ingestion_saves_everything_in_batches()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options with { BulkBatchSize = 7 });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        var episode = Episode("a long day");
        var entities = Enumerable.Range(0, 30).Select(i => Entity($"entity {i}", summary: "batch member")).ToList();
        var facts = entities.Skip(1).Select((e, i) => Fact(entities[i], e, fact: $"link {i}")).ToList();

        await driver.AddNodesAndEdgesBulkAsync([episode], [.. entities.Select(e => Mention(episode, e))], entities, facts);

        Assert.Equal(30, (await driver.EntityNodeOps.GetByGroupIdsAsync([Group])).Count);
        Assert.Equal(29, (await driver.EntityEdgeOps.GetByGroupIdsAsync([Group])).Count);
        Assert.Equal(30, (await driver.SearchOps.NodeFulltextSearchAsync("batch", new SearchFilters(), [Group], 100)).Count);
    }

    [SqlServerFact]
    public async Task Standalone_context_reads_what_the_driver_wrote_and_ef_vector_distance_works()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("Alice", embedding: [1, 0, 0, 0], summary: "engineer"));
        await driver.EntityNodeOps.SaveAsync(Entity("Bob", embedding: [0, 1, 0, 0], summary: "artist"));

        await using var context = new GraphitiDbContext(TestDatabase.ConnectionString, Options);
        var alice = await context.Entities.SingleAsync(e => e.Name == "Alice");
        Assert.Equal(new float[] { 1, 0, 0, 0 }, ((SqlVector<float>)alice.NameEmbedding!).Memory.ToArray());
        Assert.Equal(["Entity"], alice.Labels);
        Assert.Equal(_schema, context.Model.FindEntityType(typeof(EntityNodeRecord))!.GetSchema());

        // EF Core 10 translates VectorDistance over the native vector column.
        var query = new SqlVector<float>(new float[] { 0.1f, 0.9f, 0, 0 });
        var nearest = await context.Entities
            .OrderBy(e => EF.Functions.VectorDistance("cosine", (SqlVector<float>)e.NameEmbedding!, query))
            .Select(e => e.Name)
            .FirstAsync();
        Assert.Equal("Bob", nearest);
    }

    [SqlServerFact]
    public async Task Host_dbcontext_mode_with_a_table_prefix_creates_only_the_graph_tables()
    {
        var options = Options with { TablePrefix = "graph_" };
        var dbOptions = new DbContextOptionsBuilder<HostDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .ReplaceService<IModelCacheKeyFactory, HostModelCacheKeyFactory>()
            .Options;
        await using var driver = new SqlServerDriver(() => new HostDbContext(dbOptions, options), options);

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("Alice", summary: "works on graphs"));

        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
        Assert.Single(await driver.SearchOps.NodeFulltextSearchAsync("graphs", new SearchFilters(), [Group]));
        Assert.Equal(11, await TestDatabase.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @schema AND t.name LIKE 'graph[_]%'",
            ("@schema", _schema)));
        Assert.Equal(0, await TestDatabase.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.tables WHERE name = @name", ("@name", "host_notes_" + _schema)));

        // The host's own migrations would include the graph tables.
        await using var context = new HostDbContext(dbOptions, options);
        var script = context.Database.GenerateCreateScript();
        Assert.Contains($"CREATE TABLE [{_schema}].[graph_entities]", script, StringComparison.Ordinal);
        Assert.Contains("vector(4)", script, StringComparison.Ordinal);
        Assert.Contains($"CREATE TABLE [{_schema}].[graph_search_terms]", script, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task A_dbcontext_with_another_schema_is_rejected()
    {
        var dbOptions = new DbContextOptionsBuilder<HostDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString)
            .ReplaceService<IModelCacheKeyFactory, HostModelCacheKeyFactory>()
            .Options;
        await using var driver = new SqlServerDriver(
            () => new HostDbContext(dbOptions, Options with { Schema = "somewhere_else" }), Options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.EntityNodeOps.GetByUuidsAsync(["x"]));
        Assert.Contains("somewhere_else", error.Message, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Unicode_and_Arabic_text_round_trips_and_is_searchable()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        var ahmed = Entity(
            "أحمد",
            summary: "يعمل أحمد مهندساً في القاهرة منذ عام ٢٠٢٠",
            attributes: new Dictionary<string, object?> { ["المدينة"] = "القاهرة", ["note"] = "日本語 😀" });
        var mona = Entity("منى", summary: "تعيش منى في الإسكندرية");
        await driver.EntityNodeOps.SaveBulkAsync([ahmed, mona]);
        await driver.EntityEdgeOps.SaveAsync(Fact(ahmed, mona, fact: "أحمد يعرف منى منذ الطفولة", name: "يعرف"));
        var episode = Episode("قال أحمد: أنا أعمل في القاهرة 😀");
        await driver.EpisodeNodeOps.SaveAsync(episode);

        var read = await driver.EntityNodeOps.GetByUuidAsync(ahmed.Uuid);
        Assert.Equal("أحمد", read.Name);
        Assert.Equal(ahmed.Summary, read.Summary);
        Assert.Equal("القاهرة", read.Attributes["المدينة"]);
        Assert.Equal("日本語 😀", read.Attributes["note"]);
        Assert.Equal(episode.Content, (await driver.EpisodeNodeOps.GetByUuidAsync(episode.Uuid)).Content);

        var filters = new SearchFilters();
        Assert.Equal([ahmed.Uuid], (await driver.SearchOps.NodeFulltextSearchAsync("القاهرة", filters, [Group])).Select(n => n.Uuid));
        Assert.Equal([mona.Uuid], (await driver.SearchOps.NodeFulltextSearchAsync("الإسكندرية", filters, [Group])).Select(n => n.Uuid));
        Assert.Single(await driver.SearchOps.EdgeFulltextSearchAsync("الطفولة", filters, [Group]));
        Assert.Single(await driver.SearchOps.EpisodeFulltextSearchAsync("القاهرة", filters, [Group]));
        Assert.Equal([ahmed.Uuid], (await driver.EntityNodeOps.GetByNamesAsync(["أحمد"], [Group])).Select(n => n.Uuid));
        Assert.Equal(
            [ahmed.Uuid],
            (await driver.EntityNodeOps.GetByAttributeValuesAsync(
                Group, new Dictionary<string, object?> { ["المدينة"] = "القاهرة" })).Select(n => n.Uuid));
    }

    [SqlServerFact]
    public async Task Names_and_ids_compare_exactly_whatever_the_database_collation()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        var upper = Entity("Alice", uuid: "B-node");
        var lower = Entity("alice", uuid: "a-node");
        await driver.EntityNodeOps.SaveBulkAsync([upper, lower]);

        Assert.Equal(["B-node"], (await driver.EntityNodeOps.GetByNamesAsync(["Alice"], [Group])).Select(n => n.Uuid));
        // Ordinal order: upper-case letters sort before lower-case ones.
        Assert.Equal(["a-node", "B-node"], (await driver.EntityNodeOps.GetByGroupIdsAsync([Group])).Select(n => n.Uuid));
        Assert.Equal(["B-node"], (await driver.EntityNodeOps.GetByGroupIdsAsync([Group], uuidCursor: "a-node")).Select(n => n.Uuid));
    }

    [SqlServerFact]
    public async Task Keyword_index_follows_updates_and_deletes()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        var filters = new SearchFilters();
        var alice = Entity("Alice", summary: "plays the oboe");
        var bob = Entity("Bob", summary: "plays chess");
        await driver.EntityNodeOps.SaveBulkAsync([alice, bob]);
        await driver.EntityEdgeOps.SaveAsync(Fact(alice, bob, fact: "Alice teaches Bob the oboe"));

        alice.Summary = "plays the violin";
        await driver.EntityNodeOps.SaveAsync(alice);
        Assert.Empty(await driver.SearchOps.NodeFulltextSearchAsync("oboe", filters, [Group]));
        Assert.Single(await driver.SearchOps.NodeFulltextSearchAsync("violin", filters, [Group]));

        // Deleting a node deletes its facts, and both leave the keyword index.
        await driver.EntityNodeOps.DeleteAsync(alice);
        Assert.Empty(await driver.SearchOps.EdgeFulltextSearchAsync("oboe", filters, [Group]));
        Assert.Equal(0, await TestDatabase.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM [{_schema}].[search_terms] WHERE uuid <> @bob", ("@bob", bob.Uuid)));

        await driver.GraphOps.ClearDataAsync([Group]);
        Assert.Equal(0, await TestDatabase.ScalarAsync<int>($"SELECT COUNT(*) FROM [{_schema}].[search_terms]"));
        Assert.Equal(0, await TestDatabase.ScalarAsync<int>($"SELECT COUNT(*) FROM [{_schema}].[search_fields]"));
    }

    [SqlServerFact]
    public async Task Keyword_ranking_matches_the_in_memory_driver()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await using var memory = new InMemoryGraphDriver();
        var words = new[] { "alpha", "beta", "gamma", "delta", "WORKS_AT", "acme", "the", "of", "city", "river" };
        var random = new Random(42);
        string Text(int count) => string.Join(' ', Enumerable.Range(0, count).Select(_ => words[random.Next(words.Length)]));
        var entities = Enumerable.Range(0, 40)
            .Select(i => Entity($"{words[i % words.Length]} {i}", summary: Text(random.Next(0, 12)), group: i % 3 == 0 ? "other" : Group))
            .ToList();
        var facts = entities.Skip(1)
            .Select((e, i) => Fact(entities[i], e, fact: Text(random.Next(1, 15)), name: i % 2 == 0 ? "WORKS_AT" : "KNOWS", group: Group))
            .ToList();
        foreach (var target in new IGraphDriver[] { driver, memory })
        {
            await target.EntityNodeOps.SaveBulkAsync(entities);
            await target.EntityEdgeOps.SaveBulkAsync(facts);
        }

        var filters = new SearchFilters();
        foreach (var query in new[] { "alpha", "beta gamma", "works_at acme", "the city of the river", "delta delta", "zzz" })
        {
            var expected = await memory.SearchOps.NodeFulltextSearchAsync(query, filters, [Group], 25);
            var actual = await driver.SearchOps.NodeFulltextSearchAsync(query, filters, [Group], 25);
            Assert.Equal(expected.Select(n => n.Uuid), actual.Select(n => n.Uuid));

            var expectedFacts = await memory.SearchOps.EdgeFulltextSearchAsync(query, filters, null, 25);
            var actualFacts = await driver.SearchOps.EdgeFulltextSearchAsync(query, filters, null, 25);
            Assert.Equal(expectedFacts.Select(f => f.Uuid), actualFacts.Select(f => f.Uuid));
        }
    }

    [Fact]
    public void Options_are_validated()
    {
        const string connection = "Server=x;Database=y";
        Assert.Throws<ArgumentException>(() => new SqlServerDriver(connection, new SqlServerGraphOptions { Schema = "a;b" }));
        Assert.Throws<ArgumentException>(() => new SqlServerDriver(connection, new SqlServerGraphOptions { TablePrefix = "x]" }));
        Assert.Throws<ArgumentException>(
            () => new SqlServerDriver(connection, new SqlServerGraphOptions { EmbeddingDimension = 1999 }));
        Assert.Throws<ArgumentException>(() => new SqlServerDriver(connection, new SqlServerGraphOptions { BulkBatchSize = 0 }));
        Assert.Equal("sqlserver", new SqlServerDriver(connection).Provider);
    }

    /// <summary>An application context that hosts the graph tables next to its own.</summary>
    private sealed class HostDbContext(DbContextOptions<HostDbContext> options, SqlServerGraphOptions graphOptions)
        : DbContext(options)
    {
        public string Schema => graphOptions.Schema;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().ToTable("host_notes_" + graphOptions.Schema);
            modelBuilder.AddGraphiti(graphOptions);
        }
    }

    /// <summary>Each test uses its own schema, so the model is cached per schema.</summary>
    private sealed class HostModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((HostDbContext)context).Schema, designTime);
    }

    private sealed class Note
    {
        public int Id { get; set; }

        public string Text { get; set; } = "";
    }
}
