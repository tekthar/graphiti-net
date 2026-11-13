// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GraphitiNet.Llm;

/// <summary>
/// Turns a response model type (a C# record whose JSON property names are snake_case) into a JSON schema, and
/// reads LLM output back into it. Upstream uses Pydantic's <c>model_json_schema()</c> for this.
/// </summary>
/// <remarks>
/// This is the seam for the prompts port: when <c>GraphitiNet.Prompts</c> has a Pydantic-compatible schema helper,
/// make <see cref="GetSchema"/> (and, if it differs, <see cref="SerializerOptions"/>) delegate to it. Every LLM client
/// gets its schemas from here.
/// </remarks>
internal static class ResponseModelSchema
{
    private const int MaxRefDepth = 32;

    /// <summary>Options for writing schemas and reading responses: snake_case names, required constructor
    /// parameters and nullable annotations respected, enums as strings.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = AddTitleAndDescription,
    };

    /// <summary>The schema name sent to providers; upstream uses the Pydantic class name.</summary>
    public static string GetName(Type responseModel)
    {
        ArgumentNullException.ThrowIfNull(responseModel);
        var display = (System.ComponentModel.DisplayNameAttribute?)Attribute.GetCustomAttribute(
            responseModel, typeof(System.ComponentModel.DisplayNameAttribute));
        return display?.DisplayName is { Length: > 0 } name ? name : responseModel.Name;
    }

    /// <summary>The JSON schema of <paramref name="responseModel"/>, with references inlined.</summary>
    public static JsonObject GetSchema(Type responseModel)
    {
        ArgumentNullException.ThrowIfNull(responseModel);
        // The Pydantic-shaped generator from the prompts port, so providers see the same schema upstream sends.
        var schema = InlineRefs(GraphitiNet.Prompts.ResponseSchema.Generate(responseModel, SerializerOptions));
        schema.Remove("$defs");
        return schema;
    }

    /// <summary>The schema as upstream writes it into prompts: <c>json.dumps(model_json_schema())</c>.</summary>
    public static string Serialize(Type responseModel) =>
        GraphitiNet.Prompts.ResponseSchema.ToPromptJson(responseModel, SerializerOptions);

    /// <summary>
    /// Makes a schema meet OpenAI's strict structured-output subset, as the OpenAI Python SDK's
    /// <c>to_strict_json_schema</c> does for <c>responses.parse</c>: every object gets
    /// <c>additionalProperties: false</c> and lists all its properties as required, and defaults are dropped.
    /// </summary>
    public static JsonObject ToStrictSchema(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var strict = (JsonObject)schema.DeepClone();
        MakeStrict(strict);
        return strict;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Converters = { new JsonStringEnumConverter() },
        };
        options.MakeReadOnly();
        return options;
    }

    private static JsonNode AddTitleAndDescription(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject obj)
        {
            return node;
        }

        ICustomAttributeProvider? provider = context.PropertyInfo?.AttributeProvider;
        if (context.PropertyInfo is { } property)
        {
            if (!obj.ContainsKey("title"))
            {
                obj["title"] = TitleCase(property.Name);
            }
        }
        else if (context.Path.IsEmpty)
        {
            provider = context.TypeInfo.Type;
            if (!obj.ContainsKey("title"))
            {
                obj["title"] = context.TypeInfo.Type.Name;
            }
        }

        var description = provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()?.Description;
        if (description is not null && !obj.ContainsKey("description"))
        {
            obj["description"] = description;
        }

        return obj;
    }

    // Pydantic's default field title: name.replace('_', ' ').title().
    private static string TitleCase(string name)
    {
        var chars = name.Replace('_', ' ').ToCharArray();
        var startOfWord = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLetter(c))
            {
                chars[i] = startOfWord ? char.ToUpper(c, CultureInfo.InvariantCulture)
                    : char.ToLower(c, CultureInfo.InvariantCulture);
                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }

        return new string(chars);
    }

    private static JsonObject InlineRefs(JsonObject root)
    {
        var original = (JsonObject)root.DeepClone();
        var result = (JsonObject)root.DeepClone();
        InlineRefs(result, original, 0);
        return result;
    }

    private static void InlineRefs(JsonNode? node, JsonObject original, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonObject childObj && TryResolveRef(childObj, original, depth, out var resolved))
                    {
                        obj[key] = resolved;
                        InlineRefs(resolved, original, depth + 1);
                    }
                    else
                    {
                        InlineRefs(child, original, depth);
                    }
                }

                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonObject childObj && TryResolveRef(childObj, original, depth, out var resolved))
                    {
                        arr[i] = resolved;
                        InlineRefs(resolved, original, depth + 1);
                    }
                    else
                    {
                        InlineRefs(child, original, depth);
                    }
                }

                break;
        }
    }

    private static bool TryResolveRef(JsonObject obj, JsonObject original, int depth, out JsonObject resolved)
    {
        resolved = null!;
        if (depth >= MaxRefDepth || obj["$ref"] is not JsonValue refValue
            || !refValue.TryGetValue<string>(out var pointer) || !pointer.StartsWith('#'))
        {
            return false;
        }

        JsonNode? target = original;
        foreach (var raw in pointer[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            target = target switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, CultureInfo.InvariantCulture, out var index) => a[index],
                _ => null,
            };
        }

        if (target is not JsonObject targetObj)
        {
            return false;
        }

        resolved = (JsonObject)targetObj.DeepClone();
        // Keep sibling keywords such as "description" next to the reference.
        foreach (var (key, value) in obj)
        {
            if (key != "$ref")
            {
                resolved[key] = value?.DeepClone();
            }
        }

        return true;
    }

    private static void MakeStrict(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("default");
                if (obj["properties"] is JsonObject properties)
                {
                    obj["additionalProperties"] = false;
                    obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)p.Key).ToArray());
                    foreach (var (_, value) in properties)
                    {
                        MakeStrict(value);
                    }
                }
                else if (IsObjectType(obj) && !obj.ContainsKey("additionalProperties"))
                {
                    obj["additionalProperties"] = false;
                }

                foreach (var (key, value) in obj)
                {
                    if (key != "properties")
                    {
                        MakeStrict(value);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    MakeStrict(item);
                }

                break;
        }
    }

    private static bool IsObjectType(JsonObject obj) => obj["type"] switch
    {
        JsonValue v => v.TryGetValue<string>(out var s) && s == "object",
        JsonArray a => a.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "object"),
        _ => false,
    };
}
