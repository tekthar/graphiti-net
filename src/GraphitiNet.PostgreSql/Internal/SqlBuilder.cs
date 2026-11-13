// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// Builds a SQL statement with positional parameters (<c>$1</c>, <c>$2</c> and so on). Every value goes through a
/// parameter; only identifiers the driver controls are written into the text.
/// </summary>
internal sealed class SqlBuilder
{
    private readonly StringBuilder _sql = new();

    public List<NpgsqlParameter> Parameters { get; } = [];

    public SqlBuilder Append(string sql)
    {
        _sql.Append(sql);
        return this;
    }

    /// <summary>Adds a parameter and returns its placeholder.</summary>
    public string Param(object? value, NpgsqlDbType type)
    {
        Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value, NpgsqlDbType = type });
        return "$" + Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string TextArray(IEnumerable<string> values) => Param(values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);

    public string Text(string? value) => Param(value, NpgsqlDbType.Text);

    public string Int(int value) => Param(value, NpgsqlDbType.Integer);

    public string Vector(IReadOnlyList<float> value) =>
        "CAST(" + Param(value as float[] ?? value.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Real) + " AS vector)";

    public override string ToString() => _sql.ToString();

    public NpgsqlCommand ToCommand(NpgsqlConnection connection)
    {
        var command = new NpgsqlCommand(_sql.ToString(), connection);
        command.Parameters.AddRange(Parameters.ToArray());
        return command;
    }
}

/// <summary>Parameter helpers for statements with fixed positional parameters.</summary>
internal static class Params
{
    public static NpgsqlParameter Text(string? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter TextArray(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    public static NpgsqlParameter Time(DateTimeOffset value) =>
        new() { Value = value.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = value.HasValue ? value.Value.ToUniversalTime() : DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public static NpgsqlParameter Jsonb(string? json) =>
        new() { Value = (object?)json ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb };

    public static NpgsqlParameter Embedding(float[]? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Real };

    public static NpgsqlParameter IntArray(int[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer };

    public static NpgsqlParameter Int(int value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Integer };
}
