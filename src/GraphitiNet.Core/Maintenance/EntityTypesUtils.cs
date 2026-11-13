// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/ontology_utils/entity_types_utils.py and validate_excluded_entity_types in
// graphiti_core/helpers.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Maintenance;

/// <summary>
/// Checks for custom entity types. A custom entity type is a class or record: its <see cref="DescriptionAttribute"/>
/// plays the part of upstream's Pydantic docstring, and its public properties are the attributes the LLM extracts,
/// named in snake_case (or by <see cref="JsonPropertyNameAttribute"/>). See <c>docs/custom-types.md</c>.
/// </summary>
public static class EntityTypesUtils
{
    /// <summary>
    /// The attribute names an entity type may not use because <see cref="Models.EntityNode"/> already has a field of
    /// that name (upstream <c>EntityNode.model_fields</c>).
    /// </summary>
    public static IReadOnlySet<string> ProtectedAttributeNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "uuid", "name", "group_id", "labels", "created_at", "name_embedding", "summary", "attributes",
    };

    /// <summary>
    /// Checks that no custom entity type declares an attribute with a protected name (upstream
    /// <c>validate_entity_types</c>).
    /// </summary>
    /// <returns><see langword="true"/>.</returns>
    /// <exception cref="EntityTypeValidationException">A type uses a protected attribute name.</exception>
    public static bool ValidateEntityTypes(IReadOnlyDictionary<string, Type>? entityTypes)
    {
        if (entityTypes is null)
        {
            return true;
        }

        foreach (var (entityTypeName, entityTypeModel) in entityTypes)
        {
            foreach (var field in GetFields(entityTypeModel))
            {
                if (ProtectedAttributeNames.Contains(field.Name))
                {
                    throw new EntityTypeValidationException(entityTypeName, field.Name);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Checks that every excluded type name is <c>Entity</c> or a custom entity type (upstream
    /// <c>validate_excluded_entity_types</c>).
    /// </summary>
    /// <returns><see langword="true"/>.</returns>
    /// <exception cref="ArgumentException">An excluded name is not an available type (upstream raises
    /// <c>ValueError</c> with the same message).</exception>
    public static bool ValidateExcludedEntityTypes(
        IReadOnlyList<string>? excludedEntityTypes,
        IReadOnlyDictionary<string, Type>? entityTypes = null)
    {
        if (excludedEntityTypes is null || excludedEntityTypes.Count == 0)
        {
            return true;
        }

        var availableTypes = new HashSet<string>(StringComparer.Ordinal) { DedupHelpers.EntityBaseLabel };
        if (entityTypes is not null)
        {
            availableTypes.UnionWith(entityTypes.Keys);
        }

        var invalidTypes = excludedEntityTypes.ToHashSet(StringComparer.Ordinal);
        invalidTypes.ExceptWith(availableTypes);
        if (invalidTypes.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid excluded entity types: {PythonText.Repr(invalidTypes.Order(StringComparer.Ordinal).ToList())}. " +
                $"Available types: {PythonText.Repr(availableTypes.Order(StringComparer.Ordinal).ToList())}",
                nameof(excludedEntityTypes));
        }

        return true;
    }

    /// <summary>The type's description (<see cref="DescriptionAttribute"/>), upstream's <c>__doc__</c>, or
    /// <see langword="null"/>.</summary>
    public static string? GetDescription(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return entityType.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }

    /// <summary>
    /// The attribute fields of an entity or edge type, as Pydantic's <c>model_fields</c>: public readable instance
    /// properties not marked <see cref="JsonIgnoreAttribute"/>, base type first, in declaration order.
    /// </summary>
    internal static IReadOnlyList<AttributeField> GetFields(Type model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var naming = ResponseModelSchema.SerializerOptions.PropertyNamingPolicy;
        var positional = PositionalParameters(model);
        var nullability = new NullabilityInfoContext();
        var hierarchy = new Stack<Type>();
        for (var t = model; t is not null && t != typeof(object); t = t.BaseType)
        {
            hierarchy.Push(t);
        }

        var fields = new List<AttributeField>();
        foreach (var property in hierarchy.SelectMany(t => t
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0 &&
                                 !p.IsDefined(typeof(JsonIgnoreAttribute)) && p.Name != "EqualityContract")
                     .OrderBy(p => p.MetadataToken)))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? naming?.ConvertName(property.Name)
                ?? property.Name;
            var isNullable = nullability.Create(property).ReadState == NullabilityState.Nullable;
            var isRequired = property.IsDefined(typeof(RequiredMemberAttribute)) ||
                             property.IsDefined(typeof(JsonRequiredAttribute)) ||
                             (positional.TryGetValue(property.Name, out var parameter) &&
                              !parameter.HasDefaultValue && !isNullable);
            int? maxLength = property.GetCustomAttribute<MaxLengthAttribute>()?.Length
                ?? property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;
            fields.Add(new AttributeField(name, property, isRequired, maxLength is > 0 ? maxLength : null));
        }

        return fields;
    }

    private static Dictionary<string, ParameterInfo> PositionalParameters(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        return constructors is [{ } only] && only.GetParameters().Length > 0
            ? only.GetParameters().Where(p => p.Name is not null)
                .ToDictionary(p => p.Name!, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>One attribute field of a custom type.</summary>
/// <param name="Name">The JSON (snake_case) name.</param>
/// <param name="Property">The property.</param>
/// <param name="IsRequired">Whether the field has no default (Pydantic <c>is_required()</c>): the
/// <see langword="required"/> modifier, <see cref="JsonRequiredAttribute"/>, or a non-nullable positional parameter
/// without a default.</param>
/// <param name="MaxLength">An explicit length cap from <see cref="MaxLengthAttribute"/> or
/// <see cref="StringLengthAttribute"/> (Pydantic <c>Field(max_length=...)</c>).</param>
internal sealed record AttributeField(string Name, PropertyInfo Property, bool IsRequired, int? MaxLength);
