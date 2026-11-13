// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/openai_client.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.Json;
using System.Text.Json.Nodes;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Llm;

/// <summary>
/// The default LLM client: OpenAI, through the official OpenAI .NET SDK. Structured output uses the Responses API
/// with a strict JSON schema; plain JSON calls use chat completions in JSON mode.
/// </summary>
/// <remarks>The default model is <see cref="BaseOpenAIClient.DefaultModel"/> (<c>gpt-6-luna</c>), which may not be
/// available on your account. Set <see cref="LLMConfig.Model"/> and <see cref="LLMConfig.SmallModel"/> to models you
/// can use.</remarks>
public class OpenAIClient : BaseOpenAIClient
{
    /// <summary>Creates the client.</summary>
    /// <param name="config">The settings. The API key falls back to <c>OPENAI_API_KEY</c>, the base URL to
    /// <c>OPENAI_BASE_URL</c>.</param>
    /// <param name="cache">Must be <see langword="false"/>: caching isn't implemented for OpenAI clients.</param>
    /// <param name="client">An SDK client to use instead of creating one from <paramref name="config"/>.</param>
    /// <param name="maxTokens">The default output token limit.</param>
    /// <param name="reasoning">The reasoning effort for reasoning models; <see cref="BaseOpenAIClient.DefaultReasoning"/>
    /// picks one per model.</param>
    /// <param name="verbosity">The verbosity for reasoning models.</param>
    public OpenAIClient(
        LLMConfig? config = null,
        bool cache = false,
        SdkOpenAIClient? client = null,
        int maxTokens = LLMConfig.DefaultMaxTokens,
        string? reasoning = DefaultReasoning,
        string? verbosity = DefaultVerbosity)
        : base(config, cache, maxTokens, reasoning, verbosity)
    {
        Client = client ?? OpenAIWire.CreateClient(Config.ApiKey, Config.BaseUrl);
    }

    /// <summary>The OpenAI SDK client the requests go through.</summary>
    public SdkOpenAIClient Client { get; }

    /// <summary>Creates a structured completion with the Responses API, as upstream's <c>responses.parse</c>.
    /// Reasoning models get a reasoning effort and verbosity instead of a temperature.</summary>
    protected internal override Task<JsonElement> CreateStructuredCompletionAsync(
        string model,
        JsonArray messages,
        float? temperature,
        int maxTokens,
        Type responseModel,
        string? reasoning,
        string? verbosity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var isReasoningModel = IsReasoningModel(model);
        var text = new JsonObject { ["format"] = StrictJsonSchemaFormat(responseModel) };
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = messages.DeepClone(),
            ["max_output_tokens"] = maxTokens,
            ["text"] = text,
        };

        if (!isReasoningModel && temperature is not null)
        {
            body["temperature"] = temperature;
        }

        // 'auto' resolves to a per-model effort; an empty string is never sent.
        var effort = ResolveReasoningEffort(model, reasoning);
        if (isReasoningModel && !string.IsNullOrEmpty(effort))
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        if (isReasoningModel && verbosity is not null)
        {
            text["verbosity"] = verbosity;
        }

        if (PromptCacheOptions(model) is { } cacheOptions)
        {
            body["prompt_cache_options"] = cacheOptions;
        }

        return OpenAIWire.CreateResponseAsync(Client, body, cancellationToken);
    }

    /// <summary>Creates a chat completion in JSON mode. Reasoning models get no temperature and use
    /// <c>max_completion_tokens</c>.</summary>
    protected internal override Task<JsonElement> CreateCompletionAsync(
        string model,
        JsonArray messages,
        float? temperature,
        int maxTokens,
        Type? responseModel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var isReasoningModel = IsReasoningModel(model);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            [ChatTokenLimitName(model)] = maxTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };

        if (!isReasoningModel && temperature is not null)
        {
            body["temperature"] = temperature;
        }

        if (PromptCacheOptions(model) is { } cacheOptions)
        {
            body["prompt_cache_options"] = cacheOptions;
        }

        return OpenAIWire.CompleteChatAsync(Client, model, body, cancellationToken);
    }
}
