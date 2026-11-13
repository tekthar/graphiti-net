// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_bulk_utils.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using static GraphitiNet.Tests.Maintenance.EdgeTestData;

namespace GraphitiNet.Tests.Maintenance;

/// <summary>
/// The batch functions of <see cref="BulkUtils"/>. Upstream monkeypatches the per-episode resolvers; here they are
/// passed to the internal overloads, or the fake LLM answers the prompts.
/// </summary>
public class BulkUtilsTests
{
    private static readonly IReadOnlyList<EpisodicNode> NoEpisodes = [];

    private static readonly int[] FirstCandidate = [0];

    private static EpisodicNode MakeEpisode(string suffix, string groupId = "group", DateTimeOffset? validAt = null) => new()
    {
        Name = $"episode-{suffix}",
        GroupId = groupId,
        Labels = [],
        Source = EpisodeType.Message,
        Content = "content",
        SourceDescription = "test",
        CreatedAt = DateTimeOffset.UtcNow,
        ValidAt = validAt ?? DateTimeOffset.UtcNow,
    };

    private static EntityNode MakeNode(string name, string? uuid = null) => new()
    {
        Uuid = uuid ?? Guid.NewGuid().ToString(),
        Name = name,
        GroupId = "group",
        Labels = ["Entity"],
    };

    private static BulkUtils.ExtractedNodesResolver Resolver(
        Func<IReadOnlyList<EntityNode>, (IReadOnlyList<EntityNode>, IReadOnlyDictionary<string, string>,
            IReadOnlyList<(EntityNode Extracted, EntityNode Resolved)>)> resolve,
        List<IReadOnlyList<EntityNode>>? calls = null) =>
        (_, nodes, _, _, _, _) =>
        {
            lock (resolve)
            {
                calls?.Add(nodes);
            }

            return Task.FromResult(resolve(nodes));
        };

    // dedupe_nodes_bulk

    [Fact]
    public async Task DedupeNodesBulk_reuses_canonical_nodes()
    {
        var (clients, _, _, _) = Clients();
        var episodeOne = MakeEpisode("1");
        var episodeTwo = MakeEpisode("2");
        var extractedOne = MakeNode("Alice Smith");
        var extractedTwo = MakeNode("Alice Smith");
        var canonical = extractedOne;

        var resolver = Resolver(nodes =>
        {
            if (nodes.SequenceEqual([extractedOne]))
            {
                return ([canonical], new Dictionary<string, string> { [canonical.Uuid] = canonical.Uuid }, []);
            }

            Assert.Equal([extractedTwo], nodes);
            return ([canonical], new Dictionary<string, string> { [extractedTwo.Uuid] = canonical.Uuid },
                [(extractedTwo, canonical)]);
        });

        var (nodesByEpisode, compressedMap) = await BulkUtils.DedupeNodesBulkAsync(
            clients, [[extractedOne], [extractedTwo]], [(episodeOne, NoEpisodes), (episodeTwo, NoEpisodes)], null,
            resolver, CancellationToken.None);

        Assert.Equal([canonical], nodesByEpisode[episodeOne.Uuid]);
        Assert.Equal([canonical], nodesByEpisode[episodeTwo.Uuid]);
        Assert.Equal(canonical.Uuid, compressedMap[extractedTwo.Uuid]);
    }

