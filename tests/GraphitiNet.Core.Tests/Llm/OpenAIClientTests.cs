// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/llm_client/test_openai_client.py (Graphiti v0.31.2) and changed for .NET. The fake SDK client
// is replaced by a fake HTTP handler, so the tests check the JSON actually sent.

using System.Net;
using System.Text.Json.Nodes;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Llm;

public sealed record DummyResponseModel(string Foo);

public class OpenAIClientTests
{
    private static (OpenAIClient Client, FakeHttpHandler Handler) Create(LLMConfig? config = null)
    {
        var handler = new FakeHttpHandler { Fallback = (HttpStatusCode.OK, FakeOpenAI.Response("{}")) };
        return (new OpenAIClient(config ?? new LLMConfig(), client: FakeOpenAI.Client(handler)), handler);
    }

    private static List<Message> Messages() => [new("system", "system message"), new("user", "user message")];

    [Fact]
    public void DefaultModelAndReasoningSentinel()
    {
        Assert.Equal("gpt-6-luna", BaseOpenAIClient.DefaultModel);
        Assert.Equal("auto", BaseOpenAIClient.DefaultReasoning);
    }

    [Theory]
    // GPT 5.1+ and later major versions use 'none'.
    [InlineData("gpt-5.1", "auto", "none")]
    [InlineData("gpt-5.4-mini", "auto", "none")]
    [InlineData("gpt-5.5", "auto", "none")]
    [InlineData("gpt-5.5-2026-04-23", "auto", "none")]
    [InlineData("gpt-5.6-luna", "auto", "none")]
    [InlineData("gpt-6-luna", "auto", "none")]
    [InlineData("gpt-6.1-sol", "auto", "none")]
    // The original GPT-5 family and o-series models use 'minimal'.
    [InlineData("gpt-5", "auto", "minimal")]
    [InlineData("gpt-5-mini", "auto", "minimal")]
    [InlineData("gpt-5-nano", "auto", "minimal")]
    [InlineData("gpt-5-2025-08-07", "auto", "minimal")]
    [InlineData("o3", "auto", "minimal")]
    // Non-reasoning model: the value is ignored by the caller.
    [InlineData("gpt-4.1", "auto", "minimal")]
    // Explicit values always pass through unchanged.
    [InlineData("gpt-5.5", "high", "high")]
    [InlineData("gpt-5", "low", "low")]
    [InlineData("gpt-5.5", null, null)]
    public void ResolveReasoningEffort(string model, string? reasoning, string? expected) =>
        Assert.Equal(expected, BaseOpenAIClient.ResolveReasoningEffort(model, reasoning));

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-nano", true)]
    [InlineData("gpt-5.5", true)]
    [InlineData("gpt-6-luna", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("o1", true)]
    [InlineData("o3-mini", true)]
    [InlineData("gpt-4.1-mini", false)]
    [InlineData("gpt-4o-mini", false)]
    public void IsReasoningModel(string model, bool expected) =>
        Assert.Equal(expected, BaseOpenAIClient.IsReasoningModel(model));

    [Fact]
    public void DefaultModelForSize_IsGpt6Luna()
    {
        var (client, _) = Create();
        Assert.Equal("gpt-6-luna", client.GetModelForSize(ModelSize.Medium));
        Assert.Equal("gpt-6-luna", client.GetModelForSize(ModelSize.Small));
        Assert.Equal("override", client.GetModelForSize(ModelSize.Small, "override"));
    }

    [Fact]
    public void ConfiguredModelsAreUsed()
    {
        var (client, _) = Create(new LLMConfig(model: "big", smallModel: "small"));
        Assert.Equal("big", client.GetModelForSize(ModelSize.Medium));
        Assert.Equal("small", client.GetModelForSize(ModelSize.Small));
    }

    [Fact]
    public void DefaultRerankerModel_IsGpt41Mini() => Assert.Equal("gpt-4.1-mini", OpenAIRerankerClient.DefaultModel);

    [Fact]
    public void CacheIsNotSupported() =>
        Assert.Throws<NotSupportedException>(() => new OpenAIClient(cache: true, client: FakeOpenAI.Client(new FakeHttpHandler())));

    [Fact]
    public async Task Gpt55StructuredCompletion_SendsNoneEffortAndNoTemperature()
    {
        var (client, handler) = Create();

        await client.CreateStructuredCompletionAsync(
            "gpt-5.5", [], 0.5f, 64, typeof(DummyResponseModel), BaseOpenAIClient.DefaultReasoning, "low");

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/responses", request.Uri.AbsolutePath, StringComparison.Ordinal);
        var body = request.Body!;
        Assert.Equal("gpt-5.5", (string?)body["model"]);
        Assert.Equal(64, (int)body["max_output_tokens"]!);
        // Reasoning models do not accept temperature.
        Assert.False(body.ContainsKey("temperature"));
        // 'auto' resolves to 'none' for gpt-5.5.
        Assert.Equal("none", (string?)body["reasoning"]!["effort"]);
        Assert.Equal("low", (string?)body["text"]!["verbosity"]);
        Assert.False(body.ContainsKey("prompt_cache_options"));

        var format = body["text"]!["format"]!;
        Assert.Equal("json_schema", (string?)format["type"]);
        Assert.Equal("DummyResponseModel", (string?)format["name"]);
        Assert.True((bool)format["strict"]!);
        Assert.False((bool)format["schema"]!["additionalProperties"]!);
        Assert.Equal("foo", (string?)format["schema"]!["required"]![0]);
    }

    [Theory]
    [InlineData("gpt-6-luna", "none")]
    [InlineData("gpt-5.5", "none")]
    [InlineData("gpt-4.1-mini", null)]
    public async Task StructuredCompletion_UsesReasoningFieldsForModel(string model, string? expectedEffort)
    {
        var (client, handler) = Create();

        await client.CreateStructuredCompletionAsync(model, [], 0.5f, 64, typeof(DummyResponseModel), "auto", null);

        var body = handler.Requests[0].Body!;
        if (expectedEffort is null)
        {
            Assert.False(body.ContainsKey("reasoning"));
            Assert.Equal(0.5, (double)body["temperature"]!);
            Assert.False(body.ContainsKey("prompt_cache_options"));
        }
        else
        {
            Assert.Equal(expectedEffort, (string?)body["reasoning"]!["effort"]);
            Assert.False(body.ContainsKey("temperature"));
            if (model == "gpt-6-luna")
            {
                Assert.Equal("explicit", (string?)body["prompt_cache_options"]!["mode"]);
            }
            else
            {
                Assert.False(body.ContainsKey("prompt_cache_options"));
            }
        }
    }

    [Fact]
    public async Task ExplicitReasoningOverridesAuto()
    {
        var (client, handler) = Create();

        await client.CreateStructuredCompletionAsync("gpt-5.5", [], null, 64, typeof(DummyResponseModel), "high", "low");

        Assert.Equal("high", (string?)handler.Requests[0].Body!["reasoning"]!["effort"]);
    }

    [Fact]
    public async Task NonReasoningModel_OmitsReasoningAndKeepsTemperature()
    {
        var (client, handler) = Create();

        await client.CreateStructuredCompletionAsync(
            "gpt-4.1", [], 0.4f, 64, typeof(DummyResponseModel), BaseOpenAIClient.DefaultReasoning, "low");

        var body = handler.Requests[0].Body!;
        Assert.Equal(0.4, (double)body["temperature"]!, 5);
        Assert.False(body.ContainsKey("reasoning"));
        Assert.False(body["text"]!.AsObject().ContainsKey("verbosity"));
    }

    [Fact]
    public async Task EmptyStringReasoning_IsNotSent()
    {
        var (client, handler) = Create();

        await client.CreateStructuredCompletionAsync("gpt-5.5", [], null, 64, typeof(DummyResponseModel), "", "low");

        Assert.False(handler.Requests[0].Body!.ContainsKey("reasoning"));
    }

    [Fact]
    public async Task OriginalGpt5Family_DefaultsToMinimal()
    {
        var (client, handler) = Create();

        await client.CreateStructuredCompletionAsync(
            "gpt-5", [], 0.5f, 64, typeof(DummyResponseModel), BaseOpenAIClient.DefaultReasoning, "low");

        var body = handler.Requests[0].Body!;
        Assert.Equal("minimal", (string?)body["reasoning"]!["effort"]);
        Assert.False(body.ContainsKey("temperature"));
    }

    // Deviation from upstream: upstream sends max_tokens to every model but gpt-6, which the API rejects for the
    // GPT-5 family and o-series ("Use 'max_completion_tokens' instead"). The port uses max_completion_tokens for
    // every reasoning model.
    [Theory]
    [InlineData("gpt-6-luna", "max_completion_tokens", true)]
    [InlineData("gpt-5.5", "max_completion_tokens", false)]
    [InlineData("gpt-4.1-mini", "max_tokens", false)]
    public async Task CreateCompletion_ModelSpecificOptions(string model, string expectedTokenLimit, bool cacheOptions)
    {
        var handler = new FakeHttpHandler { Fallback = (HttpStatusCode.OK, FakeOpenAI.ChatCompletion("{}")) };
        var client = new OpenAIClient(new LLMConfig(), client: FakeOpenAI.Client(handler));

        await client.CreateCompletionAsync(model, [], 0.5f, 64);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/chat/completions", request.Uri.AbsolutePath, StringComparison.Ordinal);
        var body = request.Body!;
        if (model == "gpt-4.1-mini")
        {
            Assert.Equal(0.5, (double)body["temperature"]!);
        }
        else
        {
            Assert.False(body.ContainsKey("temperature"));
        }

        Assert.Equal("json_object", (string?)body["response_format"]!["type"]);
        Assert.Equal(64, (int)body[expectedTokenLimit]!);
        var other = expectedTokenLimit == "max_completion_tokens" ? "max_tokens" : "max_completion_tokens";
        Assert.False(body.ContainsKey(other));
        Assert.Equal(cacheOptions, body.ContainsKey("prompt_cache_options"));
    }

    [Fact]
    public async Task CreateCompletion_KeepsTemperatureForNonReasoningModel()
    {
        var handler = new FakeHttpHandler { Fallback = (HttpStatusCode.OK, FakeOpenAI.ChatCompletion("{}")) };
        var client = new OpenAIClient(new LLMConfig(), client: FakeOpenAI.Client(handler));

        await client.CreateCompletionAsync("gpt-4.1", [], 0.4f, 64);

        Assert.Equal(0.4, (double)handler.Requests[0].Body!["temperature"]!, 5);
    }

    [Fact]
    public async Task GenerateResponse_StructuredParsesOutputAndTracksTokens()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Response("""{"foo": "bar"}""", inputTokens: 120, outputTokens: 8));
        var messages = Messages();

