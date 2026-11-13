// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search_filters.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Utils;

namespace GraphitiNet.Search;

/// <summary>Constants shared by search and ingestion. Values match upstream.</summary>
public static class SearchDefaults
{
    /// <summary>Upstream <c>DEFAULT_MIN_SCORE</c>: minimum cosine similarity for vector search.</summary>
    public const double DefaultMinScore = 0.6;

    /// <summary>Upstream <c>DEFAULT_MMR_LAMBDA</c>.</summary>
    public const double DefaultMmrLambda = 0.5;

    /// <summary>Upstream <c>MAX_SEARCH_DEPTH</c>: the breadth-first search hop cap.</summary>
    public const int MaxSearchDepth = 3;

    /// <summary>Upstream <c>RELEVANT_SCHEMA_LIMIT</c>.</summary>
    public const int RelevantSchemaLimit = 10;

    /// <summary>Upstream <c>DEFAULT_SEARCH_LIMIT</c>.</summary>
    public const int DefaultSearchLimit = 10;
}

/// <summary>How a filter compares a value.</summary>
public enum ComparisonOperator
{
    /// <summary><c>=</c></summary>
    Equals,

    /// <summary><c>&lt;&gt;</c></summary>
    NotEquals,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterThanEqual,

    /// <summary><c>&lt;=</c></summary>
    LessThanEqual,

    /// <summary><c>IS NULL</c>: the field has no value. <see cref="DateFilter.Date"/> is ignored.</summary>
    IsNull,

    /// <summary><c>IS NOT NULL</c>: the field has a value. <see cref="DateFilter.Date"/> is ignored.</summary>
    IsNotNull,
}

/// <summary>Compares a date field with a value.</summary>
public sealed record DateFilter
{
    /// <summary>The value to compare with. Not used by <see cref="ComparisonOperator.IsNull"/> and
    /// <see cref="ComparisonOperator.IsNotNull"/>.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>The comparison.</summary>
    public required ComparisonOperator ComparisonOperator { get; init; }
}

/// <summary>Compares a custom attribute with a value.</summary>
public sealed record PropertyFilter
{
    /// <summary>The attribute name.</summary>
    public required string PropertyName { get; init; }

    /// <summary>The value: a string, a number, or <see langword="null"/>.</summary>
    public object? PropertyValue { get; init; }

    /// <summary>The comparison.</summary>
    public required ComparisonOperator ComparisonOperator { get; init; }
}

/// <summary>
/// Restricts search results. Each date field takes a list of OR groups, each an AND list: <c>[[a, b], [c]]</c> means
/// <c>(a AND b) OR c</c>, as upstream.
/// </summary>
public sealed record SearchFilters
{
    private readonly IReadOnlyList<string>? _nodeLabels;

    /// <summary>Only entities with at least one of these labels (and facts between such entities).</summary>
    /// <exception cref="NodeLabelValidationException">A label is not a safe identifier.</exception>
    public IReadOnlyList<string>? NodeLabels
    {
        get => _nodeLabels;
        init
        {
            Validation.ValidateNodeLabels(value);
            _nodeLabels = value;
        }
    }

    /// <summary>Only facts with one of these names.</summary>
    public IReadOnlyList<string>? EdgeTypes { get; init; }

    /// <summary>Conditions on <c>ValidAt</c>.</summary>
    public IReadOnlyList<IReadOnlyList<DateFilter>>? ValidAt { get; init; }

    /// <summary>Conditions on <c>InvalidAt</c>.</summary>
    public IReadOnlyList<IReadOnlyList<DateFilter>>? InvalidAt { get; init; }

    /// <summary>Conditions on <c>CreatedAt</c>.</summary>
    public IReadOnlyList<IReadOnlyList<DateFilter>>? CreatedAt { get; init; }

    /// <summary>Conditions on <c>ExpiredAt</c>.</summary>
    public IReadOnlyList<IReadOnlyList<DateFilter>>? ExpiredAt { get; init; }

    /// <summary>Only facts with these ids.</summary>
    public IReadOnlyList<string>? EdgeUuids { get; init; }

    /// <summary>Only facts starting at these entities.</summary>
    public IReadOnlyList<string>? EdgeSourceNodeUuids { get; init; }

    /// <summary>Only facts ending at these entities.</summary>
    public IReadOnlyList<string>? EdgeTargetNodeUuids { get; init; }

    /// <summary>Conditions on custom attributes, all of which must hold.</summary>
    public IReadOnlyList<PropertyFilter>? PropertyFilters { get; init; }

    /// <summary>A filter that restricts nothing.</summary>
    public static SearchFilters None { get; } = new();
}
