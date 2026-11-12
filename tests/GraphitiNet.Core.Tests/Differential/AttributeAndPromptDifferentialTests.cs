// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using GraphitiNet.Maintenance;
using GraphitiNet.Prompts;
using GraphitiNet.Tests.Maintenance;

namespace GraphitiNet.Tests.Differential;

/// <summary>attribute_utils.py and prompt_helpers.to_prompt_json against upstream's recorded results.</summary>
/// <remarks>Runs with the other tests that read <c>GRAPHITI_ATTRIBUTE_MAX_LENGTH</c>, which the fixtures leave
/// unset.</remarks>
[Collection(AttributeEnvironmentGroup.Name)]
public class AttributeAndPromptDifferentialTests
{
    [Fact]
    public void CapStringAttributes_matches_upstream()
    {
        var m = new Mismatches("cap_string_attributes");
        foreach (var c in Fixture.Cases("attribute_utils", "cap_string_attributes"))
        {
            var (kept, dropped) = AttributeUtils.CapStringAttributes(
                Response(c.Input), Model(c.Input), c.Input.GetProperty("default_max_length").GetInt32());
            m.Equal(c, c.Output.GetProperty("kept"), kept, "kept");
            m.Equal(c, c.Output.GetProperty("dropped"), dropped, "dropped");
        }

        m.AssertNone();
    }

    [Fact]
    public void ApplyCappedAttributes_matches_upstream()
    {
        var m = new Mismatches("apply_capped_attributes");
        foreach (var c in Fixture.Cases("attribute_utils", "apply_capped_attributes"))
        {
            var mode = c.Input.GetProperty("merge_mode").GetString() == "overlay"
                ? AttributeMergeMode.Overlay
                : AttributeMergeMode.Replace;
            var prior = (Dictionary<string, object?>)Fixture.Decode(c.Input.GetProperty("prior"))!;
            var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
                Response(c.Input), Model(c.Input), prior, mode, c.Input.GetProperty("default_max_length").GetInt32());

            m.Equal(c, c.Output.GetProperty("dropped"), dropped, "dropped");

            // Replace mode restores dropped fields in Python set order, which is arbitrary; compare those keys as a set
            // and every other key in order.
            var expected = c.Output.GetProperty("merged");
            var droppedNames = dropped.ToHashSet(StringComparer.Ordinal);
            m.Equal(c, Without(expected, droppedNames), merged.Where(p => !droppedNames.Contains(p.Key)).ToList(),
                "merged");
            m.Equal(c, Only(expected, droppedNames),
                merged.Where(p => droppedNames.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList(),
                "restored fields");
        }

        m.AssertNone();
    }

    [Fact]
    public void ToPromptJson_matches_upstream()
    {
        var m = new Mismatches("to_prompt_json");
        foreach (var c in Fixture.Cases("prompt_helpers", "to_prompt_json"))
        {
            var indent = c.Input.GetProperty("indent") is { ValueKind: JsonValueKind.Number } i
                ? i.GetInt32()
                : (int?)null;
            var json = PromptHelpers.ToPromptJson(
                Fixture.Decode(c.Input.GetProperty("data")), c.Input.GetProperty("ensure_ascii").GetBoolean(), indent);
            m.Equal(c, c.Output, json);
        }

        m.AssertNone();
    }

    private static Dictionary<string, object?> Response(JsonElement input) =>
        (Dictionary<string, object?>)Fixture.Decode(input.GetProperty("response"))!;

    private static Type Model(JsonElement input) => input.GetProperty("model").GetString() switch
    {
        "DiffPerson" => typeof(DiffPerson),
        "DiffEmpty" => typeof(DiffEmpty),
        var other => throw new NotSupportedException(other),
    };

    private static JsonElement Without(JsonElement map, HashSet<string> names) =>
        JsonSerializer.SerializeToElement(map.EnumerateObject().Where(p => !names.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value));

    private static JsonElement Only(JsonElement map, HashSet<string> names) =>
        JsonSerializer.SerializeToElement(map.EnumerateObject().Where(p => names.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => p.Value));

    // The generator's Pydantic models, field for field:
    //
    //     class DiffPerson(BaseModel):
    //         full_name: str
    //         phones: str | None = Field(default=None, max_length=20)
    //         aliases: list[str] | None = None
    //         industry: str | None = None
    //         notes: str | None = Field(default=None, max_length=0)
    //         tags: list[str]
    //         bio: str = Field(default='', max_length=5)
    //
    //     class DiffEmpty(BaseModel):
    //         pass
#pragma warning disable CA1812 // Instantiated by reflection only.
    private sealed class DiffPerson
    {
        public required string FullName { get; init; }

        [MaxLength(20)]
        public string? Phones { get; init; }

        public List<string>? Aliases { get; init; }

        public string? Industry { get; init; }

        [MaxLength(0)]
        public string? Notes { get; init; }

        public required List<string> Tags { get; init; }

        [MaxLength(5)]
        public string Bio { get; init; } = "";
    }

    private sealed class DiffEmpty;
#pragma warning restore CA1812
}
