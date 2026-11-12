// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/llm_client/test_client.py (Graphiti v0.31.2) and changed for .NET.

using System.ClientModel;
using System.Text.Json;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Llm;

public sealed record TestPerson(string Name, int Age, string? Nickname = null);

public class LLMClientTests
{
    public static TheoryData<string, string> CleanInputCases => new()
    {
        // Basic text should remain unchanged
        { "Hello World", "Hello World" },
        // Control characters should be removed
        { "Hello\u0000World", "HelloWorld" },
        // Newlines, tabs, returns should be preserved
        { "Hello\nWorld\tTest\r", "Hello\nWorld\tTest\r" },
        // Zero-width characters should be removed
        { "Hello\u200bWorld", "HelloWorld" },
        { "Test\ufeffWord", "TestWord" },
        // Empty string should remain empty
        { "", "" },
        // Form feed and other control characters from the error case
        { "{\"edges\":[{\"relation_typ...\f\u0004Hn\\?\"}]}", "{\"edges\":[{\"relation_typ...Hn\\?\"}]}" },
        // More specific control character tests
        { "Hello\u000cWorld", "HelloWorld" },
        { "Hello\u0004World", "HelloWorld" },
        // Combined JSON-like string with control characters
        { "{\"test\": \"value\f\u0000\u0004\"}", "{\"test\": \"value\"}" },
        // Valid surrogate pairs survive
        { "emoji \ud83d\ude00", "emoji \ud83d\ude00" },
    };

    [Theory]
    [MemberData(nameof(CleanInputCases))]
    public void CleanInput(string input, string expected) =>
        Assert.Equal(expected, LLMClient.CleanInput(input));

    // Lone surrogates can't go through xUnit's theory data, so these upstream cases are facts.
    [Fact]
    public void CleanInput_RemovesInvalidUnicode()
    {
        // Invalid Unicode should be removed
        Assert.Equal("HelloWorld", LLMClient.CleanInput("Hello\udcdeWorld"));
        // Multiple issues combined
        Assert.Equal("Hello\nWorld", LLMClient.CleanInput("Hello\u0000\u200b\nWorld\udcde"));
    }

    [Fact]
    public void AttributeExtractionPreamble_NoOpWhenDisabled()
    {
        var messages = new List<Message> { new("system", "base"), new("user", "hi") };
        LLMClient.ApplyAttributeExtractionPreamble(messages, attributeExtraction: false);
        Assert.Equal("base", messages[0].Content);
        Assert.Equal("hi", messages[1].Content);
    }

