// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/openai_base_client.py (Graphiti v0.31.2) and changed for .NET.

using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GraphitiNet.Prompts;

namespace GraphitiNet.Llm;

/// <summary>
/// Shared logic of the OpenAI and Azure OpenAI clients: model selection, reasoning-model handling, response parsing
/// and a retry loop that tells the model what was wrong with its previous answer.
/// </summary>
public abstract partial class BaseOpenAIClient : LLMClient
{
    /// <summary>
    /// The default model, upstream's <c>DEFAULT_MODEL</c>. <c>gpt-6-luna</c> may not be available on every OpenAI
    /// account; set <see cref="LLMConfig.Model"/> to a model you can use if it isn't.
    /// </summary>
    public const string DefaultModel = "gpt-6-luna";

    /// <summary>The default small model, upstream's <c>DEFAULT_SMALL_MODEL</c>. Same caveat as
    /// <see cref="DefaultModel"/>.</summary>
    public const string DefaultSmallModel = "gpt-6-luna";

    /// <summary>A sentinel meaning "pick a reasoning effort for the model" (see
    /// <see cref="ResolveReasoningEffort"/>). It is never sent to the API.</summary>
    public const string DefaultReasoning = "auto";

    /// <summary>The default verbosity for reasoning models.</summary>
    public const string DefaultVerbosity = "low";

    /// <summary>How many times the retry loop asks the model again after an invalid answer.</summary>
    public const int MaxRetries = 2;

    /// <summary>Creates the client.</summary>
    /// <param name="config">The settings; <see langword="null"/> means defaults.</param>
    /// <param name="cache">Must be <see langword="false"/>: caching isn't implemented for OpenAI clients.</param>
    /// <param name="maxTokens">The default output token limit. Overrides <see cref="LLMConfig.MaxTokens"/>.</param>
    /// <param name="reasoning">The reasoning effort for reasoning models, <see cref="DefaultReasoning"/> to pick one
    /// per model, or <see langword="null"/> to not send one.</param>
    /// <param name="verbosity">The verbosity for reasoning models, or <see langword="null"/> to not send one.</param>
    /// <exception cref="NotSupportedException"><paramref name="cache"/> is <see langword="true"/>.</exception>
    protected BaseOpenAIClient(
        LLMConfig? config = null,
        bool cache = false,
        int maxTokens = LLMConfig.DefaultMaxTokens,
        string? reasoning = DefaultReasoning,
        string? verbosity = DefaultVerbosity)
        : base(config ?? new LLMConfig(), ThrowIfCache(cache))
    {
        MaxTokens = maxTokens;
        Reasoning = reasoning;
        Verbosity = verbosity;
    }

    /// <summary>The reasoning effort setting.</summary>
    public string? Reasoning { get; }

    /// <summary>The verbosity setting.</summary>
    public string? Verbosity { get; }

    /// <summary>Calls the chat completions API in JSON mode. Returns the raw response.</summary>
    protected internal abstract Task<JsonElement> CreateCompletionAsync(
        string model,
        JsonArray messages,
        float? temperature,
        int maxTokens,
        Type? responseModel = null,
        CancellationToken cancellationToken = default);

    /// <summary>Calls the API with structured output for <paramref name="responseModel"/>. Returns the raw
    /// response.</summary>
    protected internal abstract Task<JsonElement> CreateStructuredCompletionAsync(
        string model,
        JsonArray messages,
        float? temperature,
        int maxTokens,
        Type responseModel,
        string? reasoning,
        string? verbosity,
        CancellationToken cancellationToken = default);

    /// <summary>The model to use: <paramref name="model"/> if given, else the configured model for the size.</summary>
    protected internal string GetModelForSize(ModelSize modelSize, string? model = null)
    {
        if (model is not null)
        {
            return model;
        }

        return modelSize == ModelSize.Small ? SmallModel ?? DefaultSmallModel : Model ?? DefaultModel;
    }

