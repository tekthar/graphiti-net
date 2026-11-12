# graphiti-net

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

Early development. The core records and utilities are in place; storage, the ingestion pipeline and search are
next. See the [roadmap](docs/roadmap.md) and the requirements the port is built against:
[docs/requirements.md](docs/requirements.md).

## What it will do

- **Memory that updates as you go**: each new episode merges into the graph straight away. No batch rebuilds.
- **Time awareness**: every fact records when it became true and when it stopped being true. Old facts are closed, not
  deleted, so you can ask what is true now or what was true on any past date.
- **Provenance**: every fact points back to the episode that produced it.
- **Fast hybrid search**: semantic, keyword (BM25) and graph search, combined and reranked, with no LLM call at query
  time.
- **Your own types**: describe the entities and relationships you care about as C# classes or records, or let the
  structure emerge from the data.
- **No graph database needed**: the graph lives in the database you already run, through EF Core. PostgreSQL with
  pgvector is the default, and SQL Server comes next.
- **Pluggable providers**: LLMs and embedders through Microsoft.Extensions.AI.

It's a library, not a server. You bring the database, the LLM and the embedder, and build your service around it.

## How we'll know it works

The port is correct when it gives the same results as Python Graphiti on the same inputs:

1. Graphiti's own test suite, ported to C#.
2. Parity runs: the same episodes through both, with the same models and settings, comparing entities, facts,
   invalidations and search results.
3. Benchmarks per release (LongMemEval, DMR, LoCoMo): the port scores within 1–2 points of Python Graphiti.

## Upstream version

The port follows one tagged Graphiti release at a time and records which one. The baseline is
[v0.31.2](https://github.com/getzep/graphiti/releases/tag/v0.31.2).

## Documentation

- [Requirements](docs/requirements.md): data model, ingestion pipeline, temporal model, search, storage, providers
  and verification.
- [Graph drivers](docs/drivers.md): the storage contract, the in-memory driver, and how to add and test a driver.
- [PostgreSQL storage](docs/postgresql.md): setup, hosting the tables in your own DbContext, migrations, embedding
  size, languages and how keyword ranking differs from upstream.
- [Roadmap](docs/roadmap.md): the phases of the port and the first pull requests.
- [Development environment](docs/development.md): services, settings and the Claude Code cloud environment.

## Contributing

Issues and pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first, especially the porting rules.
Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

[Apache License 2.0](LICENSE), the same licence as Graphiti. Parts of graphiti-net will be translations of Graphiti's
Python source, prompts and tests; see [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Trademarks

"Graphiti" and "Zep" are trademarks of Zep Software, Inc. This project uses the names only to say which project it
rewrites and aims to be compatible with. The Apache License does not grant permission to use Zep's trademarks.
