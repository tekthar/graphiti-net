// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Prompts;
using Microsoft.Extensions.AI;

namespace GraphitiNet.Llm;

/// <summary>
/// An LLM client over any Microsoft.Extensions.AI <see cref="IChatClient"/>, so Anthropic, Gemini, Ollama, Azure
/// OpenAI, OpenAI and every other provider with an M.E.AI adapter work without a dedicated client.
/// </summary>
/// <remarks>
/// It follows upstream's base <c>LLMClient</c>: the response schema goes into the prompt, transient errors are
/// retried with backoff, and responses can be cached. When <c>useJsonSchemaResponseFormat</c> is on (the default),
/// it also asks the provider for native JSON-schema output. Models come from <see cref="LLMConfig.Model"/> and
/// <see cref="LLMConfig.SmallModel"/>; when they are <see langword="null"/> the chat client's own default is used.
/// </remarks>
public class ChatClientLLMClient : LLMClient
{
    private readonly ChatOptions? _defaultOptions;

    /// <summary>Creates the client.</summary>
    /// <param name="chatClient">The chat client to send requests to.</param>
    /// <param name="config">Model names, temperature and the token limit. Set <see cref="LLMConfig.Temperature"/>
    /// to <see langword="null"/> for models that don't accept one.</param>
    /// <param name="cache">Whether to cache responses on disk. Off by default.</param>
    /// <param name="useJsonSchemaResponseFormat">Whether to request native JSON-schema output for response models.
    /// Turn it off for providers that don't support it; the schema stays in the prompt either way.</param>
    /// <param name="defaultOptions">Options every request starts from, for provider-specific settings such as a
    /// reasoning effort. Each request clones them.</param>
    public ChatClientLLMClient(
        IChatClient chatClient,
        LLMConfig? config = null,
        bool cache = false,
        bool useJsonSchemaResponseFormat = true,
        ChatOptions? defaultOptions = null)
        : base(config, cache)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ChatClient = chatClient;
        UseJsonSchemaResponseFormat = useJsonSchemaResponseFormat;
        _defaultOptions = defaultOptions;
    }

    /// <summary>The chat client requests go to.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>Whether native JSON-schema output is requested for response models.</summary>
    public bool UseJsonSchemaResponseFormat { get; }

    /// <inheritdoc/>
    protected override async Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages,
        Type? responseModel,
        int maxTokens,
        ModelSize modelSize,
        string? model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var chatMessages = messages.Select(m => new ChatMessage(ToChatRole(m.Role), m.Content)).ToList();
        var options = _defaultOptions?.Clone() ?? new ChatOptions();
        options.ModelId = model ?? (modelSize == ModelSize.Small ? SmallModel ?? Model : Model) ?? options.ModelId;
        options.Temperature = Temperature ?? options.Temperature;
        options.MaxOutputTokens = maxTokens;
        options.ResponseFormat = responseModel is not null && UseJsonSchemaResponseFormat
            ? ChatResponseFormat.ForJsonSchema(
                JsonSerializer.SerializeToElement(ResponseModelSchema.GetSchema(responseModel)),
                ResponseModelSchema.GetName(responseModel))
            : ChatResponseFormat.Json;

        ChatResponse response;
        try
        {
            response = await ChatClient.GetResponseAsync(chatMessages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (IsRateLimit(e))
        {
            throw new RateLimitException(RateLimitException.DefaultMessage, e);
        }

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new EmptyResponseException("LLM returned an empty response");
        }

        return new LLMClientResponse(
            ParseJson(StripCodeFences(text)),
            response.Usage?.InputTokenCount ?? 0,
            response.Usage?.OutputTokenCount ?? 0);
    }

    /// <summary>The provider name from the chat client's metadata, or a guess from the class name.</summary>
    protected override string GetProviderType() =>
        ChatClient.GetService<ChatClientMetadata>()?.ProviderName ?? base.GetProviderType();

    private static ChatRole ToChatRole(string role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "user" => ChatRole.User,
        _ => new ChatRole(role),
    };
}
