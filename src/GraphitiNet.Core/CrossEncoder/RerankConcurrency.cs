// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.CrossEncoder;

/// <summary>Runs one LLM call per passage with bounded concurrency, like upstream's <c>semaphore_gather</c>.</summary>
internal static class RerankConcurrency
{
    /// <summary>Upstream's <c>DEFAULT_SEMAPHORE_LIMIT</c>.</summary>
    public const int DefaultLimit = 20;

    public static async Task<TResult[]> GatherAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int limit,
        Func<TItem, CancellationToken, Task<TResult>> call,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, limit));
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await call(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
