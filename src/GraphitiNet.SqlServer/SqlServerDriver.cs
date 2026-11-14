// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.SqlServer.Internal;
using GraphitiNet.Driver.SqlServer.Operations;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;

namespace GraphitiNet.Driver.SqlServer;

/// <summary>
/// The SQL Server 2025 and Azure SQL graph driver: EF Core with the native <c>vector</c> type and
/// <c>VECTOR_DISTANCE</c> for vector search, and BM25 over a driver-maintained keyword index for keyword search.
/// </summary>
/// <remarks>
/// <para>Two ways to host the tables:</para>
/// <list type="bullet">
/// <item><b>Standalone:</b> <c>new SqlServerDriver("Host=...;Database=...;Username=...;Password=...")</c> uses its own
/// <see cref="GraphitiDbContext"/>. Call <c>await driver.GraphOps.BuildIndicesAndConstraintsAsync()</c> once to create
/// the tables, or apply EF Core migrations.</item>
/// <item><b>Your own DbContext:</b> call <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/> in its
/// <c>OnModelCreating</c>, then pass a factory: <c>new SqlServerDriver(() =&gt; factory.CreateDbContext(), options)</c>.
/// The driver creates one short-lived context per operation and disposes it.</item>
/// </list>
/// <para>Reads never track entities. Writes are batched upserts (<c>MERGE</c> over a JSON batch read with
/// <c>OPENJSON</c>), each in one transaction with the keyword index rows of the records it writes.
/// Edges are stored by node id without foreign keys, as in a graph database; deleting a node deletes every edge
/// that touches it.</para>
/// </remarks>
public sealed class SqlServerDriver : IGraphDriver
{
    private readonly DriverCore _core;
    private readonly EpisodeNodeOperations _episodes;
    private readonly EntityNodeOperations _entities;
    private readonly EpisodicEdgeOperations _episodicEdges;
    private readonly EntityEdgeOperations _entityEdges;

    /// <summary>Creates a driver that keeps the graph in its own <see cref="GraphitiDbContext"/>.</summary>
    /// <param name="connectionString">A SQL Server connection string, for example
    /// <c>Server=localhost;Database=graphiti;User Id=sa;Password=...;TrustServerCertificate=True</c>. Connections are
    /// pooled by SqlClient.</param>
    /// <param name="options">Schema, embedding dimension and search settings; the defaults if
    /// <see langword="null"/>.</param>
    public SqlServerDriver(string connectionString, SqlServerGraphOptions? options = null)
        : this(CreateStandaloneFactory(connectionString, options ??= SqlServerGraphOptions.Default), options)
    {
    }

    /// <summary>
    /// Creates a driver over your own <see cref="DbContext"/>, whose model includes the graph tables through
    /// <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/> and which is configured with
    /// <c>UseSqlServer(...)</c> or <c>UseAzureSql(...)</c>.
    /// </summary>
    /// <param name="contextFactory">Creates a new context; the driver disposes each one after use. For example
    /// <c>() =&gt; dbContextFactory.CreateDbContext()</c>.</param>
    /// <param name="options">The same settings passed to <c>AddGraphiti</c>; the defaults if
    /// <see langword="null"/>.</param>
    public SqlServerDriver(Func<DbContext> contextFactory, SqlServerGraphOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        options ??= SqlServerGraphOptions.Default;
        options.Validate();
        _core = new DriverCore(contextFactory, options);
        _episodes = new EpisodeNodeOperations(_core);
        _entities = new EntityNodeOperations(_core);
        _episodicEdges = new EpisodicEdgeOperations(_core);
        _entityEdges = new EntityEdgeOperations(_core);
        CommunityNodeOps = new CommunityNodeOperations(_core);
        SagaNodeOps = new SagaNodeOperations(_core);
        CommunityEdgeOps = new CommunityEdgeOperations(_core);
        HasEpisodeEdgeOps = new HasEpisodeEdgeOperations(_core);
        NextEpisodeEdgeOps = new NextEpisodeEdgeOperations(_core);
        SearchOps = new SearchOperations(_core);
        GraphOps = new GraphMaintenanceOperations(_core);
    }

