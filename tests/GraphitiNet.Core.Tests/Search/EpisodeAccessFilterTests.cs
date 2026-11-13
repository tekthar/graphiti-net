// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

/// <summary>The episode access filter (graphiti-net extension), against the in-memory driver.</summary>
public sealed class EpisodeAccessFilterTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryGraphDriver _driver = new();

    public ValueTask DisposeAsync() => _driver.DisposeAsync();

    private sealed class Filter(IEnumerable<string> readable, EntityVisibility visibility = EntityVisibility.AllMentionsReadable)
        : IEpisodeAccessFilter
    {
        private readonly HashSet<string> _readable = [.. readable];

        public List<IReadOnlyCollection<string>> Calls { get; } = [];

        public EntityVisibility EntityVisibility => visibility;

        public ValueTask<IReadOnlySet<string>> GetReadableEpisodeUuidsAsync(
            IReadOnlyCollection<string> episodeUuids,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(episodeUuids);
            }

            IReadOnlySet<string> readable = episodeUuids.Where(_readable.Contains).ToHashSet();
            return ValueTask.FromResult(readable);
        }
    }

    private static readonly string[] Readable = ["ep-public", "ep-other"];

    private static EpisodicNode Episode(string uuid, string group, string content) => new()
    {
        Uuid = uuid,
        Name = uuid,
        GroupId = group,
        Source = EpisodeType.Text,
        SourceDescription = "test",
        Content = content,
        ValidAt = Now,
    };

    private static EntityNode Entity(string uuid, string group, string name) =>
        new() { Uuid = uuid, Name = name, GroupId = group, CreatedAt = Now };

    private static EpisodicEdge Mention(string episode, string entity, string group) => new()
    {
        GroupId = group,
        SourceNodeUuid = episode,
        TargetNodeUuid = entity,
        CreatedAt = Now,
    };

    private static EntityEdge Fact(string uuid, string group, string source, string target, string fact, params string[] episodes) =>
        new()
        {
            Uuid = uuid,
            GroupId = group,
            SourceNodeUuid = source,
            TargetNodeUuid = target,
            Name = "RELATES_TO",
            Fact = fact,
            CreatedAt = Now,
            Episodes = [.. episodes],
        };

    /// <summary>
    /// Two groups. In g1, "ep-secret" is unreadable. The hidden records match the query best, so a filter applied
    /// after the limit would return too few results.
    /// </summary>
    private async Task SeedAsync()
    {
        var episodes = new[]
        {
            Episode("ep-public", "g1", "alpha report"),
            Episode("ep-secret", "g1", "alpha alpha alpha secret"),
            Episode("ep-other", "g2", "alpha notes"),
        };
        var entities = new[]
        {
            Entity("alice", "g1", "alpha Alice"),
            Entity("bob", "g1", "alpha alpha alpha Bob"),
            Entity("carol", "g1", "alpha Carol"),
            Entity("dave", "g2", "alpha Dave"),
        };
        var mentions = new[]
        {
            Mention("ep-public", "alice", "g1"),
            Mention("ep-secret", "alice", "g1"),
            Mention("ep-secret", "bob", "g1"),
            Mention("ep-public", "carol", "g1"),
            Mention("ep-other", "dave", "g2"),
        };
        var facts = new[]
        {
            Fact("f-secret", "g1", "alice", "bob", "alpha alpha alpha Alice met Bob", "ep-secret"),
            Fact("f-mixed", "g1", "alice", "carol", "alpha alpha Alice told Carol", "ep-public", "ep-secret"),
            Fact("f-public", "g1", "alice", "carol", "alpha Alice knows Carol", "ep-public"),
            Fact("f-other", "g2", "dave", "dave", "alpha Dave writes notes", "ep-other"),
            Fact("f-orphan", "g1", "carol", "alice", "alpha alpha Carol orphan fact"),
        };
        await _driver.AddNodesAndEdgesBulkAsync(episodes, mentions, entities, facts);
    }

    private static readonly SearchConfig Bm25Config = new()
    {
        EdgeConfig = new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.Bm25] },
        NodeConfig = new NodeSearchConfig { SearchMethods = [NodeSearchMethod.Bm25] },
        EpisodeConfig = new EpisodeSearchConfig { SearchMethods = [EpisodeSearchMethod.Bm25] },
        CommunityConfig = new CommunitySearchConfig { SearchMethods = [CommunitySearchMethod.Bm25] },
        Limit = 2,
    };

    private Task<SearchResults> SearchAsync(IEpisodeAccessFilter? filter, SearchConfig? config = null) =>
        SearchEngine.SearchAsync(
            _driver,
            new FakeEmbedder([1f]),
            null,
            "alpha",
            ["g1", "g2"],
            config ?? Bm25Config,
            new SearchFilters(),
            episodeAccessFilter: filter);

    [Fact]
    public async Task Without_a_filter_the_hidden_records_rank_first()
    {
        await SeedAsync();

        var results = await SearchAsync(null);

        Assert.Contains(results.Edges, e => e.Uuid == "f-secret");
        Assert.Contains(results.Nodes, n => n.Uuid == "bob");
        Assert.Contains(results.Episodes, e => e.Uuid == "ep-secret");
        Assert.Null(results.NodeEpisodeUuids);
    }

    [Fact]
    public async Task Filter_hides_records_behind_unreadable_episodes_and_still_fills_the_limit()
    {
        await SeedAsync();
        var filter = new Filter(Readable);

        var results = await SearchAsync(filter);

        // Facts: f-secret and f-mixed rely on ep-secret, f-orphan has no episodes; the limit is filled from the rest.
        Assert.Equal(["f-other", "f-public"], results.Edges.Select(e => e.Uuid).Order());

        // Entities: bob is mentioned only by ep-secret, alice also by it (hidden under the default rule).
        Assert.Equal(["carol", "dave"], results.Nodes.Select(n => n.Uuid).Order());
        Assert.NotNull(results.NodeEpisodeUuids);
        Assert.Equal(["ep-public"], results.NodeEpisodeUuids["carol"]);
        Assert.Equal(["ep-other"], results.NodeEpisodeUuids["dave"]);

        Assert.Equal(["ep-other", "ep-public"], results.Episodes.Select(e => e.Uuid).Order());
        Assert.Empty(results.Communities);
        Assert.Equal(results.Edges.Count, results.EdgeRerankerScores.Count);
    }

    [Fact]
    public async Task Any_mention_rule_shows_entities_with_one_readable_mention()
    {
        await SeedAsync();
        var filter = new Filter(Readable, EntityVisibility.AnyMentionReadable);
        var config = Bm25Config with { Limit = 10, EdgeConfig = null, EpisodeConfig = null };

        var results = await SearchAsync(filter, config);

        Assert.Equal(["alice", "carol", "dave"], results.Nodes.Select(n => n.Uuid).Order());
        Assert.Equal(["ep-public"], results.NodeEpisodeUuids!["alice"]);
    }

    [Fact]
    public async Task Filter_checks_each_scope_in_one_batch()
    {
        await SeedAsync();
        var filter = new Filter(Readable);

        await SearchAsync(filter);

        // One call each for facts, entities and episodes; each id appears once per call.
        Assert.Equal(3, filter.Calls.Count);
        Assert.All(filter.Calls, call => Assert.Equal(call.Count, call.Distinct().Count()));
    }

    [Fact]
    public async Task Filter_overfetches_candidates()
    {
        var driver = new FakeGraphDriver();
        var filter = new Filter([]);

        await SearchEngine.EdgeSearchAsync(
            driver,
            null,
            "q",
            [1f],
            null,
            new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.Bm25] },
            new SearchFilters(),
            limit: 3,
            episodeAccessFilter: filter);

        var call = Assert.Single(driver.Search.Calls);
        Assert.Equal(2 * 3 * EpisodeAccess.CandidateMultiplier, call.Args[3]);
    }
}
