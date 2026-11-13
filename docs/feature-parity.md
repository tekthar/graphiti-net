# Feature parity with Graphiti

Every feature Python Graphiti offers, whether graphiti-net has it, and when the rest lands. Upstream's latest release
is v0.31.2 (October 2026), the release graphiti-net 0.1.0 ports, so this is the whole of Graphiti today.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent rewrite. It is not affiliated with,
> endorsed by, or sponsored by Zep Software, Inc.

Status: **Done** in 0.1.0 · **Partial** (works, with a gap noted) · **Planned** (with the target release) ·
**Not planned** (with the reason).

## Summary

| Area | Done | Partial | Planned | Not planned |
| --- | --- | --- | --- | --- |
| Ingestion | 12 | 0 | 0 | 0 |
| Time and invalidation | 5 | 0 | 0 | 0 |
| Search | 9 | 0 | 0 | 0 |
| Custom types | 6 | 0 | 0 | 0 |
| Communities and sagas | 5 | 0 | 0 | 0 |
| Storage | 3 | 0 | 0 | 4 |
| LLM clients | 4 | 3 | 3 | 0 |
| Embedders and rerankers | 5 | 1 | 1 | 0 |
| Prompts, caching, observability | 6 | 1 | 0 | 1 |
| Servers | 0 | 0 | 2 | 0 |
| Verification | 4 | 1 | 2 | 0 |

The library itself, from ingestion through communities and sagas, is complete. The work left is in providers,
servers and benchmarks.

## Ingestion

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| `add_episode` (message, text, JSON, fact triple episodes) | `AddEpisodeAsync` | Done |
| Entity extraction with recent episodes as context | `NodeOperations.ExtractNodesAsync` | Done |
| Entity deduplication: identity attributes, exact and fuzzy (MinHash) names, LLM for the rest | `NodeOperations.ResolveExtractedNodesAsync` | Done |
| Fact extraction, dangling endpoints materialised | `EdgeOperations.ExtractEdgesAsync` | Done |
| Fact resolution: duplicates and contradictions | `EdgeOperations.ResolveExtractedEdgesAsync` | Done |
| Entity summaries and typed attributes | `NodeOperations.ExtractAttributesFromNodesAsync` | Done |
| `add_episode_bulk` with combined node and fact extraction | `AddEpisodeBulkAsync` | Done |
| `add_triplet` | `AddTripletAsync` (keeps every write inside one group) | Done |
| `remove_episode` | `RemoveEpisodeAsync`, plus `strict: true` | Done |
| `retrieve_episodes`, `get_nodes_and_edges_by_episode` | Same names, `Async` | Done |
| Content chunking utility | `ContentChunking` | Done |
| Concurrency limit (`SEMAPHORE_LIMIT`, `max_coroutines`) | `maxCoroutines`, `SEMAPHORE_LIMIT` | Done |

## Time and invalidation

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| Bi-temporal facts (`valid_at`, `invalid_at`, `created_at`, `expired_at`) | Same fields | Done |
| Timestamp extraction relative to the episode's reference time | `EdgeOperations`, Python's `fromisoformat` rules | Done |
| Contradiction invalidation, including out-of-order episodes | `ApplyTemporalInvalidation` | Done |
| Future-tense guard on extracted dates | `TemporalEdgeUtils` | Done |
| Date filters with AND/OR groups | `SearchFilters` | Done |

## Search

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| `search` (hybrid facts, optional centre node) | `SearchAsync` | Done |
| `search_` with a full `SearchConfig` | `SearchAsync(query, config, ...)` / `SearchAdvancedAsync` | Done |
| Cosine, BM25 keyword and breadth-first search | Every driver, BM25 with Lucene-style tokenisation | Done |
| Targets: facts, entities, episodes, communities | Same | Done |
| Rerankers: RRF, MMR, node distance, episode mentions, cross-encoder | Same | Done |
| Every predefined recipe (16) | `SearchConfigRecipes` | Done |
| `SearchFilters`: labels, fact types, dates, ids, endpoints, properties | Same | Done |
| `search_results_to_context_string` | `SearchHelpers` | Done |
| Group-scoped search across several groups | Same, plus the episode access filter (graphiti-net addition) | Done |

