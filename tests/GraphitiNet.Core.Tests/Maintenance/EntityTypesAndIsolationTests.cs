// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported in part from tests/test_entity_exclusion_int.py and tests/utils/maintenance/test_dangling_endpoint_merge.py
// (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.Text.Json.Serialization;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using static GraphitiNet.Tests.Maintenance.TestHarness;

namespace GraphitiNet.Tests.Maintenance;

public class EntityTypesUtilsTests
{
    [Description("A person.")]
    private sealed record Person
    {
        public string? FirstName { get; init; }
    }

    [Description("An organization.")]
    private sealed record Organization;

    private sealed record BadSummary
    {
        public string? Summary { get; init; }
    }

    private sealed record BadGroup
    {
        [JsonPropertyName("group_id")]
        public string? Tenant { get; init; }
    }

    private static readonly Dictionary<string, Type> Types = new()
    {
        ["Person"] = typeof(Person),
        ["Organization"] = typeof(Organization),
    };

    [Fact]
    public void Validation_valid_excluded_types()
    {
        Assert.True(EntityTypesUtils.ValidateExcludedEntityTypes(["Entity"], Types));
        Assert.True(EntityTypesUtils.ValidateExcludedEntityTypes(["Person"], Types));
        Assert.True(EntityTypesUtils.ValidateExcludedEntityTypes(["Entity", "Person"], Types));
        Assert.True(EntityTypesUtils.ValidateExcludedEntityTypes(null, Types));
        Assert.True(EntityTypesUtils.ValidateExcludedEntityTypes([], Types));
    }

    [Fact]
    public void Validation_invalid_excluded_types()
    {
        var e = Assert.Throws<ArgumentException>(() => EntityTypesUtils.ValidateExcludedEntityTypes(["InvalidType"], Types));
        Assert.StartsWith(
            "Invalid excluded entity types: ['InvalidType']. Available types: ['Entity', 'Organization', 'Person']",
            e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            EntityTypesUtils.ValidateExcludedEntityTypes(["Person", "NonExistentType"], Types));
    }

    [Fact]
    public void Validate_entity_types_rejects_protected_names()
    {
        Assert.True(EntityTypesUtils.ValidateEntityTypes(null));
        Assert.True(EntityTypesUtils.ValidateEntityTypes(Types));

        var e = Assert.Throws<EntityTypeValidationException>(() =>
            EntityTypesUtils.ValidateEntityTypes(new Dictionary<string, Type> { ["Bad"] = typeof(BadSummary) }));
        Assert.Equal("summary", e.EntityTypeAttribute);
        Assert.Equal("Bad", e.EntityType);

        Assert.Throws<EntityTypeValidationException>(() =>
            EntityTypesUtils.ValidateEntityTypes(new Dictionary<string, Type> { ["Bad"] = typeof(BadGroup) }));
    }

    [Fact]
    public void Description_and_fields_come_from_attributes()
    {
        Assert.Equal("A person.", EntityTypesUtils.GetDescription(typeof(Person)));
        Assert.Null(EntityTypesUtils.GetDescription(typeof(BadSummary)));
        Assert.Equal(["first_name"], EntityTypesUtils.GetFields(typeof(Person)).Select(f => f.Name));
        Assert.Empty(EntityTypesUtils.GetFields(typeof(Organization)));
    }
}

