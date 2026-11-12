// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using static GraphitiNet.Driver.PostgreSql.Tests.GraphBuilders;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>PostgreSQL-specific behaviour: schema creation, hosting modes, transactions and options.</summary>
public sealed class PostgresDriverTests : IAsyncLifetime
{
    private readonly string _schema = TestDatabase.NewSchema();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => TestDatabase.SkipReason is null ? TestDatabase.DropSchemaAsync(_schema) : Task.CompletedTask;

    private PostgresGraphOptions Options => new() { Schema = _schema, EmbeddingDimension = 4 };

    [PostgresFact]
    public async Task Build_creates_tables_vector_columns_and_indexes_on_an_empty_database()
    {
        await using var driver = new PostgresDriver(TestDatabase.ConnectionString, Options with { EmbeddingDimension = 6 });

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();

        var tables = await QueryAsync<string>(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema = $1 ORDER BY 1", _schema);
        Assert.Equal(
            ["communities", "community_edges", "entities", "entity_edges", "episodes", "episodic_edges",
                "has_episode_edges", "next_episode_edges", "sagas"],
            tables);

        var indexes = await QueryAsync<string>("SELECT indexdef FROM pg_indexes WHERE schemaname = $1", _schema);
        Assert.Equal(3, indexes.Count(i => i.Contains("USING hnsw", StringComparison.Ordinal)
            && i.Contains("vector_cosine_ops", StringComparison.Ordinal)));
        Assert.Equal(4, indexes.Count(i => i.Contains("USING gin (search_vector)", StringComparison.Ordinal)));
        Assert.Equal(9, indexes.Count(i => i.Contains("UNIQUE INDEX pk_", StringComparison.Ordinal)));

        var vectorType = await QueryAsync<string>(
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute "
            + "WHERE attrelid = ($1 || '.entities')::regclass AND attname = 'name_embedding'",
            _schema);
        Assert.Equal(["vector(6)"], vectorType);

        var generated = await QueryAsync<string>(
            "SELECT table_name::text FROM information_schema.columns "
            + "WHERE table_schema = $1 AND column_name = 'search_vector' AND is_generated = 'ALWAYS' ORDER BY 1",
            _schema);
        Assert.Equal(["communities", "entities", "entity_edges", "episodes"], generated);
    }

    [PostgresFact]
    public async Task Build_recreates_a_dropped_table_without_touching_the_others()
    {
        await using var driver = new PostgresDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("kept"));
        await ExecuteAsync($"DROP TABLE \"{_schema}\".community_edges");
        await ExecuteAsync($"DROP INDEX \"{_schema}\".ix_entities_attributes");

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();

        // A missing index on an existing table (as after an upgrade that adds one) is created too.
        Assert.Single(await QueryAsync<string>(
            "SELECT indexname::text FROM pg_indexes WHERE schemaname = $1 AND indexname = 'ix_entities_attributes'",
            _schema));
        Assert.Empty(await driver.CommunityEdgeOps.GetByGroupIdsAsync([Group]));
        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
    }

    [PostgresFact]
    public async Task Bulk_ingestion_is_one_transaction()
    {
        await using var driver = new PostgresDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        var episode = Episode();
        var alice = Entity("Alice", embedding: [1, 0, 0, 0]);
        var bob = Entity("Bob");
        var badFact = Fact(alice, bob, embedding: [1, 2, 3]);

        await Assert.ThrowsAsync<PostgresException>(
            () => driver.AddNodesAndEdgesBulkAsync([episode], [Mention(episode, alice)], [alice, bob], [badFact]));

        Assert.Equal(0, await TestDatabase.CountAsync(_schema, "episodes"));
        Assert.Equal(0, await TestDatabase.CountAsync(_schema, "entities"));
        Assert.Equal(0, await TestDatabase.CountAsync(_schema, "episodic_edges"));
    }

    [PostgresFact]
    public async Task Standalone_context_reads_what_the_driver_wrote()
    {
        await using var driver = new PostgresDriver(TestDatabase.ConnectionString, Options);
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("Alice", embedding: [1, 0, 0, 0], summary: "engineer"));

        await using var context = new GraphitiDbContext(TestDatabase.ConnectionString, Options);
        var row = await context.Entities.SingleAsync();

        Assert.Equal("Alice", row.Name);
        Assert.Equal(new float[] { 1, 0, 0, 0 }, row.NameEmbedding!.ToArray());
        Assert.Equal(_schema, context.Model.FindEntityType(typeof(EntityNodeRecord))!.GetSchema());
    }

    [PostgresFact]
    public async Task Host_dbcontext_mode_creates_only_the_graph_tables()
    {
        var options = Options;
        var dbOptions = new DbContextOptionsBuilder<HostDbContext>()
            .UseNpgsql(TestDatabase.ConnectionString, o => o.UseVector())
            .ReplaceService<IModelCacheKeyFactory, HostModelCacheKeyFactory>()
            .Options;
        await using var driver = new PostgresDriver(() => new HostDbContext(dbOptions, options), options);

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        await driver.EntityNodeOps.SaveAsync(Entity("Alice"));

        Assert.Single(await driver.EntityNodeOps.GetByGroupIdsAsync([Group]));
        var noteTable = await QueryAsync<string>(
            "SELECT table_name::text FROM information_schema.tables WHERE table_name = $1", "host_notes_" + _schema);
        Assert.Empty(noteTable);

        // The host's own migrations would include the graph tables.
        await using var context = new HostDbContext(dbOptions, options);
        var script = context.Database.GenerateCreateScript();
        Assert.Contains($"CREATE TABLE {_schema}.entities", script, StringComparison.Ordinal);
        Assert.Contains("vector(4)", script, StringComparison.Ordinal);
        Assert.Contains("USING hnsw", script, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_dbcontext_with_another_schema_is_rejected()
    {
        var dbOptions = new DbContextOptionsBuilder<HostDbContext>()
            .UseNpgsql(TestDatabase.ConnectionString, o => o.UseVector())
            .ReplaceService<IModelCacheKeyFactory, HostModelCacheKeyFactory>()
            .Options;
        await using var driver = new PostgresDriver(
            () => new HostDbContext(dbOptions, Options with { Schema = "somewhere_else" }), Options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.EntityNodeOps.GetByUuidsAsync(["x"]));
        Assert.Contains("somewhere_else", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new PostgresDriver("Host=x", new PostgresGraphOptions { Schema = "a;b" }));
        Assert.Throws<ArgumentException>(
            () => new PostgresDriver("Host=x", new PostgresGraphOptions { DefaultTextSearchConfig = "english'" }));
        Assert.Throws<ArgumentException>(
            () => new PostgresDriver("Host=x", new PostgresGraphOptions { EmbeddingDimension = 0 }));
        Assert.Equal("postgresql", new PostgresDriver("Host=x").Provider);
    }

    private static async Task<List<T>> QueryAsync<T>(string sql, string parameter)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter);
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetFieldValue<T>(0));
        }

        return result;
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An application context that hosts the graph tables next to its own.</summary>
    private sealed class HostDbContext(DbContextOptions<HostDbContext> options, PostgresGraphOptions graphOptions)
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
