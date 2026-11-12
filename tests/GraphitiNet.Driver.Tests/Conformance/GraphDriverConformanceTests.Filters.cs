// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Driver.Tests.Conformance;

// Every SearchFilters field, through every search method that takes filters.
public abstract partial class GraphDriverConformanceTests
{
    /// <summary>
    /// A star of four facts around a hub, all containing the word "keyword" and sharing one embedding:
    /// <list type="bullet">
    /// <item>f1: Hub -LIKES-> X1, valid Jan 1, created Jan 1, weight 1, colour red</item>
    /// <item>f2: Hub -LIKES-> X2, valid Jan 10 to Jan 20, created Jan 10, expired Jan 21, weight 5, colour blue</item>
    /// <item>f3: X3 -KNOWS-> Hub, no valid time, created Jan 15, weight 10</item>
    /// <item>f4: Hub -KNOWS-> X4, valid Jan 5 to Jan 6, created Jan 5, expired Jan 7, no attributes</item>
    /// </list>
    /// Hub, X1 and X3 are Persons, X2 is a Place, X4 has no labels.
    /// </summary>
    private async Task<FilterGraph> SaveFilterGraph()
    {
        var group = NewGroup();
        var hub = Entity(group, "Hub", labels: ["Person"]);
        var x1 = Entity(group, "X1", labels: ["Person"]);
        var x2 = Entity(group, "X2", labels: ["Place"]);
        var x3 = Entity(group, "X3", labels: ["Person"]);
        var x4 = Entity(group, "X4");
        await Save(hub, x1, x2, x3, x4);
        var embedding = Vec(0, 0, 1);
        var f1 = Fact(
            group, hub, x1, "LIKES", "keyword one", embedding, createdAt: Day(1), validAt: Day(1),
            attributes: new Dictionary<string, object?> { ["weight"] = 1L, ["colour"] = "red" });
        var f2 = Fact(
            group, hub, x2, "LIKES", "keyword two", embedding, createdAt: Day(10), validAt: Day(10),
            invalidAt: Day(20), expiredAt: Day(21),
            attributes: new Dictionary<string, object?> { ["weight"] = 5L, ["colour"] = "blue" });
        var f3 = Fact(
            group, x3, hub, "KNOWS", "keyword three", embedding, createdAt: Day(15),
            attributes: new Dictionary<string, object?> { ["weight"] = 10L });
        var f4 = Fact(
            group, hub, x4, "KNOWS", "keyword four", embedding, createdAt: Day(5), validAt: Day(5),
            invalidAt: Day(6), expiredAt: Day(7));
        await Save(f1, f2, f3, f4);
        return new FilterGraph(group, hub, x1, x2, x3, x4, f1, f2, f3, f4, embedding);
    }

    private sealed record FilterGraph(
        string Group,
        EntityNode Hub,
        EntityNode X1,
        EntityNode X2,
        EntityNode X3,
        EntityNode X4,
        EntityEdge F1,
        EntityEdge F2,
        EntityEdge F3,
        EntityEdge F4,
        float[] Embedding);

    /// <summary>Asserts full-text, similarity and breadth-first fact search (from the hub and X3, which between
    /// them start every fact) all return exactly
    /// <paramref name="expected"/> under <paramref name="filters"/>.</summary>
    private async Task AssertEdgeFilter(FilterGraph g, SearchFilters filters, params EntityEdge[] expected)
    {
        AssertSameEdges(
            expected,
            await Driver.SearchOps.EdgeFulltextSearchAsync("keyword", filters, [g.Group], limit: 100));
        AssertSameEdges(
            expected,
            await Driver.SearchOps.EdgeSimilaritySearchAsync(g.Embedding, null, null, filters, [g.Group], limit: 100));
        AssertSameEdges(
            expected,
            await Driver.SearchOps.EdgeBfsSearchAsync([g.Hub.Uuid, g.X3.Uuid], 1, filters, [g.Group], limit: 100));
    }

    private static DateFilter On(ComparisonOperator op, DateTimeOffset? date = null) =>
        new() { ComparisonOperator = op, Date = date };

    private static PropertyFilter Property(string name, ComparisonOperator op, object? value = null) =>
        new() { PropertyName = name, ComparisonOperator = op, PropertyValue = value };

    [Fact]
    public async Task No_filter_returns_every_fact()
    {
        var g = await SaveFilterGraph();
        await AssertEdgeFilter(g, SearchFilters.None, g.F1, g.F2, g.F3, g.F4);
        await AssertEdgeFilter(g, new SearchFilters(), g.F1, g.F2, g.F3, g.F4);
    }

