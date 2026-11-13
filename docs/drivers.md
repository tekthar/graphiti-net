# Graph drivers

A graph driver stores the graph and runs the search primitives. Core code talks only to `IGraphDriver`
(`src/GraphitiNet.Core/Driver/GraphDriver.cs`); it never sends raw queries. This page sums up the contract, how
graphiti-net differs from upstream's Neo4j driver, and how to add and test a driver.

## Drivers

| Driver | Package | Notes |
| --- | --- | --- |
| In-memory (`InMemoryGraphDriver`) | `GraphitiNet` | Zero-install: tests, demos, small apps. Thread-safe, one lock. BM25 full-text, exact cosine similarity. Can save to and load from a JSON file. |
| PostgreSQL (`PostgresDriver`) | `GraphitiNet.PostgreSql` | The default for production: EF Core, pgvector, BM25 keyword search in SQL. See [postgresql.md](postgresql.md). |
| SQL Server (`SqlServerDriver`) | `GraphitiNet.SqlServer` | SQL Server 2025 and Azure SQL: EF Core, the native `vector` type, BM25 keyword search over a driver-maintained keyword index. See [sqlserver.md](sqlserver.md). |
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
- **Labels** round-trip as a set (order and duplicates are not kept). Entity nodes always come back with the `Entity`
  label, added if it was not saved, as upstream; drivers add or remove no other label.
- **Attributes and episode metadata** round-trip as JSON: what comes back serialises to the same JSON. The in-memory
  driver returns `string`, `long`, `double`, `bool`, `null`, `Dictionary<string, object?>` and `List<object?>`.
- **Endpoints:** core saves an edge's endpoints first. What a backend does with an edge whose endpoint is missing is
  not specified (Neo4j silently skips it, a SQL backend with foreign keys throws, the in-memory driver stores it).

### Episodes and sagas

- `RetrieveEpisodesAsync` takes the `lastN` latest episodes with `ValidAt <= referenceTime` (by `ValidAt`, then
  `CreatedAt`, descending), then returns them oldest first. `null` or empty group ids mean every group. `source`
  filters further. `saga` restricts to the episodes of the saga with that name **in the first group id**, as upstream;
  with no group ids it matches nothing, and the group ids do not further restrict the saga's episodes.
- `GetBySagaAsync`: the saga's episodes by `CreatedAt`, then `ValidAt`, ascending; `createdAfter` is exclusive.
- `SagaNodeOps.GetByNameAsync`: exact, case-sensitive name within one group.
- `EntityNodeOps.GetByNamesAsync`: entities whose name equals one of the names exactly (case-sensitive) in any of the
  groups; empty names or groups return nothing. The default implementation scans `GetByGroupIdsAsync`; override it
  with an indexed lookup.
- `EntityNodeOps.GetByAttributeValuesAsync` (optional): up to `limit` entities in one group whose attributes equal all
  the given values (as JSON: strings exactly, numbers by value; a missing attribute never matches; no values match
  nothing), optionally carrying all the given labels, by `CreatedAt` then `Uuid`. The default throws
  `NotSupportedException`, as upstream's drivers do not implement it either; entity deduplication then skips the
  identity-property lookup. The in-memory and PostgreSQL drivers implement it.
- `EpisodeNodeOps.GetEpisodeUuidsByEntityNodeUuidsAsync`: for many entities at once, the ids of the episodes in each
  entity's own group that mention it; every requested id is a key (empty for unknown entities). The default calls
  `GetByEntityNodeUuidAsync` per entity and cannot apply the group rule; override it with one query. The search
  episode-access filter uses it.
- `GetPreviousEpisodeUuidAsync`: the saga's latest episode other than the current one, by `ValidAt` then `CreatedAt`,
  both descending.
