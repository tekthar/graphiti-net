// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/graph_data_operations.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Driver;
using GraphitiNet.Models;

namespace GraphitiNet.Maintenance;

/// <summary>Graph-wide data operations: clearing data and fetching recent episodes as context.</summary>
/// <remarks>
/// Upstream sends Cypher here (or delegates to the driver's <c>graph_operations_interface</c>). graphiti-net has no
/// shared query language, so these delegate to the driver's operations, which implement the same queries.
/// </remarks>
public static class GraphDataOperations
{
    /// <summary>Upstream <c>EPISODE_WINDOW_LEN</c>: the default number of previous episodes fetched.</summary>
    public const int EpisodeWindowLen = 3;

    /// <summary>
    /// Deletes every node and edge in <paramref name="groupIds"/>, or the whole graph when it is
    /// <see langword="null"/>. An empty list deletes nothing, as upstream's <c>IN $group_ids</c>.
    /// </summary>
    /// <param name="driver">Storage.</param>
    /// <param name="groupIds">The groups to clear, or <see langword="null"/> for everything.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task ClearDataAsync(
        IGraphDriver driver,
        IReadOnlyList<string>? groupIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.GraphOps.ClearDataAsync(groupIds, cancellationToken);
    }

    /// <summary>
    /// The last <paramref name="lastN"/> episodes valid at or before <paramref name="referenceTime"/>, oldest first.
    /// </summary>
    /// <param name="driver">Storage.</param>
    /// <param name="referenceTime">Only episodes with <c>ValidAt</c> at or before this time.</param>
    /// <param name="lastN">How many episodes.</param>
    /// <param name="groupIds">The groups to read; <see langword="null"/> or empty reads every group.</param>
    /// <param name="source">Only episodes of this source type.</param>
    /// <param name="saga">Only episodes in the saga with this name (in the first group id).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The episodes in chronological order.</returns>
    public static Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        IGraphDriver driver,
        DateTimeOffset referenceTime,
        int lastN = EpisodeWindowLen,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return driver.EpisodeNodeOps.RetrieveEpisodesAsync(
            referenceTime, lastN, groupIds, source, saga, cancellationToken);
    }
}
