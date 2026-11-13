// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_entity_extraction.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using static GraphitiNet.Tests.Maintenance.TestHarness;

namespace GraphitiNet.Tests.Maintenance;

public class EntityExtractionTests
{
    [Description("A human person.")]
    private sealed record Person;

    [Description("A user of the system.")]
    private sealed record User;

    [Description("A named business or organization.")]
    private sealed record Company;

    [Description("A business or organization.")]
    private sealed record Organization;

    private static TestHarness WithExtraction(string json, EpisodeType source = EpisodeType.Text)
    {
        var h = new TestHarness();
        var name = source switch
        {
            EpisodeType.Message => PromptNames.ExtractNodesExtractMessage,
            EpisodeType.Json => PromptNames.ExtractNodesExtractJson,
            _ => PromptNames.ExtractNodesExtractText,
        };
        h.Llm.Respond(name, json);
        return h;
    }

    [Fact]
    public async Task Small_input_single_llm_call()
    {
        var h = WithExtraction("""{"extracted_entities": [{"name": "Alice", "entity_type_id": 0}, {"name": "Bob", "entity_type_id": 0}]}""");

        var (nodes, _) = await NodeOperations.ExtractNodesAsync(
            h.Clients, Episode("Alice talked to Bob.", EpisodeType.Text), []);

        Assert.Equal(["Alice", "Bob"], nodes.Select(n => n.Name));
        Assert.Single(h.Llm.Calls);
    }

    [Fact]
    public async Task Extracts_entity_types()
    {
        var h = WithExtraction("""{"extracted_entities": [{"name": "Alice", "entity_type_id": 1}, {"name": "Acme Corp", "entity_type_id": 0}]}""");

        var (nodes, _) = await NodeOperations.ExtractNodesAsync(
            h.Clients, Episode("Alice works at Acme Corp.", EpisodeType.Text), [],
            new Dictionary<string, Type> { ["Person"] = typeof(Person) });

        Assert.Contains("Person", nodes.Single(n => n.Name == "Alice").Labels);
        Assert.Equal(["Entity"], nodes.Single(n => n.Name == "Acme Corp").Labels);
    }

