# Roadmap

Status: draft, November 2026.

How we get from requirements to a working port. The requirements themselves are in [requirements.md](requirements.md).

## Baseline

The port follows **Graphiti [v0.31.2](https://github.com/getzep/graphiti/releases/tag/v0.31.2)**. Port from that
tag only, not from upstream `main`.

Rough size of what we port, excluding the FalkorDB, Kuzu and Neptune drivers:

| Upstream part | Lines |
| --- | --- |
| `graphiti_core` | about 31,000 |
| `tests` | about 24,000 |
| Largest files | `graphiti.py` (2.2k), `search/search_utils.py` (2.1k), `utils/maintenance/node_operations.py` (1.3k), `utils/maintenance/edge_operations.py` (1.2k), `nodes.py` (1.2k), `edges.py` (1.1k) |

## Gaps between the requirements and v0.31.2

Found while surveying the baseline. Fix these in [requirements.md](requirements.md) before writing code.

1. ~~**Sagas are missing.**~~ Fixed: the requirements now list all eight record types. `Graphiti.summarize_saga`
   is still to port.
2. ~~**The concurrency default is wrong.**~~ Fixed: upstream's `DEFAULT_SEMAPHORE_LIMIT` is 20.
3. ~~**Newer features aren't covered.**~~ Decided with the first consumer
   ([#2](https://github.com/tekthar/graphiti-net/issues/2), item 3):

   | Feature | First release? | Notes |
   | --- | --- | --- |
   | The default `add_episode` extraction path | Yes | Separate node and edge extraction, as v0.31.2's `add_episode` does |
   | Combined node and edge extraction | With bulk ingest | `add_episode_bulk` uses it by default in v0.31.2 |
   | Content chunking | Yes | A public utility; v0.31.2's `add_episode` doesn't call it, so behaviour is unchanged |
   | `add_triplet` | Yes | Hosts add facts they already know without an extraction call |
   | Token tracking | Yes | Also reported per `AddEpisodeAsync` call |
   | LLM response cache | Yes | Optional; parity replay uses recorded answers instead (upstream's OpenAI client has no cache) |
   | Decisions and GLiNER2 clients | Later | |

## First consumer

[AbpAgent](https://github.com/tekthar/abp-agent) is graphiti-net's first consumer. Its requirements are in
[#1](https://github.com/tekthar/graphiti-net/issues/1) and its suggested order of work in
[#2](https://github.com/tekthar/graphiti-net/issues/2). Everything below is additive: default behaviour stays
upstream's.

| Requirement | Where it lands |
| --- | --- |
| Episode access filter applied before the search limit (#1.1) | `SearchEngine` / `Graphiti.SearchAsync` option |
| Strict episode removal; deleting a whole group (#1.2) | `Graphiti.RemoveEpisodeAsync(strict: true)`, group deletion |
| One ingestion at a time per group (#1.3) | `IGroupLock`, in-process by default |
| Call context and per-call token usage (#1.4) | `GraphitiCallContext`, `AddEpisodeResults` token usage |
| Cancellation between steps, a budget gate before model calls (#1.5) | Pipeline and a model-call gate |
| Host-owned tables: schema name and table prefix (#1.6) | PostgreSQL `ModelBuilder` extension options |
| Keyword search per language (#1.7) | Text search configuration per group in the PostgreSQL driver |
| Entities never merge across groups (#1) | Group-scoped deduplication, with a test |
| One thin path from episode to search, tested in CI on PostgreSQL (#2.1) | CI runs a pgvector service |
| Recorded upstream answers replayed on every commit (#2.2) | [parity/README.md](../parity/README.md) |
| Episode removal in the ingestion phase (#2.4) | `RemoveEpisodeAsync` ships with `AddEpisodeAsync` |
| Arabic parity dataset (#2.5) | `parity/datasets/arabic.json` |

## Progress

Done and merged:

- Solution skeleton, central package management, CI with a pgvector service.
- Core records, errors, validation, UTC and text helpers, token tracking.
- The graph-driver contract, a driver conformance suite, and the in-memory driver (BM25 keyword ranking, as
  upstream's Lucene).
- The prompt library: every prompt matches upstream's text exactly, and every response schema matches Pydantic's.
- LLM, embedder and reranker clients: OpenAI, OpenAI-compatible endpoints, Azure OpenAI, and adapters for any
  Microsoft.Extensions.AI provider.
- The search engine: every method, reranker and recipe.
- Parity recordings of upstream's answers for two datasets.

In progress: the PostgreSQL driver, node and edge operations, the `Graphiti` class with communities and sagas, and
the episode access filter.

Next: wire `AddEpisodeAsync` end to end, replay the parity recordings in CI, then package a first preview.

## Phases

### Phase 0: Foundations

- Fix the gaps above and record the scope decisions.
- Solution skeleton:
  - `GraphitiNet.Core`: records, interfaces, pipeline and search
  - `GraphitiNet.EntityFrameworkCore.PostgreSql`: storage and the PostgreSQL search module
  - `GraphitiNet.Providers`: Microsoft.Extensions.AI adapters
  - test projects for unit and integration tests
  - `Directory.Build.props` and central package management
- CI on GitHub Actions: build, unit tests, and integration tests against a PostgreSQL + pgvector service container.
- A SessionStart hook that starts PostgreSQL and Neo4j in cloud sessions, as described in
  [development.md](development.md).

### Phase 1: Data model and storage

- Port the node and edge records (`nodes.py`, `edges.py`) as C# records, with the temporal fields and group id.
- EF Core model:
  - `ModelBuilder` extension, and a standalone `DbContext` in its own schema
  - a global query filter on group id
  - migrations with HNSW vector indexes, a `tsvector` full-text index and uniqueness constraints
- The graph-driver interface. Upstream's `driver/operations/*` split, one operations interface per record type, is
  the template.
- Port `models/nodes/node_db_queries.py` and `models/edges/edge_db_queries.py` as LINQ and SQL.
- Tests: `test_node_int.py`, `test_edge_int.py`, `test_edge_db_queries.py`.

### Phase 2: Providers and prompts

- `ILlmClient` on `IChatClient` with structured JSON output, `IEmbedder` on `IEmbeddingGenerator`, and
  `ICrossEncoder`.
- Port the prompt library word for word (`prompts/*.py`, `prompts/lib.py`), with overrides per instance and routing of
  individual prompts to other models (`llm_client/prompt_config.py`).
- JSON Schema generated from C# types, for custom entity and edge types.
- Tests: `tests/prompts/*`, `tests/llm_client/test_client.py`, `test_graphiti_configurable_prompts.py`.

### Phase 3: Ingestion pipeline

- Port `add_episode` end to end:
  1. save the episode
  2. extract entities
  3. deduplicate entities (`dedup_helpers.py`, `node_operations.py`)
  4. extract facts
  5. resolve facts (`edge_operations.py`)
  6. invalidate contradicted facts (`temporal_edge_utils.py`)
  7. update summaries and attributes (`attribute_utils.py`)
  8. embed and save
- Concurrency limit with `SemaphoreSlim`, and OpenTelemetry tracing.
- Tests: `tests/utils/maintenance/*`, `test_graphiti_mock.py`, `test_entity_exclusion_int.py`.

### Phase 4: Search

- PostgreSQL search module:
  - cosine similarity with pgvector
  - full-text search with `tsvector`, and ParadeDB `pg_search` BM25 where the host allows it
  - breadth-first graph search as a recursive query, capped at 3 hops (`MAX_SEARCH_DEPTH`)
- Rerankers: reciprocal rank fusion, maximal marginal relevance, node distance, episode mentions, cross-encoder.
- Search filters, config recipes (`search_config_recipes.py`), and the simple and advanced search APIs.
- Tests: `tests/utils/search/*`, including `test_search_security.py`, and `test_handle_multiple_group_ids.py`.

### Phase 5: The rest of the library

- Bulk ingest (`utils/bulk_utils.py`).
- Delete an episode and clean up what only it produced.
- Communities (`community_operations.py`).
- Sagas, `add_triplet` and content chunking, depending on the Phase 0 scope decisions.

### Phase 6: Verification

- Parity harness: the same episodes through Python Graphiti v0.31.2 on Neo4j and the port on PostgreSQL, with pinned
  models at temperature 0. Compare entities, facts, invalidations and search results.
- DMR as a smoke test, then LongMemEval as the release gate: within 1–2 points of Python Graphiti.

### Phase 7: Optional extras

- SQL Server 2025 / Azure SQL backend.
- ASP.NET Core REST server.
- MCP server on the official C# MCP SDK.
- NuGet packaging and release process.

## First pull requests

1. Docs: fix the gaps above and record the scope decisions.
2. Solution skeleton, CI and the SessionStart hook.
3. Core records and the EF Core model with the first migration, plus CRUD integration tests on PostgreSQL.
