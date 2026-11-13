# SQL Server storage

`GraphitiNet.SqlServer` stores the graph in SQL Server 2025 or Azure SQL, through EF Core: ordinary tables in a
database you already run, the native `vector` type and `VECTOR_DISTANCE` for vector search, and BM25 keyword search
over a keyword index the driver keeps in two more tables. No graph database is needed, and neither is full-text
search.

## Requirements

- SQL Server 2025 (any edition: Developer, Express, Standard, Enterprise, the Linux container) or Azure SQL Database
  / Azure SQL Managed Instance. Anything with the `vector` data type works; SQL Server 2022 and Azure SQL Edge do not
  have it.
- A login that may create a schema and tables once, or an administrator who runs the migration.
- Tested on `mcr.microsoft.com/mssql/server:2025-latest` (SQL Server 2025 RTM-CU11, 17.0.5065.9, Developer edition
  on Linux), database compatibility level 170.

For local development:

```bash
docker run -d --name mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Graphiti_dev_Passw0rd' \
  -p 1433:1433 mcr.microsoft.com/mssql/server:2025-latest
```

## Quick start

```csharp
using GraphitiNet.Driver.SqlServer;

await using var driver = new SqlServerDriver(
    "Server=localhost;Database=graphiti;User Id=app;Password=...;TrustServerCertificate=True");
await driver.GraphOps.BuildIndicesAndConstraintsAsync(); // once: creates the schema, tables and indexes
```

`BuildIndicesAndConstraintsAsync` is idempotent: it creates the schema, any missing tables with their indexes, and any
index a newer version added to an existing table, and leaves existing data alone. It builds the DDL from the same EF
Core model a migration would use, so the two never disagree. It runs in one transaction under an application lock
(`sp_getapplock`), so several app instances may call it at start-up. `BuildIndicesAndConstraintsAsync(deleteExisting:
true)` drops the eleven tables first: **all graph data is lost**.

Options:

```csharp
var driver = new SqlServerDriver(connectionString, new SqlServerGraphOptions
{
    Schema = "graphiti",       // default "graphiti"
    TablePrefix = "",          // default none, e.g. "graph_"
    EmbeddingDimension = 1536, // default 1024; must match your embedder; at most 1998
    BulkBatchSize = 500,       // rows per round trip in bulk saves
});
```

The driver creates a short-lived `DbContext` per operation and borrows its pooled SqlClient connection, so one driver
instance can be shared by the whole application (register it as a singleton). Commands use the context's command
timeout.

## Two ways to host the tables

### Standalone: `GraphitiDbContext`

The driver above uses `GraphitiDbContext` (in `GraphitiNet.Driver.SqlServer`), a context holding only the graph
tables. You can also use it directly with LINQ, including EF Core 10's vector functions:

```csharp
await using var db = new GraphitiDbContext(connectionString, options);
var query = new SqlVector<float>(embedding);
var nearest = await db.Entities
    .Where(e => e.GroupId == "tenant-1" && e.NameEmbedding != null)
    .OrderBy(e => EF.Functions.VectorDistance("cosine", (SqlVector<float>)e.NameEmbedding!, query))
    .Take(10)
    .ToListAsync();
```

For dependency injection:

```csharp
services.AddDbContextFactory<GraphitiDbContext>(o => o
    .UseSqlServer(connectionString)          // or UseAzureSql(connectionString)
    .UseGraphitiOptions(graphOptions));
services.AddSingleton<IGraphDriver>(sp =>
{
    var factory = sp.GetRequiredService<IDbContextFactory<GraphitiDbContext>>();
    return new SqlServerDriver(factory.CreateDbContext, graphOptions);
});
```

### Inside your own `DbContext`

Add the tables to your application's context, so they live in your schema and your migrations:

```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public static readonly SqlServerGraphOptions Graph = new() { Schema = "app", TablePrefix = "graph_" };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddGraphiti(Graph);
    }
}

services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));
services.AddSingleton<IGraphDriver>(sp => new SqlServerDriver(
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext, AppDbContext.Graph));
```

Pass the **same** `SqlServerGraphOptions` to `AddGraphiti` and `SqlServerDriver`; the driver checks the schema and
table names on first use and throws if they differ. `AddGraphiti` adds no global query filter: every driver call is
scoped by the group ids it is given, as the driver contract requires, so tenant isolation does not depend on the
context's filters.

The entity classes (`EntityNodeRecord`, `EntityEdgeRecord`, `SearchTermRecord` and so on, in
`GraphitiNet.Driver.SqlServer.Entities`) are public so your context can query them, but write through the driver: it
keeps the keyword index, labels and cascades right.

