// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_edge_operations.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using GraphitiNet.Llm;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using static GraphitiNet.Tests.Maintenance.EdgeTestData;

#pragma warning disable CA1861 // Canned LLM responses are built once per test.

namespace GraphitiNet.Tests.Maintenance;

public class EdgeOperationsTests
{
    [Description("Edge model stub for OCCURRED_AT.")]
    public sealed class OccurredAtEdge;

    [Description("A person works in a domain.")]
    public sealed class WorksInEdge;

    [Description("A term means something.")]
    public sealed class MeansEdge;

    [Description("Any entity mentions any other entity. No signature is declared.")]
    public sealed class MentionsEdge;

    [Description("A relationship between two people.")]
    public sealed class InterpersonalRelationship;

    [Description("A relationship indicating something is located in a place.")]
    public sealed class LocatedIn;

    [Description("Edge attribute schema mirroring a customer's EMPLOYMENT relation.")]
    public sealed class EmploymentEdge
    {
        public string? Title { get; init; }

        public string? IsCurrent { get; init; }
    }

    private static readonly Dictionary<string, Type> SignatureEdgeTypes = new()
    {
        ["WORKS_IN"] = typeof(WorksInEdge),
        ["MEANS"] = typeof(MeansEdge),
        ["MENTIONS"] = typeof(MentionsEdge),
    };

    private static readonly Dictionary<(string Source, string Target), IReadOnlyList<string>> SignatureEdgeTypeMap =
        new()
        {
            [("Person", "Domain")] = ["WORKS_IN"],
            [("User", "Domain")] = ["WORKS_IN"],
            [("Term", "Rule")] = ["MEANS"],
            [("Term", "Domain")] = ["MEANS"],
        };

    private static EntityEdge MockExtractedEdge() =>
        Edge("source_uuid", "target_uuid", "Test fact", episodes: ["episode_1"]);

    private static List<EntityEdge> MockExistingEdges() =>
    [
        Edge("source_uuid_3", "target_uuid_3", "Existing fact", name: "existing_edge", episodes: ["episode_3"],
            createdAt: DateTimeOffset.UtcNow.AddDays(-2), validAt: DateTimeOffset.UtcNow.AddDays(-2)),
    ];

    private static EpisodicNode CurrentEpisode() => Episode("episode_1", "Current episode content");

    [Fact]
    public async Task ResolveExtractedEdge_ExactFactShortCircuit()
    {
        var (clients, llm, _, _) = Clients();
        var extracted = Edge("source_uuid", "target_uuid", "Related fact", episodes: ["episode_1"]);
        var related = new List<EntityEdge>
        {
            Edge("source_uuid", "target_uuid", " related FACT  ", name: "related_edge", episodes: ["episode_2"]),
        };
        var episode = CurrentEpisode();

        var (resolved, invalidated, duplicates) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, related, MockExistingEdges(), episode, edgeTypeCandidates: null);

        Assert.Same(related[0], resolved);
        Assert.Equal(1, resolved.Episodes.Count(e => e == episode.Uuid));
        Assert.Empty(duplicates);
        Assert.Empty(invalidated);
        Assert.Empty(llm.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveExtractedEdge_MergesEpisodeProvenance(bool exactMatch)
    {
        var (clients, llm, _, _) = Clients();
        var episode = Episode("episode_2");
        var extracted = Edge("source_uuid", "target_uuid", exactMatch ? "Related fact" : "Extracted fact",
            uuid: "extracted-edge", episodes: ["episode_1", "episode_2"]);
        var existing = Edge("source_uuid", "target_uuid", "Related fact", uuid: "existing-edge",
            episodes: ["episode_0"], createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        llm.On(PromptNames.DedupeEdgesResolveEdge, new { duplicate_facts = new[] { 0 }, contradicted_facts = Array.Empty<int>() });

        var (resolved, _, _) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [existing], [], episode);
        Assert.Same(existing, resolved);
        Assert.Equal(["episode_0", "episode_1", "episode_2"], resolved.Episodes);

        (resolved, _, _) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [existing], [], episode);
        Assert.Equal(["episode_0", "episode_1", "episode_2"], resolved.Episodes);
        Assert.Equal(exactMatch ? 0 : 2, llm.Calls.Count);
    }

