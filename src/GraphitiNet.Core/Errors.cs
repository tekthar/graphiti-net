// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/errors.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet;

/// <summary>Base exception for graphiti-net.</summary>
public class GraphitiException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public GraphitiException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public GraphitiException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates the exception with a default message.</summary>
    public GraphitiException() { }
}

/// <summary>An edge was not found.</summary>
public sealed class EdgeNotFoundException(string uuid) : GraphitiException($"edge {uuid} not found")
{
    /// <summary>The id that was looked up.</summary>
    public string Uuid { get; } = uuid;
}

/// <summary>None of a list of edges was found.</summary>
public sealed class EdgesNotFoundException(IReadOnlyList<string> uuids)
    : GraphitiException($"None of the edges for {PythonFormat.List(uuids)} were found.")
{
    /// <summary>The ids that were looked up.</summary>
    public IReadOnlyList<string> Uuids { get; } = uuids;
}

/// <summary>No edges were found for a list of group ids.</summary>
public sealed class GroupsEdgesNotFoundException(IReadOnlyList<string> groupIds)
    : GraphitiException($"no edges found for group ids {PythonFormat.List(groupIds)}")
{
    /// <summary>The group ids that were searched.</summary>
    public IReadOnlyList<string> GroupIds { get; } = groupIds;
}

/// <summary>No nodes were found for a list of group ids.</summary>
public sealed class GroupsNodesNotFoundException(IReadOnlyList<string> groupIds)
    : GraphitiException($"no nodes found for group ids {PythonFormat.List(groupIds)}")
{
    /// <summary>The group ids that were searched.</summary>
    public IReadOnlyList<string> GroupIds { get; } = groupIds;
}

/// <summary>A node was not found.</summary>
public sealed class NodeNotFoundException(string uuid) : GraphitiException($"node {uuid} not found")
{
    /// <summary>The id that was looked up.</summary>
    public string Uuid { get; } = uuid;
}

/// <summary>A search reranker was misconfigured or failed.</summary>
public sealed class SearchRerankerException(string message) : GraphitiException(message);

/// <summary>A custom entity type uses a protected attribute name.</summary>
public sealed class EntityTypeValidationException(string entityType, string entityTypeAttribute)
    : GraphitiException(
        $"{entityTypeAttribute} cannot be used as an attribute for {entityType} as it is a protected attribute name.")
{
    /// <summary>The custom entity type.</summary>
    public string EntityType { get; } = entityType;

    /// <summary>The protected attribute name it uses.</summary>
    public string EntityTypeAttribute { get; } = entityTypeAttribute;
}

/// <summary>A group id contains characters other than ASCII letters, digits, dashes and underscores.</summary>
public sealed class GroupIdValidationException(string groupId)
    : GraphitiException($"group_id \"{groupId}\" must contain only alphanumeric characters, dashes, or underscores")
{
    /// <summary>The rejected group id.</summary>
    public string GroupId { get; } = groupId;
}

/// <summary>
/// One or more node labels are not safe identifiers: they must start with a letter or underscore and contain only
/// letters, digits and underscores. Upstream also derives this from <c>ValueError</c>; here it derives from
/// <see cref="GraphitiException"/> only.
/// </summary>
public sealed class NodeLabelValidationException(IReadOnlyList<string> nodeLabels)
    : GraphitiException(
        "node_labels must start with a letter or underscore and contain only alphanumeric characters or underscores: "
        + string.Join(", ", nodeLabels.Select(label => $"\"{label}\"")))
{
    /// <summary>The rejected labels.</summary>
    public IReadOnlyList<string> NodeLabels { get; } = nodeLabels;
}

/// <summary>Formats values the way Python's <c>str()</c> does, so error messages match upstream.</summary>
internal static class PythonFormat
{
    public static string List(IEnumerable<string> items) =>
        "[" + string.Join(", ", items.Select(item => $"'{item}'")) + "]";
}
