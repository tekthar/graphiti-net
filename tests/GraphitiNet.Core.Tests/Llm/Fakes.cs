// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;
using Microsoft.Extensions.AI;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Tests.Llm;

/// <summary>A request the fake HTTP handler received.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, JsonObject? Body);

/// <summary>An HTTP handler that records requests and answers from a queue, so no network is used.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string>? Headers)> _responses =
        new();
    private readonly Lock _lock = new();

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Answer used when the queue is empty.</summary>
    public (HttpStatusCode Status, string Body)? Fallback { get; set; }

    public FakeHttpHandler Enqueue(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _responses.Enqueue((status, body, headers));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        (HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string>? Headers) response;
        lock (_lock)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!,
                string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text) as JsonObject));
            response = _responses.Count > 0
                ? _responses.Dequeue()
                : Fallback is { } fallback
                    ? (fallback.Status, fallback.Body, null)
                    : throw new InvalidOperationException("No fake response queued.");
        }

        var message = new HttpResponseMessage(response.Status)
        {
            Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return message;
    }
}

internal static class FakeOpenAI
{
    /// <summary>An SDK client that talks to <paramref name="handler"/>, with the SDK's own retries off.</summary>
    public static SdkOpenAIClient Client(FakeHttpHandler handler)
    {
#pragma warning disable CA2000 // The HttpClient lives as long as the SDK client.
        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://fake.openai.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0),
        };
#pragma warning restore CA2000
        return new SdkOpenAIClient(new ApiKeyCredential("test"), options);
    }

    public static string ChatCompletion(string? content, long promptTokens = 0, long completionTokens = 0)
    {
        var json = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion",
            ["created"] = 0,
            ["model"] = "test-model",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens,
            },
        };
        return json.ToJsonString();
    }

    public static string Response(string outputText, long inputTokens = 0, long outputTokens = 0, string? refusal = null)
    {
        var part = refusal is null
            ? new JsonObject { ["type"] = "output_text", ["text"] = outputText, ["annotations"] = new JsonArray() }
            : new JsonObject { ["type"] = "refusal", ["refusal"] = refusal };
        var json = new JsonObject
        {
            ["id"] = "resp-test",
            ["object"] = "response",
            ["status"] = "completed",
            ["output"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = "msg-test",
                    ["role"] = "assistant",
                    ["status"] = "completed",
                    ["content"] = new JsonArray { part },
                },
            },
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens,
            },
        };
        return json.ToJsonString();
    }

    public static string Error(string message, string type = "invalid_request_error") =>
        new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = type } }.ToJsonString();
}

/// <summary>An <see cref="IChatClient"/> that records requests and answers with a function.</summary>
internal sealed class FakeChatClient(Func<IList<ChatMessage>, ChatOptions?, ChatResponse> respond) : IChatClient
{
    private readonly Lock _lock = new();

    public FakeChatClient(params string[] texts)
        : this(QueueResponder(texts))
    {
    }

    public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public string? ProviderName { get; init; } = "fake";

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (_lock)
        {
            Calls.Add((list, options));
            return Task.FromResult(respond(list, options));
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(ProviderName) : null;

    public void Dispose()
    {
    }

    private static Func<IList<ChatMessage>, ChatOptions?, ChatResponse> QueueResponder(string[] texts)
    {
        var queue = new Queue<string>(texts);
        return (_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, queue.Dequeue()));
    }
}

/// <summary>A minimal concrete <see cref="LLMClient"/> for testing the base class.</summary>
internal sealed class MockLLMClient : LLMClient
{
    public MockLLMClient(LLMConfig? config = null)
        : base(config)
    {
        RetryDelayAsync = (delay, _) =>
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        };
    }

    public MockLLMClient(LLMConfig? config, LLMCache cache)
        : base(config, cache)
    {
    }

    public List<TimeSpan> Delays { get; } = [];

    private readonly Queue<Func<LLMClientResponse>> _responses = new();

    public List<List<Message>> Calls { get; } = [];

    public MockLLMClient Enqueue(string json, long inputTokens = 0, long outputTokens = 0)
    {
        _responses.Enqueue(() => new LLMClientResponse(ParseJson(json), inputTokens, outputTokens));
        return this;
    }

    public MockLLMClient EnqueueError(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize, string? model,
        CancellationToken cancellationToken)
    {
        Calls.Add(messages.Select(m => new Message(m.Role, m.Content)).ToList());
        return Task.FromResult(_responses.Dequeue()());
    }
}
