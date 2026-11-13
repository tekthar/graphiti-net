// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Driver.SqlServer;
using Npgsql;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>The storage backends every criterion runs against.</summary>
public static class Drivers
{
    public const string InMemory = "in-memory";

    public const string PostgreSql = "postgresql";

    public const string SqlServer = "sqlserver";

    public static TheoryData<string> All => new() { InMemory, PostgreSql, SqlServer };

    /// <summary>Every bundled driver; the same as <see cref="All"/>.</summary>
    public static TheoryData<string> Every => All;
}

/// <summary>
/// One <see cref="Graphiti"/> over a fresh store, with the scripted model and embedder. On PostgreSQL and SQL Server
/// each harness uses its own schema, dropped on dispose; the test is skipped when the server cannot be reached.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    private readonly string? _schema;

    private readonly Func<string, Task> _dropSchema;

    private Harness(
        Graphiti graphiti,
        ScriptedLlm llm,
        WordHashEmbedder embedder,
        string? schema,
        Func<string, Task> dropSchema)
    {
        Graphiti = graphiti;
        Llm = llm;
        Embedder = embedder;
        _schema = schema;
        _dropSchema = dropSchema;
    }

    public Graphiti Graphiti { get; }

    public ScriptedLlm Llm { get; }

    public WordHashEmbedder Embedder { get; }

    /// <summary>The PostgreSQL or SQL Server schema, or <see langword="null"/> in memory.</summary>
    public string? Schema => _schema;

    public static async Task<Harness> CreateAsync(
        string driverKind,
        ScriptedLlm llm,
        IGroupLock? groupLock = null,
        IModelCallGate? gate = null,
        IReadOnlyDictionary<string, string>? textSearchConfigs = null,
        CrossEncoderClient? crossEncoder = null,
        Func<IGraphDriver, IGraphDriver>? wrapDriver = null)
    {
        string? schema = null;
        Func<string, Task>? dropSchema = null;
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
            dropSchema = SqlServerDatabase.DropSchemaAsync;
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

        return await CreateAsync(driver, llm, groupLock, gate, schema, dropSchema, crossEncoder);
    }

    public static async Task<Harness> CreateAsync(
        IGraphDriver driver,
        ScriptedLlm llm,
        IGroupLock? groupLock = null,
        IModelCallGate? gate = null,
        string? schema = null,
        Func<string, Task>? dropSchema = null,
        CrossEncoderClient? crossEncoder = null)
    {
        var embedder = new WordHashEmbedder();
        var graphiti = new Graphiti(
            driver,
            llm,
            embedder,
            crossEncoder ?? new WordOverlapCrossEncoder(),
            groupLock: groupLock,
            modelCallGate: gate);
        await graphiti.BuildIndicesAndConstraintsAsync();
        return new Harness(graphiti, llm, embedder, schema, dropSchema ?? Postgres.DropSchemaAsync);
    }

    public static string NewGroup() => "g" + Guid.NewGuid().ToString("N")[..12];

    public async ValueTask DisposeAsync()
    {
        await Graphiti.DisposeAsync();
        if (_schema is not null)
        {
            await _dropSchema(_schema);
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
