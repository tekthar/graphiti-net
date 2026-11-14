// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;
using static GraphitiNet.Acceptance.Tests.Support;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>
/// tekthar/graphiti-net#10: reads that apply the episode access filter with the same rules as search, on every driver.
/// </summary>
public class Issue10Tests
{
    private static readonly ScriptedEpisode AliceAcme = new(
        "Alice works at Acme.",
        ["Alice", "Acme"],
        [new("Alice", "WORKS_AT", "Acme", "Alice works at Acme.")]);

    private static readonly ScriptedEpisode AliceTennis = new(
        "Alice plays tennis at the Riverside Club.",
        ["Alice", "Riverside Club"],
        [new("Alice", "PLAYS_AT", "Riverside Club", "Alice plays tennis at the Riverside Club.")]);

    private static readonly ScriptedEpisode BobGlobex = new(
        "Bob works at Globex.",
        ["Bob", "Globex"],
        [new("Bob", "WORKS_AT", "Globex", "Bob works at Globex.")]);

    private static readonly ScriptedEpisode CarolOslo = new(
        "Carol lives in Oslo.",
        ["Carol", "Oslo"],
        [new("Carol", "LIVES_IN", "Oslo", "Carol lives in Oslo.")]);

    private static readonly ScriptedEpisode DaveBerlin = new(
        "Dave studies in Berlin.",
        ["Dave", "Berlin"],
        [new("Dave", "STUDIES_IN", "Berlin", "Dave studies in Berlin.")]);

    private static readonly ScriptedEpisode EveRome = new(
        "Eve paints in Rome.",
        ["Eve", "Rome"],
        [new("Eve", "PAINTS_IN", "Rome", "Eve paints in Rome.")]);

    private static ScriptedLlm Llm() => new(AliceAcme, AliceTennis, BobGlobex, CarolOslo, DaveBerlin, EveRome);

    /// <summary>A group of five episodes; the tennis and Oslo episodes are the hidden ones.</summary>
    private static async Task<(string Group, HideEpisodes Filter, EpisodicNode[] Hidden)> SeedAsync(Harness h)
    {
        var group = Harness.NewGroup();
        await h.AddAsync(group, AliceAcme.Body, Jan);
        var tennis = (await h.AddAsync(group, AliceTennis.Body, Mar)).Episode;
        await h.AddAsync(group, BobGlobex.Body, Mar);
        var oslo = (await h.AddAsync(group, CarolOslo.Body, Jun)).Episode;
        await h.AddAsync(group, DaveBerlin.Body, Jun);
        return (group, new HideEpisodes(tennis.Uuid, oslo.Uuid), [tennis, oslo]);
    }

    private static async Task<List<GroupGraphPage>> AllGraphPagesAsync(
        Harness h, string group, IEpisodeAccessFilter? filter, int limit, Func<int, Task>? afterPage = null)
    {
        var pages = new List<GroupGraphPage>();
        string? cursor = null;
        do
        {
            var page = await h.Graphiti.GetGroupGraphAsync(group, filter, limit, cursor);
            pages.Add(page);
            cursor = page.NextCursor;
            if (afterPage is not null)
            {
                await afterPage(pages.Count);
            }
        }
        while (cursor is not null);

        return pages;
    }

