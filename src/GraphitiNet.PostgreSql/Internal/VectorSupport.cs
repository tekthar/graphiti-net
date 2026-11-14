// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// The pgvector extension the schema needs: whether the database can hold vectors, and the
/// <see cref="VectorStoreUnavailableException"/> for a failed <c>CREATE EXTENSION</c>.
/// </summary>
internal static class VectorSupport
{
    public const string CreateExtensionSql = "CREATE EXTENSION IF NOT EXISTS vector";

    // The extension exists in the database, or it is installed on the server and this role may create it: a superuser,
    // or (for a trusted extension, or one that does not need a superuser) a role with CREATE on the database.
    private const string CanHoldVectorsSql =
        "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') OR EXISTS ("
        + "SELECT 1 FROM pg_available_extensions a JOIN pg_available_extension_versions v "
        + "ON v.name = a.name AND v.version = a.default_version WHERE a.name = 'vector' "
        + "AND (current_setting('is_superuser') = 'on' "
        + "OR ((v.trusted OR NOT v.superuser) AND has_database_privilege(current_database(), 'CREATE'))))";

    public static async Task<bool> CanHoldVectorsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(CanHoldVectorsSql, connection);
        return (bool)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The exception for a failed <c>CREATE EXTENSION vector</c>, or <see langword="null"/> when the error is not about
    /// the extension being missing or forbidden.
    /// </summary>
    public static VectorStoreUnavailableException? Unavailable(NpgsqlConnection connection, PostgresException error)
    {
        var database = connection.Database;
        var where = $"PostgreSQL database \"{database}\" on {connection.Host}";
        (VectorStoreUnavailableReason? Reason, string Message) result = error.SqlState switch
        {
            // "extension "vector" is not available" (PostgreSQL 15+), or the control file is missing (older).
            PostgresErrorCodes.FeatureNotSupported or PostgresErrorCodes.UndefinedFile => (
                VectorStoreUnavailableReason.NotInstalled,
                $"{where} cannot hold graphiti-net's embedding vectors: the pgvector extension (\"vector\") is not "
                + "installed on the PostgreSQL server. Install pgvector on the server "
                + "(https://github.com/pgvector/pgvector#installation; on a managed service, allow the \"vector\" "
                + "extension in its settings), then run BuildIndicesAndConstraintsAsync again."),
            PostgresErrorCodes.InsufficientPrivilege => (
                VectorStoreUnavailableReason.NotPermitted,
                $"{where} cannot hold graphiti-net's embedding vectors: the pgvector extension (\"vector\") is not "
                + $"created in the database, and role \"{connection.UserName}\" is not allowed to create it. Have a "
                + $"superuser run CREATE EXTENSION vector; in database \"{database}\" once, then run "
                + "BuildIndicesAndConstraintsAsync again (it leaves an existing extension alone)."),
            _ => (null, ""),
        };
        return result.Reason is { } reason
            ? new VectorStoreUnavailableException(
                $"{result.Message} PostgreSQL said: {error.MessageText}",
                "postgresql",
                database,
                reason,
                error)
            : null;
    }
}
