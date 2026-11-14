// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.SqlServer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GraphitiNet.Driver.SqlServer;

/// <summary>Adds graphiti-net's tables to an EF Core model.</summary>
public static class GraphitiModelBuilderExtensions
{
    /// <summary>Byte length of an id or group column: <c>nvarchar(255)</c>, so composite index keys stay within SQL
    /// Server's limits.</summary>
    internal const int IdLength = 255;

    /// <summary>
    /// Adds graphiti-net's eleven tables (nine graph tables and the two keyword index tables) and their indexes to the
    /// model, in <see cref="SqlServerGraphOptions.Schema"/> with <see cref="SqlServerGraphOptions.TablePrefix"/>. Call
    /// it from your <c>DbContext.OnModelCreating</c> so the tables join your own migrations, and configure the
    /// context with <c>UseSqlServer</c> or <c>UseAzureSql</c>:
    /// <code>
    /// options.UseSqlServer(connectionString);
    /// protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    ///     modelBuilder.AddGraphiti(new SqlServerGraphOptions { Schema = "app", TablePrefix = "graph_" });
    /// </code>
    /// No global query filter is added: the driver scopes every call by the group ids it is given.
    /// </summary>
    /// <param name="modelBuilder">The model to add the tables to.</param>
    /// <param name="options">Schema, table prefix and embedding dimension; the defaults if <see langword="null"/>.
    /// Pass the same settings to <see cref="SqlServerDriver"/>.</param>
    /// <returns>The same model builder.</returns>
    public static ModelBuilder AddGraphiti(this ModelBuilder modelBuilder, SqlServerGraphOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        options ??= SqlServerGraphOptions.Default;
        options.Validate();

        var schema = options.Schema;
        var prefix = options.TablePrefix;
        var vectorType = $"vector({options.EmbeddingDimension})";

        modelBuilder.Entity<EpisodeRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Episodes);
            b.Property(e => e.Source).HasColumnName("source").HasMaxLength(50);
            b.Property(e => e.SourceDescription).HasColumnName("source_description");
            b.Property(e => e.Content).HasColumnName("content");
            b.Property(e => e.ValidAt).HasColumnName("valid_at");
            b.Property(e => e.EntityEdges).HasColumnName("entity_edges");
            b.Property(e => e.EpisodeMetadata).HasColumnName("episode_metadata");
            b.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(IdLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.HasIndex(e => new { e.GroupId, e.ValidAt }).HasDatabaseName($"ix_{t}_group_id_valid_at");
            // One episode per (group, key); filtered, so episodes without a key never collide.
            b.HasIndex(e => new { e.GroupId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("[idempotency_key] IS NOT NULL")
                .HasDatabaseName($"ux_{t}_group_id_idempotency_key");
            b.HasIndex(e => e.CreatedAt).HasDatabaseName($"ix_{t}_created_at");
        });

        modelBuilder.Entity<EntityNodeRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Entities);
            b.Property(e => e.Summary).HasColumnName("summary");
            b.Property(e => e.Attributes).HasColumnName("attributes");
            b.Property(e => e.NameEmbedding).HasColumnName("name_embedding").HasColumnType(vectorType);
            // Exact-name lookups (GetByNamesAsync) are always scoped by group; names are nvarchar(max), so the index
            // holds their hash.
            ConfigureNameKey(b);
            b.HasIndex("GroupId", "NameKey").HasDatabaseName($"ix_{t}_group_id_name_key");
            b.HasIndex(e => e.CreatedAt).HasDatabaseName($"ix_{t}_created_at");
        });

        modelBuilder.Entity<CommunityRecord>(b =>
        {
            ConfigureNode(b, schema, prefix + SqlNames.Communities);
            b.Property(e => e.Summary).HasColumnName("summary");
            b.Property(e => e.NameEmbedding).HasColumnName("name_embedding").HasColumnType(vectorType);
        });

        modelBuilder.Entity<SagaRecord>(b =>
        {
            var t = ConfigureNode(b, schema, prefix + SqlNames.Sagas);
            b.Property(e => e.Summary).HasColumnName("summary");
            b.Property(e => e.FirstEpisodeUuid).HasColumnName("first_episode_uuid").HasMaxLength(IdLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.LastEpisodeUuid).HasColumnName("last_episode_uuid").HasMaxLength(IdLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.LastSummarizedAt).HasColumnName("last_summarized_at");
            b.Property(e => e.LastSummarizedEpisodeValidAt).HasColumnName("last_summarized_episode_valid_at");
            ConfigureNameKey(b);
            b.HasIndex("GroupId", "NameKey").HasDatabaseName($"ix_{t}_group_id_name_key");
        });

        modelBuilder.Entity<EpisodicEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.EpisodicEdges));

        modelBuilder.Entity<EntityEdgeRecord>(b =>
        {
            var t = ConfigureEdge(b, schema, prefix + SqlNames.EntityEdges);
            b.Property(e => e.Name).HasColumnName("name").UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.Fact).HasColumnName("fact");
            b.Property(e => e.FactEmbedding).HasColumnName("fact_embedding").HasColumnType(vectorType);
            b.Property(e => e.Episodes).HasColumnName("episodes");
            b.Property(e => e.ExpiredAt).HasColumnName("expired_at");
            b.Property(e => e.ValidAt).HasColumnName("valid_at");
            b.Property(e => e.InvalidAt).HasColumnName("invalid_at");
            b.Property(e => e.ReferenceTime).HasColumnName("reference_time");
            b.Property(e => e.Attributes).HasColumnName("attributes");
            b.HasIndex(e => e.CreatedAt).HasDatabaseName($"ix_{t}_created_at");
            b.HasIndex(e => e.ValidAt).HasDatabaseName($"ix_{t}_valid_at");
            b.HasIndex(e => e.InvalidAt).HasDatabaseName($"ix_{t}_invalid_at");
            b.HasIndex(e => e.ExpiredAt).HasDatabaseName($"ix_{t}_expired_at");
        });

        modelBuilder.Entity<CommunityEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.CommunityEdges));
        modelBuilder.Entity<HasEpisodeEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.HasEpisodeEdges));
        modelBuilder.Entity<NextEpisodeEdgeRecord>(b => ConfigureEdge(b, schema, prefix + SqlNames.NextEpisodeEdges));

        modelBuilder.Entity<SearchTermRecord>(b =>
        {
            var t = prefix + SqlNames.SearchTerms;
            b.ToTable(t, schema);
            // Deletes and rewrites go by record; lookups by term go through the clustered index.
            b.HasKey(e => new { e.DocKind, e.Uuid, e.Field, e.Term }).HasName($"pk_{t}").IsClustered(false);
            b.Property(e => e.DocKind).HasColumnName("doc_kind");
            b.Property(e => e.Uuid).HasColumnName("uuid").HasMaxLength(IdLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.Field).HasColumnName("field");
            b.Property(e => e.Term).HasColumnName("term").HasMaxLength(Internal.Tokenizer.MaxTermLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.GroupId).HasColumnName("group_id").HasMaxLength(IdLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.Frequency).HasColumnName("frequency");
            b.Property(e => e.FieldLength).HasColumnName("field_length");
            b.HasIndex(e => new { e.DocKind, e.Term }).IsClustered().HasDatabaseName($"ix_{t}_term");
        });

        modelBuilder.Entity<SearchFieldRecord>(b =>
        {
            var t = prefix + SqlNames.SearchFields;
            b.ToTable(t, schema);
            b.HasKey(e => new { e.DocKind, e.Uuid, e.Field }).HasName($"pk_{t}");
            b.Property(e => e.DocKind).HasColumnName("doc_kind");
            b.Property(e => e.Uuid).HasColumnName("uuid").HasMaxLength(IdLength)
                .UseCollation(SqlServerGraphOptions.BinaryCollation);
            b.Property(e => e.Field).HasColumnName("field");
            b.Property(e => e.FieldLength).HasColumnName("field_length");
        });

        return modelBuilder;
    }

    /// <summary>A persisted SHA-256 of the name, indexed with the group for exact-name lookups.</summary>
    private static void ConfigureNameKey<T>(EntityTypeBuilder<T> b)
        where T : NodeRecord =>
        b.Property<byte[]>("NameKey")
            .HasColumnName("name_key")
            .HasColumnType("binary(32)")
            .HasComputedColumnSql("CAST(HASHBYTES('SHA2_256', [name]) AS binary(32))", stored: true);

    private static string ConfigureRecord<T>(EntityTypeBuilder<T> b, string schema, string table)
        where T : GraphRecord
    {
        b.ToTable(table, schema);
        b.HasKey(e => e.Uuid).HasName($"pk_{table}");
        // A binary collation so uuid ordering and cursors are ordinal, as the driver contract requires.
        b.Property(e => e.Uuid).HasColumnName("uuid").HasMaxLength(IdLength)
            .UseCollation(SqlServerGraphOptions.BinaryCollation);
        b.Property(e => e.GroupId).HasColumnName("group_id").HasMaxLength(IdLength)
            .UseCollation(SqlServerGraphOptions.BinaryCollation);
        b.Property(e => e.CreatedAt).HasColumnName("created_at");
        return table;
    }

    private static string ConfigureNode<T>(EntityTypeBuilder<T> b, string schema, string table)
        where T : NodeRecord
    {
        ConfigureRecord(b, schema, table);
        b.Property(e => e.Name).HasColumnName("name").UseCollation(SqlServerGraphOptions.BinaryCollation);
        b.Property(e => e.Labels).HasColumnName("labels");
        b.HasIndex(e => e.GroupId).HasDatabaseName($"ix_{table}_group_id");
        return table;
    }

    private static string ConfigureEdge<T>(EntityTypeBuilder<T> b, string schema, string table)
        where T : EdgeRecord
    {
        ConfigureRecord(b, schema, table);
        b.Property(e => e.SourceNodeUuid).HasColumnName("source_node_uuid").HasMaxLength(IdLength)
            .UseCollation(SqlServerGraphOptions.BinaryCollation);
        b.Property(e => e.TargetNodeUuid).HasColumnName("target_node_uuid").HasMaxLength(IdLength)
            .UseCollation(SqlServerGraphOptions.BinaryCollation);
        b.HasIndex(e => e.GroupId).HasDatabaseName($"ix_{table}_group_id");
        b.HasIndex(e => new { e.SourceNodeUuid, e.TargetNodeUuid }).HasDatabaseName($"ix_{table}_source_target");
        b.HasIndex(e => e.TargetNodeUuid).HasDatabaseName($"ix_{table}_target");
        return table;
    }
}
