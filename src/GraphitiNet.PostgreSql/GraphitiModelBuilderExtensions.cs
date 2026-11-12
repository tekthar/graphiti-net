// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Linq.Expressions;
using GraphitiNet.Driver.PostgreSql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>Adds graphiti-net's tables to an EF Core model.</summary>
public static class GraphitiModelBuilderExtensions
{
    /// <summary>
    /// Adds graphiti-net's nine tables, their indexes and the <c>vector</c> extension to the model, in
    /// <see cref="PostgresGraphOptions.Schema"/> with <see cref="PostgresGraphOptions.TablePrefix"/>. Call it from your
    /// <c>DbContext.OnModelCreating</c> so the tables join your own migrations, and configure Npgsql with
    /// <c>UseVector()</c>:
    /// <code>
    /// options.UseNpgsql(connectionString, o => o.UseVector());
    /// protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    ///     modelBuilder.AddGraphiti(new PostgresGraphOptions { Schema = "app", TablePrefix = "graph_" });
    /// </code>
    /// No global query filter is added: the driver scopes every call by the group ids it is given.
    /// </summary>
    /// <param name="modelBuilder">The model to add the tables to.</param>
    /// <param name="options">Schema, table prefix and embedding dimension; the defaults if <see langword="null"/>.
    /// Pass the same settings to <see cref="PostgresDriver"/>.</param>
    /// <returns>The same model builder.</returns>
    public static ModelBuilder AddGraphiti(this ModelBuilder modelBuilder, PostgresGraphOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        options ??= PostgresGraphOptions.Default;
        options.Validate();

        var schema = options.Schema;
        var prefix = options.TablePrefix;
        var vectorType = $"vector({options.EmbeddingDimension})";

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<EpisodeRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Episodes);
            b.Property(e => e.Source).HasColumnName("source");
            b.Property(e => e.SourceDescription).HasColumnName("source_description");
            b.Property(e => e.Content).HasColumnName("content");
            b.Property(e => e.ValidAt).HasColumnName("valid_at");
            b.Property(e => e.EntityEdges).HasColumnName("entity_edges");
            b.Property(e => e.EpisodeMetadata).HasColumnName("episode_metadata").HasColumnType("jsonb");
            ConfigureSearch(b, t, e => e.SearchVector, "content", "source", "source_description");
            b.HasIndex(e => new { e.GroupId, e.ValidAt }).HasDatabaseName($"ix_{t}_group_id_valid_at");
            b.HasIndex(e => e.CreatedAt).HasDatabaseName($"ix_{t}_created_at");
        });

        modelBuilder.Entity<EntityNodeRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Entities);
            b.Property(e => e.Summary).HasColumnName("summary");
            b.Property(e => e.Attributes).HasColumnName("attributes").HasColumnType("jsonb");
            b.Property(e => e.NameEmbedding).HasColumnName("name_embedding").HasColumnType(vectorType);
            ConfigureSearch(b, t, e => e.SearchVector, "name", "summary");
            b.HasIndex(e => e.Name).HasDatabaseName($"ix_{t}_name");
            b.HasIndex(e => e.CreatedAt).HasDatabaseName($"ix_{t}_created_at");
            b.HasIndex(e => e.Labels).HasMethod("gin").HasDatabaseName($"ix_{t}_labels");
            b.HasIndex(e => e.NameEmbedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasDatabaseName($"ix_{t}_name_embedding");
        });

        modelBuilder.Entity<CommunityRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Communities);
            b.Property(e => e.Summary).HasColumnName("summary");
            b.Property(e => e.NameEmbedding).HasColumnName("name_embedding").HasColumnType(vectorType);
            ConfigureSearch(b, t, e => e.SearchVector, "name");
            b.HasIndex(e => e.NameEmbedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasDatabaseName($"ix_{t}_name_embedding");
        });

        modelBuilder.Entity<SagaRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Sagas);
            b.Property(e => e.Summary).HasColumnName("summary");
            b.Property(e => e.FirstEpisodeUuid).HasColumnName("first_episode_uuid").UseCollation("C");
            b.Property(e => e.LastEpisodeUuid).HasColumnName("last_episode_uuid").UseCollation("C");
            b.Property(e => e.LastSummarizedAt).HasColumnName("last_summarized_at");
            b.Property(e => e.LastSummarizedEpisodeValidAt).HasColumnName("last_summarized_episode_valid_at");
            b.HasIndex(e => new { e.GroupId, e.Name }).HasDatabaseName($"ix_{t}_group_id_name");
        });

        modelBuilder.Entity<EpisodicEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.EpisodicEdges));

        modelBuilder.Entity<EntityEdgeRecord>(b =>
        {
            var t = ConfigureEdge(b, schema, prefix + SqlNames.EntityEdges);
            b.Property(e => e.Name).HasColumnName("name");
            b.Property(e => e.Fact).HasColumnName("fact");
            b.Property(e => e.FactEmbedding).HasColumnName("fact_embedding").HasColumnType(vectorType);
            b.Property(e => e.Episodes).HasColumnName("episodes");
            b.Property(e => e.ExpiredAt).HasColumnName("expired_at");
            b.Property(e => e.ValidAt).HasColumnName("valid_at");
            b.Property(e => e.InvalidAt).HasColumnName("invalid_at");
            b.Property(e => e.ReferenceTime).HasColumnName("reference_time");
            b.Property(e => e.Attributes).HasColumnName("attributes").HasColumnType("jsonb");
            ConfigureSearch(b, t, e => e.SearchVector, "name", "fact");
            b.HasIndex(e => e.Name).HasDatabaseName($"ix_{t}_name");
            b.HasIndex(e => e.CreatedAt).HasDatabaseName($"ix_{t}_created_at");
            b.HasIndex(e => e.ValidAt).HasDatabaseName($"ix_{t}_valid_at");
            b.HasIndex(e => e.InvalidAt).HasDatabaseName($"ix_{t}_invalid_at");
            b.HasIndex(e => e.ExpiredAt).HasDatabaseName($"ix_{t}_expired_at");
            b.HasIndex(e => e.FactEmbedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasDatabaseName($"ix_{t}_fact_embedding");
        });

        modelBuilder.Entity<CommunityEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.CommunityEdges));
        modelBuilder.Entity<HasEpisodeEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.HasEpisodeEdges));
        modelBuilder.Entity<NextEpisodeEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.NextEpisodeEdges));

        return modelBuilder;
    }

    /// <summary>
    /// A <c>regconfig</c> column holding the row's text search configuration, and a stored generated
    /// <c>tsvector</c> built with it (so each group can use its own language), with a GIN index.
    /// </summary>
    private static void ConfigureSearch<T>(
        EntityTypeBuilder<T> b,
        string table,
        Expression<Func<T, object?>> vector,
        params string[] columns)
        where T : class
    {
        // A shadow property: EF Core maps regconfig to an object id (uint); only the driver's SQL reads or writes it.
        b.Property<uint>("TextSearchConfig")
            .HasColumnName("text_search_config")
            .HasColumnType("regconfig")
            .HasDefaultValueSql($"'{PostgresGraphOptions.SimpleTextSearchConfig}'::regconfig");
        var text = string.Join(" || ' ' || ", columns.Select(c => $"coalesce({c}, '')"));
        b.Property(vector)
            .HasColumnName("search_vector")
            .HasColumnType("tsvector")
            .HasComputedColumnSql($"to_tsvector(text_search_config, {text})", stored: true);
        b.HasIndex(vector).HasMethod("gin").HasDatabaseName($"ix_{table}_search_vector");
    }

    private static string ConfigureRecord<T>(EntityTypeBuilder<T> b, string schema, string table)
        where T : GraphRecord
    {
        b.ToTable(table, schema);
        b.HasKey(e => e.Uuid).HasName($"pk_{table}");
        // Byte-order collation so uuid ordering and cursors are ordinal, as the driver contract requires.
        b.Property(e => e.Uuid).HasColumnName("uuid").UseCollation("C");
        b.Property(e => e.GroupId).HasColumnName("group_id");
        b.Property(e => e.CreatedAt).HasColumnName("created_at");
        return table;
    }

    private static string ConfigureNode<T>(EntityTypeBuilder<T> b, string schema, string table)
        where T : NodeRecord
    {
        ConfigureRecord(b, schema, table);
        b.Property(e => e.Name).HasColumnName("name");
        b.Property(e => e.Labels).HasColumnName("labels");
        b.HasIndex(e => e.GroupId).HasDatabaseName($"ix_{table}_group_id");
        return table;
    }

    private static string ConfigureEdge<T>(EntityTypeBuilder<T> b, string schema, string table)
        where T : EdgeRecord
    {
        ConfigureRecord(b, schema, table);
        b.Property(e => e.SourceNodeUuid).HasColumnName("source_node_uuid").UseCollation("C");
        b.Property(e => e.TargetNodeUuid).HasColumnName("target_node_uuid").UseCollation("C");
        b.HasIndex(e => e.GroupId).HasDatabaseName($"ix_{table}_group_id");
        b.HasIndex(e => new { e.SourceNodeUuid, e.TargetNodeUuid }).HasDatabaseName($"ix_{table}_source_target");
        b.HasIndex(e => e.TargetNodeUuid).HasDatabaseName($"ix_{table}_target");
        return table;
    }
}
