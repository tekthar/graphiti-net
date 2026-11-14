# PostgreSQL keyword search at a million mostly Arabic records

Date: 2026-11-13. graphiti-net 0.2 (this branch) against 0.1.0 (commit `4a13c44`), PostgreSQL 16.15 with pgvector,
on one machine. The benchmark is `tools/GraphitiNet.Bench`; it is not part of CI.

## Summary

Keyword (BM25) search on PostgreSQL ranked every row holding any query word, and counted each word's document
frequency through the GIN index, so a query made only of common words (`في من على`) read most of a table: about
**1 s** for one large tenant and **2.5 s** across all groups at this size. 0.2 reads only the rows that can still make
the top results (see [Keyword search and ranking](../postgresql.md#keyword-search-and-ranking)) and keeps every
statistic it needs in tables the triggers maintain. The same queries now take **20 to 40 ms** for a large tenant's
group and **45 to 60 ms** across every group (medians), with **identical results**: all 1,280 benchmark queries
returned the same records in the same order before and after, and the in-memory driver's ranking matches on a
`simple`-configuration copy (see [Exactness](#exactness)). Writes pay for it: bulk saves run at about half the rate
(see [Write cost](#write-cost)).

| Common words only, warm median / p99 (ms) | 0.1.0 | 0.2 |
| --- | --- | --- |
| Entities, one large tenant (`org-alpha`, 22% of the rows) | 1,081 / 1,762 | 33 / 63 |
| Facts, one large tenant | 738 / 1,218 | 22 / 50 |
| Episodes, one large tenant | 884 / 1,208 | 37 / 164 |
| Entities, the large `arabic` tenant (`org-beta`) | 758 / 1,310 | 38 / 60 |
| Entities, person + org unit + project + site | 662 / 1,300 | 14 / 19 |
| Entities, one small group | 749 / 1,312 | 6.2 / 6.9 |
| Entities, all groups | 2,266 / 3,194 | 61 / 184 |
| Facts, all groups | 1,761 / 2,606 | 44 / 128 |
| Episodes, all groups | 3,019 / 3,415 | 49 / 229 |
| **Slowest of all 128 cases** (any words, scope, filter) | 3,096 median, 3,591 p99 | 96 median, 229 p99 |

On one large tenant's group, every case (any words, any search type, filtered or not) has a median between 7 and
68 ms; across all 403 groups, at most 96 ms.

## Setup

- **Machine:** 4 vCPU, 16 GB RAM, PostgreSQL 16.15 with pgvector 0.6 and its default settings (`shared_buffers`
  128 MB, `work_mem` 4 MB, 2 parallel workers per gather, JIT on), on the same machine as the benchmark, which other
  jobs shared now and then (expect a few milliseconds of noise). A tuned server (`shared_buffers` at a quarter of RAM)
  would read rows from its own cache rather than the operating system's and be faster in both columns.
- **Data** (`Corpus.cs`, deterministic): 1,000,000 records: 300,000 entities (names of 2.5 words, summaries of 15 to
  65 words), 550,000 facts (a relation name, a fact of 8 to 25 words between two entities of the group), 140,000
  episodes (messages, texts and JSON of 40 to 240 words) and 10,000 communities. Words follow a Zipf-Mandelbrot law
  (`1 / (rank + 1.5)`) over 60,000 Arabic word forms (stop words first, then common nouns, verbs and adjectives with
  their prefixed and suffixed forms, then words built from random roots on Arabic patterns), with 6% English words
  and names. So `في` is in 36% of facts, 74% of entity summaries and 96% of episodes; `من` in 27%, 62% and 92%.
- **Groups:** 403, as AbpAgent uses them (one per person, org unit, project and site): three large tenants,
  `org-alpha` (22% of the records), `org-beta` (15%) and `org-gamma` (8%), and 400 small groups sharing the rest
  (from 0.02% to 0.3% each).
- **Languages:** `org-beta`, the projects and the sites use the `arabic` text search configuration (stemming, Arabic
  stop words dropped), everything else the default `simple`: about a third of the rows are `arabic`.
- **Embeddings:** none (`EmbeddingDimension = 1`, null vectors): keyword search does not read them.
- **Loading:** through the driver's `SaveBulkAsync`, 5,000 records per call, three loaders in parallel: 7 minutes for
  the million (about 2,400 records a second; 1,100 entities or facts and 300 episodes a second per loader).
  Resumable: a progress table records each saved chunk.

## Queries

Each case runs 10 queries (3 to 4 words, from a fixed seed) twice: **cold** is the first time each query runs in a
fresh process (new connections, the query's rows not recently read; the server and the operating system's cache were
not restarted), **warm** the second. Latency is measured in the client, statistics and every round trip included,
limit 10.

- **Words:** *rare* (2-3 words of rank 5,000 to 40,000), *mid* (2-4 of rank 100 to 1,000), *common* (2-4 of the 25
  commonest: stop words only), *mixed* (2 common, 1 mid, 1 rare), *names* (a person's or company's name, whose words
  are common among entity names).
- **Scopes:** `large` (`org-alpha`, `simple`), `large-ar` (`org-beta`, `arabic`), `small` (one person, 0.2%),
  `person+unit+project+site` (four small groups of both languages, as AbpAgent searches them), `large+3` (`org-alpha`
  and three small groups), `all` (no group filter).
- **Filters** (`filtered`): entities with the label `Person` or `Organization`; facts valid from the second year on.

## Results

Each cell: 0.1.0's median → **0.2's median** (0.1.0's p99 → 0.2's p99), warm, in milliseconds; cold runs and
communities are in [the full table](#full-results). Episodes have no filters and no name queries (communities match
none of the common or name queries: their names hold no stop words).

| Words | Scope | Entities | Facts | Episodes |
| --- | --- | --- | --- | --- |
| common | large | 1,081 → **33** (1,762 → 63) | 738 → **22** (1,218 → 50) | 884 → **37** (1,208 → 164) |
| common | large-ar | 758 → **38** (1,310 → 60) | 869 → **30** (1,457 → 58) | 565 → **62** (928 → 116) |
| common | small | 749 → **6.2** (1,312 → 6.9) | 531 → **4.9** (977 → 6.9) | 361 → **8.0** (522 → 12) |
| common | person+unit+project+site | 662 → **14** (1,300 → 19) | 434 → **9.5** (1,000 → 14) | 474 → **18** (706 → 22) |
| common | large+3 | 1,158 → **24** (1,876 → 210) | 682 → **30** (1,710 → 110) | 960 → **39** (1,356 → 178) |
| common | all | 2,266 → **61** (3,194 → 184) | 1,761 → **44** (2,606 → 128) | 3,019 → **49** (3,415 → 229) |
| common | large filtered | 1,012 → **43** (1,512 → 82) | 792 → **29** (1,339 → 65) | n/a |
| common | all filtered | 2,835 → **96** (3,441 → 184) | 1,551 → **14** (2,782 → 98) | n/a |
| mixed | large | 362 → **23** (1,121 → 29) | 621 → **23** (881 → 38) | 268 → **34** (305 → 47) |
| mixed | large-ar | 338 → **34** (801 → 43) | 419 → **31** (1,010 → 48) | 281 → **44** (352 → 78) |
| mixed | small | 347 → **6.1** (1,028 → 8.3) | 342 → **5.2** (708 → 5.6) | 230 → **7.4** (261 → 8.0) |
| mixed | person+unit+project+site | 574 → **16** (836 → 19) | 540 → **12** (1,296 → 13) | 302 → **18** (477 → 21) |
| mixed | large+3 | 544 → **23** (1,228 → 29) | 912 → **21** (1,612 → 41) | 622 → **30** (1,250 → 45) |
| mixed | all | 2,452 → **20** (3,590 → 34) | 1,186 → **21** (2,659 → 42) | 3,096 → **29** (3,528 → 43) |
| mixed | large filtered | 434 → **25** (801 → 33) | 677 → **23** (900 → 32) | n/a |
| mixed | all filtered | 2,702 → **17** (3,336 → 41) | 1,344 → **21** (2,683 → 54) | n/a |
| mid | large | 75 → **43** (123 → 80) | 70 → **18** (131 → 22) | 110 → **43** (182 → 90) |
| mid | large-ar | 81 → **44** (198 → 88) | 131 → **46** (187 → 66) | 116 → **68** (290 → 92) |
| mid | small | 43 → **5.2** (79 → 5.8) | 38 → **4.8** (66 → 5.9) | 49 → **5.9** (78 → 6.5) |
| mid | person+unit+project+site | 88 → **14** (155 → 15) | 124 → **12** (661 → 20) | 86 → **16** (165 → 18) |
| mid | large+3 | 97 → **25** (231 → 41) | 177 → **28** (244 → 36) | 133 → **28** (285 → 52) |
| mid | all | 355 → **21** (488 → 44) | 365 → **14** (598 → 20) | 575 → **32** (2,720 → 54) |
| mid | large filtered | 63 → **23** (128 → 39) | 75 → **24** (198 → 50) | n/a |
| mid | all filtered | 387 → **24** (658 → 58) | 350 → **16** (614 → 30) | n/a |
| rare | large | 9.0 → **9.8** (9.8 → 17) | 34 → **12** (43 → 22) | 5.7 → **6.8** (8.5 → 9.5) |
| rare | large-ar | 9.5 → **12** (15 → 13) | 79 → **16** (183 → 25) | 7.8 → **7.6** (13 → 13) |
| rare | small | 3.3 → **3.7** (4.2 → 4.4) | 16 → **3.0** (17 → 3.5) | 2.7 → **2.8** (3.1 → 5.5) |
| rare | person+unit+project+site | 7.8 → **6.2** (162 → 16) | 38 → **5.7** (69 → 9.5) | 5.7 → **4.6** (126 → 15) |
| rare | large+3 | 19 → **21** (195 → 38) | 69 → **19** (151 → 33) | 14 → **11** (134 → 38) |
| rare | all | 158 → **16** (301 → 23) | 273 → **8.9** (382 → 15) | 209 → **23** (481 → 52) |
| rare | large filtered | 12 → **11** (21 → 14) | 30 → **12** (33 → 15) | n/a |
| rare | all filtered | 137 → **15** (298 → 33) | 252 → **10** (340 → 17) | n/a |
| names | large | 197 → **14** (359 → 19) | 117 → **19** (837 → 65) | n/a |
| names | large-ar | 16 → **11** (324 → 89) | 66 → **22** (722 → 31) | n/a |
| names | small | 162 → **5.3** (253 → 6.5) | 72 → **4.9** (527 → 5.6) | n/a |
| names | person+unit+project+site | 23 → **4.9** (248 → 14) | 223 → **12** (793 → 20) | n/a |
| names | large+3 | 32 → **15** (384 → 19) | 200 → **22** (1,214 → 41) | n/a |
| names | all | 639 → **10** (1,466 → 16) | 354 → **8.4** (1,326 → 11) | n/a |
| names | large filtered | 202 → **14** (322 → 29) | 134 → **28** (914 → 36) | n/a |
| names | all filtered | 174 → **12** (1,561 → 63) | 357 → **10** (1,925 → 11) | n/a |

## Write cost

Saving 5,000 new entities, 5,000 new facts and 1,000 new episodes into the million-record graph (`SaveBulkAsync`, 500
rows per statement), then 300 entities one at a time (`SaveAsync`), then re-saving the same records unchanged; median
of three runs alternating between the versions, records per second (higher is better) and milliseconds per single
save:

| Saving | 0.1.0 | 0.2 |
| --- | --- | --- |
| New entities, bulk | 1,448 /s | 822 /s |
| New facts, bulk | 2,498 /s | 1,400 /s |
| New episodes, bulk | 796 /s | 355 /s |
| A new entity, one at a time | 2.5 ms | 4.3 ms |
| Unchanged entities, bulk re-save | 2,207 /s | 1,286 /s |
| Unchanged facts, bulk re-save | 2,656 /s | 2,294 /s |
| Unchanged episodes, bulk re-save | 947 /s | 371 /s |
| An unchanged entity, one at a time | 2.7 ms | 3.1 ms |
| Deleting the 11,300 records | 0.2 s | 2.1 s |

0.2 keeps a row count per keyword key (`search_term_counts`, written by the same statement-level triggers: one upsert
per distinct key a statement adds or removes, about 12,000 for 500 entities and 30,000 for 500 episodes) and a larger
GIN index (one entry per term, field and impact class). Bulk saves now send one multi-row statement per batch instead
of one statement per row, so the triggers run once per batch; without that, the counts made bulk saves about three
times slower. Ingestion through `AddEpisodeAsync` spends seconds in LLM calls per episode, so a couple of milliseconds
per save do not show there; bulk backfills and large deletions do take longer. A server whose `shared_buffers` holds
`search_term_counts` (330 MB here) would pay less: on this one, most of the upserts' time is reading its index pages
from the operating system's cache.

Sizes for the million records: the GIN indexes on `search_keys` grew from 54-75 MB to 76-118 MB per table (freshly
built), and `search_term_counts` holds 2.0 million rows (330 MB with its index); the whole schema went from 2.2 GB to
about 2.9 GB.

## Upgrade

`BuildIndicesAndConstraintsAsync` on the 0.1.0 database (a copy of the one above): **12 minutes** to convert the
keyword index of the million rows and count their keys (no embeddings; with them, the HNSW indexes take one insertion
per row as well). Later calls: 3 seconds (14 the first time after the write tests, folding their counts). Counting
only (rows already converted, as after recreating the tables): 36 seconds. See [Upgrading from 0.1](../postgresql.md#upgrading-from-01).

## Exactness

- **0.1.0 against 0.2 at a million records:** `dump` ran the 128 cases' 1,280 queries with both builds (0.1.0 on the
  original schema, 0.2 on the upgraded copy): the 10,431 results are identical, in the same order, for both text
  search configurations, every scope and filter.
- **Against the in-memory driver:** `verify` loaded a fifth of the corpus (200,000 records, every group under
  `simple`, since the in-memory driver does not stem) and ran the 110 cases' 1,100 queries on both drivers: 0
  differences.
- **Tests:** `PostgresBm25Tests` compares the PostgreSQL driver with the in-memory driver on mixed corpora, including
  one of common words only large enough for the pruned passes to run (it asserts they did), with groups, filters,
  repeated words and ties, and checks that the per-key counts equal a recount after concurrent writers, and that rows
  keyed by 0.1 rank exactly, before and after their conversion.

## Reproduce

```sh
# PostgreSQL 16 with pgvector on localhost (postgres/postgres), or set GRAPHITI_BENCH_POSTGRES.
dotnet build -c Release tools/GraphitiNet.Bench
bench="dotnet tools/GraphitiNet.Bench/bin/Release/net10.0/GraphitiNet.Bench.dll"

$bench load --schema kw1m --parallel 3        # about 7 minutes and 2.5 GB; resumable
$bench search --schema kw1m --n 10 --out results.json > results.md
$bench write --schema kw1m                     # saves, re-saves and deletes 11,300 records
$bench dump --schema kw1m --n 10 --out dump.txt  # every result, to diff two builds

# Against the in-memory driver (it has no stemming, so on a copy where every group uses `simple`):
$bench load --schema kw_simple --scale 0.2 --simple
$bench verify --schema kw_simple --scale 0.2 --simple --n 10
```

For the "before" column, build the same tool against 0.1.0's sources (`git archive 4a13c44 src Directory.Build.props
Directory.Packages.props global.json` next to a copy of `tools/GraphitiNet.Bench`), load with it, then copy the schema
and run `build --schema <copy>` with 0.2 to measure the upgrade. Drop the schemas when done
(`DROP SCHEMA kw1m CASCADE`); the database is `graphiti_bench`.

## Full results

*Results* is the average number of records a query returned (limit 10). The first query of a run (the first cold
`entities Rare large`) includes the process's own warm-up.

| Case | Before: warm median / p90 / p99 (ms) | After: warm median / p90 / p99 (ms) | After: cold median / p90 / p99 (ms) | Results |
| --- | --- | --- | --- | --- |
| entities Rare large | 9.0 / 9.8 / 9.8 | 9.8 / 13.5 / 17.1 | 10.7 / 104.4 / 801.2 | 10.0 |
| entities Rare large filtered | 11.9 / 15.3 / 21.4 | 11.4 / 12.9 / 14.4 | 12.5 / 13.3 / 14.9 | 8.6 |
| entities Rare large-ar | 9.5 / 12.3 / 15.1 | 11.9 / 12.9 / 13.4 | 12.4 / 14.9 / 18.5 | 10.0 |
| entities Rare small | 3.3 / 3.7 / 4.2 | 3.7 / 4.1 / 4.4 | 3.8 / 4.8 / 6.2 | 0.4 |
| entities Rare person+unit+project+site | 7.8 / 59.2 / 161.8 | 6.2 / 13.9 / 16.1 | 9.6 / 23.7 / 27.2 | 5.2 |
| entities Rare large+3 | 19.4 / 85.7 / 194.7 | 21.0 / 33.0 / 37.6 | 18.7 / 46.4 / 52.6 | 10.0 |
| entities Rare all | 158.3 / 233.3 / 300.6 | 15.9 / 21.9 / 22.9 | 17.9 / 29.1 / 33.8 | 10.0 |
| entities Rare all filtered | 137.2 / 235.0 / 297.7 | 15.4 / 20.8 / 33.0 | 18.1 / 24.1 / 43.3 | 10.0 |
| entities Mid large | 74.6 / 111.9 / 123.2 | 43.4 / 57.3 / 79.9 | 42.1 / 57.1 / 91.8 | 10.0 |
| entities Mid large filtered | 63.1 / 97.1 / 128.1 | 22.6 / 38.1 / 39.0 | 26.2 / 40.4 / 41.1 | 10.0 |
| entities Mid large-ar | 81.4 / 168.0 / 197.6 | 44.2 / 68.8 / 88.3 | 45.3 / 65.4 / 72.9 | 10.0 |
| entities Mid small | 42.8 / 66.7 / 78.9 | 5.2 / 5.6 / 5.8 | 5.6 / 8.1 / 25.8 | 9.1 |
| entities Mid person+unit+project+site | 88.4 / 128.1 / 154.9 | 13.6 / 14.3 / 14.9 | 16.0 / 18.9 / 19.5 | 10.0 |
| entities Mid large+3 | 96.8 / 142.9 / 230.6 | 25.1 / 39.3 / 40.9 | 28.6 / 46.0 / 47.4 | 10.0 |
| entities Mid all | 355.3 / 482.4 / 487.7 | 21.4 / 40.3 / 44.0 | 21.3 / 42.9 / 53.6 | 10.0 |
| entities Mid all filtered | 387.3 / 624.2 / 657.5 | 24.2 / 50.7 / 58.3 | 25.1 / 50.6 / 60.3 | 10.0 |
| entities Common large | 1080.9 / 1735.1 / 1762.1 | 32.8 / 61.5 / 63.2 | 33.6 / 66.8 / 77.8 | 10.0 |
| entities Common large filtered | 1011.7 / 1119.2 / 1511.9 | 42.7 / 60.2 / 82.3 | 40.9 / 65.0 / 70.8 | 10.0 |
| entities Common large-ar | 758.4 / 1024.6 / 1310.3 | 37.7 / 43.3 / 59.6 | 39.1 / 46.6 / 66.6 | 10.0 |
| entities Common small | 748.6 / 1152.4 / 1311.7 | 6.2 / 6.7 / 6.9 | 6.6 / 7.7 / 9.5 | 10.0 |
| entities Common person+unit+project+site | 661.9 / 1250.4 / 1299.8 | 13.9 / 16.7 / 19.1 | 16.5 / 17.9 / 19.1 | 10.0 |
| entities Common large+3 | 1158.5 / 1646.3 / 1875.8 | 24.4 / 77.0 / 209.8 | 26.4 / 75.3 / 236.0 | 10.0 |
| entities Common all | 2265.7 / 2955.7 / 3193.5 | 61.2 / 114.8 / 184.2 | 59.0 / 121.3 / 178.2 | 10.0 |
| entities Common all filtered | 2834.9 / 3289.1 / 3440.7 | 96.1 / 152.8 / 184.1 | 101.2 / 199.1 / 202.2 | 10.0 |
| entities Mixed large | 362.0 / 529.3 / 1121.1 | 23.2 / 26.4 / 29.2 | 26.6 / 32.0 / 35.4 | 10.0 |
| entities Mixed large filtered | 434.0 / 771.8 / 801.1 | 24.7 / 28.0 / 33.0 | 26.6 / 32.2 / 33.3 | 10.0 |
| entities Mixed large-ar | 338.3 / 790.6 / 800.8 | 33.8 / 38.7 / 42.8 | 34.9 / 41.2 / 44.8 | 10.0 |
| entities Mixed small | 347.3 / 444.9 / 1027.6 | 6.1 / 8.1 / 8.3 | 6.7 / 9.5 / 15.5 | 10.0 |
| entities Mixed person+unit+project+site | 574.5 / 796.7 / 835.8 | 15.5 / 16.9 / 18.9 | 16.6 / 18.4 / 18.5 | 10.0 |
| entities Mixed large+3 | 544.0 / 1222.5 / 1227.7 | 22.9 / 27.7 / 28.7 | 25.8 / 32.2 / 36.8 | 10.0 |
| entities Mixed all | 2452.1 / 2901.5 / 3590.4 | 19.9 / 34.0 / 34.2 | 25.2 / 33.4 / 44.2 | 10.0 |
| entities Mixed all filtered | 2702.0 / 3198.3 / 3335.7 | 17.2 / 24.3 / 41.4 | 19.9 / 29.0 / 47.4 | 10.0 |
| entities Names large | 197.2 / 338.1 / 358.6 | 13.5 / 18.4 / 18.6 | 13.9 / 19.8 / 24.0 | 10.0 |
| entities Names large filtered | 202.5 / 291.9 / 321.9 | 14.2 / 24.2 / 29.3 | 15.1 / 22.9 / 29.1 | 9.0 |
| entities Names large-ar | 15.9 / 285.6 / 323.7 | 11.4 / 21.5 / 89.3 | 13.0 / 26.8 / 82.8 | 10.0 |
| entities Names small | 162.2 / 240.8 / 253.4 | 5.3 / 5.6 / 6.5 | 5.8 / 8.5 / 8.9 | 8.1 |
| entities Names person+unit+project+site | 22.6 / 231.4 / 247.7 | 4.9 / 12.5 / 14.5 | 5.2 / 12.6 / 15.8 | 9.5 |
| entities Names large+3 | 32.3 / 360.8 / 383.8 | 14.9 / 16.5 / 19.1 | 16.5 / 17.7 / 20.3 | 10.0 |
| entities Names all | 639.1 / 795.1 / 1466.1 | 10.1 / 13.6 / 15.5 | 11.0 / 18.0 / 26.9 | 10.0 |
| entities Names all filtered | 173.8 / 1520.0 / 1561.1 | 11.6 / 59.0 / 62.8 | 11.8 / 58.2 / 71.2 | 10.0 |
| facts Rare large | 34.2 / 37.2 / 42.6 | 11.9 / 13.9 / 21.9 | 11.9 / 17.4 / 22.1 | 9.9 |
| facts Rare large filtered | 29.5 / 31.8 / 32.7 | 11.5 / 12.0 / 15.1 | 12.7 / 13.5 / 17.2 | 9.4 |
| facts Rare large-ar | 78.6 / 177.0 / 183.0 | 16.3 / 23.0 / 25.4 | 21.6 / 31.3 / 38.9 | 10.0 |
| facts Rare small | 16.0 / 16.7 / 16.7 | 3.0 / 3.0 / 3.5 | 3.1 / 3.2 / 4.1 | 0.2 |
| facts Rare person+unit+project+site | 38.0 / 63.8 / 68.9 | 5.7 / 7.3 / 9.5 | 4.4 / 7.5 / 8.7 | 4.2 |
| facts Rare large+3 | 69.3 / 126.8 / 151.1 | 19.0 / 23.3 / 32.7 | 20.2 / 25.2 / 56.4 | 10.0 |
| facts Rare all | 273.0 / 299.8 / 382.5 | 8.9 / 15.0 / 15.3 | 13.5 / 14.6 / 27.4 | 10.0 |
| facts Rare all filtered | 252.4 / 324.5 / 340.4 | 10.5 / 17.2 / 17.3 | 13.6 / 25.8 / 25.9 | 10.0 |
| facts Mid large | 69.7 / 113.6 / 131.1 | 17.5 / 21.0 / 21.6 | 23.0 / 25.0 / 28.3 | 10.0 |
| facts Mid large filtered | 75.0 / 138.0 / 198.5 | 24.1 / 31.2 / 50.4 | 22.1 / 38.0 / 42.9 | 10.0 |
| facts Mid large-ar | 131.2 / 180.0 / 187.2 | 45.5 / 62.6 / 65.6 | 43.8 / 63.8 / 69.5 | 10.0 |
| facts Mid small | 38.5 / 65.1 / 65.8 | 4.8 / 5.8 / 5.9 | 5.6 / 7.7 / 8.2 | 7.6 |
| facts Mid person+unit+project+site | 124.4 / 194.9 / 661.2 | 12.1 / 15.0 / 19.9 | 12.4 / 18.8 / 20.5 | 10.0 |
| facts Mid large+3 | 177.2 / 222.0 / 244.4 | 27.9 / 33.9 / 35.7 | 26.6 / 36.6 / 40.0 | 10.0 |
| facts Mid all | 364.7 / 531.1 / 598.1 | 14.3 / 19.7 / 20.1 | 18.4 / 25.2 / 26.5 | 10.0 |
| facts Mid all filtered | 349.5 / 472.9 / 613.6 | 16.0 / 27.6 / 29.9 | 18.8 / 23.0 / 28.8 | 10.0 |
| facts Common large | 738.2 / 1122.8 / 1217.8 | 22.2 / 49.0 / 50.5 | 31.1 / 54.7 / 60.1 | 10.0 |
| facts Common large filtered | 792.2 / 1006.6 / 1339.0 | 29.3 / 42.3 / 65.2 | 32.1 / 41.3 / 49.7 | 10.0 |
| facts Common large-ar | 868.6 / 1136.0 / 1456.9 | 30.4 / 58.1 / 58.2 | 38.7 / 56.1 / 60.2 | 10.0 |
| facts Common small | 530.8 / 619.6 / 977.3 | 4.9 / 5.7 / 6.9 | 5.3 / 6.2 / 10.9 | 10.0 |
| facts Common person+unit+project+site | 434.3 / 709.6 / 1000.0 | 9.5 / 12.4 / 13.8 | 10.5 / 11.6 / 20.1 | 10.0 |
| facts Common large+3 | 681.5 / 1653.7 / 1710.2 | 30.2 / 103.7 / 110.3 | 28.2 / 95.0 / 125.7 | 10.0 |
| facts Common all | 1761.1 / 2292.6 / 2605.6 | 44.4 / 71.9 / 128.1 | 32.9 / 73.1 / 119.1 | 10.0 |
| facts Common all filtered | 1551.0 / 2064.0 / 2781.9 | 14.2 / 84.2 / 98.4 | 15.9 / 98.5 / 108.7 | 10.0 |
| facts Mixed large | 620.8 / 696.3 / 880.8 | 23.4 / 27.1 / 38.0 | 23.5 / 34.6 / 46.0 | 10.0 |
| facts Mixed large filtered | 677.3 / 848.9 / 900.2 | 22.6 / 25.4 / 32.2 | 25.4 / 32.0 / 55.8 | 10.0 |
| facts Mixed large-ar | 418.9 / 525.8 / 1010.0 | 30.6 / 37.4 / 48.2 | 33.9 / 38.4 / 41.4 | 10.0 |
| facts Mixed small | 342.3 / 412.5 / 708.1 | 5.2 / 5.5 / 5.6 | 5.2 / 6.4 / 9.7 | 10.0 |
| facts Mixed person+unit+project+site | 540.3 / 738.1 / 1295.5 | 11.9 / 12.9 / 13.3 | 12.1 / 20.7 / 21.8 | 10.0 |
| facts Mixed large+3 | 912.5 / 1445.0 / 1612.0 | 21.2 / 29.2 / 41.4 | 25.2 / 29.1 / 29.4 | 10.0 |
| facts Mixed all | 1186.0 / 2021.8 / 2659.3 | 20.8 / 36.9 / 41.6 | 21.2 / 41.6 / 42.8 | 10.0 |
| facts Mixed all filtered | 1343.7 / 2543.8 / 2682.7 | 20.9 / 25.6 / 54.1 | 23.0 / 35.9 / 36.4 | 10.0 |
| facts Names large | 116.7 / 819.2 / 836.6 | 18.7 / 36.2 / 64.7 | 19.4 / 34.3 / 35.6 | 10.0 |
| facts Names large filtered | 133.6 / 865.0 / 914.0 | 27.9 / 33.5 / 36.5 | 20.5 / 37.0 / 39.1 | 10.0 |
| facts Names large-ar | 65.8 / 711.9 / 722.0 | 22.3 / 28.2 / 31.4 | 23.5 / 30.6 / 32.8 | 10.0 |
| facts Names small | 72.2 / 500.5 / 527.0 | 4.9 / 5.2 / 5.6 | 5.8 / 7.1 / 8.8 | 9.9 |
| facts Names person+unit+project+site | 223.2 / 781.8 / 793.4 | 11.7 / 17.4 / 20.0 | 11.4 / 12.0 / 21.9 | 9.8 |
| facts Names large+3 | 199.7 / 1001.0 / 1214.1 | 22.3 / 40.5 / 40.9 | 25.9 / 44.3 / 51.7 | 10.0 |
| facts Names all | 353.5 / 682.5 / 1325.7 | 8.4 / 9.2 / 10.7 | 9.0 / 11.8 / 13.0 | 10.0 |
| facts Names all filtered | 357.1 / 1378.6 / 1925.0 | 10.0 / 10.8 / 11.0 | 10.0 / 13.6 / 13.6 | 10.0 |
| episodes Rare large | 5.7 / 6.8 / 8.5 | 6.8 / 9.0 / 9.5 | 7.7 / 9.9 / 10.9 | 10.0 |
| episodes Rare large-ar | 7.8 / 12.2 / 12.7 | 7.6 / 10.5 / 12.9 | 10.6 / 13.8 / 14.4 | 10.0 |
| episodes Rare small | 2.7 / 2.8 / 3.1 | 2.8 / 4.5 / 5.5 | 2.7 / 3.1 / 3.2 | 0.2 |
| episodes Rare person+unit+project+site | 5.7 / 62.0 / 125.6 | 4.6 / 12.4 / 14.7 | 5.0 / 14.8 / 16.0 | 5.8 |
| episodes Rare large+3 | 14.1 / 113.7 / 133.8 | 10.7 / 19.8 / 38.0 | 12.4 / 26.1 / 65.4 | 10.0 |
| episodes Rare all | 208.6 / 418.6 / 481.0 | 23.4 / 32.9 / 51.6 | 23.9 / 26.8 / 68.7 | 10.0 |
| episodes Mid large | 109.7 / 155.3 / 182.1 | 42.7 / 77.9 / 89.9 | 43.2 / 73.3 / 78.4 | 10.0 |
| episodes Mid large-ar | 115.6 / 238.8 / 290.3 | 67.5 / 89.3 / 92.1 | 72.0 / 94.9 / 117.3 | 10.0 |
| episodes Mid small | 48.9 / 69.9 / 77.7 | 5.9 / 6.4 / 6.5 | 7.0 / 10.0 / 10.6 | 10.0 |
| episodes Mid person+unit+project+site | 86.5 / 142.6 / 164.6 | 15.6 / 17.4 / 17.8 | 14.4 / 17.4 / 21.5 | 10.0 |
| episodes Mid large+3 | 132.8 / 186.0 / 285.3 | 27.8 / 41.5 / 52.0 | 30.6 / 49.2 / 53.1 | 10.0 |
| episodes Mid all | 575.4 / 2699.4 / 2720.1 | 31.7 / 36.1 / 53.5 | 34.5 / 43.6 / 52.6 | 10.0 |
| episodes Common large | 884.4 / 1173.6 / 1208.3 | 37.3 / 140.2 / 164.4 | 41.7 / 155.0 / 170.6 | 10.0 |
| episodes Common large-ar | 565.2 / 669.6 / 927.9 | 61.7 / 89.8 / 115.6 | 53.2 / 94.2 / 107.5 | 10.0 |
| episodes Common small | 361.3 / 500.4 / 521.8 | 8.0 / 11.4 / 12.2 | 8.2 / 8.9 / 10.8 | 10.0 |
| episodes Common person+unit+project+site | 474.0 / 640.7 / 706.2 | 18.5 / 19.6 / 21.6 | 17.9 / 18.6 / 20.2 | 10.0 |
| episodes Common large+3 | 959.6 / 1283.6 / 1356.5 | 38.9 / 166.8 / 177.8 | 39.3 / 179.3 / 189.7 | 10.0 |
| episodes Common all | 3018.8 / 3340.6 / 3414.7 | 49.0 / 158.0 / 229.4 | 48.6 / 138.5 / 209.8 | 10.0 |
| episodes Mixed large | 267.5 / 298.2 / 305.2 | 34.2 / 38.8 / 47.0 | 38.8 / 47.4 / 51.6 | 10.0 |
| episodes Mixed large-ar | 281.4 / 348.7 / 352.4 | 44.1 / 65.7 / 78.1 | 48.8 / 72.3 / 76.8 | 10.0 |
| episodes Mixed small | 230.0 / 258.7 / 260.8 | 7.4 / 7.8 / 8.0 | 8.1 / 9.0 / 10.3 | 10.0 |
| episodes Mixed person+unit+project+site | 302.5 / 388.2 / 476.9 | 18.5 / 20.0 / 20.7 | 19.3 / 26.4 / 30.5 | 10.0 |
| episodes Mixed large+3 | 622.2 / 1193.6 / 1249.7 | 30.0 / 43.6 / 44.6 | 31.3 / 45.7 / 46.2 | 10.0 |
| episodes Mixed all | 3095.7 / 3368.3 / 3527.5 | 28.8 / 39.5 / 42.9 | 30.6 / 37.4 / 41.3 | 10.0 |
| communities Rare large | 1.3 / 2.0 / 2.4 | 1.3 / 1.5 / 2.7 | 1.5 / 4.6 / 8.0 | 0.1 |
| communities Rare large-ar | 2.5 / 3.0 / 3.1 | 2.8 / 3.4 / 3.6 | 2.8 / 4.0 / 4.8 | 3.0 |
| communities Rare small | 0.9 / 1.2 / 1.6 | 1.4 / 1.8 / 2.4 | 1.3 / 1.6 / 2.5 | 0.0 |
| communities Rare person+unit+project+site | 2.6 / 2.8 / 4.8 | 3.2 / 3.2 / 3.5 | 3.0 / 4.1 / 6.4 | 0.0 |
| communities Rare large+3 | 2.6 / 3.1 / 4.7 | 2.9 / 3.8 / 4.0 | 2.8 / 3.2 / 3.4 | 0.2 |
| communities Rare all | 8.0 / 9.0 / 9.9 | 3.6 / 4.3 / 4.7 | 2.8 / 4.0 / 4.5 | 4.4 |
| communities Mid large | 2.4 / 2.6 / 2.7 | 2.6 / 2.9 / 3.0 | 2.9 / 3.1 / 3.2 | 5.6 |
| communities Mid large-ar | 3.4 / 4.0 / 4.2 | 3.2 / 3.5 / 3.6 | 3.4 / 4.1 / 4.7 | 9.0 |
| communities Mid small | 2.3 / 2.5 / 2.7 | 2.3 / 2.8 / 2.9 | 2.4 / 2.6 / 3.4 | 0.0 |
| communities Mid person+unit+project+site | 2.6 / 3.6 / 3.7 | 3.3 / 3.8 / 4.0 | 3.4 / 4.1 / 4.8 | 0.4 |
| communities Mid large+3 | 3.2 / 3.5 / 3.7 | 3.6 / 4.6 / 4.7 | 3.9 / 4.9 / 5.3 | 3.7 |
| communities Mid all | 9.8 / 10.8 / 12.7 | 3.9 / 4.2 / 5.5 | 4.2 / 5.0 / 5.5 | 10.0 |
| communities Common large | 1.1 / 1.3 / 1.4 | 1.5 / 1.6 / 1.6 | 1.5 / 1.6 / 1.6 | 0.0 |
| communities Common large-ar | 1.8 / 1.9 / 2.0 | 2.0 / 2.3 / 3.0 | 1.6 / 2.0 / 2.0 | 0.0 |
| communities Common small | 1.2 / 1.4 / 1.5 | 1.7 / 1.8 / 4.3 | 1.4 / 1.6 / 3.6 | 0.0 |
| communities Common person+unit+project+site | 1.7 / 1.8 / 5.0 | 1.8 / 2.1 / 2.9 | 2.1 / 2.2 / 2.7 | 0.0 |
| communities Common large+3 | 1.6 / 8.4 / 15.6 | 1.8 / 2.0 / 2.1 | 1.8 / 2.2 / 3.3 | 0.0 |
| communities Common all | 6.0 / 6.8 / 6.9 | 2.2 / 2.4 / 2.9 | 2.0 / 2.2 / 2.4 | 0.0 |
| communities Mixed large | 1.8 / 2.1 / 2.7 | 2.3 / 2.7 / 2.7 | 2.5 / 2.8 / 2.9 | 2.8 |
| communities Mixed large-ar | 2.4 / 3.0 / 3.1 | 3.1 / 3.3 / 4.6 | 3.0 / 3.4 / 4.5 | 6.4 |
| communities Mixed small | 1.9 / 2.0 / 2.0 | 2.1 / 2.4 / 2.7 | 2.3 / 2.8 / 3.4 | 0.0 |
| communities Mixed person+unit+project+site | 2.7 / 4.1 / 8.5 | 3.0 / 3.1 / 3.2 | 3.0 / 3.1 / 3.8 | 0.1 |
| communities Mixed large+3 | 2.7 / 3.1 / 3.5 | 3.1 / 3.3 / 3.4 | 3.0 / 3.6 / 4.0 | 1.1 |
| communities Mixed all | 8.9 / 10.6 / 11.0 | 3.9 / 5.1 / 7.9 | 4.0 / 5.3 / 5.4 | 9.4 |
