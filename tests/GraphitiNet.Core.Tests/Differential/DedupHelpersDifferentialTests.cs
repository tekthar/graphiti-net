// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Numerics;
using System.Text.Json;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;

namespace GraphitiNet.Tests.Differential;

/// <summary>dedup_helpers.py against upstream's recorded results.</summary>
public class DedupHelpersDifferentialTests
{
    private const string File = "dedup_helpers";

    [Fact]
    public void NormalizeStringExact_matches_upstream() =>
        StringFunction("normalize_string_exact", DedupHelpers.NormalizeStringExact);

    [Fact]
    public void NormalizeNameForFuzzy_matches_upstream() =>
        StringFunction("normalize_name_for_fuzzy", DedupHelpers.NormalizeNameForFuzzy);

    [Fact]
    public void NameEntropy_matches_upstream()
    {
        // Math.Log2 and C's log2 may differ in the last bit.
        var m = new Mismatches("_name_entropy");
        foreach (var c in Fixture.Cases(File, "name_entropy"))
        {
            m.Equal(c, c.Output, DedupHelpers.NameEntropy(c.Input.GetString()!), relTol: 1e-12);
        }

        m.AssertNone();
    }

    [Fact]
    public void HasHighEntropy_matches_upstream()
    {
        var m = new Mismatches("_has_high_entropy");
        foreach (var c in Fixture.Cases(File, "has_high_entropy"))
        {
            m.Equal(c, c.Output, DedupHelpers.HasHighEntropy(c.Input.GetString()!));
        }

        m.AssertNone();
    }

    [Fact]
    public void Shingles_matches_upstream()
    {
        var m = new Mismatches("_shingles");
        foreach (var c in Fixture.Cases(File, "shingles"))
        {
            m.Equal(c, c.Output, DedupHelpers.Shingles(c.Input.GetString()!));
        }

        m.AssertNone();
    }

    [Fact]
    public void MinHashSignature_matches_upstream()
    {
        var m = new Mismatches("_minhash_signature");
        foreach (var c in Fixture.Cases(File, "minhash_signature"))
        {
            var signature = DedupHelpers.MinHashSignature(Fixture.Strings(c.Input));
            m.Equal(c, c.Output, signature.Select(h => new BigInteger(h)).ToArray());
        }

        m.AssertNone();
    }

    [Fact]
    public void LshBands_matches_upstream()
    {
        var m = new Mismatches("_lsh_bands");
        foreach (var c in Fixture.Cases(File, "lsh_bands"))
        {
            var signature = c.Input.EnumerateArray().Select(e => e.GetUInt64()).ToList();
            var bands = DedupHelpers.LshBands(signature)
                .Select(b => b.Values.Select(h => new BigInteger(h)).ToArray())
                .ToList();
            m.Equal(c, c.Output, bands);
        }

        m.AssertNone();
    }

    [Fact]
    public void JaccardSimilarity_matches_upstream()
    {
        var m = new Mismatches("_jaccard_similarity");
        foreach (var c in Fixture.Cases(File, "jaccard_similarity"))
        {
            var a = Fixture.Strings(c.Input[0]).ToHashSet(StringComparer.Ordinal);
            var b = Fixture.Strings(c.Input[1]).ToHashSet(StringComparer.Ordinal);
            m.Equal(c, c.Output, DedupHelpers.JaccardSimilarity(a, b));
        }

        m.AssertNone();
    }

    [Fact]
    public void ResolveWithSimilarity_matches_upstream()
    {
        var m = new Mismatches("_resolve_with_similarity");
        foreach (var c in Fixture.Cases(File, "resolve_with_similarity"))
        {
            var existing = c.Input.GetProperty("existing").EnumerateArray().Select(Node).ToList();
            var extracted = c.Input.GetProperty("extracted").EnumerateArray().Select(Node).ToList();
            var indexes = DedupHelpers.BuildCandidateIndexes(existing);
            var state = new DedupResolutionState(extracted.Count);
            DedupHelpers.ResolveWithSimilarity(extracted, indexes, state);

            var orderDependent = c.Output.GetProperty("order_dependent");
            var resolved = state.ResolvedNodes.Select(n => n?.Uuid).ToList();
            if (orderDependent.EnumerateObject().Any())
            {
                // Upstream picks among equally good fuzzy candidates in Python set order, which changes from run to
                // run; any of them is correct. Check those picks against the allowed set, the rest exactly. (The
                // generator leaves out the state those picks decide.)
                var expected = c.Output.GetProperty("resolved").EnumerateArray().ToList();
                for (var i = 0; i < resolved.Count; i++)
                {
                    if (orderDependent.TryGetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            out var allowed))
                    {
                        var ok = resolved[i] is { } r && Fixture.Strings(allowed).Contains(r);
                        m.Check(c, ok, () => $"resolved[{i}] = {resolved[i]} is not one of {allowed.GetRawText()}");
                    }
                    else
                    {
                        m.Equal(c, expected[i], resolved[i], $"resolved[{i}]");
                    }
                }

                m.Equal(c, c.Output.GetProperty("unresolved_indices"), state.UnresolvedIndices, "unresolved_indices");
                continue;
            }

            m.Equal(c, c.Output.GetProperty("resolved"), resolved, "resolved");
            m.Equal(c, c.Output.GetProperty("unresolved_indices"), state.UnresolvedIndices, "unresolved_indices");
            m.Equal(c, c.Output.GetProperty("uuid_map"), state.UuidMap, "uuid_map");
            m.Equal(c, c.Output.GetProperty("duplicate_pairs"),
                state.DuplicatePairs.Select(p => new[] { p.Extracted.Uuid, p.Resolved.Uuid }).ToList(),
                "duplicate_pairs");
            m.Equal(c, c.Output.GetProperty("existing_after"),
                existing.Select(n => new Dictionary<string, object?>
                {
                    ["uuid"] = n.Uuid,
                    ["labels"] = n.Labels,
                    ["attributes"] = n.Attributes,
                }).ToList(),
                "existing nodes after resolution");
        }

        m.AssertNone();
    }

    private static void StringFunction(string name, Func<string, string> port)
    {
        var m = new Mismatches(name);
        foreach (var c in Fixture.Cases(File, name))
        {
            m.Equal(c, c.Output, port(c.Input.GetString()!));
        }

        m.AssertNone();
    }

    private static EntityNode Node(JsonElement e) => new()
    {
        Uuid = e.GetProperty("uuid").GetString()!,
        Name = e.GetProperty("name").GetString()!,
        GroupId = "g",
        Labels = Fixture.Strings(e.GetProperty("labels")),
        Attributes = (Dictionary<string, object?>)Fixture.Decode(e.GetProperty("attributes"))!,
        CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
}
