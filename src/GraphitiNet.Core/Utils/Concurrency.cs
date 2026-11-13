// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/helpers.py (Graphiti v0.31.2) and changed for .NET.

using System.Globalization;

namespace GraphitiNet.Utils;

/// <summary>Bounded concurrency, as upstream's <c>semaphore_gather</c>.</summary>
public static class Concurrency
{
    /// <summary>Upstream <c>DEFAULT_SEMAPHORE_LIMIT</c>.</summary>
    public const int DefaultSemaphoreLimit = 20;

    /// <summary>
    /// The default limit: the <c>SEMAPHORE_LIMIT</c> environment variable if it holds a positive integer, otherwise
    /// <see cref="DefaultSemaphoreLimit"/>. Upstream reads the same variable.
    /// </summary>
    public static int SemaphoreLimit { get; } = ReadSemaphoreLimit();

    /// <summary>
    /// Runs the operations with at most <paramref name="maxCoroutines"/> in flight and returns their results in
    /// order. Each operation starts only when a slot is free, as a Python coroutine does. As
    /// <c>asyncio.gather</c>, the first failure is rethrown once every operation has finished.
    /// </summary>
    /// <param name="operations">The operations to run.</param>
    /// <param name="maxCoroutines">The limit; <see langword="null"/> or 0 uses <see cref="SemaphoreLimit"/>, as
    /// upstream's <c>max_coroutines or SEMAPHORE_LIMIT</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for a slot.</param>
    /// <returns>The results, in the order of <paramref name="operations"/>.</returns>
    public static async Task<IReadOnlyList<T>> SemaphoreGatherAsync<T>(
        IEnumerable<Func<Task<T>>> operations,
        int? maxCoroutines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        using var semaphore = new SemaphoreSlim(Limit(maxCoroutines));

        async Task<T> RunAsync(Func<Task<T>> operation)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = operations.Select(RunAsync).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Runs operations that return nothing, with at most <paramref name="maxCoroutines"/> in flight.</summary>
    /// <param name="operations">The operations to run.</param>
    /// <param name="maxCoroutines">The limit; <see langword="null"/> or 0 uses <see cref="SemaphoreLimit"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for a slot.</param>
    public static Task SemaphoreGatherAsync(
        IEnumerable<Func<Task>> operations,
        int? maxCoroutines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return SemaphoreGatherAsync(
            operations.Select<Func<Task>, Func<Task<bool>>>(operation => async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }),
            maxCoroutines,
            cancellationToken);
    }

    private static int Limit(int? maxCoroutines) =>
        maxCoroutines is { } limit && limit != 0 ? limit : SemaphoreLimit;

    private static int ReadSemaphoreLimit()
    {
        var value = Environment.GetEnvironmentVariable("SEMAPHORE_LIMIT");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0
            ? limit
            : DefaultSemaphoreLimit;
    }
}
