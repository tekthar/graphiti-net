// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/dedup_helpers.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.RegularExpressions;

namespace GraphitiNet.Maintenance;

/// <summary>Deterministic entity deduplication helpers.</summary>
public static partial class DedupHelpers
{
    /// <summary>Upstream <c>ENTITY_BASE_LABEL</c>: the label every entity carries.</summary>
    public const string EntityBaseLabel = "Entity";

    /// <summary>Lowercases and collapses whitespace so equal names map to the same key (upstream
    /// <c>_normalize_string_exact</c>).</summary>
    public static string NormalizeStringExact(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Whitespace().Replace(name.ToLowerInvariant(), " ").Trim(PythonWhitespace);
    }

    // Python's str.strip() and \s cover the same ASCII and Unicode whitespace closely enough for names.
    private static readonly char[] PythonWhitespace = [' ', '\t', '\n', '\r', '\v', '\f'];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
