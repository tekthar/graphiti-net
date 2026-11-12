// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/prompts/models.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Prompts;

/// <summary>
/// Builds a prompt from a context. Contexts are dictionaries keyed by the same snake_case keys upstream uses
/// (<c>episode_content</c>, <c>previous_episodes</c>, ...); see <see cref="PythonText"/> for how values render.
/// </summary>
/// <param name="context">The values the prompt is built from.</param>
/// <returns>The system and user text.</returns>
public delegate ChatPrompt ChatPromptBuilder(IReadOnlyDictionary<string, object?> context);

/// <summary>The system and user text of a prompt, as every prompt builder returns it.</summary>
/// <param name="System">The system message's text, without the <see cref="PromptHelpers.DoNotEscapeUnicode"/>
/// note.</param>
/// <param name="User">The user message's text.</param>
public sealed record ChatPrompt(string System, string User)
{
    /// <summary>Builds a prompt from exactly one system message followed by one user message.</summary>
    /// <exception cref="ArgumentException">The messages are not a system message followed by a user message.</exception>
    public static ChatPrompt FromMessages(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count != 2)
        {
            throw new ArgumentException("ChatPrompt requires exactly two messages", nameof(messages));
        }

        if (messages[0].Role != "system" || messages[1].Role != "user")
        {
            throw new ArgumentException(
                "ChatPrompt requires a system message followed by a user message", nameof(messages));
        }

        return new ChatPrompt(messages[0].Content, messages[1].Content);
    }

    /// <summary>
    /// Renders the prompt as the messages sent to the LLM. By default the system message ends with
    /// <see cref="PromptHelpers.DoNotEscapeUnicode"/> so providers keep non-ASCII characters as they are.
    /// </summary>
    /// <param name="appendUnicodeNote">Append the note to the system message.</param>
    /// <returns>New messages, which the LLM client may change.</returns>
    public IReadOnlyList<Message> AsMessages(bool appendUnicodeNote = true) =>
    [
        new Message("system", appendUnicodeNote ? System + PromptHelpers.DoNotEscapeUnicode : System),
        new Message("user", User),
    ];
}

/// <summary>
/// The fixed response schema of a prompt. Only prompt text can be overridden; schemas cannot.
/// </summary>
/// <param name="Name">The prompt name, such as <c>extract_nodes.extract_message</c>.</param>
/// <param name="ResponseModel">The type the response is parsed into, or <see langword="null"/> when the prompt has
/// no fixed schema.</param>
/// <param name="DynamicSchema">The caller supplies the schema for each call (attribute extraction for custom
/// entity and edge types).</param>
public sealed record PromptSpec(string Name, Type? ResponseModel = null, bool DynamicSchema = false);
