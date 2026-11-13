// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/llm_client/test_openai_generic_client.py (Graphiti v0.31.2) and changed for .NET.

using System.Net;
using System.Text.Json.Nodes;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Llm;

public sealed record ResponseModel(string Foo);

public class OpenAIGenericClientTests
{
    private static List<Message> Messages() => [new("system", "system message"), new("user", "user message")];

    private static (TestableGenericClient Client, FakeHttpHandler Handler) MakeClient(
        string? content = """{"foo": "bar"}""",
        HttpStatusCode status = HttpStatusCode.OK,
        StructuredOutputMode mode = StructuredOutputMode.JsonSchema)
    {
        var body = status == HttpStatusCode.OK ? FakeOpenAI.ChatCompletion(content, 11, 4) : FakeOpenAI.Error("error");
        var handler = new FakeHttpHandler { Fallback = (status, body) };
        var client = new TestableGenericClient(
            new LLMConfig(apiKey: "test", model: "test-model"), FakeOpenAI.Client(handler), mode);
        client.RetryDelayAsync = (_, _) => Task.CompletedTask;
        return (client, handler);
    }

    [Fact]
    public async Task DefaultsToJsonSchemaResponseFormat()
    {
        var (client, handler) = MakeClient();

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel));

        var responseFormat = handler.Requests[0].Body!["response_format"]!;
        Assert.Equal("json_schema", (string?)responseFormat["type"]);
        Assert.Equal("ResponseModel", (string?)responseFormat["json_schema"]!["name"]);
        Assert.True(JsonNode.DeepEquals(
            ResponseModelSchema.GetSchema(typeof(ResponseModel)), responseFormat["json_schema"]!["schema"]));
        Assert.False(responseFormat["json_schema"]!.AsObject().ContainsKey("strict"));
    }

    [Fact]
    public async Task JsonSchemaMode_DoesNotInjectSchemaIntoPrompt()
    {
        var (client, handler) = MakeClient();

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel));

        var sentUserContent = (string?)handler.Requests[0].Body!["messages"]!.AsArray()[^1]!["content"];
        Assert.DoesNotContain("Respond with a JSON object in the following format", sentUserContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonObjectMode_UsesJsonObjectAndInjectsSchema()
    {
        var (client, handler) = MakeClient(mode: StructuredOutputMode.JsonObject);

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel));

        var body = handler.Requests[0].Body!;
        Assert.Equal("json_object", (string?)body["response_format"]!["type"]);
        // The schema must be injected into the prompt since the API will not enforce it.
        var sentUserContent = (string?)body["messages"]!.AsArray()[^1]!["content"];
        Assert.Contains("Respond with a JSON object in the following format", sentUserContent, StringComparison.Ordinal);
        Assert.Contains(ResponseModelSchema.Serialize(typeof(ResponseModel)), sentUserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoResponseModel_UsesJsonObjectWithoutInjection()
    {
        var (client, handler) = MakeClient(content: """{"any": "thing"}""");

        var result = await client.GenerateResponseAsync(Messages());

        var body = handler.Requests[0].Body!;
        Assert.Equal("json_object", (string?)body["response_format"]!["type"]);
        Assert.DoesNotContain("Respond with a JSON object in the following format",
            (string?)body["messages"]!.AsArray()[^1]!["content"], StringComparison.Ordinal);
        Assert.Equal("thing", result.GetProperty("any").GetString());
    }

    [Fact]
    public async Task SendsModelTemperatureAndMaxTokens()
    {
        var (client, handler) = MakeClient();

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel), maxTokens: 321);

        var body = handler.Requests[0].Body!;
        Assert.Equal("test-model", (string?)body["model"]);
        Assert.Equal(1, (double)body["temperature"]!);
        Assert.Equal(321, (int)body["max_tokens"]!);
    }

    [Fact]
    public async Task RateLimitErrorIsTranslated()
    {
        var (client, _) = MakeClient(status: HttpStatusCode.TooManyRequests);

        // Asserted at the single-call level, as upstream does, to keep the retry wrapper out of it.
        await Assert.ThrowsAsync<RateLimitException>(() =>
            client.CallOnceAsync(Messages(), typeof(ResponseModel)));
    }

    [Fact]
    public async Task RateLimitIsRetriedWithBackoff()
    {
        var (client, handler) = MakeClient(status: HttpStatusCode.TooManyRequests);

        await Assert.ThrowsAsync<RateLimitException>(() => client.GenerateResponseAsync(Messages(), typeof(ResponseModel)));
        Assert.Equal(LLMClient.RetryAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task EmptyContent_RaisesEmptyResponseError()
    {
        var (client, _) = MakeClient(content: "");

        await Assert.ThrowsAsync<EmptyResponseException>(() => client.CallOnceAsync(Messages(), typeof(ResponseModel)));
    }

    [Fact]
    public void EmptyResponseError_IsRetryable() =>
        Assert.True(LLMClient.IsServerOrRetryError(new EmptyResponseException("empty")));

    [Fact]
    public async Task StripsMarkdownCodeFenceBeforeParsing()
    {
        var (client, _) = MakeClient(content: "```json\n{\"foo\": \"bar\"}\n```");

        var result = await client.GenerateResponseAsync(Messages(), typeof(ResponseModel));

        Assert.Equal("bar", result.GetProperty("foo").GetString());
    }

    [Theory]
    [InlineData("{\"a\": 1}", "{\"a\": 1}")]
    [InlineData("```json\n{\"a\": 1}\n```", "{\"a\": 1}")]
    [InlineData("```\n{\"a\": 1}\n```  ", "{\"a\": 1}")]
    [InlineData("  ```JSON {\"a\": 1}```", "{\"a\": 1}")]
    public void StripCodeFences(string input, string expected) =>
        Assert.Equal(expected, LLMClient.StripCodeFences(input));

    [Fact]
    public async Task NonRetryableErrorIsNotRetried()
    {
        var (client, handler) = MakeClient(status: HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(() =>
            client.GenerateResponseAsync(Messages(), typeof(ResponseModel)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerErrorIsRetried()
    {
        var (client, handler) = MakeClient(status: HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(() =>
            client.GenerateResponseAsync(Messages(), typeof(ResponseModel)));
        Assert.Equal(LLMClient.RetryAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task RecordsTokenUsage()
    {
        var (client, _) = MakeClient();

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel), promptName: "dedupe_nodes.nodes");

        var usage = client.TokenTracker.GetUsage()["dedupe_nodes.nodes"];
        Assert.Equal(11, usage.TotalInputTokens);
        Assert.Equal(4, usage.TotalOutputTokens);
    }

    [Fact]
    public void CacheIsNotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            new OpenAIGenericClient(cache: true, client: FakeOpenAI.Client(new FakeHttpHandler())));
}

/// <summary>Exposes one call without retries, like upstream's tests calling <c>_generate_response</c>.</summary>
internal sealed class TestableGenericClient(
    LLMConfig config, OpenAI.OpenAIClient client, StructuredOutputMode mode)
    : OpenAIGenericClient(config, client: client, structuredOutputMode: mode)
{
    public async Task<System.Text.Json.JsonElement> CallOnceAsync(IList<Message> messages, Type? responseModel) =>
        (await GenerateResponseCoreAsync(messages, responseModel, MaxTokens, ModelSize.Medium, null,
            CancellationToken.None)).Response;
}
