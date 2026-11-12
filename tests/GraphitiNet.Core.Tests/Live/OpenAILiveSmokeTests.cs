// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ClientModel;
using System.ComponentModel;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;
using SdkOpenAIClient = OpenAI.OpenAIClient;

namespace GraphitiNet.Tests.Live;

/// <summary>A fact that runs only when <c>GRAPHITI_LIVE_TESTS=1</c>. Live tests call real APIs and cost
/// tokens.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "GRAPHITI_LIVE_TESTS";

    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Live test; set {EnvironmentVariable}=1 to run it.";
        }
    }
}

public sealed record LiveCapital(
    [property: Description("The country's name in English")] string Country,
    string Capital,
    int? Population = null);

/// <summary>
/// One opt-in smoke test against the real OpenAI API: structured and JSON-mode generation, an embedding and a
/// rerank, through both the OpenAI clients and the Microsoft.Extensions.AI adapters. Uses <c>OPENAI_API_KEY</c>,
/// or a placeholder key when a proxy injects the real one.
/// </summary>
public class OpenAILiveSmokeTests(ITestOutputHelper output)
{
    private const string ChatModel = "gpt-5-nano";
    private const string LogprobModel = OpenAIRerankerClient.DefaultModel;

    [LiveFact]
    public async Task OpenAI_EndToEnd()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } key ? key : "dummy";
        var sdk = new SdkOpenAIClient(new ApiKeyCredential(apiKey));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        // 1. OpenAIClient, structured output through the Responses API (reasoning model: no temperature).
        var llm = new OpenAIClient(new LLMConfig(model: ChatModel, smallModel: ChatModel), client: sdk);
        var capital = await llm.GenerateResponseAsync<LiveCapital>(
            [new("system", "You answer geography questions."), new("user", "What is the capital of France?")],
            promptName: "live.structured", cancellationToken: ct);
        output.WriteLine($"OpenAIClient structured: {capital}");
        Assert.Contains("Paris", capital.Capital, StringComparison.OrdinalIgnoreCase);

        // 2. OpenAIClient, JSON mode through chat completions.
        var json = await llm.GenerateResponseAsync(
            [new("system", "Reply in JSON."), new("user", "Return {\"answer\": 2 + 2} with the sum computed.")],
            modelSize: ModelSize.Small, promptName: "live.json", cancellationToken: ct);
        output.WriteLine($"OpenAIClient JSON mode: {json}");
        Assert.Equal(4, json.GetProperty("answer").GetInt32());
        output.WriteLine(llm.TokenTracker.FormatSummary());
        Assert.True(llm.TokenTracker.GetUsage()["live.structured"].TotalInputTokens > 0);

        // 3. OpenAIGenericClient (chat completions with json_schema) on a non-reasoning model.
        var generic = new OpenAIGenericClient(new LLMConfig(model: LogprobModel, temperature: 0), client: sdk);
        var genericCapital = await generic.GenerateResponseAsync<LiveCapital>(
            [new("system", "You answer geography questions."), new("user", "What is the capital of Japan?")],
            cancellationToken: ct);
        output.WriteLine($"OpenAIGenericClient: {genericCapital}");
        Assert.Contains("Tokyo", genericCapital.Capital, StringComparison.OrdinalIgnoreCase);

        // 4. ChatClientLLMClient over the M.E.AI OpenAI chat client.
        using var chatClient = sdk.GetChatClient(ChatModel).AsIChatClient();
        var meai = new ChatClientLLMClient(chatClient, new LLMConfig(temperature: null));
        var meaiCapital = await meai.GenerateResponseAsync<LiveCapital>(
            [new("system", "You answer geography questions."), new("user", "What is the capital of Italy?")],
            promptName: "live.meai", cancellationToken: ct);
        output.WriteLine($"ChatClientLLMClient: {meaiCapital}");
        Assert.Contains("Rome", meaiCapital.Capital, StringComparison.OrdinalIgnoreCase);
        Assert.True(meai.TokenTracker.GetTotalUsage().TotalTokens > 0);

        // 5. Embeddings: OpenAIEmbedder and the M.E.AI adapter agree.
        var embedder = new OpenAIEmbedder(client: sdk);
        var embedding = await embedder.CreateAsync("Graphiti builds temporal knowledge graphs.", ct);
        using var generator = sdk.GetEmbeddingClient(OpenAIEmbedderConfig.DefaultEmbeddingModel).AsIEmbeddingGenerator();
        var adapted = await new EmbeddingGeneratorEmbedder(generator)
            .CreateAsync("Graphiti builds temporal knowledge graphs.", ct);
        output.WriteLine($"Embedding: {embedding.Length} dims; first {embedding[0]:F5}, adapter {adapted[0]:F5}");
        Assert.Equal(EmbedderConfig.DefaultEmbeddingDim, embedding.Length);
        Assert.Equal(embedding.Length, adapted.Length);
        Assert.True(CosineSimilarity(embedding, adapted) > 0.99);

        // 6. Reranking: logprob reranker and the IChatClient reranker.
        string[] passages =
        [
            "Bananas are a good source of potassium.",
            "Paris is the capital and most populous city of France.",
        ];
        var reranked = await new OpenAIRerankerClient(new LLMConfig(model: LogprobModel), sdk)
            .RankAsync("What is the capital of France?", passages, ct);
        output.WriteLine("OpenAIRerankerClient: " + string.Join("; ", reranked.Select(r => $"{r.Score:F3} {r.Passage}")));
        Assert.StartsWith("Paris", reranked[0].Passage, StringComparison.Ordinal);

        var chatReranked = await new ChatClientCrossEncoder(chatClient, new ChatOptions())
            .RankAsync("What is the capital of France?", passages, ct);
        output.WriteLine("ChatClientCrossEncoder: " + string.Join("; ", chatReranked.Select(r => $"{r.Score:F3} {r.Passage}")));
        Assert.StartsWith("Paris", chatReranked[0].Passage, StringComparison.Ordinal);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
