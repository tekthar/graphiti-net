// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/maintenance/attribute_utils.py (Graphiti v0.31.2) and changed for .NET.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet.Maintenance;

/// <summary>How <see cref="AttributeUtils.ApplyCappedAttributes"/> merges capped LLM output with prior attributes.</summary>
public enum AttributeMergeMode
{
    /// <summary>Node attributes: prior values overlaid by kept fields. Fields the LLM omitted or the cap dropped keep
    /// their prior values.</summary>
    Overlay,

    /// <summary>Edge attributes: kept fields replace the prior attributes, but cap-dropped fields are restored from
    /// prior. Fields the LLM omitted are cleared.</summary>
    Replace,
}

/// <summary>
/// Length caps on LLM-extracted string attributes, which guard against the model dumping reasoning or schema text into
/// free-form fields (upstream <c>attribute_utils</c>).
/// </summary>
public static class AttributeUtils
{
    /// <summary>Upstream <c>DEFAULT_ATTRIBUTE_MAX_LENGTH</c>: the default cap on a string attribute value.</summary>
    public const int DefaultAttributeMaxLength = 250;

    /// <summary>Upstream <c>LIST_TOTAL_LENGTH_MULTIPLIER</c>: a list of strings may hold at most this many times the
    /// per-item cap in total.</summary>
    public const int ListTotalLengthMultiplier = 8;

    /// <summary>The environment variable that overrides <see cref="DefaultAttributeMaxLength"/>.</summary>
    public const string MaxLengthEnvironmentVariable = "GRAPHITI_ATTRIBUTE_MAX_LENGTH";

    // One warning per distinct bad value, as upstream's _warned_invalid_env.
    private static readonly ConcurrentDictionary<string, bool> WarnedInvalidEnv = new(StringComparer.Ordinal);

    /// <summary>
    /// Drops string (or list-of-string) attributes whose value exceeds a length cap (upstream
    /// <c>cap_string_attributes</c>).
    /// </summary>
    /// <remarks>
    /// <para>The cap is the field's <see cref="System.ComponentModel.DataAnnotations.MaxLengthAttribute"/> or
    /// <see cref="System.ComponentModel.DataAnnotations.StringLengthAttribute"/> if it has one, otherwise
    /// <c>GRAPHITI_ATTRIBUTE_MAX_LENGTH</c> if that holds a positive integer, otherwise
    /// <paramref name="defaultMaxLength"/>. Lists are capped per item and in total (cap ×
    /// <see cref="ListTotalLengthMultiplier"/>). Lengths count code points, as Python's <c>len</c>. Other values pass
    /// through.</para>
    /// <para>A required field (no default) keeps an over-cap value, with a warning, because dropping it would fail
    /// validation of the whole response.</para>
    /// <para>Logs name the entity id and group, never values.</para>
    /// </remarks>
    /// <param name="response">The LLM's attribute values.</param>
    /// <param name="model">The entity or edge type.</param>
    /// <param name="defaultMaxLength">The cap when neither the field nor the environment sets one.</param>
    /// <param name="promptName">For logs.</param>
    /// <param name="entityUuid">For logs.</param>
    /// <param name="groupId">For logs.</param>
    /// <param name="logger">Where to log; none by default.</param>
    /// <returns>The kept fields, in response order, and the names of the dropped ones.</returns>
    public static (Dictionary<string, object?> Kept, HashSet<string> Dropped) CapStringAttributes(
        IReadOnlyDictionary<string, object?> response,
        Type model,
        int defaultMaxLength = DefaultAttributeMaxLength,
        string promptName = "",
        string entityUuid = "",
        string groupId = "",
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(model);
        logger ??= NullLogger.Instance;
        var effectiveDefault = ResolveDefaultMaxLength(defaultMaxLength, logger);
        var fields = EntityTypesUtils.GetFields(model).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var logGroupId = OrUnknown(groupId);
        var logEntityUuid = OrUnknown(entityUuid);
        foreach (var (fieldName, value) in response)
        {
            var field = fields.GetValueOrDefault(fieldName);
            var maxLen = field?.MaxLength ?? effectiveDefault;
            var (exceeded, reason, observedLength, breachedCap) = CheckValueAgainstCap(value, maxLen);
            if (!exceeded)
            {
                kept[fieldName] = value;
                continue;
            }

            if (field?.IsRequired == true)
            {
                MaintenanceLog.AttributeCapSkippedRequired(
                    logger, promptName, logGroupId, logEntityUuid, fieldName, reason, observedLength,
                    breachedCap);
                kept[fieldName] = value;
                continue;
            }

            MaintenanceLog.AttributeCapExceeded(
                logger, promptName, logGroupId, logEntityUuid, fieldName, reason, observedLength,
                breachedCap);
            dropped.Add(fieldName);
        }

        return (kept, dropped);
    }

