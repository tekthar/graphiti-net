// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/dedup_helpers.py (Graphiti v0.31.2) and changed for .NET.

using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using GraphitiNet.Models;
using GraphitiNet.Prompts;

namespace GraphitiNet.Maintenance;

/// <summary>Deterministic entity deduplication helpers: exact names, MinHash/LSH fuzzy names and identity
/// properties.</summary>
/// <remarks>
/// Hashing is upstream's: 64-bit big-endian BLAKE2b of <c>"{seed}:{shingle}"</c> in UTF-8, so signatures and LSH
/// buckets equal upstream's for the same names.
/// </remarks>
public static partial class DedupHelpers
{
    /// <summary>Upstream <c>ENTITY_BASE_LABEL</c>: the label every entity carries.</summary>
    public const string EntityBaseLabel = "Entity";

    /// <summary>Upstream <c>_NAME_ENTROPY_THRESHOLD</c>: names below this entropy skip fuzzy matching.</summary>
    public const double NameEntropyThreshold = 1.5;

    /// <summary>Upstream <c>_MIN_NAME_LENGTH</c>.</summary>
    public const int MinNameLength = 6;

    /// <summary>Upstream <c>_MIN_TOKEN_COUNT</c>.</summary>
    public const int MinTokenCount = 2;

    /// <summary>Upstream <c>_FUZZY_JACCARD_THRESHOLD</c>: the shingle Jaccard similarity a fuzzy match needs.</summary>
    public const double FuzzyJaccardThreshold = 0.9;

    /// <summary>Upstream <c>_MINHASH_PERMUTATIONS</c>.</summary>
    public const int MinHashPermutations = 32;

    /// <summary>Upstream <c>_MINHASH_BAND_SIZE</c>.</summary>
    public const int MinHashBandSize = 4;

    private const int ShingleCacheSize = 512;

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> ShingleCache = new(StringComparer.Ordinal);

    /// <summary>Lowercases and collapses whitespace so equal names map to the same key (upstream
    /// <c>_normalize_string_exact</c>).</summary>
    public static string NormalizeStringExact(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return PythonText.Strip(Whitespace().Replace(PythonText.Lower(name), " "));
    }

    /// <summary>A fuzzier form that keeps only <c>a-z</c>, digits, apostrophes and single spaces, for shingles
    /// (upstream <c>_normalize_name_for_fuzzy</c>).</summary>
    public static string NormalizeNameForFuzzy(string name)
    {
        var normalized = NonFuzzyChars().Replace(NormalizeStringExact(name), " ");
        normalized = PythonText.Strip(normalized);
        return Whitespace().Replace(normalized, " ");
    }