    private static async Task<List<RecordPage<EpisodicNode>>> AllEpisodePagesAsync(
        Harness h, string group, IEpisodeAccessFilter? filter, int limit)
    {
        var pages = new List<RecordPage<EpisodicNode>>();
        string? cursor = null;
        do
        {
            var page = await h.Graphiti.GetGroupEpisodesAsync(group, filter, limit, cursor);
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return pages;
    }

    /// <summary>Every page but the last is full, and the last is not over the limit.</summary>
    private static void AssertFullPages<T>(IReadOnlyList<IReadOnlyCollection<T>> pages, int limit)
    {
        for (var i = 0; i < pages.Count - 1; i++)
        {
            Assert.Equal(limit, pages[i].Count);
        }

        Assert.InRange(pages[^1].Count, 0, limit);
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Reads_leave_out_records_from_a_hidden_episode(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, Llm());
        var (group, filter, hidden) = await SeedAsync(h);

        var pages = await AllGraphPagesAsync(h, group, filter, limit: 2);
        var entities = pages.SelectMany(p => p.Entities).ToList();
        var facts = pages.SelectMany(p => p.Facts).ToList();

        // Alice is mentioned by a hidden episode; the Riverside Club, Carol and Oslo only by hidden ones.
        Assert.Equal(
            ["Acme", "Berlin", "Bob", "Dave", "Globex"],
            entities.Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Alice works at Acme.", "Bob works at Globex.", "Dave studies in Berlin."],
            facts.Select(e => e.Fact).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(facts, e => e.Episodes.Any(hidden.Select(x => x.Uuid).Contains));

        // A node's edges: the hidden tennis fact is left out, the Acme fact stays, though Alice herself is hidden.
        var alice = Assert.Single(await h.EntitiesAsync(group), n => n.Name == "Alice");
        var aliceFacts = await h.Graphiti.GetFactsByNodeUuidAsync(alice.Uuid, filter);
        Assert.Equal(["Alice works at Acme."], aliceFacts.Select(e => e.Fact));

        // Episodes, one per page.
        var episodePages = await AllEpisodePagesAsync(h, group, filter, limit: 1);
        var episodes = episodePages.SelectMany(p => p.Items).ToList();
        Assert.Equal(3, episodes.Count);
        Assert.DoesNotContain(episodes, e => hidden.Any(x => x.Uuid == e.Uuid));
        AssertFullPages([.. episodePages.Select(p => (IReadOnlyCollection<EpisodicNode>)p.Items)], 1);
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Without_a_filter_reads_return_what_the_namespace_methods_return(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, Llm());
        var (group, _, _) = await SeedAsync(h);

        string? cursor = null;
        var graphFacts = new List<EntityEdge>();
        do
        {
            var page = await h.Graphiti.GetGroupGraphAsync(group, limit: 3, cursor: cursor);
            var expected = await h.Graphiti.Nodes.Entity.GetByGroupIdsAsync([group], 3, cursor);
            Assert.Equal(expected.Select(n => n.Uuid), page.Entities.Select(n => n.Uuid));
            graphFacts.AddRange(page.Facts);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(
            (await h.FactsAsync(group)).Select(e => e.Uuid).Order(StringComparer.Ordinal),
            graphFacts.Select(e => e.Uuid).Order(StringComparer.Ordinal));

        var alice = Assert.Single(await h.EntitiesAsync(group), n => n.Name == "Alice");
        Assert.Equal(
            (await h.Graphiti.Edges.Entity.GetByNodeUuidAsync(alice.Uuid)).Select(e => e.Uuid),
            (await h.Graphiti.GetFactsByNodeUuidAsync(alice.Uuid)).Select(e => e.Uuid));

        var episodes = await h.Graphiti.GetGroupEpisodesAsync(group);
        Assert.Equal((await h.EpisodesAsync(group)).Select(e => e.Uuid), episodes.Items.Select(e => e.Uuid));
        Assert.Null(episodes.NextCursor);
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Paging_is_stable_under_filtering_and_writes_between_pages(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, Llm());
        var (group, filter, _) = await SeedAsync(h);

        // Pages of one: hidden entities never make a short page before the last.
        var single = await AllGraphPagesAsync(h, group, filter, limit: 1);
        AssertFullPages([.. single.Select(p => (IReadOnlyCollection<EntityNode>)p.Entities)], 1);
        var all = single.SelectMany(p => p.Entities).Select(n => n.Uuid).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(all.OrderDescending(StringComparer.Ordinal), all);

        // Each fact on exactly one page.
        var facts = single.SelectMany(p => p.Facts).Select(e => e.Uuid).ToList();
        Assert.Equal(facts.Distinct().Count(), facts.Count);

        // A write between pages neither repeats nor drops a record that was there before.
        var pages = await AllGraphPagesAsync(h, group, filter, limit: 2, afterPage: async n =>
        {
            if (n == 1)
            {
                await h.AddAsync(group, EveRome.Body, Jun);
            }
        });
        AssertFullPages([.. pages.Select(p => (IReadOnlyCollection<EntityNode>)p.Entities)], 2);
        var read = pages.SelectMany(p => p.Entities).Select(n => n.Uuid).ToList();
        Assert.Equal(read.Distinct().Count(), read.Count);
        Assert.Subset(read.ToHashSet(), all.ToHashSet());
        var added = (await h.EntitiesAsync(group)).Where(e => e.Name is "Eve" or "Rome").Select(e => e.Uuid);
        Assert.Subset(added.ToHashSet(), read.Except(all).ToHashSet());
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Search_and_reads_agree_on_what_one_user_can_see(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, Llm());
        var (group, filter, _) = await SeedAsync(h);
        const string query = "Alice Acme tennis Riverside Club Bob Globex Carol Oslo Dave Berlin works lives";

        var open = await h.Graphiti.SearchAsync(query, Everything, [group]);
        var filtered = await h.Graphiti.SearchAsync(query, Everything, [group], episodeAccessFilter: filter);
        var pages = await AllGraphPagesAsync(h, group, filter, limit: 3);
        var readEntities = pages.SelectMany(p => p.Entities).Select(n => n.Uuid).ToHashSet();
        var readFacts = pages.SelectMany(p => p.Facts).Select(e => e.Uuid).ToHashSet();
        var readEpisodes = (await AllEpisodePagesAsync(h, group, filter, limit: 2))
            .SelectMany(p => p.Items).Select(e => e.Uuid).ToHashSet();

        // The control: unfiltered search finds hidden and visible records of every kind.
        Assert.True(open.Nodes.Count > readEntities.Count);
        Assert.True(open.Edges.Count > readFacts.Count);
        Assert.True(open.Episodes.Count > readEpisodes.Count);

        Assert.All(open.Nodes, n => Assert.Equal(filtered.Nodes.Any(x => x.Uuid == n.Uuid), readEntities.Contains(n.Uuid)));
        Assert.All(open.Edges, e => Assert.Equal(filtered.Edges.Any(x => x.Uuid == e.Uuid), readFacts.Contains(e.Uuid)));
        Assert.All(open.Episodes, e =>
            Assert.Equal(filtered.Episodes.Any(x => x.Uuid == e.Uuid), readEpisodes.Contains(e.Uuid)));

        foreach (var node in open.Nodes)
        {
            var visibleFacts = (await h.Graphiti.GetFactsByNodeUuidAsync(node.Uuid, filter)).Select(e => e.Uuid);
            Assert.All(visibleFacts, uuid => Assert.Contains(uuid, readFacts));
        }
    }
}
