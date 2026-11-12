// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Prompts;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Llm;

/// <summary>
/// Helpers shared by the OpenAI clients. Requests are built as JSON and sent through the official SDK's protocol
/// methods, so the wire format matches what upstream's Python SDK sends, field for field, while the SDK still
/// handles authentication, endpoints and transport retries.
/// </summary>
internal static class OpenAIWire
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string BaseUrlEnvironmentVariable = "OPENAI_BASE_URL";

    /// <summary>Creates an SDK client the way <c>AsyncOpenAI(api_key=..., base_url=...)</c> does, falling back to
    /// <c>OPENAI_API_KEY</c> and <c>OPENAI_BASE_URL</c>.</summary>
    public static SdkOpenAIClient CreateClient(string? apiKey, string? baseUrl)
    {
        var key = string.IsNullOrEmpty(apiKey) ? Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) : apiKey;
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException(
                "The api_key client option must be set either by passing an API key to the client or by setting the "
                + "OPENAI_API_KEY environment variable");
        }

        var url = string.IsNullOrEmpty(baseUrl) ? Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable) : baseUrl;
        var options = new OpenAI.OpenAIClientOptions();
        if (!string.IsNullOrEmpty(url))
        {
            options.Endpoint = new Uri(url);
        }

        return new SdkOpenAIClient(new ApiKeyCredential(key), options);
    }

    /// <summary>Converts messages to chat format, cleaning each. Like upstream, only user and system messages are
    /// sent.</summary>
    public static JsonArray ToOpenAIMessages(IList<Message> messages)
    {
        var result = new JsonArray();
        foreach (var m in messages)
        {
            m.Content = LLMClient.CleanInput(m.Content);
            if (m.Role is "user" or "system")
            {
                result.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }

        return result;
    }

    /// <summary>POSTs a body to <c>/chat/completions</c>.</summary>
    public static Task<JsonElement> CompleteChatAsync(
        SdkOpenAIClient client, string model, JsonObject body, CancellationToken cancellationToken) =>
        SendAsync((content, options) => client.GetChatClient(model).CompleteChatAsync(content, options), body,
            cancellationToken);

    /// <summary>POSTs a body to <c>/responses</c>.</summary>
    public static Task<JsonElement> CreateResponseAsync(
        SdkOpenAIClient client, JsonObject body, CancellationToken cancellationToken) =>
#pragma warning disable OPENAI001 // The Responses client is marked experimental in the SDK; only its protocol method is used.
        SendAsync((content, options) => client.GetResponsesClient().CreateResponseAsync(content, options), body,
            cancellationToken);
#pragma warning restore OPENAI001

    private static async Task<JsonElement> SendAsync(
        Func<BinaryContent, RequestOptions, Task<ClientResult>> send, JsonObject body,
        CancellationToken cancellationToken)
    {
        using var content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
        var result = await send(content, new RequestOptions { CancellationToken = cancellationToken })
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.GetRawResponse().Content);
        return document.RootElement.Clone();
    }

    /// <summary>The Responses API's <c>output_text</c>: the text of every <c>output_text</c> part, joined.</summary>
    public static string OutputText(JsonElement response)
    {
        var sb = new StringBuilder();
        foreach (var part in ResponseContentParts(response))
        {
            if (part.TryGetProperty("type", out var type) && type.ValueEquals("output_text")
                && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }

        return sb.ToString();
    }

    /// <summary>The refusal text in a Responses API response, if the model refused.</summary>
    public static string? Refusal(JsonElement response)
    {
        foreach (var part in ResponseContentParts(response))
        {
            if (part.TryGetProperty("type", out var type) && type.ValueEquals("refusal")
                && part.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
            {
                return refusal.GetString();
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> ResponseContentParts(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.ValueEquals("message")
                && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    yield return part;
                }
            }
        }
    }

    /// <summary>The first choice's message in a chat completion.</summary>
    public static JsonElement? FirstChoiceMessage(JsonElement completion) =>
        completion.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
        && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var message)
            ? message
            : null;

    /// <summary>A string property, or <see langword="null"/> if it is missing or not a string.</summary>
    public static string? GetString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads <c>usage.{inputName}</c> and <c>usage.{outputName}</c>, defaulting to 0.</summary>
    public static (long Input, long Output) Usage(JsonElement response, string inputName, string outputName)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        return (ReadLong(usage, inputName), ReadLong(usage, outputName));
    }

    private static long ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var n)
            ? n
            : 0;
}
