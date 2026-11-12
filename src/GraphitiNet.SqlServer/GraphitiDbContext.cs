// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.SqlServer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GraphitiNet.Driver.SqlServer;

/// <summary>
/// A standalone <see cref="DbContext"/> holding only graphiti-net's tables, in their own schema. Use it when the graph
/// does not share a <c>DbContext</c> with your application; otherwise call
/// <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/> from your own context.
/// </summary>
public class GraphitiDbContext : DbContext
{
    /// <summary>Creates the context from configured options. Graph settings come from
    /// <see cref="GraphitiDbContextOptionsExtensions.UseGraphitiOptions{TBuilder}"/>, or the defaults.</summary>
    /// <param name="options">EF Core options, configured with <c>UseSqlServer</c> or <c>UseAzureSql</c>.</param>
    public GraphitiDbContext(DbContextOptions<GraphitiDbContext> options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        GraphOptions = options.FindExtension<GraphitiOptionsExtension>()?.GraphOptions ?? SqlServerGraphOptions.Default;
    }

    /// <summary>Creates the context for a connection string.</summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <param name="graphOptions">Graph settings, or the defaults.</param>
    public GraphitiDbContext(string connectionString, SqlServerGraphOptions? graphOptions = null)
        : this(CreateOptions(connectionString, graphOptions))
    {
    }

    /// <summary>The graph settings the model was built with.</summary>
    public SqlServerGraphOptions GraphOptions { get; }

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

    /// <summary>The keyword index: one row per term of each searchable field.</summary>
    public DbSet<SearchTermRecord> SearchTerms => Set<SearchTermRecord>();

    /// <summary>The keyword index: the length of each searchable field.</summary>
    public DbSet<SearchFieldRecord> SearchFields => Set<SearchFieldRecord>();

    /// <summary>
    /// Builds options for a connection string: SQL Server, no change tracking by default, and the migrations history
    /// table in the graph schema.
    /// </summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <param name="graphOptions">Graph settings, or the defaults.</param>
    /// <returns>The options.</returns>
    public static DbContextOptions<GraphitiDbContext> CreateOptions(
        string connectionString,
        SqlServerGraphOptions? graphOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        graphOptions ??= SqlServerGraphOptions.Default;
        graphOptions.Validate();
        return new DbContextOptionsBuilder<GraphitiDbContext>()
            .UseSqlServer(connectionString, o => o
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
    public static TBuilder UseGraphitiOptions<TBuilder>(this TBuilder builder, SqlServerGraphOptions graphOptions)
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

/// <summary>Carries <see cref="SqlServerGraphOptions"/> in the EF Core options. Every setting shares one internal
/// service provider; <see cref="GraphitiModelCacheKeyFactory"/> keeps one model per setting.</summary>
internal sealed class GraphitiOptionsExtension(SqlServerGraphOptions graphOptions) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public SqlServerGraphOptions GraphOptions { get; } = graphOptions;

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
