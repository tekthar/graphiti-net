// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/helpers.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.RegularExpressions;

namespace GraphitiNet.Utils;

/// <summary>Input validation shared by ingestion and search.</summary>
public static partial class Validation
{
    /// <summary>
    /// Checks that a group id contains only ASCII letters, digits, dashes and underscores. An empty or
    /// <see langword="null"/> group id is allowed.
    /// </summary>
    /// <exception cref="GroupIdValidationException">The group id contains other characters.</exception>
    public static void ValidateGroupId(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
        {
            return;
        }

        if (!GroupIdPattern().IsMatch(groupId))
        {
            throw new GroupIdValidationException(groupId);
        }
    }

    /// <summary>Checks each group id with <see cref="ValidateGroupId"/>. A <see langword="null"/> list is allowed.</summary>
    /// <exception cref="GroupIdValidationException">A group id contains invalid characters.</exception>
    public static void ValidateGroupIds(IEnumerable<string>? groupIds)
    {
        if (groupIds is null)
        {
            return;
        }

        foreach (var groupId in groupIds)
        {
            ValidateGroupId(groupId);
        }
    }

    /// <summary>
    /// Checks that each node label starts with a letter or underscore and contains only ASCII letters, digits and
    /// underscores. A <see langword="null"/> or empty list is allowed.
    /// </summary>
    /// <exception cref="NodeLabelValidationException">One or more labels are invalid; all of them are reported.</exception>
    public static void ValidateNodeLabels(IEnumerable<string>? nodeLabels)
    {
        if (nodeLabels is null)
        {
            return;
        }

        var invalid = nodeLabels.Where(label => !SafeIdentifierPattern().IsMatch(label)).ToList();
        if (invalid.Count > 0)
        {
            throw new NodeLabelValidationException(invalid);
        }
    }

    // \z rather than $: in .NET, $ also matches before a trailing newline. Python's re.match with $ has the same
    // quirk, but rejecting the newline is the safe choice for values that end up in queries.
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex GroupIdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();
}
