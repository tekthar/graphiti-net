// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Prompts;

/// <summary>
/// Every prompt, rendered from several contexts, must match upstream's text byte for byte. The expected text comes
/// from Fixtures/prompt_renders.json, generated from the upstream sources with real Python formatting.
/// </summary>
public class PromptGoldenTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var context in PromptFixtures.ContextNames)
        {
            foreach (var name in PromptNames.All)
            {
                data.Add(context, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Prompt_matches_upstream_text(string contextName, string promptName)
    {
        var (expectedSystem, expectedUser) = PromptFixtures.Render(contextName, promptName);

        var prompt = PromptLibrary.Default.Build(promptName, PromptFixtures.Context(contextName));

        Assert.Equal(expectedSystem, prompt.System);
        Assert.Equal(expectedUser, prompt.User);
    }

    [Fact]
    public void Fixtures_cover_every_prompt_name()
    {
        foreach (var context in PromptFixtures.ContextNames)
        {
            foreach (var name in PromptNames.All)
            {
                _ = PromptFixtures.Render(context, name);
            }
        }
    }

    [Fact]
    public void Prompt_text_has_no_carriage_returns()
    {
        // Raw string literals take the source file's line endings; .gitattributes pins the prompt sources to LF.
        var context = PromptFixtures.Context("minimal");
        foreach (var name in PromptNames.All)
        {
            var prompt = PromptLibrary.Default.Build(name, context);
            Assert.DoesNotContain('\r', prompt.System);
            Assert.DoesNotContain('\r', prompt.User);
        }
    }
}
