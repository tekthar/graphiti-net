// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GraphitiNet.Tests.Prompts;

/// <summary>
/// Loads the golden fixtures that Fixtures/generate_prompt_fixtures.py renders from the upstream sources with real
/// Python formatting.
/// </summary>
internal static class PromptFixtures
{
    private static readonly Lazy<JsonDocument> Contexts = new(() => Load("prompt_contexts.json"));
    private static readonly Lazy<JsonDocument> Renders = new(() => Load("prompt_renders.json"));
    private static readonly Lazy<JsonDocument> Schemas = new(() => Load("response_schemas.json"));

    public static IEnumerable<string> ContextNames =>
        Contexts.Value.RootElement.GetProperty("contexts").EnumerateObject().Select(p => p.Name);

    /// <summary>A context as plain .NET values, built the way pipeline code will build them.</summary>
    public static Dictionary<string, object?> Context(string name)
    {
        var element = Contexts.Value.RootElement.GetProperty("contexts").GetProperty(name);
        return (Dictionary<string, object?>)ToClr(element)!;
    }

    public static (string System, string User) Render(string contextName, string promptName)
    {
        var render = Renders.Value.RootElement.GetProperty("renders").GetProperty(contextName).GetProperty(promptName);
        return (render.GetProperty("system").GetString()!, render.GetProperty("user").GetString()!);
    }

    public static IEnumerable<(string Name, string Schema)> ResponseSchemas =>
        Schemas.Value.RootElement.GetProperty("schemas").EnumerateObject()
            .Select(p => (p.Name, p.Value.GetString()!));

    /// <summary>
    /// JSON to Dictionary/List/string/long/BigInteger/double/bool/null, with <c>{"$datetime": ...}</c> as
    /// <see cref="DateTimeOffset"/>.
    /// </summary>
    private static object? ToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties is [{ Name: "$datetime" } only])
                {
                    return DateTimeOffset.Parse(only.Value.GetString()!, CultureInfo.InvariantCulture);
                }

                var dict = new Dictionary<string, object?>();
                foreach (var p in properties)
                {
                    dict[p.Name] = ToClr(p.Value);
                }

                return dict;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(ToClr).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
                {
                    return element.GetDouble();
                }

                return element.TryGetInt64(out var l) ? l : BigInteger.Parse(raw, CultureInfo.InvariantCulture);
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static JsonDocument Load(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "Fixtures", fileName)));
}
