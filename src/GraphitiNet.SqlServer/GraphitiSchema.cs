// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.SqlServer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphitiNet.Driver.SqlServer;

/// <summary>Describes the schema the SQL Server driver creates, without a database.</summary>
public static class GraphitiSchema
{
    // Builds the model only; never opened.
    private const string PlaceholderConnectionString = "Server=localhost";

    /// <summary>
    /// The tables <c>BuildIndicesAndConstraintsAsync</c> creates and the driver maintains for a schema name and table
    /// prefix: the nine graph tables and the two keyword index tables (<c>search_terms</c>, <c>search_fields</c>) of
    /// the EF Core model (<see cref="GraphitiModelBuilderExtensions.AddGraphiti"/>), with the record type each holds.
    /// For a data map, a backup or retention policy, or a test that lists the schema. Needs no connection.
    /// </summary>
    /// <param name="options">The same settings as the driver's (only <see cref="SqlServerGraphOptions.Schema"/> and
    /// <see cref="SqlServerGraphOptions.TablePrefix"/> matter); the defaults if <see langword="null"/>.</param>
    /// <returns>The tables, ordered by name.</returns>
    public static IReadOnlyList<GraphitiTable> Tables(SqlServerGraphOptions? options = null)
    {
        options ??= SqlServerGraphOptions.Default;
        options.Validate();
        using var context = new GraphitiDbContext(PlaceholderConnectionString, options);
        return ModelTables(context.GetService<IDesignTimeModel>().Model, options.Schema);
    }

    /// <summary>
    /// The graphiti-net tables of a model (a host's or <see cref="GraphitiDbContext"/>'s) in a schema: the tables
    /// <c>BuildIndicesAndConstraintsAsync</c> creates from it.
    /// </summary>
    internal static IReadOnlyList<GraphitiTable> ModelTables(IModel model, string schema) =>
    [
        .. model.GetEntityTypes()
            .Where(e => e.ClrType.Assembly == typeof(GraphRecord).Assembly && !e.ClrType.IsAbstract
                && e.GetSchema() == schema && e.GetTableName() is not null)
            .Select(e => new GraphitiTable(schema, e.GetTableName()!, e.ClrType))
            .DistinctBy(t => t.Name)
            .OrderBy(t => t.Name, StringComparer.Ordinal),
    ];
}
