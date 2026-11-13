# API compatibility

This page maps everything a Graphiti user touches in Python Graphiti v0.31.2 to graphiti-net. The rules are the same
everywhere:

- Methods are `PascalCase` and end in `Async`: `add_episode` is `AddEpisodeAsync`.
- Parameters are `camelCase` with the same names and defaults: `reference_time` is `referenceTime`.
- Every async method takes a `CancellationToken` as its last parameter.
- `snake_case` fields become `PascalCase` properties: `valid_at` is `ValidAt`, `entity_edges` is `EntityEdges`.
- Timestamps are `DateTimeOffset`, always stored in UTC.
- Python `dict` / `list` parameters are `IReadOnlyDictionary` / `IReadOnlyList`.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent rewrite. It is not affiliated with,
> endorsed by, or sponsored by Zep Software, Inc.

## Storage

graphiti-net has no graph database. **Neo4j, FalkorDB, Amazon Neptune and Kuzu are not supported.** Every
`Graphiti` takes an `IGraphDriver`:

| Upstream | graphiti-net |
| --- | --- |
| `Graphiti(uri, user, password)` (Neo4j) | `new Graphiti(graphDriver, ...)` with `new PostgresDriver(connectionString)` (`GraphitiNet.PostgreSql`, the default backend) or `new InMemoryGraphDriver()` |
| `Graphiti(graph_driver=FalkorDriver(...))` / `Neo4jDriver` / `NeptuneDriver` | not supported; pass an `IGraphDriver` |
| (none) | `Graphiti.CreateInMemory(...)`: an instance over a new `InMemoryGraphDriver`, for demos and tests |
| `driver.execute_query(...)` | not available: drivers expose typed operations only (see [drivers.md](drivers.md)) |

## The `Graphiti` class

`GraphitiNet.Graphiti` is `IAsyncDisposable`: `await using var graphiti = new Graphiti(...)`.

### Constructor

| Upstream parameter | graphiti-net parameter | Notes |
| --- | --- | --- |
| `uri`, `user`, `password` | none | pass `graphDriver` |
| `graph_driver` | `IGraphDriver graphDriver` | required, first |
| `llm_client=None` | `LLMClient? llmClient = null` | default `new OpenAIClient()` (needs `OPENAI_API_KEY`) |
| `embedder=None` | `EmbedderClient? embedder = null` | default `new OpenAIEmbedder()` |
| `cross_encoder=None` | `CrossEncoderClient? crossEncoder = null` | default `new OpenAIRerankerClient()` |
| `store_raw_episode_content=True` | `bool storeRawEpisodeContent = true` | |
| `max_coroutines=None` | `int? maxCoroutines = null` | falls back to `SEMAPHORE_LIMIT`, then 20 |
| `prompt_library=None` | `PromptLibrary? promptLibrary = null` | `PromptLibrary.Create(overrides)`; default `PromptLibrary.Default` |
| `llm_runtime=None` | `IReadOnlyDictionary<string, LLMClient>? promptRoutes = null` | routes single prompts, by name, to other clients |
| `tracer`, `trace_span_prefix` | none | spans go to the `ActivitySource` `GraphitiNet` (`GraphitiTelemetry.ActivitySourceName`), named `graphiti.add_episode` and so on; search spans go to `GraphitiNet.Search` |
| (none) | `IGroupLock? groupLock = null` | graphiti-net addition, see below |
| (none) | `IModelCallGate? modelCallGate = null` | graphiti-net addition, see below |
| (none) | `ILogger? logger = null` | upstream logs through `logging` |
| (none) | `ModelCallRetryOptions? modelCallRetry = null` | graphiti-net addition: retries transient provider errors, see below |

### Properties

