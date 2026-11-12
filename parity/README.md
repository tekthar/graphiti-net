# Parity with Python Graphiti

graphiti-net is correct when it does what Python Graphiti does on the same input. This folder holds the datasets,
the recordings of upstream's model answers, and the scripts that produce them.

| Path | What it is |
| --- | --- |
| `datasets/*.json` | Episodes (with reference times) and search queries |
| `python/run_upstream.py` | Runs a dataset through Python Graphiti v0.31.2 on Neo4j and writes what it produced |
| `recordings/<dataset>.upstream.json` | Upstream's entities, facts (with validity and expiry) and search results |
| `recordings/<dataset>.recording.json` | Every prompt upstream sent and the answer it got, and every embedding |
| `python/generate_differential_fixtures.py` | Writes the [differential test](#differential-tests) fixtures from upstream's own functions |
| `recordings/<dataset>-bulk.*.json` | The same, with the whole dataset ingested by one `add_episode_bulk` call (`--bulk`) |

## How the comparison works

Live model runs cost tokens, and their output drifts even at temperature 0. So upstream runs once per dataset with
`--record`, and the recordings are committed. On every commit, the .NET port runs the same dataset with a replay LLM
client and a replay embedder that answer from the recording:

- A prompt is looked up by `recording_key`: the SHA-256 of the prompt messages exactly as the prompt library built
  them (`role U+241F content`, joined with `U+241E`), before the LLM client appends its language instruction or
  schema. A prompt the port words differently has no recording, and the run fails, naming the prompt and the
  closest recorded one. That is what keeps the word-for-word prompt port honest.
- An embedding is looked up by its exact input text.
- The port's entities, facts, invalidations and search results are then compared with `<dataset>.upstream.json`.

Python Graphiti stores the graph in Neo4j, the port in PostgreSQL or memory, so keyword ranking can differ slightly;
search results are compared as sets of the top results, not by exact order.

## Results

Replaying upstream's recorded answers (`tests/GraphitiNet.Parity.Tests`), on every commit:

| Dataset | In-memory driver | PostgreSQL driver |
| --- | --- | --- |
| `customer-support` | Same 9 entities and 17 facts (endpoints, text, valid and invalid dates, expiry) | Known gap: one fact-resolution step gets a different candidate set |
| `arabic` | Same 7 entities and 10 facts | Same 7 entities and 10 facts |
| `customer-support`, bulk (`AddEpisodeBulkAsync`) | Same 15 entities and 20 facts | Same 15 entities and 20 facts |

Search results match upstream's up to swaps of adjacent near-ties.

What the parity runs found and fixed so far:

- **Similarity scale.** Neo4j's `vector.similarity.cosine` returns `(1 + cosine) / 2`, so upstream's `min_score` of 0.6
  means a raw cosine above 0.2. Both drivers now score on Neo4j's scale; before, deduplication missed candidates such
  as "Alice" for "Alice Chen".
- **Tokenisation.** Lucene's `StandardTokenizer` keeps `ADDED_TO_WORKSPACE`, `can't` or `1,000` as one term; the
  in-memory driver now follows the same word-boundary rules, and PostgreSQL indexes fact names without underscores.

Candidate lists that come from search can come back in a different order than upstream's, because keyword ranking
differs between Lucene and graphiti-net's drivers (and between upstream runs: Lucene's statistics count deleted
documents until segments merge). The replay accepts a prompt whose only difference is the order of an indexed
candidate list and translates the recorded answer's indexes; a different set of candidates still fails.

## Datasets

| Dataset | What it checks |
| --- | --- |
| `customer-support` | A plan change, a move, a person leaving: extraction, deduplication, invalidation |
| `arabic` | Arabic text, and one person written in both Arabic and Latin script (upstream merges them) |

## Recording again

Only needed when a dataset changes or the upstream baseline moves. It needs Neo4j 5.26, Python with
`graphiti-core==0.31.2`, and an OpenAI key.

```bash
docker run -d --name neo4j -p 7687:7687 -e NEO4J_AUTH=neo4j/<dev password> neo4j:5.26
```

```bash
NEO4J_PASSWORD=<dev password> LLM_MODEL=gpt-5-mini EMBEDDING_MODEL=text-embedding-3-small EMBEDDING_DIM=1024 \
  python parity/python/run_upstream.py parity/datasets/customer-support.json \
  parity/recordings/customer-support.upstream.json --record parity/recordings/customer-support.recording.json
```

For the bulk recording, add `--bulk` and write to `<dataset>-bulk.upstream.json` and
`<dataset>-bulk.recording.json`.

The recordings were made with `gpt-5-mini` for both model sizes and `text-embedding-3-small` at 1,024 dimensions;
each recording file stores its settings.

## Graph-building evaluation (live models)

Graphiti's own evaluation, `tests/evals/eval_e2e_graph_building.py`, compares a candidate's graphs with a baseline's
on LongMemEval conversations, using an LLM judge. `parity/python/lme_eval.py build` makes the baseline with Python
Graphiti, `tools/GraphitiNet.Eval` makes graphiti-net's, and `lme_eval.py judge` scores them. Results are in
[results/](results/); always run a second upstream build as a control, since the judge must pick a side even between
two runs of the same pipeline.

## Question answering on LongMemEval (live models)

`parity/python/lme_qa.py` and the `qa` command of `tools/GraphitiNet.Eval` run the same benchmark on each
implementation: ingest each selected LongMemEval-oracle question's conversations message by message, search the
question's group (`COMBINED_HYBRID_SEARCH_RRF`, facts and entities, 20 each), answer with upstream's
`eval.qa_prompt` from the facts (with their valid and invalid dates) and entity summaries only, and grade every
answer with the same Python script and upstream's `eval.eval_prompt`. The grader also checks that each answering
prompt is the one upstream's `qa_prompt` builds from the stored context, character for character.

```bash
python -I parity/python/lme_qa.py select <longmemeval_oracle.json> parity/results/2026-11-12-lme-qa-questions.json
NEO4J_PASSWORD=<dev password> python -I parity/python/lme_qa.py run <oracle> <questions.json> upstream.json upstream
dotnet run --project tools/GraphitiNet.Eval -c Release -- qa <oracle> <questions.json> net.json net
JUDGE_MODEL=gpt-5.5 python -I parity/python/lme_qa.py grade net.json net.graded.json
python -I parity/python/lme_qa.py report *.graded.json
```

`lme_qa.py answer` and `GraphitiNet.Eval qa-answer` answer again from a run's stored contexts, without ingesting or
searching, as a control for the answering stage alone.

## Differential tests

The dataset runs above check whole pipelines. The differential tests check single functions: they prove that a
ported function returns exactly what upstream's own function returns, on many more inputs than hand-written tests
cover.

`python/generate_differential_fixtures.py` imports the functions from `graphiti-core` 0.31.2, runs them on inputs
generated from fixed seeds, and writes the inputs and upstream's outputs to
`tests/GraphitiNet.Core.Tests/Differential/Fixtures/*.json`. The tests in `tests/GraphitiNet.Core.Tests/Differential/`
load each file, call the C# port with the same inputs and compare. Floats may differ by 1e-9 relative where the
arithmetic runs in a different order (NumPy against plain loops), or 1e-12 for logarithms; everything else must be
identical, strings to the character. A failing test lists every differing case with its input.

| Upstream module | Functions | Cases |
| --- | --- | --- |
| `search/search_utils.py`, `helpers.py` | `calculate_cosine_similarity`, `rrf` (rank constants, `min_score`, ties, division by zero), `maximal_marginal_relevance` (`min_score`, duplicate embeddings), `normalize_l2` | 601 |
| `utils/maintenance/dedup_helpers.py` | `_normalize_string_exact`, `_normalize_name_for_fuzzy`, `_name_entropy`, `_has_high_entropy`, `_shingles`, `_minhash_signature`, `_lsh_bands`, `_jaccard_similarity`, and `_build_candidate_indexes` with `_resolve_with_similarity` end to end | 3,125 |
| `utils/maintenance/edge_operations.py` | `resolve_edge_contradictions` and `_apply_temporal_invalidation` (every combination of start, end and expiry for one candidate, plus random sets), `enforce_edge_type_signatures`, `_edge_types_for_endpoints` | 3,947 |
| `utils/maintenance/temporal_edge_utils.py` | `normalize_future_temporal_bounds`, `apply_extracted_timestamps`, and the `datetime.fromisoformat` parse behind it (hand-picked and fuzzed timestamps) | 2,056 |
| `utils/text_utils.py` | `truncate_at_sentence`, `truncate_utf8_to_bytes`, `concatenate_episodes`, `concatenate_timeline` | 805 |
| `utils/bulk_utils.py` | `compress_uuid_map`, `_build_directed_uuid_map`, `resolve_edge_pointers` | 466 |
| `utils/maintenance/attribute_utils.py` | `cap_string_attributes`, `apply_capped_attributes` (both merge modes, on two Pydantic models the tests mirror in C#) | 600 |
| `prompts/prompt_helpers.py` | `to_prompt_json` on nested values (big ints, float edge cases, non-string keys, tuples, indents, `ensure_ascii`) | 300 |
| `utils/content_chunking.py` | `estimate_tokens`, `should_chunk`, `chunk_json_content`, `chunk_text_content`, `chunk_message_content`, `generate_covering_chunks` | 305 |
| `utils/maintenance/community_operations.py` | `label_propagation` | 120 |

Inputs include empty lists, ties, Arabic and other non-Latin text, emoji, the whitespace Python and .NET disagree on
(U+001C to U+001F), timezone offsets, naive datetimes and `None` values. Upstream's `utc_now()` is frozen while the
fixtures are generated, so an expiry set to "now" is recorded as `{"$now": true}` and the port must set the current
time there.

Some cases carry a marker where the port deliberately differs from upstream (each deviation is listed in
[docs/api-compatibility.md](../docs/api-compatibility.md#behaviour-that-differs-from-upstream)):
`"deviation"` (an out-of-range timestamp, which upstream raises on and the port skips), `"astral"` (content chunking
measures UTF-16 code units, so text with emoji may chunk differently), `"unordered"` and `"order_dependent"` (results
upstream orders by Python set iteration). Graphs on which upstream's `label_propagation` never stops are left out.

To regenerate, with `graphiti-core==0.31.2` installed (no database or API key needed):

```bash
python -I parity/python/generate_differential_fixtures.py
dotnet test tests/GraphitiNet.Core.Tests --filter "FullyQualifiedName~Differential"
```

The script checks the installed version and writes the same files on every run. To cover another function, add a
`gen_*` section to the script and a test that reads its cases with `Fixture.Cases(file, function)`.
