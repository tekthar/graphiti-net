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
