// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Acceptance.Tests;
using GraphitiNet.Driver.Tests.Conformance;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary><see cref="PostgresGroupLock"/> across separate lock objects and two <see cref="Graphiti"/>
/// instances.</summary>
public sealed class PostgresGroupLockTests : DistributedGroupLockTests
{
    /// <inheritdoc />
    protected override string? SkipReason => TestDatabase.SkipReason;

    /// <inheritdoc />
    protected override IGroupLock CreateLock(string applicationName, string schema) =>
        new PostgresGroupLock(
            new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString) { ApplicationName = applicationName }
                .ConnectionString,
            new PostgresGraphOptions { Schema = schema });

    /// <inheritdoc />
    protected override IGraphDriver CreateDriver(string schema) =>
        new PostgresDriver(
            TestDatabase.ConnectionString,
            new PostgresGraphOptions { Schema = schema, EmbeddingDimension = WordHashEmbedder.Dimension });

    /// <inheritdoc />
    protected override Task DropSchemaAsync(string schema) => TestDatabase.DropSchemaAsync(schema);

    /// <inheritdoc />
    protected override async Task<int> CountSessionsAsync(string applicationName) =>
        (int)(long)(await ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE application_name = $1", applicationName))!;

    /// <inheritdoc />
    protected override async Task KillSessionsAsync(string applicationName) =>
        await ScalarAsync(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = $1",
            applicationName);

    private static async Task<object?> ScalarAsync(string sql, string parameter)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter);
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public void The_key_is_the_documented_hash_and_stable()
    {
        // Pinned: servers on different graphiti-net versions must agree on it.
        var defaults = new PostgresGroupLock("Host=unused");
        Assert.Equal(KeyOf("graphiti\0\0tenant-1"), defaults.KeyFor("tenant-1"));
        Assert.Equal(PinnedDefaultKey, defaults.KeyFor("tenant-1"));

        var prefixed = new PostgresGroupLock("Host=unused", new PostgresGraphOptions { Schema = "app", TablePrefix = "g_" });
        Assert.Equal(KeyOf("app\0g_\0tenant-1"), prefixed.KeyFor("tenant-1"));
        Assert.NotEqual(defaults.KeyFor("tenant-1"), prefixed.KeyFor("tenant-1"));
        Assert.NotEqual(defaults.KeyFor("tenant-1"), defaults.KeyFor("tenant-2"));
    }

    private const long PinnedDefaultKey = -3201234079903685220; // 0xd392ee90b077659c

    private static long KeyOf(string scopeAndGroup)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("graphiti-net group lock\0" + scopeAndGroup));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    [PostgresFact]
    public async Task The_lock_shows_in_pg_locks_under_its_key()
    {
        var groupLock = new PostgresGroupLock(TestDatabase.ConnectionString, new PostgresGraphOptions { Schema = Schema });
        var key = groupLock.KeyFor("tenant-1");
        await using (await groupLock.AcquireAsync("tenant-1"))
        {
            var held = await ScalarAsync(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted AND objsubid = 2 "
                + "AND ((classid::bigint << 32) | objid::bigint) = $1::text::bigint",
                key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1L, held);
        }
    }
}
