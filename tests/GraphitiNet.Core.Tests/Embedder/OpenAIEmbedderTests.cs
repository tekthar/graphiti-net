// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/embedder/test_openai.py and tests/embedder/embedder_fixtures.py (Graphiti v0.31.2) and changed
// for .NET. The mocked SDK client is replaced by a fake HTTP handler.

using System.Text.Json.Nodes;
using GraphitiNet.Embedder;
using GraphitiNet.Tests.Llm;

namespace GraphitiNet.Tests.Embedder;

public class OpenAIEmbedderTests
{
    /// <summary>Embedding values with the given multiplier and dimension.</summary>
    internal static float[] CreateEmbeddingValues(float multiplier = 0.1f, int dimension = 1536) =>
        Enumerable.Repeat(multiplier, dimension).ToArray();

    internal static string EmbeddingsResponse(params float[][] embeddings)
    {
        var data = new JsonArray();
        for (var i = 0; i < embeddings.Length; i++)
        {
            data.Add(new JsonObject
            {
                ["object"] = "embedding",
                ["index"] = i,
                ["embedding"] = new JsonArray(embeddings[i].Select(v => (JsonNode?)v).ToArray()),
            });
        }

        return new JsonObject
        {
            ["object"] = "list",
            ["data"] = data,
            ["model"] = "text-embedding-3-small",
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["total_tokens"] = 1 },
        }.ToJsonString();
    }

    private static (OpenAIEmbedder Embedder, FakeHttpHandler Handler) Create(string response)
    {
        var handler = new FakeHttpHandler().Enqueue(response);
        var embedder = new OpenAIEmbedder(new OpenAIEmbedderConfig { ApiKey = "test_api_key" },
            FakeOpenAI.Client(handler));
        return (embedder, handler);
    }

    [Fact]
    public async Task Create_CallsApiCorrectly()
    {
        var (embedder, handler) = Create(EmbeddingsResponse(CreateEmbeddingValues()));

        var result = await embedder.CreateAsync("Test input");

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/embeddings", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(OpenAIEmbedderConfig.DefaultEmbeddingModel, (string?)request.Body!["model"]);
        Assert.Equal("Test input", (string?)request.Body!["input"]);
        Assert.False(request.Body!.ContainsKey("dimensions"));
        Assert.Equal(CreateEmbeddingValues()[..embedder.Config.EmbeddingDim], result);
    }

    [Fact]
    public async Task CreateBatch_ProcessesMultipleInputs()
    {
        var (embedder, handler) = Create(EmbeddingsResponse(
            CreateEmbeddingValues(0.1f), CreateEmbeddingValues(0.2f), CreateEmbeddingValues(0.3f)));
        string[] inputBatch = ["Input 1", "Input 2", "Input 3"];

        var result = await embedder.CreateBatchAsync(inputBatch);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(OpenAIEmbedderConfig.DefaultEmbeddingModel, (string?)request.Body!["model"]);
        Assert.Equal(inputBatch, request.Body!["input"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(3, result.Count);
        Assert.Equal(CreateEmbeddingValues(0.1f)[..1024], result[0]);
        Assert.Equal(CreateEmbeddingValues(0.2f)[..1024], result[1]);
        Assert.Equal(CreateEmbeddingValues(0.3f)[..1024], result[2]);
    }

    [Fact]
    public async Task ShorterEmbeddingsAreNotPadded()
    {
        var handler = new FakeHttpHandler().Enqueue(EmbeddingsResponse(CreateEmbeddingValues(0.5f, 8)));
        var embedder = new OpenAIEmbedder(
            new OpenAIEmbedderConfig { EmbeddingModel = "custom", EmbeddingDim = 16 }, FakeOpenAI.Client(handler));

        var result = await embedder.CreateAsync("x");

        Assert.Equal(8, result.Length);
        Assert.Equal("custom", (string?)handler.Requests[0].Body!["model"]);
    }

    [Fact]
    public async Task AzureEmbedder_ReturnsFullLengthEmbeddings()
    {
        var handler = new FakeHttpHandler().Enqueue(EmbeddingsResponse(CreateEmbeddingValues(0.1f)));
        var embedder = new AzureOpenAIEmbedderClient(FakeOpenAI.Client(handler), "my-deployment");

        var result = await embedder.CreateAsync("Test input");

        Assert.Equal(1536, result.Length);
        Assert.Equal("my-deployment", (string?)handler.Requests[0].Body!["model"]);
    }

    [Fact]
    public void Config_ToString_does_not_print_the_api_key()
    {
        var config = new OpenAIEmbedderConfig { ApiKey = "sk-secret-value", EmbeddingModel = "m", EmbeddingDim = 8 };

        var text = config.ToString();

        Assert.DoesNotContain("sk-secret-value", text, StringComparison.Ordinal);
        Assert.Contains("EmbeddingDim = 8", text, StringComparison.Ordinal);
        Assert.Contains("EmbeddingModel = m", text, StringComparison.Ordinal);
        Assert.Contains("ApiKey = ***", text, StringComparison.Ordinal);
    }
}
