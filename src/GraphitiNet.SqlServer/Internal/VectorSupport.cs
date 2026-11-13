// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>
/// The <c>vector</c> type the schema needs (SQL Server 2025 and Azure SQL): whether the database has it, and the
/// <see cref="VectorStoreUnavailableException"/> when it does not.
/// </summary>
internal static class VectorSupport
{
    // A built-in type of SQL Server 2025 and Azure SQL, at every compatibility level; absent before.
    private const string HasVectorTypeSql =
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.types WHERE name = N'vector' AND is_user_defined = 0) "
        + "THEN 1 ELSE 0 END";

    public static async Task<bool> HasVectorTypeAsync(Db db, CancellationToken ct)
    {
        await using var command = db.Command(HasVectorTypeSql);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Throws <see cref="VectorStoreUnavailableException"/> when the database has no <c>vector</c> type.</summary>
    public static async Task EnsureVectorTypeAsync(Db db, CancellationToken ct)
    {
        if (await HasVectorTypeAsync(db, ct).ConfigureAwait(false))
        {
            return;
        }

        var connection = db.Connection;
        throw new VectorStoreUnavailableException(
            $"SQL Server database \"{connection.Database}\" on {connection.DataSource} (version "
            + $"{connection.ServerVersion}) cannot hold graphiti-net's embedding vectors: it has no vector data type, "
            + "which SQL Server 2025 (version 17) and Azure SQL introduced. Use SQL Server 2025, Azure SQL Database, or "
            + "Azure SQL Managed Instance with the SQL Server 2025 or Always-up-to-date update policy, then run "
            + "BuildIndicesAndConstraintsAsync again.",
            "sqlserver",
            connection.Database,
            VectorStoreUnavailableReason.NotSupported);
    }
}
