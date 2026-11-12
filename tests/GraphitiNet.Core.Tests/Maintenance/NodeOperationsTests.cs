// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_node_operations.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Utils;
using Microsoft.Extensions.Logging;
using static GraphitiNet.Tests.Maintenance.TestHarness;

namespace GraphitiNet.Tests.Maintenance;

public class NodeOperationsTests
{
    private const string NoDuplicate = """{"entity_resolutions": [{"id": 0, "name": "x", "duplicate_candidate_id": -1}]}""";

    [Fact]
    public async Task Semantic_candidate_search_skips_blank_names()
    {
        var h = new TestHarness();
        var nodes = new[] { Entity("Alice"), Entity(""), Entity("Bob") };

        var result = await NodeOperations.SemanticCandidateSearchAsync(h.Clients, nodes);

        Assert.Equal(3, result.Count);
        Assert.Empty(result[1]);
        Assert.Equal(["Alice", "Bob"], h.Embedder.Inputs);
    }

    [Fact]
    public async Task Semantic_candidate_search_raises_on_vector_count_mismatch()
    {
        var h = new TestHarness();
        h.Embedder.BatchOverride = _ => [[0.1f, 0.2f, 0.3f]];

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NodeOperations.SemanticCandidateSearchAsync(h.Clients, [Entity("Alice"), Entity("Bob")]));
        Assert.Contains("vectors", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_candidate_search_falls_back_to_single_embeddings()
    {
        var h = new TestHarness();
        h.Embedder.BatchOverride = _ => throw new NotSupportedException();
        var stored = Entity("Alice");
        await h.StoreAsync(stored);

        var result = await NodeOperations.SemanticCandidateSearchAsync(h.Clients, [Entity("Alice")]);

        Assert.Equal([stored.Uuid], result[0].Select(n => n.Uuid));
    }

    [Fact]
    public async Task Resolve_nodes_exact_match_skips_llm()
    {
        var h = new TestHarness();
        var candidate = Entity("Joe Michaels");
        await h.StoreAsync(candidate);
        var extracted = Entity("Joe Michaels");

        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [extracted], Episode(), []);

        Assert.Equal(candidate.Uuid, resolved[0].Uuid);
        Assert.Equal(candidate.Uuid, uuidMap[extracted.Uuid]);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_exact_match_promotes_generic_candidate_type()
    {
        var h = new TestHarness();
        var candidate = Entity("Audrey");
        await h.StoreAsync(candidate);
        var extracted = Entity("Audrey", labels: ["Entity", "Person"]);

        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [extracted], Episode(), []);

        Assert.Equal(candidate.Uuid, resolved[0].Uuid);
        Assert.Equal(["Entity", "Person"], resolved[0].Labels);
        Assert.Equal(candidate.Uuid, uuidMap[extracted.Uuid]);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_identity_properties_match_before_llm()
    {
        var candidate = Entity("ACME", labels: ["Entity", "Company"],
            attributes: new() { ["domain"] = "acme.example", ["size"] = "enterprise" });
        var driver = new AttributeLookupDriver(new InMemoryGraphDriver(), (_, _, _) => [candidate]);
        var h = new TestHarness(driver: driver);
        var extracted = Entity("ACME Incorporated", labels: ["Entity", "Company"],
            attributes: new() { ["domain"] = "acme.example", ["size"] = null, ["region"] = "us-west" });

        var (resolved, uuidMap, duplicates) = await NodeOperations.ResolveExtractedNodesAsync(
            h.Clients, [extracted], Episode(), [],
            identityProperties: new Dictionary<string, IReadOnlyList<string>> { ["Company"] = ["domain"] });

        Assert.Equal(candidate.Uuid, resolved[0].Uuid);
        Assert.Equal(
            new Dictionary<string, object?> { ["domain"] = "acme.example", ["size"] = "enterprise", ["region"] = "us-west" },
            resolved[0].Attributes);
        Assert.Equal(candidate.Uuid, uuidMap[extracted.Uuid]);
        Assert.Equal([(extracted, candidate)], duplicates);
        Assert.Empty(h.Llm.Calls);
        var lookup = Assert.Single(driver.Lookups);
        Assert.Equal("group", lookup.GroupId);
        Assert.Equal(["Company"], lookup.Labels!);
        Assert.Equal(NodeOperations.NodeDedupCandidateLimit, lookup.Limit);
        Assert.Equal("acme.example", lookup.Attributes["domain"]);
    }

    [Fact]
    public async Task Identity_lookup_is_skipped_when_driver_does_not_support_it()
    {
        var h = new TestHarness();
        var extracted = Entity("ACME", labels: ["Entity", "Company"], attributes: new() { ["domain"] = "acme.example" });

        var result = await NodeOperations.CollectIdentityPropertyCandidateNodesAsync(
            h.Clients, [extracted], new Dictionary<string, IReadOnlyList<string>> { ["Company"] = ["domain"] });

        Assert.Empty(Assert.Single(result));
    }

    [Fact]
    public async Task Resolve_nodes_low_entropy_uses_llm()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes, NoDuplicate);
        await h.StoreAsync(Entity("Joseph"));
        var extracted = Entity("Joe");

        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [extracted], Episode(), []);

        Assert.Equal(extracted.Uuid, resolved[0].Uuid);
        Assert.Equal(extracted.Uuid, uuidMap[extracted.Uuid]);
        Assert.Single(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_short_name_exact_match_skips_llm()
    {
        var h = new TestHarness();
        var candidate = Entity("Java");
        await h.StoreAsync(candidate);

        var (resolved, _, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [Entity("Java")], Episode(), []);

        Assert.Equal(candidate.Uuid, resolved[0].Uuid);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_fuzzy_match()
    {
        var h = new TestHarness();
        var candidate = Entity("Joe-Michaels");
        await h.StoreAsync(candidate);

        var (resolved, _, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [Entity("Joe Michaels")], Episode(), []);

        Assert.Equal(candidate.Uuid, resolved[0].Uuid);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Collect_candidate_nodes_prepends_exact_name_candidates()
    {
        var h = new TestHarness();
        h.Embedder.Vectors["Alice"] = [1f, 0f, 0f];
        h.Embedder.Vectors["Alice Smith"] = [1f, 0f, 0f];
        var exact = Entity("Alice");
        exact.NameEmbedding = [0f, 1f, 0f]; // not similar: found only by name
        var semantic = Entity("Alice Smith");
        await h.StoreAsync(exact, semantic);

        var result = await NodeOperations.CollectCandidateNodesAsync(h.Clients, [Entity("Alice")]);

        Assert.Equal([exact.Uuid, semantic.Uuid], Assert.Single(result).Select(n => n.Uuid));
    }

    [Fact]
    public async Task Collect_candidate_nodes_dedupes_and_merges_override()
    {
        var h = new TestHarness();
        var candidate = Entity("Alice");
        await h.StoreAsync(candidate);
        var overrideDuplicate = Entity("Alice Alt", uuid: candidate.Uuid);

        var result = await NodeOperations.CollectCandidateNodesAsync(h.Clients, [Entity("Alice")], [overrideDuplicate]);

        var only = Assert.Single(Assert.Single(result));
        Assert.Equal(candidate.Uuid, only.Uuid);
        Assert.Equal("Alice", only.Name);
    }

    [Fact]
    public async Task Collect_exact_name_candidate_nodes_scopes_by_group()
    {
        var h = new TestHarness();
        var a = Entity("Alice", groupId: "group-a");
        var b = Entity("Alice", groupId: "group-b");
        await h.StoreAsync(a, b);

        var result = await NodeOperations.CollectExactNameCandidateNodesAsync(
            h.Clients, [Entity("Alice", groupId: "group-a"), Entity("Alice", groupId: "group-b")]);
        Assert.Equal([a.Uuid], result[0].Select(n => n.Uuid));
        Assert.Equal([b.Uuid], result[1].Select(n => n.Uuid));
    }

    [Fact]
    public async Task Resolve_nodes_exact_name_lookup_handles_semantic_miss()
    {
        var h = new TestHarness();
        var candidate = Entity("Audrey");
        candidate.NameEmbedding = [0f, 1f, 0f];
        await h.StoreAsync(candidate);
        var extracted = Entity("Audrey", labels: ["Entity", "Person"]);

        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [extracted], Episode(), []);

        Assert.Equal(candidate.Uuid, resolved[0].Uuid);
        Assert.Equal(["Entity", "Person"], resolved[0].Labels);
        Assert.Equal(candidate.Uuid, uuidMap[extracted.Uuid]);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_semantic_miss_keeps_node_without_llm()
    {
        var h = new TestHarness();
        var extracted = Entity("Completely New Thing");

        var (resolved, uuidMap, duplicates) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [extracted], Episode(), []);

        Assert.Equal(extracted.Uuid, resolved[0].Uuid);
        Assert.Equal(extracted.Uuid, uuidMap[extracted.Uuid]);
        Assert.Empty(duplicates);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_multiple_exact_matches_use_llm()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 0, "name": "Java", "duplicate_candidate_id": 0}]}""");
        var candidate1 = Entity("Java");
        var candidate2 = Entity("Java");
        await h.StoreAsync(candidate1, candidate2);
        var extracted = Entity("Java");

        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, [extracted], Episode(), []);

        Assert.Equal(candidate1.Uuid, resolved[0].Uuid);
        Assert.Equal(candidate1.Uuid, uuidMap[extracted.Uuid]);
        Assert.Single(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_nodes_batches_unresolved_nodes_into_one_llm_call()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes, """
            {"entity_resolutions": [
                {"id": 0, "name": "Joe", "duplicate_candidate_id": -1},
                {"id": 1, "name": "Java", "duplicate_candidate_id": 1}]}
            """);
        h.Embedder.Vectors["Joe"] = [1f, 0f, 0f];
        h.Embedder.Vectors["Joseph"] = [1f, 0f, 0f];
        h.Embedder.Vectors["Java"] = [0f, 1f, 0f];
        var joseph = Entity("Joseph");
        var java1 = Entity("Java");
        var java2 = Entity("Java");
        await h.StoreAsync(joseph, java1, java2);
        var extracted = new[] { Entity("Joe"), Entity("Java") };

        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(h.Clients, extracted, Episode(), []);

        Assert.Equal(extracted[0].Uuid, resolved[0].Uuid);
        Assert.Equal(java1.Uuid, resolved[1].Uuid);
        Assert.Equal(extracted[0].Uuid, uuidMap[extracted[0].Uuid]);
        Assert.Equal(java1.Uuid, uuidMap[extracted[1].Uuid]);
        Assert.Single(h.Llm.Calls);
    }

    [Fact]
    public async Task Resolve_with_llm_candidate_attributes_cannot_overwrite_candidate_id()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 0, "name": "Dizzy Gillespie", "duplicate_candidate_id": 0}]}""");
        var candidate = Entity("Dizzy Gillespie", attributes: new() { ["candidate_id"] = 999L, ["genre"] = "jazz" });
        var extracted = Entity("Dizzy");
        var state = State(1, unresolved: [0]);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state, Episode(), [], null);

        var existing = (IReadOnlyList<Dictionary<string, object?>>)h.Prompts.Last(PromptNames.DedupeNodesNodes)["existing_nodes"]!;
        Assert.Equal(0, existing[0]["candidate_id"]);
        Assert.Equal("jazz", existing[0]["genre"]);
        Assert.Equal(candidate.Uuid, state.ResolvedNodes[0]!.Uuid);
    }

    [Fact]
    public async Task Resolve_with_llm_updates_unresolved()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 0, "name": "Dizzy Gillespie", "duplicate_candidate_id": 0}]}""");
        var extracted = Entity("Dizzy");
        var candidate = Entity("Dizzy Gillespie");
        var state = State(1, unresolved: [0]);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state, Episode(), [], null);

        Assert.Equal(candidate.Uuid, state.ResolvedNodes[0]!.Uuid);
        Assert.Equal(candidate.Uuid, state.UuidMap[extracted.Uuid]);
        var context = h.Prompts.Last(PromptNames.DedupeNodesNodes);
        var existing = (IReadOnlyList<Dictionary<string, object?>>)context["existing_nodes"]!;
        Assert.Equal(0, existing[0]["candidate_id"]);
        var extractedContext = (IReadOnlyList<Dictionary<string, object?>>)context["extracted_nodes"]!;
        Assert.Equal("Default Entity Type", extractedContext[0]["entity_type_description"]);
        Assert.Equal([(extracted, candidate)], state.DuplicatePairs);
    }

    [Theory]
    [InlineData(32768, 16384)]
    [InlineData(4096, 4096)]
    [InlineData(0, 16384)]
    public async Task Resolve_with_llm_caps_completion_budget(int clientMaxTokens, int expected)
    {
        var h = new TestHarness(maxTokens: clientMaxTokens);
        h.Llm.Respond(PromptNames.DedupeNodesNodes, NoDuplicate);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [Entity("Dizzy")], DedupHelpers.BuildCandidateIndexes([Entity("Dizzy Gillespie")]),
            State(1, unresolved: [0]), Episode(), [], null);

        Assert.Equal(expected, Assert.Single(h.Llm.Calls).MaxTokens);
    }

    [Fact]
    public async Task Resolve_with_llm_promotes_generic_candidate_type()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 0, "name": "Audrey", "duplicate_candidate_id": 0}]}""");
        var extracted = Entity("Audrey", labels: ["Entity", "Person"]);
        var candidate = Entity("Audrey");
        var state = State(1, unresolved: [0]);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state, Episode(), [], null);

        Assert.Same(candidate, state.ResolvedNodes[0]);
        Assert.Equal(["Entity", "Person"], candidate.Labels);
        Assert.Equal([(extracted, candidate)], state.DuplicatePairs);
    }

    [Fact]
    public async Task Resolve_with_llm_ignores_out_of_range_relative_ids()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 5, "name": "Dexter", "duplicate_candidate_id": -1}]}""");
        var state = State(1, unresolved: [0]);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [Entity("Dexter")], DedupHelpers.BuildCandidateIndexes([]), state, Episode(), [], null);

        Assert.Null(state.ResolvedNodes[0]);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning &&
            e.Message.Contains("Skipping invalid LLM dedupe id 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_with_llm_ignores_duplicate_relative_ids()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes, """
            {"entity_resolutions": [
                {"id": 0, "name": "Dizzy Gillespie", "duplicate_candidate_id": 0},
                {"id": 0, "name": "Dizzy", "duplicate_candidate_id": -1}]}
            """);
        var extracted = Entity("Dizzy");
        var candidate = Entity("Dizzy Gillespie");
        var state = State(1, unresolved: [0]);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state, Episode(), [], null);

        Assert.Equal(candidate.Uuid, state.ResolvedNodes[0]!.Uuid);
        Assert.Equal([(extracted, candidate)], state.DuplicatePairs);
    }

    [Fact]
    public async Task Resolve_with_llm_invalid_candidate_id_defaults_to_extracted()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 0, "name": "Dexter", "duplicate_candidate_id": 999}]}""");
        var extracted = Entity("Dexter");
        var state = State(1, unresolved: [0]);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [extracted], DedupHelpers.BuildCandidateIndexes([]), state, Episode(), [], null);

        Assert.Same(extracted, state.ResolvedNodes[0]);
        Assert.Equal(extracted.Uuid, state.UuidMap[extracted.Uuid]);
        Assert.Empty(state.DuplicatePairs);
    }

    [Fact]
    public async Task Batch_summaries_short_summary_no_llm()
    {
        var h = new TestHarness();
        var node = Entity("Test Node", summary: "Old summary");

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [node], [Episode()], [], null, Empty());

        Assert.Equal("Old summary", node.Summary);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Batch_summaries_callback_skip_summary()
    {
        var h = new TestHarness();
        var node = Entity("Test Node", summary: "Old summary");

        await NodeOperations.ExtractEntitySummariesBatchAsync(
            h.Clients, [node], [Episode()], [], _ => Task.FromResult(false), Empty());

        Assert.Equal("Old summary", node.Summary);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Batch_summaries_selective_callback()
    {
        var h = new TestHarness();
        var user = Entity("User", labels: ["Entity", "User"], summary: "Old");
        var topic = Entity("Topic", labels: ["Entity", "Topic"], summary: "Old");

        await NodeOperations.ExtractEntitySummariesBatchAsync(
            h.Clients, [user, topic], [Episode()], [], n => Task.FromResult(!n.Labels.Contains("User")), Empty());

        Assert.Equal("Old", user.Summary);
        Assert.Equal("Old", topic.Summary);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Extract_attributes_from_nodes_with_callback()
    {
        var h = new TestHarness();
        var node1 = Entity("Node1", labels: ["Entity", "User"], summary: "Old1");
        var node2 = Entity("Node2", labels: ["Entity", "Topic"], summary: "Old2");
        var tracker = new List<string>();

        var results = await NodeOperations.ExtractAttributesFromNodesAsync(
            h.Clients, [node1, node2], [Episode()], [],
            shouldSummarizeNode: n =>
            {
                tracker.Add(n.Name);
                return Task.FromResult(!n.Labels.Contains("User"));
            });

        Assert.Equal(["Node1", "Node2"], tracker);
        Assert.Equal("Old1", results.Single(n => n.Name == "Node1").Summary);
        Assert.Equal("Old2", results.Single(n => n.Name == "Node2").Summary);
        Assert.All(results, n => Assert.NotNull(n.NameEmbedding));
    }

    [Description("A patient.")]
    private sealed record Patient
    {
        public string? FirstName { get; init; }

        public string? LastName { get; init; }

        public string? FavoriteColor { get; init; }
    }

    [Fact]
    public async Task Extract_attributes_from_nodes_keeps_initial_nulls_from_response()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractAttributes,
            """{"first_name": "John", "last_name": null, "favorite_color": null}""");
        var node = Entity("John Smith", labels: ["Entity", "Patient"], summary: "Existing summary",
            attributes: new() { ["last_name"] = "Smith" });

        var results = await NodeOperations.ExtractAttributesFromNodesAsync(
            h.Clients, [node], [Episode()], [],
            new Dictionary<string, Type> { ["Patient"] = typeof(Patient) },
            shouldSummarizeNode: _ => Task.FromResult(false));

        Assert.Equal(
            new Dictionary<string, object?> { ["first_name"] = "John", ["last_name"] = "Smith", ["favorite_color"] = null },
            results[0].Attributes);
        var call = Assert.Single(h.Llm.Calls);
        Assert.Equal(typeof(Patient), call.ResponseModel);
        Assert.Equal(GraphitiNet.Llm.ModelSize.Small, call.ModelSize);
        Assert.True(call.AttributeExtraction);
        Assert.Equal("group", call.GroupId);
        var context = h.Prompts.Last(PromptNames.ExtractNodesExtractAttributes);
        var nodeData = (Dictionary<string, object?>)context["node"]!;
        Assert.Equal(["name", "entity_types", "attributes"], nodeData.Keys);
    }

    [Fact]
    public async Task Batch_summaries_calls_llm_for_long_summary()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractSummariesBatch,
            """{"summaries": [{"name": "Test Node", "summary": "Condensed summary"}]}""");
        var node = Entity("Test Node", summary: "Short");
        var edge = Edge(node.Uuid, "other-uuid", new string('x', TextUtils.MaxSummaryChars * 2));
        var edgesByNode = new Dictionary<string, IReadOnlyList<EntityEdge>> { [node.Uuid] = [edge, edge] };

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [node], [Episode()], [], null, edgesByNode);

        Assert.Single(h.Llm.Calls);
        Assert.Equal("Condensed summary", node.Summary);
    }

    [Fact]
    public async Task Extract_attributes_preserves_prior_attributes_when_entity_types_none()
    {
        var h = new TestHarness();
        var node = Entity("Alice", labels: ["Entity", "Person"], summary: "Existing summary",
            attributes: new() { ["age"] = 30L, ["city"] = "New York" });

        var results = await NodeOperations.ExtractAttributesFromNodesAsync(h.Clients, [node], [Episode()], []);

        Assert.Equal(new Dictionary<string, object?> { ["age"] = 30L, ["city"] = "New York" }, results[0].Attributes);
    }

    private sealed record Organization
    {
        public string? Industry { get; init; }
    }

    [Fact]
    public async Task Extract_attributes_preserves_prior_attributes_when_label_not_in_entity_types()
    {
        var h = new TestHarness();
        var node = Entity("Alice", labels: ["Entity", "Person"], summary: "Existing summary",
            attributes: new() { ["age"] = 30L, ["city"] = "New York" });

        var results = await NodeOperations.ExtractAttributesFromNodesAsync(
            h.Clients, [node], [Episode()], [], new Dictionary<string, Type> { ["Organization"] = typeof(Organization) });

        Assert.Equal(new Dictionary<string, object?> { ["age"] = 30L, ["city"] = "New York" }, results[0].Attributes);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Extract_attributes_caps_over_long_values_and_keeps_prior()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractAttributes,
            $$"""{"industry": "{{new string('x', AttributeUtils.DefaultAttributeMaxLength + 1)}}"}""");
        var node = Entity("Acme", labels: ["Entity", "Organization"], attributes: new() { ["industry"] = "Software" });

        await NodeOperations.ExtractAttributesOnlyFromNodesAsync(
            h.Clients, [node], [Episode()], [], new Dictionary<string, Type> { ["Organization"] = typeof(Organization) });

        Assert.Equal("Software", node.Attributes["industry"]);
    }

    [Fact]
    public async Task Resolve_extracted_nodes_honours_cancellation()
    {
        var h = new TestHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NodeOperations.ResolveExtractedNodesAsync(h.Clients, [Entity("Alice")], cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NodeOperations.ExtractAttributesFromNodesAsync(h.Clients, [Entity("Alice")], cancellationToken: cts.Token));
    }

    private static Dictionary<string, IReadOnlyList<EntityEdge>> Empty() => [];

    private static DedupResolutionState State(int count, int[] unresolved)
    {
        var state = new DedupResolutionState(count);
        foreach (var i in unresolved)
        {
            state.UnresolvedIndices.Add(i);
        }

        return state;
    }
}
