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
- runs twice, on `InMemoryGraphDriver` and on `PostgresDriver` (a theory over both drivers). The PostgreSQL case
  uses its own schema, dropped afterwards. It is skipped when PostgreSQL can't be reached (`GRAPHITI_TEST_POSTGRES`,
  or `Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests`). CI runs a pgvector service, so
  both cases run there.

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
| CI runs the customer-support dataset through the port on recorded answers and compares the result with upstream's. | `GraphitiNet.Parity.Tests`: `ParityRunTests.Ingestion_matches_upstream_on_recorded_answers("customer-support")` | pass | known gap, skipped (see below) |
| The Arabic dataset runs through parity, on recorded answers. | `ParityRunTests.Ingestion_matches_upstream_on_recorded_answers("arabic")` and `ParityRunTests.Ingestion_on_postgresql_matches_upstream_on_recorded_answers("arabic")` | pass | pass |

How the tests check these:

- **Episode to search result.** The three episodes are "Alice lives in Paris." (January), "Bob works at Acme."
  (March) and "Alice moved to London." (June). The scripted model reports that the London fact contradicts the Paris
  fact. The library's resolution and invalidation code then closes the Paris fact. `SearchAsync` returns both facts:
  - London is current: valid from June, with no `InvalidAt` or `ExpiredAt`;
  - Paris is closed: valid from January, `InvalidAt` set to June, `ExpiredAt` set;
  - both facts belong to one Alice entity.
- **Customer-support on PostgreSQL.** This run is skipped. PostgreSQL ranks keyword matches with `ts_rank`, not
  BM25, so in one fact-resolution step a different fact makes the candidate cut-off than on Neo4j. The criterion as
  written (the dataset runs in CI and is compared with upstream) is met by the in-memory run. The PostgreSQL run is
  an explicitly skipped test, `ParityRunTests.Ingestion_on_postgresql_matches_upstream_on_customer_support`, and
  remains open.
