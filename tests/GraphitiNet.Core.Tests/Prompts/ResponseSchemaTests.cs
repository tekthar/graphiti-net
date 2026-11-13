// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Prompts;

public class ResponseSchemaTests
{
    private static readonly Dictionary<string, Type> ModelTypes = new()
    {
        ["ExtractedEntity"] = typeof(ExtractedEntity),
        ["ExtractedEntities"] = typeof(ExtractedEntities),
        ["EntitySummary"] = typeof(EntitySummary),
        ["SummarizedEntity"] = typeof(SummarizedEntity),
        ["SummarizedEntities"] = typeof(SummarizedEntities),
        ["NodeDuplicate"] = typeof(NodeDuplicate),
        ["NodeResolutions"] = typeof(NodeResolutions),
        ["Edge"] = typeof(ExtractedEdge),
        ["ExtractedEdges"] = typeof(ExtractedEdges),
        ["EdgeTimestamps"] = typeof(EdgeTimestamps),
        ["BatchEdgeTimestamps"] = typeof(BatchEdgeTimestamps),
        ["CombinedEntity"] = typeof(CombinedEntity),
        ["CombinedFact"] = typeof(CombinedFact),
        ["CombinedExtraction"] = typeof(CombinedExtraction),
        ["EdgeDuplicate"] = typeof(EdgeDuplicate),
        ["Summary"] = typeof(Summary),
        ["SummaryDescription"] = typeof(SummaryDescription),
        ["SagaSummary"] = typeof(SagaSummary),
        ["QueryExpansion"] = typeof(QueryExpansion),
        ["QAResponse"] = typeof(QAResponse),
        ["EvalResponse"] = typeof(EvalResponse),
        ["EvalAddEpisodeResults"] = typeof(EvalAddEpisodeResults),
    };

    public static TheoryData<string> UpstreamModels()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in PromptFixtures.ResponseSchemas)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UpstreamModels))]
    public void Schema_matches_pydantic_model_json_schema(string upstreamName)
    {
        var expected = PromptFixtures.ResponseSchemas.Single(s => s.Name == upstreamName).Schema;
        Assert.True(ModelTypes.TryGetValue(upstreamName, out var type), $"No C# type for {upstreamName}");

        Assert.Equal(expected, ResponseSchema.ToPromptJson(type));
    }

    [Fact]
    public void Every_builtin_response_model_has_an_upstream_schema()
    {
        var models = PromptLibrary.BuiltinSpecs.Values.Select(s => s.ResponseModel).OfType<Type>().Distinct();
        foreach (var model in models)
        {
            Assert.Contains(model, ModelTypes.Values);
        }
    }

    [Fact]
    public void Response_models_round_trip_upstream_json()
    {
        var json = """{"extracted_entities": [{"name": "Alice", "entity_type_id": 1}, {"name": "Bob", "entity_type_id": 0, "episode_indices": [0, 2]}]}""";

        var parsed = JsonSerializer.Deserialize<ExtractedEntities>(json)!;

        Assert.Equal("Alice", parsed.Entities[0].Name);
        Assert.Equal([0], parsed.Entities[0].EpisodeIndices);
        Assert.Equal([0, 2], parsed.Entities[1].EpisodeIndices);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExtractedEntities>("{}"));
    }

    [Fact]
    public void Field_titles_follow_python_title_case()
    {
        Assert.Equal("Entity Type Id", ResponseSchema.FieldTitle("entity_type_id"));
        Assert.Equal("Answer", ResponseSchema.FieldTitle("ANSWER"));
        Assert.Equal("Abc1Def", ResponseSchema.FieldTitle("abc1def"));
        Assert.Equal("Firstname", ResponseSchema.FieldTitle("firstName"));
    }

    private enum Plan
    {
        Free,
        Pro,
    }

    [Description("A customer.")]
    private sealed class Customer
    {
        [Description("Full name")]
        public required string Name { get; init; }

        public string? Email { get; init; }

        public int? Age { get; init; }

        public Plan Plan { get; init; }

        [Description("the plan")]
        public Plan Plan2 { get; init; }

        [DefaultValue(3)]
        public int Seats { get; init; } = 3;

        public IReadOnlyList<Address> Addresses { get; init; } = [];

        public Address? Billing { get; init; }

        [Description("ship")]
        public Address Shipping { get; init; } = new("x", null);

        public Dictionary<string, double> Scores { get; init; } = [];

        public DateTimeOffset? Since { get; init; }

        [JsonIgnore]
        public string Hidden { get; init; } = "";

        public object? Extra { get; init; }
    }

    private sealed record Address(
        [property: JsonPropertyName("street_name")] string Street,
        string? City);

    [Fact]
    public void Custom_types_map_like_pydantic()
    {
        var json = ResponseSchema.ToPromptJson(typeof(Customer), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        Assert.Equal(
            """{"$defs": {"Address": {"properties": {"street_name": {"title": "Street Name", "type": "string"}, "city": {"anyOf": [{"type": "string"}, {"type": "null"}], "default": null, "title": "City"}}, "required": ["street_name"], "title": "Address", "type": "object"}, "Plan": {"enum": ["Free", "Pro"], "title": "Plan", "type": "string"}}, "description": "A customer.", "properties": {"name": {"description": "Full name", "title": "Name", "type": "string"}, "email": {"anyOf": [{"type": "string"}, {"type": "null"}], "default": null, "title": "Email"}, "age": {"anyOf": [{"type": "integer"}, {"type": "null"}], "default": null, "title": "Age"}, "plan": {"$ref": "#/$defs/Plan"}, "plan2": {"$ref": "#/$defs/Plan", "description": "the plan"}, "seats": {"default": 3, "title": "Seats", "type": "integer"}, "addresses": {"items": {"$ref": "#/$defs/Address"}, "title": "Addresses", "type": "array"}, "billing": {"anyOf": [{"$ref": "#/$defs/Address"}, {"type": "null"}], "default": null}, "shipping": {"$ref": "#/$defs/Address", "description": "ship"}, "scores": {"additionalProperties": {"type": "number"}, "title": "Scores", "type": "object"}, "since": {"anyOf": [{"format": "date-time", "type": "string"}, {"type": "null"}], "default": null, "title": "Since"}, "extra": {"anyOf": [{}, {"type": "null"}], "default": null, "title": "Extra"}}, "required": ["name"], "title": "Customer", "type": "object"}""",
            json);
    }
}