    /// <summary>Shannon entropy (bits) over the characters of a name, ignoring spaces (upstream
    /// <c>_name_entropy</c>). Short or repetitive names score low.</summary>
    public static double NameEntropy(string normalizedName)
    {
        ArgumentNullException.ThrowIfNull(normalizedName);
        if (normalizedName.Length == 0)
        {
            return 0.0;
        }

        var counts = new Dictionary<int, int>();
        foreach (var rune in normalizedName.Replace(" ", "", StringComparison.Ordinal).EnumerateRunes())
        {
            counts[rune.Value] = counts.GetValueOrDefault(rune.Value) + 1;
        }

        var total = counts.Values.Sum();
        if (total == 0)
        {
            return 0.0;
        }

        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var probability = (double)count / total;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    /// <summary>Whether a name is long and varied enough for fuzzy matching (upstream <c>_has_high_entropy</c>).</summary>
    public static bool HasHighEntropy(string normalizedName)
    {
        ArgumentNullException.ThrowIfNull(normalizedName);
        var tokenCount = PythonText.Split(normalizedName).Length;
        if (CodePointLength(normalizedName) < MinNameLength && tokenCount < MinTokenCount)
        {
            return false;
        }

        return NameEntropy(normalizedName) >= NameEntropyThreshold;
    }

    /// <summary>The 3-character shingles of a name with spaces removed (upstream <c>_shingles</c>). A name of one
    /// character is its own shingle; an empty name has none.</summary>
    public static IReadOnlySet<string> Shingles(string normalizedName)
    {
        ArgumentNullException.ThrowIfNull(normalizedName);
        var cleaned = normalizedName.Replace(" ", "", StringComparison.Ordinal);
        var runes = cleaned.EnumerateRunes().Select(r => r.ToString()).ToList();
        if (runes.Count < 2)
        {
            return cleaned.Length > 0 ? new HashSet<string>(StringComparer.Ordinal) { cleaned } : new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < runes.Count - 2; i++)
        {
            result.Add(string.Concat(runes[i], runes[i + 1], runes[i + 2]));
        }

        return result;
    }

    /// <summary>
    /// <see cref="Shingles"/>, cached per name (upstream <c>_cached_shingles</c>, an <c>lru_cache</c> of 512). The
    /// cache is cleared when full rather than evicting the least recently used entry; results are the same.
    /// </summary>
    public static IReadOnlySet<string> CachedShingles(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (ShingleCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        if (ShingleCache.Count >= ShingleCacheSize)
        {
            ShingleCache.Clear();
        }

        return ShingleCache.GetOrAdd(name, Shingles);
    }

    /// <summary>The 64-bit hash of a shingle for one permutation seed: BLAKE2b with an 8-byte digest of
    /// <c>"{seed}:{shingle}"</c>, read big-endian (upstream <c>_hash_shingle</c>).</summary>
    public static ulong HashShingle(string shingle, int seed)
    {
        ArgumentNullException.ThrowIfNull(shingle);
        var digest = Blake2b.Hash(Encoding.UTF8.GetBytes($"{seed.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{shingle}"), 8);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest);
    }

    /// <summary>The MinHash signature of a shingle set over <see cref="MinHashPermutations"/> seeds, or empty for
    /// no shingles (upstream <c>_minhash_signature</c>).</summary>
    public static IReadOnlyList<ulong> MinHashSignature(IEnumerable<string> shingles)
    {
        ArgumentNullException.ThrowIfNull(shingles);
        var list = shingles.ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var signature = new ulong[MinHashPermutations];
        for (var seed = 0; seed < MinHashPermutations; seed++)
        {
            signature[seed] = list.Min(shingle => HashShingle(shingle, seed));
        }

        return signature;
    }

    /// <summary>Splits a signature into complete bands of <see cref="MinHashBandSize"/> hashes (upstream
    /// <c>_lsh_bands</c>); a trailing partial band is dropped.</summary>
    public static IReadOnlyList<LshBand> LshBands(IEnumerable<ulong> signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var values = signature.ToList();
        var bands = new List<LshBand>();
        for (var start = 0; start + MinHashBandSize <= values.Count; start += MinHashBandSize)
        {
            bands.Add(new LshBand(values[start], values[start + 1], values[start + 2], values[start + 3]));
        }

        return bands;
    }

    /// <summary>Jaccard similarity of two shingle sets; two empty sets score 1, one empty set 0 (upstream
    /// <c>_jaccard_similarity</c>).</summary>
    public static double JaccardSimilarity(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 && b.Count == 0)
        {
            return 1.0;
        }

        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }

        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union > 0 ? (double)intersection / union : 0.0;
    }

    /// <summary>
    /// Merges the extracted node's non-empty attributes into the canonical node and, when the extracted node's
    /// specific labels strictly include the canonical node's, promotes the canonical node to them (upstream
    /// <c>_promote_resolved_node</c>). Returns <paramref name="resolvedNode"/>, changed in place.
    /// </summary>
    public static EntityNode PromoteResolvedNode(EntityNode extractedNode, EntityNode resolvedNode)
    {
        ArgumentNullException.ThrowIfNull(extractedNode);
        ArgumentNullException.ThrowIfNull(resolvedNode);
        MergeNonEmptyAttributes(resolvedNode, extractedNode);

        var resolvedSpecific = resolvedNode.Labels.Where(l => l != EntityBaseLabel).ToList();
        var extractedSpecific = extractedNode.Labels.Where(l => l != EntityBaseLabel).ToList();
        var resolvedSet = resolvedSpecific.ToHashSet(StringComparer.Ordinal);
        var extractedSet = extractedSpecific.ToHashSet(StringComparer.Ordinal);
        if (!resolvedSet.IsProperSubsetOf(extractedSet))
        {
            return resolvedNode;
        }

        var promoted = new List<string>();
        foreach (var label in new[] { EntityBaseLabel }.Concat(resolvedSpecific).Concat(extractedSpecific))
        {
            if (!promoted.Contains(label))
            {
                promoted.Add(label);
            }
        }

        resolvedNode.Labels = promoted;
        return resolvedNode;
    }

    /// <summary>Overlays the extracted node's attributes on the canonical node, skipping null and blank-string values
    /// so they never erase existing data (upstream <c>_merge_non_empty_attributes</c>).</summary>
    public static void MergeNonEmptyAttributes(EntityNode resolvedNode, EntityNode extractedNode)
    {
        ArgumentNullException.ThrowIfNull(resolvedNode);
        ArgumentNullException.ThrowIfNull(extractedNode);
        if (extractedNode.Attributes.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in extractedNode.Attributes.ToList())
        {
            var normalized = PythonText.Normalize(value);
            if (normalized is null || (normalized is string s && PythonText.Strip(s).Length == 0))
            {
                continue;
            }

            resolvedNode.Attributes[key] = value;
        }
    }

    /// <summary>The node's most specific label (the last one, other than <c>Entity</c>) that has configured identity
    /// properties, or <see langword="null"/> (upstream <c>_identity_type_for_node</c>).</summary>
    public static string? IdentityTypeForNode(
        EntityNode node,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? identityProperties)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (identityProperties is null || identityProperties.Count == 0)
        {
            return null;
        }

        for (var i = node.Labels.Count - 1; i >= 0; i--)
        {
            var label = node.Labels[i];
            if (label != EntityBaseLabel && identityProperties.ContainsKey(label))
            {
                return label;
            }
        }

        return null;
    }

    /// <summary>
    /// An identity value as upstream compares it: strings trimmed (empty is no value), booleans and numbers as they
    /// are (NaN is no value), anything else no value (upstream <c>_normalize_identity_value</c>). Numbers come back as
    /// <see cref="long"/>, <see cref="BigInteger"/> or <see cref="double"/>.
    /// </summary>
    public static object? NormalizeIdentityValue(object? value)
    {
        value = PythonText.Normalize(value);
        return value switch
        {
            null => null,
            string s => PythonText.Strip(s) is { Length: > 0 } trimmed ? trimmed : null,
            bool b => b,
            long or BigInteger => value,
            double d => double.IsNaN(d) ? null : d,
            _ => null,
        };
    }

    /// <summary>
    /// The node's identity: its identity type and the normalised values of that type's identity properties, or
    /// <see langword="null"/> when it has no identity type or lacks any of the values (upstream
    /// <c>_identity_key_for_node</c>).
    /// </summary>
    public static IdentityKey? IdentityKeyForNode(
        EntityNode node,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? identityProperties)
    {
        var identityType = IdentityTypeForNode(node, identityProperties);
        if (identityType is null || identityProperties is null)
        {
            return null;
        }

        var propertyNames = identityProperties.GetValueOrDefault(identityType) ?? [];
        if (propertyNames.Count == 0)
        {
            return null;
        }

        var values = new List<KeyValuePair<string, object>>();
        foreach (var propertyName in propertyNames)
        {
            var value = NormalizeIdentityValue(node.Attributes.TryGetValue(propertyName, out var raw) ? raw : null);
            if (value is null)
            {
                return null;
            }

            values.Add(new KeyValuePair<string, object>(propertyName, value));
        }

        return new IdentityKey(identityType, values);
    }

    /// <summary>
    /// Resolves nodes whose identity properties equal a candidate's, picking the oldest match (by
    /// <see cref="Node.CreatedAt"/>, then <see cref="Node.Uuid"/>) (upstream <c>_resolve_with_identity_properties</c>).
    /// </summary>
    public static void ResolveWithIdentityProperties(
        IReadOnlyList<EntityNode> extractedNodes,
        DedupCandidateIndexes indexes,
        DedupResolutionState state,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? identityProperties)
    {
        ArgumentNullException.ThrowIfNull(extractedNodes);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(state);
        if (identityProperties is null || identityProperties.Count == 0)
        {
            return;
        }

        for (var idx = 0; idx < extractedNodes.Count; idx++)
        {
            var node = extractedNodes[idx];
            if (state.ResolvedNodes[idx] is not null)
            {
                continue;
            }

            var identityKey = IdentityKeyForNode(node, identityProperties);
            if (identityKey is null)
            {
                continue;
            }

            var matches = indexes.ExistingNodes
                .Where(candidate => candidate.Uuid != node.Uuid &&
                                    identityKey.Equals(IdentityKeyForNode(candidate, identityProperties)))
                .OrderBy(candidate => candidate.CreatedAt)
                .ThenBy(candidate => candidate.Uuid, PythonText.CodePointOrder)
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            var match = PromoteResolvedNode(node, matches[0]);
            state.ResolvedNodes[idx] = match;
            state.UuidMap[node.Uuid] = match.Uuid;
            state.DuplicatePairs.Add((node, match));
        }
    }

    /// <summary>Precomputes the exact-name, shingle and LSH lookups over the candidates once per dedupe run
    /// (upstream <c>_build_candidate_indexes</c>).</summary>
    public static DedupCandidateIndexes BuildCandidateIndexes(IReadOnlyList<EntityNode> existingNodes)
    {
        ArgumentNullException.ThrowIfNull(existingNodes);
        var normalizedExisting = new Dictionary<string, List<EntityNode>>(StringComparer.Ordinal);
        var nodesByUuid = new Dictionary<string, EntityNode>(StringComparer.Ordinal);
        var shinglesByCandidate = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var lshBuckets = new Dictionary<(int BandIndex, LshBand Band), List<string>>();

        foreach (var candidate in existingNodes)
        {
            var normalized = NormalizeStringExact(candidate.Name);
            if (!normalizedExisting.TryGetValue(normalized, out var sameName))
            {
                normalizedExisting[normalized] = sameName = [];
            }

            sameName.Add(candidate);
            nodesByUuid[candidate.Uuid] = candidate;

            var shingles = CachedShingles(NormalizeNameForFuzzy(candidate.Name));
            shinglesByCandidate[candidate.Uuid] = shingles;

            var bands = LshBands(MinHashSignature(shingles));
            for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
            {
                var key = (bandIndex, bands[bandIndex]);
                if (!lshBuckets.TryGetValue(key, out var bucket))
                {
                    lshBuckets[key] = bucket = [];
                }

                bucket.Add(candidate.Uuid);
            }
        }

        return new DedupCandidateIndexes(
            existingNodes,
            nodesByUuid,
            normalizedExisting.ToDictionary(e => e.Key, e => (IReadOnlyList<EntityNode>)e.Value, StringComparer.Ordinal),
            shinglesByCandidate,
            lshBuckets.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value));
    }

    /// <summary>
    /// Resolves nodes deterministically (upstream <c>_resolve_with_similarity</c>): a unique exact normalised-name
    /// match wins; several exact matches, low-entropy names and fuzzy misses go to
    /// <see cref="DedupResolutionState.UnresolvedIndices"/> for the LLM; otherwise the best MinHash/LSH candidate with
    /// Jaccard similarity of at least <see cref="FuzzyJaccardThreshold"/> wins.
    /// </summary>
    /// <remarks>Upstream iterates LSH candidates in Python set order, which varies between runs; here they are
    /// visited in bucket order, so among equally similar candidates the first one found wins.</remarks>
    public static void ResolveWithSimilarity(
        IReadOnlyList<EntityNode> extractedNodes,
        DedupCandidateIndexes indexes,
        DedupResolutionState state)
    {
        ArgumentNullException.ThrowIfNull(extractedNodes);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(state);
        for (var idx = 0; idx < extractedNodes.Count; idx++)
        {
            var node = extractedNodes[idx];
            var normalizedExact = NormalizeStringExact(node.Name);
            var normalizedFuzzy = NormalizeNameForFuzzy(node.Name);

            var existingMatches = indexes.NormalizedExisting.GetValueOrDefault(normalizedExact) ?? [];
            if (existingMatches.Count == 1)
            {
                var match = PromoteResolvedNode(node, existingMatches[0]);
                Commit(state, idx, node, match);
                continue;
            }

            if (existingMatches.Count > 1)
            {
                state.UnresolvedIndices.Add(idx);
                continue;
            }

            if (!HasHighEntropy(normalizedFuzzy))
            {
                state.UnresolvedIndices.Add(idx);
                continue;
            }

            var shingles = CachedShingles(normalizedFuzzy);
            var bands = LshBands(MinHashSignature(shingles));
            var candidateIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
            {
                if (indexes.LshBuckets.TryGetValue((bandIndex, bands[bandIndex]), out var bucket))
                {
                    candidateIds.AddRange(bucket.Where(seen.Add));
                }
            }

            EntityNode? bestCandidate = null;
            var bestScore = 0.0;
            foreach (var candidateId in candidateIds)
            {
                var candidateShingles = indexes.ShinglesByCandidate.GetValueOrDefault(candidateId)
                    ?? new HashSet<string>();
                var score = JaccardSimilarity(shingles, candidateShingles);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = indexes.NodesByUuid.GetValueOrDefault(candidateId);
                }
            }

            if (bestCandidate is not null && bestScore >= FuzzyJaccardThreshold)
            {
                Commit(state, idx, node, PromoteResolvedNode(node, bestCandidate));
                continue;
            }

            state.UnresolvedIndices.Add(idx);
        }
    }

    private static void Commit(DedupResolutionState state, int idx, EntityNode node, EntityNode match)
    {
        state.ResolvedNodes[idx] = match;
        state.UuidMap[node.Uuid] = match.Uuid;
        if (match.Uuid != node.Uuid)
        {
            state.DuplicatePairs.Add((node, match));
        }
    }

    /// <summary>Python's <c>len(str)</c>: the number of code points.</summary>
    internal static int CodePointLength(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    // Python's \s on str, which also matches U+001C to U+001F.
    [GeneratedRegex(@"[\s\x1c-\x1f]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^a-z0-9' ]", RegexOptions.CultureInvariant)]
    private static partial Regex NonFuzzyChars();
}

