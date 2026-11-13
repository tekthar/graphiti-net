// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Driver.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>The storage backends every criterion runs against.</summary>
public static class Drivers
{
    public const string InMemory = "in-memory";

    public const string PostgreSql = "postgresql";

    public const string SqlServer = "sqlserver";

    public static TheoryData<string> All => new() { InMemory, PostgreSql };

    /// <summary>Every bundled driver, SQL Server included.</summary>
    public static TheoryData<string> Every => new() { InMemory, PostgreSql, SqlServer };
}

/// <summary>
/// One <see cref="Graphiti"/> over a fresh store, with the scripted model and embedder. On PostgreSQL each harness
/// uses its own schema, dropped on dispose; the test is skipped when the server cannot be reached.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    private readonly string? _schema;
    private readonly bool _sqlServer;

    private Harness(Graphiti graphiti, ScriptedLlm llm, WordHashEmbedder embedder, string? schema, bool sqlServer)
    {
        Graphiti = graphiti;
        Llm = llm;
        Embedder = embedder;
        _schema = schema;
        _sqlServer = sqlServer;
    }

    public Graphiti Graphiti { get; }

    public ScriptedLlm Llm { get; }

    public WordHashEmbedder Embedder { get; }

    /// <summary>The PostgreSQL schema, or <see langword="null"/> in memory.</summary>
    public string? Schema => _schema;

    public static async Task<Harness> CreateAsync(
        string driverKind,
        ScriptedLlm llm,
        IGroupLock? groupLock = null,
        IModelCallGate? gate = null,
        IReadOnlyDictionary<string, string>? textSearchConfigs = null,
        Func<IGraphDriver, IGraphDriver>? wrapDriver = null)
    {
        string? schema = null;
        IGraphDriver driver;
        if (driverKind == Drivers.PostgreSql)
        {
            Postgres.SkipIfUnavailable();
            schema = Postgres.NewSchema();
            driver = new PostgresDriver(Postgres.ConnectionString, Postgres.Options(schema, textSearchConfigs));
        }
        else if (driverKind == Drivers.SqlServer)
        {
            SqlServerDatabase.SkipIfUnavailable();
            schema = SqlServerDatabase.NewSchema();
            driver = new SqlServerDriver(
                SqlServerDatabase.ConnectionString,
                new SqlServerGraphOptions { Schema = schema, EmbeddingDimension = WordHashEmbedder.Dimension });
        }
        else
        {
            driver = new InMemoryGraphDriver();
        }

        if (wrapDriver is not null)
        {
            driver = wrapDriver(driver);
        }

        return await CreateAsync(driver, llm, groupLock, gate, schema, driverKind == Drivers.SqlServer);
    }

    public static async Task<Harness> CreateAsync(
        IGraphDriver driver,
        ScriptedLlm llm,
        IGroupLock? groupLock = null,
        IModelCallGate? gate = null,
        string? schema = null,
        bool sqlServer = false)
    {
        var embedder = new WordHashEmbedder();
        var graphiti = new Graphiti(
            driver,
            llm,
            embedder,
            new WordOverlapCrossEncoder(),
            groupLock: groupLock,
            modelCallGate: gate);
        await graphiti.BuildIndicesAndConstraintsAsync();
        return new Harness(graphiti, llm, embedder, schema, sqlServer);
    }

    public static string NewGroup() => "g" + Guid.NewGuid().ToString("N")[..12];

    public async ValueTask DisposeAsync()
    {
        await Graphiti.DisposeAsync();
        if (_schema is not null)
        {
            await (_sqlServer ? SqlServerDatabase.DropSchemaAsync(_schema) : Postgres.DropSchemaAsync(_schema));
        }
    }
}

