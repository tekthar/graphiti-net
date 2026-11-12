// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.Tests.Conformance;

namespace GraphitiNet.Driver.SqlServer.Tests;

/// <summary>
/// The shared driver conformance suite against one SQL Server schema shared by every test, at the default embedding
/// size (1024), as a production database would be used.
/// </summary>
public sealed class SqlServerSharedConformanceTests : GraphDriverConformanceTests
{
    private static readonly Lazy<Task<SqlServerDriver>> Shared = new(async () =>
    {
        var driver = new SqlServerDriver(
            TestDatabase.ConnectionString,
            new SqlServerGraphOptions { Schema = "conformance_shared" });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync(deleteExisting: true);
        return driver;
    });

    /// <inheritdoc />
    protected override bool IsIsolated => false;

    /// <inheritdoc />
    protected override int EmbeddingDimension => SqlServerGraphOptions.DefaultEmbeddingDimension;

    /// <inheritdoc />
    protected override string? SkipReason => TestDatabase.SkipReason;

    /// <inheritdoc />
    public override Task InitializeAsync() =>
        TestDatabase.SkipReason is null ? base.InitializeAsync() : Task.CompletedTask;

    /// <inheritdoc />
    protected override async Task<IGraphDriver> CreateDriverAsync() => await Shared.Value;
}

/// <summary>
/// The conformance suite with a fresh schema per test (so whole-graph tests run too), a table prefix and a small vector
/// column.
/// </summary>
public sealed class SqlServerIsolatedConformanceTests : GraphDriverConformanceTests
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
        var driver = new SqlServerDriver(
            TestDatabase.ConnectionString,
            new SqlServerGraphOptions { Schema = _schema, TablePrefix = "g_", EmbeddingDimension = 4 });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        return driver;
    }
}
