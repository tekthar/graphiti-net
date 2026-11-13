// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/client.py (Graphiti v0.31.2) and changed for .NET.

using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GraphitiNet.Prompts;
using GraphitiNet.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet.Llm;

/// <summary>What a provider call returned: the parsed JSON and the tokens it used.</summary>
/// <param name="Response">The parsed JSON response.</param>
/// <param name="InputTokens">Input tokens used, or 0 if the provider didn't say.</param>
/// <param name="OutputTokens">Output tokens used, or 0 if the provider didn't say.</param>
public readonly record struct LLMClientResponse(JsonElement Response, long InputTokens = 0, long OutputTokens = 0);

/// <summary>
/// Base class for LLM clients. It prepares the messages (attribute-extraction framing, the response schema, the
/// language instruction, input cleaning), retries transient failures, traces each call, records token usage and,
/// optionally, caches responses. Subclasses implement <see cref="GenerateResponseCoreAsync"/>.
/// </summary>
public abstract partial class LLMClient
{
    /// <summary>Upstream's <c>DEFAULT_CACHE_DIR</c>.</summary>
    public const string DefaultCacheDir = "./llm_cache";

    /// <summary>Attempts made by <see cref="GenerateResponseWithRetryAsync"/>, including the first.</summary>
    public const int RetryAttempts = 4;

    internal const string AttributeExtractionSentinel = "<<graphiti.attr_extraction.preamble.v1>>";

    private const string ZeroWidthCharacters = "\u200b\u200c\u200d\ufeff\u2060";

    /// <summary>Creates the client.</summary>
    /// <param name="config">The settings; <see langword="null"/> means defaults.</param>
    /// <param name="cache">Whether to cache responses on disk in <see cref="DefaultCacheDir"/>. Off by default.</param>
    protected LLMClient(LLMConfig? config, bool cache = false)
    {
        Config = config ?? new LLMConfig();
        Model = Config.Model;
        SmallModel = Config.SmallModel;
        Temperature = Config.Temperature;
        MaxTokens = Config.MaxTokens;
        CacheEnabled = cache;
        if (cache)
        {
            Cache = new LLMCache(DefaultCacheDir);
        }
    }

    /// <summary>Creates the client with caching on, in a cache of the caller's choosing.</summary>
    /// <param name="config">The settings; <see langword="null"/> means defaults.</param>
    /// <param name="cache">The response cache.</param>
    protected LLMClient(LLMConfig? config, LLMCache cache)
        : this(config, cache: false)
    {
        ArgumentNullException.ThrowIfNull(cache);
        CacheEnabled = true;
        Cache = cache;
    }

    /// <summary>The settings the client was created with.</summary>
    public LLMConfig Config { get; }

    /// <summary>The model for <see cref="ModelSize.Medium"/> prompts.</summary>
    public string? Model { get; protected set; }

    /// <summary>The model for <see cref="ModelSize.Small"/> prompts.</summary>
    public string? SmallModel { get; protected set; }

    /// <summary>The sampling temperature, or <see langword="null"/> to not send one.</summary>
    public float? Temperature { get; protected set; }

    /// <summary>The default maximum number of output tokens.</summary>
    public int MaxTokens { get; protected set; }

    /// <summary>Whether responses are cached.</summary>
    public bool CacheEnabled { get; }

    /// <summary>The response cache, or <see langword="null"/> when caching is off. Upstream: <c>cache_dir</c>.</summary>
    public LLMCache? Cache { get; protected set; }

    /// <summary>Token usage per prompt name. Upstream: <c>token_tracker</c>.</summary>
    public TokenUsageTracker TokenTracker { get; } = new();

    /// <summary>Where the client logs retries and failures.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Waits between retries. Tests replace it so they don't sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> RetryDelayAsync { get; set; } = Task.Delay;

    /// <summary>
    /// The default language instruction appended to the first message. Upstream's
    /// <c>get_extraction_language_instruction</c>; override <see cref="GetExtractionLanguageInstruction"/> to change
    /// it, for example per group.
    /// </summary>
    public static string DefaultExtractionLanguageInstruction(string? groupId = null) =>
        "\n\nAny extracted information should be returned in the same language as it was written in. "
        + "Only output non-English text when the user has written full sentences or phrases in that non-English language. "
        + "Otherwise, output English.";