/// <summary>
/// The SQL Server test database: <c>GRAPHITI_TEST_SQLSERVER</c>, or the local SQL Server 2025 container of
/// docs/sqlserver.md (created if missing). Tests skip when the server cannot be reached.
/// </summary>
public static class SqlServerDatabase
{
    public const string DefaultConnectionString =
        "Server=localhost,1433;Database=graphiti_acceptance_tests;User Id=sa;Password=Graphiti_dev_Passw0rd;"
        + "TrustServerCertificate=True";

    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GRAPHITI_TEST_SQLSERVER") is { Length: > 0 } value
            ? value
            : DefaultConnectionString;

    public static void SkipIfUnavailable() =>
        Skip.If(UnavailableReason.Value is not null, UnavailableReason.Value);

    public static string NewSchema() => "acc_" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>Drops the schema's tables, then the schema; retried when chosen as a deadlock victim.</summary>
    public static async Task DropSchemaAsync(string schema)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "DECLARE @sql nvarchar(max) = N''; "
                    + "SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N'; ' "
                    + "FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema; "
                    + "EXEC sp_executesql @sql; "
                    + "IF SCHEMA_ID(@schema) IS NOT NULL EXEC (N'DROP SCHEMA ' + @quoted);",
                    connection);
                command.Parameters.AddWithValue("@schema", schema);
                command.Parameters.AddWithValue("@quoted", "[" + schema + "]");
                await command.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException e) when (e.Number == 1205 && attempt < 5)
            {
                await Task.Delay(100 * attempt);
            }
        }
    }

    private static string? Probe()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { ConnectTimeout = 5 };
        try
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (SqlException e) when (e.Number == 4060)
        {
            return TryCreateDatabase(builder);
        }
        catch (Exception e) when (e is SqlException or TimeoutException or InvalidOperationException)
        {
            return $"SQL Server is not reachable at {builder.DataSource}: {e.Message}";
        }
    }

    private static string? TryCreateDatabase(SqlConnectionStringBuilder builder)
    {
        var database = builder.InitialCatalog;
        var admin = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        try
        {
            using var connection = new SqlConnection(admin.ConnectionString);
            connection.Open();
            using var command = new SqlCommand(
                $"IF DB_ID(@name) IS NULL EXEC (N'CREATE DATABASE [{database.Replace("]", "]]", StringComparison.Ordinal)}]')",
                connection);
            command.Parameters.AddWithValue("@name", database);
            command.ExecuteNonQuery();
            return null;
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException)
        {
            return $"Test database {database} does not exist and could not be created: {e.Message}";
        }
    }
}

/// <summary>The test database: <c>GRAPHITI_TEST_POSTGRES</c>, or a local default (created if missing).</summary>
public static class Postgres
{
    public const string DefaultConnectionString =
        "Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests";

    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GRAPHITI_TEST_POSTGRES") is { Length: > 0 } value
            ? value
            : DefaultConnectionString;

    public static void SkipIfUnavailable() =>
        Skip.If(UnavailableReason.Value is not null, UnavailableReason.Value);

    public static string NewSchema() => "acc_" + Guid.NewGuid().ToString("N")[..16];

    public static PostgresGraphOptions Options(
        string schema,
        IReadOnlyDictionary<string, string>? textSearchConfigs = null,
        string tablePrefix = "") =>
        new()
        {
            Schema = schema,
            TablePrefix = tablePrefix,
            EmbeddingDimension = WordHashEmbedder.Dimension,
            TextSearchConfigs = textSearchConfigs,
        };

    public static async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> QueryStringsAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static string? Probe()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Timeout = 5 };
        try
        {
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return TryCreateDatabase(builder);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return $"PostgreSQL is not reachable at {builder.Host}: {e.Message}";
        }
    }

    private static string? TryCreateDatabase(NpgsqlConnectionStringBuilder builder)
    {
        var database = builder.Database!;
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" };
        try
        {
            using var connection = new NpgsqlConnection(admin.ConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{database.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
            command.ExecuteNonQuery();
            return null;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return $"Test database {database} does not exist and could not be created: {e.Message}";
        }
    }
}
