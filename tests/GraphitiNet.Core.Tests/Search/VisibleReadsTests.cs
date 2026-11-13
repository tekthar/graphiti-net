// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

/// <summary>Paging of the filtered reads (tekthar/graphiti-net#10), against the in-memory driver.</summary>
public sealed class VisibleReadsTests : IAsyncDisposable
{
    private const string Group = "g";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryGraphDriver _driver = new();

    public ValueTask DisposeAsync() => _driver.DisposeAsync();

    private sealed class Readable(IEnumerable<string> readable) : IEpisodeAccessFilter
    {
        private readonly HashSet<string> _readable = [.. readable];

        public int Calls { get; private set; }

        public ValueTask<IReadOnlySet<string>> GetReadableEpisodeUuidsAsync(
            IReadOnlyCollection<string> episodeUuids, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlySet<string>>(episodeUuids.Where(_readable.Contains).ToHashSet());
        }
    }

    /// <summary>Episodes with ids "e00".."e{n-1}" (so the id order is known).</summary>
    private async Task<List<string>> SeedEpisodesAsync(int count)
    {
        var uuids = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var episode = new EpisodicNode
            {
                Uuid = $"e{i:D2}",
                Name = "e",
                GroupId = Group,
                Source = EpisodeType.Text,
                SourceDescription = "",
                Content = $"episode {i}",
                ValidAt = Now,
                CreatedAt = Now,
            };
            await _driver.EpisodeNodeOps.SaveAsync(episode);
            uuids.Add(episode.Uuid);
        }

        return uuids;
    }

    private async Task<List<RecordPage<EpisodicNode>>> ReadAllAsync(IEpisodeAccessFilter? filter, int limit)
    {
        var pages = new List<RecordPage<EpisodicNode>>();
        string? cursor = null;
        do
        {
            var page = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, filter, limit, cursor);
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null && pages.Count < 100);

        return pages;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task Pages_hold_up_to_the_limit_of_visible_records_in_id_order(int limit)
    {
        var uuids = await SeedEpisodesAsync(17);
        // Every third episode readable, plus a run of hidden ones at the start of the id order.
        var readable = uuids.Where((_, i) => i % 3 == 0 && i < 12).ToList();

        var pages = await ReadAllAsync(new Readable(readable), limit);

        var read = pages.SelectMany(p => p.Items).Select(e => e.Uuid).ToList();
        Assert.Equal(readable.OrderDescending(StringComparer.Ordinal), read);
        Assert.All(pages.SkipLast(1), page => Assert.Equal(limit, page.Items.Count));
        Assert.Null(pages[^1].NextCursor);
    }

    [Fact]
    public async Task The_cursor_is_the_last_record_scanned_and_null_when_none_remain()
    {
        var uuids = await SeedEpisodesAsync(6);
        var filter = new Readable([uuids[5], uuids[3]]);

        var first = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, filter, limit: 1);
        Assert.Equal([uuids[5]], first.Items.Select(e => e.Uuid));
        Assert.Equal(uuids[5], first.NextCursor);

        // The last readable record fills the page, and hidden records follow it: the cursor stays.
        var second = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, filter, limit: 1, cursor: first.NextCursor);
        Assert.Equal([uuids[3]], second.Items.Select(e => e.Uuid));
        Assert.Equal(uuids[3], second.NextCursor);

        var third = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, filter, limit: 1, cursor: second.NextCursor);
        Assert.Empty(third.Items);
        Assert.Null(third.NextCursor);

        // Filled by the last record there is: no cursor.
        var all = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, new Readable(uuids), limit: 4, cursor: uuids[4]);
        Assert.Equal(4, all.Items.Count);
        Assert.Null(all.NextCursor);
    }

    [Fact]
    public async Task Without_a_filter_a_page_is_the_drivers_page()
    {
        await SeedEpisodesAsync(7);

        string? cursor = null;
        do
        {
            var page = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, limit: 3, cursor: cursor);
            var expected = await _driver.EpisodeNodeOps.GetByGroupIdsAsync([Group], 3, cursor);
            Assert.Equal(expected.Select(e => e.Uuid), page.Items.Select(e => e.Uuid));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    [Fact]
    public async Task The_filter_is_asked_once_per_underlying_page()
    {
        await SeedEpisodesAsync(10);
        var filter = new Readable([]);

        var page = await VisibleReads.GetGroupEpisodesAsync(_driver, Group, filter, limit: 2);

        // Batches of limit * CandidateMultiplier = 8: two round trips cover the ten hidden episodes.
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Equal(2, filter.Calls);
    }

    [Fact]
    public async Task A_limit_below_one_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VisibleReads.GetGroupEpisodesAsync(_driver, Group, limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            VisibleReads.GetGroupGraphAsync(_driver, Group, limit: 0));
    }
}