    /// <summary>The settings the driver was created with.</summary>
    public SqlServerGraphOptions Options => _core.Options;

    /// <inheritdoc />
    public string Provider => "sqlserver";

    /// <inheritdoc />
    public IEpisodeNodeOperations EpisodeNodeOps => _episodes;

    /// <inheritdoc />
    public IEntityNodeOperations EntityNodeOps => _entities;

    /// <inheritdoc />
    public ICommunityNodeOperations CommunityNodeOps { get; }

    /// <inheritdoc />
    public ISagaNodeOperations SagaNodeOps { get; }

    /// <inheritdoc />
    public IEpisodicEdgeOperations EpisodicEdgeOps => _episodicEdges;

    /// <inheritdoc />
    public IEntityEdgeOperations EntityEdgeOps => _entityEdges;

    /// <inheritdoc />
    public ICommunityEdgeOperations CommunityEdgeOps { get; }

    /// <inheritdoc />
    public IHasEpisodeEdgeOperations HasEpisodeEdgeOps { get; }

    /// <inheritdoc />
    public INextEpisodeEdgeOperations NextEpisodeEdgeOps { get; }

    /// <inheritdoc />
    public ISearchOperations SearchOps { get; }

    /// <inheritdoc />
    /// <remarks><see cref="IGraphMaintenanceOperations.BuildIndicesAndConstraintsAsync"/> creates the
    /// schema and any missing tables with their indexes, from the EF Core model; with
    /// <c>deleteExisting</c> it drops the tables first (all data is lost). <see cref="GraphitiSchema.Tables"/> lists
    /// the tables. It throws <see cref="VectorStoreUnavailableException"/> when the database has no <c>vector</c> type
    /// (SQL Server before 2025).</remarks>
    public IGraphMaintenanceOperations GraphOps { get; }

    /// <inheritdoc />
    /// <remarks>All four lists are written in one database transaction.</remarks>
    public Task AddNodesAndEdgesBulkAsync(
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodicNodes);
        ArgumentNullException.ThrowIfNull(episodicEdges);
        ArgumentNullException.ThrowIfNull(entityNodes);
        ArgumentNullException.ThrowIfNull(entityEdges);
        return _core.InTransactionAsync(
            async db =>
            {
                await _episodes.UpsertAsync(db, episodicNodes, cancellationToken).ConfigureAwait(false);
                await _entities.UpsertAsync(db, entityNodes, cancellationToken).ConfigureAwait(false);
                await _episodicEdges.UpsertAsync(db, episodicEdges, cancellationToken).ConfigureAwait(false);
                await _entityEdges.UpsertAsync(db, entityEdges, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Checks, without creating anything, whether the database can hold the embedding vectors: whether it has the
    /// <c>vector</c> type (SQL Server 2025, Azure SQL Database, Azure SQL Managed Instance with a SQL Server 2025 or
    /// Always-up-to-date update policy). Run it before
    /// <see cref="IGraphMaintenanceOperations.BuildIndicesAndConstraintsAsync"/>, which otherwise throws
    /// <see cref="VectorStoreUnavailableException"/> with what is missing and how to fix it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when the database can hold vectors.</returns>
    public Task<bool> CanHoldVectorsAsync(CancellationToken cancellationToken = default) =>
        _core.WithConnectionAsync(db => VectorSupport.HasVectorTypeAsync(db, cancellationToken), cancellationToken);

    /// <summary>Nothing to release: connections return to SqlClient's pool after each operation.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Func<DbContext> CreateStandaloneFactory(string connectionString, SqlServerGraphOptions options)
    {
        var dbOptions = GraphitiDbContext.CreateOptions(connectionString, options);
        return () => new GraphitiDbContext(dbOptions);
    }
}
