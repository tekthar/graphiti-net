// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ComponentModel;
using System.Text.Json.Nodes;
using GraphitiNet.Llm;

namespace GraphitiNet.Tests.Llm;

public sealed record SchemaEntity(
    [property: Description("Name of the entity")] string Name,
    int EntityTypeId,
    string? Summary = null);

[Description("Extracted entities.")]
public sealed record SchemaEntities(IReadOnlyList<SchemaEntity> ExtractedEntities, SchemaEntity? Primary = null);

public class ResponseModelSchemaTests
{
    [Fact]
    public void Schema_UsesSnakeCaseTitlesAndDescriptions()
    {
        var schema = ResponseModelSchema.GetSchema(typeof(SchemaEntity));

        Assert.Equal("SchemaEntity", (string?)schema["title"]);
        var properties = schema["properties"]!.AsObject();
        Assert.Equal(["name", "entity_type_id", "summary"], properties.Select(p => p.Key));
        Assert.Equal("Name of the entity", (string?)properties["name"]!["description"]);
        Assert.Equal("Entity Type Id", (string?)properties["entity_type_id"]!["title"]);
        var required = schema["required"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("name", required);
        Assert.Contains("entity_type_id", required);
        Assert.DoesNotContain("summary", required);
    }

    [Fact]
    public void Schema_InlinesRepeatedTypes()
    {
        var schema = ResponseModelSchema.GetSchema(typeof(SchemaEntities));

        Assert.DoesNotContain("$ref", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("Extracted entities.", (string?)schema["description"]);
        var primary = schema["properties"]!["primary"]!;
        Assert.NotNull(primary["properties"]?["entity_type_id"]);
    }

    [Fact]
    public void StrictSchema_RequiresEverythingAndForbidsExtraProperties()
    {
        var strict = ResponseModelSchema.ToStrictSchema(ResponseModelSchema.GetSchema(typeof(SchemaEntities)));

        AssertStrict(strict);
        var item = strict["properties"]!["extracted_entities"]!["items"]!;
        Assert.Equal(["name", "entity_type_id", "summary"],
            item["required"]!.AsArray().Select(n => (string?)n));
        Assert.DoesNotContain("\"default\"", strict.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WritesPythonJsonDumpsFormat()
    {
        var text = ResponseModelSchema.Serialize(typeof(SchemaEntity));

        Assert.StartsWith("{\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\": {", text, StringComparison.Ordinal);
        Assert.Contains("\", \"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonJson_EscapesLikeJsonDumps()
    {
        var node = new JsonObject { ["b"] = "caf\u00e9 \"q\"\n", ["a"] = new JsonArray(1, true, null) };

        Assert.Equal("{\"b\": \"caf\\u00e9 \\\"q\\\"\\n\", \"a\": [1, true, null]}", PythonJson.Dumps(node));
        Assert.Equal("{\"a\": [1, true, null], \"b\": \"caf\\u00e9 \\\"q\\\"\\n\"}", PythonJson.Dumps(node, sortKeys: true));
    }

    private static void AssertStrict(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
            {
                Assert.False((bool)obj["additionalProperties"]!);
                Assert.Equal(properties.Select(p => p.Key), obj["required"]!.AsArray().Select(n => (string?)n));
                foreach (var (_, value) in properties)
                {
                    AssertStrict(value);
                }
            }

            foreach (var (key, value) in obj)
            {
                if (key != "properties")
                {
                    AssertStrict(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                AssertStrict(item);
            }
        }
    }
}