/// <summary>Entities never merge across groups (graphiti-net issue #1).</summary>
public class GroupIsolationTests
{
    [Fact]
    public async Task Same_name_in_two_groups_resolves_to_two_distinct_entities()
    {
        var h = new TestHarness();
        var inA = Entity("Alice Smith", groupId: "group-a");
        await h.StoreAsync(inA);

        var extractedA = Entity("Alice Smith", groupId: "group-a");
        var extractedB = Entity("Alice Smith", groupId: "group-b");
        var (resolvedA, _, _) = await NodeOperations.ResolveExtractedNodesAsync(
            h.Clients, [extractedA], Episode(groupId: "group-a"), []);
        var (resolvedB, mapB, duplicatesB) = await NodeOperations.ResolveExtractedNodesAsync(
            h.Clients, [extractedB], Episode(groupId: "group-b"), []);

        Assert.Equal(inA.Uuid, resolvedA[0].Uuid);
        Assert.Equal(extractedB.Uuid, resolvedB[0].Uuid);
        Assert.Equal("group-b", resolvedB[0].GroupId);
        Assert.Equal(extractedB.Uuid, mapB[extractedB.Uuid]);
        Assert.Empty(duplicatesB);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Candidate_collection_never_returns_nodes_from_another_group()
    {
        var h = new TestHarness();
        var a = Entity("Alice", groupId: "group-a");
        var b = Entity("Alice", groupId: "group-b");
        var bOther = Entity("Alicia Keys", groupId: "group-b");
        await h.StoreAsync(a, b, bOther);
        var overrideFromB = Entity("Alice", groupId: "group-b");

        var result = await NodeOperations.CollectCandidateNodesAsync(
            h.Clients, [Entity("Alice", groupId: "group-a"), Entity("Alicia Keys", groupId: "group-a")], [overrideFromB]);

        Assert.All(result.SelectMany(c => c), n => Assert.Equal("group-a", n.GroupId));
        Assert.Equal([a.Uuid], result[0].Select(n => n.Uuid));
    }

    [Fact]
    public async Task Llm_choice_of_a_candidate_in_another_group_is_ignored()
    {
        var h = new TestHarness();
        h.Llm.Respond(PromptNames.DedupeNodesNodes,
            """{"entity_resolutions": [{"id": 0, "name": "Dizzy", "duplicate_candidate_id": 0}]}""");
        var extracted = Entity("Dizzy", groupId: "group-a");
        var foreign = Entity("Dizzy Gillespie", groupId: "group-b");
        var state = new DedupResolutionState(1);
        state.UnresolvedIndices.Add(0);

        await NodeOperations.ResolveWithLlmAsync(
            h.Clients, [extracted], DedupHelpers.BuildCandidateIndexes([foreign]), state, Episode(), [], null);

        Assert.Same(extracted, state.ResolvedNodes[0]);
        Assert.Empty(state.DuplicatePairs);
    }
}

/// <summary>The node side of upstream's dangling-endpoint tests: a materialised endpoint resolves like any other
/// extracted entity.</summary>
public class DanglingEndpointNodeTests
{
    [Fact]
    public async Task Materialized_endpoint_resolves_onto_existing_graph_node()
    {
        var h = new TestHarness();
        var canonical = Entity("version 2.18.1", groupId: "group_1", uuid: "canonical-version");
        await h.StoreAsync(canonical);
        var nodes = new List<EntityNode> { Entity("AMI", groupId: "group_1", uuid: "ami-uuid") };
        var nameToNode = nodes.ToDictionary(n => DedupHelpers.NormalizeStringExact(n.Name));

        var materialized = DanglingEndpoints.MaterializeDanglingEndpointNode("version 2.18.1", "group_1", nameToNode, nodes);
        var (resolved, uuidMap, _) = await NodeOperations.ResolveExtractedNodesAsync(
            h.Clients, [materialized], Episode(groupId: "group_1"), []);

        Assert.Equal("canonical-version", uuidMap[materialized.Uuid]);
        Assert.Equal("canonical-version", resolved[0].Uuid);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public void Materialized_endpoint_reuses_same_name_node_from_this_episode()
    {
        var resolvedVersion = Entity("version 2.18.1", groupId: "group_1", uuid: "resolved-version");
        var nodes = new List<EntityNode> { Entity("AMI", groupId: "group_1", uuid: "ami-uuid"), resolvedVersion };
        var nameToNode = nodes.ToDictionary(n => DedupHelpers.NormalizeStringExact(n.Name));

        var node = DanglingEndpoints.MaterializeDanglingEndpointNode("Version 2.18.1 ", "group_1", nameToNode, nodes);

        Assert.Same(resolvedVersion, node);
        Assert.Equal(["ami-uuid", "resolved-version"], nodes.Select(n => n.Uuid));
    }
}
