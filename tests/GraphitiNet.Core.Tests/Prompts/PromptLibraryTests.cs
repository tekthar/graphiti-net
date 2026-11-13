// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/prompts/test_configurable_prompt_library.py, test_prompt_names.py, test_prompt_exports.py,
// test_legacy_prompt_compatibility.py and test_complete_prompt.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Prompts;

public class PromptLibraryTests
{
    private static Dictionary<string, object?> DefaultExtractContext() => new()
    {
        ["episode_content"] = "hi",
        ["previous_episodes"] = new List<object?>(),
        ["custom_extraction_instructions"] = "",
        ["entity_types"] = new List<object?>(),
        ["source_description"] = "test",
    };

    private static ChatPrompt CustomExtractMessage(IReadOnlyDictionary<string, object?> context) =>
        new("custom system", "custom user");

    [Fact]
    public void Create_without_overrides_is_equivalent_to_the_default()
    {
        var library = PromptLibrary.Create();
        var context = DefaultExtractContext();

        var expected = PromptLibrary.Default.Build(PromptNames.ExtractNodesExtractMessage, context);
        var actual = library.Build(PromptNames.ExtractNodesExtractMessage, context);

        Assert.Equal(expected, actual);
        Assert.Equal(expected, ExtractNodesPrompts.ExtractMessage(context));
    }

