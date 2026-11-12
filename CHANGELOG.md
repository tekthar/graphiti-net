# Changelog

All notable changes to graphiti-net. Versions follow [Semantic Versioning](https://semver.org/); before 1.0, a
preview may change the API.

## 0.1.0-preview.1

The first public preview. Ported from Python Graphiti
[v0.31.2](https://github.com/getzep/graphiti/releases/tag/v0.31.2).

Packages: `GraphitiNet` and `GraphitiNet.PostgreSql`, for .NET 10.

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
  (EF Core, pgvector, built-in full-text search, a text search language per group, tables in your own `DbContext`
  and migrations, schema name and table prefix). A driver conformance suite both pass.
- **Utilities**: content chunking, community operations, OpenTelemetry-compatible tracing through `ActivitySource`.
- **graphiti-net additions**, opt-in or additive: an episode access filter on search applied before the limit,
  strict episode removal, one write at a time per group (`IGroupLock`), a call context on every model call with
  token usage per call, and a model-call gate with cancellation between pipeline steps.
- **Parity**: upstream's prompts and answers recorded on two datasets (`customer-support`, `arabic`) and replayed
  through the whole pipeline on every commit; a graph-building evaluation on LongMemEval against Python Graphiti.
- **Packaging**: NuGet packages with Source Link, deterministic builds and symbol packages; a quickstart sample;
  CI that packs on every commit and a release workflow that publishes on a version tag.

### Known gaps

- On PostgreSQL, the `customer-support` parity replay has one fact-resolution step that gets a different candidate
  set than upstream; the test is skipped and tracked.
- Benchmarks so far: LongMemEval graph building against Python Graphiti (36 and 100 messages, within upstream's
  run-to-run variation). LongMemEval QA, DMR and LoCoMo runs come before 1.0.
- The default LLM model is upstream's, which may not be available on every OpenAI account; set a model in
  `LLMConfig`.

### Not supported

- Graph databases (Neo4j, FalkorDB, Amazon Neptune, Kuzu). Storage is PostgreSQL or in memory.
