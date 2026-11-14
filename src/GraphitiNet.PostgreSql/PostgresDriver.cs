// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql.Internal;
using GraphitiNet.Driver.PostgreSql.Operations;
using GraphitiNet.Models;
using Microsoft.EntityFrameworkCore;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>
/// The PostgreSQL graph driver: EF Core with pgvector for vector search and built-in text search for keyword search.
/// </summary>
/// <remarks>
/// <para>Two ways to host the tables:</para>
/// <list type="bullet">
/// <item><b>Standalone:</b> <c>new PostgresDriver("Host=...;Database=...;Username=...;Password=...")</c> uses its own
/// <see cref="GraphitiDbContext"/>. Call <c>await driver.GraphOps.BuildIndicesAndConstraintsAsync()</c> once to create
/// the tables, or apply EF Core migrations.</item>
/// <item><b>Your own DbContext:</b> call <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/> in its
/// <c>OnModelCreating</c>, then pass a factory: <c>new PostgresDriver(() =&gt; factory.CreateDbContext(), options)</c>.
/// The driver creates one short-lived context per operation and disposes it.</item>
/// </list>
/// <para>Reads never track entities. Writes are batched upserts (<c>INSERT ... ON CONFLICT (uuid) DO UPDATE</c>).
/// Edges are stored by node id without foreign keys, as in a graph database; deleting a node deletes every edge
/// that touches it.</para>
/// </remarks>
public sealed class PostgresDriver : IGraphDriver
{
    private readonly DriverCore _core;
    private readonly EpisodeNodeOperations _episodes;
    private readonly EntityNodeOperations _entities;
    private readonly EpisodicEdgeOperations _episodicEdges;
    private readonly EntityEdgeOperations _entityEdges;

    /// <summary>Creates a driver that keeps the graph in its own <see cref="GraphitiDbContext"/>.</summary>
    /// <param name="connectionString">An Npgsql connection string, for example
    /// <c>Host=localhost;Database=graphiti;Username=postgres;Password=...</c>. Connections are pooled by Npgsql.</param>
    /// <param name="options">Schema, embedding dimension and search settings; the defaults if
    /// <see langword="null"/>.</param>
    public PostgresDriver(string connectionString, PostgresGraphOptions? options = null)
        : this(CreateStandaloneFactory(connectionString, options ??= PostgresGraphOptions.Default), options)
    {
    }

    /// <summary>
    /// Creates a driver over your own <see cref="DbContext"/>, whose model includes the graph tables through
    /// <see cref="GraphitiModelBuilderExtensions.AddGraphiti"/> and which is configured with
    /// <c>UseNpgsql(..., o =&gt; o.UseVector())</c>.
    /// </summary>
    /// <param name="contextFactory">Creates a new context; the driver disposes each one after use. For example
    /// <c>() =&gt; dbContextFactory.CreateDbContext()</c>.</param>
    /// <param name="options">The same settings passed to <c>AddGraphiti</c>; the defaults if
    /// <see langword="null"/>.</param>
    public PostgresDriver(Func<DbContext> contextFactory, PostgresGraphOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        options ??= PostgresGraphOptions.Default;
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
    public PostgresGraphOptions Options => _core.Options;

    /// <inheritdoc />
    public string Provider => "postgresql";

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
    /// <c>vector</c> extension, the schema, and any missing tables with their indexes, from the EF Core model; with
    /// <c>deleteExisting</c> it drops the tables first (all data is lost). <see cref="GraphitiSchema.Tables"/> lists
    /// the tables. It throws <see cref="VectorStoreUnavailableException"/> when pgvector is not installed on the
    /// server, or the extension does not exist in the database and the role may not create it.</remarks>
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
            async connection =>
            {
                await _episodes.UpsertAsync(connection, episodicNodes, cancellationToken).ConfigureAwait(false);
                await _entities.UpsertAsync(connection, entityNodes, cancellationToken).ConfigureAwait(false);
                await _episodicEdges.UpsertAsync(connection, episodicEdges, cancellationToken).ConfigureAwait(false);
                await _entityEdges.UpsertAsync(connection, entityEdges, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Checks, without creating anything, whether the database can hold the embedding vectors: the pgvector extension
    /// (<c>vector</c>) already exists in it, or is installed on the server and the connecting role may create it (a
    /// superuser). Run it before <see cref="IGraphMaintenanceOperations.BuildIndicesAndConstraintsAsync"/>, which
    /// otherwise throws <see cref="VectorStoreUnavailableException"/> with what is missing and how to fix it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when the database can hold vectors.</returns>
    public Task<bool> CanHoldVectorsAsync(CancellationToken cancellationToken = default) =>
        _core.WithConnectionAsync(
            connection => VectorSupport.CanHoldVectorsAsync(connection, cancellationToken),
            cancellationToken);

    /// <summary>Nothing to release: connections return to Npgsql's pool after each operation.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Func<DbContext> CreateStandaloneFactory(string connectionString, PostgresGraphOptions options)
    {
        var dbOptions = GraphitiDbContext.CreateOptions(connectionString, options);
        return () => new GraphitiDbContext(dbOptions);
    }
}
