# Requirements for a .NET port of Graphiti

Status: draft, November 2026.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent rewrite of Graphiti in .NET. It is not
> affiliated with, endorsed by, or sponsored by Zep Software, Inc. "Graphiti" in this document means the upstream
> Python library at [getzep/graphiti](https://github.com/getzep/graphiti).

## Overview

Graphiti is an open-source Python library (Apache 2.0) that turns raw data into a temporal knowledge graph an AI agent
can query. It is the graph engine inside Zep. A .NET port must reproduce its library, not its servers: you supply the
database, LLM and embedder, and build the service around it.

Unlike upstream, the port stores the graph in a relational database through EF Core by default, so it needs no graph
database. See [Storage](#storage-ef-core-by-default).

What it gives an agent:

- **Memory that updates incrementally:** new data merges into the graph immediately, with no batch rebuild.
- **Time awareness:** every fact records when it became true and when it stopped being true.
- **Provenance:** every fact traces back to the raw input that produced it.
- **Fast hybrid search:** semantic, keyword and graph search combined, typically under a second, with no LLM call
  needed at query time.

The upstream project is popular (about 32k GitHub stars in November 2026) and very active, so the port tracks a pinned
upstream version.

## Core data model

The port needs eight record types. Everything derived points back to an episode.

| Record | What it holds | Key fields to port |
| --- | --- | --- |
| Episode | A raw input as ingested: text, a chat message or JSON | Content, source type, reference time, group id |
| Entity node | A person, product, policy or concept | Name, evolving summary, name embedding, labels, custom attributes |
| Entity edge (fact) | A triplet: entity, relationship, entity | Fact text, fact embedding, validity window, links to source episodes |
| Episodic edge | Links an episode to each entity it mentions | Episode id, entity id |
| Community | A cluster of related entities with a summary | Member entities, summary text |
| Community edge | Links a community to a member entity | Community id, entity id |
| Saga | An ordered chain of episodes, such as one conversation, with a summary | Summary, first and last episode, summary watermarks |
| Has-episode and next-episode edges | Put an episode in a saga, and chain the saga's episodes in order | Saga id, episode ids |

**Group id** partitions the graph, for example one graph per user or tenant. Every read and write is scoped by it, so
it is the natural hook for multi-tenancy in .NET: in EF Core, a global query filter on the group id column.

## Ingestion pipeline

Adding one episode runs these steps in order. Most need an LLM call returning structured JSON, which is where the cost
and the porting difficulty sit.

1. **Save the episode** as the raw source of truth.
2. **Extract entities** from the episode, using recent episodes as context.
3. **Deduplicate entities:** match each against existing nodes (embedding + name search, then an LLM decision) and
   merge duplicates.
4. **Extract facts** (edges) between the resolved entities.
5. **Resolve facts:** find existing facts that duplicate or contradict each new one.
6. **Invalidate contradicted facts:** set their end date instead of deleting them.
7. **Update entity summaries** and any custom attributes.
8. **Embed and save** nodes, edges and episode links.

Also required:

- **Bulk ingest** of many episodes at once, for backfills.
- **Concurrency limit** on LLM calls (upstream default 20, `DEFAULT_SEMAPHORE_LIMIT`), to avoid provider rate limits.
- **Optional community building:** cluster entities and summarise each cluster.
- **Delete an episode** and clean up what only it produced.
- **Overridable prompts** per instance, with fixed response schemas, and optional routing of individual prompts to
  different models (for example a cheap model for deduplication).

## Temporal model

Graphiti is bi-temporal: it tracks when a fact was true in the world and when the system learned it. Facts are never
overwritten.

| Field on a fact | Meaning |
| --- | --- |
| Valid at | When the fact became true in the real world |
| Invalid at | When it stopped being true (empty while still true) |
| Created at | When Graphiti recorded it |
| Expired at | When Graphiti marked it superseded |

Example: "Alice is on the Pro plan" gets an end date when "Alice upgraded to Enterprise" arrives, and both stay
queryable. The port must support asking what is true now and what was true at any past date. The episode's reference
time anchors relative phrases like "last week".

## Search and retrieval

Search combines three methods and then reranks, with no LLM summarisation at query time.

| Part | Options to port |
| --- | --- |
| Search methods | Semantic (cosine similarity on embeddings), keyword (BM25 full-text), graph traversal (breadth-first from given nodes) |
| Targets | Facts (edges), entities (nodes), episodes, communities |
| Rerankers | Reciprocal rank fusion, maximal marginal relevance, distance from a centre node, episode mentions, cross-encoder |
| Filters | Group ids, entity and edge types, date ranges on the temporal fields |
| Recipes | Predefined combinations, for example hybrid facts with fusion or nodes reranked by distance |

The simple API is "search facts for this query", optionally centred on one node. The advanced API takes a full search
configuration.

## Custom entity and edge types

Developers can define their own ontology, or let structure emerge from the data.

- **Prescribed:** entity and edge types defined upstream as Pydantic models, for example a Customer with a plan field.
  The LLM extracts those attributes into the node.
- **Learned:** no types given; Graphiti labels entities generically.
- **Edge type map:** which edge types are allowed between which entity types.
- **Excluded types:** entity types to skip during extraction.

In .NET this maps naturally to C# classes or records with attributes, with JSON Schema generated from them for
structured LLM output.

## Pluggable providers

Four interfaces sit behind the core, each swappable.

| Interface | Upstream implementations | Note for .NET |
| --- | --- | --- |
| LLM client | OpenAI (default), Azure OpenAI, Anthropic, Gemini, Groq, any OpenAI-compatible endpoint (Ollama, vLLM, LM Studio) | Must return reliable structured JSON; small local models often fail |
| Embedder | OpenAI, Azure OpenAI, Gemini, Voyage, OpenAI-compatible | Microsoft.Extensions.AI covers most of these |
| Cross-encoder (reranker) | OpenAI, Gemini, local model | Optional |
| Graph driver | Neo4j 5.26, FalkorDB, Amazon Neptune; Kuzu deprecated | Default is EF Core on PostgreSQL with pgvector (see [Storage](#storage-ef-core-by-default)). Needs vector and full-text search plus short graph traversals |

Using Microsoft.Extensions.AI abstractions for the LLM and embedder would make the port feel native and
provider-neutral.

## Storage: EF Core by default

The port stores the graph in a relational database through EF Core. Teams that already run PostgreSQL or SQL Server
don't have to add, learn and operate a graph database: agent memory becomes a few more tables in a database they
already back up and monitor.

This works because Graphiti's queries are shallow. Upstream caps graph search at 3 hops (`MAX_SEARCH_DEPTH` in
`graphiti_core/search/search_utils.py`). Everything else is lookups by id, vector search and keyword search.

| What Graphiti needs | PostgreSQL (default) | SQL Server 2025 / Azure SQL (second) |
| --- | --- | --- |
| Episodes, entities, facts, communities | Tables with foreign keys | Same |
| Vector search | pgvector with HNSW indexes, through `Pgvector.EntityFrameworkCore` | Native `vector` type and `VectorDistance` in EF Core 10 |
| Keyword search | Built-in full-text search (`tsvector`); a BM25 extension such as ParadeDB `pg_search` where the host allows it | Full-text index |
| Graph search (up to 3 hops) | Recursive SQL query | Recursive SQL query |
| Group id isolation | A group id column and an EF Core global query filter; optionally row-level security | Same |

Requirements:

- **PostgreSQL first, SQL Server second.** Keep the graph-driver interface so other backends can be added later.
  A Neo4j (or other graph database) driver is not a requirement and not in the backlog.
- **Two ways to host the tables:** a `ModelBuilder` extension, so the host adds them to its own `DbContext` and
  migrations, or a standalone `DbContext` in its own schema.
- **Migrations, not first-run setup:** EF Core migrations create the tables, vector indexes, full-text indexes and
  uniqueness constraints.
- **One search module per database:** LINQ covers reads and writes. Vector search, full-text search and recursive
  graph queries are written in SQL for each database.
- **Embedding size is part of the schema:** the vector column's dimension is set in the migration, so changing the
  embedding model needs a migration. pgvector's HNSW index supports up to 2,000 dimensions for `vector`; larger models
  (for example 3,072 dimensions) need `halfvec`.

Known differences from upstream:

- **No upstream SQL driver to copy.** Upstream ships only graph-database drivers (Neo4j, FalkorDB, Amazon Neptune,
  Kuzu), so every Cypher query is rewritten in SQL. These queries need the most testing.
- **Keyword ranking differs.** Neo4j ranks full-text matches with BM25 (through Lucene); PostgreSQL's built-in ranking
  does not. Search results will differ slightly from Python Graphiti. Keyword search is one input to the fused
  ranking, so the effect should be small, and the LongMemEval gate catches a real drop.
- **Very large graphs.** A graph database pulls ahead on very large, densely connected graphs searched many hops deep.
  Graphiti's capped queries mostly avoid this.

Not chosen: Apache AGE adds Cypher to PostgreSQL and would let the port reuse upstream's queries, but many managed
PostgreSQL services don't offer it.

## Servers and integrations

Upstream ships two reference servers. They are examples, not production code, so they are optional for a first port.

- **REST server** (FastAPI): add episodes and messages, search, get and delete data by group. In .NET this is an
  ASP.NET Core minimal API.
- **MCP server:** lets AI assistants add, retrieve and delete episodes, search nodes and facts, manage groups and clear
  the graph. The official C# MCP SDK makes this straightforward.
- **Telemetry:** anonymous, opt-out usage stats upstream. Leave it out of the port, or make it opt-in.

Both upstream servers have no authentication, authorisation or prompt-injection protection by design.

## Non-functional requirements and .NET notes

- **Async throughout:** the pipeline is concurrent; use async/await with a SemaphoreSlim for the LLM concurrency limit.
- **Schema setup:** EF Core migrations create tables and indexes (vector, full-text, uniqueness). Upstream creates them
  on first run instead.
- **Storage abstraction:** keep a graph-driver interface. EF Core on PostgreSQL is the default and SQL Server comes
  next. Graph databases such as Neo4j are not planned; the interface leaves room for them if that changes.
- **Security in the host service:** authentication, authorisation, tenant isolation by group id, and input controls
  against prompt injection.
- **Parity tests:** run the same episodes through Python Graphiti and the port, then compare extracted entities, facts
  and search results.
- **Observability:** OpenTelemetry tracing, which upstream also supports.
- **Version pinning:** port against one tagged upstream release and record it.
- **Attribution:** files translated from Graphiti keep Zep's copyright line, name the upstream file they come from,
  and are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md), as the Apache License requires.
- **Naming:** package IDs, namespaces and docs must not suggest an official Zep product.

Baseline: the port follows [v0.31.2](https://github.com/getzep/graphiti/releases/tag/v0.31.2) (October 2026). The
plan for getting there is in [roadmap.md](roadmap.md).

## Verification and benchmarks

The port is correct when it matches Python Graphiti on the same inputs, not when it hits the paper's numbers. Those
numbers are for Zep, the commercial service, with specific models.

1. **Unit tests:** port Graphiti's own test suite (the repo's tests folder) so each component behaves the same.
2. **Parity runs:** feed identical episodes into Python Graphiti and the port with the same LLM, embedding model and
   settings. Compare extracted entities, facts, invalidations and search results. Pin model versions and use
   temperature 0, since LLM randomness causes small differences even with correct code. Python Graphiti runs on Neo4j
   and the port on PostgreSQL, the setup users will run, so expect small differences in keyword ranking.
3. **Benchmark runs:** run the same benchmark through both versions. Target: the port scores within 1–2 points of
   Python Graphiti.

| Benchmark | What it tests | Use in this project |
| --- | --- | --- |
| LongMemEval | Long conversations (~115k tokens) with temporal and cross-session reasoning | Main quality gate; Zep reported 71.2% with gpt-4o |
| DMR (Deep Memory Retrieval) | Multi-session QA from MemGPT; near-saturated for modern models | Quick smoke test |
| LoCoMo | Long-conversation memory, reported by Mem0, Cognee and others | Comparison with other memory tools |

Benchmark runs cost real LLM tokens, so run them per release, not per commit.

## Development environment

The services, settings and cloud environment used to build and test the port are in
[development.md](development.md).

## Sources

- [Graphiti GitHub repository and README](https://github.com/getzep/graphiti)
- [Zep: A Temporal Knowledge Graph Architecture (paper)](https://arxiv.org/abs/2501.13956)
- [Graphiti MCP server docs](https://help.getzep.com/graphiti/getting-started/mcp-server.md)
