# Graph drivers

A graph driver stores the graph and runs the search primitives. Core code talks only to `IGraphDriver`
(`src/GraphitiNet.Core/Driver/GraphDriver.cs`); it never sends raw queries. This page sums up the contract, how
graphiti-net differs from upstream's Neo4j driver, and how to add and test a driver.

## Drivers

| Driver | Package | Notes |
| --- | --- | --- |
| In-memory (`InMemoryGraphDriver`) | `GraphitiNet.Core` | Zero-install: tests, demos, small apps. Thread-safe, one lock. BM25 full-text, exact cosine similarity. Can save to and load from a JSON file. |
| PostgreSQL | planned | The default for production. |
| SQLite | planned | |

```csharp
await using var driver = new InMemoryGraphDriver();
// ... use it, then optionally keep it:
await driver.SaveToFileAsync("graph.json");
await using var again = await InMemoryGraphDriver.LoadFromFileAsync("graph.json");
```

## The contract

The XML docs on `IGraphDriver` and its operations interfaces are the reference. The rules every driver follows, and the
conformance suite checks:

### Records

- **Saves are upserts** keyed by `Uuid`: saving an existing id replaces all its stored fields, embeddings included.
- **Gets do not load embeddings.** `NameEmbedding` and `FactEmbedding` stay `null` on everything a get or search
  returns. `LoadEmbeddingsAsync` / `LoadNameEmbeddingAsync` fill them in, and throw `NodeNotFoundException` /
  `EdgeNotFoundException` for an unknown id; the bulk versions skip unknown ids.
- **Not found:** `GetByUuidAsync` throws `NodeNotFoundException` or `EdgeNotFoundException`, also when the id belongs
  to another record type. `GetByUuidsAsync` skips missing ids.
- **Paging:** `GetByGroupIdsAsync` returns records in descending `Uuid` order (ordinal). `uuidCursor` returns only ids
  that sort strictly before it; `limit` caps the page. An empty group list returns nothing.
- **Deletes cascade:** deleting a node, by object, by ids or by group, deletes every edge of any type that starts or
  ends at it. Deleting a community or saga leaves its members and episodes. Unknown ids are ignored.
- **UTC:** every timestamp comes back in UTC (`Offset == TimeSpan.Zero`) and is compared as an instant. The suite uses
  millisecond precision; backends may drop anything finer than a microsecond.
- **Labels** round-trip as a set (order and duplicates are not kept). Drivers neither add nor remove labels.
- **Attributes and episode metadata** round-trip as JSON: what comes back serialises to the same JSON. The in-memory
  driver returns `string`, `long`, `double`, `bool`, `null`, `Dictionary<string, object?>` and `List<object?>`.
- **Endpoints:** core saves an edge's endpoints first. What a backend does with an edge whose endpoint is missing is
  not specified (Neo4j silently skips it, a SQL backend with foreign keys throws, the in-memory driver stores it).

### Episodes and sagas

- `RetrieveEpisodesAsync` takes the `lastN` latest episodes with `ValidAt <= referenceTime` (by `ValidAt`
  descending), then returns them oldest first. `null` or empty group ids mean every group. `source` and `saga` (by
  name) filter further.
- `GetBySagaAsync`: the saga's episodes by `CreatedAt`, then `ValidAt`, ascending; `createdAfter` is exclusive.
- `SagaNodeOps.GetByNameAsync`: exact, case-sensitive name within one group.
- `GetPreviousEpisodeUuidAsync`: the saga's latest episode other than the current one, by `ValidAt` then `CreatedAt`,
  both descending.

### Search