| Upstream | graphiti-net |
| --- | --- |
| `graphiti.driver` | `Driver` |
| `graphiti.llm_client` | `LlmClient` |
| `graphiti.embedder` | `Embedder` |
| `graphiti.cross_encoder` | `CrossEncoder` |
| `graphiti.clients` | `Clients` (`GraphitiClients`) |
| `graphiti.prompt_library` | `PromptLibrary` |
| `graphiti.store_raw_episode_content` | `StoreRawEpisodeContent` |
| `graphiti.max_coroutines` | `MaxCoroutines` |
| `graphiti.token_tracker` | `TokenTracker` (`TokenUsageTracker`: `GetUsage()`, `GetTotalUsage()`, `Reset()`, `FormatSummary()` for `print_summary()`) |
| `graphiti.nodes` / `graphiti.edges` | `Nodes` / `Edges` (see [Namespaces](#namespaces)) |

### Methods

| Upstream | graphiti-net |
| --- | --- |
| `await close()` | `await CloseAsync()` or `await DisposeAsync()` |
| `build_indices_and_constraints(delete_existing=False)` | `BuildIndicesAndConstraintsAsync(deleteExisting = false)` |
| `add_episode(name, episode_body, source_description, reference_time, source=EpisodeType.message, group_id=None, uuid=None, update_communities=False, entity_types=None, excluded_entity_types=None, previous_episode_uuids=None, edge_types=None, edge_type_map=None, custom_extraction_instructions=None, saga=None, saga_previous_episode_uuid=None)` | `AddEpisodeAsync(name, episodeBody, sourceDescription, referenceTime, source = EpisodeType.Message, groupId = null, uuid = null, updateCommunities = false, entityTypes = null, excludedEntityTypes = null, previousEpisodeUuids = null, edgeTypes = null, edgeTypeMap = null, customExtractionInstructions = null, saga = null, sagaPreviousEpisodeUuid = null, episodeMetadata = null, idempotencyKey = null, cancellationToken)` → `AddEpisodeResults` |
| `add_episode_bulk(bulk_episodes, group_id=None, entity_types=None, excluded_entity_types=None, edge_types=None, edge_type_map=None, strict_edge_types=False, custom_extraction_instructions=None, saga=None, use_combined_extraction=True)` | `AddEpisodeBulkAsync(bulkEpisodes, groupId, entityTypes, excludedEntityTypes, edgeTypes, edgeTypeMap, strictEdgeTypes = false, customExtractionInstructions, saga, useCombinedExtraction = true, cancellationToken)` → `AddBulkEpisodeResults` |
| `add_triplet(source_node, edge, target_node, episode=None)` | `AddTripletAsync(sourceNode, edge, targetNode, episode = null)` → `AddTripletResults` |
| `retrieve_episodes(reference_time, last_n=EPISODE_WINDOW_LEN, group_ids=None, source=None, saga=None)` | `RetrieveEpisodesAsync(referenceTime, lastN = Graphiti.EpisodeWindowLen, groupIds, source, saga)` |
| `search(query, center_node_uuid=None, group_ids=None, num_results=10, search_filter=None)` → `list[EntityEdge]` | `SearchAsync(query, centerNodeUuid = null, groupIds = null, numResults = 10, searchFilter = null, episodeAccessFilter = null)` → `IReadOnlyList<EntityEdge>` |
| `search_(query, config=COMBINED_HYBRID_SEARCH_CROSS_ENCODER, group_ids=None, center_node_uuid=None, bfs_origin_node_uuids=None, search_filter=None, query_vector=None)` → `SearchResults` | `SearchAsync(query, SearchConfig config, groupIds, centerNodeUuid, bfsOriginNodeUuids, searchFilter, queryVector, episodeAccessFilter)`, or `SearchAdvancedAsync(query, config = null, ...)` with upstream's default config |
| `_search(...)` (deprecated) | `SearchAsync(query, config, ...)` |
| `get_nodes_and_edges_by_episode(episode_uuids)` | `GetNodesAndEdgesByEpisodeAsync(episodeUuids)` → `SearchResults` with `Edges` and `Nodes` |
| `remove_episode(episode_uuid)` | `RemoveEpisodeAsync(episodeUuid, strict = false)` |
| `build_communities(group_ids=None)` → `(nodes, edges)` | `BuildCommunitiesAsync(groupIds = null)` → `(Communities, CommunityEdges)` |
| `summarize_saga(saga_id)` | `SummarizeSagaAsync(sagaId)` |
| `clear_data(driver, group_ids=None)` (module function) | `ClearDataAsync(groupIds = null)` |
| `get_default_group_id(provider)` | `Graphiti.GetDefaultGroupId(provider)` (`""` here; `_` only for FalkorDB, which is not supported) |
| `select_saga_summary_episodes(...)` | `Graphiti.SelectSagaSummaryEpisodes(...)`, with `SagaEpisodeContent` for the tuples |

Constants: `EPISODE_WINDOW_LEN` → `Graphiti.EpisodeWindowLen`; `SAGA_SUMMARY_MAX_EPISODE_CHARS`,
`SAGA_SUMMARY_MAX_PROMPT_BYTES`, `MAX_SAGA_EPISODES_FOR_SUMMARY`, `SUMMARY_REBUILD_BATCH_SIZE`,
`SAGA_SUMMARY_MAX_TOKENS` → `Graphiti.SagaSummaryMaxEpisodeChars` and so on.

### Parameter types

| Upstream | graphiti-net |
| --- | --- |
| `entity_types: dict[str, type[BaseModel]]` | `IReadOnlyDictionary<string, Type>`: a C# class or record per type; see [custom-types.md](custom-types.md) |
| `edge_types: dict[str, type[BaseModel]]` | `IReadOnlyDictionary<string, Type>` |
| `edge_type_map: dict[tuple[str, str], list[str]]` | `IReadOnlyDictionary<(string Source, string Target), IReadOnlyList<string>>`, for example `{ [("Person", "Company")] = ["WORKS_AT"] }` |
| `excluded_entity_types: list[str]` | `IReadOnlyList<string>` |
| `saga: str \| SagaNode` | `SagaReference`, which converts implicitly from a `string` name or a `SagaNode` |
| `RawEpisode(name, uuid, content, source_description, source, reference_time)` | `RawEpisode { Name, Uuid, Content, SourceDescription, Source, ReferenceTime, EpisodeMetadata, IdempotencyKey }` |
| `EntityTypeValidationError`, `ValueError` from validation | `EntityTypeValidationException`, `ArgumentException` (same message) |
| `NodeNotFoundError`, `EdgeNotFoundError` | `NodeNotFoundException`, `EdgeNotFoundException` |
| `GroupIdValidationError` | `GroupIdValidationException` |

### Results

| Upstream | graphiti-net |
| --- | --- |
| `AddEpisodeResults(episode, episodic_edges, nodes, edges, communities, community_edges)` | `AddEpisodeResults { Episode, EpisodicEdges, Nodes, Edges, Communities, CommunityEdges, TokenUsage, TokenUsageByPrompt, Replayed }` |
| `AddBulkEpisodeResults(episodes, ...)` | `AddBulkEpisodeResults { Episodes, EpisodicEdges, Nodes, Edges, Communities, CommunityEdges, TokenUsage, TokenUsageByPrompt }` |
| `AddTripletResults(nodes, edges)` | `AddTripletResults { Nodes, Edges }` |
| `SearchResults(edges, edge_reranker_scores, nodes, node_reranker_scores, episodes, episode_reranker_scores, communities, community_reranker_scores)` | `SearchResults { Edges, EdgeRerankerScores, Nodes, NodeRerankerScores, Episodes, EpisodeRerankerScores, Communities, CommunityRerankerScores, NodeEpisodeUuids }`; `SearchResults.merge` → `SearchResults.Merge` |
| `search_results_to_context_string(results)` | `SearchHelpers.SearchResultsToContextString(results)` |

## Records

| Upstream | graphiti-net (`GraphitiNet.Models`) |
| --- | --- |
| `EpisodeType.message` / `json` / `text` / `fact_triple` | `EpisodeType.Message` / `Json` / `Text` / `FactTriple`; stored names via `ToStorageName()` / `EpisodeTypes.FromStorageName` |
| `EpisodicNode`, `EntityNode`, `CommunityNode`, `SagaNode` | same names; storage methods (`save`, `get_by_uuid`, `delete`...) live on the driver and the namespaces, not on the record |
| `EntityEdge`, `EpisodicEdge`, `CommunityEdge`, `HasEpisodeEdge`, `NextEpisodeEdge` | same names |
| `node.generate_name_embedding(embedder)` / `edge.generate_embedding(embedder)` | `graphiti.Nodes.Entity.SaveAsync(node)` and `graphiti.Edges.Entity.SaveAsync(edge)` embed before saving; or `LLMUtils.GenerateEmbeddingAsync(embedder, text)` |
| `node.load_name_embedding(driver)` | `graphiti.Nodes.Entity.LoadEmbeddingsAsync(node)` |

Ids (`Uuid`) are immutable (`init`). Where upstream changes an id in place (`add_triplet` on an id clash), graphiti-net
saves a copy with a new id and returns it.

## Namespaces

`graphiti.Nodes` and `graphiti.Edges` mirror upstream's `graphiti.nodes` and `graphiti.edges`. Upstream's `tx` and
`batch_size` parameters are not ported (drivers decide how to batch and transact).

| Upstream | graphiti-net |
| --- | --- |
| `graphiti.nodes.entity` / `.episode` / `.community` / `.saga` | `graphiti.Nodes.Entity` / `.Episode` / `.Community` / `.Saga` |
| `graphiti.edges.entity` / `.episodic` / `.community` / `.has_episode` / `.next_episode` | `graphiti.Edges.Entity` / `.Episodic` / `.Community` / `.HasEpisode` / `.NextEpisode` |
| `save`, `save_bulk`, `delete`, `delete_by_uuids`, `get_by_uuid`, `get_by_uuids`, `get_by_group_ids(group_ids, limit, uuid_cursor)` | `SaveAsync`, `SaveBulkAsync`, `DeleteAsync`, `DeleteByUuidsAsync`, `GetByUuidAsync`, `GetByUuidsAsync`, `GetByGroupIdsAsync(groupIds, limit, uuidCursor)` |
| nodes: `delete_by_group_id(group_id)` | `DeleteByGroupIdAsync(groupId)` (that node type only; `ClearDataAsync` clears a whole group) |
| `entity.save` / `community.save` / `edges.entity.save` | embed the name or fact, then save, as upstream |
| `entity.load_embeddings`, `load_embeddings_bulk` | `LoadEmbeddingsAsync`, `LoadEmbeddingsBulkAsync` |
| `community.load_name_embedding` | `LoadNameEmbeddingAsync` |
| `episode.get_by_entity_node_uuid`, `episode.retrieve_episodes` | `GetByEntityNodeUuidAsync`, `RetrieveEpisodesAsync` |
| `edges.entity.get_between_nodes`, `get_by_node_uuid` | `GetBetweenNodesAsync`, `GetByNodeUuidAsync` |
| `NotImplementedError` when a driver lacks an operation | every driver implements every operation |

## Search configuration

`GraphitiNet.Search`.

| Upstream | graphiti-net |
| --- | --- |
| `SearchConfig(edge_config, node_config, episode_config, community_config, limit=10, reranker_min_score=0)` | `SearchConfig { EdgeConfig, NodeConfig, EpisodeConfig, CommunityConfig, Limit, RerankerMinScore }` (immutable record: `config with { Limit = 5 }` replaces `model_copy(deep=True)` plus assignment) |
| `EdgeSearchConfig(search_methods, reranker, sim_min_score, mmr_lambda, bfs_max_depth)` | `EdgeSearchConfig { SearchMethods, Reranker, SimMinScore, MmrLambda, BfsMaxDepth }`; same for `NodeSearchConfig`, `EpisodeSearchConfig`, `CommunitySearchConfig` |
| `EdgeSearchMethod.cosine_similarity` / `bm25` / `bfs` | `EdgeSearchMethod.CosineSimilarity` / `Bm25` / `Bfs` (and `NodeSearchMethod`, `EpisodeSearchMethod`, `CommunitySearchMethod`) |
| `EdgeReranker.rrf` / `node_distance` / `episode_mentions` / `mmr` / `cross_encoder` | `EdgeReranker.Rrf` / `NodeDistance` / `EpisodeMentions` / `Mmr` / `CrossEncoder` (and `NodeReranker`, `EpisodeReranker`, `CommunityReranker`) |
| `DEFAULT_SEARCH_LIMIT`, `DEFAULT_MIN_SCORE`, `DEFAULT_MMR_LAMBDA`, `MAX_SEARCH_DEPTH`, `RELEVANT_SCHEMA_LIMIT` | `SearchDefaults.DefaultSearchLimit`, `DefaultMinScore`, `DefaultMmrLambda`, `MaxSearchDepth`, `RelevantSchemaLimit` |
| `search(clients, query, group_ids, config, search_filter, ...)` (module function) | `SearchEngine.SearchAsync(driver, embedder, crossEncoder, query, groupIds, config, searchFilter, ...)` |

Recipes (`search_config_recipes.py` → `SearchConfigRecipes`). Each property returns a fresh copy, so changing one
never affects other searches:

| Upstream | graphiti-net |
| --- | --- |
| `COMBINED_HYBRID_SEARCH_RRF` / `_MMR` / `_CROSS_ENCODER` | `SearchConfigRecipes.CombinedHybridSearchRrf` / `CombinedHybridSearchMmr` / `CombinedHybridSearchCrossEncoder` |
| `EDGE_HYBRID_SEARCH_RRF` / `_MMR` / `_NODE_DISTANCE` / `_EPISODE_MENTIONS` / `_CROSS_ENCODER` | `EdgeHybridSearchRrf` / `EdgeHybridSearchMmr` / `EdgeHybridSearchNodeDistance` / `EdgeHybridSearchEpisodeMentions` / `EdgeHybridSearchCrossEncoder` |
| `NODE_HYBRID_SEARCH_RRF` / `_MMR` / `_NODE_DISTANCE` / `_EPISODE_MENTIONS` / `_CROSS_ENCODER` | `NodeHybridSearchRrf` / `NodeHybridSearchMmr` / `NodeHybridSearchNodeDistance` / `NodeHybridSearchEpisodeMentions` / `NodeHybridSearchCrossEncoder` |
| `COMMUNITY_HYBRID_SEARCH_RRF` / `_MMR` / `_CROSS_ENCODER` | `CommunityHybridSearchRrf` / `CommunityHybridSearchMmr` / `CommunityHybridSearchCrossEncoder` |

Filters (`search_filters.py`):

| Upstream | graphiti-net |
| --- | --- |
| `SearchFilters(node_labels, edge_types, valid_at, invalid_at, created_at, expired_at, edge_uuids, edge_source_node_uuids, edge_target_node_uuids, property_filters)` | `SearchFilters { NodeLabels, EdgeTypes, ValidAt, InvalidAt, CreatedAt, ExpiredAt, EdgeUuids, EdgeSourceNodeUuids, EdgeTargetNodeUuids, PropertyFilters }`; `SearchFilters.None` for no filter |
| `DateFilter(date, comparison_operator)` | `DateFilter { Date, ComparisonOperator }`; date fields hold OR groups of AND lists: `[[a, b], [c]]` |
| `PropertyFilter(property_name, property_value, comparison_operator)` | `PropertyFilter { PropertyName, PropertyValue, ComparisonOperator }` |
| `ComparisonOperator.equals` / `not_equals` / `greater_than` / `less_than` / `greater_than_equal` / `less_than_equal` / `is_null` / `is_not_null` | `ComparisonOperator.Equals` / `NotEquals` / `GreaterThan` / `LessThan` / `GreaterThanEqual` / `LessThanEqual` / `IsNull` / `IsNotNull` |

## Providers

| Upstream | graphiti-net |
| --- | --- |
| `LLMConfig(api_key, model, base_url, temperature, max_tokens, small_model)` | `new LLMConfig(apiKey, model, baseUrl, temperature, maxTokens, smallModel)` (`GraphitiNet.Llm`) |
| `OpenAIClient(config=None, cache=False, client=None, max_tokens=..., reasoning=..., verbosity=...)` | `new OpenAIClient(config, cache, client, maxTokens, reasoning, verbosity)` |
| `AzureOpenAILLMClient`, `OpenAIGenericClient` | `AzureOpenAILLMClient`, `OpenAIGenericClient` |
| Anthropic, Gemini, Groq and other clients | `ChatClientLLMClient` over any `Microsoft.Extensions.AI` `IChatClient` |
| `ModelSize.small` / `medium` | `ModelSize.Small` / `Medium` |
| `OpenAIEmbedder(config=OpenAIEmbedderConfig(embedding_model, api_key, base_url, embedding_dim))` | `new OpenAIEmbedder(new OpenAIEmbedderConfig { EmbeddingModel, ApiKey, BaseUrl, EmbeddingDim })` (`GraphitiNet.Embedder`) |
| `AzureOpenAIEmbedderClient` | `AzureOpenAIEmbedderClient` |
| other embedders | `EmbeddingGeneratorEmbedder` over any `IEmbeddingGenerator<string, Embedding<float>>` |
| `OpenAIRerankerClient(config=None, client=None)` | `new OpenAIRerankerClient(config, client)` (`GraphitiNet.CrossEncoder`) |
| other cross-encoders | `ChatClientCrossEncoder` over any `IChatClient` |
| `EmbedderClient.create` / `create_batch`, `CrossEncoderClient.rank`, `LLMClient.generate_response` | `CreateAsync` / `CreateBatchAsync`, `RankAsync`, `GenerateResponseAsync` |

## Utilities

| Upstream | graphiti-net |
| --- | --- |
| `utils.maintenance.community_operations` (`label_propagation`, `build_communities`, `remove_communities`, `determine_entity_community`, `update_community`, ...) | `Maintenance.CommunityOperations` (`LabelPropagation`, `BuildCommunitiesAsync`, `RemoveCommunitiesAsync`, `DetermineEntityCommunityAsync`, `UpdateCommunityAsync`, ...) |
| `utils.content_chunking` (`should_chunk`, `chunk_json_content`, `chunk_text_content`, `chunk_message_content`, `generate_covering_chunks`, `estimate_tokens`) | `Utils.ContentChunking` (`ShouldChunk`, `ChunkJsonContent`, `ChunkTextContent`, `ChunkMessageContent`, `GenerateCoveringChunks`, `EstimateTokens`); not used by `AddEpisodeAsync`, as upstream |
| `validate_entity_types`, `validate_excluded_entity_types`, `validate_group_id` | `EntityTypesUtils.ValidateEntityTypes`, `EntityTypesUtils.ValidateExcludedEntityTypes`, `Validation.ValidateGroupId` |
| `semaphore_gather` | `Concurrency.SemaphoreGatherAsync` |
| `utils.bulk_utils` (`retrieve_previous_episodes_bulk`, `add_nodes_and_edges_bulk`, `extract_nodes_and_edges_bulk`, `dedupe_nodes_bulk`, `dedupe_edges_bulk`, `resolve_edge_pointers`, `compress_uuid_map`) | `Maintenance.BulkUtils` (`RetrievePreviousEpisodesBulkAsync`, `AddNodesAndEdgesBulkAsync`, `ExtractNodesAndEdgesBulkAsync`, `DedupeNodesBulkAsync`, `DedupeEdgesBulkAsync`, `ResolveEdgePointers`, `CompressUuidMap`) |
| `utils.maintenance.combined_extraction.extract_nodes_and_edges` | `Maintenance.CombinedExtractionOperations.ExtractNodesAndEdgesAsync` (named so it does not clash with the `Prompts.CombinedExtraction` response model) |

## Quickstart side by side

Upstream `examples/quickstart/quickstart_neo4j.py`, shortened:

```python
graphiti = Graphiti(neo4j_uri, neo4j_user, neo4j_password)
try:
    for i, episode in enumerate(episodes):
        await graphiti.add_episode(
            name=f'Freakonomics Radio {i}',
            episode_body=episode['content'] if isinstance(episode['content'], str)
            else json.dumps(episode['content']),
            source=episode['type'],
            source_description=episode['description'],
            reference_time=datetime.now(timezone.utc),
        )

    results = await graphiti.search('Who was the California Attorney General?')
    for result in results:
        print(f'Fact: {result.fact}')

    center_node_uuid = results[0].source_node_uuid
    reranked = await graphiti.search(
        'Who was the California Attorney General?', center_node_uuid=center_node_uuid
    )

    node_search_config = NODE_HYBRID_SEARCH_RRF.model_copy(deep=True)
    node_search_config.limit = 5
    node_results = await graphiti._search(query='California Governor', config=node_search_config)
    for node in node_results.nodes:
        print(node.name, node.summary)
finally:
    await graphiti.close()
```

graphiti-net:

```csharp
using System.Text.Json;
using GraphitiNet;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Models;
using GraphitiNet.Search;

// OPENAI_API_KEY must be set for the default OpenAI clients.
// In production: new Graphiti(new PostgresDriver(connectionString)) (GraphitiNet.PostgreSql).
await using var graphiti = new Graphiti(new InMemoryGraphDriver());
await graphiti.BuildIndicesAndConstraintsAsync();

var episodes = new (string Content, EpisodeType Type, string Description)[]
{
    ("Kamala Harris is the Attorney General of California. She was previously the district attorney for San Francisco.",
        EpisodeType.Text, "podcast transcript"),
    ("As AG, Harris was in office from January 3, 2011 – January 3, 2017", EpisodeType.Text, "podcast transcript"),
    (JsonSerializer.Serialize(new { name = "Gavin Newsom", position = "Governor", state = "California" }),
        EpisodeType.Json, "podcast metadata"),
};

for (var i = 0; i < episodes.Length; i++)
{
    await graphiti.AddEpisodeAsync(
        name: $"Freakonomics Radio {i}",
        episodeBody: episodes[i].Content,
        source: episodes[i].Type,
        sourceDescription: episodes[i].Description,
        referenceTime: DateTimeOffset.UtcNow);
}

var results = await graphiti.SearchAsync("Who was the California Attorney General?");
foreach (var result in results)
{
    Console.WriteLine($"Fact: {result.Fact}");
}

var reranked = await graphiti.SearchAsync(
    "Who was the California Attorney General?", centerNodeUuid: results[0].SourceNodeUuid);

var nodeSearchConfig = SearchConfigRecipes.NodeHybridSearchRrf with { Limit = 5 };
var nodeResults = await graphiti.SearchAsync("California Governor", nodeSearchConfig);
foreach (var node in nodeResults.Nodes)
{
    Console.WriteLine($"{node.Name}: {node.Summary}");
}
```

## Behaviour that differs from upstream

- **`search` limit.** Upstream sets `num_results` on its shared recipe object, so it leaks into later searches that use
  the same recipe. graphiti-net uses a fresh copy per call.
- **`add_episode(update_communities=True)`.** Upstream unpacks the per-entity results into two names, which fails
  unless exactly two entities were extracted. graphiti-net returns every updated community and new membership edge.
- **Reprocessing by `uuid`.** Upstream includes the episode itself among its previous episodes when it is already
  stored; graphiti-net leaves it out. graphiti-net also processes a stored episode in its own group: `groupId` may be
  omitted, and naming another group (in `AddEpisodeAsync` or a `RawEpisode.Uuid` in `AddEpisodeBulkAsync`) throws
  `ArgumentException`, where upstream would read the other group's episodes as context and write facts to it.
- **`add_triplet` across groups.** Upstream links whatever nodes it is given, even from other groups. graphiti-net
  keeps every write inside one group: `AddTripletAsync` throws `ArgumentException` when the source or target entity
  is (or is stored) in another group than the fact.
- **`clear_data()` with no groups** waits for the group lock of every group that holds entities, so it can't race a
  running write.
- **`add_triplet` id clash.** Ids are immutable, so the new fact is a copy with a new id (see [Records](#records)).
- **Label propagation.** Upstream loops forever when two entities joined by two or more facts swap communities each
  round; graphiti-net stops when an assignment repeats.
- **Request scope.** Upstream clones the driver per group for FalkorDB (one database per group). graphiti-net has one
  store for every group, so every call uses the shared driver; group ids only partition data.
- **Text lengths** in content chunking and saga budgets are counted in UTF-16 code units, where Python counts code
  points. They differ only for characters outside the Basic Multilingual Plane, such as emoji.
- **Out-of-range timestamps from the LLM.** Timestamps are parsed exactly as Python's `datetime.fromisoformat` parses
  them, but where the conversion to UTC leaves the datetime range (`0001-01-01T00:00:00+01:00`), Python raises
  `OverflowError`, which upstream does not catch, so the whole extraction fails. graphiti-net skips the value as
  unparseable, as it does any text Python rejects.
- **Ties upstream breaks in set order.** Where upstream picks among equals by iterating a Python set (equally similar
  fuzzy candidates in entity deduplication, dropped attributes restored in `replace` mode, uncovered pairs in
  covering chunks), the order changes from run to run or follows CPython's hashing; graphiti-net uses a fixed order.

> **Group scoping in search.** As upstream, a search with `groupIds` of `null`, `[]` or `[""]` searches **every**
> group, and `""` is the default group id. If your service maps users or tenants to groups, always pass their
> non-empty group ids explicitly, and never let a tenant resolve to `""`.

## graphiti-net additions

These are opt-in or additive; with the defaults, behaviour matches upstream except where the table above says so.

### One write at a time per group (`IGroupLock`)

`AddEpisodeAsync`, `AddEpisodeBulkAsync`, `AddTripletAsync`, `RemoveEpisodeAsync`, `SummarizeSagaAsync`,
`BuildCommunitiesAsync` and `ClearDataAsync` hold the group's lock (or the locks of every group they touch) for the
whole call, so two episodes added to one group at once cannot both create the same entity. The
default `InProcessGroupLock.Shared` works within one process (shared by every `Graphiti` instance in it). Pass your own
`IGroupLock` (`ValueTask<IAsyncDisposable> AcquireAsync(string groupId, CancellationToken)`) for a distributed lock
when several servers write to one database, or `NoGroupLock.Instance` for upstream's behaviour.

### Call context on every model call (`GraphitiCallContext`)

Every LLM, embedding and rerank call made by a `Graphiti` method runs inside an ambient `GraphitiCallContext`, read
with `GraphitiCallContext.Current` from any `LLMClient`, `EmbedderClient` or `CrossEncoderClient` implementation:
`Operation` (`add_episode`, `search`, `summarize_saga`...), `GroupId`, `EpisodeUuid`, `EpisodeMetadata` and
`PromptName` (for LLM calls). A host can route restricted data to approved providers with it. `AddEpisodeAsync` takes
`episodeMetadata` (stored on the episode) and `RawEpisode` has `EpisodeMetadata` for this.

### Token usage per call

`AddEpisodeResults.TokenUsage` and `TokenUsageByPrompt` (also on `AddBulkEpisodeResults`) give the tokens that call
used, measured through its own call context, so concurrent calls do not mix. `Graphiti.TokenTracker` still totals
every call, as upstream.

### Model call gate and cancellation (`IModelCallGate`)

An optional `IModelCallGate` (or `DelegateModelCallGate`) is asked before each LLM, embedding and rerank call made in
a `Graphiti` operation, with a `ModelCallInfo` (kind, prompt, operation, group, episode, metadata). A refusal throws
`ModelCallRefusedException`. With a gate configured, `AddEpisodeAsync` saves a new episode before any model call, so
a refused episode stays saved but unprocessed; process it later with `AddEpisodeAsync(..., uuid:
exception.EpisodeUuid)`, which reuses the stored content. Without a gate nothing is saved until the end, as upstream.
The `CancellationToken` is checked between pipeline steps.

### Idempotency keys on episodes

`AddEpisodeAsync(..., idempotencyKey: "...")` and `RawEpisode.IdempotencyKey` let a host retry a failed call without a
second episode, and without looking the episode up first (upstream's way out is to find the saved episode and pass its
`uuid`). The key is stored as `EpisodicNode.IdempotencyKey`, unique per group (at most
`Graphiti.MaxIdempotencyKeyLength`, 255, characters; compared exactly), and cannot be combined with `uuid`. With a key,
the episode is saved under it before any model call. A later call with the same key in the group:

| Earlier call | This call |
| --- | --- |
| none | creates the episode, as without a key |
| saved the episode, failed before its entities and facts were saved (extraction, deduplication, resolution, a refused model call) | processes the stored episode (its stored name, content, source, reference time and metadata; the other content arguments are ignored, as with `uuid`) |
| saved its entities and facts (they are saved with the episode in one transaction) | extracts nothing: returns the stored episode, entities, facts and mentions with `AddEpisodeResults.Replayed = true` and no token usage, joins the saga if the earlier call did not get to it, and updates communities when `updateCommunities` is set |

An episode counts as processed once it mentions an entity or states a fact; one from which nothing was extracted is
processed again, which cannot duplicate anything. In `AddEpisodeBulkAsync`, processed episodes are left out of
extraction and returned with what is stored for them, the others are processed with the batch, and a saga gets only
the episodes it does not have yet; keys must be distinct within a batch.

Concurrency: the unique index on (group, key) decides, so two concurrent calls with one key create one episode
(`IEpisodeNodeOperations.AddOrGetByIdempotencyKeyAsync` saves the episode or returns the one that holds the key).
Under the default group lock the second call waits and then replays the first call's result. Without a lock shared by
both callers (`NoGroupLock`, or several servers with the in-process lock) both may process the one episode, so use a
distributed `IGroupLock` there. Without a key, behaviour is upstream's. Existing PostgreSQL and SQL Server databases
get the new column and index from `BuildIndicesAndConstraintsAsync`; EF Core migration users add a migration (see
[postgresql.md](postgresql.md#upgrading-from-010) and [sqlserver.md](sqlserver.md#upgrading-from-010)).

### Retrying transient provider errors (`ModelCallRetryOptions`)

In upstream a rate limit (HTTP 429), a 5xx or a timeout that outlasts the client's own retries fails the whole
`add_episode`. `new Graphiti(..., modelCallRetry: new ModelCallRetryOptions())` retries each LLM prompt, embedding and
rerank call a `Graphiti` operation makes, with exponential backoff and jitter. Off by default, so behaviour stays
upstream's.

| Setting | Default | Meaning |
| --- | --- | --- |
| `MaxAttempts` | 4 | attempts per call, including the first |
| `BaseDelay` | 1 s | backoff before the second attempt, doubling after; each wait is drawn between half the backoff and all of it |
| `MaxDelay` | 60 s | the longest wait, also for a `Retry-After` |
| `RespectRetryAfter` | `true` | wait as long as the provider asks (`Retry-After` or `retry-after-ms` header of a `ClientResultException`, or `RateLimitException.RetryAfter`), capped at `MaxDelay` |
| `ShouldRetry` | `ModelCallRetryOptions.IsTransient` | which errors are retried: `RateLimitException`, HTTP 408, 429 and 5xx, connection failures, timeouts (inner exceptions too); not malformed or empty answers, refusals or a refused gate |
| `TimeProvider` | system clock | for tests |

- Each LLM attempt rebuilds the prompt, so nothing a failed attempt appended (such as `BaseOpenAIClient`'s
  "previous response attempt was invalid" re-prompt, which only handles invalid answers and never sees a 429) carries
  over.
- Token usage is recorded once, by the attempt that succeeds.
- A model call gate is asked once per logical call; retries of an allowed call are not asked again, and a refusal is
  never retried.
- Cancellation stops the wait between attempts; a cancelled call is not retried.
- The policy sits above the clients' own retries, which stay as upstream's: the base `LLMClient`
  (`ChatClientLLMClient`, `OpenAIGenericClient`) retries rate limits and 5xx errors itself, 4 attempts 5 to 120
  seconds apart, and the OpenAI SDK retries 429 and 5xx responses. Attempts multiply; lower one layer if needed.

### Strict episode removal and clearing a group

`RemoveEpisodeAsync(episodeUuid, strict: true)` leaves no text from the episode in the graph: every fact listing the
episode is deleted (also facts other episodes state, since merged text can carry the removed episode's details), the
surviving mentioned entities lose their custom attributes and get their summaries rebuilt from the remaining
episodes (cleared if the rebuild fails), communities with a mentioned entity are deleted, and the episode's sagas get
their summary reset for the next `SummarizeSagaAsync`. `ClearDataAsync(groupIds)` deletes everything in the groups:
episodes, entities, facts, mentions, sagas and their links, and communities.

### Episode access filter on search

Both `SearchAsync` overloads and `SearchAdvancedAsync` take `IEpisodeAccessFilter? episodeAccessFilter`. With it,
search hides facts, entities and episodes behind episodes the caller may not read, before reranking and the limit, so
the limit is filled from visible results; communities are not returned. `SearchResults.NodeEpisodeUuids` says which
episodes stand behind each entity. See `GraphitiNet.Search.IEpisodeAccessFilter` for the rules.

### Other

- `Graphiti.CreateInMemory(...)` for demos and tests.
- `ContentChunking` is public, for hosts that chunk large content into several episodes.