- **Idempotency keys** (`EpisodicNode.IdempotencyKey`): stored with the episode and unique per group; saving an episode
  whose key another episode of its group has fails (with the backend's exception); episodes without a key never
  collide. `EpisodeNodeOps.GetByIdempotencyKeyAsync` finds the episode with a key in a group (exact, case-sensitive)
  or returns `null`. `EpisodeNodeOps.AddOrGetByIdempotencyKeyAsync` saves the episode unless its key is taken, and
  returns the episode that holds the key; when several callers race with one key, one episode is saved and all get
  it. The defaults scan `GetByGroupIdsAsync` and catch the save failure of the losing writer; override both with an
  indexed lookup and an insert that the unique index arbitrates. Core uses them for `AddEpisodeAsync`'s
  `idempotencyKey`.

### Search

- **Group ids:** `null` means every group; an empty list matches nothing.
- **Full-text** fields: entities on name and summary, facts on name and fact, episodes on content, source (its stored
  name: `message`, `json`, `text`, `fact_triple`) and source description, communities on name. A record matches if it
  contains **any** query term (Lucene's default `OR`; note that PostgreSQL's `plainto_tsquery` and SQLite FTS5 default
  to `AND`), ignoring case and punctuation. Best first by the backend's relevance score. An empty or whitespace query
  returns nothing. (Core applies upstream's 128-word query guard.)
- **Similarity:** cosine similarity, results scoring **strictly more than** `minScore` (`score > min_score`, as
  upstream), best first. Records without an embedding never match. Fact search's source and target restrictions apply
  on their own or together, with or without group ids.
- **Breadth-first** follows upstream's `(origin)-[:RELATES_TO|MENTIONS*1..maxDepth]->(n:Entity)`:
  - paths follow facts **from source to target only**, and episode-to-entity links from the episode; every link is one
    hop, so from an episode the mention is the first hop;
  - `maxDepth < 1` returns nothing; drivers do not cap it (core caps it at 3);
  - entity BFS returns, without duplicates, every entity at the end of a path of 1 to `maxDepth` hops that is in the
    same group as the path's origin. The origin itself is returned only if a cycle leads back to it within `maxDepth`
    hops. Group ids restrict both the origin and the result; filters apply to the result;
  - fact BFS returns, without duplicates, every fact on such a path: every fact whose source is at most
    `maxDepth - 1` hops from an origin (the origin itself is 0 hops). Group ids and filters restrict the facts only;
    the origin's group does not matter;
  - order is not specified.
- **Filters:**
  - entity searches (full-text, similarity, BFS) apply `NodeLabels` and `PropertyFilters` (on `Attributes`) and
    ignore the fact-only fields;
  - fact searches apply every field: `NodeLabels` to both endpoints, `PropertyFilters` to the fact's `Attributes`;
  - episode full-text takes a filter but ignores it, as upstream;
  - every entity has the label `Entity`;
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
- `GetNeighborCommunitiesAsync`: one entry per (fact, membership edge) pair, as upstream's
  `(c:Community)-[:HAS_MEMBER]->(m:Entity)-[:RELATES_TO]-(n:Entity {uuid: $entity_uuid})`: a community appears once for
  every fact (either direction) between the entity and one of its members.
- `CountEpisodeMentionsAsync`: the number of episode-to-entity links per entity (upstream's `count(*)`, so two links
  from one episode count twice); every requested id is a key, 0 if none.

## Differences from upstream

Upstream's Neo4j search runs the generic queries in `search/search_utils.py` (the `*2..depth` BFS variants there are
for Kuzu, which stores each fact as two edges around a node). graphiti-net matches those results except here:

| Topic | Upstream (v0.31.2) | graphiti-net | Why |
| --- | --- | --- | --- |
| `retrieve_episodes` order | the query returns newest first; the function reverses | the driver returns oldest first | same result to the caller |
| Fact similarity endpoints | applied only when group ids are given | always applied | core never passes endpoints, so no difference in use; the upstream form is a bug |
| Date filter OR groups | parameters are named by position within a group, so later groups overwrite earlier groups' values | each group keeps its own values | upstream bug |
| Property filters | defined but never applied | applied to entity and fact attributes | the field exists to be used |
| Entity BFS duplicates | one row per path (no `DISTINCT`), so `limit` counts paths | distinct entities | core deduplicates anyway |
| `clear_data(groups)` | nodes in the groups and their edges | also edges whose own group is listed | same in normal use, where edges share their endpoints' group |
| Full-text query length | queries of 128+ words return nothing | the driver has no limit | core applies the guard |

Both built-in drivers rank full-text with BM25 like Lucene (k1 = 1.2, b = 0.75, per-field statistics over the whole
index, every group, summed over fields) and split text like Lucene's standard tokenizer (`FullText.Tokenize`:
`WORKS_AT`, `can't` and `1,000` are one term each), with no stop words or stemming; they return the same records in
the same order for the same data. Lucene stores field lengths lossily and scores in single precision, so upstream's
scores can differ slightly; rankings rarely do. A new driver should use `FullText` too, and the conformance suite
checks the ranking rules that hold whatever else is in the database.

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