    /// <summary>Whether a model is a reasoning model: o1 and o3, and GPT models from major version 5.</summary>
    public static bool IsReasoningModel(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.StartsWith("o1", StringComparison.Ordinal) || model.StartsWith("o3", StringComparison.Ordinal))
        {
            return true;
        }

        var version = GptVersion().Match(model);
        return version.Success && int.Parse(version.Groups[1].Value, CultureInfo.InvariantCulture) >= 5;
    }

    /// <summary>
    /// The <c>prompt_cache_options</c> to send, or <see langword="null"/>. gpt-6 writes prompts of 1,024+ tokens to
    /// the cache by default at 1.25x the input rate; explicit mode without a breakpoint writes nothing. Other models
    /// reject the field.
    /// </summary>
    public static JsonObject? PromptCacheOptions(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.StartsWith("gpt-6", StringComparison.Ordinal) ? new JsonObject { ["mode"] = "explicit" } : null;
    }

    /// <summary>
    /// The reasoning effort to send. <see cref="DefaultReasoning"/> picks <c>none</c> for GPT 5.1 and later and
    /// <c>minimal</c> otherwise; any other value, including <see langword="null"/>, passes through unchanged.
    /// </summary>
    public static string? ResolveReasoningEffort(string model, string? reasoning)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (reasoning != DefaultReasoning)
        {
            return reasoning;
        }

        var version = GptMajorMinor().Match(model);
        if (version.Success)
        {
            var major = int.Parse(version.Groups[1].Value, CultureInfo.InvariantCulture);
            var minor = version.Groups[2].Success
                ? int.Parse(version.Groups[2].Value, CultureInfo.InvariantCulture)
                : (int?)null;
            if (major > 5 || (major == 5 && minor >= 1))
            {
                return "none";
            }
        }

        return "minimal";
    }

    /// <summary>Parses a structured (Responses API) response.</summary>
    /// <exception cref="RefusalException">The model refused.</exception>
    protected virtual LLMClientResponse HandleStructuredResponse(JsonElement response)
    {
        var text = OpenAIWire.OutputText(response);
        var (input, output) = OpenAIWire.Usage(response, "input_tokens", "output_tokens");
        if (!string.IsNullOrEmpty(text))
        {
            return new LLMClientResponse(ParseJson(text), input, output);
        }

        if (OpenAIWire.Refusal(response) is { Length: > 0 } refusal)
        {
            throw new RefusalException(refusal);
        }

        throw new GraphitiException($"Invalid response from LLM: {response.GetRawText()}");
    }

    /// <summary>Parses a JSON-mode chat completion. An empty answer reads as <c>{}</c>, as upstream.</summary>
    protected virtual LLMClientResponse HandleJsonResponse(JsonElement response)
    {
        var content = OpenAIWire.GetString(OpenAIWire.FirstChoiceMessage(response), "content");
        var (input, output) = OpenAIWire.Usage(response, "prompt_tokens", "completion_tokens");
        return new LLMClientResponse(ParseJson(string.IsNullOrEmpty(content) ? "{}" : content), input, output);
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
        var resolvedModel = GetModelForSize(modelSize, model);
        var limit = maxTokens > 0 ? maxTokens : MaxTokens;

        try
        {
            if (responseModel is not null)
            {
                var response = await CreateStructuredCompletionAsync(
                    resolvedModel, openAIMessages, Temperature, limit, responseModel, Reasoning, Verbosity,
                    cancellationToken).ConfigureAwait(false);
                return HandleStructuredResponse(response);
            }
            else
            {
                var response = await CreateCompletionAsync(
                    resolvedModel, openAIMessages, Temperature, limit, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return HandleJsonResponse(response);
            }
        }
        catch (Exception e) when (IsRateLimit(e))
        {
            throw new RateLimitException(RateLimitException.DefaultMessage, e);
        }
        catch (ClientResultException e) when (e.Status == 401)
        {
            ProviderLog.AuthenticationError(Logger, e);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (IsConnectionError(e))
            {
                ProviderLog.ConnectionError(Logger, e);
            }
            else
            {
                ProviderLog.GenerationError(Logger, e);
            }

            throw;
        }
    }

    /// <summary>
    /// Generates a response. Unlike the base class, OpenAI clients use native structured output instead of putting
    /// the schema in the prompt, and on an invalid answer ask the model again (up to <see cref="MaxRetries"/> times)
    /// with a message describing the error. Rate limits, refusals, and server or connection errors are not retried
    /// here; the SDK retries the latter itself.
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

        messages[0].Content += GetExtractionLanguageInstruction(groupId);

        using var activity = StartGenerateActivity(modelSize, resolvedMaxTokens, promptName);

        var retryCount = 0;
        while (true)
        {
            try
            {
                var response = await GenerateResponseCoreAsync(
                    messages, responseModel, resolvedMaxTokens, modelSize, model, cancellationToken)
                    .ConfigureAwait(false);
                TokenTracker.Record(promptName, response.InputTokens, response.OutputTokens);
                return response.Response;
            }
            catch (Exception e) when (e is RateLimitException or RefusalException or OperationCanceledException
                                      || IsServerOrConnectionError(e))
            {
                RecordError(activity, e);
                throw;
            }
            catch (Exception e)
            {
                if (retryCount >= MaxRetries)
                {
                    ProviderLog.MaxRetriesExceeded(Logger, e, MaxRetries);
                    RecordError(activity, e);
                    throw;
                }

                retryCount++;
                var errorContext =
                    "The previous response attempt was invalid. "
                    + $"Error type: {e.GetType().Name}. "
                    + $"Error details: {e.Message}. "
                    + "Please try again with a valid response, ensuring the output matches "
                    + "the expected format and constraints.";
                messages.Add(new Message("user", errorContext));
                ProviderLog.RetryingAfterApplicationError(Logger, e, retryCount, MaxRetries);
            }
        }
    }

    /// <summary>Builds the <c>text.format</c> for strict structured output, as <c>responses.parse</c> does.</summary>
    protected static JsonObject StrictJsonSchemaFormat(Type responseModel) => new()
    {
        ["type"] = "json_schema",
        ["name"] = ResponseModelSchema.GetName(responseModel),
        ["schema"] = ResponseModelSchema.ToStrictSchema(ResponseModelSchema.GetSchema(responseModel)),
        ["strict"] = true,
    };

    /// <summary>
    /// The name of the output token limit for chat completions. Reasoning models reject <c>max_tokens</c>.
    /// </summary>
    /// <remarks>Deviation from upstream: upstream switches to <c>max_completion_tokens</c> only for gpt-6 models, so
    /// JSON-mode calls to the GPT-5 family and o-series fail there with "Unsupported parameter: 'max_tokens'". The
    /// port uses <c>max_completion_tokens</c> for every reasoning model.</remarks>
    protected static string ChatTokenLimitName(string model) =>
        IsReasoningModel(model) ? "max_completion_tokens" : "max_tokens";

    // Upstream lets OpenAI's client retry APITimeoutError, APIConnectionError and InternalServerError.
    private static bool IsServerOrConnectionError(Exception e) => e switch
    {
        ClientResultException { Status: >= 500 or 0 } => true,
        _ => IsConnectionError(e),
    };

    private static bool IsConnectionError(Exception e) =>
        e is HttpRequestException or TimeoutException || e.InnerException is HttpRequestException;

    private static bool ThrowIfCache(bool cache) => cache
        ? throw new NotSupportedException("Caching is not implemented for OpenAI-based clients")
        : false;

    [GeneratedRegex(@"^gpt-(\d+)")]
    private static partial Regex GptVersion();

    [GeneratedRegex(@"^gpt-(\d+)(?:\.(\d+))?")]
    private static partial Regex GptMajorMinor();
}
