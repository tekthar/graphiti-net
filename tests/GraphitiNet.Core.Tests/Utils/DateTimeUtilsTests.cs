// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Utils;

public class DateTimeUtilsTests
{
    [Fact]
    public void UtcNow_is_utc_and_uses_the_time_provider()
    {
        var fixedTime = new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);
        Assert.Equal(fixedTime, DateTimeUtils.UtcNow(new FixedTimeProvider(fixedTime)));
        Assert.Equal(TimeSpan.Zero, DateTimeUtils.UtcNow().Offset);
    }

    [Fact]
    public void EnsureUtc_converts_offsets_to_utc()
    {
        var local = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var utc = DateTimeUtils.EnsureUtc(local);

        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(8, utc.Hour);
        Assert.Equal(local, utc);
    }

    [Fact]
    public void EnsureUtc_passes_null_through()
    {
        Assert.Null(DateTimeUtils.EnsureUtc((DateTimeOffset?)null));
    }

    [Fact]
    public void EnsureUtc_treats_a_datetime_with_no_kind_as_utc()
    {
        var naive = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Unspecified);

        var utc = DateTimeUtils.EnsureUtc(naive);

        Assert.Equal(TimeSpan.Zero, utc.Offset);
        Assert.Equal(10, utc.Hour);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
