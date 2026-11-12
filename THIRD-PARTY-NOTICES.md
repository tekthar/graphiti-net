# Third-party notices

## Graphiti

graphiti-net is a rewrite of Graphiti in C#. Files translated from Graphiti's Python source, prompts or tests keep
Graphiti's copyright line and name the upstream file and release they come from. They are listed here as they are
added.

Baseline: Graphiti v0.31.2.

| graphiti-net file | Upstream file |
| --- | --- |
| `src/GraphitiNet.Core/CrossEncoder/ChatClientCrossEncoder.cs` | `graphiti_core/cross_encoder/gemini_reranker_client.py` |
| `src/GraphitiNet.Core/CrossEncoder/CrossEncoderClient.cs` | `graphiti_core/cross_encoder/client.py` |
| `src/GraphitiNet.Core/CrossEncoder/OpenAIRerankerClient.cs` | `graphiti_core/cross_encoder/openai_reranker_client.py` |
| `src/GraphitiNet.Core/Driver/GraphDriver.cs` | `graphiti_core/driver/driver.py`, `graphiti_core/driver/operations/*.py` |
| `src/GraphitiNet.Core/Embedder/AzureOpenAIEmbedderClient.cs` | `graphiti_core/embedder/azure_openai.py` |
| `src/GraphitiNet.Core/Embedder/EmbedderClient.cs` | `graphiti_core/embedder/client.py` |
| `src/GraphitiNet.Core/Embedder/OpenAIEmbedder.cs` | `graphiti_core/embedder/openai.py` |
| `src/GraphitiNet.Core/Errors.cs` | `graphiti_core/errors.py` |
| `src/GraphitiNet.Core/Graphiti.Ingestion.cs` | `graphiti_core/graphiti.py` |
| `src/GraphitiNet.Core/Graphiti.Seams.cs` | `graphiti_core/graphiti.py` |
| `src/GraphitiNet.Core/Graphiti.cs` | `graphiti_core/graphiti.py` |
| `src/GraphitiNet.Core/GraphitiClients.cs` | `graphiti_core/graphiti_types.py` |
| `src/GraphitiNet.Core/GraphitiResults.cs` | `graphiti_core/graphiti.py (AddEpisodeResults, AddBulkEpisodeResults, AddTripletResults)`, `graphiti_core/utils/bulk_utils.py (RawEpisode)` |
| `src/GraphitiNet.Core/Llm/AzureOpenAILLMClient.cs` | `graphiti_core/llm_client/azure_openai_client.py` |
| `src/GraphitiNet.Core/Llm/BaseOpenAIClient.cs` | `graphiti_core/llm_client/openai_base_client.py` |
| `src/GraphitiNet.Core/Llm/LLMCache.cs` | `graphiti_core/llm_client/cache.py` |
| `src/GraphitiNet.Core/Llm/LLMClient.cs` | `graphiti_core/llm_client/client.py` |
| `src/GraphitiNet.Core/Llm/LLMConfig.cs` | `graphiti_core/llm_client/config.py` |
| `src/GraphitiNet.Core/Llm/LLMErrors.cs` | `graphiti_core/llm_client/errors.py` |
| `src/GraphitiNet.Core/Llm/LLMUtils.cs` | `graphiti_core/llm_client/utils.py` |
| `src/GraphitiNet.Core/Llm/OpenAIClient.cs` | `graphiti_core/llm_client/openai_client.py` |
| `src/GraphitiNet.Core/Llm/OpenAIGenericClient.cs` | `graphiti_core/llm_client/openai_generic_client.py` |
| `src/GraphitiNet.Core/Llm/TokenUsageTracker.cs` | `graphiti_core/llm_client/token_tracker.py` |
| `src/GraphitiNet.Core/Maintenance/AttributeUtils.cs` | `graphiti_core/utils/maintenance/attribute_utils.py` |
| `src/GraphitiNet.Core/Maintenance/BulkUtils.cs` | `graphiti_core/utils/bulk_utils.py` |
| `src/GraphitiNet.Core/Maintenance/CommunityOperations.cs` | `graphiti_core/utils/maintenance/community_operations.py` |
| `src/GraphitiNet.Core/Maintenance/DanglingEndpoints.cs` | `graphiti_core/utils/maintenance/dangling_endpoints.py` |
| `src/GraphitiNet.Core/Maintenance/DedupHelpers.cs` | `graphiti_core/utils/maintenance/dedup_helpers.py` |
| `src/GraphitiNet.Core/Maintenance/EdgeOperations.cs` | `graphiti_core/utils/maintenance/edge_operations.py` |
| `src/GraphitiNet.Core/Maintenance/EntityTypesUtils.cs` | `graphiti_core/utils/ontology_utils/entity_types_utils.py`, `validate_excluded_entity_types in graphiti_core/helpers.py` |
| `src/GraphitiNet.Core/Maintenance/GraphDataOperations.cs` | `graphiti_core/utils/maintenance/graph_data_operations.py` |
| `src/GraphitiNet.Core/Maintenance/NodeOperations.cs` | `graphiti_core/utils/maintenance/node_operations.py`, `create_entity_node_embeddings in graphiti_core/nodes.py` |
| `src/GraphitiNet.Core/Maintenance/TemporalEdgeUtils.cs` | `graphiti_core/utils/maintenance/temporal_edge_utils.py` |
| `src/GraphitiNet.Core/Models/Edges.cs` | `graphiti_core/edges.py` |
| `src/GraphitiNet.Core/Models/Nodes.cs` | `graphiti_core/nodes.py` |
| `src/GraphitiNet.Core/Namespaces/EdgeNamespace.cs` | `graphiti_core/namespaces/edges.py` |
| `src/GraphitiNet.Core/Namespaces/NodeNamespace.cs` | `graphiti_core/namespaces/nodes.py` |
| `src/GraphitiNet.Core/Namespaces/RecordNamespace.cs` | `graphiti_core/namespaces/nodes.py`, `graphiti_core/namespaces/edges.py` |
| `src/GraphitiNet.Core/Prompts/ChatPrompt.cs` | `graphiti_core/prompts/models.py` |
| `src/GraphitiNet.Core/Prompts/DedupeEdges.cs` | `graphiti_core/prompts/dedupe_edges.py` |
| `src/GraphitiNet.Core/Prompts/DedupeNodes.cs` | `graphiti_core/prompts/dedupe_nodes.py` |
| `src/GraphitiNet.Core/Prompts/Eval.cs` | `graphiti_core/prompts/eval.py` |
| `src/GraphitiNet.Core/Prompts/ExtractEdges.cs` | `graphiti_core/prompts/extract_edges.py` |
| `src/GraphitiNet.Core/Prompts/ExtractNodes.cs` | `graphiti_core/prompts/extract_nodes.py` |
| `src/GraphitiNet.Core/Prompts/ExtractNodesAndEdges.cs` | `graphiti_core/prompts/extract_nodes_and_edges.py` |
| `src/GraphitiNet.Core/Prompts/Message.cs` | `graphiti_core/prompts/models.py` |
| `src/GraphitiNet.Core/Prompts/PromptHelpers.cs` | `graphiti_core/prompts/prompt_helpers.py` |
| `src/GraphitiNet.Core/Prompts/PromptLibrary.cs` | `graphiti_core/prompts/lib.py` |
| `src/GraphitiNet.Core/Prompts/PromptNames.cs` | `graphiti_core/prompts/names.py` |
| `src/GraphitiNet.Core/Prompts/Snippets.cs` | `graphiti_core/prompts/snippets.py` |
| `src/GraphitiNet.Core/Prompts/SummarizeNodes.cs` | `graphiti_core/prompts/summarize_nodes.py` |
| `src/GraphitiNet.Core/Prompts/SummarizeSagas.cs` | `graphiti_core/prompts/summarize_sagas.py` |
| `src/GraphitiNet.Core/Search/SearchConfig.cs` | `graphiti_core/search/search_config.py` |
| `src/GraphitiNet.Core/Search/SearchConfigRecipes.cs` | `graphiti_core/search/search_config_recipes.py` |
| `src/GraphitiNet.Core/Search/SearchEngine.cs` | `graphiti_core/search/search.py` |
| `src/GraphitiNet.Core/Search/SearchFilters.cs` | `graphiti_core/search/search_filters.py` |
| `src/GraphitiNet.Core/Search/SearchHelpers.cs` | `graphiti_core/search/search_helpers.py`, `graphiti_core/prompts/prompt_helpers.py (to_prompt_json)` |
| `src/GraphitiNet.Core/Search/SearchTracing.cs` | `graphiti_core/search/search.py (_trace_phase)`, `graphiti_core/tracer.py` |
| `src/GraphitiNet.Core/Search/SearchUtils.cs` | `graphiti_core/search/search_utils.py`, `graphiti_core/helpers.py (normalize_l2)` |
| `src/GraphitiNet.Core/Utils/Concurrency.cs` | `graphiti_core/helpers.py` |
| `src/GraphitiNet.Core/Utils/ContentChunking.cs` | `graphiti_core/utils/content_chunking.py`, `the CHUNK_* settings of graphiti_core/helpers.py` |
| `src/GraphitiNet.Core/Utils/DateTimeUtils.cs` | `graphiti_core/utils/datetime_utils.py` |
| `src/GraphitiNet.Core/Utils/TextUtils.cs` | `graphiti_core/utils/text_utils.py` |
| `src/GraphitiNet.Core/Utils/Validation.cs` | `graphiti_core/helpers.py` |
| `src/GraphitiNet.PostgreSql/Internal/FilterSql.cs` | `graphiti_core/search/search_filters.py` |
| `src/GraphitiNet.PostgreSql/Operations/GraphMaintenanceOperations.cs` | `graphiti_core/driver/neo4j/operations/graph_ops.py` |
| `src/GraphitiNet.PostgreSql/Operations/NodeOperations.cs` | `graphiti_core/driver/neo4j/operations/episode_node_ops.py`, `entity_node_ops.py`, `community_node_ops.py`, `saga_node_ops.py`, `entity_edge_ops.py` |
| `src/GraphitiNet.PostgreSql/Operations/SearchOperations.cs` | `graphiti_core/driver/neo4j/operations/search_ops.py`, `graphiti_core/search/search_utils.py` |
| `tests/GraphitiNet.Core.Tests/CrossEncoder/ChatClientCrossEncoderTests.cs` | `tests/cross_encoder/test_gemini_reranker_client.py` |
| `tests/GraphitiNet.Core.Tests/Embedder/OpenAIEmbedderTests.cs` | `tests/embedder/test_openai.py`, `tests/embedder/embedder_fixtures.py` |
| `tests/GraphitiNet.Core.Tests/Graphiti/GraphitiTests.cs` | `tests/test_graphiti_mock.py (the parts that need no extraction)`, `tests/test_request_scope_concurrency.py`, `tests/test_add_triplet.py (signature-level parts)` |
| `tests/GraphitiNet.Core.Tests/Graphiti/SagaTests.cs` | `tests/test_saga_refetch_episode_uuids.py`, `tests/test_saga_summary_budget.py` |
| `tests/GraphitiNet.Core.Tests/Llm/LLMCacheTests.cs` | `tests/llm_client/test_cache.py` |
| `tests/GraphitiNet.Core.Tests/Llm/LLMClientTests.cs` | `tests/llm_client/test_client.py` |
| `tests/GraphitiNet.Core.Tests/Llm/LLMErrorsTests.cs` | `tests/llm_client/test_errors.py` |
| `tests/GraphitiNet.Core.Tests/Llm/OpenAIClientTests.cs` | `tests/llm_client/test_openai_client.py` |
| `tests/GraphitiNet.Core.Tests/Llm/OpenAIGenericClientTests.cs` | `tests/llm_client/test_openai_generic_client.py` |
| `tests/GraphitiNet.Core.Tests/Llm/TokenUsageTrackerTests.cs` | `tests/llm_client/test_token_tracker.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/AttributeUtilsTests.cs` | `tests/utils/maintenance/test_attribute_utils.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/BulkUtilsEdgeTests.cs` | `tests/utils/maintenance/test_bulk_utils.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/CommunityOperationsTests.cs` | `tests/utils/maintenance/test_remove_communities.py`, `the community tests of tests/test_graphiti_mock.py (test_determine_entity_community, test_get_community_clusters)` |
| `tests/GraphitiNet.Core.Tests/Maintenance/DedupHelpersTests.cs` | `tests/utils/maintenance/test_node_operations.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/EdgeDedupScenarioTests.cs` | `tests/utils/maintenance/test_edge_dedup_scenarios.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/EdgeOperationsTests.cs` | `tests/utils/maintenance/test_edge_operations.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/EntityExtractionTests.cs` | `tests/utils/maintenance/test_entity_extraction.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/NodeOperationsTests.cs` | `tests/utils/maintenance/test_node_operations.py` |
| `tests/GraphitiNet.Core.Tests/Maintenance/TemporalEdgeUtilsTests.cs` | `tests/utils/maintenance/test_temporal_edge_utils.py` |
| `tests/GraphitiNet.Core.Tests/Models/NodeLabelSecurityTests.cs` | `tests/test_node_label_security.py` |
| `tests/GraphitiNet.Core.Tests/Prompts/PromptLibraryTests.cs` | `tests/prompts/test_configurable_prompt_library.py`, `test_prompt_names.py`, `test_prompt_exports.py`, `test_legacy_prompt_compatibility.py`, `test_complete_prompt.py` |
| `tests/GraphitiNet.Core.Tests/Search/EdgeCrossEncoderRrfShortlistTests.cs` | `tests/utils/search/test_edge_cross_encoder_rrf_shortlist.py` |
| `tests/GraphitiNet.Core.Tests/Search/EpisodeMentionsRerankerTests.cs` | `tests/utils/search/test_episode_mentions_reranker.py` |
| `tests/GraphitiNet.Core.Tests/Search/SearchSecurityTests.cs` | `tests/utils/search/test_search_security.py` |
| `tests/GraphitiNet.Core.Tests/Search/SearchTracingTests.cs` | `tests/utils/search/test_search_tracing.py` |
| `tests/GraphitiNet.Core.Tests/Search/SearchUtilsTests.cs` | `tests/utils/search/search_utils_test.py` |
| `tests/GraphitiNet.Core.Tests/Utils/ContentChunkingTests.cs` | `tests/utils/test_content_chunking.py` |
| `tests/GraphitiNet.Core.Tests/Utils/TextUtilsTests.cs` | `tests/test_text_utils.py`, `tests/utils/test_concatenate_episodes.py` |
| `tests/GraphitiNet.Core.Tests/Prompts/Fixtures/prompt_renders.json`, `response_schemas.json` | Prompt text and schemas generated from `graphiti_core/prompts/*.py` |
| `tests/GraphitiNet.Core.Tests/Prompts/Fixtures/prompt_contexts.json` | The `upstream_test` context from `tests/prompts/test_legacy_prompt_compatibility.py` |

Source: https://github.com/getzep/graphiti/tree/v0.31.2

```
Copyright 2024, Zep Software, Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```
