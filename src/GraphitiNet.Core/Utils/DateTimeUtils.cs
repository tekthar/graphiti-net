// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/datetime_utils.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Utils;

/// <summary>UTC helpers. Every timestamp graphiti-net stores is in UTC.</summary>
/// <remarks>
/// Upstream's <c>convert_datetimes_to_strings</c> is not ported: it serves graph drivers that store timestamps as
/// strings, and the EF Core storage stores them as <c>timestamptz</c>.
/// </remarks>
public static class DateTimeUtils
{
    /// <summary>The current time in UTC.</summary>
    public static DateTimeOffset UtcNow(TimeProvider? timeProvider = null) =>
        (timeProvider ?? TimeProvider.System).GetUtcNow();

    /// <summary>Converts a timestamp to UTC.</summary>
    public static DateTimeOffset EnsureUtc(DateTimeOffset value) => value.ToUniversalTime();

    /// <summary>Converts a timestamp to UTC, passing <see langword="null"/> through.</summary>
    public static DateTimeOffset? EnsureUtc(DateTimeOffset? value) => value?.ToUniversalTime();

    /// <summary>
    /// Converts a <see cref="DateTime"/> to a UTC <see cref="DateTimeOffset"/>. A value with no time zone
    /// (<see cref="DateTimeKind.Unspecified"/>) is taken to be UTC already, as upstream does with naive datetimes.
    /// </summary>
    public static DateTimeOffset EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        _ => new DateTimeOffset(value.ToUniversalTime()),
    };
}