    /// <summary>
    /// Sends <paramref name="messages"/> to the LLM and returns its JSON response, parsed.
    /// </summary>
    /// <remarks>The client appends instructions to the messages in place, as upstream does.</remarks>
    /// <param name="messages">The prompt. Usually a system message followed by a user message.</param>
    /// <param name="responseModel">The type the response must match. Its JSON schema is sent to the LLM.</param>
    /// <param name="maxTokens">The output token limit; <see langword="null"/> means <see cref="MaxTokens"/>.</param>
    /// <param name="modelSize">Which configured model to use.</param>
    /// <param name="groupId">The graph partition, passed to <see cref="GetExtractionLanguageInstruction"/>.</param>
    /// <param name="promptName">The prompt's name, used for tracing and token tracking.</param>
    /// <param name="attributeExtraction">Whether to add the strict attribute-extraction framing.</param>
    /// <param name="model">A model that overrides <paramref name="modelSize"/> for this call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public virtual async Task<JsonElement> GenerateResponseAsync(
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
        var resolvedMaxTokens = maxTokens ?? MaxTokens;

        // The strict attribute-extraction framing belongs on the system message so it reaches every provider
        // regardless of structured-output mechanism.
        ApplyAttributeExtractionPreamble(messages, attributeExtraction);

        if (responseModel is not null)
        {
            AppendResponseSchema(messages, responseModel);
        }

        messages[0].Content += GetExtractionLanguageInstruction(groupId);

        foreach (var message in messages)
        {
            message.Content = CleanInput(message.Content);
        }

        // graphiti-net addition: hosts see the prompt in the ambient call context.
        using var promptScope = GraphitiCallContext.BeginPrompt(promptName);
        using var activity = StartGenerateActivity(modelSize, resolvedMaxTokens, promptName);
        activity?.SetTag("cache.enabled", CacheEnabled);

        string? cacheKey = null;
        if (CacheEnabled && Cache is not null)
        {
            cacheKey = GetCacheKey(messages, model);
            if (Cache.Get(cacheKey) is { } cached)
            {
                ProviderLog.CacheHit(Logger, cacheKey);
                activity?.SetTag("cache.hit", true);
                return cached;
            }
        }

        activity?.SetTag("cache.hit", false);

        // graphiti-net addition: the operation's model call gate may refuse the call (ModelCallRefusedException).
        await GraphitiCallContext.EnsureAllowedAsync(ModelCallKind.Llm, promptName, cancellationToken)
            .ConfigureAwait(false);

        LLMClientResponse response;
        try
        {
            response = await GenerateResponseWithRetryAsync(
                messages, responseModel, resolvedMaxTokens, modelSize, model, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            RecordError(activity, e);
            throw;
        }

        TokenTracker.Record(promptName, response.InputTokens, response.OutputTokens);
        GraphitiCallContext.Current?.Usage.Record(promptName, response.InputTokens, response.OutputTokens);

        if (cacheKey is not null)
        {
            Cache!.Set(cacheKey, response.Response);
        }

        return response.Response;
    }

    /// <summary>
    /// Sends <paramref name="messages"/> to the LLM with <typeparamref name="T"/> as the response model, and reads
    /// the response into a <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="JsonException">The response doesn't match <typeparamref name="T"/>.</exception>
    public async Task<T> GenerateResponseAsync<T>(
        IList<Message> messages,
        int? maxTokens = null,
        ModelSize modelSize = ModelSize.Medium,
        string? groupId = null,
        string? promptName = null,
        bool attributeExtraction = false,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var element = await GenerateResponseAsync(
            messages, typeof(T), maxTokens, modelSize, groupId, promptName, attributeExtraction, model,
            cancellationToken).ConfigureAwait(false);
        return element.Deserialize<T>(ResponseModelSchema.SerializerOptions)
            ?? throw new JsonException($"The LLM returned null for {typeof(T).Name}.");
    }

