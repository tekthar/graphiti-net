# PostgreSQL storage

`GraphitiNet.PostgreSql` is graphiti-net's default production storage: the graph lives in nine ordinary tables in a
PostgreSQL database you already run, through EF Core, with [pgvector](https://github.com/pgvector/pgvector) for
vector search and PostgreSQL's built-in full-text search for keyword search. No graph database is needed.

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
with their indexes, and any index a newer version added to an existing table, and leaves existing data alone. It builds the DDL from the same EF Core model a
migration would use, so the two never disagree. It runs in one transaction under an advisory lock, so several app
instances may call it at start-up. `BuildIndicesAndConstraintsAsync(deleteExisting: true)` drops the nine tables
first: **all graph data is lost**.

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
cascades right.

## Migrations

Teams that manage schemas with EF Core migrations don't need `BuildIndicesAndConstraintsAsync`.

- **Your own context:** run `dotnet ef migrations add AddGraphiti` as usual. The migration contains the nine tables,
  the `vector` extension, the HNSW and GIN indexes and the generated search columns, for your schema, prefix and
  embedding size. Apply it with `dotnet ef database update` or `context.Database.MigrateAsync()`.
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

Every edge table has `source_node_uuid`, `target_node_uuid`, an index on (source, target) and one on target. Edges
reference nodes by id without foreign keys, as in a graph database: deleting a node deletes every edge touching it
(in one round trip), and edges may be saved in any order within a bulk save.

The searchable tables (`episodes`, `entities`, `communities`, `entity_edges`) have a `text_search_config regconfig`
column, set from the row's group when it is written, and a stored generated `search_vector tsvector` built with that
configuration, with a GIN index.

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

A record matches when it contains **any** query term, like Lucene's default `OR` upstream (PostgreSQL's own
`plainto_tsquery` and `websearch_to_tsquery` would require every term). The driver normalises the query with the
row's text search configuration and ORs the resulting lexemes, quoting each so operators in user input are plain
text.

Upstream ranks with Lucene's BM25; PostgreSQL has no BM25. The driver ranks by **the number of distinct query terms a
record contains**, then by `ts_rank` with length normalisation (`ts_rank(vector, query, 1)`). Counting matched terms
first stands in for BM25's inverse document frequency, which rewards covering more of the query; `ts_rank` then
favours more occurrences in shorter text. This ordering passed the same relevance tests as the BM25 in-memory driver,
but scores are not BM25 scores, so keyword rankings differ slightly from Python Graphiti. Keyword results are one
input to the fused ranking, so the effect on final search results is small.

For BM25 in PostgreSQL, ParadeDB's [`pg_search`](https://github.com/paradedb/paradedb) extension adds it where the
host allows extensions (it is not available on most managed services). The driver does not use it yet; it would
replace the `tsvector` columns with a `bm25` index.

### Languages

The text search configuration decides how words are normalised. The default, `simple`, lower-cases words and does no
stemming and no stop-word removal, the closest built-in match to the analyzer upstream's Neo4j full-text indexes use.
A language configuration (`english`, `german`, `arabic` and others: `SELECT cfgname FROM pg_ts_config`) adds stemming,
so "runs" finds "running", and drops stop words.

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

- Reads never track entities and never select embeddings or search vectors; searches are one round trip each.
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
