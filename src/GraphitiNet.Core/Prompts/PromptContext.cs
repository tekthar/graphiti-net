// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Prompts;

/// <summary>Reads prompt context values the way upstream's f-strings do.</summary>
internal static class PromptContext
{
    /// <summary><c>context['key']</c>: the value, which must be present (it may be <see langword="null"/>).</summary>
    public static object? Get(IReadOnlyDictionary<string, object?> context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"The prompt context has no '{key}' value.");
    }

    /// <summary><c>context.get('key', default)</c>.</summary>
    public static object? GetOrDefault(IReadOnlyDictionary<string, object?> context, string key, object? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetValue(key, out var value) ? value : defaultValue;
    }

    /// <summary><c>{context['key']}</c> in an f-string.</summary>
    public static string Str(IReadOnlyDictionary<string, object?> context, string key) =>
        PythonText.Str(Get(context, key));

    /// <summary><c>{to_prompt_json(context['key'])}</c> in an f-string.</summary>
    public static string Json(IReadOnlyDictionary<string, object?> context, string key) =>
        PromptHelpers.ToPromptJson(Get(context, key));
}