    [Fact]
    public async Task Edge_types_filter_facts_by_name()
    {
        var g = await SaveFilterGraph();
        await AssertEdgeFilter(g, new SearchFilters { EdgeTypes = ["LIKES"] }, g.F1, g.F2);
        await AssertEdgeFilter(g, new SearchFilters { EdgeTypes = ["LIKES", "KNOWS"] }, g.F1, g.F2, g.F3, g.F4);
        await AssertEdgeFilter(g, new SearchFilters { EdgeTypes = ["HATES"] });
        await AssertEdgeFilter(g, new SearchFilters { EdgeTypes = [] });
    }

    [Fact]
    public async Task Edge_uuids_and_endpoints_filter_facts()
    {
        var g = await SaveFilterGraph();
        await AssertEdgeFilter(g, new SearchFilters { EdgeUuids = [g.F1.Uuid, g.F3.Uuid] }, g.F1, g.F3);
        await AssertEdgeFilter(g, new SearchFilters { EdgeUuids = [] });
        await AssertEdgeFilter(g, new SearchFilters { EdgeSourceNodeUuids = [g.X3.Uuid] }, g.F3);
        await AssertEdgeFilter(g, new SearchFilters { EdgeSourceNodeUuids = [g.Hub.Uuid] }, g.F1, g.F2, g.F4);
        await AssertEdgeFilter(g, new SearchFilters { EdgeTargetNodeUuids = [g.X1.Uuid, g.X2.Uuid] }, g.F1, g.F2);
        await AssertEdgeFilter(
            g,
            new SearchFilters { EdgeSourceNodeUuids = [g.Hub.Uuid], EdgeTargetNodeUuids = [g.X2.Uuid, g.Hub.Uuid] },
            g.F2);
    }

    [Fact]
    public async Task Node_labels_filter_facts_by_both_endpoints()
    {
        var g = await SaveFilterGraph();
        await AssertEdgeFilter(g, new SearchFilters { NodeLabels = ["Person"] }, g.F1, g.F3);
        await AssertEdgeFilter(g, new SearchFilters { NodeLabels = ["Person", "Place"] }, g.F1, g.F2, g.F3);
        await AssertEdgeFilter(g, new SearchFilters { NodeLabels = ["Place"] });

        // Every entity has the implicit Entity label, as upstream.
        await AssertEdgeFilter(g, new SearchFilters { NodeLabels = ["Entity"] }, g.F1, g.F2, g.F3, g.F4);
    }

