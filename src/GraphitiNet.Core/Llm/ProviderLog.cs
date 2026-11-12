// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Extensions.Logging;

namespace GraphitiNet.Llm;

/// <summary>Log messages of the LLM, embedder and cross-encoder clients. Texts follow upstream's log lines.</summary>
internal static partial class ProviderLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache hit for {CacheKey}")]
    public static partial void CacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Corrupted cache entry for key {CacheKey}, ignoring")]
    public static partial void CorruptedCacheEntry(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying {Method} after {Attempt} attempts...")]
    public static partial void Retrying(ILogger logger, Exception exception, string method, int attempt);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "OpenAI Authentication Error. Please verify your API key is correct.")]
    public static partial void AuthenticationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Connection error communicating with OpenAI API. Please check your network connection and API key.")]
    public static partial void ConnectionError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in generating LLM response")]
    public static partial void GenerationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Max retries ({MaxRetries}) exceeded.")]
    public static partial void MaxRetriesExceeded(ILogger logger, Exception exception, int maxRetries);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Retrying after application error (attempt {Attempt}/{MaxRetries})")]
    public static partial void RetryingAfterApplicationError(ILogger logger, Exception exception, int attempt,
        int maxRetries);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {Provider} embedding")]
    public static partial void EmbeddingError(ILogger logger, Exception exception, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not extract numeric score from response: {ScoreText}")]
    public static partial void UnparsableScore(ILogger logger, string scoreText);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Empty response from the LLM for passage scoring")]
    public static partial void EmptyScore(ILogger logger);
}
