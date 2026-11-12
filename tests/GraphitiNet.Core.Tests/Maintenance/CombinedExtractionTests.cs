// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_combined_extraction.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Maintenance;

public class CombinedExtractionTests
{
    private const string Extract = PromptNames.ExtractNodesAndEdgesExtractMessage;
    private const string Timestamps = PromptNames.ExtractEdgesExtractTimestampsBatch;

    private static readonly IReadOnlyList<EpisodicNode> NoEpisodes = [];

    private static (GraphitiClients Clients, FakeLlmClient Llm) MakeClients()
    {
        var (clients, llm, _, _) = EdgeTestData.Clients();
        llm.On(Timestamps, new { timestamps = new[] { new { valid_at = (string?)null, invalid_at = (string?)null } } });
        return (clients, llm);
    }

    private static EpisodicNode MakeEpisode(
        string content = "Acme Robotics announced the Atlas launch.", DateTimeOffset? validAt = null) => new()
    {
        Name = "test_episode",
        GroupId = "group",
        Source = EpisodeType.Text,
        SourceDescription = "test",
        Content = content,
        ValidAt = validAt ?? DateTimeUtils.UtcNow(),
    };

    private static object Entity(string name, int typeId = 0) => new { name, entity_type_id = typeId };

    private static object Fact(string source, string target, string relation, string fact, params int[] episodeIndices) =>
        new
        {
            source_entity_name = source,
            target_entity_name = target,
            relation_type = relation,
            fact,
            episode_indices = episodeIndices.Length == 0 ? [0] : episodeIndices,
        };

    private static object Response(object[] entities, params object[] edges) =>
        new { extracted_entities = entities, edges };

    [Description("A named business or organization.")]
    private sealed class Company;

    [Description("A named commercial product.")]
    private sealed class Product;

    [Description("One product integrates with another product.")]
    private sealed class IntegratesWith;

    [Description("A person.")]
    private sealed class Person;

    [Description("One entity sold another entity.")]
    private sealed class Sold;

