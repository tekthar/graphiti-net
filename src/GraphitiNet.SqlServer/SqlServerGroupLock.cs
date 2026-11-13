// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace GraphitiNet.Driver.SqlServer;

/// <summary>
/// An <see cref="IGroupLock"/> that holds across processes: a SQL Server application lock (<c>sp_getapplock</c>,
/// exclusive, owned by the session) per group, so several app servers writing to one database take turns per group.
/// </summary>
/// <remarks>
/// <para>Pass it to every <see cref="Graphiti"/> instance that writes to the graph, on every server:
/// <c>new Graphiti(driver, llm, embedder, reranker, groupLock: new SqlServerGroupLock(connectionString, options))</c>.
/// Use the same <see cref="SqlServerGraphOptions"/> as the driver.</para>
/// <para><b>Connection.</b> Each held group uses one connection of its own, opened with pooling off, so the lock is
/// released exactly when the handle is disposed (and not left behind on a pooled connection, or dropped by the pool's
/// <c>sp_reset_connection</c>). Waiters in one process for one group first queue in process, so they use no
/// connection while they wait; only the next in line waits in SQL Server. The wait has no time limit
/// (<c>@LockTimeout = -1</c>, command timeout 0).</para>
/// <para><b>Result.</b> <c>sp_getapplock</c> reports failure in its return value, not as an error: a negative value
/// (-1 timed out, -2 cancelled, -3 chosen as deadlock victim, -999 bad call) means the lock was not granted, and
/// <see cref="AcquireAsync"/> throws <see cref="InvalidOperationException"/>.</para>
/// <para><b>Resource.</b> The lock resource is <c>graphiti-net:</c> followed by 16 lower-case hex digits: the first 8
/// bytes of the SHA-256 of the UTF-8 text <c>graphiti-net group lock\0{Schema}\0{TablePrefix}\0{groupId}</c>, the
/// same key <c>PostgresGroupLock</c> uses (see <see cref="ResourceFor"/>). It fits the 32 characters
/// <c>sys.dm_tran_locks</c> shows, and does not depend on the database collation. The schema and prefix are part of it,
/// so two graphs in one database do not block each other. Application locks belong to one database. The resource is
/// stable across versions, so servers running different graphiti-net versions still exclude each other. Two group
/// ids with the same key (about one chance in 2^64) would only take turns.</para>
/// <para><b>Cancellation.</b> Cancelling the token while waiting cancels the waiting request and closes the
/// connection; no lock is left held, also when the lock was granted just as the cancellation arrived.</para>
/// <para><b>Lost connection.</b> If the holder's connection drops (its server dies, the session is killed, the
/// network fails), SQL Server releases the lock and the next server goes ahead; the holder is not told, and keeps
/// writing until it finishes. Disposing the handle afterwards does not throw.</para>
/// <para><b>Not re-entrant.</b> Like <see cref="InProcessGroupLock"/>, asking for a group the same flow already
/// holds waits for itself: forever unless the token is cancelled. Two independent operations in one process for one
/// group simply take turns. <see cref="Graphiti"/> takes several groups in ordinal order, so servers cannot deadlock
/// on each other's groups.</para>
/// </remarks>
public sealed class SqlServerGroupLock : IGroupLock
{
    private const string KeyDomain = "graphiti-net group lock";
    private const string ResourcePrefix = "graphiti-net:";

    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _tablePrefix;
    private readonly InProcessGroupLock _local = new();

    /// <summary>Creates the lock.</summary>
    /// <param name="connectionString">A SqlClient connection string for the graph's database, usually the driver's.
    /// Pooling is turned off for the lock's own connections whatever it says.</param>
    /// <param name="options">The graph's settings: their schema and table prefix scope the lock resources. The
    /// defaults if <see langword="null"/>.</param>
    public SqlServerGroupLock(string connectionString, SqlServerGraphOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options ??= SqlServerGraphOptions.Default;
        options.Validate();
        _connectionString = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Enlist = false,
        }.ConnectionString;
        _schema = options.Schema;
        _tablePrefix = options.TablePrefix;
    }

    /// <summary>
    /// The application lock resource of a group: <c>graphiti-net:</c> and the first 8 bytes, as 16 lower-case hex
    /// digits, of the SHA-256 of <c>graphiti-net group lock\0{Schema}\0{TablePrefix}\0{groupId}</c> in UTF-8.
    /// </summary>
    /// <param name="groupId">The group.</param>
    /// <returns>The resource name passed to <c>sp_getapplock</c>.</returns>
    public string ResourceFor(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var text = Encoding.UTF8.GetBytes($"{KeyDomain}\0{_schema}\0{_tablePrefix}\0{groupId}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(text, hash);
        var key = BinaryPrimitives.ReadUInt64BigEndian(hash);
        return ResourcePrefix + key.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException"><c>sp_getapplock</c> did not grant the lock.</exception>
    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var resource = ResourceFor(groupId);
        var local = await _local.AcquireAsync(groupId, cancellationToken).ConfigureAwait(false);
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var take = new SqlCommand("sp_getapplock", connection))
            {
                take.CommandType = CommandType.StoredProcedure;
                take.CommandTimeout = 0;
                take.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
                take.Parameters.Add("@LockMode", SqlDbType.VarChar, 32).Value = "Exclusive";
                take.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = "Session";
                take.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = -1;
                var result = take.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
                result.Direction = ParameterDirection.ReturnValue;
                await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfNotGranted((int)result.Value, groupId);
            }

            return new Handle(connection, resource, local);
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

    /// <summary>Throws when <c>sp_getapplock</c>'s return value says the lock was not granted.</summary>
    internal static void ThrowIfNotGranted(int result, string groupId)
    {
        if (result >= 0)
        {
            return;
        }

        var reason = result switch
        {
            -1 => "the request timed out",
            -2 => "the request was cancelled",
            -3 => "the request was chosen as a deadlock victim",
            -999 => "the call was invalid",
            _ => "unknown result",
        };
        throw new InvalidOperationException(
            $"graphiti-net could not take the lock of group '{groupId}': sp_getapplock returned {result} ({reason}).");
    }

    private static async ValueTask CloseQuietlyAsync(SqlConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException or TimeoutException)
        {
            // The session is gone either way.
        }
    }

    private sealed class Handle(SqlConnection connection, string resource, IAsyncDisposable local) : IAsyncDisposable
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
                if (connection.State == ConnectionState.Open)
                {
                    // Release explicitly so the lock is free when this returns; closing alone would also free it.
                    await using var release = new SqlCommand("sp_releaseapplock", connection);
                    release.CommandType = CommandType.StoredProcedure;
                    release.CommandTimeout = 15;
                    release.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
                    release.Parameters.Add("@LockOwner", SqlDbType.VarChar, 32).Value = "Session";
                    await release.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or TimeoutException)
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
