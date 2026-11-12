// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text.Json;

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>Converts attribute dictionaries to and from the JSON stored in JSON text columns.</summary>
internal static class JsonValues
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static string Serialize(IDictionary<string, object?>? values) =>
        values is null ? "{}" : JsonSerializer.Serialize(values, SerializerOptions);

    public static string? SerializeNullable(IDictionary<string, object?>? values) =>
        values is null ? null : JsonSerializer.Serialize(values, SerializerOptions);

    /// <summary>Serializes one scalar for a JSON comparison.</summary>
    public static string SerializeValue(object? value) => JsonSerializer.Serialize(value, SerializerOptions);

    /// <summary>Parses a JSON object into plain .NET values: string, long, double, bool, null, lists and
    /// dictionaries.</summary>
    public static Dictionary<string, object?> Deserialize(string? json)
    {
        var result = new Dictionary<string, object?>();
        if (string.IsNullOrEmpty(json))
        {
            return result;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = ToPlain(property.Value);
        }

        return result;
    }

    public static Dictionary<string, object?>? DeserializeNullable(string? json) =>
        json is null ? null : Deserialize(json);

    private static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        // Cast each branch to object, or the conditional would widen a long to double.
        JsonValueKind.Number => element.TryGetInt64(out var l)
            ? (object)l
            : (object)double.Parse(element.GetRawText(), CultureInfo.InvariantCulture),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlain(p.Value)),
        _ => null,
    };
}
