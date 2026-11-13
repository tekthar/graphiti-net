// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/dangling_endpoints.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Maintenance;

/// <summary>Keeps facts whose endpoints the LLM did not list as entities.</summary>
public static class DanglingEndpoints
{
    /// <summary>
    /// Returns the entity named <paramref name="name"/> from <paramref name="nameToNode"/> (by normalised name), or
    /// creates one, adds it to <paramref name="nodes"/> and <paramref name="nameToNode"/>, and returns it.
    /// </summary>
    public static EntityNode MaterializeDanglingEndpointNode(
        string name,
        string groupId,
        IDictionary<string, EntityNode> nameToNode,
        IList<EntityNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(nameToNode);
        ArgumentNullException.ThrowIfNull(nodes);
        var normalized = DedupHelpers.NormalizeStringExact(name);
        if (nameToNode.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var node = new EntityNode
        {
            Name = name.Trim(),
            GroupId = groupId,
            Labels = [DedupHelpers.EntityBaseLabel],
            Summary = "",
            CreatedAt = DateTimeUtils.UtcNow(),
        };
        nodes.Add(node);
        nameToNode[normalized] = node;
        return node;
    }

    /// <summary>
    /// Drops materialised nodes that no kept edge references, in place, and returns the materialised survivors.
    /// </summary>
    public static IReadOnlyList<EntityNode> PruneUnreferencedMaterializedNodes(
        List<EntityNode> nodes,
        IReadOnlySet<string> initialUuids,
        IReadOnlySet<string> referencedUuids)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(initialUuids);
        ArgumentNullException.ThrowIfNull(referencedUuids);
        nodes.RemoveAll(n => !initialUuids.Contains(n.Uuid) && !referencedUuids.Contains(n.Uuid));
        return nodes.Where(n => !initialUuids.Contains(n.Uuid)).ToList();
    }
}