    /// <summary>The instruction about output language appended to the first message.</summary>
    /// <param name="groupId">The graph partition, so the instruction can differ per group.</param>
    /// <returns>The instruction, or an empty string for none.</returns>
    protected virtual string GetExtractionLanguageInstruction(string? groupId) =>
        DefaultExtractionLanguageInstruction(groupId);

    /// <summary>Calls the provider once. Implementations parse the response and report token usage.</summary>
    protected abstract Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages,
        Type? responseModel,
        int maxTokens,
        ModelSize modelSize,
        string? model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Calls <see cref="GenerateResponseCoreAsync"/>, retrying rate limits, empty responses, malformed JSON and
    /// server errors up to <see cref="RetryAttempts"/> attempts with random exponential backoff (5 to 120 seconds),
    /// as upstream's tenacity policy does.
    /// </summary>
    protected async Task<LLMClientResponse> GenerateResponseWithRetryAsync(
        IList<Message> messages,
        Type? responseModel,
        int maxTokens,
        ModelSize modelSize,
        string? model,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await GenerateResponseCoreAsync(
                    messages, responseModel, maxTokens, modelSize, model, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < RetryAttempts && IsServerOrRetryError(e)
                                      && !cancellationToken.IsCancellationRequested)
            {
                var delay = RetryDelay(attempt);
                ProviderLog.Retrying(Logger, e, nameof(GenerateResponseCoreAsync), attempt);
                await RetryDelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether an error is worth retrying: rate limits, empty responses, malformed JSON, and HTTP 5xx errors.
    /// Upstream: <c>is_server_or_retry_error</c>.
    /// </summary>
    public static bool IsServerOrRetryError(Exception exception) => exception switch
    {
        RateLimitException or EmptyResponseException or JsonException => true,
        ClientResultException { Status: >= 500 and < 600 } => true,
        HttpRequestException { StatusCode: { } status } => (int)status is >= 500 and < 600,
        _ => false,
    };

    /// <summary>Whether an exception from a provider SDK means the rate limit was hit (HTTP 429).</summary>
    protected static bool IsRateLimit(Exception exception) => exception switch
    {
        ClientResultException { Status: 429 } => true,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
        _ => false,
    };

    /// <summary>
    /// Removes text that breaks LLM APIs: unpaired surrogates (invalid Unicode), zero-width characters and control
    /// characters other than newline, carriage return and tab.
    /// </summary>
    protected internal static string CleanInput(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    sb.Append(c).Append(input[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(c) || ZeroWidthCharacters.Contains(c, StringComparison.Ordinal))
            {
                continue;
            }

            if (c >= 32 || c is '\n' or '\r' or '\t')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// For attribute-extraction calls, adds an instruction to the system message that field descriptions in the
    /// schema describe values and must never be copied into them. Changes <paramref name="messages"/> in place, and
    /// does nothing if the instruction is already there.
    /// </summary>
    protected internal static void ApplyAttributeExtractionPreamble(IList<Message> messages, bool attributeExtraction)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!attributeExtraction || messages.Count == 0)
        {
            return;
        }

        const string note = "\n\n" + AttributeExtractionSentinel + "\n"
            + "ATTRIBUTE EXTRACTION: Field descriptions in the response schema describe "
            + "what a real value LOOKS LIKE — they are NEVER themselves valid values and "
            + "must NEVER be copied into any field. If you have no value for a field, set "
            + "it to null; never explain the absence in the field itself.";
        var target = messages[0];
        if (target.Content.Contains(AttributeExtractionSentinel, StringComparison.Ordinal))
        {
            return;
        }

        if (target.Role == "system")
        {
            target.Content += note;
        }
        else
        {
            target.Content = note.TrimStart() + "\n\n" + target.Content;
        }
    }

    /// <summary>Appends the response model's JSON schema to the last message, as upstream's prompt does.</summary>
    protected static void AppendResponseSchema(IList<Message> messages, Type responseModel)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var serialized = ResponseModelSchema.Serialize(responseModel);
        messages[^1].Content += $"\n\nRespond with a JSON object in the following format:\n\n{serialized}";
    }

    /// <summary>
    /// The cache key for a request: the MD5 of the model name and the messages, written as upstream does so the keys
    /// match.
    /// </summary>
    protected string GetCacheKey(IList<Message> messages, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = new JsonArray(messages
            .Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
            .ToArray());
        var keyString = $"{model ?? Model ?? "None"}:{PythonJson.Dumps(list, sortKeys: true)}";
#pragma warning disable CA5351 // MD5 names cache entries here; it isn't used for security.
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(keyString));
#pragma warning restore CA5351
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The provider name used in traces. The default guesses it from the class name.</summary>
    protected virtual string GetProviderType()
    {
        var name = GetType().Name.ToLowerInvariant();
        return name switch
        {
            _ when name.Contains("openai", StringComparison.Ordinal) => "openai",
            _ when name.Contains("anthropic", StringComparison.Ordinal) => "anthropic",
            _ when name.Contains("gemini", StringComparison.Ordinal) => "gemini",
            _ when name.Contains("groq", StringComparison.Ordinal) => "groq",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Describes a failed generation for logs: message count, roles and the first 500 characters of the output, but
    /// not the messages themselves, which may contain personal data.
    /// </summary>
    protected static string GetFailedGenerationLog(IList<Message> messages, string? output)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var log = new StringBuilder();
        log.Append("Input messages: ").Append(messages.Count).Append(" message(s), ");
        log.Append("roles: [").Append(string.Join(", ", messages.Select(m => $"'{m.Role}'"))).Append("]\n");
        if (output is not null)
        {
            var truncated = output.Length > 500 ? output[..500] + "..." : output;
            log.Append("Raw output (truncated): ").Append(truncated).Append('\n');
        }
        else
        {
            log.Append("No raw output available");
        }

        return log.ToString();
    }

    /// <summary>Starts the <c>llm.generate</c> activity with the standard tags, if anything is listening.</summary>
    protected Activity? StartGenerateActivity(ModelSize modelSize, int maxTokens, string? promptName)
    {
        var activity = GraphitiTelemetry.ActivitySource.StartActivity("llm.generate");
        if (activity is not null)
        {
            activity.SetTag("llm.provider", GetProviderType());
            activity.SetTag("model.size", modelSize == ModelSize.Small ? "small" : "medium");
            activity.SetTag("max_tokens", maxTokens);
            if (!string.IsNullOrEmpty(promptName))
            {
                activity.SetTag("prompt.name", promptName);
            }
        }

        return activity;
    }

    /// <summary>Marks an activity as failed with <paramref name="exception"/>.</summary>
    protected static void RecordError(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);
    }

    /// <summary>Parses a JSON response body. Returns a value that outlives the parser.</summary>
    protected static JsonElement ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Strips a Markdown code fence around a JSON payload. Models served through Ollama, llama.cpp and similar
    /// often wrap JSON in a <c>```json</c> fence even when a JSON response format is requested. From upstream's
    /// <c>OpenAIGenericClient._strip_code_fences</c>.
    /// </summary>
    protected internal static string StripCodeFences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripped = text.Trim();
        if (stripped.StartsWith("```", StringComparison.Ordinal))
        {
            stripped = OpeningFence().Replace(stripped, "", 1);
            stripped = ClosingFence().Replace(stripped, "", 1);
        }

        return stripped.Trim();
    }

    [GeneratedRegex(@"^```[a-zA-Z0-9_-]*[ \t]*\r?\n?")]
    private static partial Regex OpeningFence();

    [GeneratedRegex(@"\r?\n?```[ \t]*$")]
    private static partial Regex ClosingFence();

    // tenacity's wait_random_exponential(multiplier=10, min=5, max=120): uniform(min, clamp(10 * 2^(n-1))).
    internal static TimeSpan RetryDelay(int attempt)
    {
        var high = Math.Clamp(10 * Math.Pow(2, attempt - 1), 5, 120);
#pragma warning disable CA5394 // Backoff jitter; not security sensitive.
        var seconds = 5 + Random.Shared.NextDouble() * (high - 5);
#pragma warning restore CA5394
        return TimeSpan.FromSeconds(seconds);
    }
}
