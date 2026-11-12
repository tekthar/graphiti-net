// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/config.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Llm;

/// <summary>Which of the configured models a prompt runs on.</summary>
public enum ModelSize
{
    /// <summary>The small model (<see cref="LLMConfig.SmallModel"/>), for simpler prompts.</summary>
    Small,

    /// <summary>The main model (<see cref="LLMConfig.Model"/>).</summary>
    Medium,
}

/// <summary>
/// Settings for an LLM client: API key, model names, base URL, temperature and the output token limit.
/// </summary>
/// <param name="apiKey">The API key. OpenAI clients fall back to the <c>OPENAI_API_KEY</c> environment variable.</param>
/// <param name="model">The model for <see cref="ModelSize.Medium"/> prompts. Each client has its own default.</param>
/// <param name="baseUrl">The API's base URL. OpenAI clients fall back to <c>OPENAI_BASE_URL</c>, then to OpenAI.</param>
/// <param name="temperature">The sampling temperature. <see langword="null"/> means don't send one.</param>
/// <param name="maxTokens">The maximum number of output tokens.</param>
/// <param name="smallModel">The model for <see cref="ModelSize.Small"/> prompts.</param>
public class LLMConfig(
    string? apiKey = null,
    string? model = null,
    string? baseUrl = null,
    float? temperature = LLMConfig.DefaultTemperature,
    int maxTokens = LLMConfig.DefaultMaxTokens,
    string? smallModel = null)
{
    /// <summary>Upstream's <c>DEFAULT_MAX_TOKENS</c>.</summary>
    public const int DefaultMaxTokens = 16384;

    /// <summary>Upstream's <c>DEFAULT_TEMPERATURE</c> in <c>config.py</c>.</summary>
    public const float DefaultTemperature = 1;

    /// <summary>The API key.</summary>
    public string? ApiKey { get; init; } = apiKey;

    /// <summary>The model for <see cref="ModelSize.Medium"/> prompts.</summary>
    public string? Model { get; init; } = model;

    /// <summary>The API's base URL.</summary>
    public string? BaseUrl { get; init; } = baseUrl;

    /// <summary>The sampling temperature, or <see langword="null"/> to not send one.</summary>
    public float? Temperature { get; init; } = temperature;

    /// <summary>The maximum number of output tokens.</summary>
    public int MaxTokens { get; init; } = maxTokens;

    /// <summary>The model for <see cref="ModelSize.Small"/> prompts.</summary>
    public string? SmallModel { get; init; } = smallModel;
}
