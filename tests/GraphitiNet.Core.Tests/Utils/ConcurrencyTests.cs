// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Utils;

public class ConcurrencyTests
{
    [Fact]
    public async Task Semaphore_gather_keeps_order_and_bounds_concurrency()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new object();

        var results = await Concurrency.SemaphoreGatherAsync(
            Enumerable.Range(0, 10).Select<int, Func<Task<int>>>(i => async () =>
            {
                lock (gate)
                {
                    inFlight++;
                    maxInFlight = Math.Max(maxInFlight, inFlight);
                }

                await Task.Delay(10 - i);
                lock (gate)
                {
                    inFlight--;
                }

                return i;
            }),
            maxCoroutines: 3);

        Assert.Equal(Enumerable.Range(0, 10), results);
        Assert.InRange(maxInFlight, 1, 3);
    }

    [Fact]
    public async Task Semaphore_gather_rethrows_a_failure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Concurrency.SemaphoreGatherAsync(
            [() => Task.CompletedTask, () => Task.FromException(new InvalidOperationException())]));
    }

    [Fact]
    public void Default_limit_matches_upstream()
    {
        Assert.Equal(20, Concurrency.DefaultSemaphoreLimit);
    }
}
