// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>
/// A standalone <see cref="DbContext"/> holding only graphiti-net's tables, in their own schema. Use it when the graph
/// does not share a <c>DbContext</c> with your application; otherwise call
/// <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/> from your own context.
/// </summary>
public class GraphitiDbContext : DbContext
{
    /// <summary>Creates the context from configured options. Graph settings come from
    /// <see cref="GraphitiDbContextOptionsExtensions.UseGraphitiOptions{TBuilder}"/>, or the defaults.</summary>
    /// <param name="options">EF Core options, configured with <c>UseNpgsql(..., o => o.UseVector())</c>.</param>
    public GraphitiDbContext(DbContextOptions<GraphitiDbContext> options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        GraphOptions = options.FindExtension<GraphitiOptionsExtension>()?.GraphOptions ?? PostgresGraphOptions.Default;
    }

    /// <summary>Creates the context for a connection string.</summary>
    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="graphOptions">Graph settings, or the defaults.</param>
    public GraphitiDbContext(string connectionString, PostgresGraphOptions? graphOptions = null)
        : this(CreateOptions(connectionString, graphOptions))
    {
    }

    /// <summary>The graph settings the model was built with.</summary>
    public PostgresGraphOptions GraphOptions { get; }

    /// <summary>Episodes.</summary>
    public DbSet<EpisodeRecord> Episodes => Set<EpisodeRecord>();

    /// <summary>Entities.</summary>
    public DbSet<EntityNodeRecord> Entities => Set<EntityNodeRecord>();

    /// <summary>Communities.</summary>
    public DbSet<CommunityRecord> Communities => Set<CommunityRecord>();

    /// <summary>Sagas.</summary>
    public DbSet<SagaRecord> Sagas => Set<SagaRecord>();

    /// <summary>Episode-to-entity links.</summary>
    public DbSet<EpisodicEdgeRecord> EpisodicEdges => Set<EpisodicEdgeRecord>();

    /// <summary>Facts.</summary>
    public DbSet<EntityEdgeRecord> EntityEdges => Set<EntityEdgeRecord>();

    /// <summary>Community memberships.</summary>
    public DbSet<CommunityEdgeRecord> CommunityEdges => Set<CommunityEdgeRecord>();

    /// <summary>Saga-to-episode links.</summary>
    public DbSet<HasEpisodeEdgeRecord> HasEpisodeEdges => Set<HasEpisodeEdgeRecord>();

    /// <summary>Episode chain links.</summary>
    public DbSet<NextEpisodeEdgeRecord> NextEpisodeEdges => Set<NextEpisodeEdgeRecord>();

    /// <summary>
    /// Builds options for a connection string: Npgsql with pgvector, no change tracking by default, and the migrations
    /// history table in the graph schema.
    /// </summary>
    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="graphOptions">Graph settings, or the defaults.</param>
    /// <returns>The options.</returns>
    public static DbContextOptions<GraphitiDbContext> CreateOptions(
        string connectionString,
        PostgresGraphOptions? graphOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        graphOptions ??= PostgresGraphOptions.Default;
        graphOptions.Validate();
        return new DbContextOptionsBuilder<GraphitiDbContext>()
            .UseNpgsql(connectionString, o => o
                .UseVector()
                .MigrationsHistoryTable("__EFMigrationsHistory", graphOptions.Schema))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .UseGraphitiOptions(graphOptions)
            .Options;
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphiti(GraphOptions);
}

/// <summary>Configures <see cref="GraphitiDbContext"/>.</summary>
public static class GraphitiDbContextOptionsExtensions
{
    /// <summary>
    /// Sets the graph settings (schema, table prefix, embedding dimension) a
    /// <see cref="GraphitiDbContext"/> builds its model with. Contexts with different settings get separate models.
    /// </summary>
    /// <param name="builder">The options builder.</param>
    /// <param name="graphOptions">The settings.</param>
    /// <returns>The same builder.</returns>
    /// <typeparam name="TBuilder">The options builder type.</typeparam>
    public static TBuilder UseGraphitiOptions<TBuilder>(this TBuilder builder, PostgresGraphOptions graphOptions)
        where TBuilder : DbContextOptionsBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(graphOptions);
        graphOptions.Validate();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            new GraphitiOptionsExtension(graphOptions));
        builder.ReplaceService<IModelCacheKeyFactory, GraphitiModelCacheKeyFactory>();
        return builder;
    }
}

/// <summary>Carries <see cref="PostgresGraphOptions"/> in the EF Core options. Every setting shares one internal
/// service provider; <see cref="GraphitiModelCacheKeyFactory"/> keeps one model per setting.</summary>
internal sealed class GraphitiOptionsExtension(PostgresGraphOptions graphOptions) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public PostgresGraphOptions GraphOptions { get; } = graphOptions;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(GraphitiOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private GraphitiOptionsExtension Owner => (GraphitiOptionsExtension)Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment =>
            $"GraphitiSchema={Owner.GraphOptions.Schema} EmbeddingDimension={Owner.GraphOptions.EmbeddingDimension} ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["Graphiti:Schema"] = Owner.GraphOptions.Schema;
            debugInfo["Graphiti:EmbeddingDimension"] =
                Owner.GraphOptions.EmbeddingDimension.ToString(System.Globalization.CultureInfo.InvariantCulture);
            debugInfo["Graphiti:TablePrefix"] = Owner.GraphOptions.TablePrefix;
        }
    }
}

/// <summary>Caches one <see cref="GraphitiDbContext"/> model per schema, table prefix and embedding
/// dimension.</summary>
internal sealed class GraphitiModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is GraphitiDbContext graphiti
        ? (context.GetType(), graphiti.GraphOptions.Schema, graphiti.GraphOptions.EmbeddingDimension,
            graphiti.GraphOptions.TablePrefix, designTime)
        : (context.GetType(), designTime);
}
