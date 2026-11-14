# Changelog

All notable changes to graphiti-net are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

### Added

- **Reads that apply the episode access filter** (#10): `Graphiti.GetGroupGraphAsync` (a group's entities and the
  facts that start at them, paged), `GetFactsByNodeUuidAsync` (a node's edges) and `GetGroupEpisodesAsync` (a group's
  episodes, paged), also as `GraphitiNet.Search.VisibleReads` over any driver. They use search's visibility rules,
  so search and reads agree on what one user can see; pages hold up to `limit` visible records and the cursor is the
  last id scanned, so paging stays stable under filtering. Without a filter they return what the `Nodes` and `Edges`
  namespace methods return. See [docs/api-compatibility.md](docs/api-compatibility.md).
- **`PassThroughCrossEncoderClient`** (#6): a cross-encoder that keeps the given order and scores each passage by
  its rank, for hosts with no reranking model. With it the fact and episode cross-encoder rerankers give the RRF
  order. The default is still `OpenAIRerankerClient`.
- The acceptance tests also run on SQL Server.
- **`VectorStoreUnavailableException`** (`GraphitiNet.Driver`): `BuildIndicesAndConstraintsAsync` throws it, with the
  database's own exception inside, when the database cannot hold embedding vectors: PostgreSQL without pgvector
  installed, or where the role may not run `CREATE EXTENSION vector`, and SQL Server before 2025, which has no
  `vector` type. The message names the database, what is missing and the fix (install pgvector, have a superuser run
  `CREATE EXTENSION vector`, or use SQL Server 2025 / Azure SQL), so a host can show it to its administrator; `Reason`
  says which case it is. `PostgresDriver.CanHoldVectorsAsync` and `SqlServerDriver.CanHoldVectorsAsync` check the
  same before setup without creating anything (tekthar/graphiti-net#8).
- **`GraphitiSchema.Tables(options)`** in `GraphitiNet.PostgreSql` and `GraphitiNet.SqlServer`: the tables the driver
  creates for a schema name and table prefix, without a database, each with its qualified name and the record type it
  holds; it includes PostgreSQL's `search_statistics` and `search_term_counts` and SQL Server's keyword index tables (`search_terms`,
  `search_fields`). It reads the same EF Core model `BuildIndicesAndConstraintsAsync` builds from, and a test on each
  driver checks it against the tables the build creates (tekthar/graphiti-net#11).
- **Group locks that hold across servers** ([#7](https://github.com/tekthar/graphiti-net/issues/7)):
  `PostgresGroupLock` (`GraphitiNet.PostgreSql`, `pg_advisory_lock`) and `SqlServerGroupLock`
  (`GraphitiNet.SqlServer`, `sp_getapplock` owned by the session, negative results thrown). Pass one as
  `groupLock:` so several app servers writing to one database ingest into a group one at a time. Each holds its lock
  on an unpooled connection of its own, keyed by a SHA-256 of the schema, table prefix and group id; releases it on
  dispose, when the connection drops, or when a wait is cancelled; and queues waiters within one process without a
  connection each. See [docs/drivers.md](docs/drivers.md#several-app-servers-a-group-lock-per-database).
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
- **`StructuredOutputMode.JsonObjectGuided`** for `OpenAIGenericClient`: `json_object` output with a clearer prompt
  (return the data, not the schema, plus an example object of the right shape), for providers such as DeepSeek whose
  models sometimes echo upstream's bare schema back. `JsonObject` keeps upstream's text. See
  [docs/api-compatibility.md](docs/api-compatibility.md#guided-json-output-for-providers-without-json_schema).

### Changed

- **PostgreSQL keyword search stays fast at a million records, common words included**, with exactly the same
  results as before (and as the in-memory driver). Each keyword key now carries the term's impact class, so the GIN
  index finds the rows where a term weighs most, and a search reads only the rows that can still reach its top results
  (MaxScore by impact class, then a WAND-like pass over combinations of classes) instead of every row holding a common
  word. Document frequencies, group sizes and the text search configurations in use come from a new
  `search_term_counts` table kept by the statistics triggers, instead of counting through the GIN index (or scanning
  the table) on every search. On a synthetic graph of 1M mostly Arabic records, queries made only of common words went
  from about 1 s (one large tenant) and 2-3 s (all groups) to 20-40 ms and 45-60 ms at the median; no case of the
  benchmark is slower than 96 ms at the median. See [the benchmark](docs/benchmarks/postgresql-keyword-search.md),
  which `tools/GraphitiNet.Bench` reproduces. Bulk saves send one multi-row statement per batch; keeping the counts
  still makes them about half as fast at that size, and a single save about 2 ms slower. Upgrading:
  `BuildIndicesAndConstraintsAsync` (or `GraphitiSchemaSql.SearchStatistics` in a migration) converts the keyword
  index of rows written by 0.1 once, rewriting each searchable row (12 minutes per million rows without embeddings);
  searches stay exact on unconverted rows. See
  [Upgrading from 0.1](docs/postgresql.md#upgrading-from-01).

### Fixed

- `OpenAIClient` and `OpenAIGenericClient` now ask the model call gate before each LLM call, put the prompt name in
  `GraphitiCallContext`, and record their tokens in the operation's usage (`AddEpisodeResults.TokenUsage`). In 0.1.0
  they skipped all three, because they override `GenerateResponseAsync`; only clients on the base implementation,
  such as `ChatClientLLMClient`, honoured them.

### Measured

- **LongMemEval question answering on 102 questions** (17 per type), on PostgreSQL against Python Graphiti on Neo4j,
  with DeepSeek `deepseek-flash` building and answering and `gpt-5.5` grading: graphiti-net 89/102, Python Graphiti
  83/102 (7 questions right only with graphiti-net, 1 only with Python Graphiti). See
  [the results](parity/results/2026-11-14-lme-qa-102.md).
- **PostgreSQL keyword search at 1M mostly Arabic records**: see the Changed entry above and
  [the benchmark](docs/benchmarks/postgresql-keyword-search.md).

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