    /// <summary>
    /// Caps the LLM response with <see cref="CapStringAttributes"/> and merges it with the prior attributes per
    /// <paramref name="mergeMode"/> (upstream <c>apply_capped_attributes</c>). An explicit <see langword="null"/>
    /// from the LLM keeps a non-null prior value; with no prior value the null is kept.
    /// </summary>
    /// <returns>The merged attributes and the names of the dropped fields.</returns>
    public static (Dictionary<string, object?> Merged, HashSet<string> Dropped) ApplyCappedAttributes(
        IReadOnlyDictionary<string, object?> response,
        Type model,
        IReadOnlyDictionary<string, object?> priorAttributes,
        AttributeMergeMode mergeMode,
        int defaultMaxLength = DefaultAttributeMaxLength,
        string promptName = "",
        string entityUuid = "",
        string groupId = "",
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(priorAttributes);
        var (capped, dropped) = CapStringAttributes(
            response, model, defaultMaxLength, promptName, entityUuid, groupId, logger);

        var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in capped)
        {
            kept[field] = PythonText.Normalize(value) is null &&
                          priorAttributes.TryGetValue(field, out var prior) &&
                          PythonText.Normalize(prior) is not null
                ? prior
                : value;
        }

        Dictionary<string, object?> merged;
        switch (mergeMode)
        {
            case AttributeMergeMode.Overlay:
                merged = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, value) in priorAttributes)
                {
                    merged[key] = value;
                }

                foreach (var (key, value) in kept)
                {
                    merged[key] = value;
                }

                break;
            case AttributeMergeMode.Replace:
                merged = kept;
                foreach (var field in dropped)
                {
                    if (priorAttributes.TryGetValue(field, out var prior))
                    {
                        merged[field] = prior;
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mergeMode), mergeMode, "merge_mode must be \"overlay\" or \"replace\"");
        }

        return (merged, dropped);
    }

    /// <summary>
    /// Reads an LLM attribute response (a JSON object) into attribute values, keeping key order: strings, longs,
    /// doubles (a number with a fraction or exponent, as Python's <c>json.loads</c>), booleans, nulls, lists and
    /// dictionaries. A response that is not an object yields no attributes.
    /// </summary>
    public static Dictionary<string, object?> FromJsonResponse(JsonElement response)
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

    private static string OrUnknown(string value) => string.IsNullOrEmpty(value) ? "<unknown>" : value;

    private static int ResolveDefaultMaxLength(int defaultMaxLength, ILogger logger)
    {
        var raw = Environment.GetEnvironmentVariable(MaxLengthEnvironmentVariable);
        if (raw is null || raw.Trim().Length == 0)
        {
            return defaultMaxLength;
        }

        // As Python's int(): surrounding whitespace and a sign are allowed.
        if (int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        if (WarnedInvalidEnv.TryAdd(raw, true))
        {
            MaintenanceLog.InvalidAttributeMaxLength(logger, MaxLengthEnvironmentVariable, PythonText.Repr(raw), defaultMaxLength);
        }

        return defaultMaxLength;
    }

    private static (bool Exceeded, string Reason, int ObservedLength, int BreachedCap) CheckValueAgainstCap(
        object? value, int maxLen)
    {
        value = PythonText.Normalize(value);
        if (value is string s)
        {
            var length = DedupHelpers.CodePointLength(s);
            return length > maxLen ? (true, "per_item", length, maxLen) : (false, "ok", 0, maxLen);
        }

        if (value is not null && !PythonText.TryGetMapping(value, out _) && PythonText.TryGetSequence(value, out var items))
        {
            var lengths = items.Select(PythonText.Normalize).OfType<string>().Select(DedupHelpers.CodePointLength).ToList();
            var maxItem = lengths.Count > 0 ? lengths.Max() : 0;
            if (maxItem > maxLen)
            {
                return (true, "per_item", maxItem, maxLen);
            }

            var total = lengths.Sum();
            var aggregateCap = maxLen * ListTotalLengthMultiplier;
            if (total > aggregateCap)
            {
                return (true, "aggregate", total, aggregateCap);
            }
        }

        return (false, "ok", 0, maxLen);
    }
}