    [Fact]
    public async Task Excludes_entity_types()
    {
        var h = WithExtraction("""{"extracted_entities": [{"name": "Project X", "entity_type_id": 0}]}""");

        var (nodes, _) = await NodeOperations.ExtractNodesAsync(
            h.Clients, Episode("Alice created Project X.", EpisodeType.Text), [],
            new Dictionary<string, Type> { ["User"] = typeof(User) }, ["User"]);

        Assert.Equal("Project X", Assert.Single(nodes).Name);
        Assert.DoesNotContain("'entity_type_name': 'User'", Assert.Single(h.Llm.Calls).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excluding_default_entity_remaps_custom_type_to_zero()
    {
        var h = WithExtraction("""{"extracted_entities": [{"name": "Acme Robotics", "entity_type_id": 0}]}""");

        var (nodes, _) = await NodeOperations.ExtractNodesAsync(
            h.Clients, Episode("Acme Robotics announced the Atlas launch.", EpisodeType.Text), [],
            new Dictionary<string, Type> { ["Company"] = typeof(Company) }, ["Entity"]);

        var node = Assert.Single(nodes);
        Assert.Equal("Acme Robotics", node.Name);
        Assert.Equal(["Entity", "Company"], node.Labels);
        var text = Assert.Single(h.Llm.Calls).Text;
        Assert.DoesNotContain("'entity_type_name': 'Entity'", text, StringComparison.Ordinal);
        Assert.Contains("'entity_type_id': 0", text, StringComparison.Ordinal);
        Assert.Contains("'entity_type_name': 'Company'", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excluding_all_entity_types_skips_llm()
    {
        var h = new TestHarness();

        var (nodes, map) = await NodeOperations.ExtractNodesAsync(
            h.Clients, Episode("Acme Robotics announced the Atlas launch.", EpisodeType.Text), [],
            excludedEntityTypes: ["Entity"]);

        Assert.Empty(nodes);
        Assert.Empty(map);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Filters_empty_names()
    {
        var h = WithExtraction("""
            {"extracted_entities": [{"name": "Alice", "entity_type_id": 0}, {"name": "", "entity_type_id": 0},
             {"name": "   ", "entity_type_id": 0}]}
            """);

        var (nodes, _) = await NodeOperations.ExtractNodesAsync(h.Clients, Episode("Alice is here.", EpisodeType.Text), []);

        Assert.Equal("Alice", Assert.Single(nodes).Name);
    }

    [Fact]
    public async Task Collapses_exact_duplicate_names_preferring_specific_type()
    {
        var h = WithExtraction("""
            {"extracted_entities": [{"name": "Caroline", "entity_type_id": 0}, {"name": "Caroline", "entity_type_id": 1},
             {"name": "Melanie", "entity_type_id": 1}]}
            """, EpisodeType.Message);

        var (nodes, map) = await NodeOperations.ExtractNodesAsync(
            h.Clients,
            Episode("Caroline: Hey Mel! Good to see you! How have you been?\nMelanie: Hey Caroline! Good to see you!", EpisodeType.Message),
            [],
            new Dictionary<string, Type> { ["Person"] = typeof(Person) });

        Assert.Equal(["Caroline", "Melanie"], nodes.Select(n => n.Name));
        Assert.Contains("Person", nodes[0].Labels);
        Assert.Equal(2, map.Count);
        Assert.Equal([0], map[nodes[0].Uuid]);
    }

    [Fact]
    public async Task Multi_episode_extraction_maps_episode_indices_and_adds_attribution()
    {
        var h = WithExtraction("""
            {"extracted_entities": [{"name": "Alice", "entity_type_id": 0, "episode_indices": [0, 2, 7]},
             {"name": "Bob", "entity_type_id": 0, "episode_indices": []}]}
            """, EpisodeType.Message);
        var t = new DateTimeOffset(2025, 1, 1, 10, 0, 0, TimeSpan.Zero);
        EpisodicNode[] episodes =
        [
            Episode("a", EpisodeType.Message, validAt: t),
            Episode("b", EpisodeType.Message, validAt: t.AddMinutes(1)),
            Episode("c", EpisodeType.Message, validAt: t.AddMinutes(2)),
        ];

        var (nodes, map) = await NodeOperations.ExtractNodesAsync(h.Clients, episodes, [], customExtractionInstructions: "Be careful.");

        Assert.Equal([0, 2], map[nodes[0].Uuid]);
        Assert.Equal([0, 1, 2], map[nodes[1].Uuid]);
        var context = h.Prompts.Last(PromptNames.ExtractNodesExtractMessage);
        Assert.StartsWith("Be careful.\n7. **Episode Attribution**", (string)context["custom_extraction_instructions"]!, StringComparison.Ordinal);
        Assert.Equal("2025-01-01T10:00:00+00:00", context["episode_timestamp"]);
        Assert.Contains("[Episode 2] (timestamp: 2025-01-01T10:02:00+00:00)\nc", (string)context["episode_content"]!, StringComparison.Ordinal);
        Assert.Equal(
            ["episode_content", "episode_timestamp", "previous_episodes", "custom_extraction_instructions", "entity_types", "source_description"],
            context.Keys);
    }

    [Theory]
    [InlineData(EpisodeType.Text, PromptNames.ExtractNodesExtractText)]
    [InlineData(EpisodeType.Json, PromptNames.ExtractNodesExtractJson)]
    [InlineData(EpisodeType.Message, PromptNames.ExtractNodesExtractMessage)]
    public async Task Uses_prompt_for_episode_type(EpisodeType source, string promptName)
    {
        var h = WithExtraction("""{"extracted_entities": []}""", source);

        await NodeOperations.ExtractNodesAsync(h.Clients, Episode(source == EpisodeType.Json ? "{}" : "x", source), []);

        var call = Assert.Single(h.Llm.Calls);
        Assert.Equal(promptName, call.PromptName);
        Assert.Equal(typeof(ExtractedEntities), call.ResponseModel);
        Assert.Equal("group", call.GroupId);
    }

    [Fact]
    public void Default_entity_type_always_included()
    {
        var context = NodeOperations.BuildEntityTypesContext(null);

        var only = Assert.Single(context);
        Assert.Equal(0, only["entity_type_id"]);
        Assert.Equal("Entity", only["entity_type_name"]);
    }

    [Fact]
    public void Custom_types_added_after_default()
    {
        var context = NodeOperations.BuildEntityTypesContext(new Dictionary<string, Type>
        {
            ["Person"] = typeof(Person),
            ["Organization"] = typeof(Organization),
        });

        Assert.Equal(3, context.Count);
        Assert.Equal("Entity", context[0]["entity_type_name"]);
        Assert.Equal("Person", context[1]["entity_type_name"]);
        Assert.Equal(1, context[1]["entity_type_id"]);
        Assert.Equal("Organization", context[2]["entity_type_name"]);
        Assert.Equal(2, context[2]["entity_type_id"]);
    }

    [Fact]
    public void Filter_excluded_entity_types_remaps_ids()
    {
        var context = NodeOperations.BuildEntityTypesContext(new Dictionary<string, Type>
        {
            ["Person"] = typeof(Person),
            ["Organization"] = typeof(Organization),
        });

        var filtered = NodeOperations.FilterEntityTypesContext(context, ["Entity", "Person"]);

        var only = Assert.Single(filtered);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["entity_type_id"] = 0,
                ["entity_type_name"] = "Organization",
                ["entity_type_description"] = "A business or organization.",
            },
            only);
    }

    [Fact]
    public async Task Summaries_no_nodes_needing_summarization()
    {
        var h = new TestHarness();
        var node = Entity("Alice", summary: "Alice is a person.");

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [node], null, null, null, Edges());

        Assert.Empty(h.Llm.Calls);
        Assert.Equal("Alice is a person.", node.Summary);
    }

    [Fact]
    public async Task Summaries_short_summary_with_edge_facts()
    {
        var h = new TestHarness();
        var node = Entity("Alice", summary: "Alice is a person.", uuid: "alice-uuid");
        var edge = Edge("alice-uuid", "bob-uuid", "Alice works with Bob.");

        await NodeOperations.ExtractEntitySummariesBatchAsync(
            h.Clients, [node], null, null, null, NodeOperations.BuildEdgesByNode([edge]));

        Assert.Empty(h.Llm.Calls);
        Assert.Equal("Alice is a person.\nAlice works with Bob.", node.Summary);
    }

    [Fact]
    public async Task Summaries_long_summary_needs_llm()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractSummariesBatch,
            """{"summaries": [{"name": "Alice", "summary": "Alice is a software engineer at Acme Corp."}]}""");
        var node = Entity("Alice", summary: string.Concat(Enumerable.Repeat("Alice is a person. ", 200)));

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [node], [Episode()], [], null, Edges());

