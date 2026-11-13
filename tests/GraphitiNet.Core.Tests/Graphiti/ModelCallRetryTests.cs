// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ClientModel;
using System.Net;
using System.Text.Json;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Maintenance;
using GraphitiNet.Prompts;
using GraphitiNet.Tests.Embedder;
using GraphitiNet.Tests.Llm;
using static GraphitiNet.Tests.GraphitiApi.GraphitiFixture;

namespace GraphitiNet.Tests.GraphitiApi;

/// <summary>The opt-in retry policy around model calls (<see cref="ModelCallRetryOptions"/>).</summary>
public class ModelCallRetryTests
{
    private const string EmptyAnswer = """{"extracted_entities": [], "edges": [], "summaries": []}""";

    private static readonly ModelCallRetryOptions Fast = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
    };

    // ---- Classification and delays --------------------------------------------------------------------------

    public static TheoryData<Exception, bool> Errors => new()
    {
        { new RateLimitException(), true },
        { new HttpRequestException("reset", null, null), true },
        { new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests), true },
        { new HttpRequestException("gateway", null, HttpStatusCode.BadGateway), true },
        { new HttpRequestException("timeout", null, HttpStatusCode.RequestTimeout), true },
        { new HttpRequestException("bad", null, HttpStatusCode.BadRequest), false },
        { new HttpRequestException("auth", null, HttpStatusCode.Unauthorized), false },
        { new TimeoutException(), true },
        { new TaskCanceledException("HttpClient timeout", new TimeoutException()), true },
        { new OperationCanceledException(), false },
        { new IOException("connection reset"), true },
        { new ClientResultException("no response"), true },
        { new GraphitiException("wrapped", new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable)), true },
        { new JsonException("malformed"), false },
        { new EmptyResponseException("empty"), false },
        { new RefusalException("no"), false },
        { new ModelCallRefusedException(new ModelCallInfo(ModelCallKind.Llm, null, "op", null, null, null)), false },
        { new InvalidOperationException("bug"), false },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Transient_errors_are_rate_limits_server_errors_timeouts_and_dropped_connections(
        Exception error, bool transient) =>
        Assert.Equal(transient, ModelCallRetryOptions.IsTransient(error));

    [Fact]
    public void The_backoff_doubles_with_jitter_and_is_capped()
    {
        var options = new ModelCallRetryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(5),
        };
        var error = new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable);

        Assert.Equal(TimeSpan.FromSeconds(0.5), options.GetDelay(1, error, jitter: 0));
        Assert.Equal(TimeSpan.FromSeconds(1), options.GetDelay(1, error, jitter: 1));
        Assert.Equal(TimeSpan.FromSeconds(2), options.GetDelay(2, error, jitter: 1));
        Assert.Equal(TimeSpan.FromSeconds(4), options.GetDelay(3, error, jitter: 1));
        Assert.Equal(TimeSpan.FromSeconds(5), options.GetDelay(4, error, jitter: 1));
        Assert.Equal(TimeSpan.FromSeconds(5), options.GetDelay(40, error, jitter: 1));
    }

    [Fact]
    public void A_retry_after_is_waited_for_capped_at_the_max_delay_unless_turned_off()
    {
        var options = new ModelCallRetryOptions { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(7),
            options.GetDelay(1, new RateLimitException { RetryAfter = TimeSpan.FromSeconds(7) }, jitter: 0));
        Assert.Equal(TimeSpan.FromSeconds(30),
            options.GetDelay(1, new RateLimitException { RetryAfter = TimeSpan.FromMinutes(5) }, jitter: 0));
        Assert.Equal(TimeSpan.FromSeconds(0.5), (options with { RespectRetryAfter = false })
            .GetDelay(1, new RateLimitException { RetryAfter = TimeSpan.FromSeconds(7) }, jitter: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_options_are_refused(int maxAttempts) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graphiti(
            new InMemoryGraphDriver(), new ScriptedLlmClient("{}"), new HashEmbedder(), new LengthCrossEncoder(),
            modelCallRetry: new ModelCallRetryOptions { MaxAttempts = maxAttempts }));

    // ---- LLM calls ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_policy_a_transient_llm_error_fails_the_episode_as_upstream()
    {
        var llm = new FlakyLlmClient(failuresPerPrompt: 1);
        await using var graphiti = Create(out _, llm);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            graphiti.AddEpisodeAsync("m", "user: hello", "chat", Now, groupId: NewGroup()));
    }

    [Fact]
    public async Task With_the_policy_transient_llm_errors_are_retried_counting_tokens_and_asking_the_gate_once()
    {
        var llm = new FlakyLlmClient(failuresPerPrompt: 2);
        var asked = new List<ModelCallInfo>();
        var gate = new DelegateModelCallGate((call, _) =>
        {
            lock (asked)
            {
                asked.Add(call);
            }

            return ValueTask.FromResult(true);
        });
        await using var graphiti = new Graphiti(
            new InMemoryGraphDriver(), llm, new HashEmbedder(), new LengthCrossEncoder(),
            groupLock: new InProcessGroupLock(), modelCallGate: gate, modelCallRetry: Fast);

        var result = await graphiti.AddEpisodeAsync("m", "user: hello", "chat", Now, groupId: NewGroup());

        var answered = llm.Successes;
        Assert.True(answered > 0);
        Assert.Equal(answered * 3, llm.Attempts); // two failures, then the answer, per prompt
        Assert.Equal(answered * (FlakyLlmClient.InputTokens + FlakyLlmClient.OutputTokens), result.TokenUsage.TotalTokens);
        Assert.Equal(answered, asked.Count(call => call.Kind == ModelCallKind.Llm));

        // Every attempt sent the same freshly built prompt: nothing the failed attempts appended carries over.
        foreach (var attempts in llm.Prompts.GroupBy(p => p.PromptName))
        {
            Assert.Single(attempts.Select(a => a.Text).Distinct());
        }
    }

    [Fact]
    public async Task An_llm_error_that_outlasts_the_attempts_is_thrown()
    {
        var llm = new FlakyLlmClient(failuresPerPrompt: 5);
        await using var graphiti = new Graphiti(
            new InMemoryGraphDriver(), llm, new HashEmbedder(), new LengthCrossEncoder(), modelCallRetry: Fast);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            graphiti.AddEpisodeAsync("m", "user: hello", "chat", Now, groupId: NewGroup()));
        Assert.Equal(Fast.MaxAttempts, llm.Attempts);
    }

    [Fact]
    public async Task A_refused_call_is_not_retried()
    {
        var llm = new FlakyLlmClient(failuresPerPrompt: 0);
        var asks = 0;
        var gate = new DelegateModelCallGate((_, _) =>
        {
            Interlocked.Increment(ref asks);
            return ValueTask.FromResult(false);
        });
        await using var graphiti = new Graphiti(
            new InMemoryGraphDriver(), llm, new HashEmbedder(), new LengthCrossEncoder(),
            modelCallGate: gate, modelCallRetry: Fast);

        await Assert.ThrowsAsync<ModelCallRefusedException>(() =>
            graphiti.AddEpisodeAsync("m", "user: hello", "chat", Now, groupId: NewGroup()));
        Assert.Equal(1, asks);
        Assert.Equal(0, llm.Attempts);
    }

    [Fact]
    public async Task Cancellation_stops_the_wait_between_attempts()
    {
        using var cancellation = new CancellationTokenSource();
        var llm = new FlakyLlmClient(failuresPerPrompt: 1)
        {
            OnFailure = () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(50)),
        };
        await using var graphiti = new Graphiti(
            new InMemoryGraphDriver(), llm, new HashEmbedder(), new LengthCrossEncoder(),
            modelCallRetry: Fast with { BaseDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromMinutes(1) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => graphiti.AddEpisodeAsync(
            "m", "user: hello", "chat", Now, groupId: NewGroup(), cancellationToken: cancellation.Token));
        Assert.Equal(1, llm.Attempts);
    }

    [Fact]
    public async Task An_openai_rate_limit_is_retried_from_a_fresh_prompt_honouring_retry_after()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(FakeOpenAI.Error("slow down", "rate_limit_exceeded"), HttpStatusCode.TooManyRequests,
                new Dictionary<string, string> { ["retry-after-ms"] = "3" })
            .Enqueue(FakeOpenAI.Response("""{"description": "A short description"}""", inputTokens: 20,
                outputTokens: 4));
        var llm = new OpenAIClient(new LLMConfig(), client: FakeOpenAI.Client(handler));
        var clients = new GraphitiClients
        {
            Driver = new InMemoryGraphDriver(),
            LlmClient = llm,
            Embedder = new HashEmbedder(),
            ModelCallRetry = Fast with { MaxDelay = TimeSpan.FromSeconds(1) },
        };

        var description = await CommunityOperations.GenerateSummaryDescriptionAsync(clients, "Alice and Bob.");

        Assert.Equal("A short description", description);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Body!.ToJsonString(), handler.Requests[1].Body!.ToJsonString());
        Assert.DoesNotContain("previous response attempt was invalid", handler.Requests[1].Body!.ToJsonString(),
            StringComparison.Ordinal);
        Assert.Equal(24, llm.TokenTracker.GetTotalUsage().TotalTokens);
    }

    [Fact]
    public void The_retry_after_header_of_a_provider_response_is_read()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(FakeOpenAI.Error("slow down", "rate_limit_exceeded"), HttpStatusCode.TooManyRequests,
                new Dictionary<string, string> { ["Retry-After"] = "12" });
        var embedder = new OpenAIEmbedder(new OpenAIEmbedderConfig { ApiKey = "test" }, FakeOpenAI.Client(handler));

        var error = Assert.ThrowsAny<ClientResultException>(() => embedder.CreateAsync("x").GetAwaiter().GetResult());

        Assert.Equal(TimeSpan.FromSeconds(12), ModelCallRetryOptions.GetRetryAfter(error));
        Assert.Equal(TimeSpan.FromSeconds(12), ModelCallRetryOptions.GetRetryAfter(
            new RateLimitException(RateLimitException.DefaultMessage, error)));
        Assert.Null(ModelCallRetryOptions.GetRetryAfter(new RateLimitException()));
    }

    // ---- Embeddings and reranks -----------------------------------------------------------------------------

    [Fact]
    public async Task Embedding_and_rerank_errors_are_retried_and_the_gate_is_asked_once_per_call()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(FakeOpenAI.Error("overloaded", "server_error"), HttpStatusCode.ServiceUnavailable)
            .Enqueue(OpenAIEmbedderTests.EmbeddingsResponse(OpenAIEmbedderTests.CreateEmbeddingValues(0.1f, 8)));
        var embedder = new OpenAIEmbedder(new OpenAIEmbedderConfig { ApiKey = "test" }, FakeOpenAI.Client(handler));
        var reranker = new FlakyCrossEncoder();
        var asks = new List<ModelCallKind>();
        var gate = new DelegateModelCallGate((call, _) =>
        {
            asks.Add(call.Kind);
            return ValueTask.FromResult(true);
        });
        await using var graphiti = new Graphiti(
            new InMemoryGraphDriver(), new ScriptedLlmClient(EmptyAnswer), embedder, reranker,
            modelCallGate: gate, modelCallRetry: Fast);

        await graphiti.SearchAsync(
            "Alice", GraphitiNet.Search.SearchConfigRecipes.CombinedHybridSearchCrossEncoder, groupIds: [NewGroup()]);

        // One embedding call and each rerank call failed once; the gate saw each logical call once.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, asks.Count(kind => kind == ModelCallKind.Embedding));
        Assert.True(reranker.Attempts > 1);
        Assert.Equal(reranker.Attempts - 1, asks.Count(kind => kind == ModelCallKind.Rerank));
        Assert.Same(embedder, graphiti.Embedder);
    }

    [Fact]
    public async Task A_failed_rerank_is_retried()
    {
        var reranker = new FlakyCrossEncoder();
        var retrying = new RetryingCrossEncoderClient(reranker, Fast, null);

        var ranked = await retrying.RankAsync("q", ["a", "bb"]);

        Assert.Equal(2, reranker.Attempts);
        Assert.Equal("bb", ranked[0].Passage);
    }

    /// <summary>Fails the first <c>failuresPerPrompt</c> attempts of every prompt with a dropped connection, which the
    /// base client's own retry does not cover, then answers.</summary>
    private sealed class FlakyLlmClient(int failuresPerPrompt) : LLMClient(new LLMConfig(model: "flaky"))
    {
        public const long InputTokens = 10;
        public const long OutputTokens = 5;

        private readonly Dictionary<string, int> _failures = [];
        private readonly Lock _lock = new();

        public int Attempts { get; private set; }

        public int Successes { get; private set; }

        public Action? OnFailure { get; init; }

        public List<(string PromptName, string Text)> Prompts { get; } = [];

        protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
            IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize, string? model,
            CancellationToken cancellationToken)
        {
            var prompt = GraphitiCallContext.Current?.PromptName ?? "";
            lock (_lock)
            {
                Attempts++;
                Prompts.Add((prompt, string.Join("\n", messages.Select(m => m.Content))));
                var failed = _failures.GetValueOrDefault(prompt);
                if (failed < failuresPerPrompt)
                {
                    _failures[prompt] = failed + 1;
                    OnFailure?.Invoke();
                    throw new HttpRequestException("The connection was reset.");
                }

                Successes++;
            }

            return Task.FromResult(new LLMClientResponse(
                JsonDocument.Parse(EmptyAnswer).RootElement.Clone(), InputTokens, OutputTokens));
        }
    }

    /// <summary>A reranker whose first call fails with a 502.</summary>
    private sealed class FlakyCrossEncoder : CrossEncoderClient
    {
        public int Attempts { get; private set; }

        public override Task<IReadOnlyList<RankedPassage>> RankAsync(
            string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
        {
            if (++Attempts == 1)
            {
                throw new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway);
            }

            return Task.FromResult<IReadOnlyList<RankedPassage>>(
                passages.Select(p => new RankedPassage(p, p.Length)).OrderByDescending(p => p.Score).ToList());
        }
    }
}
