# How Graphiti works

This page explains what Graphiti does and how, as implemented in Graphiti v0.31.2, the release graphiti-net ports.
Everything here applies to graphiti-net too, except where it says otherwise.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent rewrite. It is not affiliated with,
> endorsed by, or sponsored by Zep Software, Inc.

## The problem it solves

An AI agent needs memory that keeps up with a changing world. Plain retrieval (chunk the text, embed the chunks,
search them) can find a passage, but it can't tell that "Alice is on the Pro plan" stopped being true when "Alice
upgraded to Enterprise" arrived, or that "she" in one message is the Alice from last week.

Graphiti turns each new piece of input into a **knowledge graph**: entities (people, products, policies) joined by
facts. It merges new input into the graph as it arrives, remembers when each fact was true, and traces every fact back
to the input that stated it. Queries are fast because answering one needs no LLM call: only embeddings, keyword search
and short graph walks.

## The building blocks

| Record | What it is | Example |
| --- | --- | --- |
| **Episode** | One raw input, stored as ingested: a chat message, a text document or a JSON object | `user: I just upgraded to Enterprise` |
| **Entity** | A thing the input talks about, with a name, a summary that grows over time, and optional typed attributes | `Alice` (a Customer, plan = Enterprise) |
| **Fact** (entity edge) | A relationship between two entities, stated in words, with its time window | `Alice` → `SUBSCRIBES_TO` → `Enterprise plan`: "Alice is on the Enterprise plan", valid from 2026-11-12 |
| **Mention** (episodic edge) | Links an episode to each entity it mentions | episode 42 → `Alice` |
| **Community** | A cluster of closely connected entities with a summary | "Alice's account and billing" |
| **Saga** | An ordered chain of episodes, such as one conversation, with a summary | "Support chat with Alice" |

Every record has a **group id**. It partitions the graph, for example one graph per user or tenant, and every read and
write is scoped by it.

Facts and entities carry **embeddings** (vectors from an embedding model) so they can be found by meaning, and they are
indexed for **keyword search**.

## Adding an episode

`add_episode` (`AddEpisodeAsync` in graphiti-net) is the heart of Graphiti. One call runs this pipeline:

```
episode text
   │
   ├─ 1. Save the episode as the raw source of truth
   ├─ 2. Fetch the last few episodes in the same group, as context
   ├─ 3. Extract entities ────────────── LLM
   ├─ 4. Resolve entities against the graph
   │      candidates by name, keyword and embedding search
   │      → exact and fuzzy name matching (MinHash), identity attributes
   │      → LLM decides only the cases still in doubt ── LLM
   ├─ 5. Extract facts between the resolved entities ── LLM
   ├─ 6. Resolve facts: find existing facts that duplicate or contradict each new one ── LLM
   ├─ 7. Date facts and invalidate contradicted ones (they get an end date; nothing is deleted)
   ├─ 8. Update entity summaries and typed attributes ── LLM
   └─ 9. Embed and save everything, linking the episode to each entity it mentions
```

Some details that matter:

- **Context window.** Extraction sees the previous episodes (upstream: the last 10 in the group, by reference time),
  so "she" or "the same plan" resolve correctly.
- **Deduplication is cheap first, LLM last.** Most entities resolve by exact or fuzzy name match against search
  candidates. Only ambiguous ones go to the LLM, which keeps cost and latency down.
- **Concurrency.** LLM calls run in parallel, capped at 20 at a time by default (`max_coroutines` / `SEMAPHORE_LIMIT`)
  to stay inside provider rate limits.
- **Ordering.** Episodes in the same group should be added one after another, each awaited, because each one reads
  the graph the previous one wrote. Run ingestion in a background queue, not in a request handler.
- **Bulk ingest.** `add_episode_bulk` processes many episodes together for backfills: it extracts from them in
  parallel, deduplicates within the batch first, then resolves the batch against the graph in one pass. It is much
  faster than adding the episodes one by one, but episodes in the same batch can't see each other's facts as context,
  so prefer `add_episode` for live data.

## Time: the bi-temporal model

Every fact has four timestamps:

| Field | Meaning |
| --- | --- |
| `valid_at` | When the fact became true in the world |
| `invalid_at` | When it stopped being true (empty while still true) |
| `created_at` | When Graphiti recorded it |
| `expired_at` | When Graphiti marked it superseded |

The first two are **world time**, the last two **system time**. The LLM reads world time from the text, using the
episode's **reference time** to resolve phrases like "last week" or "since March".

When a new fact contradicts an old one, Graphiti closes the old one instead of deleting it:

```
"Alice is on the Pro plan"          valid_at 2026-01-10   invalid_at 2026-11-12   expired_at <now>
"Alice is on the Enterprise plan"   valid_at 2026-11-12   invalid_at —
```