    [Fact]
    public async Task DedupeNodesBulk_handles_an_empty_batch()
    {
        var (clients, _, _, _) = Clients();
        var calls = new List<IReadOnlyList<EntityNode>>();

        var (nodesByEpisode, compressedMap) = await BulkUtils.DedupeNodesBulkAsync(
            clients, [], [], null, Resolver(_ => throw new InvalidOperationException(), calls),
            CancellationToken.None);

        Assert.Empty(nodesByEpisode);
        Assert.Empty(compressedMap);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task DedupeNodesBulk_single_episode()
    {
        var (clients, _, _, _) = Clients();
        var episode = MakeEpisode("solo");
        var extracted = MakeNode("Solo");
        var calls = new List<IReadOnlyList<EntityNode>>();

        var (nodesByEpisode, compressedMap) = await BulkUtils.DedupeNodesBulkAsync(
            clients, [[extracted]], [(episode, NoEpisodes)], null,
            Resolver(_ => ([extracted], new Dictionary<string, string> { [extracted.Uuid] = extracted.Uuid }, []), calls),
            CancellationToken.None);

        Assert.Equal([episode.Uuid], nodesByEpisode.Keys);
        Assert.Equal([extracted], nodesByEpisode[episode.Uuid]);
        Assert.Equal(new Dictionary<string, string> { [extracted.Uuid] = extracted.Uuid }, compressedMap.ToDictionary());
        Assert.Single(calls);
    }

    [Fact]
    public async Task DedupeNodesBulk_uuid_map_respects_direction()
    {
        var (clients, _, _, _) = Clients();
        var episodeOne = MakeEpisode("one");
        var episodeTwo = MakeEpisode("two");
        var extractedOne = MakeNode("Edge Case", "b-uuid");
        var extractedTwo = MakeNode("Edge Case", "a-uuid");
        var canonical = extractedOne;
        var alias = extractedTwo;

        var resolver = Resolver(nodes =>
        {
            if (nodes.SequenceEqual([extractedOne]))
            {
                return ([canonical], new Dictionary<string, string> { [canonical.Uuid] = canonical.Uuid }, []);
            }

            Assert.Equal([extractedTwo], nodes);
            return ([canonical], new Dictionary<string, string> { [alias.Uuid] = canonical.Uuid }, [(alias, canonical)]);
        });

        var (nodesByEpisode, compressedMap) = await BulkUtils.DedupeNodesBulkAsync(
            clients, [[extractedOne], [extractedTwo]], [(episodeOne, NoEpisodes), (episodeTwo, NoEpisodes)], null,
            resolver, CancellationToken.None);

        Assert.Equal([canonical], nodesByEpisode[episodeOne.Uuid]);
        Assert.Equal([canonical], nodesByEpisode[episodeTwo.Uuid]);
        Assert.Equal(canonical.Uuid, compressedMap[alias.Uuid]);
    }

    [Fact]
    public async Task DedupeNodesBulk_missing_canonical_falls_back()
    {
        var logger = new ListLogger();
        var (baseClients, _, _, _) = Clients();
        var clients = baseClients with { Logger = logger };
        var episode = MakeEpisode("missing");
        var extracted = MakeNode("Fallback");

        var (nodesByEpisode, compressedMap) = await BulkUtils.DedupeNodesBulkAsync(
            clients, [[extracted]], [(episode, NoEpisodes)], null,
            Resolver(_ => ([extracted], new Dictionary<string, string> { [extracted.Uuid] = "missing-canonical" }, [])),
            CancellationToken.None);

        Assert.Equal([extracted], nodesByEpisode[episode.Uuid]);
        Assert.Equal("missing-canonical", compressedMap[extracted.Uuid]);
        Assert.Contains(logger.Entries, e => e.Message.Contains("Canonical node missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DedupeNodesBulk_merges_exact_name_duplicates_across_episodes()
    {
        var (clients, _, _, _) = Clients();
        var episodeOne = MakeEpisode("1");
        var episodeTwo = MakeEpisode("2");
        var first = MakeNode("Acme Robotics International");
        var second = MakeNode("Acme Robotics International Ltd");
        var third = MakeNode("acme robotics international");

        var (nodesByEpisode, compressedMap) = await BulkUtils.DedupeNodesBulkAsync(
            clients, [[first], [second, third]], [(episodeOne, NoEpisodes), (episodeTwo, NoEpisodes)], null,
            Resolver(nodes => (nodes, nodes.ToDictionary(n => n.Uuid, n => n.Uuid), [])), CancellationToken.None);

        // The exact (normalised) name match collapses onto the first node.
        Assert.Equal(first.Uuid, compressedMap[third.Uuid]);
        Assert.Equal([first], nodesByEpisode[episodeOne.Uuid]);
        Assert.Contains(first, nodesByEpisode[episodeTwo.Uuid]);
    }

    // dedupe_edges_bulk

    private static Task SetEmbeddings(IReadOnlyList<EntityEdge> edges, CancellationToken _)
    {
        foreach (var edge in edges)
        {
            edge.FactEmbedding = [0.1f, 0.2f, 0.3f];
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DedupeEdgesBulk_deduplicates_within_an_episode()
    {
        var (clients, _, _, _) = Clients();
        var comparisons = new List<(string Edge, List<string> Against)>();
        BulkUtils.ExtractedEdgeResolver resolver = (_, extracted, related, _, _, _, _) =>
        {
            lock (comparisons)
            {
                comparisons.Add((extracted.Uuid, related.Select(r => r.Uuid).ToList()));
            }

            foreach (var candidate in related)
            {
                if (candidate.Uuid != extracted.Uuid
                    && candidate.SourceNodeUuid == extracted.SourceNodeUuid
                    && candidate.TargetNodeUuid == extracted.TargetNodeUuid
                    && string.Equals(candidate.Fact.Trim(), extracted.Fact.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<(EntityEdge, IReadOnlyList<EntityEdge>, IReadOnlyList<EntityEdge>)>(
                        (candidate, [], [candidate]));
                }
            }

            return Task.FromResult<(EntityEdge, IReadOnlyList<EntityEdge>, IReadOnlyList<EntityEdge>)>(
                (extracted, [], []));
        };

        var episode = MakeEpisode("1");
        var edges = Enumerable.Range(0, 3)
            .Select(_ => Edge("source-uuid", "target-uuid", "assistant recommends yoga poses", "recommends", "group",
                episodes: [episode.Uuid]))
            .ToList();

        var edgesByEpisode = await BulkUtils.DedupeEdgesBulkAsync(
            clients, [edges], [(episode, NoEpisodes)], NoTypes, SetEmbeddings, resolver, CancellationToken.None);

        Assert.Equal(3, comparisons.Count);
        Assert.All(comparisons, c => Assert.True(c.Against.Count >= 2));
        Assert.All(comparisons, c => Assert.DoesNotContain(c.Edge, c.Against));

        // All three collapse onto the smallest id.
        var smallest = edges.Select(e => e.Uuid).Min(StringComparer.Ordinal);
        Assert.All(edgesByEpisode[episode.Uuid], e => Assert.Equal(smallest, e.Uuid));
    }

    [Fact]
    public async Task DedupeEdgesBulk_merges_episode_provenance_for_duplicates()
    {
        var (clients, _, _, _) = Clients();
        var episodeOne = MakeEpisode("1");
        var episodeTwo = MakeEpisode("2");
        BulkUtils.ExtractedEdgeResolver resolver = (_, extracted, related, _, _, _, _) =>
            Task.FromResult<(EntityEdge, IReadOnlyList<EntityEdge>, IReadOnlyList<EntityEdge>)>(
                (extracted, [], related.Where(r => r.Uuid != extracted.Uuid && r.Fact == extracted.Fact).ToList()));

        var edgeOne = Edge("assistant", "yoga", "Assistant recommends yoga", "recommends", "group", "edge-a",
            [episodeOne.Uuid]);
        var edgeTwo = Edge("assistant", "yoga", "Assistant recommends yoga", "recommends", "group", "edge-b",
            [episodeTwo.Uuid]);

        var edgesByEpisode = await BulkUtils.DedupeEdgesBulkAsync(
            clients, [[edgeOne], [edgeTwo]], [(episodeOne, NoEpisodes), (episodeTwo, NoEpisodes)], NoTypes,
            SetEmbeddings, resolver, CancellationToken.None);

        var canonicalOne = edgesByEpisode[episodeOne.Uuid][0];
        var canonicalTwo = edgesByEpisode[episodeTwo.Uuid][0];
        Assert.Same(canonicalOne, canonicalTwo);
        Assert.Equal("edge-a", canonicalOne.Uuid);
        Assert.Equal([episodeOne.Uuid, episodeTwo.Uuid], canonicalOne.Episodes);
    }

    [Fact]
    public async Task DedupeEdgesBulk_takes_candidates_only_between_the_same_endpoints_by_words_or_similarity()
    {
        var (clients, _, _, _) = Clients();
        var episode = MakeEpisode("1");
        var related = new Dictionary<string, List<string>>();
        BulkUtils.ExtractedEdgeResolver resolver = (_, extracted, candidates, existing, _, _, _) =>
        {
            Assert.Same(candidates, existing);
            lock (related)
            {
                related[extracted.Fact] = candidates.Select(c => c.Fact).ToList();
            }

            return Task.FromResult<(EntityEdge, IReadOnlyList<EntityEdge>, IReadOnlyList<EntityEdge>)>(
                (extracted, [], []));
        };

        var sharesAWord = Edge("a", "b", "Alice LIKES tea");
        var sameWordOtherCase = Edge("a", "b", "alice drinks coffee");
        var noOverlap = Edge("a", "b", "Unrelated statement");
        var otherPair = Edge("b", "a", "Alice likes tea");
        Task Embed(IReadOnlyList<EntityEdge> edges, CancellationToken _)
        {
            foreach (var edge in edges)
            {
                // Orthogonal vectors: only word overlap can make a candidate.
                edge.FactEmbedding = edge == noOverlap ? [1f, 0f] : [0f, 1f];
            }

            return Task.CompletedTask;
        }

        await BulkUtils.DedupeEdgesBulkAsync(clients, [[sharesAWord, sameWordOtherCase, noOverlap, otherPair]],
            [(episode, NoEpisodes)], NoTypes, Embed, resolver, CancellationToken.None);

        Assert.Equal(["alice drinks coffee"], related["Alice LIKES tea"]);
        Assert.Equal(["Alice LIKES tea"], related["alice drinks coffee"]);
        Assert.Empty(related["Unrelated statement"]);
        Assert.Empty(related["Alice likes tea"]);
    }

    [Fact]
    public async Task DedupeEdgesBulk_uses_the_embedder_and_the_resolve_edge_prompt()
    {
        var (clients, llm, _, embedder) = Clients();
        llm.On(PromptNames.DedupeEdgesResolveEdge, new { duplicate_facts = FirstCandidate, contradicted_facts = Array.Empty<int>() });
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var episodeOne = MakeEpisode("1");
        var episodeTwo = MakeEpisode("2");
        var edgeOne = Edge("a", "b", "Alice works at Acme", uuid: "edge-1", episodes: [episodeOne.Uuid]);
        var edgeTwo = Edge("a", "b", "Alice is employed at Acme", uuid: "edge-2", episodes: [episodeTwo.Uuid]);

        var edgesByEpisode = await BulkUtils.DedupeEdgesBulkAsync(
            clients, [[edgeOne], [edgeTwo]], [(episodeOne, NoEpisodes), (episodeTwo, NoEpisodes)], [], NoTypes,
            EmptyMap);

        Assert.Equal(2, embedder.BatchCalls);
        Assert.NotNull(edgeOne.FactEmbedding);
        Assert.Equal(2, llm.CallsFor(PromptNames.DedupeEdgesResolveEdge).Count);
        Assert.Same(edgeOne, edgesByEpisode[episodeTwo.Uuid][0]);
        Assert.Equal([episodeOne.Uuid, episodeTwo.Uuid], edgeOne.Episodes);
    }

    [Fact]
    public async Task DedupeEdgesBulk_keeps_verbatim_repeats_apart_as_upstream()
    {
        // resolve_extracted_edge's verbatim fast path returns the matching fact but no duplicates, so upstream's
        // batch dedupe records no pair: both facts stay, each gaining the other's episode.
        var (clients, llm, _, _) = Clients();
        var episodeOne = MakeEpisode("1");
        var episodeTwo = MakeEpisode("2");
        var edgeOne = Edge("a", "b", "Alice works at Acme", uuid: "edge-1", episodes: [episodeOne.Uuid]);
        var edgeTwo = Edge("a", "b", "Alice works at Acme", uuid: "edge-2", episodes: [episodeTwo.Uuid]);

        var edgesByEpisode = await BulkUtils.DedupeEdgesBulkAsync(
            clients, [[edgeOne], [edgeTwo]], [(episodeOne, NoEpisodes), (episodeTwo, NoEpisodes)], [], NoTypes,
            EmptyMap);

        Assert.Empty(llm.Calls);
        Assert.Same(edgeOne, edgesByEpisode[episodeOne.Uuid][0]);
        Assert.Same(edgeTwo, edgesByEpisode[episodeTwo.Uuid][0]);
        Assert.Equal([episodeOne.Uuid, episodeTwo.Uuid], edgeOne.Episodes);
        Assert.Equal([episodeTwo.Uuid, episodeOne.Uuid], edgeTwo.Episodes);
    }

    // extract_nodes_and_edges_bulk

    private static object EmptyCombined => new { extracted_entities = Array.Empty<object>(), edges = Array.Empty<object>() };

    [Description("Knows another person.")]
    private sealed class Knows;

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_passes_custom_instructions_to_combined_extraction()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesAndEdgesExtractMessage, EmptyCombined);
        const string instructions = "Focus on extracting person entities and their relationships.";

        await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients, [(MakeEpisode("1"), NoEpisodes)], EmptyMap,
            customExtractionInstructions: instructions, useCombinedExtraction: true);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage));
        Assert.Contains(instructions, call.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_passes_the_edge_ontology_to_combined_extraction()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesAndEdgesExtractMessage, EmptyCombined);
        const string instructions = "Extract only professional relationships between people.";
        var edgeTypeMap = new Dictionary<(string Source, string Target), IReadOnlyList<string>>
        {
            [("Entity", "Entity")] = ["knows"],
        };

        await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients, [(MakeEpisode("1"), NoEpisodes)], edgeTypeMap,
            edgeTypes: new Dictionary<string, Type> { ["knows"] = typeof(Knows) }, strictEdgeTypes: true,
            customExtractionInstructions: instructions, useCombinedExtraction: true);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage));
        Assert.Contains(instructions, call.Text, StringComparison.Ordinal);
        Assert.Contains("\"fact_type_name\": \"knows\"", call.Text, StringComparison.Ordinal);
        Assert.Contains("Knows another person.", call.Text, StringComparison.Ordinal);
        Assert.Contains("If a relationship does not match any FACT TYPE, skip it", call.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_passes_custom_instructions_to_extract_nodes()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesExtractMessage, new { extracted_entities = Array.Empty<object>() });
        llm.On(PromptNames.ExtractEdgesEdge, new { edges = Array.Empty<object>() });
        const string instructions = "Focus on extracting person entities and their relationships.";

