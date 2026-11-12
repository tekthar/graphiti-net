// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/errors.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Llm;

/// <summary>The provider's rate limit was exceeded. Upstream: <c>RateLimitError</c>.</summary>
public sealed class RateLimitException : GraphitiException
{
    /// <summary>The default message.</summary>
    public const string DefaultMessage = "Rate limit exceeded. Please try again later.";

    /// <summary>Creates the exception with the default message.</summary>
    public RateLimitException() : base(DefaultMessage) { }

    /// <summary>Creates the exception with a message.</summary>
    public RateLimitException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the provider's exception.</summary>
    public RateLimitException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The LLM refused to generate a response. Upstream: <c>RefusalError</c>.</summary>
public sealed class RefusalException : GraphitiException
{
    /// <summary>Creates the exception with the refusal text.</summary>
    public RefusalException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public RefusalException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The LLM returned an empty response. Upstream: <c>EmptyResponseError</c>.</summary>
public sealed class EmptyResponseException : GraphitiException
{
    /// <summary>Creates the exception with a message.</summary>
    public EmptyResponseException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public EmptyResponseException(string message, Exception innerException) : base(message, innerException) { }
}
