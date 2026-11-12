# Parity with Python Graphiti

graphiti-net is correct when it does what Python Graphiti does on the same input. This folder holds the datasets,
the recordings of upstream's model answers, and the scripts that produce them.

| Path | What it is |
| --- | --- |
| `datasets/*.json` | Episodes (with reference times) and search queries |
| `python/run_upstream.py` | Runs a dataset through Python Graphiti v0.31.2 on Neo4j and writes what it produced |
| `recordings/<dataset>.upstream.json` | Upstream's entities, facts (with validity and expiry) and search results |
| `recordings/<dataset>.recording.json` | Every prompt upstream sent and the answer it got, and every embedding |

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

The recordings were made with `gpt-5-mini` for both model sizes and `text-embedding-3-small` at 1,024 dimensions;
each recording file stores its settings.

## Graph-building evaluation (live models)

Graphiti's own evaluation, `tests/evals/eval_e2e_graph_building.py`, compares a candidate's graphs with a baseline's
on LongMemEval conversations, using an LLM judge. `parity/python/lme_eval.py build` makes the baseline with Python
Graphiti, `tools/GraphitiNet.Eval` makes graphiti-net's, and `lme_eval.py judge` scores them. Results are in
[results/](results/); always run a second upstream build as a control, since the judge must pick a side even between
two runs of the same pipeline.
