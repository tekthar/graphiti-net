// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_edge_dedup_scenarios.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.RegularExpressions;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Maintenance;

/// <summary>
/// End-to-end fact resolution scenarios. Unlike upstream, which scripts the search, these run the real hybrid search
/// over the in-memory driver (with a bag-of-words embedder); the "LLM judge" sees only fact text, never endpoints, like
/// the real dedupe prompt, and decides by word overlap.
/// </summary>
public partial class EdgeDedupScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed record Scenario(
        IReadOnlyList<EntityEdge> Graph,
        (string Source, string Target, string Fact) Extracted,
        IReadOnlyList<string> Nodes);

    private sealed record Report(
        IReadOnlyList<string> Searches,
        IReadOnlyList<string>? ExistingFacts,
        IReadOnlyList<string>? InvalidationCandidates,
        bool LlmCalled,
        string ResolvedUuid,
        string ExtractedUuid,
        bool MergedCrossEndpoint,
        int Invalidated);

    private static EntityEdge MakeEdge(string uuid, string source, string target, string fact, int ageDays = 1) => new()
    {
        Uuid = uuid,
        SourceNodeUuid = source,
        TargetNodeUuid = target,
        Name = "RELATES_TO",
        GroupId = "g1",
        Fact = fact,
        Episodes = ["old_episode"],
        CreatedAt = Now.AddDays(-ageDays),
        ValidAt = Now.AddDays(-ageDays),
        FactEmbedding = FakeEmbedder.TextVector(fact),
    };

    private static double Jaccard(string a, string b)
    {
        var ta = Word().Matches(a.ToLowerInvariant()).Select(m => m.Value).ToHashSet();
        var tb = Word().Matches(b.ToLowerInvariant()).Select(m => m.Value).ToHashSet();
        var union = ta.Union(tb).Count();
        return (double)ta.Intersect(tb).Count() / (union == 0 ? 1 : union);
    }

    private static List<(int Idx, string Fact)> ParseList(string message, string tag)
    {
        var section = Regex.Match(message, $@"<{tag}>\s*(\[.*?\])\s*</{tag}>", RegexOptions.Singleline);
        if (!section.Success)
        {
            return [];
        }

        return FactEntry().Matches(section.Groups[1].Value)
            .Select(m => (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value))
            .ToList();
    }

    private static async Task<Report> RunScenarioAsync(Scenario scenario)
    {
        var (clients, llm, driver, _) = EdgeTestData.Clients();
        foreach (var uuid in scenario.Nodes)
        {
            await driver.EntityNodeOps.SaveAsync(new EntityNode { Uuid = uuid, Name = uuid, GroupId = "g1", Labels = ["Entity"] });
        }

        foreach (var edge in scenario.Graph)
        {
            await driver.EntityEdgeOps.SaveAsync(edge);
        }

        var (src, tgt, fact) = scenario.Extracted;
        IReadOnlyList<string>? existingShown = null;
        IReadOnlyList<string>? invalidationShown = null;
        var llmCalled = false;
        llm.On(PromptNames.DedupeEdgesResolveEdge, call =>
        {
            llmCalled = true;
            var body = call.Messages[^1].Content;
            var existing = ParseList(body, "EXISTING FACTS");
            var invalidation = ParseList(body, "FACT INVALIDATION CANDIDATES");
            var newFactMatch = Regex.Match(body, @"<NEW FACT>\s*(.*?)\s*</NEW FACT>", RegexOptions.Singleline);
            var newFact = newFactMatch.Success ? newFactMatch.Groups[1].Value : fact;
            existingShown = existing.Select(e => e.Fact).ToList();
            invalidationShown = invalidation.Select(e => e.Fact).ToList();
            var duplicates = existing.Where(e => Jaccard(e.Fact, newFact) >= 0.8).Select(e => e.Idx).ToArray();
            var contradicted = existing.Concat(invalidation)
                .Where(e => Jaccard(e.Fact, newFact) is >= 0.4 and < 0.8)
                .Select(e => e.Idx)
                .ToArray();
            return new { duplicate_facts = duplicates, contradicted_facts = contradicted };
        });

        var extracted = new EntityEdge
        {
            SourceNodeUuid = src,
            TargetNodeUuid = tgt,
            Name = "RELATES_TO",
            GroupId = "g1",
            Fact = fact,
            CreatedAt = Now,
            ValidAt = Now, // so timestamp extraction skips its LLM call
        };
        var episode = new EpisodicNode
        {
            Uuid = "ep1",
            Name = "ep",
            GroupId = "g1",
            Source = EpisodeType.Message,
            SourceDescription = "",
            Content = "",
            ValidAt = Now,
        };
        var nodes = scenario.Nodes
            .Select(u => new EntityNode { Uuid = u, Name = u, GroupId = "g1", Labels = ["Entity"] })
            .ToList();

        var (resolved, invalidated, _) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [extracted], episode, nodes, EdgeTestData.NoTypes, EdgeTestData.EmptyMap);

        var searches = driver.SearchFiltersUsed
            .Select(f => f.EdgeSourceNodeUuids is not null || f.EdgeTargetNodeUuids is not null
                ? "pair-filtered"
                : f.EdgeUuids is not null ? "filtered" : "graph-wide")
            .ToList();
        var result = resolved[0];
        return new Report(
            searches,
            existingShown,
            invalidationShown,
            llmCalled,
            result.Uuid,
            extracted.Uuid,
            (result.SourceNodeUuid, result.TargetNodeUuid) != (src, tgt),
            invalidated.Count);
    }

    [Fact]
    public async Task CrossEndpointIdenticalTextIsNotMerged()
    {
        var report = await RunScenarioAsync(new Scenario(
            [MakeEdge("E1", "dan_jones", "yoga", "Dan likes yoga")],
            ("dan_smith", "yoga", "Dan likes yoga"),
            ["dan_jones", "dan_smith", "yoga"]));

        Assert.Equal(["pair-filtered", "graph-wide"], report.Searches);
        Assert.Empty(report.ExistingFacts!);
        Assert.False(report.MergedCrossEndpoint);
        Assert.Equal(report.ExtractedUuid, report.ResolvedUuid);
        // The similar fact still reaches the LLM, properly labelled.
        Assert.Equal(["Dan likes yoga"], report.InvalidationCandidates!);
    }

    [Fact]
    public async Task SamePairDuplicateFastPathOneSearch()
    {
        var report = await RunScenarioAsync(new Scenario(
            [MakeEdge("E2", "user", "yoga", "User likes yoga")],
            ("user", "yoga", "  user LIKES yoga "),
            ["user", "yoga"]));

        Assert.Equal("E2", report.ResolvedUuid);
        Assert.False(report.LlmCalled);
        Assert.Equal(["pair-filtered", "graph-wide"], report.Searches);
    }

    [Fact]
    public async Task ReverseDirectionDuplicateIsCaught()
    {
        var report = await RunScenarioAsync(new Scenario(
            [MakeEdge("E3", "bob", "alice", "Alice manages Bob")],
            ("alice", "bob", "Alice manages Bob"),
            ["alice", "bob"]));

        Assert.Equal("E3", report.ResolvedUuid);
        Assert.Equal(["pair-filtered", "graph-wide"], report.Searches);
    }

    [Fact]
    public async Task ContradictionOnNewPairReachesInvalidationList()
    {
        var report = await RunScenarioAsync(new Scenario(
            [MakeEdge("E4", "alice", "acme", "Alice works at Acme")],
            ("alice", "betacorp", "Alice works at BetaCorp"),
            ["alice", "acme", "betacorp"]));

        Assert.Equal(["Alice works at Acme"], report.InvalidationCandidates!);
        Assert.Empty(report.ExistingFacts!);
        Assert.Equal(1, report.Invalidated);
        Assert.False(report.MergedCrossEndpoint);
    }

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();

    [GeneratedRegex(@"'idx': (\d+), 'fact': '([^']*)'")]
    private static partial Regex FactEntry();
}
