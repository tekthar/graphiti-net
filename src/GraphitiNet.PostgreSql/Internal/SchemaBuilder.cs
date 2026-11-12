// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// Creates graphiti-net's schema from the EF Core model, so the DDL is exactly what an EF Core migration of the same
/// model would produce. Idempotent: only missing tables are created (with their indexes). Runs in one transaction
/// under an advisory lock, so concurrent callers do not race.
/// </summary>
internal static class SchemaBuilder
{
    public static async Task EnsureSchemaAsync(DriverCore core, bool deleteExisting, CancellationToken ct)
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
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // One lock for every schema: CREATE EXTENSION itself is not safe to run concurrently.
            await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(hashtext('graphiti-net schema'))", ct)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS vector", ct).ConfigureAwait(false);

            if (deleteExisting)
            {
                var tables = string.Join(", ", SqlNames.AllTables.Select(names.Table));
                await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {tables} CASCADE", ct).ConfigureAwait(false);
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand(
                "SELECT table_name::text FROM information_schema.tables WHERE table_schema = $1",
                connection))
            {
                command.Parameters.Add(Params.Text(schema));
                existing.UnionWith(await DriverCore.ReadListAsync(command, r => r.GetString(0), ct).ConfigureAwait(false));
            }

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
                    case CreateIndexOperation index when index.Schema == schema && created.Contains(index.Table):
                        selected.Add(index);
                        break;
                }
            }

            foreach (var command in sqlGenerator.Generate(selected, model))
            {
                await ExecuteAsync(connection, command.CommandText, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken ct,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
