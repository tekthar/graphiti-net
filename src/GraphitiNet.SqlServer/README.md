# GraphitiNet.SqlServer

SQL Server 2025 and Azure SQL storage for [graphiti-net](https://github.com/tekthar/graphiti-net), a C# rewrite of
Python Graphiti. The graph lives in ordinary tables, through EF Core, with the native `vector` type and
`VECTOR_DISTANCE` for vector search and BM25 keyword search over a keyword index the driver maintains. No graph
database is needed.

**Preview.** The API and the table layout may still change before 1.0.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent, community-built rewrite. It is not
> affiliated with, endorsed by, or sponsored by Zep Software, Inc.

## Requirements

SQL Server 2025 (any edition, including the Linux container and Express) or Azure SQL Database / Managed Instance:
anything with the `vector` data type. Full-text search is not needed.

## Install

```bash
dotnet add package GraphitiNet --prerelease
dotnet add package GraphitiNet.SqlServer --prerelease
```

## Quickstart

```csharp
using GraphitiNet;
using GraphitiNet.Driver.SqlServer;
using GraphitiNet.Llm;
using GraphitiNet.Models;

var driver = new SqlServerDriver(
    "Server=localhost;Database=graphiti;User Id=app;Password=...;TrustServerCertificate=True",
    new SqlServerGraphOptions { Schema = "graphiti", EmbeddingDimension = 1024 }); // match your embedder

await using var graphiti = new Graphiti(
    driver,
    new OpenAIClient(new LLMConfig(model: "gpt-5-mini", smallModel: "gpt-5-mini")));

// Once, or at every start-up: creates the schema, tables and indexes. Idempotent.
await graphiti.BuildIndicesAndConstraintsAsync();

await graphiti.AddEpisodeAsync(
    name: "note",
    episodeBody: "Alice moved from Berlin to Lisbon in March.",
    sourceDescription: "chat",
    referenceTime: DateTimeOffset.UtcNow,
    source: EpisodeType.Message,
    groupId: "tenant-1");

var facts = await graphiti.SearchAsync("Where does Alice live?", groupIds: ["tenant-1"]);
```

The tables can also live in your own `DbContext` (`modelBuilder.AddGraphiti(options)`) and your own EF Core
migrations, with a schema name and table prefix of your choice.

## Learn more

- [SQL Server storage guide](https://github.com/tekthar/graphiti-net/blob/main/docs/sqlserver.md): hosting the
  tables, migrations, embedding size, keyword ranking, performance
- [graphiti-net README](https://github.com/tekthar/graphiti-net#readme)

## Licence

Apache-2.0. See `NOTICE` and `THIRD-PARTY-NOTICES.md` in this package. "Graphiti" and "Zep" are trademarks of Zep
Software, Inc.
