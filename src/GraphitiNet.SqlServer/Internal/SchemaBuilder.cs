// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>
/// Creates graphiti-net's schema from the EF Core model, so the DDL is exactly what an EF Core migration of the same
/// model would produce. Idempotent: only missing tables are created (with their indexes), and indexes a newer version
/// added are created on existing tables. Runs in one transaction under an application lock, so concurrent callers do
/// not race. SQL Server can still pick the build as a deadlock victim when its catalog reads and DDL cross another
/// session's DDL (a host's migrations, for example); the build is idempotent and its transaction is rolled back, so it
/// is retried from the start.
/// </summary>
internal static class SchemaBuilder
{
    /// <summary>Attempts at the whole build, including the first, when SQL Server picks it as a deadlock victim.</summary>
    internal const int MaxAttempts = 5;

    // "Transaction (Process ID n) was deadlocked on lock resources with another process and has been chosen as the
    // deadlock victim."
    private const int DeadlockVictimError = 1205;

    // sp_getapplock's return value when the lock request itself was chosen as the deadlock victim.
    private const int AppLockDeadlockVictim = -3;

    public static async Task EnsureSchemaAsync(DriverCore core, bool deleteExisting, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (await TryEnsureSchemaAsync(core, deleteExisting, ct).ConfigureAwait(false))
                {
                    return;
                }

                if (attempt >= MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"graphiti-net could not take the schema lock: SQL Server chose the request as a deadlock "
                        + $"victim {MaxAttempts} times.");
                }
            }
            catch (SqlException e) when (e.Number == DeadlockVictimError && attempt < MaxAttempts)
            {
                // Rolled back by SQL Server; try again.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the schema in one transaction under the schema lock. Returns <see langword="false"/>, having changed
    /// nothing, when the lock request was chosen as a deadlock victim.
    /// </summary>
    private static async Task<bool> TryEnsureSchemaAsync(DriverCore core, bool deleteExisting, CancellationToken ct)
    {
        await using var context = core.CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();
        var schema = core.Options.Schema;
        var names = core.Names;

        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)context.Database.GetDbConnection();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var db = new Db(connection, transaction, context.Database.GetCommandTimeout());

            // sp_getapplock reports failure in its return value, not as an error: without this check a refused
            // request would build the schema without the lock.
            var lockResult = await ScalarIntAsync(
                db,
                "DECLARE @result int; "
                + "EXEC @result = sp_getapplock @Resource = N'graphiti-net schema', @LockMode = 'Exclusive', "
                + "@LockOwner = 'Transaction', @LockTimeout = -1; "
                + "SELECT @result;",
                ct).ConfigureAwait(false);
            if (lockResult == AppLockDeadlockVictim)
            {
                return false;
            }

            if (lockResult < 0)
            {
                throw new InvalidOperationException(
                    $"graphiti-net could not take the schema lock: sp_getapplock returned {lockResult}.");
            }

            if (deleteExisting)
            {
                foreach (var table in SqlNames.AllTables)
                {
                    await ExecuteAsync(db, $"DROP TABLE IF EXISTS {names.Table(table)}", ct).ConfigureAwait(false);
                }
            }

            var existing = await ReadNamesAsync(
                db,
                "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema",
                schema,
                ct).ConfigureAwait(false);
            var existingIndexes = await ReadNamesAsync(
                db,
                "SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id "
                + "JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema AND i.name IS NOT NULL",
                schema,
                ct).ConfigureAwait(false);

            var ours = SqlNames.AllTables.Select(names.Name).ToHashSet(StringComparer.Ordinal);
            var created = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<MigrationOperation>();
            foreach (var operation in operations)
            {
                switch (operation)
                {
                    case EnsureSchemaOperation ensure when ensure.Name == schema:
                        selected.Add(ensure);
                        break;
                    case CreateTableOperation table
                        when table.Schema == schema && ours.Contains(table.Name) && !existing.Contains(table.Name):
                        selected.Add(table);
                        created.Add(table.Name);
                        break;
                    // Indexes of new tables, and indexes a newer version added to existing tables.
                    case CreateIndexOperation index when index.Schema == schema && ours.Contains(index.Table)
                        && (created.Contains(index.Table) || !existingIndexes.Contains(index.Name)):
                        selected.Add(index);
                        break;
                }
            }

            foreach (var command in sqlGenerator.Generate(selected, model))
            {
                await ExecuteAsync(db, command.CommandText, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> ScalarIntAsync(Db db, string sql, CancellationToken ct)
    {
        await using var command = db.Command(sql);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HashSet<string>> ReadNamesAsync(Db db, string sql, string schema, CancellationToken ct)
    {
        await using var command = db.Command(sql);
        command.Parameters.Add(Params.Text(schema)).ParameterName = "@schema";
        var names = await DriverCore.ReadListAsync(command, r => r.GetString(0), ct).ConfigureAwait(false);
        return names.ToHashSet(StringComparer.Ordinal);
    }

    private static async Task ExecuteAsync(Db db, string sql, CancellationToken ct)
    {
        await using var command = db.Command(sql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
