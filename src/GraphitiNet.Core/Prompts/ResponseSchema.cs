// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GraphitiNet.Prompts;

/// <summary>
/// JSON schemas for response models, shaped like Pydantic's <c>model_json_schema()</c>. Upstream appends
/// <c>json.dumps(response_model.model_json_schema())</c> to the prompt and passes the schema to providers' structured
/// output, so the schema text is part of the prompt.
/// </summary>
/// <remarks>
/// <para>
/// The schema of a class or record is
/// <c>{"$defs": ..., "description": ..., "properties": ..., "required": [...], "title": ..., "type": "object"}</c>:
/// </para>
/// <list type="bullet">
/// <item><description><c>title</c> is the type's <see cref="DisplayNameAttribute"/> or its name; each property's
/// <c>title</c> is its JSON name in Python title case with underscores as spaces (<c>entity_type_id</c> →
/// <c>Entity Type Id</c>), except that a property whose schema is a reference to a class or enum has no title, as
/// in Pydantic.</description></item>
/// <item><description><c>description</c> comes from <see cref="DescriptionAttribute"/> on the type or property, and
/// is left out when there is none.</description></item>
/// <item><description>A property's JSON name is its <see cref="JsonPropertyNameAttribute"/>, else the naming
/// policy's conversion, else its name. Properties marked <see cref="JsonIgnoreAttribute"/> are left out. Properties
/// keep their declaration order.</description></item>
/// <item><description>A property is required when it has the <see langword="required"/> modifier or
/// <see cref="JsonRequiredAttribute"/>, or is a non-nullable parameter without a default value of the type's only
/// public constructor (a positional record). A property that may be <see langword="null"/> is
/// <c>{"anyOf": [schema, {"type": "null"}]}</c> and, unless required, has <c>"default": null</c>. A
/// <see cref="DefaultValueAttribute"/> sets <c>default</c> explicitly. Other defaults (Pydantic's
/// <c>default_factory</c>, a C# initializer) are not in the schema, as with Pydantic.</description></item>
/// <item><description>Nested classes and enums go in <c>$defs</c> and are referenced with
/// <c>{"$ref": "#/$defs/Name"}</c>.</description></item>
/// <item><description>Keys are sorted alphabetically at every level except inside <c>properties</c> and
/// <c>default</c>, as Pydantic sorts them.</description></item>
/// </list>
/// <para>
/// Type mapping: <see cref="string"/> and <see cref="char"/> → <c>string</c>; <see cref="bool"/> →
/// <c>boolean</c>; integral types → <c>integer</c>; <see cref="float"/>, <see cref="double"/> and
/// <see cref="decimal"/> → <c>number</c>; <see cref="DateTime"/> and <see cref="DateTimeOffset"/> → string with
/// <c>format: date-time</c>; <see cref="DateOnly"/> → <c>date</c>; <see cref="TimeOnly"/> → <c>time</c>;
/// <see cref="TimeSpan"/> → <c>duration</c>; <see cref="Guid"/> → <c>uuid</c>; enums → <c>{"enum": [names],
/// "title": ..., "type": "string"}</c>; dictionaries → <c>{"additionalProperties": ..., "type": "object"}</c>;
/// other enumerables → <c>{"items": ..., "type": "array"}</c>; <see cref="object"/>, <see cref="JsonElement"/> and
/// <see cref="JsonNode"/> → <c>{}</c> (any value).
/// </para>
/// <para>
/// For the built-in response models the output is identical to upstream's (the tests compare it with
/// <c>model_json_schema()</c> output from Pydantic). For other types it is as close as the mapping above allows;
/// Pydantic features with no C# counterpart (constraints such as <c>min_length</c>, <c>Literal</c> types, custom
/// <c>json_schema_extra</c>) are not reproduced.
/// </para>
/// </remarks>
public static class ResponseSchema
{
    /// <summary>Generates the Pydantic-shaped JSON schema of a response model type.</summary>
    /// <param name="modelType">A class or record.</param>
    /// <param name="options">Serializer options whose <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> names
    /// properties without a <see cref="JsonPropertyNameAttribute"/>.</param>
    public static JsonObject Generate(Type modelType, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        var generator = new Generator(options);
        var schema = generator.ModelSchema(modelType);
        if (generator.Defs.Count > 0)
        {
            var defs = new JsonObject();
            foreach (var (name, def) in generator.Defs)
            {
                defs[name] = def;
            }

            schema["$defs"] = defs;
        }

        return SortObject(schema, parentKey: null);
    }

