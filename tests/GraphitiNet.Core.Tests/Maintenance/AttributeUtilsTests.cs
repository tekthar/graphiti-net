// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/utils/maintenance/test_attribute_utils.py (Graphiti v0.31.2) and changed for .NET.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using GraphitiNet.Maintenance;
using Microsoft.Extensions.Logging;

namespace GraphitiNet.Tests.Maintenance;

/// <summary>Tests that change GRAPHITI_ATTRIBUTE_MAX_LENGTH run in this collection, one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AttributeEnvironmentGroup
{
    public const string Name = "Attribute max length environment";
}

[Collection(AttributeEnvironmentGroup.Name)]
public class AttributeUtilsTests
{
    private const int Max = AttributeUtils.DefaultAttributeMaxLength;

    private sealed record Person
    {
        [Description("Phone numbers, comma-separated, with type tag where known")]
        public string? Phones { get; init; }

        [Description("Industry sector")]
        public string? Industry { get; init; }

        [MaxLength(2000)]
        [Description("Long-form description")]
        public string? Description { get; init; }

        [Description("Alternate names")]
        public IReadOnlyList<string>? Aliases { get; init; }
    }

    private sealed record Mixed
    {
        public double? Amount { get; init; }

        public string? Notes { get; init; }
    }

    private sealed record StrictPerson
    {
        [Description("Full legal name")]
        public required string FullName { get; init; }
    }