## Custom types

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| Custom entity types (Pydantic models) | C# classes or records with `[Description]` | Done |
| Custom edge types | Same | Done |
| Edge type map | `IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>>` | Done |
| Excluded entity types | `excludedEntityTypes` | Done |
| Strict edge types | `strictEdgeTypes` | Done |
| Custom extraction instructions | `customExtractionInstructions` | Done |

## Communities and sagas

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| `build_communities` (label propagation, pairwise summaries) | `BuildCommunitiesAsync` | Done |
| `update_communities` on `add_episode` | `updateCommunities: true` | Done |
| Sagas: episodes chained in order | `saga:` on `AddEpisodeAsync` and bulk | Done |
| `summarize_saga` (incremental, with a budget) | `SummarizeSagaAsync` | Done |
| Node and edge namespaces (`graphiti.nodes.entity.get_by_uuid`, ...) | `graphiti.Nodes.Entity.GetByUuidAsync`, ... | Done |

## Storage

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| Neo4j | — | Not planned: graphiti-net needs no graph database |
| FalkorDB | — | Not planned |
| Amazon Neptune (with OpenSearch) | — | Not planned |
| Kuzu (deprecated upstream) | — | Not planned |
| — | PostgreSQL with pgvector (production default) | Done |
| — | SQL Server 2025 / Azure SQL | Done |
| — | In memory, with JSON save and load | Done |

The driver contract stays open, so another backend can be added behind it if a need appears.

## LLM clients

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| `OpenAIClient` (Responses API, structured output, `small_model`) | `OpenAIClient`, `LLMConfig.SmallModel` | Done |
| `OpenAIGenericClient` (Ollama, vLLM, LM Studio, ...) | `OpenAIGenericClient` | Done |
| `AzureOpenAILLMClient` | `AzureOpenAILLMClient` | Done |
| Any provider | `ChatClientLLMClient` over Microsoft.Extensions.AI `IChatClient` | Done |
| `AnthropicClient` | Through `ChatClientLLMClient`; structured output differs from upstream's tool-use path | Partial; native port in 0.3 |
| `GeminiClient` | Through `ChatClientLLMClient` | Partial; native port in 0.3 |
| `GroqClient` | Through `OpenAIGenericClient` (OpenAI-compatible) or `ChatClientLLMClient` | Partial |
| `LLMRuntime` and per-prompt routing to transports | `promptRoutes`: route a prompt name to another client | Planned 0.3: full runtime with model selection per prompt |
| `GLiNER2Client` (local entity extraction model) | — | Planned 0.4, through ONNX Runtime |
| Decisions clients (`openai_decisions_client`, `jev_client`): deduplication decisions on dedicated models | — | Planned 0.4 |

