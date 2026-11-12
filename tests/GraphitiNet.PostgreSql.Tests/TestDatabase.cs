// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>
/// The test database: <c>GRAPHITI_TEST_POSTGRES</c>, or a local default. Created if missing; tests skip when the
/// server cannot be reached.
/// </summary>
internal static class TestDatabase
{
    public const string DefaultConnectionString =
        "Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests";

    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GRAPHITI_TEST_POSTGRES") is { Length: > 0 } value
            ? value
            : DefaultConnectionString;

    /// <summary>Why the database cannot be used, or <see langword="null"/> when it can.</summary>
    public static string? SkipReason => UnavailableReason.Value;

    /// <summary>A unique schema name for one test.</summary>
    public static string NewSchema() => "t_" + Guid.NewGuid().ToString("N")[..16];

    public static async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{table}\"", connection);
        return (long)(await command.ExecuteScalarAsync())!;
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
                $"CREATE DATABASE \"{database.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
                connection);
            command.ExecuteNonQuery();
            return null;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return $"Test database {database} does not exist and could not be created: {e.Message}";
        }
    }
}

/// <summary>A fact that runs only when the test database is reachable.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> when the database is unreachable.</summary>
    public PostgresFactAttribute()
    {
        if (TestDatabase.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