The rules, from `edge_operations.py`:

- An old fact that ended before the new one started, or started after the new one ended, doesn't conflict.
- An old fact that started earlier is closed: its `invalid_at` becomes the new fact's `valid_at`.
- If a contradicting fact starts *later* than the new one, the new one is the outdated one and is closed instead.
  This handles episodes that arrive out of order.

So you can ask what is true now (facts with no `invalid_at`) or what was true on any past date (filter on the
`valid_at` and `invalid_at` window).

## Search

Search never calls an LLM. It combines three ways of finding candidates, then reranks them:

| Method | Finds | How |
| --- | --- | --- |
| Semantic | Facts, entities, communities | Cosine similarity between the query's embedding and stored embeddings |
| Keyword | Facts, entities, episodes, communities | Full-text search (BM25 on Neo4j's Lucene index upstream) |
| Graph | Facts, entities | Breadth-first walk, up to 3 hops, from given nodes |

| Reranker | What it does |
| --- | --- |
| Reciprocal rank fusion (RRF) | Merges the result lists: an item ranked high by several methods wins. The default. |
| Maximal marginal relevance (MMR) | Balances relevance against variety, so results aren't near-duplicates |
| Node distance | Ranks by closeness to a centre entity, such as the current user |
| Episode mentions | Ranks entities by how often episodes mention them |
| Cross-encoder | A model scores each (query, result) pair. The most accurate and the slowest. |

There are two levels of API:

- **Simple:** `search(query, center_node_uuid=None, group_ids=None, num_results=10, search_filter=None)` returns facts,
  using hybrid search with RRF, or node-distance reranking when a centre node is given.
- **Advanced:** `search_(query, config, group_ids, center_node_uuid, bfs_origin_node_uuids, search_filter)` takes a
  full `SearchConfig` and returns facts, entities, episodes and communities with their scores. Predefined configs
  ("recipes") cover common cases, such as `COMBINED_HYBRID_SEARCH_RRF` or `NODE_HYBRID_SEARCH_NODE_DISTANCE`.

`SearchFilters` narrows results by entity labels, fact types, any of the four timestamps (with AND/OR conditions),
specific ids, and custom attributes.

## Custom types

You can describe the entities and relationships you care about. Upstream uses Pydantic models; graphiti-net uses C#
classes or records:

```python
class Customer(BaseModel):
    """A paying customer."""
    plan: str | None = Field(None, description="Subscription plan")
```

The type's name and description guide extraction, and its fields become attributes the LLM fills in. An **edge type
map** says which fact types may join which entity types, and **excluded entity types** are skipped. Without custom
types, entities are labelled `Entity` and structure emerges from the data.

## Communities and sagas

- **Communities** (`build_communities`) cluster entities with label propagation over the fact graph, then summarise
  each cluster with the LLM, merging summaries pairwise. With `update_communities=True`, each new episode also places
  its entities into existing communities.
- **Sagas** chain episodes in order (`saga=` on `add_episode`). `summarize_saga` keeps an incremental summary of the
  whole chain, such as a running summary of a conversation.

## Pluggable parts

| Part | Upstream default | graphiti-net |
| --- | --- | --- |
| Storage (graph driver) | Neo4j; also FalkorDB, Amazon Neptune | PostgreSQL with pgvector by default; in-memory with nothing to install |
| LLM | OpenAI | OpenAI, or any `IChatClient` from Microsoft.Extensions.AI (Azure OpenAI, Anthropic, Gemini, Ollama...) |
| Embedder | OpenAI `text-embedding-3-small` | Same, or any `IEmbeddingGenerator` |
| Cross-encoder | OpenAI (yes/no relevance with log probabilities) | Same, or any `IChatClient` |

Prompts can be overridden per instance, with fixed response schemas, so the pipeline keeps working.

## Where the cost goes

Adding an episode costs a handful of LLM calls (extraction, resolution of uncertain cases, summaries) plus embeddings.
Searching costs one embedding call (for the query) and a few database queries. That split is why Graphiti suits agents:
writes can happen in the background, and reads are fast enough for every turn of a conversation.

## How graphiti-net differs

- **No graph database.** Upstream needs Neo4j, FalkorDB or Neptune. graphiti-net stores the graph in PostgreSQL
  through EF Core, or in memory with nothing to install. Graphiti's queries are shallow (lookups, vector and keyword
  search, walks of at most 3 hops), so relational storage handles them well.
- **Keyword ranking.** PostgreSQL's built-in ranking isn't BM25, so keyword results can order slightly differently
  from upstream. The in-memory driver ranks with BM25, like upstream.
- **.NET idioms.** Methods are `PascalCase` and end in `Async`, parameters are `camelCase`, and every call takes a
  `CancellationToken`. See [api-compatibility.md](api-compatibility.md) for the full mapping.
