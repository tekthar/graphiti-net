// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/bulk_utils.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Models;

namespace GraphitiNet.Maintenance;

/// <summary>Helpers for bulk ingestion and for re-pointing edges after deduplication.</summary>
public static partial class BulkUtils
{
    /// <summary>Upstream <c>CHUNK_SIZE</c>.</summary>
    public const int ChunkSize = 10;

    /// <summary>
    /// Collapses alias → canonical chains while keeping their direction (upstream <c>_build_directed_uuid_map</c>).
    /// </summary>
    /// <remarks>
    /// The pairs are directed mappings found during node deduplication. A union-find with path compression makes
    /// every source resolve to its final canonical target, whatever the ids' lexicographic order.
    /// </remarks>
    /// <param name="pairs">(alias, canonical) pairs.</param>
    /// <returns>Every id seen, mapped to its canonical id (canonical ids map to themselves).</returns>
    public static IReadOnlyDictionary<string, string> BuildDirectedUuidMap(
        IEnumerable<(string Source, string Target)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);

        string Find(string uuid)
        {
            parent.TryAdd(uuid, uuid);
            var root = uuid;
            while (parent[root] != root)
            {
                root = parent[root];
            }

            while (parent[uuid] != root)
            {
                var next = parent[uuid];
                parent[uuid] = root;
                uuid = next;
            }

            return root;
        }

        foreach (var (source, target) in pairs)
        {
            parent.TryAdd(source, source);
            parent.TryAdd(target, target);
            parent[Find(source)] = Find(target);
        }

        var result = new Dictionary<string, string>(parent.Count, StringComparer.Ordinal);
        foreach (var uuid in parent.Keys.ToList())
        {
            result[uuid] = Find(uuid);
        }

        return result;
    }

    /// <summary>
    /// Groups ids joined by duplicate pairs and maps each to the lexicographically smallest id in its group (upstream
    /// <c>compress_uuid_map</c>). Direction is ignored.
    /// </summary>
    /// <param name="duplicatePairs">Pairs of ids that are duplicates of each other.</param>
    /// <returns>Every id in a pair, mapped to its group's smallest id (ordinal comparison).</returns>
    public static IReadOnlyDictionary<string, string> CompressUuidMap(
        IEnumerable<(string First, string Second)> duplicatePairs)
    {
        ArgumentNullException.ThrowIfNull(duplicatePairs);
        var pairs = duplicatePairs.ToList();
        var allUuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (a, b) in pairs)
        {
            allUuids.Add(a);
            allUuids.Add(b);
        }

        var unionFind = new UnionFind(allUuids);
        foreach (var (a, b) in pairs)
        {
            unionFind.Union(a, b);
        }

        return allUuids.ToDictionary(uuid => uuid, unionFind.Find, StringComparer.Ordinal);
    }

    /// <summary>
    /// Re-points edge endpoints through <paramref name="uuidMap"/>, in place, and drops edges that collapse onto one
    /// node (upstream <c>resolve_edge_pointers</c>).
    /// </summary>
    /// <remarks>
    /// An edge whose two endpoints were different before remapping and are the same after it is left out of the
    /// result: deduplication can map an endpoint onto the edge's other endpoint. Edges extracted as self-references
    /// (source already equal to target) are kept. Use the returned list, not the input.
    /// </remarks>
    /// <typeparam name="TEdge">The edge type.</typeparam>
    /// <param name="edges">The edges to re-point.</param>
    /// <param name="uuidMap">Old id → new id; ids not in the map stay.</param>
    /// <returns>The surviving edges, in input order.</returns>
    public static IReadOnlyList<TEdge> ResolveEdgePointers<TEdge>(
        IEnumerable<TEdge> edges,
        IReadOnlyDictionary<string, string> uuidMap)
        where TEdge : Edge
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(uuidMap);
        var surviving = new List<TEdge>();
        foreach (var edge in edges)
        {
            var sourceUuid = edge.SourceNodeUuid;
            var targetUuid = edge.TargetNodeUuid;
            edge.SourceNodeUuid = uuidMap.GetValueOrDefault(sourceUuid, sourceUuid);
            edge.TargetNodeUuid = uuidMap.GetValueOrDefault(targetUuid, targetUuid);

            if (sourceUuid != targetUuid && edge.SourceNodeUuid == edge.TargetNodeUuid)
            {
                continue;
            }

            surviving.Add(edge);
        }

        return surviving;
    }

    /// <summary>Union-find that roots each set at its lexicographically smallest id (upstream <c>UnionFind</c>).</summary>
    internal sealed class UnionFind
    {
        private readonly Dictionary<string, string> _parent;

        public UnionFind(IEnumerable<string> elements) =>
            _parent = elements.ToDictionary(e => e, e => e, StringComparer.Ordinal);

        public string Find(string x)
        {
            var parent = _parent[x];
            if (parent != x)
            {
                parent = Find(parent);
                _parent[x] = parent;
            }

            return parent;
        }

        public void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb)
            {
                return;
            }

            // Attach the lexicographically larger root under the smaller.
            if (string.CompareOrdinal(ra, rb) < 0)
            {
                _parent[rb] = ra;
            }
            else
            {
                _parent[ra] = rb;
            }
        }
    }
}
