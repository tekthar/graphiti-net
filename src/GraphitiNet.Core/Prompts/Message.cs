// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/models.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Prompts;

/// <summary>A message sent to the LLM. <see cref="Content"/> is mutable because the LLM client appends to it, as
/// upstream does.</summary>
public sealed class Message(string role, string content)
{
    /// <summary><c>system</c>, <c>user</c> or <c>assistant</c>.</summary>
    public string Role { get; } = role;

    /// <summary>The text.</summary>
    public string Content { get; set; } = content;
}
