// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search_filters.py (Graphiti v0.31.2) and changed for .NET: the Cypher filter
// constructors become parameterised SQL over the driver's tables.

using System.Globalization;
using GraphitiNet.Search;
using GraphitiNet.Utils;
using NpgsqlTypes;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>Translates <see cref="SearchFilters"/> into SQL conditions.</summary>
/// <remarks>
/// <para>As upstream, entity and community searches use only the node filters (labels, plus property filters on the
/// <c>attributes</c> column), and fact searches use all of them; date filters apply to facts only.</para>
/// <para>Differences from upstream's Cypher: each date filter gets its own parameter (upstream reuses one name per
/// position, so OR groups could overwrite each other's values); an empty list matches nothing instead of producing
/// invalid syntax; property filters are applied (upstream's Neo4j driver ignores them).</para>
/// </remarks>
internal static class FilterSql
{
    /// <summary>The conditions for an entity alias.</summary>
    public static List<string> NodeConditions(SqlBuilder b, SearchFilters filters, string n)
    {
        var conditions = new List<string>();
        if (filters.NodeLabels is not null)
        {
            conditions.Add(LabelCondition(b, filters.NodeLabels, n));
        }

        AddPropertyConditions(b, filters.PropertyFilters, $"{n}.attributes", conditions);
        return conditions;
    }

    /// <summary>Whether fact conditions need the source (<c>n</c>) and target (<c>m</c>) entities joined.</summary>
    public static bool NeedsEndpoints(SearchFilters filters) => filters.NodeLabels is not null;

    /// <summary>The conditions for a fact alias; <paramref name="n"/> and <paramref name="m"/> are the joined source
    /// and target entities when <see cref="NeedsEndpoints"/> is true.</summary>
    public static List<string> EdgeConditions(SqlBuilder b, SearchFilters filters, string e, string n, string m)
    {
        var conditions = new List<string>();
        if (filters.EdgeTypes is not null)
        {
            conditions.Add($"{e}.name = ANY({b.TextArray(filters.EdgeTypes)})");
        }

        if (filters.EdgeUuids is not null)
        {
            conditions.Add($"{e}.uuid = ANY({b.TextArray(filters.EdgeUuids)})");
        }

        if (filters.EdgeSourceNodeUuids is not null)
        {
            conditions.Add($"{e}.source_node_uuid = ANY({b.TextArray(filters.EdgeSourceNodeUuids)})");
        }

        if (filters.EdgeTargetNodeUuids is not null)
        {
            conditions.Add($"{e}.target_node_uuid = ANY({b.TextArray(filters.EdgeTargetNodeUuids)})");
        }

        if (filters.NodeLabels is not null)
        {
            conditions.Add(LabelCondition(b, filters.NodeLabels, n));
            conditions.Add(LabelCondition(b, filters.NodeLabels, m));
        }

        AddDateCondition(b, filters.ValidAt, $"{e}.valid_at", conditions);
        AddDateCondition(b, filters.InvalidAt, $"{e}.invalid_at", conditions);
        AddDateCondition(b, filters.CreatedAt, $"{e}.created_at", conditions);
        AddDateCondition(b, filters.ExpiredAt, $"{e}.expired_at", conditions);
        AddPropertyConditions(b, filters.PropertyFilters, $"{e}.attributes", conditions);
        return conditions;
    }

    /// <summary>Joins conditions with AND, prefixed with <paramref name="prefix"/>, or empty.</summary>
    public static string Where(IReadOnlyCollection<string> conditions, string prefix = " AND ") =>
        conditions.Count == 0 ? "" : prefix + string.Join(" AND ", conditions);

    private static string LabelCondition(SqlBuilder b, IReadOnlyList<string> labels, string alias)
    {
        // Defence in depth, as upstream: labels are validated again here.
        Validation.ValidateNodeLabels(labels);
        // Every upstream entity carries the Entity label, so filtering on it matches every entity.
        var p = b.TextArray(labels);
        return $"({alias}.labels && {p} OR 'Entity' = ANY({p}))";
    }

    private static void AddDateCondition(
        SqlBuilder b,
        IReadOnlyList<IReadOnlyList<DateFilter>>? orGroups,
        string column,
        List<string> conditions)
    {
        if (orGroups is null)
        {
            return;
        }

        // An empty list, at either level, matches nothing.
        var ors = orGroups
            .Select(andGroup => andGroup.Count == 0
                ? "FALSE"
                : "(" + string.Join(" AND ", andGroup.Select(filter => DateComparison(b, filter, column))) + ")")
            .DefaultIfEmpty("FALSE");
        conditions.Add("(" + string.Join(" OR ", ors) + ")");
    }

    private static string DateComparison(SqlBuilder b, DateFilter filter, string column)
    {
        if (filter.ComparisonOperator == ComparisonOperator.IsNull)
        {
            return $"({column} IS NULL)";
        }

        if (filter.ComparisonOperator == ComparisonOperator.IsNotNull)
        {
            return $"({column} IS NOT NULL)";
        }

        var value = b.Param(filter.Date?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
        return $"({column} {Operator(filter.ComparisonOperator)} {value})";
    }

    private static void AddPropertyConditions(
        SqlBuilder b,
        IReadOnlyList<PropertyFilter>? filters,
        string column,
        List<string> conditions)
    {
        if (filters is null)
        {
            return;
        }

        if (filters.Count == 0)
        {
            conditions.Add("FALSE");
            return;
        }

        foreach (var filter in filters)
        {
            var name = b.Text(filter.PropertyName);
            var field = $"({column} -> {name})";
            var op = filter.ComparisonOperator;
            if (op == ComparisonOperator.IsNull)
            {
                conditions.Add($"({field} IS NULL OR {field} = 'null'::jsonb)");
                continue;
            }

            if (op == ComparisonOperator.IsNotNull)
            {
                conditions.Add($"({field} IS NOT NULL AND {field} <> 'null'::jsonb)");
                continue;
            }

            // A comparison with a missing value, or across types, is false. Numbers compare numerically, strings
            // ordinally (the "C" collation), anything else (booleans) by JSON equality.
            var text = $"({field} #>> '{{}}')";
            conditions.Add(filter.PropertyValue switch
            {
                null => "FALSE",
                string value =>
                    $"(jsonb_typeof({field}) = 'string' AND {text} COLLATE \"C\" {Operator(op)} {b.Text(value)} COLLATE \"C\")",
                sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                    $"(jsonb_typeof({field}) = 'number' AND {text}::numeric {Operator(op)} "
                    + $"{b.Param(Convert.ToDecimal(filter.PropertyValue, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric)})",
                _ => op is ComparisonOperator.Equals or ComparisonOperator.NotEquals
                    ? $"({field} IS NOT NULL AND jsonb_typeof({field}) = jsonb_typeof({Json(b, filter)}) "
                        + $"AND {field} {Operator(op)} {Json(b, filter)})"
                    : "FALSE",
            });
        }

        static string Json(SqlBuilder b, PropertyFilter filter) =>
            b.Param(JsonValues.SerializeValue(filter.PropertyValue), NpgsqlDbType.Jsonb);
    }

    private static string Operator(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equals => "=",
        ComparisonOperator.NotEquals => "<>",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.GreaterThanEqual => ">=",
        ComparisonOperator.LessThanEqual => "<=",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };
}
