// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>
/// Builds a SQL batch with named parameters (<c>@p0</c>, <c>@p1</c> and so on). Every value goes through a parameter;
/// only identifiers the driver controls are written into the text. Lists travel as one JSON array parameter read with
/// <c>OPENJSON</c>, so a statement never approaches SQL Server's 2,100-parameter limit.
/// </summary>
internal sealed class SqlBuilder
{
    private readonly StringBuilder _sql = new();
    private int _variables;

    public List<SqlParameter> Parameters { get; } = [];

    public SqlBuilder Append(string sql)
    {
        _sql.Append(sql);
        return this;
    }

    /// <summary>Adds a parameter and returns its placeholder.</summary>
    public string Add(SqlParameter parameter)
    {
        parameter.ParameterName = "@p" + Parameters.Count.ToString(CultureInfo.InvariantCulture);
        Parameters.Add(parameter);
        return parameter.ParameterName;
    }

    public string Text(string? value) => Add(Params.Text(value));

    public string Int(int value) => Add(Params.Int(value));

    public string Float(double value) => Add(new SqlParameter { SqlDbType = SqlDbType.Float, Value = value });

    public string Time(DateTimeOffset? value) => Add(Params.Time(value));

    public string Vector(IReadOnlyList<float> value) => Add(Params.Vector(value));

    /// <summary>A JSON text parameter.</summary>
    public string Json(string json) => Add(Params.Json(json));

    /// <summary>A subquery yielding the strings as rows of one column <c>v</c>.</summary>
    public string List(IEnumerable<string> values) =>
        $"(SELECT v FROM OPENJSON({Json(JsonSerializer.Serialize(values.ToArray()))}) WITH (v nvarchar(max) '$'))";

    /// <summary>A subquery yielding ids as rows of one column <c>v</c>, typed like the id columns so lookups seek.</summary>
    public string Ids(IEnumerable<string> values) =>
        $"(SELECT v FROM OPENJSON({Json(JsonSerializer.Serialize(values.ToArray()))}) WITH (v nvarchar(4000) '$'))";

    /// <summary>A fresh T-SQL variable name, unique in this batch.</summary>
    public string Variable(string name) =>
        "@" + name + (_variables++).ToString(CultureInfo.InvariantCulture);

    public override string ToString() => _sql.ToString();

    public SqlCommand ToCommand(Db db)
    {
        var command = db.Command(_sql.ToString());
        command.Parameters.AddRange(Parameters.ToArray());
        return command;
    }
}

/// <summary>Parameter helpers.</summary>
internal static class Params
{
    /// <summary>A Unicode string. Strings up to 4,000 characters share one parameter size, so plans are reused.</summary>
    public static SqlParameter Text(string? value) => new()
    {
        SqlDbType = SqlDbType.NVarChar,
        Size = value is null || value.Length <= 4000 ? 4000 : -1,
        Value = (object?)value ?? DBNull.Value,
    };

    public static SqlParameter Json(string json) => new() { SqlDbType = SqlDbType.NVarChar, Size = -1, Value = json };

    public static SqlParameter Int(int value) => new() { SqlDbType = SqlDbType.Int, Value = value };

    public static SqlParameter Time(DateTimeOffset? value) => new()
    {
        SqlDbType = SqlDbType.DateTimeOffset,
        Value = value.HasValue ? value.Value.ToUniversalTime() : DBNull.Value,
    };

    /// <summary>A native <c>vector</c> parameter (sent in SQL Server's binary vector format).</summary>
    public static SqlParameter Vector(IReadOnlyList<float> value) => new()
    {
        SqlDbType = SqlDbTypeExtensions.Vector,
        Value = new SqlVector<float>(value as float[] ?? value.ToArray()),
    };
}

/// <summary>An open connection and the transaction commands must join, if any (SqlClient requires it).</summary>
internal sealed class Db(SqlConnection connection, SqlTransaction? transaction, int? commandTimeout)
{
    public SqlConnection Connection { get; } = connection;

    public SqlTransaction? Transaction { get; } = transaction;

    /// <summary>A command on the connection, in the transaction, with the context's command timeout.</summary>
    public SqlCommand Command(string sql)
    {
        var command = new SqlCommand(sql, Connection, Transaction);
        if (commandTimeout is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        return command;
    }
}
