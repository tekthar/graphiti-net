// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Diagnostics;

namespace GraphitiNet.Telemetry;

/// <summary>
/// Tracing for graphiti-net. Upstream takes a <c>Tracer</c> object; .NET uses an <see cref="ActivitySource"/>
/// instead. To collect the spans, subscribe to <see cref="ActivitySourceName"/>, for example with OpenTelemetry's
/// <c>AddSource("GraphitiNet")</c>. When nothing listens, no activities are created.
/// </summary>
public static class GraphitiTelemetry
{
    /// <summary>The name of the activity source used by graphiti-net.</summary>
    public const string ActivitySourceName = "GraphitiNet";

    /// <summary>The activity source used by graphiti-net.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
}
