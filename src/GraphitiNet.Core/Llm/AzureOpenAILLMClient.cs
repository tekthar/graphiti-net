// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/azure_openai_client.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.Json;
using System.Text.Json.Nodes;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Llm;

/// <summary>
/// An LLM client for Azure OpenAI. Pass an <c>AzureOpenAIClient</c> (from the Azure.AI.OpenAI package, which derives
/// from the SDK's <see cref="SdkOpenAIClient"/>) or an <see cref="SdkOpenAIClient"/> pointed at Azure's v1 endpoint.
/// Model names are deployment names.
/// </summary>
public class AzureOpenAILLMClient : BaseOpenAIClient
{
    /// <summary>Creates the client.</summary>
    /// <param name="azureClient">The SDK client for the Azure resource.</param>
    /// <param name="config">The settings: deployment names, temperature.</param>
    /// <param name="maxTokens">The default output token limit.</param>
    /// <param name="reasoning">The reasoning effort for reasoning models, or <see langword="null"/>.</param>
    /// <param name="verbosity">The verbosity for reasoning models, or <see langword="null"/>.</param>
    public AzureOpenAILLMClient(
        SdkOpenAIClient azureClient,
        LLMConfig? config = null,
        int maxTokens = LLMConfig.DefaultMaxTokens,
        string? reasoning = null,
        string? verbosity = null)
        : base(config, cache: false, maxTokens, reasoning, verbosity)
    {
        ArgumentNullException.ThrowIfNull(azureClient);
        Client = azureClient;
    }

    /// <summary>The SDK client the requests go through.</summary>
    public SdkOpenAIClient Client { get; }

    /// <summary>
    /// Creates a structured completion: the Responses API for reasoning models, and chat completions with a strict
    /// <c>json_schema</c> response format for other models, because Azure's v1 endpoint doesn't fully support the
    /// Responses API for them.
    /// </summary>
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
        if (SupportsReasoningFeatures(model))
        {
            var text = new JsonObject { ["format"] = StrictJsonSchemaFormat(responseModel) };
            var body = new JsonObject
            {
                ["model"] = model,
                ["input"] = messages.DeepClone(),
                ["max_output_tokens"] = maxTokens,
                ["text"] = text,
            };

            var effort = ResolveReasoningEffort(model, reasoning);
            if (!string.IsNullOrEmpty(effort))
            {
                body["reasoning"] = new JsonObject { ["effort"] = effort };
            }

            if (!string.IsNullOrEmpty(verbosity))
            {
                text["verbosity"] = verbosity;
            }

            return OpenAIWire.CreateResponseAsync(Client, body, cancellationToken);
        }

        var format = StrictJsonSchemaFormat(responseModel);
        var chatBody = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["max_tokens"] = maxTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = format["name"]!.DeepClone(),
                    ["schema"] = format["schema"]!.DeepClone(),
                    ["strict"] = true,
                },
            },
        };
        if (temperature is not null)
        {
            chatBody["temperature"] = temperature;
        }

        return OpenAIWire.CompleteChatAsync(Client, model, chatBody, cancellationToken);
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
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            [ChatTokenLimitName(model)] = maxTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };

        if (!SupportsReasoningFeatures(model) && temperature is not null)
        {
            body["temperature"] = temperature;
        }

        return OpenAIWire.CompleteChatAsync(Client, model, body, cancellationToken);
    }

    /// <summary>Parses either a chat completion (non-reasoning models) or a Responses API response.</summary>
    protected override LLMClientResponse HandleStructuredResponse(JsonElement response)
    {
        if (OpenAIWire.FirstChoiceMessage(response) is { } message)
        {
            var (input, output) = OpenAIWire.Usage(response, "prompt_tokens", "completion_tokens");
            if (OpenAIWire.GetString(message, "content") is { Length: > 0 } content)
            {
                return new LLMClientResponse(ParseJson(content), input, output);
            }

            if (OpenAIWire.GetString(message, "refusal") is { Length: > 0 } refusal)
            {
                throw new RefusalException(refusal);
            }

            throw new GraphitiException($"Invalid response from LLM: {response.GetRawText()}");
        }

        return base.HandleStructuredResponse(response);
    }

    /// <summary>Whether the model takes reasoning and verbosity options.</summary>
    protected static bool SupportsReasoningFeatures(string model) => IsReasoningModel(model);
}
