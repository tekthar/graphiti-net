// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql.Entities;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>
/// The table list (<see cref="GraphitiSchema.Tables"/>) against what the schema build creates, and the typed error when
/// the database cannot hold vectors.
/// </summary>
public sealed class PostgresSchemaTests : IAsyncLifetime
{
    private readonly string _schema = TestDatabase.NewSchema();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => TestDatabase.SkipReason is null ? TestDatabase.DropSchemaAsync(_schema) : Task.CompletedTask;

    [Fact]
    public void Tables_lists_the_graph_tables_with_their_record_types_and_the_search_statistics()
    {
        var tables = GraphitiSchema.Tables(new PostgresGraphOptions { Schema = "app", TablePrefix = "graph_" });

        Assert.Equal(
            ["app.graph_communities", "app.graph_community_edges", "app.graph_entities", "app.graph_entity_edges",
                "app.graph_episodes", "app.graph_episodic_edges", "app.graph_has_episode_edges",
                "app.graph_next_episode_edges", "app.graph_sagas", "app.graph_search_statistics"],
            tables.Select(t => t.QualifiedName));
        Assert.Equal(typeof(EntityNodeRecord), tables.Single(t => t.Name == "graph_entities").RecordType);
        Assert.Equal(typeof(EntityEdgeRecord), tables.Single(t => t.Name == "graph_entity_edges").RecordType);
        Assert.Null(tables.Single(t => t.Name == "graph_search_statistics").RecordType);
        Assert.Equal("graphiti", GraphitiSchema.Tables()[0].Schema);
    }

    [PostgresFact]
    public async Task Tables_are_exactly_what_the_schema_build_creates()
    {
        var options = new PostgresGraphOptions { Schema = _schema, TablePrefix = "gx_", EmbeddingDimension = 4 };
        await using var driver = new PostgresDriver(TestDatabase.ConnectionString, options);

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_schema::text || '.' || table_name::text FROM information_schema.tables "
            + "WHERE table_schema = $1 ORDER BY table_name COLLATE \"C\"",
            connection);
        command.Parameters.AddWithValue(_schema);
        var created = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                created.Add(reader.GetString(0));
            }
        }

        Assert.Equal(GraphitiSchema.Tables(options).Select(t => t.QualifiedName), created);
    }

    [PostgresFact]
    public async Task CanHoldVectors_is_true_on_the_test_database()
    {
        await using var driver = new PostgresDriver(TestDatabase.ConnectionString);

        Assert.True(await driver.CanHoldVectorsAsync());
    }

    /// <summary>
    /// pgvector is installed on the server, but the extension does not exist in a fresh database and the role there
    /// is not a superuser: <c>CREATE EXTENSION</c> is refused.
    /// </summary>
    [PostgresFact]
    public async Task Build_throws_a_typed_error_when_the_role_may_not_create_the_vector_extension()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var database = "graphiti_novector_" + suffix;
        var role = "graphiti_limited_" + suffix;
        const string Password = "graphiti_limited_pw";
        await AdminExecuteAsync($"CREATE DATABASE \"{database}\"");
        try
        {
            await AdminExecuteAsync($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{Password}'");
            var limited = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
            {
                Database = database,
                Username = role,
                Password = Password,
                Pooling = false,
            };
            var admin = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
            {
                Database = database,
                Pooling = false,
            };
            await using var driver = new PostgresDriver(limited.ConnectionString);
            await using var adminDriver = new PostgresDriver(admin.ConnectionString);

            Assert.False(await driver.CanHoldVectorsAsync());
            Assert.True(await adminDriver.CanHoldVectorsAsync());
            var error = await Assert.ThrowsAsync<VectorStoreUnavailableException>(
                () => driver.GraphOps.BuildIndicesAndConstraintsAsync());

            Assert.Equal(VectorStoreUnavailableReason.NotPermitted, error.Reason);
            Assert.Equal("postgresql", error.Provider);
            Assert.Equal(database, error.Database);
            Assert.Contains($"database \"{database}\"", error.Message, StringComparison.Ordinal);
            Assert.Contains($"role \"{role}\"", error.Message, StringComparison.Ordinal);
            Assert.Contains("superuser run CREATE EXTENSION vector;", error.Message, StringComparison.Ordinal);
            var inner = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, inner.SqlState);
        }
        finally
        {
            await AdminExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await AdminExecuteAsync($"DROP ROLE IF EXISTS \"{role}\"");
        }
    }

    /// <summary>
    /// A PostgreSQL server without pgvector installed (<c>GRAPHITI_TEST_POSTGRES_NO_PGVECTOR</c>, for example the
    /// plain <c>postgres:16</c> image): <c>CREATE EXTENSION</c> finds no extension to create.
    /// </summary>
    [PostgresWithoutPgvectorFact]
    public async Task Build_throws_a_typed_error_when_pgvector_is_not_installed()
    {
        var connectionString = PostgresWithoutPgvectorFactAttribute.ConnectionString!;
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var driver = new PostgresDriver(connectionString, new PostgresGraphOptions { Schema = _schema });

        Assert.False(await driver.CanHoldVectorsAsync());
        var error = await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => driver.GraphOps.BuildIndicesAndConstraintsAsync());

        Assert.Equal(VectorStoreUnavailableReason.NotInstalled, error.Reason);
        Assert.Equal(database, error.Database);
        Assert.Contains($"database \"{database}\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("pgvector extension (\"vector\") is not installed", error.Message, StringComparison.Ordinal);
        Assert.Contains("Install pgvector", error.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.FeatureNotSupported, inner.SqlState);
    }

    private static async Task AdminExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A fact that runs only when <c>GRAPHITI_TEST_POSTGRES_NO_PGVECTOR</c> names a reachable PostgreSQL database whose
/// server does not have pgvector installed.
/// </summary>
public sealed class PostgresWithoutPgvectorFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    /// <summary>Sets <see cref="FactAttribute.Skip"/> when no such database is configured or reachable.</summary>
    public PostgresWithoutPgvectorFactAttribute()
    {
        if (UnavailableReason.Value is { } reason)
        {
            Skip = reason;
        }
    }

    /// <summary>The connection string, or <see langword="null"/> when not configured.</summary>
    public static string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GRAPHITI_TEST_POSTGRES_NO_PGVECTOR") is { Length: > 0 } value ? value : null;

    private static string? Probe()
    {
        if (ConnectionString is null)
        {
            return "GRAPHITI_TEST_POSTGRES_NO_PGVECTOR is not set (a PostgreSQL server without pgvector, such as the "
                + "postgres:16 image).";
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Timeout = 5 };
        try
        {
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_available_extensions WHERE name = 'vector'",
                connection);
            return (long)command.ExecuteScalar()! == 0
                ? null
                : $"GRAPHITI_TEST_POSTGRES_NO_PGVECTOR ({builder.Host}:{builder.Port}) has pgvector installed.";
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return $"PostgreSQL without pgvector is not reachable at {builder.Host}:{builder.Port}: {e.Message}";
        }
    }
}
