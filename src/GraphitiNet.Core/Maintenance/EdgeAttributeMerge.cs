// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/attribute_utils.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Maintenance;

/// <summary>
/// The edge side of upstream's <c>apply_capped_attributes(..., merge_mode='replace')</c>: caps over-long string
/// attributes in an LLM response and merges the rest with the edge's prior attributes.
/// </summary>
/// <remarks>
/// This is a self-contained copy of the parts of <c>attribute_utils.py</c> the fact pipeline needs, written while
/// <c>AttributeUtils</c> was being ported in parallel; it should be replaced by a call to
/// <c>AttributeUtils.ApplyCappedAttributes</c> once that exists. Field metadata maps from Pydantic as follows: a
/// field's <c>max_length</c> is <see cref="MaxLengthAttribute"/> or <see cref="StringLengthAttribute"/> on the
/// property; a field is required when the response schema (<see cref="ResponseSchema.Generate"/>) lists it as
/// required.
/// </remarks>
internal static class EdgeAttributeMerge
{
    /// <summary>Upstream <c>DEFAULT_ATTRIBUTE_MAX_LENGTH</c>.</summary>
    public const int DefaultAttributeMaxLength = 250;

    /// <summary>Upstream <c>LIST_TOTAL_LENGTH_MULTIPLIER</c>.</summary>
    public const int ListTotalLengthMultiplier = 8;

    private const string EnvVar = "GRAPHITI_ATTRIBUTE_MAX_LENGTH";

    /// <summary>The number of fields the model has (Pydantic <c>len(model.model_fields)</c>).</summary>
    public static int FieldCount(Type model) =>
        ResponseModelSchema.SerializerOptions.GetTypeInfo(model).Properties.Count;

    /// <summary>
    /// Converts an LLM response object to attribute values (strings, longs, doubles, booleans, nulls, lists and
    /// dictionaries), keeping key order. A non-object response yields no attributes.
    /// </summary>
    public static Dictionary<string, object?> ToAttributes(JsonElement response)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (response.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in response.EnumerateObject())
        {
            result[property.Name] = JsonValues.Normalize(property.Value.Clone());
        }

        return result;
    }

    /// <summary>
    /// Caps the response and merges it with <paramref name="prior"/> using upstream's <c>replace</c> mode: kept fields
    /// replace the prior attributes wholesale, fields dropped by the cap fall back to their prior values, and an
    /// explicit <see langword="null"/> keeps a non-null prior value.
    /// </summary>
    public static Dictionary<string, object?> ApplyCappedReplace(
        IReadOnlyDictionary<string, object?> response,
        Type model,
        IDictionary<string, object?> prior)
    {
        var (kept, dropped) = CapStringAttributes(response, model);

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in kept)
        {
            merged[field] = value is null && prior.TryGetValue(field, out var priorValue) && priorValue is not null
                ? priorValue
                : value;
        }

        foreach (var field in dropped)
        {
            if (prior.TryGetValue(field, out var priorValue))
            {
                merged[field] = priorValue;
            }
        }

        return merged;
    }

    private static (Dictionary<string, object?> Kept, List<string> Dropped) CapStringAttributes(
        IReadOnlyDictionary<string, object?> response,
        Type model)
    {
        var effectiveDefault = ResolveDefaultMaxLength(DefaultAttributeMaxLength);
        var properties = Properties(model);
        var required = RequiredFields(model);
        var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        var dropped = new List<string>();
        foreach (var (field, value) in response)
        {
            var maxLength = properties.TryGetValue(field, out var property) ? FieldMaxLength(property) : null;
            var maxLen = maxLength is > 0 ? maxLength.Value : effectiveDefault;
            if (!ExceedsCap(value, maxLen) || required.Contains(field))
            {
                kept[field] = value;
                continue;
            }

            dropped.Add(field);
        }

        return (kept, dropped);
    }

    private static bool ExceedsCap(object? value, int maxLen)
    {
        switch (value)
        {
            case string s:
                return PythonLength(s) > maxLen;
            case IEnumerable<object?> list:
                var strings = list.OfType<string>().Select(PythonLength).ToList();
                var maxItem = strings.Count == 0 ? 0 : strings.Max();
                if (maxItem > maxLen)
                {
                    return true;
                }

                return strings.Sum() > maxLen * ListTotalLengthMultiplier;
            default:
                return false;
        }
    }

    // Python's len() counts code points.
    private static int PythonLength(string s)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    private static int ResolveDefaultMaxLength(int defaultMaxLength)
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultMaxLength;
        }

        return int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
               && parsed > 0
            ? parsed
            : defaultMaxLength;
    }

    private static int? FieldMaxLength(PropertyInfo property)
    {
        if (property.GetCustomAttribute<MaxLengthAttribute>() is { } maxLength)
        {
            return maxLength.Length;
        }

        return property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;
    }

    private static Dictionary<string, PropertyInfo> Properties(Type model)
    {
        var typeInfo = ResponseModelSchema.SerializerOptions.GetTypeInfo(model);
        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider is PropertyInfo info)
            {
                result[property.Name] = info;
            }
        }

        return result;
    }

    private static HashSet<string> RequiredFields(Type model)
    {
        var schema = ResponseSchema.Generate(model, ResponseModelSchema.SerializerOptions);
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?.GetValue<string>() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        return required;
    }
}