## Embedders and rerankers

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| `OpenAIEmbedder` | `OpenAIEmbedder` | Done |
| Azure OpenAI embedder | `AzureOpenAIEmbedderClient` | Done |
| Gemini and Voyage embedders | `EmbeddingGeneratorEmbedder` over any `IEmbeddingGenerator` | Done |
| `OpenAIRerankerClient` (logprobs) | `OpenAIRerankerClient` | Done |
| `GeminiRerankerClient` | `ChatClientCrossEncoder` (same prompt and scoring, any provider) | Done |
| `BGERerankerClient` (local cross-encoder) | — | Planned 0.4, through ONNX Runtime |
| Embedding dimension truncation (`EMBEDDING_DIM`) | `EmbedderConfig.EmbeddingDim` | Partial: covered by [#3](https://github.com/tekthar/graphiti-net/issues/3) (no cutting by default for local models) |

## Prompts, caching and observability

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| Prompt library, word for word | `PromptLibrary` | Done |
| Per-instance prompt overrides with fixed schemas | `PromptLibrary.Create(overrides)` | Done |
| LLM response cache | `LLMCache` (files instead of SQLite, same keys) | Done |
| Token usage tracking | `TokenTracker`, plus per-call usage (graphiti-net addition) | Done |
| OpenTelemetry tracing | `ActivitySource` (`GraphitiNet`, `GraphitiNet.Search`) | Done |
| Multilingual extraction instruction | Same | Done |
| Eval prompts (`qa_prompt`, `eval_prompt`, ...) | `EvalPrompts` | Partial: ported, no public eval API |
| Anonymous telemetry (opt-out) | — | Not planned: graphiti-net sends nothing |

## Servers

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| MCP server: `add_memory` (queued per group), `search_nodes`, `search_memory_facts`, `get_episodes`, `get_entity_edge`, `get_episode_entities`, `delete_episode`, `delete_entity_edge`, `add_triplet`, `summarize_saga`, `build_communities`, `clear_graph`, `get_status`; stdio and HTTP transports; provider configuration | — | Planned 0.3: `GraphitiNet.Mcp` on the official C# MCP SDK, same tools, PostgreSQL or SQL Server storage |
| REST server: `POST /messages`, `POST /entity-node`, `POST /search`, `POST /get-memory`, `GET /entity-edge/{uuid}`, `GET /episodes/{group_id}`, deletes, `POST /clear` | — | Planned 0.3: `GraphitiNet.AspNetCore` minimal API endpoints you map into your own app, with your own authentication |

Both upstream servers ship without authentication; graphiti-net's will plug into the host's authentication and run
writes through the group lock.

## Verification

| Graphiti | graphiti-net | Status |
| --- | --- | --- |
| Unit tests (`tests/`) | Ported where meaningful, plus a driver conformance suite | Done |
| — | Parity replay of upstream's recorded answers on every commit | Done |
| — | Differential tests against upstream's own functions | Done |
| Graph-building evaluation (`eval_e2e_graph_building.py`) | Run against Python Graphiti on 100 messages | Done |
| LongMemEval question answering | 24 questions: 21/24 against upstream's 18/24 and 19/24 | Partial: 100+ questions planned for 0.2 |
| DMR | — | Planned 0.3 |
| LoCoMo | — | Planned 0.3 |

## .NET additions beyond Graphiti

| Feature | Status |
| --- | --- |
| Episode access filter before the search limit | Done |
| Strict episode removal | Done |
| Per-group write lock (`IGroupLock`) | Done |
| Call context and per-call token usage | Done |
| Model-call gate (budgets) and cancellation between steps | Done |
| Host-owned tables (schema, prefix) and keyword search language per group | Done |
| Dependency injection: `services.AddGraphiti(...)` with options binding | Planned 0.3 |
| .NET Aspire integration (PostgreSQL resource, health checks) | Planned 0.3 |

## Releases

| Release | Theme | Contents |
| --- | --- | --- |
| **0.2** | Consumers | What the first consumer, AbpAgent 0.5.0 on graphiti-net 0.1.0, surfaced: local embedding models: query and document input types and prefixes, no key needed, a tested Ollama path, Arabic ([#3](https://github.com/tekthar/graphiti-net/issues/3)); group locks that hold across servers ([#7](https://github.com/tekthar/graphiti-net/issues/7)); reading a group's graph, a node's facts and its episodes with the episode access filter applied ([#10](https://github.com/tekthar/graphiti-net/issues/10)); a built-in reranker that keeps the given order ([#6](https://github.com/tekthar/graphiti-net/issues/6)); a typed error when the database can't store vectors ([#8](https://github.com/tekthar/graphiti-net/issues/8)); each driver's table names for a schema and prefix ([#11](https://github.com/tekthar/graphiti-net/issues/11)); an idempotency key on `AddEpisodeAsync` ([#9](https://github.com/tekthar/graphiti-net/issues/9)); opt-in rate-limit retry. Measured: LongMemEval question answering on 100+ questions, and PostgreSQL keyword search on common words, tested at about 1M mostly Arabic records |
| **0.3** | Reach and hosting | MCP server (`GraphitiNet.Mcp`); dependency injection (`AddGraphiti`); full LLM runtime with per-prompt model routing; REST endpoints for ASP.NET Core (`GraphitiNet.AspNetCore`); Aspire integration; native Anthropic and Gemini clients for exact parity; DMR and LoCoMo runs |
| **0.4** | Local models | GLiNER2 extraction and BGE reranking through ONNX Runtime; decisions clients |
| Later | | SQL Server bulk save speed (binary embeddings or `SqlBulkCopy`): no consumer uses SQL Server as its store yet |
| **1.0** | Stable API | API frozen; full benchmark suite within 1–2 points of Python Graphiti; follows the next upstream release |

Upstream moves fast. When it tags a new release, the parity recordings are re-made against it and the gaps it opens
are added here first.
