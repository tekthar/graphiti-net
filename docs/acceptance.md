# Acceptance tests for AbpAgent

[AbpAgent](https://github.com/tekthar/abp-agent), graphiti-net's first consumer, set its requirements in
[#1](https://github.com/tekthar/graphiti-net/issues/1) and [#2](https://github.com/tekthar/graphiti-net/issues/2).
This page maps each "done when" criterion, and each other requirement of #1, to the test that checks it.

## How the tests work

The tests are in [tests/GraphitiNet.Acceptance.Tests](../tests/GraphitiNet.Acceptance.Tests). Each one:

- uses only the public API: `AddEpisodeAsync`, `SearchAsync`, `RemoveEpisodeAsync`, `graphiti.Nodes` and
  `graphiti.Edges`. The project gets no access to the library's internals;
- runs with a scripted model (`ScriptedLlm`) that answers each prompt by name with fixed JSON for the episode text in
  the prompt. The library's own extraction, entity and fact deduplication, contradiction handling, temporal
  invalidation and summary code runs on those answers. The embedder (`WordHashEmbedder`) is a deterministic
  bag-of-words hash;
- runs on `InMemoryGraphDriver`, `PostgresDriver` and `SqlServerDriver` (a theory over the drivers). The database
  cases use their own schema, dropped afterwards. Each is skipped when its server can't be reached: PostgreSQL at
  `GRAPHITI_TEST_POSTGRES` (or `Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests`), SQL
  Server at `GRAPHITI_TEST_SQLSERVER` (or the `sa` login of a local SQL Server 2025 container, database
  `graphiti_acceptance_tests`, as in [sqlserver.md](sqlserver.md)). CI runs both servers, so every case runs there.

Run them with `dotnet test tests/GraphitiNet.Acceptance.Tests`.

Status: **pass** means the test passes on both drivers as of the commit that added this page. No library change was
needed to meet any criterion.

## #1: done when

| Criterion | Test | In memory | PostgreSQL |
| --- | --- | --- | --- |
| A test adds episodes to two groups, filters one episode out, and gets back neither the fact that episode supports nor a summary built from it. | `Issue1Tests.Episodes_in_two_groups_with_one_filtered_out_return_neither_its_fact_nor_a_summary_built_from_it` | pass | pass |
| After a strict removal, no text from the removed episode is left in any fact or summary. | `Issue1Tests.After_a_strict_removal_no_text_from_the_removed_episode_is_left_in_any_fact_or_summary` | pass | pass |
| Two concurrent adds to one group make one entity. | `Issue1Tests.Two_concurrent_adds_to_one_group_make_one_entity` (control: `Issue1Tests.Control_without_the_group_lock_two_concurrent_adds_can_make_two_entities`) | pass | pass |
| A model call receives the episode's group id and metadata, and an AddEpisodeAsync reports the tokens it used. | `Issue1Tests.A_model_call_receives_the_episodes_group_id_and_metadata_and_add_episode_reports_its_tokens` | pass | pass |
| An Arabic episode can be found by keyword search. | `Issue1Tests.An_Arabic_episode_can_be_found_by_keyword_search` | pass | pass |

What each test shows:

- **Filtering.** The test adds two episodes to group A and one to group B, then searches both groups with a filter
  that hides one of A's episodes. The hidden episode's fact doesn't come back, and neither do any entities whose
  summary was built from it: Alice, whose summary has the hidden fact appended, and Riverside Club. The other facts
  from both groups still come back. A control search without the filter returns the hidden fact and Alice's summary.
- **Strict removal.** The removed episode states two facts. A later episode restates one of them, so that fact lists
  both episodes and carries the removed episode's wording. After `RemoveEpisodeAsync(strict: true)`:
  - no fact or entity name or summary contains the removed episode's words or facts;
  - the entity that only the removed episode mentioned is deleted;
  - Alice's and Carol's summaries are rebuilt from the remaining episodes only.

  A check run during development, with `strict: false`, fails this test.
- **Concurrent adds.** The scripted model holds the first add inside entity extraction while the second add starts,
  so both calls are in flight at once. With the default group lock, the second waits, and the group ends with one
  Alice that both episodes mention. The control test passes `NoGroupLock.Instance` and lets both adds into
  extraction together. It gets two Alices, so the one-entity result comes from the lock.
- **Call context and tokens.** Every LLM call in the add sees `Operation = add_episode`, the group id, the episode id
  and the episode's metadata in `GraphitiCallContext.Current`. The embedding calls see the group id.
  `AddEpisodeResults.TokenUsage` and `TokenUsageByPrompt` equal the tokens of that add's own calls. The test runs an
  earlier add on the same instance first, and its tokens aren't counted.
- **Arabic.** On PostgreSQL the group uses the `arabic` text search configuration. A keyword-only (BM25) search for
  `شركة النور` finds the fact, the entity and the episode.

## #1: other requirements

| Requirement | Test | In memory | PostgreSQL |
| --- | --- | --- | --- |
| Entities never merge across groups. | `Issue1Tests.Entities_never_merge_across_groups` | pass | pass |
| One search names several groups at once; results carry their group id and episode ids. | `Issue1Tests.One_search_names_several_groups_and_results_carry_their_group_id_and_episode_ids` | pass | pass |
| Host data goes in `EpisodeMetadata`. | `Issue1Tests.Host_data_goes_in_episode_metadata` | pass | pass |
| Cancellation is honoured between steps. | `Issue1Tests.Cancellation_is_honoured_between_steps` | pass | pass |
| A budget hook can refuse a model call; the episode stays saved but unprocessed and can be processed later. | `Issue1Tests.A_budget_hook_can_refuse_a_model_call_and_the_episode_stays_saved_unprocessed_and_can_be_processed_later` | pass | pass |
| The `ModelBuilder` extension takes a schema name and table prefix. | `PostgreSqlTests.The_ModelBuilder_extension_takes_a_schema_name_and_table_prefix` | n/a (no tables) | pass |
| Keyword search language per group (PostgreSQL `arabic`). | `PostgreSqlTests.Keyword_search_language_is_set_per_group` | n/a (one analyzer) | pass |

Notes:

- **Groups.** The same text is added to two groups. The test checks that each group gets its own entities and its own
  fact, and that each fact lists only its own episode.
- **Several groups in one search.** A search names groups A and B, while a third group holds matching data. Each
  fact carries its `GroupId` and the id of the episode it came from (`EntityEdge.Episodes`). With an access filter,
  each entity's episode ids are in `SearchResults.NodeEpisodeUuids`. Nothing from the third group comes back.
- **Cancellation.** The token is cancelled while entity extraction is running. The add throws
  `OperationCanceledException`, makes no further model call, and saves nothing.
- **Budget hook.** A `DelegateModelCallGate` refuses the first call. The add throws `ModelCallRefusedException`,
  which carries the episode's id, group and metadata. The episode is saved with its content but with no entities or
  facts. Once the gate allows calls again, `AddEpisodeAsync(..., uuid: exception.EpisodeUuid)` processes the stored
  episode, and its fact can be searched.
- **ModelBuilder.** A host `DbContext` calls `modelBuilder.AddGraphiti(new PostgresGraphOptions { Schema, TablePrefix
  = "abp_graph_" })`, and the driver runs over that context. Every graph table is created in that schema with the
  prefix. The host's create script includes the graph tables. An episode added through the host context can be
  searched.
- **Language per group.** One group uses `arabic` and another the default `simple`, and both get the same Arabic
  episode. A search for `الشركة` ("the company", with the article) finds the fact only in the `arabic` group, where
  stemming matches it to `شركة`. Each row stores its group's configuration.

## #2: done when

| Criterion | Test | In memory | PostgreSQL |
| --- | --- | --- | --- |
| One test in CI goes from episode text to a search result through real PostgreSQL, with a fake model. It adds three episodes, one of which contradicts another, then finds the current fact and the fact that was closed. | `Issue2Tests.Episode_text_to_search_result_finds_the_current_fact_and_the_closed_fact` | pass | pass |
| CI runs the customer-support dataset through the port on recorded answers and compares the result with upstream's. | `GraphitiNet.Parity.Tests`: `ParityRunTests.Ingestion_matches_upstream_on_recorded_answers("customer-support")` | pass | pass |
| The Arabic dataset runs through parity, on recorded answers. | `ParityRunTests.Ingestion_matches_upstream_on_recorded_answers("arabic")` and `ParityRunTests.Ingestion_on_postgresql_matches_upstream_on_recorded_answers("arabic")` | pass | pass |

How the tests check these:

- **Episode to search result.** The three episodes are "Alice lives in Paris." (January), "Bob works at Acme."
  (March) and "Alice moved to London." (June). The scripted model reports that the London fact contradicts the Paris
  fact. The library's resolution and invalidation code then closes the Paris fact. `SearchAsync` returns both facts:
  - London is current: valid from June, with no `InvalidAt` or `ExpiredAt`;
  - Paris is closed: valid from January, `InvalidAt` set to June, `ExpiredAt` set;
  - both facts belong to one Alice entity.
- **Customer-support on PostgreSQL.** `ParityRunTests.Ingestion_on_postgresql_matches_upstream_on_recorded_answers("customer-support")`
  passes since the PostgreSQL driver ranks keyword search with BM25, as upstream's Lucene does.

## #10: reads that apply the episode access filter

| Criterion | Test | In memory | PostgreSQL | SQL Server |
| --- | --- | --- | --- | --- |
| Records from a hidden episode are left out of the reads. | `Issue10Tests.Reads_leave_out_records_from_a_hidden_episode` | pass | pass | pass |
| Paging is stable. | `Issue10Tests.Paging_is_stable_under_filtering_and_writes_between_pages` | pass | pass | pass |
| Search and these reads agree on what one user can see. | `Issue10Tests.Search_and_reads_agree_on_what_one_user_can_see` | pass | pass | pass |
| With no filter, the reads return what the namespace methods return. | `Issue10Tests.Without_a_filter_reads_return_what_the_namespace_methods_return` | pass | pass | pass |

- **Hidden records.** Five episodes in one group; the filter hides two. The group graph, read two entities a page,
  holds the five entities only readable episodes mention and the three facts from readable episodes. Alice, whom a
  hidden episode also mentions, is left out, but her readable fact is in her edges (`GetFactsByNodeUuidAsync`). The
  episodes, read one a page, are the three readable ones, and no page before the last is short.
- **Paging.** With a page of one, every page but the last holds one entity, the entities come in descending id order,
  and no fact repeats. An episode added after the first page of two neither repeats nor drops an entity that was
  there before.
- **Agreement.** For every entity, fact and episode an unfiltered search finds, the filtered search returns it if and
  only if the reads do.

## #6: a cross-encoder that keeps the given order

| Criterion | Test | In memory | PostgreSQL | SQL Server |
| --- | --- | --- | --- | --- |
| The order is kept and the scores fall with rank. | `GraphitiNet.Core.Tests`: `PassThroughCrossEncoderClientTests.Keeps_the_given_order_and_scores_fall_with_rank` | pass | n/a | n/a |
| A search with the cross-encoder reranker gives the same order as with RRF alone. | `Issue6Tests.The_cross_encoder_reranker_with_the_pass_through_client_gives_the_rrf_order` | pass | pass | pass |

- **Same order.** Facts searched by keyword and vector, entities and episodes by keyword, limit 3, once with the RRF
  rerankers and once with the cross-encoder rerankers over `PassThroughCrossEncoderClient`: the facts, entities and
  episodes come back in the same order. Entities use one search method because, as upstream, they reach the
  cross-encoder in the order the methods found them rather than in RRF order.
