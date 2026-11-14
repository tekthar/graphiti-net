// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.SqlServer.Entities;
using Microsoft.Data.SqlClient;

namespace GraphitiNet.Driver.SqlServer.Tests;

/// <summary>
/// The table list (<see cref="GraphitiSchema.Tables"/>) against what the schema build creates, and the typed error when
/// the database has no <c>vector</c> type.
/// </summary>
public sealed class SqlServerSchemaTests : IAsyncLifetime
{
    private readonly string _schema = TestDatabase.NewSchema();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() =>
        TestDatabase.SkipReason is null ? TestDatabase.DropSchemaAsync(_schema) : Task.CompletedTask;

    [Fact]
    public void Tables_lists_the_graph_and_keyword_index_tables_with_their_record_types()
    {
        var tables = GraphitiSchema.Tables(new SqlServerGraphOptions { Schema = "app", TablePrefix = "graph_" });

        Assert.Equal(
            ["app.graph_communities", "app.graph_community_edges", "app.graph_entities", "app.graph_entity_edges",
                "app.graph_episodes", "app.graph_episodic_edges", "app.graph_has_episode_edges",
                "app.graph_next_episode_edges", "app.graph_sagas", "app.graph_search_fields", "app.graph_search_terms"],
            tables.Select(t => t.QualifiedName));
        Assert.Equal(typeof(EntityNodeRecord), tables.Single(t => t.Name == "graph_entities").RecordType);
        Assert.Equal(typeof(SearchTermRecord), tables.Single(t => t.Name == "graph_search_terms").RecordType);
        Assert.Equal(typeof(SearchFieldRecord), tables.Single(t => t.Name == "graph_search_fields").RecordType);
        Assert.Equal("graphiti", GraphitiSchema.Tables()[0].Schema);
    }

    [SqlServerFact]
    public async Task Tables_are_exactly_what_the_schema_build_creates()
    {
        var options = new SqlServerGraphOptions { Schema = _schema, TablePrefix = "gx_", EmbeddingDimension = 4 };
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString, options);

        await driver.GraphOps.BuildIndicesAndConstraintsAsync();

        var created = await TestDatabase.ListAsync(
            "SELECT s.name + N'.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id "
            + "WHERE s.name = @schema ORDER BY t.name COLLATE Latin1_General_100_BIN2",
            ("@schema", _schema));
        Assert.Equal(GraphitiSchema.Tables(options).Select(t => t.QualifiedName), created);
    }

    [SqlServerFact]
    public async Task CanHoldVectors_is_true_on_the_test_database()
    {
        await using var driver = new SqlServerDriver(TestDatabase.ConnectionString);

        Assert.True(await driver.CanHoldVectorsAsync());
    }

    /// <summary>
    /// A SQL Server without the <c>vector</c> type (<c>GRAPHITI_TEST_SQLSERVER_NO_VECTOR</c>, for example the
    /// <c>mcr.microsoft.com/mssql/server:2022-latest</c> image). A lower compatibility level on SQL Server 2025 does
    /// not reproduce it: the type is there at every level.
    /// </summary>
    [SqlServerWithoutVectorFact]
    public async Task Build_throws_a_typed_error_when_the_database_has_no_vector_type()
    {
        var connectionString = SqlServerWithoutVectorFactAttribute.ConnectionString!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        await using var driver = new SqlServerDriver(connectionString, new SqlServerGraphOptions { Schema = _schema });

        Assert.False(await driver.CanHoldVectorsAsync());
        var error = await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => driver.GraphOps.BuildIndicesAndConstraintsAsync());

        Assert.Equal(VectorStoreUnavailableReason.NotSupported, error.Reason);
        Assert.Equal("sqlserver", error.Provider);
        Assert.Equal(database, error.Database);
        Assert.Contains($"database \"{database}\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("no vector data type", error.Message, StringComparison.Ordinal);
        Assert.Contains("Use SQL Server 2025, Azure SQL Database", error.Message, StringComparison.Ordinal);

        // Nothing was created.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.schemas WHERE name = @schema", connection);
        command.Parameters.AddWithValue("@schema", _schema);
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
    }
}

/// <summary>
/// A fact that runs only when <c>GRAPHITI_TEST_SQLSERVER_NO_VECTOR</c> names a reachable SQL Server database without
/// the <c>vector</c> type (created if missing).
/// </summary>
public sealed class SqlServerWithoutVectorFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    /// <summary>Sets <see cref="FactAttribute.Skip"/> when no such database is configured or reachable.</summary>
    public SqlServerWithoutVectorFactAttribute()
    {
        if (UnavailableReason.Value is { } reason)
        {
            Skip = reason;
        }
    }

    /// <summary>The connection string, or <see langword="null"/> when not configured.</summary>
    public static string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GRAPHITI_TEST_SQLSERVER_NO_VECTOR") is { Length: > 0 } value ? value : null;

    private static string? Probe()
    {
        if (ConnectionString is null)
        {
            return "GRAPHITI_TEST_SQLSERVER_NO_VECTOR is not set (a SQL Server before 2025, such as the "
                + "mcr.microsoft.com/mssql/server:2022-latest image).";
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString) { ConnectTimeout = 5 };
        var admin = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        try
        {
            using var connection = new SqlConnection(admin.ConnectionString);
            connection.Open();
            using var command = new SqlCommand(
                $"IF DB_ID(@name) IS NULL EXEC (N'CREATE DATABASE [{builder.InitialCatalog.Replace("]", "]]", StringComparison.Ordinal)}]'); "
                + "SELECT COUNT(*) FROM sys.types WHERE name = N'vector' AND is_user_defined = 0",
                connection);
            command.Parameters.AddWithValue("@name", builder.InitialCatalog);
            return (int)command.ExecuteScalar()! == 0
                ? null
                : $"GRAPHITI_TEST_SQLSERVER_NO_VECTOR ({builder.DataSource}) has the vector type.";
        }
        catch (Exception e) when (e is SqlException or TimeoutException or InvalidOperationException)
        {
            return $"SQL Server without the vector type is not reachable at {builder.DataSource}: {e.Message}";
        }
    }
}
