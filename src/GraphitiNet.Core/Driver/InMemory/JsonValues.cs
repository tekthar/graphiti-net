// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text.Json;

namespace GraphitiNet.Driver.InMemory;

/// <summary>
/// Normalises attribute and metadata values to JSON-shaped CLR values, the way a database storing them as JSON would
/// return them: <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>,
/// <see langword="null"/>, <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/>, and
/// <see cref="List{T}"/> of <see cref="object"/>.
/// </summary>
internal static class JsonValues
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    /// <summary>A normalised deep copy of a dictionary, or <see langword="null"/>.</summary>
    public static Dictionary<string, object?>? Normalize(IDictionary<string, object?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = Normalize(value);
        }

        return result;
    }

    /// <summary>A normalised deep copy of one value.</summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        long l => l,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        sbyte b => (long)b,
        ushort u => (long)u,
        uint u => (long)u,
        double d => d,
        float f => double.Parse(f.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        JsonElement element => FromElement(element),
        IDictionary<string, object?> map => Normalize(map),
        IEnumerable<object?> list => list.Select(Normalize).ToList(),
        _ => FromElement(JsonSerializer.SerializeToElement(value, value.GetType(), SerializerOptions)),
    };

    private static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => FromElement(g.Last().Value), StringComparer.Ordinal),
        _ => throw new NotSupportedException(
            string.Create(CultureInfo.InvariantCulture, $"JSON value kind {element.ValueKind}")),
    };

    /// <summary>Converts a number-like value to <see cref="double"/>, or <see langword="null"/> if it is not one.</summary>
    public static double? AsNumber(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte b => b,
        ushort u => u,
        uint u => u,
        ulong u => u,
        double d => d,
        float f => f,
        decimal m => (double)m,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        _ => null,
    };
}