- **Group ids:** `null` means every group; an empty list matches nothing.
- **Full-text** fields: entities on name and summary, facts on name and fact, episodes on content and source
  description, communities on name. A record matches if it contains **any** query term (Lucene's default `OR`; note
  that PostgreSQL's `plainto_tsquery` and SQLite FTS5 default to `AND`), ignoring case and punctuation. Best first by
  the backend's relevance score. An empty or whitespace query returns nothing.
- **Similarity:** cosine similarity, results scoring **at least** `minScore`, best first. Records without an embedding
  never match. Fact search's source and target restrictions apply on their own or together, with or without group
  ids.
- **Breadth-first:** from entity or episode origins, following facts in both directions and episode-to-entity links
  outward only, up to `maxDepth` hops (`< 1` returns nothing; callers cap it at 3). Entity BFS returns entities 1 to
  `maxDepth` hops away, in the origin's group, not counting the origin itself. Fact BFS returns facts on those paths;
  from an episode, the mention is the first hop. Order is not specified.
- **Filters:**
  - entity searches (full-text, similarity, BFS) apply `NodeLabels` and `PropertyFilters` (on `Attributes`) and
    ignore the fact-only fields;
  - fact searches apply every field: `NodeLabels` to both endpoints, `PropertyFilters` to the fact's `Attributes`;
  - episode full-text takes a filter but ignores it, as upstream;
  - every entity has the implicit label `Entity`;
  - a date field holds OR groups of AND lists: `[[a, b], [c]]` is `(a AND b) OR c`;
  - an empty list in any filter matches nothing;
  - a comparison with a missing value is false; only `IsNull` matches it;
  - property values compare as numbers when both are numbers, as ordinal strings when both are strings, never across
    types.
- `GetConnectedNodeUuidsAsync`: the candidates joined to the centre by a fact in either direction.

### Maintenance

- `ClearDataAsync(groups)` deletes every node and edge in those groups (and edges touching those nodes); `null`
  deletes everything, an empty list nothing.
- `BuildIndicesAndConstraintsAsync` is idempotent and keeps data.
- `GetEntityProjectionAsync`: every entity in the group is a key; neighbours are entities in the same group joined by
  facts in either direction, with the number of such facts.
- `GetEntityGroupIdsAsync`: distinct groups that have entities.
- `RemoveCommunitiesAsync`: communities in the groups (all if `null` or empty) and their membership edges.
- `GetMentionedNodesAsync` / `GetCommunitiesByNodesAsync`: distinct results.
- `GetNeighborCommunitiesAsync`: one entry per neighbouring member, so a community appears once for each of its
  members that shares a fact with the entity.
- `CountEpisodeMentionsAsync`: distinct mentioning episodes per entity; every requested id is a key, 0 if none.

## Differences from upstream's Neo4j driver

Where the contract and upstream differ, the contract wins:

| Topic | Upstream (Neo4j, v0.31.2) | graphiti-net |
| --- | --- | --- |
| `retrieve_episodes` order | the Neo4j operation returns newest first; the caller reverses | the driver returns oldest first |
| `retrieve_episodes` saga | looked up in the first group id only; with no group ids it is ignored or matches nothing, depending on the code path | looked up in every given group, or every group |
| Similarity threshold | `score > min_score` | `score >= minScore` |
| Fact similarity endpoints | applied only when group ids are given | always applied |
| Breadth-first direction | facts followed source to target only | facts followed both ways |
| Date filter OR groups | parameters are named by position within a group, so later groups overwrite earlier groups' values | each group keeps its own values |
| Property filters | defined but never applied | applied to entity and fact attributes |
| Neighbour communities | one row per (member, fact) pair | one entry per member |
| `clear_data(groups)` | nodes in the groups and their edges | also edges whose own group is listed |
| Full-text fields | episodes also index `source` | content and source description only |
| Full-text query length | queries of 128+ words return nothing | no limit |
| Labels | `Entity` is added on save | stored as given; `Entity` is implicit in filters |

The in-memory driver ranks full-text with BM25 like Lucene (k1 = 1.2, b = 0.75, per-field statistics summed over
fields). It splits on every character that is not a letter or digit, so `WORKS_AT` is two terms, where Lucene's
standard tokenizer keeps it as one, and it has no stop words or stemming. Lucene also stores field lengths lossily, so
scores can differ slightly; rankings rarely do.

## Adding a driver

1. Implement `IGraphDriver` and the operations interfaces in your own project (`src/GraphitiNet.<Backend>`).
2. Add a test project that references `tests/GraphitiNet.Driver.Tests` and subclass the conformance suite:

   ```csharp
   public sealed class PostgreSqlDriverConformanceTests : GraphDriverConformanceTests
   {
       protected override async Task<IGraphDriver> CreateDriverAsync()
       {
           var driver = new PostgreSqlGraphDriver(TestDatabase.ConnectionString);
           await driver.GraphOps.BuildIndicesAndConstraintsAsync();
           return driver;
       }

       // One database shared by every test: skip the whole-graph tests.
       protected override bool IsIsolated => false;

       // The vector column's size; test vectors are padded with zeros.
       protected override int EmbeddingDimension => 1024;
   }
   ```

3. Run it: `dotnet test --filter "FullyQualifiedName~PostgreSqlDriverConformanceTests"`.

What a subclass provides:

- `CreateDriverAsync()` (required): a ready driver, schema in place. It is called once per test, and the driver is
  disposed after the test, so a shared driver must tolerate `DisposeAsync` (or the subclass can override
  `DisposeAsync`).
- `IsIsolated` (default `true`): `true` only if each driver sees a store of its own, empty at the start. With `false`,
  the few whole-graph tests (clear everything, remove every community, search every group by vector, retrieve
  episodes from every group) pass without checking anything.
- `EmbeddingDimension` (default 3): the stored vector size.

Every test writes to fresh group ids (`conformance-<guid>`), searches only those groups (or uses words no other test
uses), and clears its groups afterwards with `ClearDataAsync`, so tests can run in parallel against one database.
`InMemorySharedDriverConformanceTests` runs the suite against one pre-filled shared store to keep it that way.
