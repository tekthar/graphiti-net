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
alone (rows missing their full-text columns, from an earlier version or written outside the driver, are indexed). It
builds the DDL from the same EF Core model a migration would use, so the two never disagree, then installs the
full-text statistics triggers and recounts the statistics (see [Keyword search](#keyword-search-and-ranking)), which
reads each searchable table once. It runs in one transaction under an advisory lock, so several app instances may call
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

It creates the `search_statistics` table and its triggers and counts the rows already there; it is idempotent, so
it can also run again later (after a migration that recreates the graph tables, say). Without it, keyword search
throws an `InvalidOperationException` saying so, rather than ranking on wrong statistics. Calling
`BuildIndicesAndConstraintsAsync` instead also works.

`BuildIndicesAndConstraintsAsync` and migrations produce the same tables. If you start with the former and move to
migrations later, mark the initial migration as applied (insert its id into `__EFMigrationsHistory`) instead of
running it.

## Schema

All tables live in `Schema`, named `TablePrefix` + the name below. Ids are `text` primary keys with the `C`
collation, so paging by id is ordinal. Every table has a `group_id` column and index.

| Table | Holds | Notable columns and indexes |
| --- | --- | --- |
| `episodes` | Episodes | `content`, `source`, `source_description`, `valid_at`, `entity_edges text[]`, `episode_metadata jsonb`; full-text over content, source and source description |
| `entities` | Entities | `name` (indexed with `group_id` for exact-name lookups), `summary`, `labels text[]` (GIN), `attributes jsonb` (GIN `jsonb_path_ops`, for attribute-value lookups), `name_embedding vector(N)` (HNSW, cosine); full-text over name and summary |
| `communities` | Communities | `name`, `summary`, `name_embedding vector(N)` (HNSW); full-text over name |
| `sagas` | Sagas | `name` (indexed with `group_id`), summary bookkeeping |
| `entity_edges` | Facts | `source_node_uuid`, `target_node_uuid`, `name`, `fact`, `episodes text[]`, the four temporal columns (indexed), `attributes jsonb`, `fact_embedding vector(N)` (HNSW); full-text over name and fact |
| `episodic_edges` | Episode mentions entity | |
| `community_edges` | Community has member | |
| `has_episode_edges` | Saga has episode | |
| `next_episode_edges` | Episode chain | |
| `search_statistics` | Keyword-ranking statistics | Not part of the EF Core model; kept by triggers (see below) |

Every edge table has `source_node_uuid`, `target_node_uuid`, an index on (source, target) and one on target. Edges
reference nodes by id without foreign keys, as in a graph database: deleting a node deletes every edge touching it
(in one round trip), and edges may be saved in any order within a bulk save.

The searchable tables (`episodes`, `entities`, `communities`, `entity_edges`) have four full-text columns, which the
driver computes when it writes a row:

- `text_search_config regconfig`, the row's text search configuration, from its group;
- `search_terms jsonb`, each term of the searchable fields mapped to its frequency in each field (packed into one
  number, 20 bits per field);
- `search_keys text[]`, each field's distinct terms as `field:term` (for example `1:alice`), with a GIN index;
- `search_lengths integer[]`, the number of terms in each field.

Three statement-level triggers on each of them keep `search_statistics` current.

`GraphitiSchema.Tables(options)` lists these tables for a schema name and table prefix, without a database: each
`GraphitiTable` has the `Schema`, the `Name` (with the prefix), the `QualifiedName` and the `RecordType` it holds
(`null` for `search_statistics`). Use it for a data map, a backup or retention policy, or a test that lists the
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
  Document frequencies are counted from the GIN index on `search_keys` for just the query's terms (no row is read).
  Row counts and lengths come from `search_statistics`, which the triggers update in the writing transaction:
  writers only insert small delta rows (so concurrent writers never wait on one another), and now and then a trigger
  folds a table's deltas into one row under a try-lock. A search reads its statistics and its rows in one
  repeatable-read snapshot, so the statistics always match exactly the rows it scores, whatever is written
  concurrently.
- **Speed.** Candidates come from the GIN index; each candidate's frequencies are decoded once and its score is one
  SQL expression. A query mixing rare and common terms first scores only the rows with its rare terms; a term adds at
  most `idf * (k1 + 1)` per field however often it repeats, so when those rows already fill the limit with scores
  above what the common terms alone could reach, they are the answer, exactly as a full pass would rank them
  (Lucene's MaxScore), and the many rows holding only common terms are never read. A search takes two or three
  round trips.

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

## Performance notes

- Reads never track entities and never select embeddings or search columns. Vector and graph searches are one round
  trip each; keyword searches two or three (statistics, then scoring, inside one read-only snapshot).
- Keyword search time follows the number of rows that contain a query term. On a synthetic graph of 20,000
  entities, 50,000 facts and 5,000 episodes in one group (Zipf-distributed words, so a few words occur in most rows;
  PostgreSQL 16 in a small container; 40 queries of 3 to 8 words each, median and 90th percentile):

  | Queries | BM25 (this version) | `ts_rank` (before) |
  | --- | --- | --- |
  | Rare words, facts | 4 ms / 5 ms | 38 ms / 46 ms |
  | Mid-frequency words, facts | 16–18 ms / 27 ms | 45–49 ms / 54–64 ms |
  | Mid-frequency words, entities | 17–18 ms / 22–32 ms | 28–29 ms / 36–37 ms |
  | Random words (several in most rows), facts | 37–41 ms / 72–80 ms | 109–120 ms / 149–155 ms |
  | Random words, episodes | 13 ms / 53–55 ms | 69–75 ms / 92–95 ms |
  | Random words, entities | 99–104 ms / 156–176 ms | 59–64 ms / 79–82 ms |

  Rare-terms-first pruning settles most mixed queries early. It cannot help when common words carry real weight, as
  in the two-word entity names here: then every matching row is scored, and on entities whose summaries share
  the query's commonest words (90% of the table matches) a query costs about 5 µs per matching row. Saving the
  same graph took 13% longer (the terms are computed in .NET; the triggers add one small insert per statement).
- Saves are batched upserts (`INSERT ... ON CONFLICT (uuid) DO UPDATE`), `BulkBatchSize` rows (default 500) per
  round trip. `AddNodesAndEdgesBulkAsync` writes everything in one transaction.
- HNSW indexes make inserts slower (each insert updates the graph index). For a large backfill into an empty graph,
  you can drop the HNSW indexes, load, and recreate them with a generous `maintenance_work_mem`; and if you keep
  `ApproximateVectorSearch` off, the HNSW indexes only serve your own approximate queries.
- Give PostgreSQL enough `shared_buffers` to hold the vector indexes you search.

## Testing

The tests in `tests/GraphitiNet.PostgreSql.Tests` run the shared driver conformance suite (once against one shared
schema at 1024 dimensions, once with a fresh schema per test) plus PostgreSQL-specific tests. They read the
connection string from `GRAPHITI_TEST_POSTGRES` (default
`Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests`), create the database if it is
missing, and work in schemas of their own. The PostgreSQL-specific tests skip when the server is unreachable.

Two tests cover `VectorStoreUnavailableException`. The "not permitted" one needs only the test server: it creates a
fresh database (where the extension does not exist yet) and a role without superuser rights, then drops both. The
"not installed" one needs a PostgreSQL server without pgvector, named by `GRAPHITI_TEST_POSTGRES_NO_PGVECTOR`, and
skips without it; CI runs the plain `postgres:16` image for it:

```bash
docker run -d --name pg-novector -e POSTGRES_PASSWORD=postgres -p 5433:5432 postgres:16
export GRAPHITI_TEST_POSTGRES_NO_PGVECTOR="Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres"
```