        var result = await client.GenerateResponseAsync<DummyResponseModel>(messages, promptName: "test.prompt");

        Assert.Equal("bar", result.Foo);
        var body = handler.Requests[0].Body!;
        Assert.Equal("gpt-5-mini", (string?)body["model"]);
        Assert.Equal(LLMConfig.DefaultMaxTokens, (int)body["max_output_tokens"]!);
        var input = body["input"]!.AsArray();
        Assert.Equal("system", (string?)input[0]!["role"]);
        Assert.EndsWith(LLMClient.DefaultExtractionLanguageInstruction(), (string?)input[0]!["content"], StringComparison.Ordinal);
        // Native structured output: the schema isn't written into the prompt.
        Assert.Equal("user message", (string?)input[1]!["content"]);

        var usage = client.TokenTracker.GetUsage()["test.prompt"];
        Assert.Equal(1, usage.CallCount);
        Assert.Equal(120, usage.TotalInputTokens);
        Assert.Equal(8, usage.TotalOutputTokens);
    }

    [Fact]
    public async Task GenerateResponse_WithoutResponseModelUsesJsonMode()
    {
        var handler = new FakeHttpHandler().Enqueue(FakeOpenAI.ChatCompletion("""{"any": "thing"}""", 10, 2));
        var client = new OpenAIClient(new LLMConfig(model: "gpt-4.1-mini"), client: FakeOpenAI.Client(handler));

        var result = await client.GenerateResponseAsync(Messages());

        Assert.Equal("thing", result.GetProperty("any").GetString());
        Assert.Equal(new TokenUsage(10, 2), client.TokenTracker.GetTotalUsage());
        Assert.Equal(1, (double)handler.Requests[0].Body!["temperature"]!);
    }

    [Fact]
    public async Task GenerateResponse_AsksAgainWithErrorContextAfterInvalidJson()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Response("not json"));
        handler.Enqueue(FakeOpenAI.Response("""{"foo": "fixed"}"""));
        var messages = Messages();

        var result = await client.GenerateResponseAsync(messages, typeof(DummyResponseModel));

        Assert.Equal("fixed", result.GetProperty("foo").GetString());
        Assert.Equal(2, handler.Requests.Count);
        var retryInput = handler.Requests[1].Body!["input"]!.AsArray();
        Assert.Equal(3, retryInput.Count);
        var errorContext = (string?)retryInput[2]!["content"];
        Assert.StartsWith("The previous response attempt was invalid. Error type: ", errorContext, StringComparison.Ordinal);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task GenerateResponse_GivesUpAfterMaxRetries()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Fallback = (HttpStatusCode.OK, FakeOpenAI.Response("still not json"));

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel)));
        Assert.Equal(BaseOpenAIClient.MaxRetries + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task GenerateResponse_RateLimitIsTranslatedAndNotRetried()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Error("slow down", "rate_limit_error"), HttpStatusCode.TooManyRequests);

        await Assert.ThrowsAsync<RateLimitException>(() =>
            client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateResponse_RefusalIsNotRetried()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Response("", refusal: "I can't help with that."));

        var error = await Assert.ThrowsAsync<RefusalException>(() =>
            client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel)));
        Assert.Equal("I can't help with that.", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateResponse_ServerErrorIsLeftToTheSdk()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Error("boom", "server_error"), HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(() =>
            client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateResponse_AttributeExtractionAddsPreamble()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Response("""{"foo": "x"}"""));

        await client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel), attributeExtraction: true);

        var system = (string?)handler.Requests[0].Body!["input"]![0]!["content"];
        Assert.Contains("ATTRIBUTE EXTRACTION:", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssistantMessagesAreNotSent()
    {
        var (client, handler) = Create(new LLMConfig(model: "gpt-5-mini"));
        handler.Enqueue(FakeOpenAI.Response("""{"foo": "x"}"""));

        await client.GenerateResponseAsync(
            [new("system", "s"), new("assistant", "a"), new("user", "u")], typeof(DummyResponseModel));

        var input = handler.Requests[0].Body!["input"]!.AsArray();
        Assert.Equal(["system", "user"], input.Select(m => (string?)m!["role"]));
    }

    [Fact]
    public void ExplicitApiKeyAndBaseUrlAreAccepted()
    {
        // An explicit key is always accepted; creating a client makes no request.
        var client = new OpenAIClient(new LLMConfig(apiKey: "sk-test", baseUrl: "http://localhost:11434/v1"));
        Assert.NotNull(client.Client);
    }

    [Fact]
    public void JsonResponse_EmptyContentReadsAsEmptyObject()
    {
        var client = new TestableOpenAIClient();
        var response = client.HandleJson(System.Text.Json.JsonDocument.Parse(FakeOpenAI.ChatCompletion(null)).RootElement);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, response.Response.ValueKind);
        Assert.Empty(response.Response.EnumerateObject());
    }

    private sealed class TestableOpenAIClient()
        : OpenAIClient(new LLMConfig(), client: FakeOpenAI.Client(new FakeHttpHandler()))
    {
        public LLMClientResponse HandleJson(System.Text.Json.JsonElement response) => HandleJsonResponse(response);
    }
}
