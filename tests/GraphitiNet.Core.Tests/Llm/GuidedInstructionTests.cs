// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Llm;

public class GuidedInstructionTests
{
    // Written by parity/python/lme_qa.py's guided_instruction from upstream's Pydantic models, so a guided run sends
    // the same text from both implementations.
    private static readonly Dictionary<string, string> Python = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "Fixtures", "guided_instructions.json")))!;

    public static TheoryData<string> Models => ["EdgeDuplicate", "ExtractedEntities", "NodeResolutions", "ExtractedEdges"];

    [Theory]
    [MemberData(nameof(Models))]
    public void Matches_the_python_harness_character_for_character(string name)
    {
        var type = name switch
        {
            "EdgeDuplicate" => typeof(EdgeDuplicate),
            "ExtractedEntities" => typeof(ExtractedEntities),
            "NodeResolutions" => typeof(NodeResolutions),
            _ => typeof(ExtractedEdges),
        };

        Assert.Equal(Python[name], ResponseModelSchema.GuidedInstruction(type));
    }

    [Fact]
    public void Example_is_an_instance_not_a_schema()
    {
        var text = ResponseModelSchema.GuidedInstruction(typeof(EdgeDuplicate));

        Assert.EndsWith("{\"duplicate_facts\": [0], \"contradicted_facts\": [0]}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Guided_mode_asks_for_json_object()
    {
        var client = new OpenAIGenericClient(
            new LLMConfig(apiKey: "test"), structuredOutputMode: StructuredOutputMode.JsonObjectGuided);

        Assert.Equal("json_object", client.BuildResponseFormat(typeof(EdgeDuplicate))["type"]!.GetValue<string>());
    }
}
