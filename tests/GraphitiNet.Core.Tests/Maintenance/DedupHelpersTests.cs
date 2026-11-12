// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_node_operations.py (Graphiti v0.31.2) and changed for .NET.

using System.Text;
using GraphitiNet.Maintenance;
using static GraphitiNet.Tests.Maintenance.TestHarness;

namespace GraphitiNet.Tests.Maintenance;

public class DedupHelpersTests
{
    [Fact]
    public void Blake2b_matches_python_hashlib()
    {
        // Values from Python: int.from_bytes(blake2b(s.encode(), digest_size=8).digest(), 'big').
        Assert.Equal(7705568351334315143UL, Big(Blake2b.Hash(Encoding.UTF8.GetBytes("0:abc"), 8)));
        Assert.Equal(8869559195517739721UL, Big(Blake2b.Hash(Encoding.UTF8.GetBytes("31:lic"), 8)));
        Assert.Equal(16476032584258269876UL, Big(Blake2b.Hash([], 8)));
        Assert.Equal(10384424932072301570UL, Big(Blake2b.Hash(Encoding.UTF8.GetBytes(new string('x', 128)), 8)));
        Assert.Equal(8962578962434949606UL, Big(Blake2b.Hash(Encoding.UTF8.GetBytes(new string('y', 129)), 8)));
        Assert.Equal(7651359336849078874UL, Big(Blake2b.Hash(Encoding.UTF8.GetBytes("5:é"), 8)));
        Assert.Equal(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexStringLower(Blake2b.Hash("abc"u8, 64)));

        static ulong Big(byte[] bytes) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    [Fact]
    public void Hash_shingle_and_minhash_match_upstream_values()
    {
        Assert.Equal(7705568351334315143UL, DedupHelpers.HashShingle("abc", 0));
        var signature = DedupHelpers.MinHashSignature(DedupHelpers.Shingles("alice"));
        Assert.Equal(
            [6442218702044509519UL, 5485487455325798234UL, 5209621350033769776UL, 848456258298321966UL, 1981166575756687985UL],
            signature.Take(5));
    }

    [Fact]
    public void Normalize_helpers()
    {
        Assert.Equal("alice smith", DedupHelpers.NormalizeStringExact("  Alice   Smith "));
        Assert.Equal("alice smith", DedupHelpers.NormalizeNameForFuzzy("Alice-Smith!"));
    }

    [Fact]
    public void Name_entropy_variants()
    {
        Assert.True(DedupHelpers.NameEntropy("alice") > DedupHelpers.NameEntropy("aaaaa"));
        Assert.Equal(0.0, DedupHelpers.NameEntropy(""));
    }

    [Fact]
    public void Has_high_entropy_rules()
    {
        Assert.True(DedupHelpers.HasHighEntropy("meaningful name"));
        Assert.False(DedupHelpers.HasHighEntropy("aa"));
    }

    [Fact]
    public void Shingles_and_cache()
    {
        var shingles = DedupHelpers.Shingles("alice");
        Assert.True(shingles.SetEquals(["ali", "lic", "ice"]));
        Assert.True(DedupHelpers.CachedShingles("alice").SetEquals(shingles));
        Assert.Same(DedupHelpers.CachedShingles("alice"), DedupHelpers.CachedShingles("alice"));
        Assert.True(DedupHelpers.Shingles("a").SetEquals(["a"]));
        Assert.Empty(DedupHelpers.Shingles("ab"));
        Assert.Empty(DedupHelpers.Shingles(""));
    }

    [Fact]
    public void Hash_minhash_and_lsh()
    {
        string[] shingles = ["abc", "bcd", "cde"];
        var signature = DedupHelpers.MinHashSignature(shingles);
        Assert.Equal(32, signature.Count);
        var bands = DedupHelpers.LshBands(signature);
        Assert.Equal(8, bands.Count);
        Assert.All(bands, band => Assert.Equal(4, band.Values.Count));
        Assert.Equal(3, shingles.Select(s => DedupHelpers.HashShingle(s, 0)).Distinct().Count());
        Assert.Empty(DedupHelpers.MinHashSignature([]));
    }

    [Fact]
    public void Jaccard_similarity_edges()
    {
        var a = new HashSet<string> { "a", "b" };
        var b = new HashSet<string> { "a", "c" };
        Assert.Equal(1.0 / 3, DedupHelpers.JaccardSimilarity(a, b), 10);
        Assert.Equal(1.0, DedupHelpers.JaccardSimilarity(new HashSet<string>(), new HashSet<string>()));
        Assert.Equal(0.0, DedupHelpers.JaccardSimilarity(a, new HashSet<string>()));
    }

    [Fact]
    public void Build_candidate_indexes_populates_structures()
    {
        var candidate = Entity("Bob Dylan");
        var indexes = DedupHelpers.BuildCandidateIndexes([candidate]);

        Assert.Same(candidate, indexes.NormalizedExisting["bob dylan"][0]);
        Assert.Same(candidate, indexes.NodesByUuid[candidate.Uuid]);
        Assert.Contains(candidate.Uuid, indexes.ShinglesByCandidate.Keys);
        Assert.Contains(indexes.LshBuckets.Values, bucket => bucket.Contains(candidate.Uuid));
    }

    [Fact]
    public void Resolve_with_similarity_exact_match_updates_state()
    {
        var candidate = Entity("Charlie Parker");
        var extracted = Entity("Charlie Parker");
        var state = new DedupResolutionState(1);

        DedupHelpers.ResolveWithSimilarity([extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state);

        Assert.Equal(candidate.Uuid, state.ResolvedNodes[0]!.Uuid);
        Assert.Equal(candidate.Uuid, state.UuidMap[extracted.Uuid]);
        Assert.Empty(state.UnresolvedIndices);
        Assert.Equal([(extracted, candidate)], state.DuplicatePairs);
    }

    [Fact]
    public void Resolve_with_similarity_short_name_exact_match_resolves_deterministically()
    {
        var candidate = Entity("Nate", labels: ["Entity", "Person"]);
        var extracted = Entity("Nate", labels: ["Entity", "Person"]);
        var state = new DedupResolutionState(1);

        DedupHelpers.ResolveWithSimilarity([extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state);

        Assert.Equal(candidate.Uuid, state.ResolvedNodes[0]!.Uuid);
        Assert.Empty(state.UnresolvedIndices);
        Assert.Equal([(extracted, candidate)], state.DuplicatePairs);
    }

    [Fact]
    public void Resolve_with_similarity_short_name_no_candidate_defers_to_llm()
    {
        var extracted = Entity("Nate", labels: ["Entity", "Person"]);
        var state = new DedupResolutionState(1);

        DedupHelpers.ResolveWithSimilarity([extracted], DedupHelpers.BuildCandidateIndexes([]), state);

        Assert.Null(state.ResolvedNodes[0]);
        Assert.Empty(state.UuidMap);
        Assert.Equal([0], state.UnresolvedIndices);
        Assert.Empty(state.DuplicatePairs);
    }

    [Theory]
    [InlineData("Java")]
    [InlineData("Johnny Appleseed")]
    public void Resolve_with_similarity_multiple_exact_matches_defers_to_llm(string name)
    {
        var indexes = DedupHelpers.BuildCandidateIndexes([Entity(name), Entity(name)]);
        var state = new DedupResolutionState(1);

        DedupHelpers.ResolveWithSimilarity([Entity(name)], indexes, state);

        Assert.Null(state.ResolvedNodes[0]);
        Assert.Equal([0], state.UnresolvedIndices);
        Assert.Empty(state.DuplicatePairs);
    }

    [Fact]
    public void Resolve_with_similarity_low_entropy_defers_resolution()
    {
        var state = new DedupResolutionState(1);
        DedupHelpers.ResolveWithSimilarity([Entity("Bob")], DedupHelpers.BuildCandidateIndexes([]), state);

        Assert.Null(state.ResolvedNodes[0]);
        Assert.Equal([0], state.UnresolvedIndices);
    }

    [Fact]
    public void Resolve_with_similarity_fuzzy_match()
    {
        var candidate = Entity("Joe-Michaels");
        var extracted = Entity("Joe Michaels");
        var state = new DedupResolutionState(1);

        DedupHelpers.ResolveWithSimilarity([extracted], DedupHelpers.BuildCandidateIndexes([candidate]), state);

        Assert.Equal(candidate.Uuid, state.ResolvedNodes[0]!.Uuid);
    }

    [Fact]
    public void Identity_type_for_node_uses_label_with_identity_properties()
    {
        var node = Entity("Acme", labels: ["Entity", "Party", "Company"]);
        var both = new Dictionary<string, IReadOnlyList<string>> { ["Party"] = ["party_id"], ["Company"] = ["company_id"] };

        Assert.Equal("Company", DedupHelpers.IdentityTypeForNode(node, both));
        Assert.Equal("Party", DedupHelpers.IdentityTypeForNode(
            node, new Dictionary<string, IReadOnlyList<string>> { ["Party"] = ["party_id"] }));
    }

    [Fact]
    public void Identity_keys_compare_as_python_values()
    {
        var props = new Dictionary<string, IReadOnlyList<string>> { ["Company"] = ["code"] };
        var a = Entity("A", labels: ["Entity", "Company"], attributes: new() { ["code"] = 1L });
        var b = Entity("B", labels: ["Entity", "Company"], attributes: new() { ["code"] = 1.0 });
        var c = Entity("C", labels: ["Entity", "Company"], attributes: new() { ["code"] = "  1 " });
        var blank = Entity("D", labels: ["Entity", "Company"], attributes: new() { ["code"] = "  " });

        Assert.Equal(DedupHelpers.IdentityKeyForNode(a, props), DedupHelpers.IdentityKeyForNode(b, props));
        Assert.NotEqual(DedupHelpers.IdentityKeyForNode(a, props), DedupHelpers.IdentityKeyForNode(c, props));
        Assert.Null(DedupHelpers.IdentityKeyForNode(blank, props));
    }

    [Fact]
    public void Promote_resolved_node_merges_non_empty_attributes_and_promotes_labels()
    {
        var resolved = Entity("Audrey", attributes: new() { ["age"] = 30L, ["city"] = "Paris" });
        var extracted = Entity("Audrey", labels: ["Entity", "Person"], attributes: new() { ["age"] = null, ["city"] = " ", ["job"] = "chef" });

        var result = DedupHelpers.PromoteResolvedNode(extracted, resolved);

        Assert.Same(resolved, result);
        Assert.Equal(["Entity", "Person"], result.Labels);
        Assert.Equal(new Dictionary<string, object?> { ["age"] = 30L, ["city"] = "Paris", ["job"] = "chef" }, result.Attributes);
    }

    [Fact]
    public void Promote_resolved_node_keeps_labels_unless_strict_superset()
    {
        var resolved = Entity("Acme", labels: ["Entity", "Company"]);
        DedupHelpers.PromoteResolvedNode(Entity("Acme", labels: ["Entity", "Person"]), resolved);
        Assert.Equal(["Entity", "Company"], resolved.Labels);
    }
}
