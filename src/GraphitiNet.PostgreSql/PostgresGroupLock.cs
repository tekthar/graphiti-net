// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql;

/// <summary>
/// An <see cref="IGroupLock"/> that holds across processes: a PostgreSQL session-level advisory lock
/// (<c>pg_advisory_lock</c>) per group, so several app servers writing to one database take turns per group.
/// </summary>
/// <remarks>
/// <para>Pass it to every <see cref="Graphiti"/> instance that writes to the graph, on every server:
/// <c>new Graphiti(driver, llm, embedder, reranker, groupLock: new PostgresGroupLock(connectionString, options))</c>.
/// Use the same <see cref="PostgresGraphOptions"/> as the driver.</para>
/// <para><b>Connection.</b> Each held group uses one connection of its own, opened with pooling off, so the lock is
/// released exactly when the handle is disposed (and not left behind on a pooled connection, or dropped by a pool's
/// session reset). Waiters in one process for one group first queue in process, so they use no connection while they
/// wait; only the next in line waits in PostgreSQL. The session sets <c>statement_timeout</c>, <c>lock_timeout</c>
/// and, on PostgreSQL 14 or later, <c>idle_session_timeout</c> to 0, so a role default cannot end the wait or the
/// held lock early; the command timeout is infinite.</para>
/// <para><b>Key.</b> The lock is the two-integer advisory lock <c>pg_advisory_lock(key1, key2)</c> (in
/// <c>pg_locks</c>: <c>classid = key1</c>, <c>objid = key2</c>, <c>objsubid = 2</c>), a key space apart from the
/// single-<c>bigint</c> advisory locks graphiti-net takes while building the schema. The 64-bit key is the first 8
/// bytes, big-endian, of the SHA-256 of the UTF-8 text <c>graphiti-net group lock\0{Schema}\0{TablePrefix}\0{groupId}</c>;
/// <c>key1</c> is its upper 32 bits and <c>key2</c> its lower 32 bits, as signed integers. The schema and prefix are
/// part of it, so two graphs in one database do not block each other. Advisory locks belong to one database. The key
/// is stable across versions, so servers running different graphiti-net versions still exclude each other; see
/// <see cref="KeyFor"/>. Two group ids with the same key (about one chance in 2^64) would only take turns.</para>
/// <para><b>Cancellation.</b> Cancelling the token while waiting cancels the waiting statement and closes the
/// connection; no lock is left held, also when the lock was granted just as the cancellation arrived.</para>
/// <para><b>Lost connection.</b> If the holder's connection drops (its server dies, the backend is terminated, the
/// network fails), PostgreSQL releases the lock and the next server goes ahead; the holder is not told, and keeps
/// writing until it finishes. Disposing the handle afterwards does not throw. Turn on TCP keepalives
/// (<c>tcp_keepalives_idle</c> on the server, or <c>Keepalive</c> in the connection string) to notice a dead client
/// sooner.</para>
/// <para><b>Not re-entrant.</b> Like <see cref="InProcessGroupLock"/>, asking for a group the same flow already
/// holds waits for itself: forever unless the token is cancelled. Two independent operations in one process for one
/// group simply take turns. <see cref="Graphiti"/> takes several groups in ordinal order, so servers cannot deadlock
/// on each other's groups.</para>
/// </remarks>
public sealed class PostgresGroupLock : IGroupLock
{
    private const string KeyDomain = "graphiti-net group lock";

    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _tablePrefix;
    private readonly InProcessGroupLock _local = new();

    /// <summary>Creates the lock.</summary>
    /// <param name="connectionString">An Npgsql connection string for the graph's database, usually the driver's.
    /// Pooling is turned off for the lock's own connections whatever it says.</param>
    /// <param name="options">The graph's settings: their schema and table prefix scope the lock keys. The defaults
    /// if <see langword="null"/>.</param>
    public PostgresGroupLock(string connectionString, PostgresGraphOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options ??= PostgresGraphOptions.Default;
        options.Validate();
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
        }.ConnectionString;
        _schema = options.Schema;
        _tablePrefix = options.TablePrefix;
    }

    /// <summary>
    /// The 64-bit advisory lock key of a group: the first 8 bytes, big-endian, of the SHA-256 of
    /// <c>graphiti-net group lock\0{Schema}\0{TablePrefix}\0{groupId}</c> in UTF-8. The lock is taken as
    /// <c>pg_advisory_lock((key &gt;&gt; 32)::int, key::int)</c>.
    /// </summary>
    /// <param name="groupId">The group.</param>
    /// <returns>The key.</returns>
    public long KeyFor(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var text = Encoding.UTF8.GetBytes($"{KeyDomain}\0{_schema}\0{_tablePrefix}\0{groupId}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(text, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    /// <inheritdoc/>
    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var key = KeyFor(groupId);
        var local = await _local.AcquireAsync(groupId, cancellationToken).ConfigureAwait(false);
        NpgsqlConnection? connection = null;
        try
        {
            connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var settings = "SET statement_timeout = 0; SET lock_timeout = 0;"
                           + (connection.PostgreSqlVersion.Major >= 14 ? " SET idle_session_timeout = 0;" : "");
            await using (var set = new NpgsqlCommand(settings, connection))
            {
                await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1, $2)", connection))
            {
                take.CommandTimeout = 0;
                take.Parameters.Add(new NpgsqlParameter<int> { TypedValue = High(key) });
                take.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Low(key) });
                await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Handle(connection, key, local);
        }
        catch (Exception e)
        {
            // Closing the unpooled connection ends the session, which releases the lock if it was granted after all.
            await CloseQuietlyAsync(connection).ConfigureAwait(false);
            await local.DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested && e is not OperationCanceledException)
            {
                throw new OperationCanceledException("Waiting for the group lock was cancelled.", e, cancellationToken);
            }

            throw;
        }
    }

    private static int High(long key) => unchecked((int)(key >> 32));

    private static int Low(long key) => unchecked((int)key);

    private static async ValueTask CloseQuietlyAsync(NpgsqlConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // The session is gone either way.
        }
    }

    private sealed class Handle(NpgsqlConnection connection, long key, IAsyncDisposable local) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    // Unlock explicitly so the lock is free when this returns; closing alone would also free it.
                    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1, $2)", connection);
                    release.CommandTimeout = 15;
                    release.Parameters.Add(new NpgsqlParameter<int> { TypedValue = High(key) });
                    release.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Low(key) });
                    await release.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                // The connection dropped, so the server has already released the lock.
            }
            finally
            {
                await CloseQuietlyAsync(connection).ConfigureAwait(false);
                await local.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
