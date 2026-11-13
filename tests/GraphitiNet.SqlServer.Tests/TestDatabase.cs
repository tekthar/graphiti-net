// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Data.SqlClient;

namespace GraphitiNet.Driver.SqlServer.Tests;

/// <summary>
/// The test database: <c>GRAPHITI_TEST_SQLSERVER</c>, or a local default (the <c>sa</c> login of a SQL Server 2025
/// container started as in docs/sqlserver.md). Created if missing; tests skip when the server cannot be reached.
/// </summary>
internal static class TestDatabase
{
    public const string DefaultConnectionString =
        "Server=localhost,1433;Database=graphiti_sqlserver_tests;User Id=sa;Password=Graphiti_dev_Passw0rd;"
        + "TrustServerCertificate=True";

    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("GRAPHITI_TEST_SQLSERVER") is { Length: > 0 } value
            ? value
            : DefaultConnectionString;

    /// <summary>Why the database cannot be used, or <see langword="null"/> when it can.</summary>
    public static string? SkipReason => UnavailableReason.Value;

    /// <summary>A unique schema name for one test.</summary>
    public static string NewSchema() => "t_" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Drops every table in a schema, then the schema. Tests run in parallel, so this DDL can cross another test's
    /// schema build and be chosen as the deadlock victim (error 1205); it is then retried.
    /// </summary>
    public static async Task DropSchemaAsync(string schema)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DropSchemaOnceAsync(schema);
                return;
            }
            catch (SqlException e) when (e.Number == 1205 && attempt < 5)
            {
                await Task.Delay(100 * attempt);
            }
        }
    }

    private static async Task DropSchemaOnceAsync(string schema)
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
    }

    public static async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<string>> ListAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
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
            // Cannot open the database: create it.
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

/// <summary>A fact that runs only when the test database is reachable.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> when the database is unreachable.</summary>
    public SqlServerFactAttribute()
    {
        if (TestDatabase.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
