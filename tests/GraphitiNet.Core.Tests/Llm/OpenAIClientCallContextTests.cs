// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Net;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.Llm;

/// <summary>
/// The OpenAI clients override <see cref="LLMClient.GenerateResponseAsync"/>, so they must apply the graphiti-net call
/// context themselves: the model call gate and per-operation token usage (issue #1, items 4 and 5).
/// </summary>
public class OpenAIClientCallContextTests
{
    private static List<Message> Messages() => [new("system", "system message"), new("user", "user message")];

    private static OpenAIClient OpenAI(FakeHttpHandler handler) =>
        new(new LLMConfig(model: "gpt-5-mini"), client: FakeOpenAI.Client(handler));

    private static OpenAIGenericClient Generic(FakeHttpHandler handler) =>
        new(new LLMConfig(apiKey: "test", model: "test-model"), client: FakeOpenAI.Client(handler));

    public static TheoryData<string> Clients => new() { "openai", "generic" };

    private static (LLMClient Client, FakeHttpHandler Handler) Create(string kind)
    {
        var handler = new FakeHttpHandler
        {
            Fallback = kind == "openai"
                ? (HttpStatusCode.OK, FakeOpenAI.Response("""{"foo": "bar"}""", 11, 4))
                : (HttpStatusCode.OK, FakeOpenAI.ChatCompletion("""{"foo": "bar"}""", 11, 4)),
        };
        return (kind == "openai" ? OpenAI(handler) : Generic(handler), handler);
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task A_refusing_gate_stops_the_call_before_any_request(string kind)
    {
        var (client, handler) = Create(kind);
        ModelCallInfo? asked = null;
        var gate = new DelegateModelCallGate((info, _) =>
        {
            asked = info;
            return ValueTask.FromResult(false);
        });
        using var scope = GraphitiCallContext.Begin(new GraphitiCallContext
        {
            Operation = "add_episode",
            GroupId = "g1",
            ModelCallGate = gate,
        });

        await Assert.ThrowsAsync<ModelCallRefusedException>(() =>
            client.GenerateResponseAsync(Messages(), promptName: "extract_nodes.extract_message"));

        Assert.Empty(handler.Requests);
        Assert.NotNull(asked);
        Assert.Equal(ModelCallKind.Llm, asked.Kind);
        Assert.Equal("extract_nodes.extract_message", asked.PromptName);
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task Tokens_reach_the_operations_usage_and_the_prompt_name_is_current(string kind)
    {
        var (client, _) = Create(kind);
        string? promptSeenByGate = null;
        var gate = new DelegateModelCallGate((_, _) =>
        {
            promptSeenByGate = GraphitiCallContext.Current?.PromptName;
            return ValueTask.FromResult(true);
        });
        var context = new GraphitiCallContext { Operation = "add_episode", GroupId = "g1", ModelCallGate = gate };
        using var scope = GraphitiCallContext.Begin(context);

        // A response model sends OpenAIClient through the Responses API, the shape its fake answers with.
        await client.GenerateResponseAsync(Messages(), typeof(Foo), promptName: "extract_edges.edge");

        Assert.Equal("extract_edges.edge", promptSeenByGate);
        var usage = context.Usage.GetTotalUsage();
        Assert.Equal(11, usage.InputTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(1, context.Usage.GetUsage()["extract_edges.edge"].CallCount);
    }

    private sealed record Foo(string Bar);
}