/// <summary>One LSH band: <see cref="DedupHelpers.MinHashBandSize"/> consecutive MinHash values.</summary>
/// <param name="H0">The first hash.</param>
/// <param name="H1">The second hash.</param>
/// <param name="H2">The third hash.</param>
/// <param name="H3">The fourth hash.</param>
public readonly record struct LshBand(ulong H0, ulong H1, ulong H2, ulong H3)
{
    /// <summary>The band's hashes in order.</summary>
    public IReadOnlyList<ulong> Values => [H0, H1, H2, H3];
}

/// <summary>
/// A node's identity for identity-property deduplication: its type and the values of that type's identity
/// properties, in configured order. Values compare as Python values do (<c>1 == 1.0 == True</c>).
/// </summary>
public sealed class IdentityKey : IEquatable<IdentityKey>
{
    /// <summary>Creates an identity key.</summary>
    public IdentityKey(string identityType, IReadOnlyList<KeyValuePair<string, object>> values)
    {
        IdentityType = identityType;
        Values = values;
    }

    /// <summary>The identity type (an entity label).</summary>
    public string IdentityType { get; }

    /// <summary>The property names and normalised values.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> Values { get; }

    /// <inheritdoc/>
    public bool Equals(IdentityKey? other) =>
        other is not null &&
        IdentityType == other.IdentityType &&
        Values.Count == other.Values.Count &&
        Values.Zip(other.Values).All(p => p.First.Key == p.Second.Key && PyEquals(p.First.Value, p.Second.Value));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as IdentityKey);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IdentityType);
        foreach (var (key, value) in Values)
        {
            hash.Add(key);
            hash.Add(value is string s ? s.GetHashCode(StringComparison.Ordinal) : AsNumber(value)?.GetHashCode() ?? 0);
        }

        return hash.ToHashCode();
    }

    private static bool PyEquals(object a, object b)
    {
        if (a is string sa || b is string)
        {
            return a is string x && b is string y && string.Equals(x, y, StringComparison.Ordinal);
        }

        if (a is BigInteger ba && b is BigInteger bb)
        {
            return ba == bb;
        }

        return AsNumber(a) is { } na && AsNumber(b) is { } nb && na == nb;
    }

    private static double? AsNumber(object? value) => value switch
    {
        bool b => b ? 1 : 0,
        long l => l,
        double d => d,
        BigInteger bi => (double)bi,
        _ => null,
    };
}