    [Fact]
    public void AttributeExtractionPreamble_AppendsToSystem()
    {
        var messages = new List<Message> { new("system", "You are helpful."), new("user", "hi") };
        LLMClient.ApplyAttributeExtractionPreamble(messages, attributeExtraction: true);
        Assert.StartsWith("You are helpful.", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("ATTRIBUTE EXTRACTION:", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("NEVER themselves valid values", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("hi", messages[1].Content);
    }

    [Fact]
    public void AttributeExtractionPreamble_IsIdempotent()
    {
        var messages = new List<Message> { new("system", "You are helpful."), new("user", "hi") };
        LLMClient.ApplyAttributeExtractionPreamble(messages, attributeExtraction: true);
        var once = messages[0].Content;
        LLMClient.ApplyAttributeExtractionPreamble(messages, attributeExtraction: true);
        Assert.Equal(once, messages[0].Content);
    }

    [Fact]
    public void AttributeExtractionPreamble_FallsBackToFirstMessageIfNoSystem()
    {
        var messages = new List<Message> { new("user", "hi") };
        LLMClient.ApplyAttributeExtractionPreamble(messages, attributeExtraction: true);
        Assert.Contains("ATTRIBUTE EXTRACTION:", messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("hi", messages[0].Content, StringComparison.Ordinal);
        // The sentinel must be at the front so the idempotency check finds it.
        Assert.StartsWith("<<graphiti.attr_extraction.preamble.v1>>", messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeExtractionPreamble_HandlesEmptyMessages()
    {
        var messages = new List<Message>();
        LLMClient.ApplyAttributeExtractionPreamble(messages, attributeExtraction: true);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task GenerateResponse_AppendsSchemaAndLanguageInstruction()
    {
        var client = new MockLLMClient().Enqueue("""{"name": "Ada", "age": 36}""");
        var messages = new List<Message> { new("system", "sys"), new("user", "who?") };

        await client.GenerateResponseAsync(messages, typeof(TestPerson));

        var sent = client.Calls[0];
        Assert.Equal("sys" + LLMClient.DefaultExtractionLanguageInstruction(), sent[0].Content);
        Assert.StartsWith("who?\n\nRespond with a JSON object in the following format:\n\n{", sent[1].Content,
            StringComparison.Ordinal);
        Assert.Contains("\"name\": {", sent[1].Content, StringComparison.Ordinal);
        Assert.Contains("\"title\": \"TestPerson\"", sent[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateResponse_Generic_DeserializesSnakeCaseRecord()
    {
        var client = new MockLLMClient().Enqueue("""{"name": "Ada", "age": 36, "nickname": null}""");

        var person = await client.GenerateResponseAsync<TestPerson>([new("system", "s"), new("user", "u")]);

        Assert.Equal(new TestPerson("Ada", 36), person);
    }

    [Fact]
    public async Task GenerateResponse_RecordsTokensPerPromptName()
    {
        var client = new MockLLMClient()
            .Enqueue("{}", inputTokens: 100, outputTokens: 20)
            .Enqueue("{}", inputTokens: 50, outputTokens: 5)
            .Enqueue("{}", inputTokens: 7, outputTokens: 3);

        await client.GenerateResponseAsync([new("system", "s"), new("user", "u")], promptName: "extract_nodes.extract_message");
        await client.GenerateResponseAsync([new("system", "s"), new("user", "u")], promptName: "extract_nodes.extract_message");
        await client.GenerateResponseAsync([new("system", "s"), new("user", "u")]);

        var usage = client.TokenTracker.GetUsage();
        Assert.Equal(2, usage["extract_nodes.extract_message"].CallCount);
        Assert.Equal(150, usage["extract_nodes.extract_message"].TotalInputTokens);
        Assert.Equal(25, usage["extract_nodes.extract_message"].TotalOutputTokens);
        Assert.Equal(1, usage[TokenUsageTracker.UnknownPromptName].CallCount);
        Assert.Equal(new TokenUsage(157, 28), client.TokenTracker.GetTotalUsage());
    }

    [Fact]
    public async Task GenerateResponse_RetriesTransientErrorsWithBackoff()
    {
        var client = new MockLLMClient()
            .EnqueueError(new RateLimitException())
            .EnqueueError(new JsonException("bad json"))
            .EnqueueError(new EmptyResponseException("empty"))
            .Enqueue("""{"ok": true}""");

        var result = await client.GenerateResponseAsync([new("system", "s"), new("user", "u")]);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(4, client.Calls.Count);
        Assert.Equal(3, client.Delays.Count);
        Assert.All(client.Delays, d => Assert.InRange(d.TotalSeconds, 5, 120));
    }

    [Fact]
    public async Task GenerateResponse_GivesUpAfterFourAttempts()
    {
        var client = new MockLLMClient();
        for (var i = 0; i < 4; i++)
        {
            client.EnqueueError(new ClientResultException("server error", null));
        }

        // A ClientResultException without a response has status 0, which is not retryable.
        await Assert.ThrowsAsync<ClientResultException>(() =>
            client.GenerateResponseAsync([new("system", "s"), new("user", "u")]));
        Assert.Single(client.Calls);

        var retrying = new MockLLMClient();
        for (var i = 0; i < 4; i++)
        {
            retrying.EnqueueError(new RateLimitException());
        }

        await Assert.ThrowsAsync<RateLimitException>(() =>
            retrying.GenerateResponseAsync([new("system", "s"), new("user", "u")]));
        Assert.Equal(LLMClient.RetryAttempts, retrying.Calls.Count);
    }

    [Fact]
    public async Task GenerateResponse_DoesNotRetryOtherErrors()
    {
        var client = new MockLLMClient().EnqueueError(new InvalidOperationException("bad response"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateResponseAsync([new("system", "s"), new("user", "u")]));
        Assert.Single(client.Calls);
    }

    [Fact]
    public void IsServerOrRetryError_ClassifiesErrors()
    {
        Assert.True(LLMClient.IsServerOrRetryError(new EmptyResponseException("empty")));
        Assert.True(LLMClient.IsServerOrRetryError(new RateLimitException()));
        Assert.True(LLMClient.IsServerOrRetryError(new JsonException()));
        Assert.True(LLMClient.IsServerOrRetryError(
            new HttpRequestException("boom", null, System.Net.HttpStatusCode.BadGateway)));
        Assert.False(LLMClient.IsServerOrRetryError(
            new HttpRequestException("nope", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.False(LLMClient.IsServerOrRetryError(new RefusalException("no")));
        Assert.False(LLMClient.IsServerOrRetryError(new InvalidOperationException()));
    }

    [Fact]
    public void RetryDelay_FollowsRandomExponentialBounds()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(LLMClient.RetryDelay(1).TotalSeconds, 5, 10);
            Assert.InRange(LLMClient.RetryDelay(2).TotalSeconds, 5, 20);
            Assert.InRange(LLMClient.RetryDelay(3).TotalSeconds, 5, 40);
            Assert.InRange(LLMClient.RetryDelay(10).TotalSeconds, 5, 120);
        }
    }

    [Fact]
    public async Task GenerateResponse_UsesCacheWhenEnabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "graphiti-net-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var cache = new LLMCache(directory);
            var client = new MockLLMClient(new LLMConfig(model: "m"), cache).Enqueue("""{"v": 1}""");

            var first = await client.GenerateResponseAsync([new("system", "s"), new("user", "u")]);
            var second = await client.GenerateResponseAsync([new("system", "s"), new("user", "u")]);

            Assert.Single(client.Calls);
            Assert.Equal(1, first.GetProperty("v").GetInt32());
            Assert.Equal(1, second.GetProperty("v").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CacheKey_MatchesUpstreamFormat()
    {
        // hashlib.md5(b'm:[{"content": "hi", "role": "user"}]').hexdigest()
        var client = new KeyClient(new LLMConfig(model: "m"));
        var key = client.Key([new Message("user", "hi")]);
        Assert.Equal("eb285cfaeb1ed07fe53264b382723200", key);
        Assert.NotEqual(key, client.Key([new Message("user", "hi")], "other-model"));
    }

    [Fact]
    public void FailedGenerationLog_OmitsMessageContent()
    {
        var log = KeyClient.FailedLog([new Message("system", "secret"), new Message("user", "secret")],
            new string('x', 600));
        Assert.DoesNotContain("secret", log, StringComparison.Ordinal);
        Assert.Contains("2 message(s)", log, StringComparison.Ordinal);
        Assert.Contains("['system', 'user']", log, StringComparison.Ordinal);
        Assert.Contains(new string('x', 500) + "...", log, StringComparison.Ordinal);
    }

    private sealed class KeyClient(LLMConfig config) : LLMClient(config)
    {
        public string Key(IList<Message> messages, string? model = null) => GetCacheKey(messages, model);

        public static string FailedLog(IList<Message> messages, string? output) =>
            GetFailedGenerationLog(messages, output);

        protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
            IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize, string? model,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
