// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Driver;

/// <summary>Why a database cannot hold graphiti-net's embedding vectors.</summary>
public enum VectorStoreUnavailableReason
{
    /// <summary>The database server has no vector support installed (PostgreSQL without the pgvector extension).</summary>
    NotInstalled,

    /// <summary>
    /// Vector support is installed on the server but not enabled in the database, and the connecting role is not
    /// allowed to enable it (PostgreSQL: <c>CREATE EXTENSION vector</c> needs a superuser).
    /// </summary>
    NotPermitted,

    /// <summary>The database engine has no vector type (SQL Server before 2025).</summary>
    NotSupported,
}

/// <summary>
/// The database cannot hold graphiti-net's embedding vectors, so <c>BuildIndicesAndConstraintsAsync</c> cannot create
/// the schema. The message names the database, what is missing and how to fix it, and is written to be shown to the
/// database's administrator as it is. The database driver's own exception, if there was one, is the
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class VectorStoreUnavailableException : GraphitiException
{
    /// <summary>Creates the exception with a default message.</summary>
    public VectorStoreUnavailableException()
        : base("The database cannot hold embedding vectors.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public VectorStoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the database driver's exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The database driver's exception.</param>
    public VectorStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with every detail.</summary>
    /// <param name="message">The message: the database, what is missing and how to fix it.</param>
    /// <param name="provider">The driver's provider name, for example <c>postgresql</c> or <c>sqlserver</c>.</param>
    /// <param name="database">The database name.</param>
    /// <param name="reason">Why vectors cannot be stored.</param>
    /// <param name="innerException">The database driver's exception, if any.</param>
    public VectorStoreUnavailableException(
        string message,
        string provider,
        string? database,
        VectorStoreUnavailableReason reason,
        Exception? innerException = null)
        : base(message, innerException!)
    {
        Provider = provider;
        Database = database;
        Reason = reason;
    }

    /// <summary>The driver's provider name, for example <c>postgresql</c> or <c>sqlserver</c>.</summary>
    public string? Provider { get; }

    /// <summary>The database that cannot hold vectors.</summary>
    public string? Database { get; }

    /// <summary>Why vectors cannot be stored, or <see langword="null"/> when not given.</summary>
    public VectorStoreUnavailableReason? Reason { get; }
}
