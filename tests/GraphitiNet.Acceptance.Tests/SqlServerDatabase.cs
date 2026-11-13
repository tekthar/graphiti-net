// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Data.SqlClient;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>
/// The SQL Server test database: <c>GRAPHITI_TEST_SQLSERVER</c>, or a local default (the <c>sa</c> login of a SQL
/// Server 2025 container started as in docs/sqlserver.md), created if missing.
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

    /// <summary>Drops every table in the schema, then the schema; retried when chosen as a deadlock victim (tests
    /// run in parallel).</summary>
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