    [Fact]
    public async Task ResolveExtractedEdge_KeepsCallerAttributesWithoutSchema()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.DedupeEdgesResolveEdge, NoDuplicates).On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var extracted = Edge("source_uuid", "target_uuid", "Customer viewed listing 42", name: "VIEWED",
            episodes: ["episode_1"],
            attributes: new Dictionary<string, object?> { ["event_name"] = "car_viewed", ["event_id"] = "42" });

        var (resolved, invalidated, duplicates) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [], MockExistingEdges(), CurrentEpisode(), edgeTypeCandidates: null);

        Assert.Equal(
            new Dictionary<string, object?> { ["event_name"] = "car_viewed", ["event_id"] = "42" },
            resolved.Attributes);
        Assert.Empty(duplicates);
        Assert.Empty(invalidated);
    }

    [Fact]
    public async Task ResolveExtractedEdge_ClearsAttributesWhenSchemaOmitsType()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.DedupeEdgesResolveEdge, NoDuplicates).On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var extracted = Edge("source_uuid", "target_uuid", "Customer viewed listing 42", name: "VIEWED",
            episodes: ["episode_1"], attributes: new Dictionary<string, object?> { ["event_name"] = "car_viewed" });

        var (resolved, _, _) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [], MockExistingEdges(), CurrentEpisode(),
            edgeTypeCandidates: new Dictionary<string, Type>());

        Assert.Empty(resolved.Attributes);
    }

    [Fact]
    public async Task ResolveExtractedEdge_SendsContinuousIndicesToTheLlm()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.DedupeEdgesResolveEdge, NoDuplicates).On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var extracted = Edge("source_uuid", "target_uuid", "User likes yoga");
        var related = Edge("source_uuid", "target_uuid", "User enjoys yoga");
        var existing = Edge("x", "y", "User's yoga mat is blue");

        await EdgeOperations.ResolveExtractedEdgeAsync(clients, extracted, [related], [existing], Episode());

        var call = Assert.Single(llm.CallsFor(PromptNames.DedupeEdgesResolveEdge));
        Assert.Contains("<EXISTING FACTS>\n[{'idx': 0, 'fact': 'User enjoys yoga'}]\n</EXISTING FACTS>", call.Text);
        Assert.Contains(
            "<FACT INVALIDATION CANDIDATES>\n[{'idx': 1, 'fact': \"User's yoga mat is blue\"}]\n</FACT INVALIDATION CANDIDATES>",
            call.Text);
        Assert.Contains("<NEW FACT>\nUser likes yoga\n</NEW FACT>", call.Text);
        Assert.Equal(ModelSize.Small, call.ModelSize);
    }

    [Fact]
    public async Task ResolveExtractedEdges_KeepsUnknownNames()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var source = Node("source_uuid", "User Node", "User");
        var target = Node("target_uuid", "Topic Node", "Topic");
        var extracted = Edge(source.Uuid, target.Uuid, "User interacted with topic", name: "INTERACTED_WITH");

        var (resolved, invalidated, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients,
            [extracted],
            Episode(),
            [source, target],
            new Dictionary<string, Type> { ["OCCURRED_AT"] = typeof(OccurredAtEdge) },
            new Dictionary<(string Source, string Target), IReadOnlyList<string>> { [("Event", "Entity")] = ["OCCURRED_AT"] });

        Assert.Equal("INTERACTED_WITH", resolved[0].Name);
        Assert.Empty(invalidated);
        Assert.Equal(resolved, newEdges);
    }

    private static EntityNode SignatureNode(string uuid, string label) => Node(uuid, uuid, label);

    private static EntityEdge SignatureEdge(string name, EntityNode source, EntityNode target) =>
        Edge(source.Uuid, target.Uuid, $"{source.Name} {name} {target.Name}", name: name,
            attributes: new Dictionary<string, object?> { ["note"] = "x" });

    private static (IReadOnlyList<EntityEdge> Edges, IReadOnlyList<IReadOnlyDictionary<string, Type>> Types) Enforce(
        IReadOnlyList<EntityEdge> edges, IReadOnlyList<EntityNode> nodes, bool strict)
    {
        var map = nodes.ToDictionary(n => n.Uuid);
        var edgeTypesLst = edges
            .Select(e => EdgeOperations.EdgeTypesForEndpoints(e, map, SignatureEdgeTypes, SignatureEdgeTypeMap))
            .ToList();
        return EdgeOperations.EnforceEdgeTypeSignatures(edges, edgeTypesLst, SignatureEdgeTypeMap, strict);
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_KeepsMatchingCustomEdge()
    {
        var person = SignatureNode("person", "Person");
        var domain = SignatureNode("domain", "Domain");
        var edge = SignatureEdge("WORKS_IN", person, domain);

        foreach (var strict in new[] { true, false })
        {
            var (kept, keptTypes) = Enforce([edge], [person, domain], strict);
            Assert.Equal([edge], kept);
            Assert.Equal("WORKS_IN", edge.Name);
            Assert.Equal("x", edge.Attributes["note"]);
            Assert.Equal(new Dictionary<string, Type> { ["WORKS_IN"] = typeof(WorksInEdge) }, Assert.Single(keptTypes));
        }
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_AcceptsAnyDeclaredSignature()
    {
        var user = SignatureNode("user", "User");
        var term = SignatureNode("term", "Term");
        var domain = SignatureNode("domain", "Domain");
        EntityEdge[] edges = [SignatureEdge("WORKS_IN", user, domain), SignatureEdge("MEANS", term, domain)];

        var (kept, _) = Enforce(edges, [user, term, domain], strict: true);

        Assert.Equal(["WORKS_IN", "MEANS"], kept.Select(e => e.Name));
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_StrictDropsMismatchedCustomEdge()
    {
        var person = SignatureNode("person", "Person");
        var org = SignatureNode("org", "Organization");
        var validDomain = SignatureNode("domain", "Domain");
        var wrong = SignatureEdge("WORKS_IN", person, org);
        var valid = SignatureEdge("WORKS_IN", person, validDomain);

        var (kept, keptTypes) = Enforce([wrong, valid], [person, org, validDomain], strict: true);

        Assert.Equal([valid], kept);
        Assert.Equal(new Dictionary<string, Type> { ["WORKS_IN"] = typeof(WorksInEdge) }, Assert.Single(keptTypes));
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_NonStrictRenamesMismatchedCustomEdge()
    {
        var person = SignatureNode("person", "Person");
        var org = SignatureNode("org", "Organization");
        var wrong = SignatureEdge("WORKS_IN", person, org);

        var (kept, keptTypes) = Enforce([wrong], [person, org], strict: false);

        Assert.Equal([wrong], kept);
        Assert.Equal(EdgeOperations.DefaultEdgeName, wrong.Name);
        Assert.Empty(wrong.Attributes);
        Assert.Empty(Assert.Single(keptTypes));
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_IgnoresNonCustomNames()
    {
        var person = SignatureNode("person", "Person");
        var org = SignatureNode("org", "Organization");
        var edge = SignatureEdge("EMPLOYED_BY", person, org);

        foreach (var strict in new[] { true, false })
        {
            var (kept, _) = Enforce([edge], [person, org], strict);
            Assert.Equal([edge], kept);
            Assert.Equal("EMPLOYED_BY", edge.Name);
        }
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_IgnoresCustomTypeWithoutSignature()
    {
        var person = SignatureNode("person", "Person");
        var org = SignatureNode("org", "Organization");
        var edge = SignatureEdge("MENTIONS", person, org);

        foreach (var strict in new[] { true, false })
        {
            var (kept, keptTypes) = Enforce([edge], [person, org], strict);
            Assert.Equal([edge], kept);
            Assert.Equal("MENTIONS", edge.Name);
            Assert.Equal("x", edge.Attributes["note"]);
            Assert.Empty(Assert.Single(keptTypes));
        }
    }

    [Fact]
    public void EnforceEdgeTypeSignatures_TreatsUnknownEndpointAsGenericEntity()
    {
        var person = SignatureNode("person", "Person");
        var missing = SignatureNode("missing", "Domain");
        var edge = SignatureEdge("WORKS_IN", person, missing);

        var (kept, _) = Enforce([edge], [person], strict: true);

        Assert.Empty(kept);
    }

    [Fact]
    public async Task ResolveExtractedEdges_AppliesSignatureBeforeDedupe()
    {
        var (clients, llm, _, embedder) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var person = SignatureNode("person", "Person");
        var org = SignatureNode("org", "Organization");
        var domain = SignatureNode("domain", "Domain");
        var wrong = SignatureEdge("WORKS_IN", person, org);
        var valid = SignatureEdge("WORKS_IN", person, domain);
        var episode = Episode();

        var (resolved, _, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [wrong, valid], episode, [person, org, domain], SignatureEdgeTypes, SignatureEdgeTypeMap,
            strictEdgeTypes: true);

        Assert.Equal([valid.Uuid], resolved.Select(e => e.Uuid));
        Assert.Equal(resolved, newEdges);
        Assert.Equal([valid.Fact], embedder.Batches[0]);

        var renamed = SignatureEdge("WORKS_IN", person, org);
        (resolved, _, _) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [renamed], episode, [person, org], SignatureEdgeTypes, SignatureEdgeTypeMap,
            strictEdgeTypes: false);

        Assert.Equal([EdgeOperations.DefaultEdgeName], resolved.Select(e => e.Name));

        // Same endpoints and fact, different names: the mismatched name is dropped first, so the exact-fact dedupe
        // keeps the valid one.
        var term = SignatureNode("term", "Term");
        var sameFactWrong = SignatureEdge("WORKS_IN", term, domain);
        var sameFactValid = SignatureEdge("MEANS", term, domain);
        sameFactValid.Fact = sameFactWrong.Fact;
        (resolved, _, _) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [sameFactWrong, sameFactValid], episode, [term, domain], SignatureEdgeTypes, SignatureEdgeTypeMap,
            strictEdgeTypes: true);

        Assert.Equal([(sameFactValid.Uuid, "MEANS")], resolved.Select(e => (e.Uuid, e.Name)));
    }

    [Fact]
    public async Task ResolveExtractedEdge_UsesIntegerIndicesForDuplicates()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.DedupeEdgesResolveEdge, new { duplicate_facts = new[] { 0, 1 }, contradicted_facts = Array.Empty<int>() });
        var extracted = Edge("source_uuid", "target_uuid", "User likes yoga");
        var episode = Episode();
        var related0 = Edge("source_uuid", "target_uuid", "User enjoys yoga", episodes: ["episode_1"]);
        var related1 = Edge("source_uuid", "target_uuid", "User practices yoga", episodes: ["episode_2"]);
        var related2 = Edge("source_uuid", "target_uuid", "User loves swimming", episodes: ["episode_3"]);

        var (resolved, invalidated, duplicates) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [related0, related1, related2], [], episode);

        Assert.Single(llm.Calls);
        Assert.Equal(2, duplicates.Count);
        Assert.Contains(related0, duplicates);
        Assert.Contains(related1, duplicates);
        Assert.Empty(invalidated);
        Assert.Equal(related0.Uuid, resolved.Uuid);
        Assert.Contains(episode.Uuid, resolved.Episodes);
    }

    [Fact]
    public async Task ResolveExtractedEdges_FastPathDeduplication()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var source = Node("source_uuid", "Assistant");
        var target = Node("target_uuid", "User");
        var edge1 = Edge(source.Uuid, target.Uuid, "assistant recommends yoga poses", name: "recommends");
        var edge2 = Edge(source.Uuid, target.Uuid, "  Assistant Recommends YOGA Poses  ", name: "recommends");
        var edge3 = Edge(source.Uuid, target.Uuid, "assistant recommends yoga poses", name: "recommends");

        var (resolved, invalidated, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [edge1, edge2, edge3], Episode(), [source, target], NoTypes, EmptyMap);

        // One fact left to resolve: one timestamp call.
        Assert.Single(llm.CallsFor(PromptNames.ExtractEdgesExtractTimestamps));
        Assert.Single(resolved);
        Assert.Empty(invalidated);
        Assert.Equal(resolved, newEdges);
    }

    [Theory]
    [InlineData("  Assistant Recommends YOGA Poses  ", "recommends", new[] { "edge-1" })]
    [InlineData("assistant likes yoga poses", "recommends", new[] { "edge-1", "edge-2" })]
    [InlineData("  Assistant Recommends YOGA Poses  ", "recommended_by", new[] { "edge-1", "edge-2" })]
    public async Task ResolveExtractedEdges_FastPathDeduplicatesReverseDirectionByFact(
        string reverseFact, string reverseName, string[] expectedEdgeUuids)
    {
        var (clients, llm, driver, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        driver.EdgeSearchOverride = _ => [];
        var source = Node("source_uuid", "Assistant");
        var target = Node("target_uuid", "User");
        var edge1 = Edge(source.Uuid, target.Uuid, "assistant recommends yoga poses", name: "recommends",
            uuid: "edge-1");
        var edge2 = Edge(target.Uuid, source.Uuid, reverseFact, name: reverseName, uuid: "edge-2");

        var (resolved, invalidated, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [edge1, edge2], Episode(), [source, target], NoTypes, EmptyMap);

        Assert.Equal(expectedEdgeUuids, resolved.Select(e => e.Uuid));
        Assert.Same(edge1, resolved[0]);
        Assert.Equal(expectedEdgeUuids.Length, llm.CallsFor(PromptNames.ExtractEdgesExtractTimestamps).Count);
        Assert.Equal(2 * expectedEdgeUuids.Length, driver.SearchFiltersUsed.Count);
        Assert.Empty(invalidated);
        Assert.Equal(resolved, newEdges);
    }

    [Fact]
    public async Task ExtractEdges_ShowsEveryEdgeTypeSignature()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge, new { edges = Array.Empty<object>() });
        var edgeTypeMap = new Dictionary<(string Source, string Target), IReadOnlyList<string>>
        {
            [("Person", "Person")] = ["InterpersonalRelationship"],
            [("Person", "Entity")] = ["InterpersonalRelationship"],
            [("Person", "City")] = ["LocatedIn"],
            [("Entity", "City")] = ["LocatedIn"],
        };
        var edgeTypes = new Dictionary<string, Type>
        {
            ["InterpersonalRelationship"] = typeof(InterpersonalRelationship),
            ["LocatedIn"] = typeof(LocatedIn),
            ["Unmapped"] = typeof(MentionsEdge),
        };

        await EdgeOperations.ExtractEdgesAsync(clients, Episode(), [Node("a", "Alice", "Person")], [], edgeTypeMap,
            "group_1", edgeTypes);

        var text = Assert.Single(llm.Calls).Text;
        Assert.Contains(
            "{\"fact_type_name\": \"InterpersonalRelationship\", \"fact_type_signatures\": [[\"Person\", \"Person\"], " +
            "[\"Person\", \"Entity\"]], \"fact_type_description\": \"A relationship between two people.\"}",
            text);
        Assert.Contains(
            "{\"fact_type_name\": \"LocatedIn\", \"fact_type_signatures\": [[\"Person\", \"City\"], [\"Entity\", \"City\"]]",
            text);
        Assert.Contains(
            "{\"fact_type_name\": \"Unmapped\", \"fact_type_signatures\": [[\"Entity\", \"Entity\"]]", text);
    }

    private static object ExtractedEdgesResponse(params (string Source, string Target, string Relation, string Fact)[] edges) =>
        new
        {
            edges = edges.Select(e => new
            {
                source_entity_name = e.Source,
                target_entity_name = e.Target,
                relation_type = e.Relation,
                fact = e.Fact,
                valid_at = (string?)null,
                invalid_at = (string?)null,
                episode_indices = new[] { 0 },
            }).ToArray(),
        };

    [Fact]
    public async Task ExtractEdges_KeepsSelfEdges()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge, ExtractedEdgesResponse(
            ("Alice", "Bob", "CONGRATULATED", "Alice congratulated Bob"),
            ("Alice", "Alice", "FEELS_HAPPY", "Alice feels happy")));
        var alice = Node("alice_uuid", "Alice", "Person");
        var bob = Node("bob_uuid", "Bob", "Person");

        var (edges, _) = await EdgeOperations.ExtractEdgesAsync(
            clients, Episode("ep_uuid", "Alice congratulated Bob. Alice feels happy."), [alice, bob], [], EmptyMap,
            "group_1");

        Assert.Equal(
            new HashSet<(string, string, string)>
            {
                ("alice_uuid", "bob_uuid", "CONGRATULATED"),
                ("alice_uuid", "alice_uuid", "FEELS_HAPPY"),
            },
            edges.Select(e => (e.SourceNodeUuid, e.TargetNodeUuid, e.Name)).ToHashSet());
    }

    [Fact]
    public async Task ExtractEdges_KeepsValidEdgesWithSameNameDifferentNodes()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge,
            ExtractedEdgesResponse(("Alice", "Paris", "LIVES_IN", "Alice lives in Paris")));
        var alice = Node("alice_uuid", "Alice", "Person");
        var paris = Node("paris_uuid", "Paris", "City");

        var (edges, _) = await EdgeOperations.ExtractEdgesAsync(
            clients, Episode("ep_uuid", "Alice lives in Paris."), [alice, paris], [], EmptyMap, "group_1");

        var edge = Assert.Single(edges);
        Assert.Equal("alice_uuid", edge.SourceNodeUuid);
        Assert.Equal("paris_uuid", edge.TargetNodeUuid);
    }

    [Fact]
    public async Task ExtractEdges_MaterializesMissingTargetEndpoint()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge, ExtractedEdgesResponse(
            ("AMI", "version 2.18.1", "PINNED_AT", "AMI is pinned at version 2.18.1.")));
        var ami = Node("ami_uuid", "AMI");
        var nodes = new List<EntityNode> { ami };

        var (edges, materialized) = await EdgeOperations.ExtractEdgesAsync(
            clients, Episode("ep_uuid", "AMI is pinned at version 2.18.1."), nodes, [], EmptyMap, "group_1");

        var edge = Assert.Single(edges);
        Assert.Equal("PINNED_AT", edge.Name);
        Assert.Equal("ami_uuid", edge.SourceNodeUuid);
        Assert.Equal(new HashSet<string> { "AMI", "version 2.18.1" }, nodes.Select(n => n.Name).ToHashSet());
        var node = Assert.Single(materialized);
        Assert.Equal("version 2.18.1", node.Name);
        Assert.Equal("group_1", node.GroupId);
        Assert.Equal(node.Uuid, edge.TargetNodeUuid);
        Assert.NotEqual(edge.SourceNodeUuid, edge.TargetNodeUuid);
    }

    [Description("Software is pinned at a version.")]
    public sealed class PinnedAt;

    [Fact]
    public async Task ExtractEdges_DoesNotKeepOrphansWhenEdgeFiltered()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge, ExtractedEdgesResponse(
            ("AMI", "version 2.18.1", "NOT_IN_ONTOLOGY", "AMI is pinned at version 2.18.1.")));
        var nodes = new List<EntityNode> { Node("ami_uuid", "AMI") };

        var (edges, materialized) = await EdgeOperations.ExtractEdgesAsync(
            clients,
            Episode("ep_uuid", "AMI is pinned at version 2.18.1."),
            nodes,
            [],
            new Dictionary<(string Source, string Target), IReadOnlyList<string>> { [("Entity", "Entity")] = ["PINNED_AT"] },
            "group_1",
            new Dictionary<string, Type> { ["PINNED_AT"] = typeof(PinnedAt) },
            strictEdgeTypes: true);

        Assert.Empty(edges);
        Assert.Empty(materialized);
        Assert.Equal(["AMI"], nodes.Select(n => n.Name));
    }

    [Description("One product integrates with another product.")]
    public sealed class IntegratesWith;

    [Fact]
    public async Task ExtractEdges_StrictEdgeTypesFiltersDerivedRelations()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge, ExtractedEdgesResponse(
            ("Atlas Home Robot", "Acme Smart Hub", "INTEGRATES_WITH", "Atlas Home Robot integrates with Acme Smart Hub."),
            ("Atlas Home Robot", "Acme Smart Hub", "SOLD_WITH", "Atlas Home Robot is sold with Acme Smart Hub.")));
        var atlas = Node("atlas_uuid", "Atlas Home Robot", "Entity", "Product");
        var hub = Node("hub_uuid", "Acme Smart Hub", "Entity", "Product");

        var (edges, _) = await EdgeOperations.ExtractEdgesAsync(
            clients,
            Episode("ep_uuid", "Atlas Home Robot integrates with Acme Smart Hub."),
            [atlas, hub],
            [],
            new Dictionary<(string Source, string Target), IReadOnlyList<string>> { [("Product", "Product")] = ["INTEGRATES_WITH"] },
            "group_1",
            new Dictionary<string, Type> { ["INTEGRATES_WITH"] = typeof(IntegratesWith) },
            strictEdgeTypes: true);

        Assert.Equal(["INTEGRATES_WITH"], edges.Select(e => e.Name));
        var text = Assert.Single(llm.Calls).Text;
        Assert.Contains("If a relationship does not match any FACT_TYPE, skip it", text);
        Assert.Contains("Do not derive a new relation_type", text);
    }

    [Fact]
    public async Task ExtractEdges_StrictWithoutEdgeTypesExtractsNothing()
    {
        var (clients, llm, _, _) = Clients();

        var (edges, materialized) = await EdgeOperations.ExtractEdgesAsync(
            clients, Episode(), [Node("a", "A")], [], EmptyMap, strictEdgeTypes: true);

        Assert.Empty(edges);
        Assert.Empty(materialized);
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task ExtractEdges_BuildsTheUpstreamContext()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesEdge, new { edges = Array.Empty<object>() });
        var previous = Episode("prev", "Earlier message", new DateTimeOffset(2025, 1, 2, 9, 30, 15, TimeSpan.Zero));
        var episode = Episode("ep", "Alice met Bob", new DateTimeOffset(2025, 1, 3, 9, 30, 15, TimeSpan.Zero),
            groupId: "episode_group");

        await EdgeOperations.ExtractEdgesAsync(
            clients, episode, [Node("a", "Alice", "Entity", "Person")], [previous], EmptyMap,
            customExtractionInstructions: "Be careful.");

        var call = Assert.Single(llm.Calls);
        Assert.Equal("episode_group", call.GroupId);
        Assert.Equal(EdgeOperations.ExtractEdgesMaxTokens, call.MaxTokens);
        Assert.Contains("[{\"content\": \"Earlier message\", \"timestamp\": \"2025-01-02T09:30:15+00:00\"}]", call.Text);
        Assert.Contains("<CURRENT_MESSAGE>\nAlice met Bob\n</CURRENT_MESSAGE>", call.Text);
        Assert.Contains("[{\"name\": \"Alice\", \"entity_types\": [\"Entity\", \"Person\"]}]", call.Text);
        Assert.Contains("2025-01-03 09:30:15+00:00  # ISO 8601", call.Text);
        Assert.Contains("Be careful.", call.Text);
        Assert.DoesNotContain("Episode Attribution", call.Text);
        Assert.DoesNotContain("<FACT_TYPES>", call.Text);
    }

    [Fact]
    public async Task ExtractEdges_SeveralEpisodesAttributeFactsAndDates()
    {
        var (clients, llm, _, _) = Clients();
        var t0 = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        llm.On(PromptNames.ExtractEdgesEdge, new
        {
            edges = new object[]
            {
                new
                {
                    source_entity_name = "alice", target_entity_name = " Bob ", relation_type = "KNOWS",
                    fact = "Alice knows Bob", valid_at = "2024-12-31T00:00:00Z", invalid_at = "not a date",
                    episode_indices = new[] { 1, 7 },
                },
                new
                {
                    source_entity_name = "Alice", target_entity_name = "Bob", relation_type = "WILL_MEET",
                    fact = "Alice will meet Bob by March 2026", valid_at = "2026-03-01T00:00:00Z",
                    invalid_at = "2026-03-01T00:00:00Z", episode_indices = new[] { 9 },
                },
                new
                {
                    source_entity_name = "", target_entity_name = "Bob", relation_type = "X", fact = "dropped",
                    episode_indices = new[] { 0 },
                },
                new
                {
                    source_entity_name = "Alice", target_entity_name = "Bob", relation_type = "X", fact = "   ",
                    episode_indices = new[] { 0 },
                },
            },
        });
        var episodes = new[] { Episode("e0", "first", t0), Episode("e1", "second", t1) };

        var (edges, _) = await EdgeOperations.ExtractEdgesAsync(
            clients, episodes, [Node("a", "Alice"), Node("b", "Bob")], [], EmptyMap, "g");

        var text = Assert.Single(llm.Calls).Text;
        Assert.Contains("8. **Episode Attribution**", text);
        Assert.Contains("[Episode 0] (timestamp: 2025-01-01T00:00:00+00:00)\nfirst", text);
        Assert.Contains("2025-02-01 00:00:00+00:00  # ISO 8601", text);

        Assert.Equal(2, edges.Count);
        var knows = edges[0];
        Assert.Equal(("a", "b", "g"), (knows.SourceNodeUuid, knows.TargetNodeUuid, knows.GroupId));
        Assert.Equal(["e1"], knows.Episodes);
        Assert.Equal(t1, knows.ReferenceTime);
        Assert.Equal(new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero), knows.ValidAt);
        Assert.Null(knows.InvalidAt);

        // Out-of-range indices fall back to every episode and the primary reference time; the future-tense fact is
        // valid from the reference time and its soft deadline is not an end date.
        var meet = edges[1];
        Assert.Equal(["e0", "e1"], meet.Episodes);
        Assert.Equal(t0, meet.ReferenceTime);
        Assert.Equal(t0, meet.ValidAt);
        Assert.Null(meet.InvalidAt);
    }

    [Fact]
    public async Task ResolveExtractedEdge_OvercapAttributePreservesPrior()
    {
        var (clients, llm, _, _) = Clients();
        var bleed = string.Concat(Enumerable.Repeat(
            "true (implied by context, but no new information explicitly stated. I should ideally remove it or set " +
            "it to null. However, the instruction is to preserve existing values...) ", 30));
        llm.On(PromptNames.ExtractEdgesExtractAttributes, new Dictionary<string, object?>
            {
                ["title"] = "Senior Engineer",
                ["is_current"] = bleed,
            })
            .On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var extracted = Edge("person_uuid", "company_uuid", "Sam was promoted to Senior Engineer at Northwind",
            name: "EMPLOYMENT",
            attributes: new Dictionary<string, object?> { ["title"] = "Engineer", ["is_current"] = "true" });

        var (resolved, invalidated, duplicates) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [], [], Episode(content: "Sam was promoted to Senior Engineer."),
            new Dictionary<string, Type> { ["EMPLOYMENT"] = typeof(EmploymentEdge) });

        Assert.Equal("Senior Engineer", resolved.Attributes["title"]);
        Assert.Equal("true", resolved.Attributes["is_current"]);
        Assert.Empty(duplicates);
        Assert.Empty(invalidated);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractEdgesExtractAttributes));
        Assert.Equal(typeof(EmploymentEdge), call.ResponseModel);
        Assert.True(call.AttributeExtraction);
        Assert.Contains("{\"title\": \"Engineer\", \"is_current\": \"true\"}", call.Text);
    }

    [Fact]
    public async Task ResolveExtractedEdge_KeepsInitialNullAttributes()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractAttributes, new Dictionary<string, object?>
            {
                ["title"] = "Senior Engineer",
                ["is_current"] = null,
            })
            .On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var extracted = Edge("person_uuid", "company_uuid", "Sam is a Senior Engineer at Northwind",
            name: "EMPLOYMENT");

        var (resolved, invalidated, duplicates) = await EdgeOperations.ResolveExtractedEdgeAsync(
            clients, extracted, [], [], Episode(),
            new Dictionary<string, Type> { ["EMPLOYMENT"] = typeof(EmploymentEdge) });

        Assert.Equal(
            new Dictionary<string, object?> { ["title"] = "Senior Engineer", ["is_current"] = null },
            resolved.Attributes);
        Assert.Empty(duplicates);
        Assert.Empty(invalidated);
    }

    [Fact]
    public async Task ResolveExtractedEdges_DedupesAgainstReverseDirection()
    {
        var (clients, llm, driver, _) = Clients();
        var reverseEdge = Edge("target_uuid", "source_uuid", "Yoga is liked by the user", name: "liked_by",
            uuid: "reverse_0", createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        driver.EdgeSearchOverride = filter => filter.EdgeSourceNodeUuids is not null ? [reverseEdge] : [];
        llm.On(PromptNames.DedupeEdgesResolveEdge, new { duplicate_facts = new[] { 0 }, contradicted_facts = Array.Empty<int>() });
        var extracted = Edge("source_uuid", "target_uuid", "User likes yoga", name: "likes");
        var episode = Episode();

        var (resolved, invalidated, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [extracted], episode, [Node("source_uuid", "User"), Node("target_uuid", "Yoga")],
            NoTypes, EmptyMap);

        Assert.Equal(reverseEdge.Uuid, resolved[0].Uuid);
        Assert.Contains(episode.Uuid, resolved[0].Episodes);
        Assert.Empty(invalidated);
        Assert.Empty(newEdges);
    }

    private static EntityEdge CandidateEdge(int idx, string fact, string source = "source_uuid", string target = "target_uuid") =>
        Edge(source, target, fact, name: "relates_to", uuid: $"candidate_{idx}");

    [Fact]
    public void SamePairEdges_KeepsBothOrientationsDropsStrays()
    {
        var extracted = CandidateEdge(0, "fact", "a", "b");
        var forward = CandidateEdge(1, "forward", "a", "b");
        var reverse = CandidateEdge(2, "reverse", "b", "a");
        var selfLoop = CandidateEdge(3, "self loop", "a", "a");
        var stray = CandidateEdge(4, "stray", "a", "c");

        var result = EdgeOperations.SamePairEdges(extracted, [forward, reverse, selfLoop, stray]);

        Assert.Equal(["candidate_1", "candidate_2"], result.Select(e => e.Uuid));
    }

    [Fact]
    public void SamePairEdges_SelfLoopPair()
    {
        var extracted = CandidateEdge(0, "fact", "a", "a");
        var selfLoop = CandidateEdge(1, "self loop", "a", "a");
        var other = CandidateEdge(2, "other", "a", "b");

        var result = EdgeOperations.SamePairEdges(extracted, [selfLoop, other]);

        Assert.Equal(["candidate_1"], result.Select(e => e.Uuid));
    }

    [Fact]
    public void PairSearchFilter_SetsBothEndpointLists()
    {
        var filter = EdgeOperations.PairSearchFilter(MockExtractedEdge());

        Assert.Equal(["source_uuid", "target_uuid"], filter.EdgeSourceNodeUuids!);
        Assert.Equal(["source_uuid", "target_uuid"], filter.EdgeTargetNodeUuids!);
        Assert.Null(filter.EdgeUuids);
    }

    [Fact]
    public async Task ResolveExtractedEdges_PairScopesDuplicateSearch()
    {
        var (clients, _, driver, _) = Clients();
        var existing = CandidateEdge(0, "User likes yoga");
        driver.EdgeSearchOverride = filter => filter.EdgeSourceNodeUuids is not null ? [existing] : [];
        var extracted = Edge("source_uuid", "target_uuid", "  user LIKES yoga ", name: "likes");

        var (resolved, invalidated, _) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [extracted], Episode(), [Node("source_uuid", "User"), Node("target_uuid", "Yoga")],
            NoTypes, EmptyMap);

        // Exactly two searches: the pair-scoped duplicate search, then the graph-wide invalidation search.
        var filters = driver.SearchFiltersUsed;
        Assert.Equal(2, filters.Count);
        Assert.Equal(["source_uuid", "target_uuid"], filters[0].EdgeSourceNodeUuids!);
        Assert.Equal(["source_uuid", "target_uuid"], filters[0].EdgeTargetNodeUuids!);
        Assert.Null(filters[1].EdgeSourceNodeUuids);
        Assert.Null(filters[1].EdgeUuids);
        // Resolved by the exact-fact fast path.
        Assert.Equal(existing.Uuid, resolved[0].Uuid);
        Assert.Empty(invalidated);
    }

    [Fact]
    public async Task ResolveExtractedEdges_MergesOverrideCandidates()
    {
        var (clients, llm, driver, _) = Clients();
        driver.EdgeSearchOverride = _ => [];
        var overrideEdge = Edge("source_uuid", "target_uuid", "User likes yoga", name: "likes", uuid: "override_edge");
        var otherGroup = Edge("source_uuid", "target_uuid", "User likes yoga", name: "likes", uuid: "other_group",
            groupId: "group_2");
        var extracted = Edge("source_uuid", "target_uuid", "User likes yoga", name: "likes");

        var (resolved, _, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [extracted], Episode(), [Node("source_uuid", "User"), Node("target_uuid", "Yoga")],
            NoTypes, EmptyMap, existingEdgesOverride: [otherGroup, overrideEdge]);

        // The override joined the duplicate candidates, so the exact-fact fast path picked it with no LLM call.
        Assert.Same(overrideEdge, Assert.Single(resolved));
        Assert.Empty(newEdges);
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task ResolveExtractedEdges_ReusesFactEmbeddingForSearches()
    {
        var (clients, llm, driver, embedder) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        var extracted = Edge("source_uuid", "target_uuid", "User likes yoga", name: "likes");

        await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [extracted], Episode(), [Node("source_uuid", "User"), Node("target_uuid", "Yoga")],
            NoTypes, EmptyMap);

        // Duplicate search + invalidation search, both with the precomputed vector.
        var vectors = driver.EdgeSearches.Where(s => s.Kind == "similarity").Select(s => s.Vector!).ToList();
        Assert.Equal(2, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(extracted.FactEmbedding!, v));
        Assert.Equal(0, embedder.CreateCalls);
    }

    [Fact]
    public async Task ResolveExtractedEdge_ExtractsTimestampsForUntaggedPairwise()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, new { valid_at = "2024-05-01T00:00:00Z", invalid_at = (string?)null });
        var extracted = Edge("source_uuid", "target_uuid", "Alice knows Bob.", name: "KNOWS", episodes: ["episode_1"]);
        var episode = Episode(content: "Alice knows Bob.",
            validAt: new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.FromHours(2)));

        await EdgeOperations.ResolveExtractedEdgeAsync(clients, extracted, [], [], episode);

        var call = Assert.Single(llm.CallsFor(PromptNames.ExtractEdgesExtractTimestamps));
        Assert.Contains("<FACT>\nAlice knows Bob.\n</FACT>", call.Text);
        Assert.Contains("<REFERENCE TIME>\n2024-06-01T10:00:00+00:00\n</REFERENCE TIME>", call.Text);
        Assert.Equal(ModelSize.Small, call.ModelSize);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), extracted.ValidAt);
    }

    [Fact]
    public async Task ExtractEdgeTimestamps_PrefersEdgeReferenceTime()
    {
        var (clients, llm, _, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, new { valid_at = "2024-01-02T00:00:00Z", invalid_at = (string?)null });
        var edgeReferenceTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var edge = Edge("source_uuid", "target_uuid", "Alice knows Bob.", name: "KNOWS");
        edge.ReferenceTime = edgeReferenceTime;
        var episode = Episode(content: edge.Fact, validAt: new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));

        await EdgeOperations.ExtractEdgeTimestampsAsync(clients, edge, episode);

        var call = Assert.Single(llm.Calls);
        Assert.Contains("<REFERENCE TIME>\n2024-01-01T00:00:00+00:00\n</REFERENCE TIME>", call.Text);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), edge.ValidAt);
        Assert.Null(edge.InvalidAt);
    }

    [Fact]
    public async Task ExtractEdgeTimestamps_SkipsDatedEdgesAndSwallowsFailures()
    {
        var (clients, llm, _, _) = Clients();
        var dated = Edge("a", "b", "fact", validAt: DateTimeOffset.UtcNow);
        await EdgeOperations.ExtractEdgeTimestampsAsync(clients, dated, Episode());
        Assert.Empty(llm.Calls);

        llm.On(PromptNames.ExtractEdgesExtractTimestamps, _ => throw new InvalidOperationException("boom"));
        var undated = Edge("a", "b", "fact");
        await EdgeOperations.ExtractEdgeTimestampsAsync(clients, undated, Episode());
        Assert.Single(llm.Calls);
        Assert.Null(undated.ValidAt);
    }

    [Fact]
    public async Task ExtractEdgeTimestamps_DoesNotSwallowAModelCallRefusal()
    {
        // The base LLMClient asks the gate; the usual fake overrides GenerateResponseAsync and would not.
        var llm = new GraphitiNet.Tests.GraphitiApi.ScriptedLlmClient("""{"valid_at": null, "invalid_at": null}""");
        var clients = Clients().Clients with { LlmClient = llm };
        var gate = new DelegateModelCallGate((_, _) => ValueTask.FromResult(false));
        using var scope = GraphitiCallContext.Begin(
            new GraphitiCallContext { Operation = "add_episode", ModelCallGate = gate });

        await Assert.ThrowsAsync<ModelCallRefusedException>(() =>
            EdgeOperations.ExtractEdgeTimestampsAsync(clients, Edge("a", "b", "fact"), Episode()));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task ResolveExtractedEdges_InvalidatesContradictedFactsAndRecordsWhoDidIt()
    {
        var (clients, llm, driver, _) = Clients();
        var now = DateTimeOffset.UtcNow;
        var old = Edge("alice", "acme", "Alice works at Acme", uuid: "old", validAt: now.AddDays(-30));
        driver.EdgeSearchOverride = filter => filter.EdgeSourceNodeUuids is null ? [old] : [];
        llm.On(PromptNames.DedupeEdgesResolveEdge, new { duplicate_facts = Array.Empty<int>(), contradicted_facts = new[] { 0 } });
        var extracted = Edge("alice", "beta", "Alice works at BetaCorp", validAt: now);
        var invalidatedBy = new Dictionary<string, List<string>>();

        var (resolved, invalidated, newEdges) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [extracted], Episode(), [Node("alice", "Alice"), Node("beta", "BetaCorp")], NoTypes, EmptyMap,
            invalidatedBy: invalidatedBy);

        Assert.Same(extracted, Assert.Single(resolved));
        Assert.Equal(resolved, newEdges);
        var closed = Assert.Single(invalidated);
        Assert.Same(old, closed);
        Assert.Equal(now, closed.InvalidAt);
        Assert.NotNull(closed.ExpiredAt);
        Assert.NotNull(closed.FactEmbedding);
        Assert.Equal([extracted.Uuid], invalidatedBy["old"]);
    }

    [Fact]
    public async Task ResolveExtractedEdges_LoadsMissingEndpointsFromTheEdgeGroup()
    {
        var (clients, llm, driver, _) = Clients();
        llm.On(PromptNames.ExtractEdgesExtractTimestamps, NoTimestamps);
        await driver.EntityNodeOps.SaveAsync(Node("person", "Pat", "Person"));
        await driver.EntityNodeOps.SaveAsync(Node("domain", "Math", "Domain"));
        var edge = Edge("person", "domain", "Pat works in math", name: "WORKS_IN");

        var (resolved, _, _) = await EdgeOperations.ResolveExtractedEdgesAsync(
            clients, [edge], Episode(), [], SignatureEdgeTypes, SignatureEdgeTypeMap, strictEdgeTypes: true);

        // The stored endpoints' labels match WORKS_IN's signature, so the edge is kept.
        Assert.Equal("WORKS_IN", Assert.Single(resolved).Name);
    }

    [Fact]
    public void BuildEpisodicEdges_LinksEachNodeToItsEpisodes()
    {
        var created = DateTimeOffset.UtcNow;
        var a = Node("a", "A");
        var b = Node("b", "B");

        var all = EdgeOperations.BuildEpisodicEdges([a, b], ["e0", "e1"], created);
        Assert.Equal(
            [("e0", "a"), ("e1", "a"), ("e0", "b"), ("e1", "b")],
            all.Select(e => (e.SourceNodeUuid, e.TargetNodeUuid)));
        Assert.All(all, e => Assert.Equal(("group_1", created), (e.GroupId, e.CreatedAt)));

        var mapped = EdgeOperations.BuildEpisodicEdges([a, b], ["e0", "e1"], created,
            new Dictionary<string, IReadOnlyList<int>> { ["a"] = [1, 5, -1] });
        Assert.Equal(
            [("e1", "a"), ("e0", "b"), ("e1", "b")],
            mapped.Select(e => (e.SourceNodeUuid, e.TargetNodeUuid)));

        var single = EdgeOperations.BuildEpisodicEdges([a], "e9", created);
        Assert.Equal(("e9", "a"), (single[0].SourceNodeUuid, single[0].TargetNodeUuid));
    }

    [Fact]
    public void BuildCommunityEdges_LinksCommunityToEachMember()
    {
        var community = new CommunityNode { Name = "c", GroupId = "cg" };
        var created = DateTimeOffset.UtcNow;

        var edges = EdgeOperations.BuildCommunityEdges([Node("a", "A"), Node("b", "B")], community, created);

        Assert.Equal(
            [(community.Uuid, "a", "cg"), (community.Uuid, "b", "cg")],
            edges.Select(e => (e.SourceNodeUuid, e.TargetNodeUuid, e.GroupId)));
    }

    [Fact]
    public async Task FilterExistingDuplicateOfEdges_DropsPairsAlreadyMarked()
    {
        var (_, _, driver, _) = Clients();
        var a = Node("a", "A");
        var b = Node("b", "B");
        var c = Node("c", "C");
        foreach (var node in new[] { a, b, c })
        {
            await driver.EntityNodeOps.SaveAsync(node);
        }

        await driver.EntityEdgeOps.SaveAsync(Edge("a", "b", "A is a duplicate of B", name: "IS_DUPLICATE_OF"));
        await driver.EntityEdgeOps.SaveAsync(Edge("c", "a", "C relates to A", name: "RELATES_TO"));

        var remaining = await EdgeOperations.FilterExistingDuplicateOfEdgesAsync(driver, [(a, b), (c, a), (b, a)]);

        Assert.Equal([("c", "a"), ("b", "a")], remaining.Select(p => (p.Source.Uuid, p.Target.Uuid)));
        Assert.Empty(await EdgeOperations.FilterExistingDuplicateOfEdgesAsync(driver, []));
    }
}
