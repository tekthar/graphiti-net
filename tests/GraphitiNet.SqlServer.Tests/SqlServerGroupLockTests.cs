// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Acceptance.Tests;
using GraphitiNet.Driver.Tests.Conformance;
using Microsoft.Data.SqlClient;

namespace GraphitiNet.Driver.SqlServer.Tests;

/// <summary><see cref="SqlServerGroupLock"/> across separate lock objects and two <see cref="Graphiti"/>
/// instances.</summary>
public sealed class SqlServerGroupLockTests : DistributedGroupLockTests
{
    /// <inheritdoc />
    protected override string? SkipReason => TestDatabase.SkipReason;

    /// <inheritdoc />
    protected override IGroupLock CreateLock(string applicationName, string schema) =>
        new SqlServerGroupLock(
            new SqlConnectionStringBuilder(TestDatabase.ConnectionString) { ApplicationName = applicationName }
                .ConnectionString,
            new SqlServerGraphOptions { Schema = schema });

    /// <inheritdoc />
    protected override IGraphDriver CreateDriver(string schema) =>
        new SqlServerDriver(
            TestDatabase.ConnectionString,
            new SqlServerGraphOptions { Schema = schema, EmbeddingDimension = WordHashEmbedder.Dimension });

    /// <inheritdoc />
    protected override Task DropSchemaAsync(string schema) => TestDatabase.DropSchemaAsync(schema);

    /// <inheritdoc />
    protected override async Task<int> CountSessionsAsync(string applicationName) =>
        await TestDatabase.ScalarAsync<int>(
            "SELECT count(*) FROM sys.dm_exec_sessions WHERE program_name = @app",
            ("@app", applicationName));

    /// <inheritdoc />
    protected override async Task KillSessionsAsync(string applicationName)
    {
        var sessions = await TestDatabase.ListAsync(
            "SELECT CAST(session_id AS nvarchar(10)) FROM sys.dm_exec_sessions WHERE program_name = @app",
            ("@app", applicationName));
        Assert.NotEmpty(sessions);
        foreach (var session in sessions)
        {
            await TestDatabase.ExecuteAsync($"KILL {int.Parse(session, System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    [Fact]
    public void The_resource_is_the_documented_hash_and_stable()
    {
        // Pinned, and the same key PostgresGroupLock uses: servers on different versions must agree on it.
        var defaults = new SqlServerGroupLock("Server=unused");
        Assert.Equal("graphiti-net:d392ee90b077659c", defaults.ResourceFor("tenant-1"));

        var prefixed = new SqlServerGroupLock("Server=unused", new SqlServerGraphOptions { Schema = "app", TablePrefix = "g_" });
        Assert.NotEqual(defaults.ResourceFor("tenant-1"), prefixed.ResourceFor("tenant-1"));
        Assert.NotEqual(defaults.ResourceFor("tenant-1"), defaults.ResourceFor("tenant-2"));
        Assert.True(defaults.ResourceFor(new string('x', 1000)).Length <= 32);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public void A_negative_sp_getapplock_result_means_not_granted(int result)
    {
        var e = Assert.Throws<InvalidOperationException>(() => SqlServerGroupLock.ThrowIfNotGranted(result, "g"));
        Assert.Contains($"returned {result}", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_zero_or_positive_result_means_granted(int result) =>
        SqlServerGroupLock.ThrowIfNotGranted(result, "g");

    [SqlServerFact]
    public async Task The_lock_is_an_exclusive_session_application_lock()
    {
        var groupLock = new SqlServerGroupLock(TestDatabase.ConnectionString, new SqlServerGraphOptions { Schema = Schema });
        var resource = groupLock.ResourceFor("tenant-1");
        await using (await groupLock.AcquireAsync("tenant-1"))
        {
            // Another session could not take it now.
            var free = await TestDatabase.ScalarAsync<int>(
                "SELECT APPLOCK_TEST('public', @resource, 'Exclusive', 'Session')",
                ("@resource", resource));
            Assert.Equal(0, free);
        }

        Assert.Equal(1, await TestDatabase.ScalarAsync<int>(
            "SELECT APPLOCK_TEST('public', @resource, 'Exclusive', 'Session')",
            ("@resource", resource)));
    }
}
