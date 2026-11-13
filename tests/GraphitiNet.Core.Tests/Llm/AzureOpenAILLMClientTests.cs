// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Net;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Llm;

public class AzureOpenAILLMClientTests
{
    private static List<Message> Messages() => [new("system", "s"), new("user", "u")];

    [Fact]
    public async Task NonReasoningModel_UsesStrictChatCompletionsSchema()
    {
        var handler = new FakeHttpHandler().Enqueue(FakeOpenAI.ChatCompletion("""{"foo": "bar"}""", 5, 1));
        var client = new AzureOpenAILLMClient(FakeOpenAI.Client(handler), new LLMConfig(model: "gpt-4o"));

        var result = await client.GenerateResponseAsync<DummyResponseModel>(Messages());

        Assert.Equal("bar", result.Foo);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/chat/completions", request.Uri.AbsolutePath, StringComparison.Ordinal);
        var body = request.Body!;
        Assert.Equal("json_schema", (string?)body["response_format"]!["type"]);
        Assert.True((bool)body["response_format"]!["json_schema"]!["strict"]!);
        Assert.Equal(1, (double)body["temperature"]!);
        Assert.True(body.ContainsKey("max_tokens"));
        Assert.Equal(new TokenUsage(5, 1), client.TokenTracker.GetTotalUsage());
    }

    [Fact]
    public async Task ReasoningModel_UsesResponsesApiWithoutDefaultReasoning()
    {
        var handler = new FakeHttpHandler().Enqueue(FakeOpenAI.Response("""{"foo": "bar"}"""));
        var client = new AzureOpenAILLMClient(FakeOpenAI.Client(handler), new LLMConfig(model: "gpt-5-mini"));

        await client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel));

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/responses", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.False(request.Body!.ContainsKey("reasoning"));
        Assert.False(request.Body!.ContainsKey("temperature"));
        Assert.False(request.Body!["text"]!.AsObject().ContainsKey("verbosity"));
    }

    [Fact]
    public async Task ChatRefusalIsReported()
    {
        var refusal = """
            {"choices": [{"index": 0, "message": {"role": "assistant", "content": null, "refusal": "no"},
              "finish_reason": "stop"}], "usage": {"prompt_tokens": 1, "completion_tokens": 1}}
            """;
        var handler = new FakeHttpHandler().Enqueue(refusal);
        var client = new AzureOpenAILLMClient(FakeOpenAI.Client(handler), new LLMConfig(model: "gpt-4o"));

        await Assert.ThrowsAsync<RefusalException>(() => client.GenerateResponseAsync(Messages(), typeof(DummyResponseModel)));
    }

    [Fact]
    public async Task JsonMode_ReasoningModelOmitsTemperature()
    {
        var handler = new FakeHttpHandler { Fallback = (HttpStatusCode.OK, FakeOpenAI.ChatCompletion("{}")) };
        var client = new AzureOpenAILLMClient(FakeOpenAI.Client(handler), new LLMConfig(model: "o3-mini"));

        await client.GenerateResponseAsync(Messages());

        var body = handler.Requests[0].Body!;
        Assert.False(body.ContainsKey("temperature"));
        Assert.True(body.ContainsKey("max_completion_tokens"));
    }
}
