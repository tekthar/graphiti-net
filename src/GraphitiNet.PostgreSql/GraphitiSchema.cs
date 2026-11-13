// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>Describes the schema the PostgreSQL driver creates, without a database.</summary>
public static class GraphitiSchema
{
    // Builds the model only; never opened.
    private const string PlaceholderConnectionString = "Host=localhost";

    /// <summary>
    /// The tables <c>BuildIndicesAndConstraintsAsync</c> creates and the driver maintains for a schema name and table
    /// prefix: the nine graph tables of the EF Core model (<see cref="GraphitiModelBuilderExtensions.AddGraphiti"/>)
    /// with the record type each holds, and the <c>search_statistics</c> and <c>search_term_counts</c> tables BM25
    /// ranking reads (created by
    /// <see cref="GraphitiSchemaSql.SearchStatistics"/>, outside the EF Core model). For a data map, a backup or
    /// retention policy, or a test that lists the schema. Needs no connection.
    /// </summary>
    /// <param name="options">The same settings as the driver's (only <see cref="PostgresGraphOptions.Schema"/> and
    /// <see cref="PostgresGraphOptions.TablePrefix"/> matter); the defaults if <see langword="null"/>.</param>
    /// <returns>The tables, ordered by name.</returns>
    public static IReadOnlyList<GraphitiTable> Tables(PostgresGraphOptions? options = null)
    {
        options ??= PostgresGraphOptions.Default;
        options.Validate();
        using var context = new GraphitiDbContext(PlaceholderConnectionString, options);
        var model = context.GetService<IDesignTimeModel>().Model;
        return
        [
            .. ModelTables(model, options.Schema)
                .Concat(SearchStatisticsSql.Tables.Select(t => new GraphitiTable(options.Schema, options.TablePrefix + t, null)))
                .OrderBy(t => t.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The graph tables of a model (a host's or <see cref="GraphitiDbContext"/>'s) in a schema: the tables
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
