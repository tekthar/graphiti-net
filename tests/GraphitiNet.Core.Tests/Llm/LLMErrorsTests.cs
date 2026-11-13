// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/llm_client/test_errors.py (Graphiti v0.31.2) and changed for .NET. Upstream's "message is
// required" tests are compile-time checks in C#: RefusalException and EmptyResponseException have no parameterless
// constructor.

using GraphitiNet.Llm;

namespace GraphitiNet.Tests.Llm;

public class LLMErrorsTests
{
    [Fact]
    public void RateLimit_DefaultMessage()
    {
        var error = new RateLimitException();
        Assert.Equal("Rate limit exceeded. Please try again later.", error.Message);
        Assert.IsAssignableFrom<GraphitiException>(error);
    }

    [Fact]
    public void RateLimit_CustomMessage()
    {
        var error = new RateLimitException("Custom rate limit message");
        Assert.Equal("Custom rate limit message", error.Message);
    }

    [Fact]
    public void Refusal_MessageAssignment()
    {
        const string message = "The LLM refused to respond to this prompt.";
        var error = new RefusalException(message);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void EmptyResponse_MessageAssignment()
    {
        const string message = "The LLM returned an empty response.";
        var error = new EmptyResponseException(message);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void ErrorsHaveNoParameterlessConstructor()
    {
        Assert.Null(typeof(RefusalException).GetConstructor(Type.EmptyTypes));
        Assert.Null(typeof(EmptyResponseException).GetConstructor(Type.EmptyTypes));
    }
}
