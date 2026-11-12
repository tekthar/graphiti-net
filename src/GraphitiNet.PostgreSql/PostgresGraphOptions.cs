// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.RegularExpressions;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>
/// Settings for the PostgreSQL graph schema. Pass the same settings to
/// <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/>, <see cref="GraphitiDbContext"/> and
/// <see cref="PostgresDriver"/>: they describe one schema.
/// </summary>
public sealed partial record PostgresGraphOptions
{
    /// <summary>Upstream's <c>EMBEDDING_DIM</c>, the default vector size.</summary>
    public const int DefaultEmbeddingDimension = 1024;

    /// <summary>The default database schema for graphiti-net's tables.</summary>
    public const string DefaultSchema = "graphiti";

    /// <summary>
    /// The default text search configuration, <c>simple</c>: lower-casing with no stemming and no stop words, the
    /// closest built-in match to the analyzer upstream's Neo4j full-text indexes use.
    /// </summary>
    public const string SimpleTextSearchConfig = "simple";

    /// <summary>
    /// The database schema that holds the tables. Default <c>graphiti</c>; a host that keeps the tables in its own
    /// schema (for example <c>public</c> or its module schema) sets it here. Must be a plain identifier (letters,
    /// digits and underscores, not starting with a digit).
    /// </summary>
    public string Schema { get; init; } = DefaultSchema;

    /// <summary>
    /// A prefix for every table and index name, for example <c>Graphiti</c> to get <c>Graphitientities</c>, or
    /// <c>app_graph_</c>. Default none. Lets a host keep the tables beside its own in one schema. Letters, digits and
    /// underscores only.
    /// </summary>
    public string TablePrefix { get; init; } = "";

    /// <summary>
    /// The size of the embedding vectors, part of the schema (<c>vector(N)</c>). Default 1024, upstream's
    /// <c>EMBEDDING_DIM</c>. OpenAI <c>text-embedding-3-small</c> users typically set 1536. pgvector's HNSW index
    /// supports up to 2,000 dimensions.
    /// </summary>
    public int EmbeddingDimension { get; init; } = DefaultEmbeddingDimension;

    /// <summary>
    /// The PostgreSQL text search configuration (<c>regconfig</c>) for groups with no entry in
    /// <see cref="TextSearchConfigs"/> or <see cref="TextSearchConfigSelector"/>. Default <c>simple</c>, the closest
    /// to upstream's Lucene analyzer (no stemming). Language configurations such as <c>english</c>, <c>german</c> or
    /// <c>arabic</c> add stemming and stop words.
    /// </summary>
    public string DefaultTextSearchConfig { get; init; } = SimpleTextSearchConfig;

    /// <summary>Text search configurations per group id, for example <c>{ ["tenant-eg"] = "arabic" }</c>.</summary>
    public IReadOnlyDictionary<string, string>? TextSearchConfigs { get; init; }

    /// <summary>
    /// Picks the text search configuration for a group id, or returns <see langword="null"/> to fall back to
    /// <see cref="TextSearchConfigs"/> and then <see cref="DefaultTextSearchConfig"/>. It must always give the same
    /// answer for a group: rows keep the configuration they were written with.
    /// </summary>
    public Func<string, string?>? TextSearchConfigSelector { get; init; }

    /// <summary>
    /// When <see langword="true"/>, similarity searches order by pgvector's distance operator so the HNSW indexes can
    /// answer them: much faster on large graphs, but approximate, and filters (group ids, labels, dates) are applied
    /// after the index scan, so a selective filter can return fewer results than the limit. When
    /// <see langword="false"/> (the default), similarity is computed exactly over the filtered rows, as upstream does.
    /// </summary>
    public bool ApproximateVectorSearch { get; init; }

    /// <summary>How many rows a bulk save sends to the server per round trip. Default 500.</summary>
    public int BulkBatchSize { get; init; } = 500;

    /// <summary>The default settings.</summary>
    public static PostgresGraphOptions Default { get; } = new();

    /// <summary>The text search configuration for a group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns>The configuration name.</returns>
    /// <exception cref="ArgumentException">The configuration is not a valid name.</exception>
    public string ResolveTextSearchConfig(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var config = TextSearchConfigSelector?.Invoke(groupId)
            ?? (TextSearchConfigs is not null && TextSearchConfigs.TryGetValue(groupId, out var c) ? c : null)
            ?? DefaultTextSearchConfig;
        ValidateTextSearchConfig(config);
        return config;
    }

    /// <summary>Checks the settings.</summary>
    /// <exception cref="ArgumentException">A setting is out of range or not a safe identifier.</exception>
    public void Validate()
    {
        if (!IdentifierPattern().IsMatch(Schema ?? ""))
        {
            throw new ArgumentException(
                $"Schema \"{Schema}\" must contain only letters, digits and underscores and not start with a digit.",
                nameof(Schema));
        }

        if (!PrefixPattern().IsMatch(TablePrefix ?? "\0"))
        {
            throw new ArgumentException(
                $"TablePrefix \"{TablePrefix}\" must contain only letters, digits and underscores (at most 40).",
                nameof(TablePrefix));
        }

        ValidateTextSearchConfig(DefaultTextSearchConfig);
        foreach (var config in TextSearchConfigs?.Values ?? [])
        {
            ValidateTextSearchConfig(config);
        }

        if (EmbeddingDimension is < 1 or > 16000)
        {
            throw new ArgumentException("EmbeddingDimension must be between 1 and 16000.", nameof(EmbeddingDimension));
        }

        if (BulkBatchSize < 1)
        {
            throw new ArgumentException("BulkBatchSize must be at least 1.", nameof(BulkBatchSize));
        }
    }

    private static void ValidateTextSearchConfig(string? config)
    {
        if (!ConfigPattern().IsMatch(config ?? ""))
        {
            throw new ArgumentException(
                $"Text search configuration \"{config}\" must be a name such as simple, english or pg_catalog.arabic.",
                nameof(config));
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9_]{0,40}$")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("^([A-Za-z_][A-Za-z0-9_]{0,62}\\.)?[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex ConfigPattern();
}
