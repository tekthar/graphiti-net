// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet;

/// <summary>
/// An opt-in retry policy around each model call a <see cref="Graphiti"/> operation makes (LLM prompts, embeddings,
/// reranks), so a transient provider error (HTTP 429, a 5xx, a timeout, a dropped connection) is retried with
/// exponential backoff instead of failing the whole operation. Pass it as <c>modelCallRetry</c> to the
/// <see cref="Graphiti"/> constructor. A graphiti-net addition; without it, behaviour is upstream's.
/// </summary>
/// <remarks>
/// <para>Each attempt of an LLM call rebuilds the prompt from scratch, so nothing an earlier attempt appended (such as
/// the OpenAI clients' "the previous response attempt was invalid" re-prompt) carries over. Failed attempts record no
/// token usage; the attempt that succeeds records its usage once. A <see cref="IModelCallGate"/> is asked once per
/// logical call: retries of a call it allowed are not asked again.</para>
/// <para>It sits above the clients' own retries, which stay as upstream's: the base <see cref="LLMClient"/> retries
/// rate limits and 5xx errors itself (4 attempts, 5 to 120 seconds apart), and the OpenAI SDK retries 429 and 5xx
/// responses before graphiti-net sees them. Attempts therefore multiply; lower one layer if that is too many.</para>
/// <para>The cancellation token cancels the wait between attempts, and a cancelled call is never retried.</para>
/// </remarks>
public sealed record ModelCallRetryOptions
{
    /// <summary>Attempts per model call, including the first. Default 4; 1 turns retries off.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>The backoff before the second attempt; it doubles for each attempt after that, up to
    /// <see cref="MaxDelay"/>. The wait is drawn at random between half the backoff and the whole of it (jitter), so
    /// concurrent calls spread out. Default 1 second.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between two attempts, including one a <c>Retry-After</c> asks for. Default 60
    /// seconds.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether to wait as long as the provider asks, when the error carries a <c>Retry-After</c> (or
    /// <c>retry-after-ms</c>) response header or <see cref="RateLimitException.RetryAfter"/>, instead of the backoff
    /// (capped at <see cref="MaxDelay"/>). Default <see langword="true"/>.
    /// </summary>
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>Decides which errors are retried; <see langword="null"/> means <see cref="IsTransient"/>.</summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    /// <summary>The clock the waits run on; tests pass a fake one. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Whether an error is transient: <see cref="RateLimitException"/>; an HTTP 408, 429 or 5xx status (from
    /// <see cref="ClientResultException"/> or <see cref="HttpRequestException"/>); a connection failure (an HTTP error
    /// without a status, <see cref="IOException"/>, <see cref="SocketException"/>); a <see cref="TimeoutException"/>
    /// or an HTTP client timeout. Inner exceptions are checked too. Model-output errors (malformed JSON, empty
    /// responses, refusals) and <see cref="ModelCallRefusedException"/> are not transient.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ModelCallRefusedException or RefusalException:
                    return false;
                case RateLimitException or TimeoutException or IOException or SocketException:
                case TaskCanceledException { InnerException: TimeoutException }:
                    return true;
                case ClientResultException client:
                    return client.Status is 0 or 408 or 429 or >= 500;
                case HttpRequestException http:
                    return http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                           || (int)http.StatusCode >= 500;
            }
        }

        return false;
    }

    /// <summary>
    /// How long the provider asked to wait before trying again, from the first error in the chain that says:
    /// <see cref="RateLimitException.RetryAfter"/>, or a <see cref="ClientResultException"/>'s <c>retry-after-ms</c>
    /// or <c>Retry-After</c> (seconds or an HTTP date) response header. <see langword="null"/> when none does.
    /// </summary>
    public static TimeSpan? GetRetryAfter(Exception exception, TimeProvider? timeProvider = null)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is RateLimitException { RetryAfter: { } retryAfter })
            {
                return retryAfter;
            }

            if (e is ClientResultException client && client.GetRawResponse() is { } response)
            {
                if (response.Headers.TryGetValue("retry-after-ms", out var ms)
                    && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
                    && milliseconds >= 0)
                {
                    return TimeSpan.FromMilliseconds(milliseconds);
                }

                if (response.Headers.TryGetValue("Retry-After", out var value) && value is not null)
                {
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && seconds >= 0)
                    {
                        return TimeSpan.FromSeconds(seconds);
                    }

                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var date))
                    {
                        var wait = date - (timeProvider ?? TimeProvider.System).GetUtcNow();
                        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
                    }
                }
            }
        }

        return null;
    }

    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThan(BaseDelay, TimeSpan.Zero, nameof(BaseDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDelay, TimeSpan.Zero, nameof(MaxDelay));
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }

    /// <summary>The wait after failed attempt <paramref name="attempt"/> (1-based), with
    /// <paramref name="jitter"/> in [0, 1).</summary>
    internal TimeSpan GetDelay(int attempt, Exception exception, double jitter)
    {
        if (RespectRetryAfter && GetRetryAfter(exception, TimeProvider) is { } retryAfter)
        {
            return retryAfter < MaxDelay ? retryAfter : MaxDelay;
        }

        var backoff = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        var capped = Math.Min(backoff, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped / 2 + jitter * capped / 2);
    }

    /// <summary>Runs <paramref name="call"/>, retrying transient errors.</summary>
    internal async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> call,
        string kind,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var shouldRetry = ShouldRetry ?? IsTransient;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested
                                      && shouldRetry(e))
            {
#pragma warning disable CA5394 // Backoff jitter; not security sensitive.
                var delay = GetDelay(attempt, e, Random.Shared.NextDouble());
#pragma warning restore CA5394
                ModelCallRetryLog.Retrying(logger, e, kind, attempt, MaxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal static partial class ModelCallRetryLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Transient error in a {Kind} call (attempt {Attempt} of {MaxAttempts}); retrying in {DelayMs} ms")]
    public static partial void Retrying(ILogger logger, Exception exception, string kind, int attempt, int maxAttempts,
        double delayMs);
}

/// <summary>
/// Asks the inner gate until it allows once, then allows every later ask: the retries of one logical model call are
/// not asked about again.
/// </summary>
internal sealed class AllowOnceGate(IModelCallGate inner) : IModelCallGate
{
    private volatile bool _allowed;

    public async ValueTask<bool> AllowAsync(ModelCallInfo modelCall, CancellationToken cancellationToken)
    {
        if (_allowed)
        {
            return true;
        }

        var allowed = await inner.AllowAsync(modelCall, cancellationToken).ConfigureAwait(false);
        _allowed = allowed;
        return allowed;
    }
}

/// <summary>Retries transient embedding errors.</summary>
internal sealed class RetryingEmbedderClient(EmbedderClient inner, ModelCallRetryOptions options, ILogger? logger)
    : EmbedderClient
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public EmbedderClient Inner { get; } = inner;

    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default) =>
        options.ExecuteAsync(ct => Inner.CreateAsync(inputData, ct), "embedding", _logger, cancellationToken);

    public override Task<IReadOnlyList<float[]>> CreateBatchAsync(
        IReadOnlyList<string> inputDataList,
        CancellationToken cancellationToken = default) =>
        options.ExecuteAsync(ct => Inner.CreateBatchAsync(inputDataList, ct), "embedding", _logger, cancellationToken);
}

/// <summary>Retries transient rerank errors.</summary>
internal sealed class RetryingCrossEncoderClient(
    CrossEncoderClient inner,
    ModelCallRetryOptions options,
    ILogger? logger) : CrossEncoderClient
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public CrossEncoderClient Inner { get; } = inner;

    public override Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query,
        IReadOnlyList<string> passages,
        CancellationToken cancellationToken = default) =>
        options.ExecuteAsync(ct => Inner.RankAsync(query, passages, ct), "rerank", _logger, cancellationToken);
}