        await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients, [(MakeEpisode("1"), NoEpisodes)], EmptyMap,
            customExtractionInstructions: instructions, useCombinedExtraction: false);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractNodesExtractMessage));
        Assert.Contains(instructions, call.Text, StringComparison.Ordinal);
        Assert.Empty(llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage));
    }

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_passes_custom_instructions_to_extract_edges()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesExtractMessage,
            new { extracted_entities = new[] { new { name = "Test", entity_type_id = 0 } } });
        llm.On(PromptNames.ExtractEdgesEdge, new { edges = Array.Empty<object>() });
        const string instructions = "Extract only professional relationships between people.";
        var edgeTypeMap = new Dictionary<(string Source, string Target), IReadOnlyList<string>>
        {
            [("Entity", "Entity")] = ["knows"],
        };

        var (nodes, edges) = await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients, [(MakeEpisode("1"), NoEpisodes)],
            edgeTypeMap, edgeTypes: new Dictionary<string, Type> { ["knows"] = typeof(Knows) },
            customExtractionInstructions: instructions, useCombinedExtraction: false);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractEdgesEdge));
        Assert.Contains(instructions, call.Text, StringComparison.Ordinal);
        Assert.Contains("\"fact_type_name\": \"knows\"", call.Text, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Test\"", call.Text, StringComparison.Ordinal);
        Assert.Equal("Test", Assert.Single(Assert.Single(nodes)).Name);
        Assert.Empty(Assert.Single(edges));
    }

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_defaults_to_combined_extraction()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesAndEdgesExtractMessage, EmptyCombined);

        await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients, [(MakeEpisode("1"), NoEpisodes)], EmptyMap);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage));
        Assert.DoesNotContain("<CUSTOM INSTRUCTIONS>", call.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_passes_custom_instructions_for_every_episode()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesAndEdgesExtractMessage, EmptyCombined);
        const string instructions = "Extract entities related to financial transactions.";

        var (nodes, edges) = await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients,
            [(MakeEpisode("1"), NoEpisodes), (MakeEpisode("2"), NoEpisodes), (MakeEpisode("3"), NoEpisodes)],
            EmptyMap, customExtractionInstructions: instructions, useCombinedExtraction: true);

        var calls = llm.CallsFor(PromptNames.ExtractNodesAndEdgesExtractMessage);
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Contains(instructions, call.Text, StringComparison.Ordinal));
        Assert.Equal(3, nodes.Count);
        Assert.Equal(3, edges.Count);
    }

    [Fact]
    public async Task ExtractNodesAndEdgesBulk_separate_appends_materialized_endpoints_as_upstream()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractNodesExtractMessage,
            new { extracted_entities = new[] { new { name = "Alice", entity_type_id = 0 } } });
        llm.On(PromptNames.ExtractEdgesEdge, new
        {
            edges = new[]
            {
                new
                {
                    source_entity_name = "Alice", target_entity_name = "Acme", relation_type = "WORKS_AT",
                    fact = "Alice works at Acme.", valid_at = (string?)null, invalid_at = (string?)null,
                },
            },
        });

        var (nodes, edges) = await BulkUtils.ExtractNodesAndEdgesBulkAsync(clients, [(MakeEpisode("1"), NoEpisodes)],
            EmptyMap, useCombinedExtraction: false);

        // extract_edges adds "Acme" to the node list and the bulk function adds it once more, as upstream.
        Assert.Equal(["Alice", "Acme", "Acme"], Assert.Single(nodes).Select(n => n.Name));
        Assert.Single(Assert.Single(edges));
    }

    // retrieve_previous_episodes_bulk

    [Fact]
    public async Task RetrievePreviousEpisodesBulk_returns_each_episodes_window_in_its_group()
    {
        await using var driver = new InMemoryGraphDriver();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var saved = Enumerable.Range(0, 5)
            .Select(i => MakeEpisode($"saved-{i}", i % 2 == 0 ? "g1" : "g2", t0.AddHours(i)))
            .ToList();
        await driver.AddNodesAndEdgesBulkAsync(saved, [], [], []);

        var late = MakeEpisode("late", "g1", t0.AddDays(1));
        var early = MakeEpisode("early", "g2", t0.AddMinutes(30));

        var result = await BulkUtils.RetrievePreviousEpisodesBulkAsync(driver, [late, early]);

        Assert.Same(late, result[0].Episode);
        Assert.Equal(["episode-saved-0", "episode-saved-2", "episode-saved-4"], result[0].PreviousEpisodes.Select(e => e.Name));
        Assert.Same(early, result[1].Episode);
        Assert.Empty(result[1].PreviousEpisodes);
    }
}
