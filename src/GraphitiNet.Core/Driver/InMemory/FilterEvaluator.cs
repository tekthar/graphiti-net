// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Driver.InMemory;

/// <summary>
/// Evaluates <see cref="SearchFilters"/> against records, with SQL/Cypher null semantics: a comparison with a missing
/// value is false; only <see cref="ComparisonOperator.IsNull"/> matches it.
/// </summary>
internal static class FilterEvaluator
{
    /// <summary>The label every entity carries implicitly, as upstream's <c>:Entity</c> label.</summary>
    public const string EntityLabel = "Entity";

    /// <summary>Whether a group passes the group id restriction (<see langword="null"/> means every group).</summary>
    public static bool InGroups(IReadOnlyCollection<string>? groupIds, string groupId) =>
        groupIds is null || groupIds.Contains(groupId);

    /// <summary>Entity filters: node labels and property filters on attributes.</summary>
    public static bool MatchesNode(SearchFilters filters, EntityNode node) =>
        HasLabel(filters.NodeLabels, node) && MatchesProperties(filters.PropertyFilters, node.Attributes);

    /// <summary>Fact filters: every field. <paramref name="source"/> and <paramref name="target"/> are the endpoints,
    /// or <see langword="null"/> if missing.</summary>
    public static bool MatchesEdge(SearchFilters filters, EntityEdge edge, EntityNode? source, EntityNode? target)
    {
        if (filters.EdgeTypes is { } types && !types.Contains(edge.Name))
        {
            return false;
        }

        if (filters.EdgeUuids is { } uuids && !uuids.Contains(edge.Uuid))
        {
            return false;
        }

        if (filters.EdgeSourceNodeUuids is { } sources && !sources.Contains(edge.SourceNodeUuid))
        {
            return false;
        }

        if (filters.EdgeTargetNodeUuids is { } targets && !targets.Contains(edge.TargetNodeUuid))
        {
            return false;
        }

        if (filters.NodeLabels is not null
            && (source is null || target is null
                || !HasLabel(filters.NodeLabels, source) || !HasLabel(filters.NodeLabels, target)))
        {
            return false;
        }

        return MatchesDates(filters.ValidAt, edge.ValidAt)
            && MatchesDates(filters.InvalidAt, edge.InvalidAt)
            && MatchesDates(filters.CreatedAt, edge.CreatedAt)
            && MatchesDates(filters.ExpiredAt, edge.ExpiredAt)
            && MatchesProperties(filters.PropertyFilters, edge.Attributes);
    }

    private static bool HasLabel(IReadOnlyList<string>? labels, EntityNode node) =>
        labels is null
        || labels.Any(label => label == EntityLabel || node.Labels.Contains(label));

    /// <summary>OR over the groups, AND within each group. A <see langword="null"/> list does not restrict.</summary>
    private static bool MatchesDates(IReadOnlyList<IReadOnlyList<DateFilter>>? orGroups, DateTimeOffset? value) =>
        orGroups is null || orGroups.Any(andGroup => andGroup.All(filter => MatchesDate(filter, value)));

    private static bool MatchesDate(DateFilter filter, DateTimeOffset? value) => filter.ComparisonOperator switch
    {
        ComparisonOperator.IsNull => value is null,
        ComparisonOperator.IsNotNull => value is not null,
        _ when value is null || filter.Date is null => false,
        _ => Compare(filter.ComparisonOperator, value.Value.CompareTo(filter.Date.Value)),
    };

    private static bool MatchesProperties(
        IReadOnlyList<PropertyFilter>? filters,
        IDictionary<string, object?> attributes) =>
        filters is null || filters.All(filter => MatchesProperty(filter, attributes));

    private static bool MatchesProperty(PropertyFilter filter, IDictionary<string, object?> attributes)
    {
        var value = attributes.TryGetValue(filter.PropertyName, out var found) ? found : null;
        switch (filter.ComparisonOperator)
        {
            case ComparisonOperator.IsNull:
                return value is null;
            case ComparisonOperator.IsNotNull:
                return value is not null;
        }

        if (value is null || filter.PropertyValue is null)
        {
            return false;
        }

        if (JsonValues.AsNumber(value) is { } number && JsonValues.AsNumber(filter.PropertyValue) is { } expected)
        {
            return Compare(filter.ComparisonOperator, number.CompareTo(expected));
        }

        if (value is string text && filter.PropertyValue is string expectedText)
        {
            return Compare(filter.ComparisonOperator, string.CompareOrdinal(text, expectedText));
        }

        if (value is bool flag && filter.PropertyValue is bool expectedFlag)
        {
            return Compare(filter.ComparisonOperator, flag.CompareTo(expectedFlag));
        }

        // Different types never compare, as in Cypher.
        return false;
    }

    private static bool Compare(ComparisonOperator op, int comparison) => op switch
    {
        ComparisonOperator.Equals => comparison == 0,
        ComparisonOperator.NotEquals => comparison != 0,
        ComparisonOperator.GreaterThan => comparison > 0,
        ComparisonOperator.LessThan => comparison < 0,
        ComparisonOperator.GreaterThanEqual => comparison >= 0,
        ComparisonOperator.LessThanEqual => comparison <= 0,
        _ => false,
    };
}