## Migrations

Teams that manage schemas with EF Core migrations don't need `BuildIndicesAndConstraintsAsync`:

- **Your own context:** `dotnet ef migrations add AddGraphiti` produces the eleven tables, the `vector(N)` columns and
  every index, for your schema, prefix and embedding size.
- **`GraphitiDbContext`:** the package ships no migration, because the schema, prefix and vector size are your choice.
  Generate one in your application project with an `IDesignTimeDbContextFactory<GraphitiDbContext>` that calls
  `UseSqlServer(...)` and `UseGraphitiOptions(...)`, as in [PostgreSQL storage](postgresql.md#migrations).

`BuildIndicesAndConstraintsAsync` and migrations produce the same tables. If you start with the former and move to
migrations later, mark the initial migration as applied instead of running it.

### Upgrading from 0.1.0

The next release adds idempotency keys on episodes
([#9](https://github.com/tekthar/graphiti-net/issues/9)): a nullable `idempotency_key nvarchar(255)` column (binary
collation) on `episodes` and a unique index `ux_episodes_group_id_idempotency_key` on (`group_id`, `idempotency_key`),
filtered on `[idempotency_key] IS NOT NULL` so rows without a key never collide. Existing rows need no change.

- **`BuildIndicesAndConstraintsAsync`:** call it once after upgrading the package; it adds the column and the index to
  an existing database. It is idempotent.
- **EF Core migrations:** add a migration after upgrading (`dotnet ef migrations add GraphitiIdempotencyKeys`). It
  contains, for your schema and prefix:

  ```csharp
  migrationBuilder.AddColumn<string>(
      name: "idempotency_key", schema: "graphiti", table: "episodes", type: "nvarchar(255)", maxLength: 255,
      collation: "Latin1_General_100_BIN2", nullable: true);
  migrationBuilder.CreateIndex(
      name: "ux_episodes_group_id_idempotency_key", schema: "graphiti", table: "episodes",
      columns: new[] { "group_id", "idempotency_key" }, unique: true, filter: "[idempotency_key] IS NOT NULL");
  ```

  Until the migration is applied, every episode write fails with "Invalid column name 'idempotency_key'".

## Schema

All tables live in `Schema`, named `TablePrefix` + the name below. Ids, group ids and edge endpoints are
`nvarchar(255)` with the binary collation `Latin1_General_100_BIN2`, so paging by id is ordinal and lookups are exact
whatever the database's default collation. Names use the same collation, so exact-name lookups are case-sensitive.
Lists (`labels`, `episodes`, `entity_edges`) are JSON arrays and attributes JSON objects, in `nvarchar(max)` columns.
Times are `datetimeoffset`, stored in UTC.

| Table | Holds | Notable columns and indexes |
| --- | --- | --- |
| `episodes` | Episodes | `content`, `source`, `source_description`, `valid_at` (indexed with `group_id`), `entity_edges`, `episode_metadata`, `idempotency_key` (unique with `group_id`, filtered on not null) |
| `entities` | Entities | `name`, `name_key` (a persisted SHA-256 of the name, indexed with `group_id` for exact-name lookups), `summary`, `labels`, `attributes`, `name_embedding vector(N)` |
| `communities` | Communities | `name`, `summary`, `name_embedding vector(N)` |
| `sagas` | Sagas | `name`, `name_key` (indexed with `group_id`), summary bookkeeping |
| `entity_edges` | Facts | `source_node_uuid`, `target_node_uuid`, `name`, `fact`, `episodes`, the four temporal columns (indexed), `attributes`, `fact_embedding vector(N)` |
| `episodic_edges` | Episode mentions entity | |
| `community_edges` | Community has member | |
| `has_episode_edges` | Saga has episode | |
| `next_episode_edges` | Episode chain | |
| `search_terms` | Keyword index: one row per (record, field, term) | `doc_kind`, `uuid`, `field`, `term`, `group_id`, `frequency`, `field_length`; clustered on (`doc_kind`, `term`) |
| `search_fields` | Keyword index: the length of each non-empty searchable field | `doc_kind`, `uuid`, `field`, `field_length` |

Every edge table has an index on (source, target) and one on target. Edges reference nodes by id without foreign
keys, as in a graph database: deleting a node deletes every edge touching it, and their keyword index rows, in one
transaction.

## Embedding size

The vector size is part of the schema (`vector(N)`), set by `EmbeddingDimension`; the default 1024 matches upstream's
`EMBEDDING_DIM`. Set it to your embedder's size, for example **1536 for OpenAI `text-embedding-3-small`**. SQL
Server's `vector` holds at most 1,998 single-precision dimensions, so for `text-embedding-3-large` request fewer
`dimensions`. Saving a vector of another size fails; a query vector of another size matches nothing.

## Search

- **Vector search** is cosine similarity on Neo4j's scale, `1 - VECTOR_DISTANCE('cosine', embedding, query) / 2`,
  which is `(1 + cosine) / 2`, keeping results scoring strictly above `minScore`, as upstream. It is exact over the
  rows matching the group and filters, as upstream's Neo4j query. Zero vectors never match. The query vector travels
  as a native binary `vector` parameter. SQL Server 2025's approximate vector indexes (`CREATE VECTOR INDEX`,
  DiskANN) are a preview feature and are not used.
- **Breadth-first search** runs as one batch that walks facts from source to target (and an episode's mentions) one
  hop per round, up to `maxDepth` hops, visiting each node once per origin at its fewest hops. A recursive CTE would
  re-walk every path (SQL Server's recursive CTEs cannot deduplicate), which grows quickly on dense graphs.
- **Filters** (`SearchFilters`) become parameterised T-SQL: date filters with their AND/OR groups, `IsNull` /
  `IsNotNull`, labels, edge types, ids and property filters on the JSON attributes (through `OPENJSON`). Lists travel
  as one JSON parameter each, so no query approaches SQL Server's 2,100-parameter limit. No user input is ever
  concatenated into SQL.

### Keyword search and ranking

Keyword search is **Okapi BM25 the way Lucene ranks it**, as upstream's Neo4j full-text indexes and graphiti-net's
in-memory driver do: k1 = 1.2, b = 0.75, each query term scored against each field with that field's own statistics
(document count, average length, document frequency), summed; a record matches when it holds **any** query term.
Text is split with the in-memory driver's tokenizer, which follows Lucene's `StandardTokenizer` (`WORKS_AT` is one
term; no stemming, no stop words; any script, Arabic included). The collection statistics cover every record of the
type, as a Lucene index covers every group; only the requested groups are scored. Rankings match the in-memory driver
(a test checks this), and both parity datasets match upstream on SQL Server.

Why not SQL Server full-text search? It is not installed in the SQL Server Linux container images (the
`mssql-server-fts` package must be added), it ranks with its own formula rather than BM25 (`CONTAINSTABLE` ranks are
not comparable across queries), its word breakers differ from Lucene's (for example around underscores), and full-text
indexes populate asynchronously, so a fact saved a moment ago may not be found. The keyword index instead is written in
the same transaction as the record: one `search_terms` row per distinct term per field and one `search_fields` row per
non-empty field, rewritten on every save and deleted with the record.

## Performance notes

- Reads never track entities and never select embeddings; each search is one round trip.
- Saves are batched upserts: `BulkBatchSize` records (default 500) travel as one JSON parameter into a
  `MERGE ... WITH (HOLDLOCK)`, followed by the keyword index rewrite, in one transaction.
  `AddNodesAndEdgesBulkAsync` writes everything in one transaction.
- Measured on the SQL Server 2025 container (one laptop-class machine, 1024-dimension embeddings): bulk saves run at
  about 540 records per second (10,000 entities and 20,000 facts in 56 s), most of it parsing the embeddings out of
  JSON. With 30,000 records in ten groups: keyword search 15-60 ms, vector search 15-40 ms within one group and
  about 150 ms across all 20,000 facts, three-hop breadth-first search under 10 ms, exact-name lookups under 20 ms.
- Vector search is exact, so its cost grows with the number of embedded rows in the searched groups. Keyword search
  reads the index rows of the query's terms, so very common terms cost the most.

## Testing

The tests in `tests/GraphitiNet.SqlServer.Tests` run the shared driver conformance suite (once against one shared
schema at 1024 dimensions, once with a fresh schema and a table prefix per test) plus SQL-Server-specific tests:
idempotent schema creation, concurrent builds, the host `DbContext` mode, bulk transaction rollback, Unicode and Arabic
round trips and keyword search, exact collation, keyword index maintenance and ranking against the in-memory driver.
They read the connection string from `GRAPHITI_TEST_SQLSERVER` (default
`Server=localhost,1433;Database=graphiti_sqlserver_tests;User Id=sa;Password=Graphiti_dev_Passw0rd;TrustServerCertificate=True`,
the container above), create the database if it is missing, work in schemas of their own, and skip when the server is
unreachable. The parity replay (`tests/GraphitiNet.Parity.Tests`) runs both datasets on SQL Server with the same
variable.