    /// <summary>
    /// The schema as upstream puts it in the prompt: <c>json.dumps(response_model.model_json_schema())</c>, which
    /// escapes non-ASCII characters.
    /// </summary>
    /// <param name="modelType">A class or record.</param>
    /// <param name="options">See <see cref="Generate"/>.</param>
    public static string ToPromptJson(Type modelType, JsonSerializerOptions? options = null) =>
        PromptHelpers.ToPromptJson(Generate(modelType, options), ensureAscii: true);

    /// <summary>The title Pydantic gives a field: <c>name.title().replace('_', ' ')</c>.</summary>
    internal static string FieldTitle(string name)
    {
        var sb = new StringBuilder(name.Length);
        var previousIsLetter = false;
        foreach (var c in name)
        {
            if (char.IsLetter(c))
            {
                sb.Append(previousIsLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
                previousIsLetter = true;
            }
            else
            {
                sb.Append(c == '_' ? ' ' : c);
                previousIsLetter = false;
            }
        }

        return sb.ToString().Trim();
    }

    // Pydantic's GenerateJsonSchema.sort: keys sorted at every level except directly under "properties" and
    // "default".
    private static JsonObject SortObject(JsonObject obj, string? parentKey)
    {
        IEnumerable<KeyValuePair<string, JsonNode?>> entries = obj;
        if (parentKey is not ("properties" or "default"))
        {
            entries = entries.OrderBy(e => e.Key, StringComparer.Ordinal);
        }

        var sorted = new JsonObject();
        foreach (var (key, value) in entries)
        {
            sorted[key] = SortNode(value, key);
        }

        return sorted;
    }

    private static JsonNode? SortNode(JsonNode? node, string? parentKey) => node switch
    {
        JsonObject obj => SortObject(obj, parentKey),
        JsonArray array => new JsonArray(array.Select(item => SortNode(item, parentKey)).ToArray()),
        _ => node?.DeepClone(),
    };

    private sealed class Generator(JsonSerializerOptions? options)
    {
        private readonly NullabilityInfoContext _nullability = new();
        private readonly HashSet<Type> _inProgress = [];

        public Dictionary<string, JsonObject> Defs { get; } = new(StringComparer.Ordinal);

        public JsonObject ModelSchema(Type type)
        {
            _inProgress.Add(type);
            var schema = new JsonObject();
            if (type.GetCustomAttribute<DescriptionAttribute>() is { } typeDescription)
            {
                schema["description"] = typeDescription.Description;
            }

            var properties = new JsonObject();
            var required = new JsonArray();
            var constructorParameters = PositionalParameters(type);
            foreach (var property in GetProperties(type))
            {
                var name = JsonName(property);
                var nullability = _nullability.Create(property);
                var isNullable = nullability.ReadState == NullabilityState.Nullable;
                var isRequired = property.IsDefined(typeof(RequiredMemberAttribute)) ||
                                 property.IsDefined(typeof(JsonRequiredAttribute)) ||
                                 (constructorParameters.TryGetValue(property.Name, out var parameter) &&
                                  !parameter.HasDefaultValue && !isNullable);

                var propertySchema = TypeSchema(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, nullability);

                // Pydantic gives no title to a field whose schema is a reference to a model or enum.
                var isReference = propertySchema.ContainsKey("$ref");
                if (isNullable)
                {
                    propertySchema = new JsonObject { ["anyOf"] = new JsonArray(propertySchema, new JsonObject { ["type"] = "null" }) };
                }

                if (property.GetCustomAttribute<DefaultValueAttribute>() is { } defaultValue)
                {
                    propertySchema["default"] = JsonSerializer.SerializeToNode(defaultValue.Value);
                }
                else if (isNullable && !isRequired)
                {
                    propertySchema["default"] = null;
                }

                if (property.GetCustomAttribute<DescriptionAttribute>() is { } description)
                {
                    propertySchema["description"] = description.Description;
                }

                if (!isReference)
                {
                    propertySchema["title"] = FieldTitle(name);
                }

                properties[name] = propertySchema;
                if (isRequired)
                {
                    required.Add(name);
                }
            }

            schema["properties"] = properties;
            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            schema["title"] = Title(type);
            schema["type"] = "object";
            _inProgress.Remove(type);
            return schema;
        }

        /// <summary>
        /// The parameters of a positional record's (or any type's single public) constructor, by name: a parameter
        /// with no default value makes its property required, like a Pydantic field with no default.
        /// </summary>
        private static Dictionary<string, ParameterInfo> PositionalParameters(Type type)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            return constructors is [{ } only] && only.GetParameters().Length > 0
                ? only.GetParameters().Where(p => p.Name is not null)
                    .ToDictionary(p => p.Name!, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<PropertyInfo> GetProperties(Type type)
        {
            var hierarchy = new Stack<Type>();
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                hierarchy.Push(t);
            }

            return hierarchy.SelectMany(t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0 &&
                            !p.IsDefined(typeof(JsonIgnoreAttribute)) &&
                            p.Name != "EqualityContract")
                .OrderBy(p => p.MetadataToken));
        }

        private string JsonName(PropertyInfo property) =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? options?.PropertyNamingPolicy?.ConvertName(property.Name)
            ?? property.Name;

        private static string Title(Type type) =>
            type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName is { Length: > 0 } displayName
                ? displayName
                : type.Name.Split('`')[0];

        private JsonObject TypeSchema(Type type, NullabilityInfo? nullability)
        {
            if (type == typeof(string) || type == typeof(char))
            {
                return new JsonObject { ["type"] = "string" };
            }

            if (type == typeof(bool))
            {
                return new JsonObject { ["type"] = "boolean" };
            }

            if (type.IsEnum)
            {
                return Reference(type, () => new JsonObject
                {
                    ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode?)n).ToArray()),
                    ["title"] = Title(type),
                    ["type"] = "string",
                });
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or
                    TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64:
                    return new JsonObject { ["type"] = "integer" };
                case TypeCode.Single or TypeCode.Double or TypeCode.Decimal:
                    return new JsonObject { ["type"] = "number" };
            }

            var format = type == typeof(DateTime) || type == typeof(DateTimeOffset) ? "date-time"
                : type == typeof(DateOnly) ? "date"
                : type == typeof(TimeOnly) ? "time"
                : type == typeof(TimeSpan) ? "duration"
                : type == typeof(Guid) ? "uuid"
                : null;
            if (format is not null)
            {
                return new JsonObject { ["format"] = format, ["type"] = "string" };
            }

            if (type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(JsonElement))
            {
                return [];
            }

            if (DictionaryValueType(type) is { } valueType)
            {
                var valueNullability = nullability?.GenericTypeArguments is { Length: 2 } args ? args[1] : null;
                return new JsonObject
                {
                    ["additionalProperties"] = ElementSchema(valueType, valueNullability),
                    ["type"] = "object",
                };
            }

            if (ElementType(type) is { } elementType)
            {
                var elementNullability = nullability?.ElementType
                    ?? (nullability?.GenericTypeArguments is { Length: 1 } args ? args[0] : null);
                return new JsonObject { ["items"] = ElementSchema(elementType, elementNullability), ["type"] = "array" };
            }

            if (type.IsClass || type.IsValueType)
            {
                return Reference(type, () => ModelSchema(type));
            }

            throw new NotSupportedException($"Cannot generate a JSON schema for {type}.");
        }

        private JsonObject ElementSchema(Type type, NullabilityInfo? nullability)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            var schema = TypeSchema(underlying ?? type, nullability);
            var isNullable = underlying is not null || nullability?.ReadState == NullabilityState.Nullable;
            return isNullable
                ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) }
                : schema;
        }

        private JsonObject Reference(Type type, Func<JsonObject> build)
        {
            var name = Title(type);
            if (!Defs.ContainsKey(name) && !_inProgress.Contains(type))
            {
                Defs[name] = build();
            }

            return new JsonObject { ["$ref"] = "#/$defs/" + name };
        }

        private static Type? DictionaryValueType(Type type)
        {
            var dictionary = type.IsGenericType && IsDictionaryDefinition(type.GetGenericTypeDefinition())
                ? type
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && IsDictionaryDefinition(i.GetGenericTypeDefinition()));
            return dictionary?.GetGenericArguments()[1];
        }

        private static bool IsDictionaryDefinition(Type definition) =>
            definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>);

        private static Type? ElementType(Type type)
        {
            if (type.IsArray)
            {
                return type.GetElementType();
            }

            if (!typeof(IEnumerable).IsAssignableFrom(type))
            {
                return null;
            }

            var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable?.GetGenericArguments()[0] ?? typeof(object);
        }
    }
}
