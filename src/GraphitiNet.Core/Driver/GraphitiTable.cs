// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Driver;

/// <summary>
/// A table a database driver creates and maintains, as listed by the driver's <c>GraphitiSchema.Tables</c>: for a
/// host's data map, backup or retention policy, or a test that lists the schema.
/// </summary>
/// <param name="Schema">The database schema.</param>
/// <param name="Name">The table name, with the table prefix.</param>
/// <param name="RecordType">The EF Core entity type the table holds (for example the driver's
/// <c>EntityNodeRecord</c>), or <see langword="null"/> for a table outside the EF Core model, such as PostgreSQL's
/// <c>search_statistics</c>.</param>
public sealed record GraphitiTable(string Schema, string Name, Type? RecordType)
{
    /// <summary>The schema-qualified name, unquoted: <c>schema.name</c>.</summary>
    public string QualifiedName => Schema + "." + Name;
}