    [Fact]
    public void Create_applies_a_single_override()
    {
        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = CustomExtractMessage,
        });

        var prompt = library.Build(PromptNames.ExtractNodesExtractMessage, new Dictionary<string, object?>());

        Assert.Equal("custom system", prompt.System);
        Assert.Equal("custom user", prompt.User);
        Assert.True(library.IsOverridden(PromptNames.ExtractNodesExtractMessage));
        Assert.Equal([PromptNames.ExtractNodesExtractMessage], library.OverriddenPrompts);
    }

    [Fact]
    public void Create_preserves_unspecified_defaults()
    {
        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = CustomExtractMessage,
        });
        var context = DefaultExtractContext();

        Assert.Equal(
            ExtractNodesPrompts.ExtractText(context),
            library.Build(PromptNames.ExtractNodesExtractText, context));
        Assert.False(library.IsOverridden(PromptNames.ExtractEdgesEdge));
    }

    [Fact]
    public void With_overrides_layers_on_an_existing_library()
    {
        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = CustomExtractMessage,
        }).WithOverrides(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.SummarizeSagasSummarizeSaga] = context => new("CUSTOM_SAGA_PROMPT", PythonText.Str(context["saga_name"])),
        });

        Assert.Equal("custom system", library.Build(PromptNames.ExtractNodesExtractMessage, DefaultExtractContext()).System);
        var saga = library.Build(PromptNames.SummarizeSagasSummarizeSaga, new Dictionary<string, object?> { ["saga_name"] = "test saga" });
        Assert.Equal("test saga", saga.User);
    }

    [Fact]
    public void An_override_can_wrap_the_default_builder()
    {
        var defaultBuilder = PromptLibrary.GetDefaultBuilder(PromptNames.ExtractNodesExtractMessage);
        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = context => defaultBuilder(context) with { System = "prefix" },
        });

        var prompt = library.Build(PromptNames.ExtractNodesExtractMessage, DefaultExtractContext());

        Assert.Equal("prefix", prompt.System);
        Assert.Equal(ExtractNodesPrompts.ExtractMessage(DefaultExtractContext()).User, prompt.User);
    }

    [Fact]
    public void As_messages_appends_the_unicode_note_for_overrides()
    {
        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = CustomExtractMessage,
        });

        var messages = library.Build(PromptNames.ExtractNodesExtractMessage, new Dictionary<string, object?>()).AsMessages();

        Assert.EndsWith(PromptHelpers.DoNotEscapeUnicode, messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptHelpers.DoNotEscapeUnicode, messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void An_override_returning_null_throws()
    {
        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.ExtractNodesExtractMessage] = _ => null!,
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => library.Build(PromptNames.ExtractNodesExtractMessage, new Dictionary<string, object?>()));
        Assert.Contains("ChatPrompt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_override_group_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            ["nope.extract_message"] = CustomExtractMessage,
        }));
        Assert.Contains("Unknown prompt group", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_override_function_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            ["extract_nodes.nope"] = CustomExtractMessage,
        }));
        Assert.Contains("Unknown prompt function", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_names_without_a_group_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => PromptLibrary.Default.GetBuilder("extract_message"));
        Assert.Contains("expected group.method", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_library_exposes_every_group_and_function()
    {
        foreach (var (group, functions) in PromptLibrary.PromptGroups)
        {
            foreach (var function in functions)
            {
                Assert.NotNull(PromptLibrary.Default.GetBuilder($"{group}.{function}"));
            }
        }
    }

    [Fact]
    public void Prompt_specs_are_fixed()
    {
        var spec = PromptLibrary.BuiltinSpecs[PromptNames.ExtractNodesExtractMessage];

        Assert.Equal(typeof(ExtractedEntities), spec.ResponseModel);
        Assert.False(spec.DynamicSchema);
        Assert.True(PromptLibrary.BuiltinSpecs[PromptNames.ExtractNodesExtractAttributes].DynamicSchema);
    }

    // test_prompt_names.py: the name list, the groups and the specs stay in sync.
    [Fact]
    public void Prompt_names_match_builtin_specs()
    {
        Assert.Equal(PromptNames.All.Order(StringComparer.Ordinal), PromptLibrary.BuiltinSpecs.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Prompt_group_names_match_prompt_groups()
    {
        Assert.Equal(
            PromptGroupNames.All.Order(StringComparer.Ordinal),
            PromptLibrary.PromptGroups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            PromptNames.All,
            PromptGroupNames.All.SelectMany(g => PromptLibrary.PromptGroups[g].Select(f => $"{g}.{f}")));
    }

    // test_complete_prompt.py / lib.resolve_response_model.
    [Fact]
    public void Resolve_response_model_returns_the_fixed_schema()
    {
        Assert.Equal(typeof(ExtractedEntities), PromptLibrary.ResolveResponseModel(PromptNames.ExtractNodesExtractMessage));
        Assert.Equal(
            typeof(ExtractedEntities),
            PromptLibrary.ResolveResponseModel(PromptNames.ExtractNodesExtractMessage, typeof(ExtractedEntities)));
        Assert.Null(PromptLibrary.ResolveResponseModel(PromptNames.DedupeNodesNodeList));
    }

    [Fact]
    public void Resolve_response_model_rejects_a_different_schema()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PromptLibrary.ResolveResponseModel(PromptNames.ExtractNodesExtractMessage, typeof(EdgeDuplicate)));
        Assert.Contains("schema overrides are not allowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_response_model_requires_a_schema_for_dynamic_prompts()
    {
        Assert.Throws<ArgumentException>(() => PromptLibrary.ResolveResponseModel(PromptNames.ExtractEdgesExtractAttributes));
        Assert.Equal(
            typeof(EdgeDuplicate),
            PromptLibrary.ResolveResponseModel(PromptNames.ExtractEdgesExtractAttributes, typeof(EdgeDuplicate)));
    }

    [Fact]
    public void Resolve_response_model_rejects_unknown_names()
    {
        var ex = Assert.Throws<ArgumentException>(() => PromptLibrary.ResolveResponseModel("extract_nodes.nope"));
        Assert.Contains("Unknown prompt_name", ex.Message, StringComparison.Ordinal);
    }

    // test_legacy_prompt_compatibility.py: ChatPrompt shape checks.
    [Theory]
    [InlineData("")]
    [InlineData("system")]
    [InlineData("user,system")]
    [InlineData("assistant,user")]
    [InlineData("system,user,user")]
    public void Chat_prompt_rejects_invalid_message_shapes(string roles)
    {
        var messages = roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Message(r, r)).ToList();

        Assert.Throws<ArgumentException>(() => ChatPrompt.FromMessages(messages));
    }

    [Fact]
    public void Chat_prompt_from_messages_copies_content_without_unicode_note()
    {
        var prompt = ChatPrompt.FromMessages([new Message("system", "system prompt"), new Message("user", "user prompt")]);

        Assert.Equal("system prompt", prompt.System);
        Assert.Equal("user prompt", prompt.User);
        Assert.Equal("system prompt", prompt.AsMessages(appendUnicodeNote: false)[0].Content);
        Assert.Equal("system prompt" + PromptHelpers.DoNotEscapeUnicode, prompt.AsMessages()[0].Content);
        Assert.Equal(["system", "user"], prompt.AsMessages().Select(m => m.Role));
    }

    [Fact]
    public void Missing_context_keys_throw_key_not_found()
    {
        var ex = Assert.Throws<KeyNotFoundException>(
            () => PromptLibrary.Default.Build(PromptNames.ExtractNodesExtractMessage, new Dictionary<string, object?>()));
        Assert.Contains("entity_types", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Saga_prompt_uses_defaults_for_missing_keys()
    {
        var prompt = SummarizeSagasPrompts.SummarizeSaga(new Dictionary<string, object?>());

        Assert.Contains("factual knowledge brief for the topic \"Unknown\"", prompt.User, StringComparison.Ordinal);
        Assert.Contains("<MESSAGES>\n(no messages)\n</MESSAGES>", prompt.User, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTING_KNOWLEDGE", prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_constant_matches_its_text_form()
    {
        Assert.Equal(Snippets.MaxSummaryCharsText, Snippets.MaxSummaryChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