    [Fact]
    public async Task Valid_at_filters_combine_and_within_groups_and_or_across_them()
    {
        var g = await SaveFilterGraph();
        SearchFilters ValidAt(params IReadOnlyList<DateFilter>[] groups) => new() { ValidAt = groups };

        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.GreaterThanEqual, Day(5))]), g.F2, g.F4);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.GreaterThan, Day(5))]), g.F2);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.LessThan, Day(5))]), g.F1);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.LessThanEqual, Day(5))]), g.F1, g.F4);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.Equals, Day(10))]), g.F2);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.NotEquals, Day(10))]), g.F1, g.F4);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.IsNull)]), g.F3);
        await AssertEdgeFilter(g, ValidAt([On(ComparisonOperator.IsNotNull)]), g.F1, g.F2, g.F4);

        // AND within a group.
        await AssertEdgeFilter(
            g,
            ValidAt([On(ComparisonOperator.GreaterThanEqual, Day(2)), On(ComparisonOperator.LessThanEqual, Day(9))]),
            g.F4);

        // OR across groups, each with its own values (upstream reuses the last group's values; see docs/drivers.md).
        await AssertEdgeFilter(
            g,
            ValidAt(
                [On(ComparisonOperator.GreaterThanEqual, Day(2)), On(ComparisonOperator.LessThanEqual, Day(9))],
                [On(ComparisonOperator.GreaterThanEqual, Day(10)), On(ComparisonOperator.LessThanEqual, Day(31))]),
            g.F2, g.F4);
        await AssertEdgeFilter(
            g,
            ValidAt([On(ComparisonOperator.LessThanEqual, Day(1))], [On(ComparisonOperator.IsNull)]),
            g.F1, g.F3);

        // Offsets do not matter: the comparison is on the instant.
        await AssertEdgeFilter(
            g,
            ValidAt([On(ComparisonOperator.Equals, Day(10).ToOffset(TimeSpan.FromHours(-8)))]),
            g.F2);
    }

    [Fact]
    public async Task Invalid_at_created_at_and_expired_at_filter_facts()
    {
        var g = await SaveFilterGraph();

        await AssertEdgeFilter(g, new SearchFilters { InvalidAt = [[On(ComparisonOperator.IsNull)]] }, g.F1, g.F3);
        await AssertEdgeFilter(g, new SearchFilters { InvalidAt = [[On(ComparisonOperator.GreaterThan, Day(10))]] }, g.F2);
        await AssertEdgeFilter(
            g,
            new SearchFilters { InvalidAt = [[On(ComparisonOperator.IsNull)], [On(ComparisonOperator.LessThan, Day(10))]] },
            g.F1, g.F3, g.F4);

        await AssertEdgeFilter(g, new SearchFilters { CreatedAt = [[On(ComparisonOperator.LessThan, Day(10))]] }, g.F1, g.F4);
        await AssertEdgeFilter(
            g,
            new SearchFilters
            {
                CreatedAt = [[On(ComparisonOperator.GreaterThan, Day(1)), On(ComparisonOperator.LessThanEqual, Day(10))]],
            },
            g.F2, g.F4);
        await AssertEdgeFilter(g, new SearchFilters { CreatedAt = [[On(ComparisonOperator.IsNull)]] });

        await AssertEdgeFilter(g, new SearchFilters { ExpiredAt = [[On(ComparisonOperator.IsNotNull)]] }, g.F2, g.F4);
        await AssertEdgeFilter(
            g,
            new SearchFilters { ExpiredAt = [[On(ComparisonOperator.IsNull)], [On(ComparisonOperator.GreaterThan, Day(20))]] },
            g.F1, g.F2, g.F3);
    }

    [Fact]
    public async Task Date_filters_on_different_fields_all_apply()
    {
        var g = await SaveFilterGraph();
        await AssertEdgeFilter(
            g,
            new SearchFilters
            {
                ValidAt = [[On(ComparisonOperator.IsNotNull)]],
                ExpiredAt = [[On(ComparisonOperator.IsNull)], [On(ComparisonOperator.GreaterThan, Day(20))]],
                EdgeTypes = ["LIKES", "KNOWS"],
            },
            g.F1, g.F2);
        await AssertEdgeFilter(
            g,
            new SearchFilters { EdgeTypes = ["LIKES"], ValidAt = [[On(ComparisonOperator.GreaterThanEqual, Day(5))]] },
            g.F2);
    }

    [Fact]
    public async Task Property_filters_compare_fact_attributes()
    {
        var g = await SaveFilterGraph();
        SearchFilters Where(params PropertyFilter[] filters) => new() { PropertyFilters = filters };

        await AssertEdgeFilter(g, Where(Property("weight", ComparisonOperator.GreaterThan, 1)), g.F2, g.F3);
        await AssertEdgeFilter(g, Where(Property("weight", ComparisonOperator.Equals, 5)), g.F2);
        await AssertEdgeFilter(g, Where(Property("weight", ComparisonOperator.GreaterThanEqual, 5.0)), g.F2, g.F3);
        await AssertEdgeFilter(g, Where(Property("weight", ComparisonOperator.LessThan, 5.5)), g.F1, g.F2);
        await AssertEdgeFilter(g, Where(Property("weight", ComparisonOperator.NotEquals, 5)), g.F1, g.F3);
        await AssertEdgeFilter(g, Where(Property("colour", ComparisonOperator.Equals, "red")), g.F1);
        await AssertEdgeFilter(g, Where(Property("colour", ComparisonOperator.NotEquals, "red")), g.F2);
        await AssertEdgeFilter(g, Where(Property("colour", ComparisonOperator.IsNull)), g.F3, g.F4);
        await AssertEdgeFilter(g, Where(Property("colour", ComparisonOperator.IsNotNull)), g.F1, g.F2);
        await AssertEdgeFilter(g, Where(Property("missing", ComparisonOperator.Equals, "x")));

        // Every property filter must hold.
        await AssertEdgeFilter(
            g,
            Where(
                Property("weight", ComparisonOperator.IsNotNull),
                Property("weight", ComparisonOperator.LessThanEqual, 5)),
            g.F1, g.F2);
        await AssertEdgeFilter(
            g,
            Where(Property("weight", ComparisonOperator.GreaterThan, 1), Property("colour", ComparisonOperator.Equals, "red")));
    }

    [Fact]
    public async Task Edge_similarity_endpoints_and_filters_combine()
    {
        var g = await SaveFilterGraph();
        AssertSameEdges(
            [g.F1],
            await Driver.SearchOps.EdgeSimilaritySearchAsync(
                g.Embedding, g.Hub.Uuid, null, new SearchFilters { NodeLabels = ["Person"] }, [g.Group]));
    }

    // ---------------------------------------------------------------- entity filters

    private async Task<(string Group, EntityNode Ann, EntityNode Paris, EntityNode Bob, float[] Embedding)> SaveNodeFilterGraph()
    {
        var group = NewGroup();
        var embedding = Vec(0, 1, 0);
        var ann = Entity(
            group, "Ann keyword", labels: ["Person"], embedding: embedding,
            attributes: new Dictionary<string, object?> { ["age"] = 30L, ["city"] = "Oslo" });
        var paris = Entity(
            group, "Paris keyword", labels: ["Place"], embedding: embedding,
            attributes: new Dictionary<string, object?> { ["age"] = 2000L });
        var bob = Entity(group, "Bob keyword", labels: ["Person", "Employee"], embedding: embedding);
        await Save(ann, paris, bob);
        return (group, ann, paris, bob, embedding);
    }

    private async Task AssertNodeFilter(
        string group,
        string originUuid,
        float[] embedding,
        SearchFilters filters,
        params EntityNode[] expected)
    {
        AssertSameNodes(
            expected,
            await Driver.SearchOps.NodeFulltextSearchAsync("keyword", filters, [group], limit: 100));
        AssertSameNodes(
            expected,
            await Driver.SearchOps.NodeSimilaritySearchAsync(embedding, filters, [group], limit: 100));
        AssertSameNodes(
            expected,
            await Driver.SearchOps.NodeBfsSearchAsync([originUuid], filters, 1, [group], limit: 100));
    }

    [Fact]
    public async Task Entity_searches_apply_node_labels_and_property_filters()
    {
        var (group, ann, paris, bob, embedding) = await SaveNodeFilterGraph();

        // An episode that mentions all three, as the BFS origin.
        var episode = Episode(group, "origin");
        await Save(episode);
        await Save(Mention(group, episode, ann), Mention(group, episode, paris), Mention(group, episode, bob));

        await AssertNodeFilter(group, episode.Uuid, embedding, SearchFilters.None, ann, paris, bob);
        await AssertNodeFilter(group, episode.Uuid, embedding, new SearchFilters { NodeLabels = ["Person"] }, ann, bob);
        await AssertNodeFilter(group, episode.Uuid, embedding, new SearchFilters { NodeLabels = ["Employee"] }, bob);
        await AssertNodeFilter(
            group, episode.Uuid, embedding, new SearchFilters { NodeLabels = ["Employee", "Place"] }, paris, bob);
        await AssertNodeFilter(
            group, episode.Uuid, embedding, new SearchFilters { NodeLabels = ["Entity"] }, ann, paris, bob);
        await AssertNodeFilter(group, episode.Uuid, embedding, new SearchFilters { NodeLabels = ["Organization"] });

        await AssertNodeFilter(
            group, episode.Uuid, embedding,
            new SearchFilters { PropertyFilters = [Property("age", ComparisonOperator.LessThan, 100)] },
            ann);
        await AssertNodeFilter(
            group, episode.Uuid, embedding,
            new SearchFilters { PropertyFilters = [Property("age", ComparisonOperator.IsNull)] },
            bob);
        await AssertNodeFilter(
            group, episode.Uuid, embedding,
            new SearchFilters { PropertyFilters = [Property("city", ComparisonOperator.Equals, "Oslo")] },
            ann);
        await AssertNodeFilter(
            group, episode.Uuid, embedding,
            new SearchFilters
            {
                NodeLabels = ["Person", "Place"],
                PropertyFilters = [Property("age", ComparisonOperator.GreaterThanEqual, 30)],
            },
            ann, paris);
    }

    [Fact]
    public async Task Entity_searches_ignore_fact_only_filter_fields()
    {
        var (group, ann, paris, bob, embedding) = await SaveNodeFilterGraph();
        var episode = Episode(group, "origin");
        await Save(episode);
        await Save(Mention(group, episode, ann), Mention(group, episode, paris), Mention(group, episode, bob));

        var factOnly = new SearchFilters
        {
            EdgeTypes = ["NOTHING"],
            EdgeUuids = [],
            EdgeSourceNodeUuids = [],
            EdgeTargetNodeUuids = [],
            ValidAt = [[On(ComparisonOperator.IsNotNull)]],
            InvalidAt = [[On(ComparisonOperator.IsNotNull)]],
            CreatedAt = [[On(ComparisonOperator.IsNull)]],
            ExpiredAt = [[On(ComparisonOperator.IsNotNull)]],
        };
        await AssertNodeFilter(group, episode.Uuid, embedding, factOnly, ann, paris, bob);
    }

    [Fact]
    public async Task Episode_fulltext_search_takes_but_does_not_apply_filters()
    {
        var group = NewGroup();
        var episode = Episode(group, "e", "keyword content");
        await Save(episode);

        var filters = new SearchFilters
        {
            NodeLabels = ["Nothing"],
            EdgeTypes = [],
            CreatedAt = [[On(ComparisonOperator.IsNull)]],
            PropertyFilters = [Property("missing", ComparisonOperator.Equals, "x")],
        };
        AssertSameNodes([episode], await Driver.SearchOps.EpisodeFulltextSearchAsync("keyword", filters, [group]));
    }
}
