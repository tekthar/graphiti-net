// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/search/test_search_tracing.py (Graphiti v0.31.2) and changed for .NET: upstream injects a
// tracer object; graphiti-net traces through an ActivitySource, so the tests listen to it.

using System.Collections.Concurrent;
using System.Diagnostics;
using GraphitiNet.Search;

namespace GraphitiNet.Tests.Search;

public sealed class SearchTracingTests : IDisposable
{
    private static readonly ActivitySource TestSource = new("GraphitiNet.Tests.SearchTracing");

    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public SearchTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name is SearchTracing.SourceName or "GraphitiNet.Tests.SearchTracing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Search_emits_trace_spans_for_edge_similarity()
    {
        var driver = new FakeGraphDriver();
        SearchResults results;
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("test-root"))
        {
            Assert.NotNull(root);
            traceId = root.TraceId;
            results = await SearchEngine.SearchAsync(
                driver,
                new FakeEmbedder([0.1f, 0.2f, 0.3f]),
                null,
                "where is the edge",
                null,
                new SearchConfig
                {
                    EdgeConfig = new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.CosineSimilarity] },
                },
                new SearchFilters());
        }

        Assert.Empty(results.Edges);
        Assert.Empty(results.Nodes);
        Assert.Empty(results.Episodes);
        Assert.Empty(results.Communities);

        // Other tests may trace concurrently; keep this test's trace only.
        var spans = _activities.Where(a => a.TraceId == traceId && a.Source.Name == SearchTracing.SourceName).ToList();
        var names = spans.Select(s => s.OperationName).ToList();
        Assert.Contains("search.embed_query_vector", names);
        Assert.Contains("search.execute_scopes", names);
        Assert.Contains("search.edge_search", names);
        Assert.Contains("search.edge_search.execute_methods", names);
        Assert.Contains("search.edge_search.rerank", names);

        var executeScopes = spans.Single(s => s.OperationName == "search.execute_scopes");
        Assert.Equal(true, executeScopes.GetTagItem("scope.edges"));
        Assert.Equal(0, executeScopes.GetTagItem("result.edges"));
        Assert.Equal(ActivityStatusCode.Ok, executeScopes.Status);

        var embed = spans.Single(s => s.OperationName == "search.embed_query_vector");
        Assert.Equal("where is the edge".Length, embed.GetTagItem("query.length"));
        Assert.Equal(3, embed.GetTagItem("query_vector.dimension"));
        Assert.Equal(executeScopes.ParentId, embed.ParentId);

        var edgeSearch = spans.Single(s => s.OperationName == "search.edge_search");
        Assert.Equal("reciprocal_rank_fusion", edgeSearch.GetTagItem("reranker"));
        Assert.Equal(["cosine_similarity"], Assert.IsType<string[]>(edgeSearch.GetTagItem("search_methods")));
    }

    [Fact]
    public async Task Search_works_without_a_listener()
    {
        // The equivalent of upstream's no-op tracer: nothing listens to this activity source in production by
        // default, and search must not depend on one.
        _listener.Dispose();

        var (edges, scores) = await SearchEngine.EdgeSearchAsync(
            new FakeGraphDriver(),
            null,
            "where is the edge",
            [0.1f, 0.2f, 0.3f],
            null,
            new EdgeSearchConfig { SearchMethods = [EdgeSearchMethod.CosineSimilarity] },
            new SearchFilters());

        Assert.Empty(edges);
        Assert.Empty(scores);
    }

    [Fact]
    public async Task Failing_phase_records_the_error()
    {
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("test-root"))
        {
            Assert.NotNull(root);
            traceId = root.TraceId;
            await Assert.ThrowsAsync<SearchRerankerException>(() => SearchEngine.EdgeSearchAsync(
                new FakeGraphDriver(),
                null,
                "q",
                [0.1f],
                null,
                new EdgeSearchConfig
                {
                    SearchMethods = [EdgeSearchMethod.Bm25],
                    Reranker = EdgeReranker.NodeDistance,
                },
                new SearchFilters()));
        }

        var edgeSearch = _activities.Single(a => a.TraceId == traceId && a.OperationName == "search.edge_search");
        Assert.Equal(ActivityStatusCode.Error, edgeSearch.Status);
        Assert.Equal("No center node provided for Node Distance reranker", edgeSearch.StatusDescription);
    }
}
