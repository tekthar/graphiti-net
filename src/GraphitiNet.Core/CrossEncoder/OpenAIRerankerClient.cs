// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/cross_encoder/openai_reranker_client.py (Graphiti v0.31.2) and changed for .NET.

using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.CrossEncoder;

/// <summary>
/// Reranks passages with OpenAI: one boolean "is this passage relevant?" call per passage, run concurrently, scored
/// by the log-probability of the answer. Needs a model that supports <c>logprobs</c>, <c>logit_bias</c> and
/// <c>max_tokens</c>, which rules out reasoning models.
/// </summary>
public class OpenAIRerankerClient : CrossEncoderClient
{
    /// <summary>The default model. gpt-6-luna rejects <c>max_tokens</c> and <c>logit_bias</c>, so the reranker
    /// uses a non-reasoning model.</summary>
    public const string DefaultModel = "gpt-4.1-mini";

    private const string Indent = "                           ";

    /// <summary>Creates the reranker.</summary>
    /// <param name="config">The settings: API key, base URL and model.</param>
    /// <param name="client">An SDK client to use instead of creating one from <paramref name="config"/>.</param>
    public OpenAIRerankerClient(LLMConfig? config = null, SdkOpenAIClient? client = null)
    {
        Config = config ?? new LLMConfig();
        Client = client ?? OpenAIWire.CreateClient(Config.ApiKey, Config.BaseUrl);
    }

    /// <summary>Creates the reranker, sharing an <see cref="OpenAIClient"/>'s SDK client.</summary>
    /// <param name="config">The settings: the model.</param>
    /// <param name="client">The LLM client whose SDK client to use.</param>
    public OpenAIRerankerClient(LLMConfig? config, OpenAIClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Config = config ?? new LLMConfig();
        Client = client.Client;
    }

    /// <summary>The settings.</summary>
    public LLMConfig Config { get; }

    /// <summary>The OpenAI SDK client the requests go through.</summary>
    public SdkOpenAIClient Client { get; }

    /// <summary>How many passages are scored at once.</summary>
    public int MaxConcurrency { get; init; } = RerankConcurrency.DefaultLimit;

    /// <summary>Where errors are logged.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passages);
        var model = Config.Model ?? DefaultModel;
        try
        {
            var responses = await RerankConcurrency.GatherAsync(
                passages,
                MaxConcurrency,
                (passage, ct) => OpenAIWire.CompleteChatAsync(Client, model, BuildRequest(model, query, passage), ct),
                cancellationToken).ConfigureAwait(false);

            var scores = new List<double>();
            foreach (var response in responses)
            {
                var topLogprobs = TopLogprobs(response);
                if (topLogprobs.Count == 0)
                {
                    continue;
                }

                var (token, logprob) = topLogprobs[0];
                var normLogprob = Math.Exp(logprob);
                var firstWord = token.Trim().Split(' ')[0];
                scores.Add(string.Equals(firstWord, "true", StringComparison.OrdinalIgnoreCase)
                    ? normLogprob
                    : 1 - normLogprob);
            }

            if (scores.Count != passages.Count)
            {
                throw new InvalidOperationException(
                    $"Got {scores.Count} relevance scores for {passages.Count} passages.");
            }

            return passages.Zip(scores, (passage, score) => new RankedPassage(passage, score))
                .OrderByDescending(r => r.Score)
                .ToList();
        }
        catch (Exception e) when (e is System.ClientModel.ClientResultException { Status: 429 })
        {
            throw new RateLimitException(RateLimitException.DefaultMessage, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ProviderLog.GenerationError(Logger, e);
            throw;
        }
    }

    /// <summary>The chat completion request for one passage.</summary>
    internal static JsonObject BuildRequest(string model, string query, string passage) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You are an expert tasked with determining whether the passage is relevant to the query",
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] =
                    "\n"
                    + Indent + "Respond with \"True\" if PASSAGE is relevant to QUERY and \"False\" otherwise.\n"
                    + Indent + "<PASSAGE>\n"
                    + Indent + passage + "\n"
                    + Indent + "</PASSAGE>\n"
                    + Indent + "<QUERY>\n"
                    + Indent + query + "\n"
                    + Indent + "</QUERY>\n"
                    + Indent,
            },
        },
        ["temperature"] = 0,
        ["max_tokens"] = 1,
        ["logit_bias"] = new JsonObject { ["6432"] = 1, ["7983"] = 1 },
        ["logprobs"] = true,
        ["top_logprobs"] = 2,
    };

    private static List<(string Token, double Logprob)> TopLogprobs(JsonElement response)
    {
        var result = new List<(string, double)>();
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("logprobs", out var logprobs) || logprobs.ValueKind != JsonValueKind.Object
            || !logprobs.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() == 0
            || !content[0].TryGetProperty("top_logprobs", out var top) || top.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in top.EnumerateArray())
        {
            result.Add((item.GetProperty("token").GetString() ?? "", item.GetProperty("logprob").GetDouble()));
        }

        return result;
    }
}