    private static Dictionary<string, object?> D(params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    private static (Dictionary<string, object?> Kept, HashSet<string> Dropped) Cap(
        Dictionary<string, object?> response, Type? model = null, ILogger? logger = null) =>
        AttributeUtils.CapStringAttributes(response, model ?? typeof(Person), logger: logger);

    [Fact]
    public void Short_string_passes_through()
    {
        var response = D(("phones", "415-555-0142"), ("industry", "SaaS"));
        var (kept, dropped) = Cap(response);
        Assert.Equal(response, kept);
        Assert.Empty(dropped);
    }

    [Fact]
    public void Overlong_string_dropped()
    {
        var (kept, dropped) = Cap(D(("phones", new string('x', Max * 10)), ("industry", "SaaS")));
        Assert.DoesNotContain("phones", kept.Keys);
        Assert.Equal("SaaS", kept["industry"]);
        Assert.Equal(["phones"], dropped);
    }

    [Fact]
    public void Explicit_max_length_honored_and_enforced()
    {
        var longDesc = new string('a', 1500);
        Assert.Equal(longDesc, Cap(D(("description", longDesc))).Kept["description"]);

        var (kept, dropped) = Cap(D(("description", new string('a', 2500))));
        Assert.Empty(kept);
        Assert.Equal(["description"], dropped);
    }

    [Fact]
    public void Non_string_and_null_fields_pass_through()
    {
        var response = D(("amount", 42.5), ("notes", "short note"));
        Assert.Equal(response, Cap(response, typeof(Mixed)).Kept);
        Assert.Equal(D(("industry", null)), Cap(D(("industry", null))).Kept);
    }

    [Fact]
    public void List_with_one_overlong_element_drops_field()
    {
        var (kept, dropped) = Cap(D(("aliases", new List<object?> { "Sammy", new string('x', Max + 1), "Sam R." })));
        Assert.Empty(kept);
        Assert.Equal(["aliases"], dropped);
    }

    [Fact]
    public void List_aggregate_length_caught()
    {
        var items = Enumerable.Repeat<object?>(new string('x', Max - 1), AttributeUtils.ListTotalLengthMultiplier + 2).ToList();
        var logger = new ListLogger();
        var (kept, dropped) = AttributeUtils.CapStringAttributes(
            D(("aliases", items)), typeof(Person), entityUuid: "node-uuid", groupId: "tenant-x", logger: logger,
            promptName: "extract_nodes.extract_attributes");

        Assert.Empty(kept);
        Assert.Equal(["aliases"], dropped);
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("attribute_length_cap_exceeded", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("reason=aggregate", entry.Message, StringComparison.Ordinal);
        Assert.Contains($"length={(Max - 1) * (AttributeUtils.ListTotalLengthMultiplier + 2)}", entry.Message, StringComparison.Ordinal);
        Assert.Contains($"cap={Max * AttributeUtils.ListTotalLengthMultiplier}", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void List_short_passes_through()
    {
        var response = D(("aliases", new List<object?> { "Sammy", "Sam R.", "S. Rivera" }));
        Assert.Equal(response, Cap(response).Kept);
    }

    [Fact]
    public void Required_field_overcap_kept_with_warning()
    {
        var bleed = new string('x', Max + 50);
        var logger = new ListLogger();
        var (kept, dropped) = AttributeUtils.CapStringAttributes(
            D(("full_name", bleed)), typeof(StrictPerson), promptName: "extract_nodes.extract_attributes",
            entityUuid: "abc-123", groupId: "tenant-x", logger: logger);

        Assert.Equal(D(("full_name", bleed)), kept);
        Assert.Empty(dropped);
        var matching = logger.Entries.Where(e => e.Message.Contains("attribute_length_cap_skipped_required", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(matching);
        Assert.All(matching, e => Assert.Equal(LogLevel.Warning, e.Level));
    }

    [Fact]
    public void Logs_info_on_drop_with_uuid_and_group_id_no_values()
    {
        var logger = new ListLogger();
        AttributeUtils.CapStringAttributes(
            D(("phones", "Sam Rivera" + new string('x', Max))), typeof(Person),
            promptName: "extract_nodes.extract_attributes", entityUuid: "abc-123-uuid", groupId: "tenant-x", logger: logger);

        var matching = logger.Entries.Where(e => e.Message.Contains("attribute_length_cap_exceeded", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(matching);
        Assert.All(matching, e => Assert.Equal(LogLevel.Information, e.Level));
        Assert.Contains(matching, e => e.Message.Contains("phones", StringComparison.Ordinal) &&
                                       e.Message.Contains("abc-123-uuid", StringComparison.Ordinal) &&
                                       e.Message.Contains("tenant-x", StringComparison.Ordinal) &&
                                       e.Message.Contains("reason=per_item", StringComparison.Ordinal));
        Assert.DoesNotContain(matching, e => e.Message.Contains("Sam Rivera", StringComparison.Ordinal));
    }

    [Fact]
    public void Env_var_overrides_default_but_explicit_max_length_wins() =>
        WithEnv("50", () =>
        {
            var (kept, dropped) = Cap(D(("phones", new string('x', 100))));
            Assert.Empty(kept);
            Assert.Equal(["phones"], dropped);

            var longDesc = new string('a', 1500);
            Assert.Equal(D(("description", longDesc)), Cap(D(("description", longDesc))).Kept);
        });

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("   ")]
    [InlineData("")]
    public void Env_var_invalid_falls_back_to_default(string value) =>
        WithEnv(value, () =>
        {
            var (kept, dropped) = Cap(D(("phones", new string('x', 100))));
            Assert.Equal(D(("phones", new string('x', 100))), kept);
            Assert.Empty(dropped);
        });

    [Fact]
    public void Invalid_env_warning_dedups_across_calls()
    {
        var value = "garbage-" + Guid.NewGuid();
        WithEnv(value, () =>
        {
            var logger = new ListLogger();
            for (var i = 0; i < 5; i++)
            {
                Cap(D(("phones", new string('x', 50))), logger: logger);
            }

            Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning &&
                e.Message.Contains("GRAPHITI_ATTRIBUTE_MAX_LENGTH", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Apply_overlay_keeps_prior_for_omitted_and_dropped_fields()
    {
        var prior = D(("phones", "415-555-0142"), ("industry", "Software"));
        var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
            D(("phones", new string('x', Max + 1))), typeof(Person), prior, AttributeMergeMode.Overlay);
        Assert.Equal("415-555-0142", merged["phones"]);
        Assert.Equal("Software", merged["industry"]);
        Assert.Equal(["phones"], dropped);
    }

    [Fact]
    public void Apply_overlay_legitimate_update_passes_through()
    {
        var prior = D(("phones", "415-555-0142"), ("industry", "Software"));
        var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
            D(("industry", "SaaS")), typeof(Person), prior, AttributeMergeMode.Overlay);
        Assert.Equal(D(("phones", "415-555-0142"), ("industry", "SaaS")), merged);
        Assert.Empty(dropped);
    }

    [Fact]
    public void Apply_overlay_preserves_prior_non_null_for_extracted_nulls()
    {
        var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
            D(("phones", null), ("industry", null)), typeof(Person), D(("phones", "415-555-0142")), AttributeMergeMode.Overlay);
        Assert.Equal(D(("phones", "415-555-0142"), ("industry", null)), merged);
        Assert.Empty(dropped);
    }

    [Fact]
    public void Apply_replace_clears_omitted_but_preserves_dropped()
    {
        var prior = D(("phones", "415-555-0142"), ("industry", "Software"));
        var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
            D(("phones", new string('x', Max + 1))), typeof(Person), prior, AttributeMergeMode.Replace);
        Assert.Equal("415-555-0142", merged["phones"]);
        Assert.DoesNotContain("industry", merged.Keys);
        Assert.Equal(["phones"], dropped);
    }

    [Fact]
    public void Apply_replace_legitimate_update_replaces_prior_and_clears_omitted()
    {
        var prior = D(("phones", "415-555-0142"), ("industry", "Software"));
        var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
            D(("industry", "SaaS")), typeof(Person), prior, AttributeMergeMode.Replace);
        Assert.Equal(D(("industry", "SaaS")), merged);
        Assert.Empty(dropped);
    }

    [Fact]
    public void Apply_replace_preserves_prior_non_null_for_extracted_nulls()
    {
        var prior = D(("phones", "415-555-0142"), ("description", "prior"));
        var (merged, dropped) = AttributeUtils.ApplyCappedAttributes(
            D(("phones", null), ("industry", null)), typeof(Person), prior, AttributeMergeMode.Replace);
        Assert.Equal(D(("phones", "415-555-0142"), ("industry", null)), merged);
        Assert.Empty(dropped);
    }

    private static void WithEnv(string value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(AttributeUtils.MaxLengthEnvironmentVariable);
        Environment.SetEnvironmentVariable(AttributeUtils.MaxLengthEnvironmentVariable, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttributeUtils.MaxLengthEnvironmentVariable, previous);
        }
    }
}
