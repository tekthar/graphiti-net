// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using Microsoft.Extensions.AI;

namespace GraphitiNet.Tests.Embedder;

public class EmbeddingGeneratorEmbedderTests
{
    private sealed class FakeGenerator(int dimension) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<(List<string> Values, EmbeddingGenerationOptions? Options)> Calls { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Calls.Add((list, options));
            var embeddings = list.Select((v, i) =>
                new Embedding<float>(Enumerable.Repeat((float)(i + 1), dimension).ToArray()));
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Create_TruncatesToEmbeddingDim()
    {
        var generator = new FakeGenerator(1536);
        var options = new EmbeddingGenerationOptions { ModelId = "m" };
        var embedder = new EmbeddingGeneratorEmbedder(generator, options: options);

        var result = await embedder.CreateAsync("hello");

        Assert.Equal(EmbedderConfig.DefaultEmbeddingDim, result.Length);
        Assert.All(result, v => Assert.Equal(1f, v));
        Assert.Same(options, generator.Calls[0].Options);
    }

    [Fact]
    public async Task CreateBatch_IsOneCallInOrder()
    {
        var generator = new FakeGenerator(4);
        var embedder = new EmbeddingGeneratorEmbedder(generator, new EmbedderConfig { EmbeddingDim = 2 });

        var result = await embedder.CreateBatchAsync(["a", "b", "c"]);

        Assert.Single(generator.Calls);
        Assert.Equal([["a", "b", "c"]], generator.Calls.Select(c => c.Values));
        Assert.Equal([[1f, 1f], [2f, 2f], [3f, 3f]], result);
        Assert.Empty(await embedder.CreateBatchAsync([]));
    }

    [Fact]
    public async Task GenerateEmbedding_ReplacesNewlines()
    {
        var generator = new FakeGenerator(2);
        var embedder = new EmbeddingGeneratorEmbedder(generator);

        await LLMUtils.GenerateEmbeddingAsync(embedder, "line one\nline two");

        Assert.Equal("line one line two", generator.Calls[0].Values[0]);
    }
}