    [Fact]
    public async Task Excluding_the_default_entity_remaps_the_custom_type_to_zero()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response(
            [Entity("Acme Robotics"), Entity("Atlas launch")],
            Fact("Acme Robotics", "Atlas launch", "ANNOUNCED", "Acme Robotics announced the Atlas launch.")));

        var (nodes, edges, map) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(
            clients, MakeEpisode(), NoEpisodes,
            entityTypes: new Dictionary<string, Type> { ["Company"] = typeof(Company) },
            excludedEntityTypes: ["Entity"]);

        Assert.Equal(["Acme Robotics", "Atlas launch"], nodes.Select(n => n.Name).Order());
        Assert.All(nodes, n => Assert.Equal(["Company", "Entity"], n.Labels.Order()));
        Assert.Single(edges);
        Assert.Equal(nodes.Count, map.Count);
        Assert.All(map.Values, indices => Assert.Equal([0], indices));

        var prompt = llm.CallsFor(Extract)[0].Text;
        Assert.DoesNotContain("'entity_type_name': 'Entity'", prompt, StringComparison.Ordinal);
        Assert.Contains("'entity_type_id': 0", prompt, StringComparison.Ordinal);
        Assert.Contains("'entity_type_name': 'Company'", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_edge_types_filter_derived_relations()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response(
            [Entity("Atlas Home Robot"), Entity("Acme Smart Hub")],
            Fact("Atlas Home Robot", "Acme Smart Hub", "INTEGRATES_WITH", "Atlas Home Robot integrates with Acme Smart Hub."),
            Fact("Atlas Home Robot", "Acme Smart Hub", "SOLD_WITH", "Atlas Home Robot is sold with Acme Smart Hub.")));

        var (nodes, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(
            clients, MakeEpisode(), NoEpisodes,
            entityTypes: new Dictionary<string, Type> { ["Product"] = typeof(Product) },
            excludedEntityTypes: ["Entity"],
            edgeTypes: new Dictionary<string, Type> { ["INTEGRATES_WITH"] = typeof(IntegratesWith) },
            edgeTypeMap: new Dictionary<(string Source, string Target), IReadOnlyList<string>>
            {
                [("Product", "Product")] = ["INTEGRATES_WITH"],
            },
            strictEdgeTypes: true);

        Assert.Equal(["Acme Smart Hub", "Atlas Home Robot"], nodes.Select(n => n.Name).Order());
        Assert.Equal(["INTEGRATES_WITH"], edges.Select(e => e.Name));
        var prompt = llm.CallsFor(Extract)[0].Text;
        Assert.Contains("If a relationship does not match any FACT TYPE, skip it", prompt, StringComparison.Ordinal);
        Assert.Contains("derive a new relation_type", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_edge_types_without_edge_types_extract_nothing()
    {
        var (clients, llm) = MakeClients();

        var (nodes, edges, map) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(
            clients, MakeEpisode(), NoEpisodes, strictEdgeTypes: true);

        Assert.Empty(nodes);
        Assert.Empty(edges);
        Assert.Empty(map);
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task Materializes_a_missing_target_endpoint()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("AMI")],
            Fact("AMI", "version 2.18.1", "PINNED_AT", "AMI is pinned at version 2.18.1.")));

        var (nodes, edges, map) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Equal(["AMI", "version 2.18.1"], nodes.Select(n => n.Name).Order(StringComparer.Ordinal));
        var edge = Assert.Single(edges);
        Assert.Equal("PINNED_AT", edge.Name);
        Assert.Equal("AMI is pinned at version 2.18.1.", edge.Fact);
        var nameToUuid = nodes.ToDictionary(n => n.Name, n => n.Uuid);
        Assert.Equal(nameToUuid["AMI"], edge.SourceNodeUuid);
        Assert.Equal(nameToUuid["version 2.18.1"], edge.TargetNodeUuid);
        Assert.NotEqual(edge.SourceNodeUuid, edge.TargetNodeUuid);
        Assert.Equal(nameToUuid.Values.Order(), map.Keys.Order());
    }

    [Fact]
    public async Task Materializes_a_missing_source_endpoint()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("production cluster")],
            Fact("version 6.8.6", "production cluster", "DEPLOYED_ON", "version 6.8.6 is deployed on the production cluster.")));

        var (nodes, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Equal(["production cluster", "version 6.8.6"], nodes.Select(n => n.Name).Order(StringComparer.Ordinal));
        var nameToUuid = nodes.ToDictionary(n => n.Name, n => n.Uuid);
        var edge = Assert.Single(edges);
        Assert.Equal(nameToUuid["version 6.8.6"], edge.SourceNodeUuid);
        Assert.Equal(nameToUuid["production cluster"], edge.TargetNodeUuid);
    }

    [Fact]
    public async Task Keeps_an_edge_when_both_endpoints_are_present()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("AMI"), Entity("version 2.18.1")],
            Fact("AMI", "version 2.18.1", "PINNED_AT", "AMI is pinned at version 2.18.1.")));

        var (nodes, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Equal(2, nodes.Count);
        Assert.Single(edges);
        Assert.All(nodes, n => Assert.Equal(["Entity"], n.Labels));
        var prompt = llm.CallsFor(Extract)[0].Text;
        Assert.Contains("source_entity_name and target_entity_name must be names from extracted_entities", prompt,
            StringComparison.Ordinal);
        Assert.Contains("If an endpoint is a value such as a version, add it as an entity.", prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolves_a_dangling_endpoint_case_insensitively()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("AMI"), Entity("Version 2.18.1")],
            Fact("ami", "version 2.18.1", "PINNED_AT", "AMI is pinned at version 2.18.1.")));

        var (nodes, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Equal(2, nodes.Count);
        var edge = Assert.Single(edges);
        var nameToUuid = nodes.ToDictionary(n => n.Name, n => n.Uuid);
        Assert.Equal(nameToUuid["AMI"], edge.SourceNodeUuid);
        Assert.Equal(nameToUuid["Version 2.18.1"], edge.TargetNodeUuid);
    }

    [Fact]
    public async Task Does_not_resurrect_an_excluded_entity_endpoint()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("Alice"), Entity("secret value", 1)],
            Fact("Alice", "secret value", "KNOWS", "Alice knows secret value.")));

        var (nodes, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(
            clients, MakeEpisode(), NoEpisodes,
            entityTypes: new Dictionary<string, Type> { ["Person"] = typeof(Person) },
            excludedEntityTypes: ["Entity"]);

        Assert.DoesNotContain("secret value", nodes.Select(n => n.Name));
        Assert.Empty(edges);
    }

    [Fact]
    public async Task Drops_orphan_nodes_and_empty_facts()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("Alice"), Entity("Bob"), Entity("Orphan"), Entity("  ")],
            Fact("Alice", "Bob", "KNOWS", "Alice knows Bob."),
            Fact("Alice", "Orphan", "KNOWS", "   "),
            Fact("", "Bob", "KNOWS", "Someone knows Bob.")));

        var (nodes, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Equal(["Alice", "Bob"], nodes.Select(n => n.Name));
        Assert.Equal("Alice knows Bob.", Assert.Single(edges).Fact);
    }

    [Fact]
    public async Task A_refused_timestamp_batch_is_not_swallowed()
    {
        // The base LLMClient asks the gate; the usual fake overrides GenerateResponseAsync and would not.
        var response = System.Text.Json.JsonSerializer.Serialize(
            Response([Entity("Alice"), Entity("Bob")], Fact("Alice", "Bob", "KNOWS", "Alice knows Bob.")));
        var llm = new GraphitiNet.Tests.GraphitiApi.ScriptedLlmClient(response);
        var clients = MakeClients().Clients with { LlmClient = llm };
        var gate = new DelegateModelCallGate((call, _) => ValueTask.FromResult(call.PromptName != Timestamps));
        using var scope = GraphitiCallContext.Begin(
            new GraphitiCallContext { Operation = "add_episode_bulk", ModelCallGate = gate });

        await Assert.ThrowsAsync<ModelCallRefusedException>(() =>
            CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes));
        Assert.Single(llm.Calls);
    }

    [Fact]
    public async Task A_null_batch_answer_does_not_retry()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("Alice"), Entity("Bob")], Fact("Alice", "Bob", "KNOWS", "Alice knows Bob.")));

        var (_, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Null(Assert.Single(edges).ValidAt);
        Assert.Equal(2, llm.Calls.Count);
        Assert.Equal(Timestamps, llm.Calls[1].PromptName);
        Assert.Equal(GraphitiNet.Llm.ModelSize.Small, llm.Calls[1].ModelSize);
    }

    [Fact]
    public async Task Keeps_an_end_before_its_start()
    {
        var (clients, llm) = MakeClients();
        const string start = "2024-02-01T00:00:00+00:00";
        const string endBeforeStart = "2024-01-01T00:00:00+00:00";
        llm.On(Extract, Response([Entity("Alice"), Entity("Bob")], Fact("Alice", "Bob", "KNOWS", "Alice knows Bob.")));
        llm.On(Timestamps, new { timestamps = new[] { new { valid_at = start, invalid_at = endBeforeStart } } });

        var (_, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        var edge = Assert.Single(edges);
        Assert.Equal(start, PythonText.IsoFormat(edge.ValidAt!.Value));
        Assert.Equal(endBeforeStart, PythonText.IsoFormat(edge.InvalidAt!.Value));
    }

    [Fact]
    public async Task Renders_a_timeline_with_context_turns()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("Acme Robotics")]));
        var targetA = MakeEpisode("user turn one");
        var contextB = MakeEpisode("ignored assistant turn");
        var targetC = MakeEpisode("user turn two");

        await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, [targetA, targetC], NoEpisodes,
            timeline: [targetA, contextB, targetC]);

        var prompt = llm.CallsFor(Extract)[0].Text;
        var episode0 = prompt.IndexOf("[Episode 0]", StringComparison.Ordinal);
        var context = prompt.IndexOf("[CONTEXT EPISODE] (timestamp:", StringComparison.Ordinal);
        var episode1 = prompt.IndexOf("[Episode 1]", StringComparison.Ordinal);
        Assert.True(episode0 >= 0 && episode0 < context && context < episode1);
        Assert.Contains("ignored assistant turn", prompt, StringComparison.Ordinal);
        Assert.Contains("[CONTEXT EPISODE] are not numbered episodes", prompt, StringComparison.Ordinal);
        Assert.Contains("PREVIOUS MESSAGES and [CONTEXT EPISODE] messages to resolve", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTEXT EPISODES:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeline_indices_attribute_to_targets_only()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("Acme Robotics"), Entity("Atlas launch")],
            Fact("Acme Robotics", "Atlas launch", "ANNOUNCED", "Acme Robotics announced the Atlas launch.", 1)));
        var targetA = MakeEpisode("user turn one");
        var contextB = MakeEpisode("ignored assistant turn");
        var targetC = MakeEpisode("user turn two");

        var (_, edges, map) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, [targetA, targetC],
            NoEpisodes, timeline: [targetA, contextB, targetC]);

        var edge = Assert.Single(edges);
        Assert.Equal([targetC.Uuid], edge.Episodes);
        Assert.Equal(targetC.ValidAt, edge.ReferenceTime);
        Assert.NotEmpty(map);
        Assert.All(map.Values, indices => Assert.Equal([1], indices));
    }

    [Fact]
    public async Task Renders_custom_instructions_after_fact_types()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([]));

        await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes,
            entityTypes: new Dictionary<string, Type> { ["Product"] = typeof(Product) },
            edgeTypes: new Dictionary<string, Type> { ["SOLD"] = typeof(Sold) },
            edgeTypeMap: new Dictionary<(string Source, string Target), IReadOnlyList<string>>
            {
                [("Product", "Product")] = ["SOLD"],
            },
            customExtractionInstructions: "Only extract products launched after 2020.");

        var prompt = llm.CallsFor(Extract)[0].Text;
        Assert.Contains("<CUSTOM INSTRUCTIONS>", prompt, StringComparison.Ordinal);
        Assert.Contains("Only extract products launched after 2020.", prompt, StringComparison.Ordinal);
        Assert.Contains("the CUSTOM INSTRUCTION wins", prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("</FACT TYPES>", StringComparison.Ordinal)
                    < prompt.IndexOf("<CUSTOM INSTRUCTIONS>", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("<CUSTOM INSTRUCTIONS>", StringComparison.Ordinal)
                    < prompt.IndexOf("<PREVIOUS MESSAGES>", StringComparison.Ordinal));
        Assert.Contains("[\"Product\", \"Product\"]", prompt, StringComparison.Ordinal);
        Assert.Contains("One entity sold another entity.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Omits_the_custom_instructions_block_when_empty()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([]));

        await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.DoesNotContain("<CUSTOM INSTRUCTIONS>", llm.CallsFor(Extract)[0].Text, StringComparison.Ordinal);
        Assert.Empty(llm.CallsFor(Timestamps));
    }

    [Fact]
    public async Task Dates_facts_in_groups()
    {
        var factCount = CombinedExtractionOperations.TimestampBatchSize + 5;
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response(
            Enumerable.Range(0, factCount + 1).Select(i => Entity($"Entity {i}")).ToArray(),
            Enumerable.Range(0, factCount)
                .Select(i => Fact($"Entity {i}", $"Entity {i + 1}", "LINKS_TO", $"Entity {i} links to Entity {i + 1}."))
                .ToArray()));
        var groupSizes = new List<int>();
        llm.On(Timestamps, call =>
        {
            var size = Enumerable.Range(0, factCount)
                .Count(i => call.Text.Contains($"Entity {i} links to Entity", StringComparison.Ordinal));
            int attempt;
            lock (groupSizes)
            {
                groupSizes.Add(size);
                attempt = groupSizes.Count;
            }

            if (attempt == 1)
            {
                throw new InvalidOperationException("batch timestamps failed");
            }

            return new
            {
                timestamps = Enumerable.Repeat(new { valid_at = "2024-01-01T00:00:00Z", invalid_at = (string?)null }, size),
            };
        });

        var (_, edges, _) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(clients, MakeEpisode(), NoEpisodes);

        Assert.Equal(factCount, edges.Count);
        Assert.Equal(new[] { CombinedExtractionOperations.TimestampBatchSize, CombinedExtractionOperations.TimestampBatchSize, 5 }.Order(),
            groupSizes.Order());
        Assert.All(edges, e => Assert.NotNull(e.ValidAt));
    }

    [Fact]
    public async Task Previous_episodes_and_several_episodes_render_as_upstream()
    {
        var (clients, llm) = MakeClients();
        llm.On(Extract, Response([Entity("Alice"), Entity("Bob")], Fact("Alice", "Bob", "KNOWS", "Alice knows Bob.", 1, 0)));
        var previous = MakeEpisode("Earlier message", new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var first = MakeEpisode("Alice met Bob.");
        var second = MakeEpisode("Bob met Alice.", first.ValidAt.AddMinutes(5));

        var (_, edges, map) = await CombinedExtractionOperations.ExtractNodesAndEdgesAsync(
            clients, [first, second], [previous]);

        var prompt = llm.CallsFor(Extract)[0].Text;
        Assert.Contains("[{\"content\": \"Earlier message\", \"timestamp\": \"2025-01-02T03:04:05+00:00\"}]", prompt,
            StringComparison.Ordinal);
        Assert.Contains(TextUtils.ConcatenateEpisodes([first, second]), prompt, StringComparison.Ordinal);
        var edge = Assert.Single(edges);
        Assert.Equal([second.Uuid, first.Uuid], edge.Episodes);
        Assert.Equal(second.ValidAt, edge.ReferenceTime);
        Assert.All(map.Values, indices => Assert.Equal([0, 1], indices));
        Assert.Equal("group", llm.CallsFor(Extract)[0].GroupId);
    }
}