/// <summary>Precomputed lookups that drive entity deduplication (upstream <c>DedupCandidateIndexes</c>).</summary>
/// <param name="ExistingNodes">The candidates, in order.</param>
/// <param name="NodesByUuid">Candidates by id.</param>
/// <param name="NormalizedExisting">Candidates by <see cref="DedupHelpers.NormalizeStringExact"/> name.</param>
/// <param name="ShinglesByCandidate">Each candidate's fuzzy-name shingles, by id.</param>
/// <param name="LshBuckets">Candidate ids by (band index, band).</param>
public sealed record DedupCandidateIndexes(
    IReadOnlyList<EntityNode> ExistingNodes,
    IReadOnlyDictionary<string, EntityNode> NodesByUuid,
    IReadOnlyDictionary<string, IReadOnlyList<EntityNode>> NormalizedExisting,
    IReadOnlyDictionary<string, IReadOnlySet<string>> ShinglesByCandidate,
    IReadOnlyDictionary<(int BandIndex, LshBand Band), IReadOnlyList<string>> LshBuckets);

/// <summary>Mutable bookkeeping shared by the deterministic and LLM resolution passes (upstream
/// <c>DedupResolutionState</c>).</summary>
public sealed class DedupResolutionState
{
    /// <summary>Creates a state for <paramref name="count"/> extracted nodes, none resolved.</summary>
    public DedupResolutionState(int count)
    {
        ResolvedNodes = new EntityNode?[count];
    }

    /// <summary>The resolution of each extracted node, by index; <see langword="null"/> while unresolved.</summary>
    public IList<EntityNode?> ResolvedNodes { get; }

    /// <summary>Extracted node id to resolved node id.</summary>
    public IDictionary<string, string> UuidMap { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Indexes of extracted nodes left for the LLM.</summary>
    public IList<int> UnresolvedIndices { get; } = new List<int>();

    /// <summary>(extracted, canonical) pairs for nodes resolved onto another node.</summary>
    public IList<(EntityNode Extracted, EntityNode Resolved)> DuplicatePairs { get; } =
        new List<(EntityNode Extracted, EntityNode Resolved)>();
}
