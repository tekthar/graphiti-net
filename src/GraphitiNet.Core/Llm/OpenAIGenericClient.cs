// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/openai_generic_client.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Prompts;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Llm;

/// <summary>How <see cref="OpenAIGenericClient"/> asks for structured output.</summary>
public enum StructuredOutputMode
{
    /// <summary>Native <c>json_schema</c> response format (constrained decoding where the server supports it).</summary>
    JsonSchema,

    /// <summary><c>json_object</c> response format, with the schema written into the prompt. For servers without
    /// <c>json_schema</c> support, such as DeepSeek.</summary>
    JsonObject,
}

/// <summary>
/// An LLM client for any OpenAI-compatible <c>/chat/completions</c> endpoint: OpenAI, vLLM, llama.cpp, Ollama,
/// DeepSeek, Together and others. Set <see cref="LLMConfig.BaseUrl"/> to the server, for example
/// <c>http://localhost:11434/v1</c> for Ollama.
/// </summary>
public class OpenAIGenericClient : LLMClient
{
    /// <summary>The default model, upstream's <c>DEFAULT_MODEL</c> in <c>openai_generic_client.py</c>.</summary>
    public const string DefaultModel = "gpt-4.1-mini";

    /// <summary>Creates the client.</summary>
    /// <param name="config">The settings. The API key falls back to <c>OPENAI_API_KEY</c>, the base URL to
    /// <c>OPENAI_BASE_URL</c>.</param>
    /// <param name="cache">Must be <see langword="false"/>: caching isn't implemented for this client.</param>
    /// <param name="client">An SDK client to use instead of creating one from <paramref name="config"/>.</param>
    /// <param name="maxTokens">The default output token limit; 16K suits local models.</param>
    /// <param name="structuredOutputMode">How to ask for structured output.</param>
    /// <exception cref="NotSupportedException"><paramref name="cache"/> is <see langword="true"/>.</exception>
    public OpenAIGenericClient(
        LLMConfig? config = null,
        bool cache = false,
        SdkOpenAIClient? client = null,
        int maxTokens = 16384,
        StructuredOutputMode structuredOutputMode = StructuredOutputMode.JsonSchema)
        : base(config ?? new LLMConfig(), cache
            ? throw new NotSupportedException("Caching is not implemented for OpenAI")
            : false)
    {
        MaxTokens = maxTokens;
        StructuredOutputMode = structuredOutputMode;
        Client = client ?? OpenAIWire.CreateClient(Config.ApiKey, Config.BaseUrl);
    }

    /// <summary>The OpenAI SDK client the requests go through.</summary>
    public SdkOpenAIClient Client { get; }

    /// <summary>How structured output is requested.</summary>
    public StructuredOutputMode StructuredOutputMode { get; }

    /// <summary>
    /// The <c>response_format</c> to send: native <c>json_schema</c> when there is a response model and the mode is
    /// <see cref="StructuredOutputMode.JsonSchema"/>, otherwise <c>json_object</c>. The schema is sent without
    /// <c>strict</c>, because strict mode needs a subset of JSON schema that general models don't meet; adherence is
    /// best effort on OpenAI itself, while constrained-decoding servers still enforce it.
    /// </summary>
    protected internal JsonObject BuildResponseFormat(Type? responseModel)
    {
        if (responseModel is null || StructuredOutputMode == StructuredOutputMode.JsonObject)
        {
            return new JsonObject { ["type"] = "json_object" };
        }

        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = ResponseModelSchema.GetName(responseModel),
                ["schema"] = ResponseModelSchema.GetSchema(responseModel),
            },
        };
    }

    /// <inheritdoc/>
    protected override async Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages,
        Type? responseModel,
        int maxTokens,
        ModelSize modelSize,
        string? model,
        CancellationToken cancellationToken)
    {
        var openAIMessages = OpenAIWire.ToOpenAIMessages(messages);
        var resolvedModel = model ?? Model ?? DefaultModel;
        var body = new JsonObject
        {
            ["model"] = resolvedModel,
            ["messages"] = openAIMessages,
        };
        if (Temperature is not null)
        {
            body["temperature"] = Temperature;
        }

        body["max_tokens"] = maxTokens;
        body["response_format"] = BuildResponseFormat(responseModel);

        try
        {
            var response = await OpenAIWire.CompleteChatAsync(Client, resolvedModel, body, cancellationToken)
                .ConfigureAwait(false);
            var result = OpenAIWire.GetString(OpenAIWire.FirstChoiceMessage(response), "content");
            // An empty body (refusal, length cut-off or a flaky endpoint) would otherwise fail as a cryptic JSON
            // error.
            if (string.IsNullOrEmpty(result))
            {
                throw new EmptyResponseException("LLM returned an empty response");
            }

            var (input, output) = OpenAIWire.Usage(response, "prompt_tokens", "completion_tokens");
            return new LLMClientResponse(ParseJson(StripCodeFences(result)), input, output);
        }
        catch (Exception e) when (IsRateLimit(e))
        {
            throw new RateLimitException(RateLimitException.DefaultMessage, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ProviderLog.GenerationError(Logger, e);
            throw;
        }
    }

    /// <summary>
    /// Generates a response. In <see cref="StructuredOutputMode.JsonObject"/> mode the schema goes into the prompt,
    /// because the API won't enforce it; in <see cref="StructuredOutputMode.JsonSchema"/> mode it doesn't. Transient
    /// errors are retried with backoff (see <see cref="LLMClient.GenerateResponseWithRetryAsync"/>).
    /// </summary>
    public override async Task<JsonElement> GenerateResponseAsync(
        IList<Message> messages,
        Type? responseModel = null,
        int? maxTokens = null,
        ModelSize modelSize = ModelSize.Medium,
        string? groupId = null,
        string? promptName = null,
        bool attributeExtraction = false,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ApplyAttributeExtractionPreamble(messages, attributeExtraction);
        var resolvedMaxTokens = maxTokens ?? MaxTokens;

        if (responseModel is not null && StructuredOutputMode == StructuredOutputMode.JsonObject)
        {
            AppendResponseSchema(messages, responseModel);
        }

        messages[0].Content += GetExtractionLanguageInstruction(groupId);

        using var activity = StartGenerateActivity(modelSize, resolvedMaxTokens, promptName);
        try
        {
            var response = await GenerateResponseWithRetryAsync(
                messages, responseModel, resolvedMaxTokens, modelSize, model, cancellationToken).ConfigureAwait(false);
            TokenTracker.Record(promptName, response.InputTokens, response.OutputTokens);
            return response.Response;
        }
        catch (Exception e)
        {
            RecordError(activity, e);
            throw;
        }
    }
}
