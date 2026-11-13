// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.Tests.Conformance;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>
/// The shared driver conformance suite against one PostgreSQL schema shared by every test, at the default embedding
/// size (1024), as a production database would be used.
/// </summary>
public sealed class PostgresSharedConformanceTests : GraphDriverConformanceTests
{
    private static readonly Lazy<Task<PostgresDriver>> Shared = new(async () =>
    {
        var driver = new PostgresDriver(
            TestDatabase.ConnectionString,
            new PostgresGraphOptions { Schema = "conformance_shared" });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync(deleteExisting: true);
        return driver;
    });

    /// <inheritdoc />
    protected override bool IsIsolated => false;

    /// <inheritdoc />
    protected override int EmbeddingDimension => PostgresGraphOptions.DefaultEmbeddingDimension;

    /// <inheritdoc />
    protected override string? SkipReason => TestDatabase.SkipReason;

    /// <inheritdoc />
    public override Task InitializeAsync() =>
        TestDatabase.SkipReason is null ? base.InitializeAsync() : Task.CompletedTask;

    /// <inheritdoc />
    protected override async Task<IGraphDriver> CreateDriverAsync() => await Shared.Value;
}

/// <summary>
/// The conformance suite with a fresh schema per test (so whole-graph tests run too), with approximate HNSW vector
/// search and a small vector column.
/// </summary>
public sealed class PostgresIsolatedConformanceTests : GraphDriverConformanceTests
{
    private string? _schema;

    /// <inheritdoc />
    protected override int EmbeddingDimension => 4;

    /// <inheritdoc />
    protected override string? SkipReason => TestDatabase.SkipReason;

    /// <inheritdoc />
    public override Task InitializeAsync() =>
        TestDatabase.SkipReason is null ? base.InitializeAsync() : Task.CompletedTask;

    /// <inheritdoc />
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_schema is not null)
        {
            await TestDatabase.DropSchemaAsync(_schema);
        }
    }

    /// <inheritdoc />
    protected override async Task<IGraphDriver> CreateDriverAsync()
    {
        _schema = TestDatabase.NewSchema();
        var driver = new PostgresDriver(
            TestDatabase.ConnectionString,
            new PostgresGraphOptions { Schema = _schema, EmbeddingDimension = 4, ApproximateVectorSearch = true });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        return driver;
    }
}
