# Quickstart

The graphiti-net version of Python Graphiti's
[`examples/quickstart/quickstart_neo4j.py`](https://github.com/getzep/graphiti/blob/v0.31.2/examples/quickstart/quickstart_neo4j.py),
step for step:

1. Add four episodes: two text, two JSON.
2. Search for facts with hybrid search.
3. Rerank the same search by graph distance to a center node.
4. Search for entities with the `NodeHybridSearchRrf` recipe.

The graph is kept in memory, or in PostgreSQL when a connection string is set. No graph database is needed.

## Run it

```bash
export OPENAI_API_KEY=sk-...
dotnet run --project samples/Quickstart
```

With PostgreSQL and pgvector (the database must exist; the sample creates the extension, a `graphiti_quickstart`
schema and its tables):

```bash
export ConnectionStrings__Graph="Host=localhost;Database=graphiti;Username=postgres;Password=postgres"
dotnet run --project samples/Quickstart
```

The sample clears its graph at the start of each run, so the output stays comparable. In PostgreSQL it only touches
the `graphiti_quickstart` schema.

## Settings

| Variable | Default | Purpose |
| --- | --- | --- |
| `OPENAI_API_KEY` | (required) | The OpenAI key, read by the OpenAI clients |
| `OPENAI_BASE_URL` | OpenAI | An OpenAI-compatible endpoint |
| `ConnectionStrings__Graph` | none: in memory | PostgreSQL connection string |
| `LLM_MODEL` | `gpt-5-mini` | The model for extraction |
| `SMALL_LLM_MODEL` | `LLM_MODEL` | The model for simpler prompts |
| `EMBEDDING_MODEL` | `text-embedding-3-small` | The embedding model |
| `EMBEDDING_DIM` | `1024` | Embedding size, as upstream's `EMBEDDING_DIM` |
| `RERANKER_MODEL` | `gpt-4.1-mini` | The cross-encoder's model (a non-reasoning model; this sample's searches don't call it) |

The sample sets `gpt-5-mini` because upstream's default model, which the library also uses when no model is given,
may not be available on every account.

## Output

A real run against OpenAI with `gpt-5-mini` and the in-memory driver, shortened. Extraction is done by an LLM, so the
wording of facts and summaries changes from run to run.

```text
Storage: in memory. Models: gpt-5-mini, text-embedding-3-small.
Added episode: Freakonomics Radio 0 (text)
Added episode: Freakonomics Radio 1 (text)
Added episode: Freakonomics Radio 2 (json)
Added episode: Freakonomics Radio 3 (json)

Searching for: 'Who was the California Attorney General?'

Search Results:
UUID: 73b8d64b-88d2-48ce-a7af-210ecdec6374
Fact: Kamala Harris is the Attorney General of California.
Valid from: 2026-11-12 19:17:06Z
---
UUID: de59eec0-6a93-43a0-baf0-0800e0cef6b7
Fact: Kamala Harris was previously the district attorney for San Francisco.
Valid from: 2026-11-12 19:17:06Z
---
UUID: b23ead98-32a0-4de4-b048-ee2f0e8aafe0
Fact: Gavin Newsom is Governor of California
Valid from: 2026-11-12 19:17:22Z
---
...
UUID: 20955bcf-9ce0-4ef5-8042-28c4621ccfce
Fact: Harris held the office of AG from January 3, 2011 to January 3, 2017.
Valid from: 2011-01-03 00:00:00Z
Valid until: 2017-01-03 00:00:00Z
---

Reranking search results based on graph distance:
Using center node UUID: 8a6dbf99-3a44-42d0-b743-7ab345427c21

Reranked Search Results:
UUID: 73b8d64b-88d2-48ce-a7af-210ecdec6374
Fact: Kamala Harris is the Attorney General of California.
Valid from: 2026-11-12 19:17:06Z
---
UUID: de59eec0-6a93-43a0-baf0-0800e0cef6b7
Fact: Kamala Harris was previously the district attorney for San Francisco.
Valid from: 2026-11-12 19:17:06Z
---
UUID: 20955bcf-9ce0-4ef5-8042-28c4621ccfce
Fact: Harris held the office of AG from January 3, 2011 to January 3, 2017.
Valid from: 2011-01-03 00:00:00Z
Valid until: 2017-01-03 00:00:00Z
---
...

Performing node search with the standard recipe NodeHybridSearchRrf:

Node Search Results:
Node UUID: e2d0063f-b6c5-4da1-b142-03b87a15282c
Node Name: Attorney General of California
Content Summary: Kamala Harris is the Attorney General of California. Harris held the office of AG from January 3, 20...
Node Labels: Entity
Created At: 2026-11-12 19:17:08Z
---
Node UUID: f51fb6e8-e0e8-4fbc-a6a1-7f30afa988f5
Node Name: Gavin Newsom
Content Summary: Gavin Newsom is Governor of California Gavin Newsom previously served as Lieutenant Governor Gavin N...
Node Labels: Entity
Created At: 2026-11-12 19:17:24Z
---
...
Node UUID: 8a6dbf99-3a44-42d0-b743-7ab345427c21
Node Name: Kamala Harris
Content Summary: Kamala Harris is the Attorney General of California. Kamala Harris was previously the district attor...
Node Labels: Entity
Created At: 2026-11-12 19:17:08Z
---

Tokens used: 17834 input, 1124 output.

Connection closed
```

On PostgreSQL the same run gave the same six facts and five entities, with slightly different wording (for example
"Harris held the office of Attorney General of California from January 3, 2011 to January 3, 2017.").

Reranking by graph distance moves the facts about Kamala Harris, the center node, ahead of the facts about Gavin
Newsom. The term fact has both a start and an end date: the LLM read them from the episode text, not from the
episode's reference time.
