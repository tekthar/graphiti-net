// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search.py (_trace_phase) and graphiti_core/tracer.py (Graphiti v0.31.2) and
// changed for .NET.

using System.Diagnostics;

namespace GraphitiNet.Search;

/// <summary>
/// Search tracing through <see cref="System.Diagnostics.ActivitySource"/>, which OpenTelemetry picks up when the
/// source <see cref="SourceName"/> is registered. Span names and attributes match upstream's
/// (<c>search.edge_search</c>, <c>search.edge_search.rerank</c> and so on).
/// </summary>
public static class SearchTracing
{
    /// <summary>The activity source name: <c>GraphitiNet.Search</c>.</summary>
    public const string SourceName = "GraphitiNet.Search";

    internal static ActivitySource Source { get; } = new(SourceName);

    /// <summary>Starts a phase span with attributes. Dispose it to end the span; call <see cref="Phase.Ok"/> on
    /// success or <see cref="Phase.Fail"/> on error, as upstream's <c>_trace_phase</c>.</summary>
    internal static Phase Start(string name, params (string Key, object? Value)[] attributes)
    {
        var activity = Source.StartActivity(name);
        var phase = new Phase(activity);
        phase.Add(attributes);
        return phase;
    }

    /// <summary>Runs <paramref name="body"/> inside a span, marking it ok or recording the error.</summary>
    internal static async Task<T> TraceAsync<T>(
        string name,
        (string Key, object? Value)[] attributes,
        Func<Phase, Task<T>> body)
    {
        using var phase = Start(name, attributes);
        try
        {
            var result = await body(phase).ConfigureAwait(false);
            phase.Ok();
            return result;
        }
        catch (Exception ex)
        {
            phase.Fail(ex);
            throw;
        }
    }

    /// <summary>Runs <paramref name="body"/> inside a span, marking it ok or recording the error.</summary>
    internal static T Trace<T>(string name, (string Key, object? Value)[] attributes, Func<Phase, T> body)
    {
        using var phase = Start(name, attributes);
        try
        {
            var result = body(phase);
            phase.Ok();
            return result;
        }
        catch (Exception ex)
        {
            phase.Fail(ex);
            throw;
        }
    }

    /// <summary>A traced phase; a no-op when nobody listens.</summary>
    internal readonly struct Phase(Activity? activity) : IDisposable
    {
        public void Add(params (string Key, object? Value)[] attributes)
        {
            if (activity is null)
            {
                return;
            }

            foreach (var (key, value) in attributes)
            {
                if (value is not null)
                {
                    activity.SetTag(key, value);
                }
            }
        }

        public void Ok() => activity?.SetStatus(ActivityStatusCode.Ok);

        public void Fail(Exception exception)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddException(exception);
        }

        public void Dispose() => activity?.Dispose();
    }
}