        var call = Assert.Single(h.Llm.Calls);
        Assert.Equal(GraphitiNet.Llm.ModelSize.Small, call.ModelSize);
        Assert.Equal("Alice is a software engineer at Acme Corp.", node.Summary);
        var context = h.Prompts.Last(PromptNames.ExtractNodesExtractSummariesBatch);
        Assert.Equal(["entities", "episode_content", "previous_episodes", "entity_type_descriptions"], context.Keys);
    }

    [Fact]
    public async Task Summaries_should_summarize_filter()
    {
        var h = new TestHarness();

        await NodeOperations.ExtractEntitySummariesBatchAsync(
            h.Clients, [Entity("Alice")], [Episode()], [], _ => Task.FromResult(false), Edges());

        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Summaries_batch_multiple_nodes()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractSummariesBatch,
            """{"summaries": [{"name": "Alice", "summary": "Alice summary."}, {"name": "Bob", "summary": "Bob summary."}]}""");
        var longSummary = string.Concat(Enumerable.Repeat("X ", 1500));
        var alice = Entity("Alice", summary: longSummary);
        var bob = Entity("Bob", summary: longSummary);

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [alice, bob], [Episode()], [], null, Edges());

        Assert.Single(h.Llm.Calls);
        Assert.Equal("Alice summary.", alice.Summary);
        Assert.Equal("Bob summary.", bob.Summary);
    }

    [Fact]
    public async Task Summaries_unknown_entity_in_response()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractSummariesBatch,
            """{"summaries": [{"name": "UnknownEntity", "summary": "Should be ignored."}, {"name": "Alice", "summary": "Alice summary."}]}""");
        var alice = Entity("Alice", summary: string.Concat(Enumerable.Repeat("X ", 1500)));

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [alice], [Episode()], [], null, Edges());

        Assert.Equal("Alice summary.", alice.Summary);
        Assert.Contains(h.Logger.Entries, e => e.Message.Contains("unknown entity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summaries_no_episode_and_no_summary()
    {
        var h = new TestHarness();
        var node = Entity("Alice");

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [node], null, null, null, Edges());

        Assert.Empty(h.Llm.Calls);
        Assert.Equal("", node.Summary);
    }

    [Fact]
    public async Task Summaries_flight_partitioning()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractSummariesBatch, """{"summaries": []}""");
        var longSummary = string.Concat(Enumerable.Repeat("X ", 1500));
        var nodes = Enumerable.Range(0, 5).Select(i => Entity($"Entity{i}", summary: longSummary)).ToList();

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, nodes, [Episode()], [], null, Edges(), flightSize: 2);

        Assert.Equal(3, h.Llm.Calls.Count);
    }

    [Fact]
    public async Task Summaries_case_insensitive_name_matching()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractSummariesBatch,
            """{"summaries": [{"name": "ALICE", "summary": "Alice summary from LLM."}]}""");
        var node = Entity("alice", summary: string.Concat(Enumerable.Repeat("X ", 1500)));

        await NodeOperations.ExtractEntitySummariesBatchAsync(h.Clients, [node], [Episode()], [], null, Edges());

        Assert.Equal("Alice summary from LLM.", node.Summary);
    }

    [Fact]
    public async Task Summaries_skip_fact_appending_uses_episode_prompt_with_type_descriptions()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.ExtractNodesExtractEntitySummariesFromEpisodes,
            """{"summaries": [{"name": "Alice", "summary": "Alice is a person."}]}""");
        var node = Entity("Alice", labels: ["Entity", "Person"], summary: "Short");

        await NodeOperations.ExtractEntitySummariesBatchAsync(
            h.Clients, [node], null, null, null, Edges(), skipFactAppending: true,
            entityTypes: new Dictionary<string, Type> { ["Person"] = typeof(Person), ["User"] = typeof(User) });

        Assert.Equal("Alice is a person.", node.Summary);
        var context = h.Prompts.Last(PromptNames.ExtractNodesExtractEntitySummariesFromEpisodes);
        Assert.Equal(
            new Dictionary<string, object?> { ["Person"] = "A human person.", ["User"] = "A user of the system." },
            (Dictionary<string, object?>)context["entity_type_descriptions"]!);
        Assert.Equal("", context["episode_content"]);
    }

    [Theory]
    [InlineData("A human person who interacts with the system.", "A human person who interacts with the system.")]
    [InlineData("A human person.\n\nGOOD: Alice, Bob\nBAD: happiness", "A human person.")]
    [InlineData("First. Second. Third. Fourth should be dropped.", "First. Second. Third.")]
    [InlineData("A specific thing. It has properties. Use it wisely.\n\nGOOD: example", "A specific thing. It has properties. Use it wisely.")]
    [InlineData("\n\nA location.\n\nGOOD: New York", "A location.")]
    [InlineData("A specific, named subject\nor knowledge domain.\n\nGOOD: cooking", "A specific, named subject or knowledge domain.")]
    [InlineData("", "")]
    [InlineData("A human person who interacts with the system", "A human person who interacts with the system")]
    [InlineData(
        "A specific thing that a speaker likes, dislikes, wants, or has expressed an opinion about.\n\nGOOD: \"sushi\"\nBAD: do not use\n\nTrigger patterns: \"I love X\"",
        "A specific thing that a speaker likes, dislikes, wants, or has expressed an opinion about.")]
    [InlineData("An entity used in e.g. medical contexts. It tracks Dr. Smith's patients.", "An entity used in e.g. medical contexts. It tracks Dr. Smith's patients.")]
    [InlineData("Requires version 2.0 or higher. Must be certified.", "Requires version 2.0 or higher. Must be certified.")]
    public void Truncate_type_description(string doc, string expected) =>
        Assert.Equal(expected, NodeOperations.TruncateTypeDescription(doc));

    [Fact]
    public async Task Extract_nodes_honours_cancellation()
    {
        var h = WithExtraction("""{"extracted_entities": []}""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NodeOperations.ExtractNodesAsync(h.Clients, Episode(), [], cancellationToken: cts.Token));
        Assert.Empty(h.Llm.Calls);
    }

    private static Dictionary<string, IReadOnlyList<EntityEdge>> Edges() => [];
}
