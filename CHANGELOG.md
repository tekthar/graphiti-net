# Changelog

All notable changes to graphiti-net are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

### Added

- **Idempotency keys on episodes** ([#9](https://github.com/tekthar/graphiti-net/issues/9)): `AddEpisodeAsync` takes
  `idempotencyKey` and `RawEpisode` has `IdempotencyKey`, unique per group. A call that failed after its episode was
  saved is retried with the same key: an unprocessed episode is resumed, a processed one is replayed
  (`AddEpisodeResults.Replayed`) without extracting again, so the retry ends with one episode and no duplicate entities
  or facts. Two concurrent calls with one key create one episode. Stored as `EpisodicNode.IdempotencyKey`; drivers
  gain `IEpisodeNodeOperations.GetByIdempotencyKeyAsync` and `AddOrGetByIdempotencyKeyAsync` (with default
  implementations for third-party drivers). Without a key, behaviour is upstream's.
- **Schema change for PostgreSQL and SQL Server**: the episodes table gets a nullable `idempotency_key` column and a
  unique index `ux_episodes_group_id_idempotency_key` on (`group_id`, `idempotency_key`).
  `BuildIndicesAndConstraintsAsync` adds both to a 0.1.0 database (it now adds any column a newer version added to an
  existing table); hosts on EF Core migrations add a migration, see docs/postgresql.md and docs/sqlserver.md.
- **Opt-in retry of transient provider errors**: `new Graphiti(..., modelCallRetry: new ModelCallRetryOptions())`
  retries each LLM prompt, embedding and rerank call on a rate limit (HTTP 429), a 5xx, a timeout or a dropped
  connection, with exponential backoff and jitter (`MaxAttempts`, `BaseDelay`, `MaxDelay`), honouring `Retry-After`.
  Each LLM attempt rebuilds its prompt, tokens are counted once, and a model call gate is asked once per call. Off by
  default, so behaviour stays upstream's. `RateLimitException.RetryAfter` carries a provider's requested wait.

## [0.1.0] - 2026-11-13

The first release. Ported from Python Graphiti
[v0.31.2](https://github.com/getzep/graphiti/releases/tag/v0.31.2).

Packages: `GraphitiNet`, `GraphitiNet.PostgreSql` and `GraphitiNet.SqlServer`, for .NET 10.

### Added

- **`Graphiti`**, the entry point, with upstream's methods and parameter names: `AddEpisodeAsync`,
  `AddEpisodeBulkAsync` (combined entity and fact extraction by default),
  `AddTripletAsync`, `RemoveEpisodeAsync`, `RetrieveEpisodesAsync`, `SearchAsync`, `SearchAdvancedAsync`,
  `GetNodesAndEdgesByEpisodeAsync`, `BuildCommunitiesAsync`, `SummarizeSagaAsync`, `ClearDataAsync`,
  `BuildIndicesAndConstraintsAsync`, and the `Nodes` and `Edges` namespaces.
- **Ingestion**: entity extraction and deduplication, fact extraction and resolution, temporal invalidation of
  contradicted facts, summaries and attributes, custom entity and edge types as C# classes or records.
- **Search**: cosine similarity, keyword and breadth-first search for facts, entities, episodes and communities;
  RRF, MMR, node-distance, episode-mentions and cross-encoder rerankers; every upstream recipe; search filters.
- **Prompts**: upstream's prompt library word for word, with response schemas that match Pydantic's; overrides per
  instance and routing of single prompts to other clients.
- **Providers**: OpenAI, Azure OpenAI and OpenAI-compatible LLM, embedder and reranker clients; adapters for any
  Microsoft.Extensions.AI `IChatClient` and `IEmbeddingGenerator`; token usage tracking.
- **Storage**: the in-memory driver (BM25 keyword ranking, save to and load from JSON) and the PostgreSQL driver
  (EF Core, pgvector, BM25 keyword search in SQL with the same tokenisation and ranking as Lucene, a text search language per group, tables in your own `DbContext`
  and migrations, schema name and table prefix). A driver conformance suite both pass.
- **SQL Server storage** (`GraphitiNet.SqlServer`): a driver for SQL Server 2025 and Azure SQL through EF Core, with
  the native `vector` type for vector search, BM25 keyword search (ranked as upstream's Lucene) over a keyword index
  the driver keeps in step with every write, breadth-first search in one batch, tables in your own `DbContext` and
  migrations with a schema name and table prefix. It passes the driver conformance suite, and both parity datasets
  match upstream on it. See [docs/sqlserver.md](docs/sqlserver.md).
- **Utilities**: content chunking, community operations, OpenTelemetry-compatible tracing through `ActivitySource`.
- **graphiti-net additions**, opt-in or additive: an episode access filter on search applied before the limit,
  strict episode removal, one write at a time per group (`IGroupLock`), a call context on every model call with
  token usage per call, and a model-call gate with cancellation between pipeline steps.
- **Parity**: upstream's prompts and answers recorded on two datasets (`customer-support`, `arabic`) and replayed
  through the whole pipeline on every commit; a graph-building evaluation on LongMemEval against Python Graphiti.
- **Packaging**: NuGet packages with Source Link, deterministic builds and symbol packages; a quickstart sample;
  CI that packs on every commit and a release workflow that publishes on a version tag.

### Known gaps

- Benchmarks so far: LongMemEval graph building against Python Graphiti (36 and 100 messages, within upstream's
  run-to-run variation). LongMemEval question answering on 24 questions: graphiti-net 21/24, Python Graphiti 18/24 and 19/24. DMR and
  LoCoMo runs come before 1.0.
- The default LLM model is upstream's, which may not be available on every OpenAI account; set a model in
  `LLMConfig`.

### Not supported

- Graph databases (Neo4j, FalkorDB, Amazon Neptune, Kuzu). Storage is PostgreSQL, SQL Server or in memory.

[Unreleased]: https://github.com/tekthar/graphiti-net/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/tekthar/graphiti-net/releases/tag/v0.1.0
