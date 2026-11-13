# graphiti-net

[![CI](https://github.com/tekthar/graphiti-net/actions/workflows/ci.yml/badge.svg)](https://github.com/tekthar/graphiti-net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/GraphitiNet.svg)](https://www.nuget.org/packages/GraphitiNet)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

**Temporal knowledge graphs for AI agents, in .NET.**

graphiti-net is a rewrite in C# of [Graphiti](https://github.com/getzep/graphiti), the open-source Python library from
Zep. It turns raw data, such as chat messages, documents and JSON, into a knowledge graph an AI agent can query. The
graph updates the moment new data arrives, remembers when each fact was true, and traces every fact back to the input
it came from.

> **Graphiti is a brand of Zep Software, Inc.** graphiti-net is an independent, community-built rewrite of Graphiti
> in .NET. It is not affiliated with, endorsed by, or sponsored by Zep Software, Inc. For the original Python library,
> go to [getzep/graphiti](https://github.com/getzep/graphiti).

## Status

**Preview** (`0.1.0-preview.1`). The API follows Python Graphiti v0.31.2 and may still change before 1.0. See the
[changelog](CHANGELOG.md) and the [roadmap](docs/roadmap.md).

| Area | State |
| --- | --- |
| `AddEpisodeAsync`: extraction, deduplication, fact resolution, invalidation, summaries | Works; replayed against upstream's recorded answers on every commit |
| `AddTripletAsync`, `RemoveEpisodeAsync`, `ClearDataAsync`, `RetrieveEpisodesAsync` | Works |
| Search: every method, reranker and recipe, filters, center-node rerank | Works |
| Communities, sagas (`SummarizeSagaAsync`), custom entity and edge types | Works |
| Storage: PostgreSQL + pgvector, SQL Server 2025 / Azure SQL, in memory | Works |
| Providers: OpenAI, Azure OpenAI, OpenAI-compatible, any Microsoft.Extensions.AI client | Works |
| `AddEpisodeBulkAsync`, with combined extraction by default | Works; replayed against a recorded upstream `add_episode_bulk` run on every commit |
| Graph building against Python Graphiti on LongMemEval | 100 messages: within upstream's own run-to-run variation ([results](parity/results/2026-11-12-lme-graph-building.md)) |
| Question answering on LongMemEval against Python Graphiti | 24 questions: graphiti-net 21/24 (in memory and PostgreSQL), Python Graphiti 18/24 and 19/24 ([results](parity/results/2026-11-13-lme-qa.md)) |
| DMR and LoCoMo benchmarks | Before 1.0 |
| Consumer requirements (AbpAgent, issues #1 and #2) | Every criterion tested on both drivers ([acceptance](docs/acceptance.md)) |

## Install

```bash
dotnet add package GraphitiNet --prerelease
dotnet add package GraphitiNet.PostgreSql --prerelease   # for PostgreSQL storage
dotnet add package GraphitiNet.SqlServer --prerelease    # for SQL Server 2025 / Azure SQL storage
```

| Package | What it holds |
| --- | --- |
| [`GraphitiNet`](https://www.nuget.org/packages/GraphitiNet) | The library: ingestion, search, providers, the in-memory driver |
| [`GraphitiNet.PostgreSql`](https://www.nuget.org/packages/GraphitiNet.PostgreSql) | PostgreSQL storage through EF Core, with pgvector |
| [`GraphitiNet.SqlServer`](https://www.nuget.org/packages/GraphitiNet.SqlServer) | SQL Server 2025 and Azure SQL storage through EF Core, with the native `vector` type |

Requires .NET 10. Nothing else to install for the in-memory driver; PostgreSQL needs the pgvector extension; SQL
Server needs version 2025 (or Azure SQL) for the `vector` type.

## Quickstart

The same flow as upstream's `examples/quickstart/quickstart_neo4j.py`. Set `OPENAI_API_KEY`, then:

```csharp
using System.Text.Json;
using GraphitiNet;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Search;

// In production: new PostgresDriver(connectionString) from GraphitiNet.PostgreSql.
await using var graphiti = new Graphiti(
    new InMemoryGraphDriver(),
    new OpenAIClient(new LLMConfig(model: "gpt-5-mini", smallModel: "gpt-5-mini")));
await graphiti.BuildIndicesAndConstraintsAsync();

await graphiti.AddEpisodeAsync(
    name: "Freakonomics Radio 0",
    episodeBody: "Kamala Harris is the Attorney General of California. " +
                 "She was previously the district attorney for San Francisco.",
    sourceDescription: "podcast transcript",
    referenceTime: DateTimeOffset.UtcNow,
    source: EpisodeType.Text);
await graphiti.AddEpisodeAsync(
    name: "Freakonomics Radio 1",
    episodeBody: JsonSerializer.Serialize(new { name = "Gavin Newsom", position = "Governor", state = "California" }),
    sourceDescription: "podcast metadata",
    referenceTime: DateTimeOffset.UtcNow,
    source: EpisodeType.Json);

// Facts, by hybrid search.
var results = await graphiti.SearchAsync("Who was the California Attorney General?");
foreach (var fact in results)
{
    Console.WriteLine($"{fact.Fact} (valid from {fact.ValidAt})");
}

// The same search, reranked by graph distance to an entity.
var reranked = await graphiti.SearchAsync(
    "Who was the California Attorney General?", centerNodeUuid: results[0].SourceNodeUuid);

// Entities, with a search recipe.
var nodes = await graphiti.SearchAsync("California Governor", SearchConfigRecipes.NodeHybridSearchRrf with { Limit = 5 });
foreach (var node in nodes.Nodes)
{
    Console.WriteLine($"{node.Name}: {node.Summary}");
}
```

The full version, with real output, is in [samples/Quickstart](samples/Quickstart). The Python original and this
version are side by side in [API compatibility](docs/api-compatibility.md#quickstart-side-by-side).

Python Graphiti users will find the same names: `add_episode` is `AddEpisodeAsync`, `search_` is
`SearchAsync(query, config)`, `NODE_HYBRID_SEARCH_RRF` is `SearchConfigRecipes.NodeHybridSearchRrf`, and parameters
keep their names in `camelCase`.

## Storage

| Driver | Package | Use it for |
| --- | --- | --- |
| `PostgresDriver` | `GraphitiNet.PostgreSql` | Production. Tables in a PostgreSQL database you already run, through EF Core; pgvector for vectors, BM25 keyword search in SQL. Can live in your own `DbContext` and migrations. See [docs/postgresql.md](docs/postgresql.md). |
| `SqlServerDriver` | `GraphitiNet.SqlServer` | Production on SQL Server 2025 or Azure SQL, through EF Core; the native `vector` type for vectors, BM25 keyword ranking as upstream's Lucene (no full-text search needed). Can live in your own `DbContext` and migrations. See [docs/sqlserver.md](docs/sqlserver.md). |
| `InMemoryGraphDriver` | `GraphitiNet` | Zero-install: tests, demos, small apps. BM25 keyword ranking. Can save to and load from a JSON file. |

No graph database is needed, and none is supported. Other drivers implement `IGraphDriver`; see
[docs/drivers.md](docs/drivers.md).

## Providers

| Provider | Classes |
| --- | --- |
| OpenAI | `OpenAIClient`, `OpenAIEmbedder`, `OpenAIRerankerClient` (the defaults; read `OPENAI_API_KEY`) |
| Azure OpenAI | `AzureOpenAILLMClient`, `AzureOpenAIEmbedderClient` |
| OpenAI-compatible endpoints | `OpenAIGenericClient`, or `LLMConfig.BaseUrl` / `OPENAI_BASE_URL` |
| Anything with a Microsoft.Extensions.AI client | `ChatClientLLMClient` over an `IChatClient`, `EmbeddingGeneratorEmbedder` over an `IEmbeddingGenerator<string, Embedding<float>>`, `ChatClientCrossEncoder` |

The library's default model is upstream's, which may not be available on every account: pass a model in
`LLMConfig`, as above.

## graphiti-net additions

Opt-in or additive. With the defaults, behaviour matches upstream. Details in
[API compatibility](docs/api-compatibility.md#graphiti-net-additions).

| Addition | What it does |
| --- | --- |
| Episode access filter (`IEpisodeAccessFilter`) | Search hides facts, entities and episodes behind episodes the caller may not read, before the limit, so the limit is filled from visible results |
| Strict removal (`RemoveEpisodeAsync(uuid, strict: true)`) | Leaves no text from the episode in the graph: facts, attributes, summaries, communities and saga summaries |
| One write at a time per group (`IGroupLock`) | Two episodes added to one group at once cannot both create the same entity; plug in a distributed lock for several servers |
| Call context (`GraphitiCallContext`) | Every model call knows its operation, group, episode, metadata and prompt, so a host can route or audit it; token usage per call |
| Model-call gate (`IModelCallGate`) | Asked before each LLM, embedding and rerank call; a refusal stops the operation, and the episode can be processed later |

## Parity and evaluation

Python Graphiti on the same input is the definition of correct. See [parity/README.md](parity/README.md).

- **Replay on every commit.** Upstream's prompts and model answers are recorded once; the port replays them and must
  produce the same entities, facts, validity dates and invalidations. Every dataset matches on every driver: in memory, on
  PostgreSQL and on SQL Server.
- **Graph building on LongMemEval** with live models, judged by an LLM with upstream's own evaluation: graphiti-net
  scored 0.472 against a Python Graphiti baseline, where a second Python Graphiti run scored 0.361 and a graph with
  every fact removed 0.111. A small sample (36 messages); see
  [parity/results](parity/results/2026-11-12-lme-graph-building.md).

The port follows **Graphiti [v0.31.2](https://github.com/getzep/graphiti/releases/tag/v0.31.2)**.

## Documentation

- [API compatibility](docs/api-compatibility.md): every Python Graphiti call and its graphiti-net equivalent, and
  where behaviour differs.
- [How Graphiti works](docs/how-graphiti-works.md): episodes, entities, facts, time and search.
- [PostgreSQL storage](docs/postgresql.md): setup, your own DbContext, migrations, embedding size, languages.
- [SQL Server storage](docs/sqlserver.md): SQL Server 2025 and Azure SQL; setup, your own DbContext, keyword ranking.
- [Graph drivers](docs/drivers.md): the storage contract and how to add a driver.
- [Custom types](docs/custom-types.md): your own entity and edge types as C# classes.
- [Acceptance tests](docs/acceptance.md): each requirement of the first consumer, AbpAgent, and the test that checks
  it.
- [Requirements](docs/requirements.md) and [roadmap](docs/roadmap.md).
- [Development environment](docs/development.md).

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first, especially the porting rules.
Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Licence

[Apache License 2.0](LICENSE), the same licence as Graphiti. Parts of graphiti-net are translations of Graphiti's
Python source, prompts and tests; see [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Trademarks

"Graphiti" and "Zep" are trademarks of Zep Software, Inc. This project uses the names only to say which project it
rewrites and aims to be compatible with. The Apache License does not grant permission to use Zep's trademarks.
