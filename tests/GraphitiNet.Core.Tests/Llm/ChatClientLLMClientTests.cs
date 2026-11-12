// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ClientModel;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;
using Microsoft.Extensions.AI;

namespace GraphitiNet.Tests.Llm;

public class ChatClientLLMClientTests
{
    private static List<Message> Messages() => [new("system", "sys"), new("assistant", "earlier"), new("user", "hi")];

    private static ChatClientLLMClient Create(FakeChatClient chat, LLMConfig? config = null,
        bool useJsonSchemaResponseFormat = true, ChatOptions? defaultOptions = null)
    {
        var client = new ChatClientLLMClient(chat, config, useJsonSchemaResponseFormat: useJsonSchemaResponseFormat,
            defaultOptions: defaultOptions);
        client.RetryDelayAsync = (_, _) => Task.CompletedTask;
        return client;
    }

    [Fact]
    public async Task SendsMessagesOptionsAndSchema()
    {
        var chat = new FakeChatClient("""{"foo": "bar"}""");
        var client = Create(chat, new LLMConfig(model: "big", smallModel: "small", temperature: 0.2f));

        var result = await client.GenerateResponseAsync<ResponseModel>(Messages(), maxTokens: 99);

        Assert.Equal("bar", result.Foo);
        var (messages, options) = Assert.Single(chat.Calls);
        Assert.Equal([ChatRole.System, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.EndsWith(LLMClient.DefaultExtractionLanguageInstruction(), messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Respond with a JSON object in the following format", messages[2].Text, StringComparison.Ordinal);
        Assert.Equal("big", options!.ModelId);
        Assert.Equal(0.2f, options.Temperature);
        Assert.Equal(99, options.MaxOutputTokens);
        var format = Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.Equal("ResponseModel", format.SchemaName);
        Assert.NotNull(format.Schema);
    }

    [Fact]
    public async Task SmallModelAndOverrides()
    {
        var chat = new FakeChatClient("{}", "{}", "{}");
        var client = Create(chat, new LLMConfig(model: "big", smallModel: "small", temperature: null));

        await client.GenerateResponseAsync(Messages(), modelSize: ModelSize.Small);
        await client.GenerateResponseAsync(Messages(), model: "override");
        await new ChatClientLLMClient(chat).GenerateResponseAsync(Messages());

        Assert.Equal("small", chat.Calls[0].Options!.ModelId);
        Assert.Null(chat.Calls[0].Options!.Temperature);
        Assert.Equal(ChatResponseFormat.Json, chat.Calls[0].Options!.ResponseFormat);
        Assert.Equal("override", chat.Calls[1].Options!.ModelId);
        // No model configured: the chat client's own default applies.
        Assert.Null(chat.Calls[2].Options!.ModelId);
    }

    [Fact]
    public async Task DefaultOptionsAreClonedPerRequest()
    {
        var chat = new FakeChatClient("{}");
        var defaults = new ChatOptions { ModelId = "from-options", AdditionalProperties = new() { ["k"] = "v" } };
        var client = Create(chat, new LLMConfig(temperature: null), defaultOptions: defaults);

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel));

        var options = chat.Calls[0].Options!;
        Assert.NotSame(defaults, options);
        Assert.Equal("from-options", options.ModelId);
        Assert.Equal("v", options.AdditionalProperties!["k"]);
        Assert.Null(defaults.ResponseFormat);
    }

    [Fact]
    public async Task JsonSchemaResponseFormatCanBeTurnedOff()
    {
        var chat = new FakeChatClient("{}");
        var client = Create(chat, useJsonSchemaResponseFormat: false);

        await client.GenerateResponseAsync(Messages(), typeof(ResponseModel));

        Assert.Equal(ChatResponseFormat.Json, chat.Calls[0].Options!.ResponseFormat);
    }

    [Fact]
    public async Task TracksTokensAndStripsFences()
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "```json\n{\"foo\": \"x\"}\n```"))
        {
            Usage = new UsageDetails { InputTokenCount = 30, OutputTokenCount = 6 },
        });
        var client = Create(chat);

        var result = await client.GenerateResponseAsync(Messages(), promptName: "p");

        Assert.Equal("x", result.GetProperty("foo").GetString());
        Assert.Equal(new TokenUsage(30, 6), client.TokenTracker.GetTotalUsage());
        Assert.Equal(1, client.TokenTracker.GetUsage()["p"].CallCount);
    }

    [Fact]
    public async Task EmptyAndInvalidResponsesAreRetried()
    {
        var chat = new FakeChatClient("", "not json", """{"foo": "ok"}""");
        var client = Create(chat);

        var result = await client.GenerateResponseAsync(Messages());

        Assert.Equal("ok", result.GetProperty("foo").GetString());
        Assert.Equal(3, chat.Calls.Count);
    }

    [Fact]
    public async Task RateLimitIsTranslatedAndRetried()
    {
        var chat = new FakeChatClient((_, _) =>
            throw new HttpRequestException("slow down", null, System.Net.HttpStatusCode.TooManyRequests));
        var client = Create(chat);

        var error = await Assert.ThrowsAsync<RateLimitException>(() => client.GenerateResponseAsync(Messages()));
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(LLMClient.RetryAttempts, chat.Calls.Count);
    }

    [Fact]
    public async Task ClientErrorsAreNotRetried()
    {
        var chat = new FakeChatClient((_, _) => throw new ClientResultException("bad request", null));
        var client = Create(chat);

        await Assert.ThrowsAsync<ClientResultException>(() => client.GenerateResponseAsync(Messages()));
        Assert.Single(chat.Calls);
    }
}
