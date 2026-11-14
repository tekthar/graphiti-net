# PostgreSQL storage

`GraphitiNet.PostgreSql` is graphiti-net's default production storage: the graph lives in nine ordinary tables in a
PostgreSQL database you already run, through EF Core, with [pgvector](https://github.com/pgvector/pgvector) for
vector search, and keyword search ranked with Lucene's BM25 as upstream's Neo4j indexes rank it, in plain SQL. No
graph database and no search extension is needed.

## Requirements

- PostgreSQL 13 or later (tested on 16) with the pgvector extension (0.5 or later, for HNSW indexes). Most managed
  services offer it: Azure Database for PostgreSQL, Amazon RDS and Aurora, Google Cloud SQL, Supabase, Neon.
- A role that may create the extension once (`CREATE EXTENSION vector`), or an administrator who has created it.

## Quick start

```csharp
using GraphitiNet.Driver.PostgreSql;

await using var driver = new PostgresDriver("Host=localhost;Database=graphiti;Username=app;Password=...");
await driver.GraphOps.BuildIndicesAndConstraintsAsync(); // once: creates the extension, schema, tables and indexes
```

`BuildIndicesAndConstraintsAsync` is idempotent: it creates the `vector` extension, the schema, any missing tables
with their indexes, and any index or column a newer version added to an existing table, and leaves existing data
alone (rows missing their full-text columns, from an earlier version or written outside the driver, are indexed, and
rows from 0.1 get their keyword index converted once: see [Upgrading from 0.1](#upgrading-from-01)). It builds the DDL
from the same EF Core model a migration would use, so the two never disagree, then installs the full-text statistics
triggers and recounts the per-field statistics (see [Keyword search](#keyword-search-and-ranking)), which reads each
searchable table once (about 2 seconds for a million rows). It runs in one transaction under an advisory lock, so several app instances may call
it at start-up. `BuildIndicesAndConstraintsAsync(deleteExisting: true)` drops the tables first: **all graph data is
lost**.

When the database cannot hold vectors, `BuildIndicesAndConstraintsAsync` throws `VectorStoreUnavailableException`
(namespace `GraphitiNet.Driver`), with the `PostgresException` as its inner exception. Its message names the database,
what is missing and the fix, so a host can show it to its administrator as it is; `Reason` says which case it is:

- `NotInstalled`: pgvector is not installed on the PostgreSQL server. Install it (or, on a managed service, allow the
  `vector` extension in its settings).
- `NotPermitted`: pgvector is installed, but the extension does not exist in the database and the connecting role may
  not create it. Have a superuser run `CREATE EXTENSION vector;` in that database once; the build then leaves it alone.

`await driver.CanHoldVectorsAsync()` answers the same question before setup without creating anything: it is `true`
when the extension exists in the database, or is installed and the role may create it.

Options:

```csharp
var driver = new PostgresDriver(connectionString, new PostgresGraphOptions
{
    Schema = "graphiti",               // default "graphiti"
    TablePrefix = "",                  // default none, e.g. "graph_"
    EmbeddingDimension = 1536,         // default 1024; must match your embedder
    DefaultTextSearchConfig = "simple" // default "simple"; see "Languages" below
});
```

The driver creates a short-lived `DbContext` per operation and borrows its pooled Npgsql connection, so one driver
instance can be shared by the whole application (register it as a singleton).

## Two ways to host the tables

### Standalone: `GraphitiDbContext`

The driver above uses `GraphitiDbContext`, a context holding only the graph tables. You can also use it directly to
read the tables with LINQ:

```csharp
await using var db = new GraphitiDbContext(connectionString, options);
var count = await db.Entities.CountAsync(e => e.GroupId == "tenant-1");
```

For dependency injection, configure it like any context and pass the graph options with `UseGraphitiOptions`:

```csharp
services.AddDbContextFactory<GraphitiDbContext>(o => o
    .UseNpgsql(connectionString, n => n.UseVector())
    .UseGraphitiOptions(graphOptions));
services.AddSingleton<IGraphDriver>(sp =>
{
    var factory = sp.GetRequiredService<IDbContextFactory<GraphitiDbContext>>();
    return new PostgresDriver(factory.CreateDbContext, graphOptions);
});
```

### Inside your own `DbContext`

Add the tables to your application's context, so they live in your schema and your migrations (ABP Framework and
similar hosts work this way):

```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public static readonly PostgresGraphOptions Graph = new() { Schema = "app", TablePrefix = "graph_" };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddGraphiti(Graph);
    }
}

// Npgsql must know the vector type:
services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString, n => n.UseVector()));
services.AddSingleton<IGraphDriver>(sp => new PostgresDriver(
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext, AppDbContext.Graph));
```

Pass the **same** `PostgresGraphOptions` to `AddGraphiti` and `PostgresDriver`; the driver checks the schema and
table names on first use and throws if they differ. `AddGraphiti` adds no global query filter: every driver call is
scoped by the group ids it is given, as the driver contract requires, so tenant isolation does not depend on the
context's filters. (A host may still add its own filters or row-level security for its own queries.)

The entity classes (`EntityNodeRecord`, `EntityEdgeRecord` and so on, in `GraphitiNet.Driver.PostgreSql.Entities`)
are public so your context can query them, but write through the driver: it keeps the search columns, labels and
cascades right. (A row written another way has no full-text columns and is not found by keyword search until
`BuildIndicesAndConstraintsAsync` indexes it.)

## Migrations

Teams that manage schemas with EF Core migrations don't need `BuildIndicesAndConstraintsAsync`.

- **Your own context:** run `dotnet ef migrations add AddGraphiti` as usual. The migration contains the nine tables,
  the `vector` extension, the HNSW and GIN indexes and the search columns, for your schema, prefix and embedding
  size. Apply it with `dotnet ef database update` or `context.Database.MigrateAsync()`.
- **`GraphitiDbContext`:** the package ships no migration, because the schema name, table prefix and vector size are
  your choice and are baked into a migration. Generate one in your application project with a design-time factory:

  ```csharp
  public sealed class GraphitiDesignFactory : IDesignTimeDbContextFactory<GraphitiDbContext>
  {
      public GraphitiDbContext CreateDbContext(string[] args) => new(
          new DbContextOptionsBuilder<GraphitiDbContext>()
              .UseNpgsql("Host=localhost;Database=graphiti;Username=postgres",
                  n => n.UseVector().MigrationsAssembly(typeof(GraphitiDesignFactory).Assembly.GetName().Name)
                      .MigrationsHistoryTable("__EFMigrationsHistory", "graphiti"))
              .UseGraphitiOptions(new PostgresGraphOptions { EmbeddingDimension = 1536 })
              .Options);
  }
  ```

  then `dotnet ef migrations add InitialGraphiti --context GraphitiDbContext` (with
  `Microsoft.EntityFrameworkCore.Design` referenced by the application project).

EF Core models tables, not triggers, so keyword ranking's statistics need one more step in a migration. Add, after
the tables are created:

```csharp
migrationBuilder.Sql(GraphitiSchemaSql.SearchStatistics(graphOptions)); // same options as the driver
```

It creates the `search_statistics` and `search_term_counts` tables and their triggers and counts the rows already
there (and, once, converts the keyword index of rows written by 0.1: see [Upgrading from 0.1](#upgrading-from-01)); it
is idempotent, so it can also run again later (after a migration that recreates the graph tables, say). Without it,
keyword search throws an `InvalidOperationException` saying so, rather than ranking on wrong statistics. Calling
`BuildIndicesAndConstraintsAsync` instead also works.

`BuildIndicesAndConstraintsAsync` and migrations produce the same tables. If you start with the former and move to
migrations later, mark the initial migration as applied (insert its id into `__EFMigrationsHistory`) instead of
running it.

## Upgrading from 0.1

### Idempotency keys

0.2 adds idempotency keys on episodes
([#9](https://github.com/tekthar/graphiti-net/issues/9)): a nullable `idempotency_key text COLLATE "C"` column on
`episodes` and a unique index `ux_episodes_group_id_idempotency_key` on (`group_id`, `idempotency_key`). Rows without
a key never collide (PostgreSQL treats NULLs as distinct), so existing rows need no change.

- **`BuildIndicesAndConstraintsAsync`:** call it once after upgrading the package, as at every start; it adds the
  column and the index to an existing database. It is idempotent.
- **EF Core migrations:** add a migration after upgrading (`dotnet ef migrations add GraphitiIdempotencyKeys`, with
  `--context GraphitiDbContext` for the standalone context). It contains, for your schema and prefix:

  ```csharp
  migrationBuilder.AddColumn<string>(
      name: "idempotency_key", schema: "graphiti", table: "episodes", type: "text", collation: "C", nullable: true);
  migrationBuilder.CreateIndex(
      name: "ux_episodes_group_id_idempotency_key", schema: "graphiti", table: "episodes",
      columns: new[] { "group_id", "idempotency_key" }, unique: true);
  ```

  Until the migration is applied, every episode write fails with "column idempotency_key does not exist".

### Keyword index

0.2 changes the keyword index (`search_keys` holds `field:term|class` instead of `field:term`) and adds the
`search_term_counts` table. `BuildIndicesAndConstraintsAsync`, or `GraphitiSchemaSql.SearchStatistics` in a migration,
does the upgrade once, in its transaction:

- it rewrites `search_keys` for every row written by 0.1, computing the impact classes in SQL from the stored
  frequencies and lengths (no text is analysed again), with the GIN index dropped first and rebuilt after;
- it counts every table's keys into `search_term_counts` (and marks the table counted, so later calls skip this).

On the benchmark's million records (no embeddings; 4 vCPU, default PostgreSQL settings) the upgrade took **12
minutes**; counting alone, on already converted rows, takes about 40 seconds. Every searchable row gets a new version,
so the tables take about twice their space until `VACUUM` lets new rows reuse it (or `VACUUM FULL` returns it), and
**HNSW indexes receive one insertion per row**: with embeddings, expect the upgrade to take about as long as saving every
row again, and plan it as maintenance (for example, run the migration script outside application start-up). Searches
stay exact before the conversion and during a rolling upgrade: a key without a class is treated as the highest class,
so such rows are always read (only the speed-up is missing). Rows written by 0.2 cannot be read correctly by 0.1, so
don't roll back to 0.1 against an upgraded database.

## Schema

All tables live in `Schema`, named `TablePrefix` + the name below. Ids are `text` primary keys with the `C`
collation, so paging by id is ordinal. Every table has a `group_id` column and index.

| Table | Holds | Notable columns and indexes |
| --- | --- | --- |
| `episodes` | Episodes | `content`, `source`, `source_description`, `valid_at`, `entity_edges text[]`, `episode_metadata jsonb`, `idempotency_key` (unique with `group_id`); full-text over content, source and source description |
| `entities` | Entities | `name` (indexed with `group_id` for exact-name lookups), `summary`, `labels text[]` (GIN), `attributes jsonb` (GIN `jsonb_path_ops`, for attribute-value lookups), `name_embedding vector(N)` (HNSW, cosine); full-text over name and summary |
| `communities` | Communities | `name`, `summary`, `name_embedding vector(N)` (HNSW); full-text over name |
| `sagas` | Sagas | `name` (indexed with `group_id`), summary bookkeeping |
| `entity_edges` | Facts | `source_node_uuid`, `target_node_uuid`, `name`, `fact`, `episodes text[]`, the four temporal columns (indexed), `attributes jsonb`, `fact_embedding vector(N)` (HNSW); full-text over name and fact |
| `episodic_edges` | Episode mentions entity | |
| `community_edges` | Community has member | |
| `has_episode_edges` | Saga has episode | |
| `next_episode_edges` | Episode chain | |
| `search_statistics` | Keyword-ranking statistics | Not part of the EF Core model; kept by triggers (see below) |
| `search_term_counts` | Rows per keyword key, group and configuration | Not part of the EF Core model; kept by the same triggers |

Every edge table has `source_node_uuid`, `target_node_uuid`, an index on (source, target) and one on target. Edges
reference nodes by id without foreign keys, as in a graph database: deleting a node deletes every edge touching it
(in one round trip), and edges may be saved in any order within a bulk save.

The searchable tables (`episodes`, `entities`, `communities`, `entity_edges`) have four full-text columns, which the
driver computes when it writes a row:

- `text_search_config regconfig`, the row's text search configuration, from its group;
- `search_terms jsonb`, each term of the searchable fields mapped to its frequency in each field (packed into one
  number, 20 bits per field);
- `search_keys text[]`, each field's distinct terms with their impact class as `field:term|class` (for example
  `1:alice|7`; see [Keyword search](#keyword-search-and-ranking)), with a GIN index;
- `search_lengths integer[]`, the number of terms in each field.

Three statement-level triggers on each of them keep `search_statistics` and `search_term_counts` current.

`GraphitiSchema.Tables(options)` lists these tables for a schema name and table prefix, without a database: each
`GraphitiTable` has the `Schema`, the `Name` (with the prefix), the `QualifiedName` and the `RecordType` it holds
(`null` for `search_statistics` and `search_term_counts`). Use it for a data map, a backup or retention policy, or a test that lists the
schema. It reads the same EF Core model `BuildIndicesAndConstraintsAsync` creates the tables from, and a test checks
the two against each other.

## Embedding size

The vector size is part of the schema (`vector(N)`), set by `EmbeddingDimension`; the default 1024 matches upstream's
`EMBEDDING_DIM`. Set it to your embedder's size, for example **1536 for OpenAI `text-embedding-3-small`** (3072 for
`text-embedding-3-large`, which pgvector's HNSW cannot index as `vector`: use a smaller `dimensions` setting on the
embedding request). Saving a vector of another size fails. Changing the size means a new migration (or
`deleteExisting: true`) and re-embedding.

## Search

- **Vector search** is cosine similarity, `1 - (embedding <=> query)`, keeping results scoring strictly above
  `minScore`, as upstream. By default it is exact over the rows matching the group and filters, which is what
  upstream's Neo4j query does and stays fast per group up to tens of thousands of rows. Set
  `ApproximateVectorSearch = true` to let the HNSW indexes answer instead: much faster on large graphs, but pgvector
  applies the filters after the index scan, so a selective filter can return fewer than `limit` results (raise
  `hnsw.ef_search` to compensate).
- **Breadth-first search** is a recursive CTE following facts from source to target (and an episode's mentions), up
  to `maxDepth` hops, as upstream's Neo4j queries.
- **Filters** (`SearchFilters`) become parameterised SQL: date filters with their AND/OR groups, `IsNull` /
  `IsNotNull`, labels, edge types, ids and property filters on the `jsonb` attributes. No user input is ever
  concatenated into SQL.

### Keyword search and ranking

Upstream searches Neo4j full-text indexes: Lucene, the `standard-no-stop-words` analyzer, the query's terms OR-ed,
results ranked by BM25. The driver does the same in SQL and returns **the same records in the same order as the
in-memory driver** (whose BM25 reproduces upstream's rankings on the parity datasets) for the same data; tests compare
the two on a mixed corpus.

- **Terms.** Text is split like Lucene's `StandardTokenizer` (`FullText.Tokenize` in `GraphitiNet.Core`, shared by
  both drivers) and lower-cased: `ADDED_TO_WORKSPACE`, `can't`, `u.s.a` and `1,000` are one term each; there are no
  stop words and no stemming. The splitting happens in .NET, because PostgreSQL's own parser splits differently. A
  record matches when it contains **any** query term, like Lucene's default `OR`.
- **Score.** Lucene's BM25 with its defaults, `k1 = 1.2` and `b = 0.75`: for each field the index covers (entities:
  name, summary; facts: name, fact; episodes: content, source, source description; communities: name) and each query
  term (a repeated term counts again), `idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * length / averageLength))` with
  `idf = ln(1 + (N - df + 0.5) / (df + 0.5))`, summed. Ties go to the smaller uuid. The inverse document frequencies
  and average lengths are computed in .NET with the in-memory driver's own expressions and sent as parameters;
  PostgreSQL performs the rest of the arithmetic in the same order, so scores are identical to the bit and ties break
  the same way.
- **Statistics** cover the whole table, every group, as one Lucene index covers every group, and are per field:
  `N`, the rows whose field has any term; the fields' total length; and, per query term, its document frequency.
  Row counts and lengths come from `search_statistics`; document frequencies from `search_term_counts`, which counts
  the rows holding each `search_keys` key (and the rows of each group and text search configuration). Statement-level
  triggers update both in the writing transaction, without ever making concurrent writers wait on one another: for
  `search_statistics` writers insert small delta rows, which a trigger folds now and then under a try-lock; for
  `search_term_counts` each transaction adds its changes to the keys' rows in one of eight *lanes* that it holds
  alone (an advisory lock), so no two writers update the same row. A search reads its statistics and its rows in one
  repeatable-read snapshot, so the statistics always match exactly the rows it scores, whatever is written
  concurrently. Reading them is one round trip that touches a few rows per query term, whatever the table's size.
- **Speed.** Each key in `search_keys` carries the term's *impact class* in that field: its frequency (1, 2, 3, 4 or
  more) and the field's length divided by it (four classes per doubling). A class bounds what the term can add to a
  row's score whatever the statistics, so the GIN index can find the rows where a term weighs at least so much, and a
  row's score is at most the sum of its keys' bounds. A search then reads few rows, in up to three passes, and still
  returns exactly what scoring every matching row would (each bound is computed with a margin far above
  floating-point rounding, so a row left out scores strictly below the last result, and ties break as in a full
  pass):
  1. **First pass** (Lucene's MaxScore, by impact class instead of by term): the keys with the highest bounds, while
     about 1,500 of their rows are in the searched groups, are scored. The last of the best `limit` scores is a
     threshold. A row holding none of those keys can score at most the sum, over the query's (term, field) pairs, of
     the best bound left; when the threshold is above that, these are the answer (typical when a query mixes rare and
     common words).
  2. **Second pass** (in the spirit of block-max WAND): otherwise, only rows whose keys' bounds add up to the
     threshold can still beat it. Each pair's remaining classes are grouped into a few tiers, and every combination of
     tiers whose bounds reach the threshold becomes one conjunction of key sets, which the GIN index answers without
     reading a row. A query made only of common words matches most rows, but its best results hold several of the
     words with high weights, which few rows do: at a million records, this pass typically reads tens to a few
     thousand rows instead of hundreds of thousands.
  3. **Full pass**, only when the groups searched are small (their rows are read through the `group_id` index and
     tested with the term map) or when pruning cannot help.

  Rows written by graphiti-net 0.1 have keys without a class (`field:term`) until `BuildIndicesAndConstraintsAsync`
  converts them; searches treat such a key as the highest class, so results stay exact while both kinds coexist
  (during a rolling upgrade, say), only slower. A search takes three to six round trips, in one transaction, for
  which it sets (`SET LOCAL`, nothing outlives it) `enable_seqscan = off` (the planner cannot estimate how few rows
  the key conditions select), `work_mem = '64MB'` (so GIN bitmaps stay exact), and no parallel workers or JIT
  compilation (both take milliseconds to start, more than these statements run).

Differences from Neo4j that remain: Lucene stores field lengths in one byte (lossy) and computes in single precision,
and its statistics count deleted documents until segments merge, so upstream's own scores drift slightly between
runs; graphiti-net computes exactly, in double precision.

### Languages

The text search configuration decides how terms are normalised. Under the default, `simple`, the tokenizer's terms are
indexed as they are, which is exactly upstream's analyzer. Under a language configuration (`english`, `german`,
`arabic` and others: `SELECT cfgname FROM pg_ts_config`), each term is passed through PostgreSQL's
`to_tsvector(config, term)`, which stems it (so "runs" finds "running") or drops it as a stop word, and the query's
terms are normalised the same way. BM25 then works on those lexemes exactly as above: dropped stop words don't count
towards a field's length, and statistics are still per table, across every group and language.

Each group can have its own language:

```csharp
new PostgresGraphOptions
{
    DefaultTextSearchConfig = "simple",
    TextSearchConfigs = new Dictionary<string, string> { ["tenant-cairo"] = "arabic" },
    TextSearchConfigSelector = groupId => groupId.StartsWith("en-") ? "english" : null, // null: fall back
};
```

The selector wins, then the dictionary, then the default. Each row stores the configuration it was written with and
is matched against the query normalised with that same configuration, so a search across groups with different
languages works. A group's configuration must not change once it has data; re-save its rows if it does.

## Several app servers: `PostgresGroupLock`

`Graphiti` runs one write at a time per group (see `IGroupLock` in [api-compatibility.md](api-compatibility.md)), but
its default lock, `InProcessGroupLock.Shared`, works only within one process. When several app servers write to one
database, give every `Graphiti` instance on every server a `PostgresGroupLock`, with the driver's connection string and
options:

```csharp
var options = new PostgresGraphOptions { Schema = "graphiti", EmbeddingDimension = 1024 };
var driver = new PostgresDriver(connectionString, options);
await using var graphiti = new Graphiti(
    driver, llm, embedder, reranker,
    groupLock: new PostgresGroupLock(connectionString, options));

// With dependency injection: one lock per process.
services.AddSingleton<IGroupLock>(_ => new PostgresGroupLock(connectionString, options));
```

- **How it locks.** Each group's lock is a session-level advisory lock, `pg_advisory_lock(key1, key2)`, taken on a
  connection of its own with pooling off, so it is released exactly when the operation ends (a pooled connection
  would keep it after going back to the pool, or lose it to a session reset). One held group uses one connection; the
  operations of one process waiting for one group queue in process and use none, so only the next in line waits in
  PostgreSQL. The wait has no time limit: the lock's session sets `statement_timeout`, `lock_timeout` and (14 or
  later) `idle_session_timeout` to 0.
- **The key.** The first 8 bytes, big-endian, of the SHA-256 of the UTF-8 text
  `graphiti-net group lock\0{Schema}\0{TablePrefix}\0{groupId}`: `key1` is the upper 32 bits and `key2` the lower 32
  bits, as signed integers. Including the schema and prefix keeps two graphs in one database apart. The two-integer
  form (`objsubid = 2` in `pg_locks`) does not share keys with the single-`bigint` advisory locks the schema build
  takes. `KeyFor(groupId)` returns the 64-bit key; it is stable across versions, so servers on different
  graphiti-net versions still exclude each other. To see who holds a group:

  ```sql
  SELECT l.pid, a.application_name, a.client_addr
  FROM pg_locks l JOIN pg_stat_activity a USING (pid)
  WHERE l.locktype = 'advisory' AND l.objsubid = 2 AND l.granted
    AND ((l.classid::bigint << 32) | l.objid::bigint) = <KeyFor(groupId)>;
  ```
- **Cancellation.** Cancelling the operation's token while it waits cancels the waiting statement and closes the
  connection; no connection and no lock is left behind.
- **A dropped connection releases the lock.** If the holder's server dies, its backend is terminated or the network
  fails, PostgreSQL ends the session and the next server goes ahead. The holder is not told and finishes its
  operation unlocked; releasing afterwards does not throw. Turn on TCP keepalives (`tcp_keepalives_idle` on the
  server, `Keepalive` in the connection string) so a vanished client is noticed sooner.
- **Not re-entrant.** As with the in-process lock, an operation must not start another write to a group it already
  holds (for example from inside a model call): it would wait for itself until cancelled. Independent operations on
  one group simply take turns. Operations over several groups take them in ordinal order on every server, so they
  cannot deadlock.

## Performance notes

- Reads never track entities and never select embeddings or search columns. Vector and graph searches are one round
  trip each; keyword searches three to six, inside one read-only snapshot.
- **Keyword search at a million records.** `tools/GraphitiNet.Bench` generates a mostly Arabic graph of 1M records
  (300,000 entities, 550,000 facts, 140,000 episodes, 10,000 communities; Zipf-distributed words, so `في` or `من`
  are in most rows) over three large tenants and 400 small groups, a third of them under the `arabic` configuration,
  and measures every search type with rare, mid-frequency, common-only, mixed and name queries, per group scope,
  with and without filters (4 vCPU, PostgreSQL 16 with its default settings; warm medians, limit 10):

  | Queries | Scope | 0.1 | 0.2 |
  | --- | --- | --- | --- |
  | Common words only, entities | one large tenant (22% of the rows) | 1,081 ms | 33 ms |
  | Common words only, facts | one large tenant | 738 ms | 22 ms |
  | Common words only, episodes | one large tenant | 884 ms | 37 ms |
  | Common words only, entities | person + unit + project + site | 662 ms | 14 ms |
  | Common words only, entities | one small group | 749 ms | 6 ms |
  | Common words only, entities / facts / episodes | all groups | 2,266 / 1,761 / 3,019 ms | 61 / 44 / 49 ms |
  | Mixed (common, mid and rare words), facts | one large tenant | 621 ms | 23 ms |
  | Names, entities | all groups | 639 ms | 10 ms |
  | Rare words, entities / facts / episodes | one large tenant | 9 / 34 / 6 ms | 10 / 12 / 7 ms |

  On one large tenant's group every case has a median between 7 and 68 ms; across all groups at most 96 ms, and no
  case's 99th percentile exceeds 230 ms (0.1's slowest case took 3.1 s at the median). Every result is identical to
  0.1's. Setup, every case, write costs and the commands to reproduce are in
  [docs/benchmarks/postgresql-keyword-search.md](benchmarks/postgresql-keyword-search.md).
- Keyword search time follows the rows it reads: a query's best rows hold its words with high weights, and only rows
  that can still reach the results are read (see [Keyword search](#keyword-search-and-ranking)). Searches over a few
  small groups read those groups' rows directly. Give PostgreSQL enough `shared_buffers` (or OS cache) to hold the
  searchable tables' hot pages and the `search_keys` GIN indexes.
- Saves are batched upserts (`INSERT ... ON CONFLICT (uuid) DO UPDATE`), `BulkBatchSize` rows (default 500) per
  statement, so the statistics triggers run once per batch. Keeping the per-key counts costs writes: at a million
  records, bulk saves ran at 45-60% of 0.1's rate and a single save took about 2 ms more (see the benchmark).
  `AddNodesAndEdgesBulkAsync` writes everything in one transaction.
- HNSW indexes make inserts slower (each insert updates the graph index). For a large backfill into an empty graph,
  you can drop the HNSW indexes, load, and recreate them with a generous `maintenance_work_mem`; and if you keep
  `ApproximateVectorSearch` off, the HNSW indexes only serve your own approximate queries.
- Give PostgreSQL enough `shared_buffers` to hold the vector indexes you search.

## Testing

The tests in `tests/GraphitiNet.PostgreSql.Tests` run the shared driver conformance suite (once against one shared
schema at 1024 dimensions, once with a fresh schema per test) plus PostgreSQL-specific tests. They read the
connection string from `GRAPHITI_TEST_POSTGRES` (default
`Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests`), create the database if it is
missing, and work in schemas of their own. The PostgreSQL-specific tests skip when the server is unreachable. The
`PostgresGroupLock` tests (the shared `DistributedGroupLockTests`) run two lock objects and two `Graphiti` instances as
two servers, cancel a wait, and terminate the holder's backend with `pg_terminate_backend`.

Two tests cover `VectorStoreUnavailableException`. The "not permitted" one needs only the test server: it creates a
fresh database (where the extension does not exist yet) and a role without superuser rights, then drops both. The
"not installed" one needs a PostgreSQL server without pgvector, named by `GRAPHITI_TEST_POSTGRES_NO_PGVECTOR`, and
skips without it; CI runs the plain `postgres:16` image for it:

```bash
docker run -d --name pg-novector -e POSTGRES_PASSWORD=postgres -p 5433:5432 postgres:16
export GRAPHITI_TEST_POSTGRES_NO_PGVECTOR="Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres"
```
