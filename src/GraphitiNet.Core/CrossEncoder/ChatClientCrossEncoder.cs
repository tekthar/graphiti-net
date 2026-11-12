// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/cross_encoder/gemini_reranker_client.py (Graphiti v0.31.2) and changed for .NET:
// it runs over any Microsoft.Extensions.AI IChatClient instead of the Gemini SDK.

using System.Globalization;
using System.Text.RegularExpressions;
using GraphitiNet.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet.CrossEncoder;

/// <summary>
/// Reranks passages over any Microsoft.Extensions.AI <see cref="IChatClient"/>. Most providers don't expose log
/// probabilities, so, like upstream's Gemini reranker, each passage is scored directly: the model rates its
/// relevance from 0 to 100, and the score is normalized to [0, 1]. One call per passage, run concurrently.
/// </summary>
/// <remarks>
/// The default options use temperature 0 and at most 3 output tokens, as upstream's Gemini reranker. Reasoning models
/// reject a temperature and need room to think, so pass <c>chatOptions</c> without them (for example an empty
/// <see cref="ChatOptions"/>) when using one.
/// </remarks>
public partial class ChatClientCrossEncoder : CrossEncoderClient
{
    /// <summary>The system instruction sent with each passage.</summary>
    public const string SystemInstruction =
        "You are an expert at rating passage relevance. Respond with only a number from 0-100.";

    private readonly ChatOptions _chatOptions;

    /// <summary>Creates the reranker.</summary>
    /// <param name="chatClient">The chat client to send requests to.</param>
    /// <param name="chatOptions">Options for each request; <see langword="null"/> means temperature 0 and 3 output
    /// tokens. Each request clones them.</param>
    /// <param name="maxConcurrency">How many passages are scored at once.</param>
    public ChatClientCrossEncoder(
        IChatClient chatClient,
        ChatOptions? chatOptions = null,
        int maxConcurrency = RerankConcurrency.DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ChatClient = chatClient;
        _chatOptions = chatOptions ?? new ChatOptions { Temperature = 0, MaxOutputTokens = 3 };
        MaxConcurrency = maxConcurrency;
    }

    /// <summary>The chat client requests go to.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>How many passages are scored at once.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Where warnings and errors are logged.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passages);
        if (passages.Count <= 1)
        {
            return passages.Select(p => new RankedPassage(p, 1.0)).ToList();
        }

        try
        {
            var responses = await RerankConcurrency.GatherAsync(
                passages,
                MaxConcurrency,
                (passage, ct) => ChatClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, SystemInstruction),
                        new ChatMessage(ChatRole.User, BuildPrompt(query, passage))],
                    _chatOptions.Clone(),
                    ct),
                cancellationToken).ConfigureAwait(false);

            return passages.Zip(responses, (passage, response) => new RankedPassage(passage, Score(response.Text)))
                .OrderByDescending(r => r.Score)
                .ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException && IsRateLimit(e))
        {
            throw new RateLimitException(RateLimitException.DefaultMessage, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ProviderLog.GenerationError(Logger, e);
            throw;
        }
    }

    /// <summary>The scoring prompt for one passage, as upstream's Gemini reranker writes it.</summary>
    internal static string BuildPrompt(string query, string passage) =>
        "Rate how well this passage answers or relates to the query. Use a scale from 0 to 100.\n\n"
        + $"Query: {query}\n\n"
        + $"Passage: {passage}\n\n"
        + "Provide only a number between 0 and 100 (no explanation, just the number):";

    /// <summary>Reads a 0-100 score from the model's answer and normalizes it to [0, 1]; 0 if there is none.</summary>
    internal double Score(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            ProviderLog.EmptyScore(Logger);
            return 0.0;
        }

        var scoreText = text.Trim();
        var match = ScorePattern().Match(scoreText);
        if (!match.Success)
        {
            ProviderLog.UnparsableScore(Logger, scoreText);
            return 0.0;
        }

        var score = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return Math.Clamp(score / 100.0, 0.0, 1.0);
    }

    private static bool IsRateLimit(Exception e)
    {
        if (e is System.ClientModel.ClientResultException { Status: 429 }
            or HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
        {
            return true;
        }

        var message = e.Message;
        return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("resource_exhausted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("429", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\b(\d{1,3})\b")]
    private static partial Regex ScorePattern();
}
