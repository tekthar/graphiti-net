# GraphitiNet.PostgreSql

PostgreSQL storage for [graphiti-net](https://github.com/tekthar/graphiti-net), a C# rewrite of Python Graphiti. The
graph lives in nine ordinary tables, through EF Core, with [pgvector](https://github.com/pgvector/pgvector) for
vector search and BM25 keyword search in SQL, with the same tokenisation and ranking as upstream's Lucene indexes. No graph database is needed.

**Preview.** The API and the table layout may still change before 1.0.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent, community-built rewrite. It is not
> affiliated with, endorsed by, or sponsored by Zep Software, Inc.

## Requirements

PostgreSQL 13 or later with the pgvector extension (0.5 or later). Most managed services offer it.

## Install

```bash
dotnet add package GraphitiNet
dotnet add package GraphitiNet.PostgreSql
```

## Quickstart

```csharp
using GraphitiNet;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Llm;
using GraphitiNet.Models;

var driver = new PostgresDriver(
    "Host=localhost;Database=graphiti;Username=app;Password=...",
    new PostgresGraphOptions { Schema = "graphiti", EmbeddingDimension = 1024 }); // match your embedder

await using var graphiti = new Graphiti(
    driver,
    new OpenAIClient(new LLMConfig(model: "gpt-5-mini", smallModel: "gpt-5-mini")));

// Once, or at every start-up: creates the extension, schema, tables and indexes. Idempotent.
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

- [PostgreSQL storage guide](https://github.com/tekthar/graphiti-net/blob/main/docs/postgresql.md): hosting the
  tables, migrations, embedding size, languages, keyword ranking
- [graphiti-net README](https://github.com/tekthar/graphiti-net#readme)

## Licence

Apache-2.0. See `NOTICE` and `THIRD-PARTY-NOTICES.md` in this package. "Graphiti" and "Zep" are trademarks of Zep
Software, Inc.
