// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.RegularExpressions;

namespace GraphitiNet.Driver.SqlServer;

/// <summary>
/// Settings for the SQL Server graph schema. Pass the same settings to
/// <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/>, <see cref="GraphitiDbContext"/> and
/// <see cref="SqlServerDriver"/>: they describe one schema.
/// </summary>
public sealed partial record SqlServerGraphOptions
{
    /// <summary>Upstream's <c>EMBEDDING_DIM</c>, the default vector size.</summary>
    public const int DefaultEmbeddingDimension = 1024;

    /// <summary>The largest vector SQL Server's <c>vector</c> type holds (single-precision floats).</summary>
    public const int MaxEmbeddingDimension = 1998;

    /// <summary>The default database schema for graphiti-net's tables.</summary>
    public const string DefaultSchema = "graphiti";

    /// <summary>
    /// The binary (ordinal, case-sensitive) collation of the id, group, name and term columns, so uuid paging and
    /// exact-name lookups follow the driver contract whatever the database's default collation is.
    /// </summary>
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The database schema that holds the tables. Default <c>graphiti</c>; a host that keeps the tables in its own
    /// schema (for example <c>dbo</c>) sets it here. Must be a plain identifier (letters, digits and underscores, not
    /// starting with a digit).
    /// </summary>
    public string Schema { get; init; } = DefaultSchema;

    /// <summary>
    /// A prefix for every table and index name, for example <c>graph_</c>. Default none. Lets a host keep the tables
    /// beside its own in one schema. Letters, digits and underscores only.
    /// </summary>
    public string TablePrefix { get; init; } = "";

    /// <summary>
    /// The size of the embedding vectors, part of the schema (<c>vector(N)</c>). Default 1024, upstream's
    /// <c>EMBEDDING_DIM</c>. OpenAI <c>text-embedding-3-small</c> users typically set 1536. SQL Server's
    /// <c>vector</c> type holds at most 1,998 dimensions.
    /// </summary>
    public int EmbeddingDimension { get; init; } = DefaultEmbeddingDimension;

    /// <summary>How many rows a bulk save sends to the server per round trip. Default 500.</summary>
    public int BulkBatchSize { get; init; } = 500;

    /// <summary>The default settings.</summary>
    public static SqlServerGraphOptions Default { get; } = new();

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

        if (EmbeddingDimension is < 1 or > MaxEmbeddingDimension)
        {
            throw new ArgumentException(
                $"EmbeddingDimension must be between 1 and {MaxEmbeddingDimension}.",
                nameof(EmbeddingDimension));
        }

        if (BulkBatchSize < 1)
        {
            throw new ArgumentException("BulkBatchSize must be at least 1.", nameof(BulkBatchSize));
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9_]{0,40}$")]
    private static partial Regex PrefixPattern();
}
