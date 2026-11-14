// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql.Internal;
using GraphitiNet.Models;
using GraphitiNet.Search;
using Npgsql;
using static GraphitiNet.Driver.PostgreSql.Tests.GraphBuilders;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>
/// BM25 keyword ranking: the PostgreSQL driver returns the same records in the same order as the in-memory driver
/// (whose BM25 reproduces upstream's Lucene indexes), and keeps its collection statistics exact.
/// </summary>
public sealed class PostgresBm25Tests : IAsyncLifetime
{
    private static readonly string[] Vocabulary = MakeVocabulary(400);

    private static readonly string[] StatisticsOperations = ["insert", "update", "delete"];

    private readonly string _schema = TestDatabase.NewSchema();
    private readonly List<PostgresDriver> _drivers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var driver in _drivers)
        {
            await driver.DisposeAsync();
        }

        if (TestDatabase.SkipReason is null)
        {
            await TestDatabase.DropSchemaAsync(_schema);
        }
    }

    [PostgresFact]
    public async Task Fulltext_order_matches_the_in_memory_driver_on_a_mixed_corpus()
    {
        var postgres = await CreateAsync();
        var memory = new InMemoryGraphDriver();
        var random = new Random(11);
        string[] groups = ["g-1", "g-2", "g-3"];

        var entities = new List<EntityNode>
        {
            // Hand-written edge cases: Lucene token rules, case, repeats, ties (identical text), empty fields.
            Entity("Alice Chen", "g-1", summary: "Alice works at Acme; Alice's manager is Bob.", uuid: "e-01"),
            Entity("Bob", "g-1", summary: "Bob can't attend: the meeting moved to 2026.03, budget 1,000 USD.", uuid: "e-02"),
            Entity("ACME Corp", "g-1", summary: "", uuid: "e-03"),
            Entity("Acme", "g-2", summary: "acme acme acme ACME", uuid: "e-04"),
            Entity("Twin", "g-2", summary: "identical words here", uuid: "e-06"),
            Entity("Twin", "g-2", summary: "identical words here", uuid: "e-05"),
            Entity("u.s.a office", "g-3", summary: "ADDED_TO_WORKSPACE by admin", uuid: "e-07"),
            Entity("المهندسون", "g-3", summary: "المهندسون يعملون في الشركة", uuid: "e-08"),
            Entity("Café naïve", "g-3", summary: "Crème brûlée at the café", uuid: "e-09"),
        };
        for (var i = 0; i < 150; i++)
        {
            entities.Add(Entity(Text(random, 1 + random.Next(3)), groups[i % 3], summary: Text(random, random.Next(30)),
                uuid: $"e-gen-{i:D3}"));
        }

        var facts = new List<EntityEdge>
        {
            Fact(entities[0], entities[1], "Alice manages Bob at Acme since 2024.", name: "MANAGES"),
            Fact(entities[1], entities[0], "Bob was ADDED_TO_WORKSPACE acme by Alice.", name: "ADDED_TO_WORKSPACE"),
            Fact(entities[0], entities[2], "Alice works at ACME Corp.", name: "WORKS_AT"),
            Fact(entities[3], entities[2], "acme is acme", name: "SAME_AS"),
        };
        for (var i = 0; i < 300; i++)
        {
            var source = entities[random.Next(entities.Count)];
            var target = entities[random.Next(entities.Count)];
            facts.Add(Fact(source, target, Text(random, 3 + random.Next(15)),
                name: Vocabulary[random.Next(20)].ToUpperInvariant() + "_" + Vocabulary[random.Next(20)].ToUpperInvariant(),
                group: source.GroupId));
        }

        var episodes = new List<EpisodicNode>
        {
            Episode("Alice: can't make it, the budget is 1,000.", "g-1", sourceDescription: "chat log", uuid: "ep-1"),
            Episode("{\"name\": \"Acme\", \"plan\": \"pro\"}", "g-1", source: EpisodeType.Json, sourceDescription: "crm",
                uuid: "ep-2"),
            Episode("المهندسون يعملون في المشاريع الكبيرة", "g-3", uuid: "ep-3"),
        };
        for (var i = 0; i < 60; i++)
        {
            episodes.Add(Episode(Text(random, 20 + random.Next(80)), groups[i % 3],
                source: (EpisodeType)(i % 3), sourceDescription: Text(random, random.Next(4)), uuid: $"ep-gen-{i:D2}"));
        }

        var communities = new List<CommunityNode> { Community("Acme people", "g-1"), Community("acme", "g-2") };
        for (var i = 0; i < 30; i++)
        {
            communities.Add(Community(Text(random, 1 + random.Next(4)), groups[i % 3]));
        }

        foreach (IGraphDriver driver in new IGraphDriver[] { postgres, memory })
        {
            await driver.EntityNodeOps.SaveBulkAsync(entities);
            await driver.EntityEdgeOps.SaveBulkAsync(facts);
            await driver.EpisodeNodeOps.SaveBulkAsync(episodes);
            await driver.CommunityNodeOps.SaveBulkAsync(communities);
        }

        // Re-saving rows (upserts) and deleting some keeps the statistics in step.
        await SaveBoth(postgres, memory, d => d.EntityNodeOps.SaveAsync(Entity("Twin", "g-2", summary: "changed words", uuid: "e-06")));
        await SaveBoth(postgres, memory, d => d.EntityNodeOps.DeleteByUuidsAsync([entities[10].Uuid, entities[11].Uuid]));
        await SaveBoth(postgres, memory, d => d.EntityEdgeOps.DeleteByUuidsAsync([facts[10].Uuid]));

        var queries = new List<string>
        {
            "Alice", "alice bob acme", "acme acme", "ACME Corp", "can't", "can", "1,000", "2026.03", "u.s.a",
            "added_to_workspace", "workspace", "identical words", "المهندسون", "café", "Alice's", "budget meeting",
            "json crm", "message", "!!!", "manages works_at",
        };
        for (var i = 0; i < 25; i++)
        {
            queries.Add(Text(random, 1 + random.Next(6)));
        }

        IReadOnlyList<string>?[] groupSets = [null, ["g-1"], ["g-2", "g-3"]];
        foreach (var query in queries)
        {
            foreach (var groupIds in groupSets)
            {
                foreach (var limit in new[] { 3, 1000 })
                {
                    var because = $"query \"{query}\", groups {(groupIds is null ? "all" : string.Join(",", groupIds))}, limit {limit}";
                    Assert.True(
                        Ids(await memory.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))
                            .SequenceEqual(Ids(await postgres.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))),
                        "entities: " + because);
                    Assert.True(
                        Ids(await memory.SearchOps.EdgeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))
                            .SequenceEqual(Ids(await postgres.SearchOps.EdgeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))),
                        "facts: " + because);
                    Assert.True(
                        Ids(await memory.SearchOps.EpisodeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))
                            .SequenceEqual(Ids(await postgres.SearchOps.EpisodeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))),
                        "episodes: " + because);
                    Assert.True(
                        Ids(await memory.SearchOps.CommunityFulltextSearchAsync(query, groupIds, limit))
                            .SequenceEqual(Ids(await postgres.SearchOps.CommunityFulltextSearchAsync(query, groupIds, limit))),
                        "communities: " + because);
                }
            }
        }

        // A sanity check that the comparison is not vacuous: four repeats in a short summary win.
        Assert.Equal(
            "e-04",
            (await postgres.SearchOps.NodeFulltextSearchAsync("acme", SearchFilters.None, ["g-1", "g-2"], 2))[0].Uuid);
    }

    [PostgresFact]
    public async Task Rows_with_only_common_terms_are_skipped_without_changing_the_ranking()
    {
        // Enough rows that a query mixing rare and common terms first scores the rows with its rare terms only.
        var postgres = await CreateAsync();
        var memory = new InMemoryGraphDriver();
        var random = new Random(5);
        var people = Enumerable.Range(0, 50).Select(i => Entity($"person {i}", uuid: $"p-{i:D2}")).ToList();
        var facts = Enumerable.Range(0, 6000)
            .Select(i => Fact(people[i % 50], people[(i * 7) % 50], Text(random, 6 + random.Next(10)),
                name: i % 4 == 0 ? "KNOWS" : "LIKES"))
            .ToList();
        foreach (IGraphDriver driver in new IGraphDriver[] { postgres, memory })
        {
            await driver.EntityNodeOps.SaveBulkAsync(people);
            await driver.EntityEdgeOps.SaveBulkAsync(facts);
        }

        var knows = new SearchFilters { EdgeTypes = ["KNOWS"] };
        for (var i = 0; i < 30; i++)
        {
            // Common words (the top of the vocabulary) mixed with rare ones (the tail).
            var query = string.Join(' ',
                Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Vocabulary[random.Next(3)])
                    .Concat(Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Vocabulary[200 + random.Next(200)])));
            foreach (var filter in new[] { SearchFilters.None, knows })
            {
                foreach (var limit in new[] { 1, 10 })
                {
                    Assert.Equal(
                        Ids(await memory.SearchOps.EdgeFulltextSearchAsync(query, filter, [Group], limit)),
                        Ids(await postgres.SearchOps.EdgeFulltextSearchAsync(query, filter, [Group], limit)));
                }
            }
        }
    }

    [PostgresFact]
    public async Task Queries_of_common_words_only_rank_as_a_full_pass_would()
    {
        // Common words in most rows, in several groups: the impact-class passes read few of them, and must still
        // return exactly the in-memory driver's ranking, with groups, filters, repeated words and ties.
        var postgres = await CreateAsync();
        var memory = new InMemoryGraphDriver();
        var random = new Random(17);
        string[] groups = ["big", "big-2", "small"];
        string GroupOf(int i) => i % 10 < 6 ? groups[0] : i % 10 < 9 ? groups[1] : groups[2];
        var entities = Enumerable.Range(0, 5000)
            .Select(i => Entity(Text(random, 1 + random.Next(3)), GroupOf(i), summary: Text(random, random.Next(40)),
                labels: i % 3 == 0 ? ["Entity", "Person"] : ["Entity"], uuid: $"e-{i:D4}"))
            .ToList();
        // A few exact duplicates, so that ties are broken by uuid across the passes.
        entities.AddRange(Enumerable.Range(0, 20).Select(i =>
            Entity(entities[i].Name, entities[i].GroupId, summary: entities[i].Summary, uuid: $"e-dup-{i:D2}")));
        var facts = Enumerable.Range(0, 6000)
            .Select(i => Fact(entities[i % 5000], entities[(i * 7) % 5000], Text(random, 3 + random.Next(20)),
                name: i % 4 == 0 ? "KNOWS" : "LIKES", group: GroupOf(i), validAt: T0.AddDays(i % 100)))
            .ToList();
        foreach (IGraphDriver driver in new IGraphDriver[] { postgres, memory })
        {
            await driver.EntityNodeOps.SaveBulkAsync(entities);
            await driver.EntityEdgeOps.SaveBulkAsync(facts);
        }

        var people = new SearchFilters { NodeLabels = ["Person"] };
        var recent = new SearchFilters
        {
            ValidAt = [[new DateFilter { Date = T0.AddDays(50), ComparisonOperator = ComparisonOperator.GreaterThanEqual }]],
        };
        IReadOnlyList<string>?[] groupSets = [["big"], ["small"], ["big-2", "small"], null];
        var pruned = Interlocked.Read(ref KeywordPlan.PrunedSearches);
        for (var i = 0; i < 24; i++)
        {
            // The commonest words only, sometimes repeated, sometimes with one mid-frequency word.
            var words = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => Vocabulary[random.Next(4)]).ToList();
            if (i % 4 == 3)
            {
                words.Add(Vocabulary[10 + random.Next(20)]);
            }

            var query = string.Join(' ', words);
            foreach (var groupIds in groupSets)
            {
                var limit = i % 3 == 0 ? 1 : 10;
                var because = $"query \"{query}\", groups {(groupIds is null ? "all" : string.Join(",", groupIds))}";
                Assert.True(
                    Ids(await memory.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))
                        .SequenceEqual(Ids(await postgres.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))),
                    "entities: " + because);
                Assert.True(
                    Ids(await memory.SearchOps.NodeFulltextSearchAsync(query, people, groupIds, limit))
                        .SequenceEqual(Ids(await postgres.SearchOps.NodeFulltextSearchAsync(query, people, groupIds, limit))),
                    "people: " + because);
                Assert.True(
                    Ids(await memory.SearchOps.EdgeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))
                        .SequenceEqual(Ids(await postgres.SearchOps.EdgeFulltextSearchAsync(query, SearchFilters.None, groupIds, limit))),
                    "facts: " + because);
                Assert.True(
                    Ids(await memory.SearchOps.EdgeFulltextSearchAsync(query, recent, groupIds, limit))
                        .SequenceEqual(Ids(await postgres.SearchOps.EdgeFulltextSearchAsync(query, recent, groupIds, limit))),
                    "recent facts: " + because);
            }
        }

        // Not vacuous: some of these searches were answered without reading every row with a query term.
        Assert.True(Interlocked.Read(ref KeywordPlan.PrunedSearches) > pruned);
    }

    [PostgresFact]
    public async Task Bulk_saves_write_many_rows_per_statement_and_keep_the_last_of_a_uuid()
    {
        // Three rows per statement: the duplicate falls in the same statement as its first version, then in another.
        var postgres = await CreateAsync(new PostgresGraphOptions { BulkBatchSize = 3 });
        var memory = new InMemoryGraphDriver();
        var batch = new List<EntityNode>
        {
            Entity("Alice", summary: "first version", uuid: "d-1"),
            Entity("Bob", summary: "bob", uuid: "d-2"),
            Entity("Alice", summary: "second version", uuid: "d-1"),
            Entity("Carol", summary: "carol", uuid: "d-3"),
            Entity("Dan", summary: "dan", uuid: "d-4"),
            Entity("Alice", summary: "third version wins", uuid: "d-1"),
            Entity("Erin", summary: "erin", uuid: "d-5"),
        };
        await SaveBoth(postgres, memory, d => d.EntityNodeOps.SaveBulkAsync(batch));

        Assert.Equal("third version wins", (await postgres.EntityNodeOps.GetByUuidAsync("d-1")).Summary);
        Assert.Equal(5L, await ScalarAsync<long>(
            $"SELECT sum(docs1)::bigint FROM \"{_schema}\".search_statistics WHERE table_name = 'entities' AND $1 <> ''"));
        foreach (var query in new[] { "alice", "version", "third wins", "bob carol dan erin" })
        {
            Assert.Equal(
                Ids(await memory.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, [Group])),
                Ids(await postgres.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, [Group])));
        }
    }

    [PostgresFact]
    public async Task Rows_keyed_by_graphiti_net_0_1_are_searched_exactly_then_converted()
    {
        var postgres = await CreateAsync();
        var memory = new InMemoryGraphDriver();
        var random = new Random(23);
        var entities = Enumerable.Range(0, 5000)
            .Select(i => Entity(Text(random, 1 + random.Next(3)), summary: Text(random, random.Next(40)), uuid: $"e-{i:D4}"))
            .ToList();
        foreach (IGraphDriver driver in new IGraphDriver[] { postgres, memory })
        {
            await driver.EntityNodeOps.SaveBulkAsync(entities);
        }

        var table = $"\"{_schema}\".entities";
        var keys = await ScalarAsync<long>($"SELECT sum(cardinality(search_keys))::bigint FROM {table} WHERE $1 <> ''");
        var digest = $"SELECT md5(string_agg(k, ',' ORDER BY uuid, k)) FROM {table}, unnest(search_keys) AS k WHERE $1 <> ''";
        var before = await ScalarAsync<string>(digest);

        // Every other row back to 0.1's keys (field:term, no impact class), as a 0.1 application would write them;
        // the triggers keep the counts exact.
        await ExecuteAsync(
            $"UPDATE {table} SET search_keys = ARRAY(SELECT DISTINCT regexp_replace(k, '\\|-?[0-9]+$', '') "
            + "FROM unnest(search_keys) AS k) WHERE right(uuid, 1) IN ('0', '2', '4', '6', '8')");
        Assert.Equal(keys, await ScalarAsync<long>($"SELECT sum(cardinality(search_keys))::bigint FROM {table} WHERE $1 <> ''"));

        async Task AssertSameRankingAsync()
        {
            for (var i = 0; i < 12; i++)
            {
                var query = string.Join(' ', Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Vocabulary[random.Next(5)]));
                Assert.Equal(
                    Ids(await memory.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, [Group], 10)),
                    Ids(await postgres.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, [Group], 10)));
            }
        }

        await AssertSameRankingAsync();

        // Building converts them, computing the impact classes in SQL exactly as .NET does.
        await ExecuteAsync($"DELETE FROM \"{_schema}\".search_term_counts WHERE key = ''");
        await postgres.GraphOps.BuildIndicesAndConstraintsAsync();
        Assert.Equal(before, await ScalarAsync<string>(digest));
        await AssertSameRankingAsync();
    }

    [PostgresFact]
    public async Task Statistics_stay_exact_under_concurrent_writes()
    {
        var postgres = await CreateAsync();
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            var mine = new List<EntityNode>();
            for (var i = 0; i < 60; i++)
            {
                switch (random.Next(4))
                {
                    case 0 when mine.Count > 0:
                        var gone = mine[random.Next(mine.Count)];
                        mine.Remove(gone);
                        await postgres.EntityNodeOps.DeleteByUuidsAsync([gone.Uuid]);
                        break;
                    case 1 when mine.Count > 0:
                        var changed = mine[random.Next(mine.Count)];
                        await postgres.EntityNodeOps.SaveAsync(
                            Entity(changed.Name, changed.GroupId, summary: Text(random, random.Next(20)), uuid: changed.Uuid));
                        break;
                    default:
                        var batch = Enumerable.Range(0, 1 + random.Next(5))
                            .Select(_ => Entity(Text(random, 1 + random.Next(3)), $"w-{worker % 3}",
                                summary: Text(random, random.Next(20))))
                            .ToList();
                        mine.AddRange(batch);
                        await postgres.EntityNodeOps.SaveBulkAsync(batch);
                        break;
                }
            }
        }));
        await Task.WhenAll(tasks);
        await postgres.GraphOps.ClearDataAsync(["w-0"]);

        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT (SELECT ARRAY[sum(docs1), sum(docs2), sum(length1), sum(length2)]::bigint[] FROM \"{_schema}\".search_statistics "
            + "WHERE table_name = 'entities'), "
            + "(SELECT ARRAY[count(*) FILTER (WHERE search_lengths[1] > 0), count(*) FILTER (WHERE search_lengths[2] > 0), "
            + $"coalesce(sum(search_lengths[1]), 0), coalesce(sum(search_lengths[2]), 0)]::bigint[] FROM \"{_schema}\".entities)",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var kept = reader.GetFieldValue<long[]>(0);
        var counted = reader.GetFieldValue<long[]>(1);
        Assert.Equal(counted, kept);
        Assert.True(counted[0] > 0);
        await reader.DisposeAsync();

        // The per-key counts too: no key whose kept count differs from a recount.
        Assert.Equal(0L, await ScalarAsync<long>(
            $"SELECT count(*) FROM (SELECT key, sum(docs) AS docs FROM \"{_schema}\".search_term_counts "
            + "WHERE table_name = 'entities' AND key <> '' GROUP BY key HAVING sum(docs) <> 0) AS kept "
            + "FULL JOIN (SELECT k.key, count(*) AS docs FROM \"" + _schema + "\".entities AS r, unnest(r.search_keys) AS k(key) "
            + "GROUP BY k.key UNION ALL SELECT '#' || text_search_config::text, count(*) FROM \"" + _schema + "\".entities "
            + "WHERE search_lengths IS NOT NULL GROUP BY 1 UNION ALL SELECT '@' || group_id, count(*) FROM \"" + _schema + "\".entities "
            + "WHERE search_lengths IS NOT NULL GROUP BY 1) AS counted ON counted.key = kept.key "
            + "WHERE kept.docs IS DISTINCT FROM counted.docs AND $1 <> ''"));
    }

    [PostgresFact]
    public async Task Build_upgrades_tables_from_before_bm25_ranking()
    {
        var postgres = await CreateAsync();
        var records = new[]
        {
            Entity("Alice", summary: "Alice works at Acme", uuid: "a-1"),
            Entity("Acme", summary: "acme acme", uuid: "a-2"),
            Entity("Bob", summary: "Bob knows Alice", uuid: "a-3"),
        };

        // The schema as an earlier version left it: a generated tsvector, no term columns, no statistics.
        var entities = $"\"{_schema}\".entities";
        await ExecuteAsync(
            $"DROP TABLE \"{_schema}\".search_statistics; "
            + string.Concat(StatisticsOperations.Select(op =>
                $"DROP FUNCTION \"{_schema}\".search_statistics_{op}() CASCADE; "))
            + $"ALTER TABLE {entities} DROP COLUMN search_terms, DROP COLUMN search_keys, DROP COLUMN search_lengths, "
            + "ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (to_tsvector(text_search_config, "
            + "coalesce(name, '') || ' ' || coalesce(summary, ''))) STORED; "
            + $"CREATE INDEX ix_entities_search_vector ON {entities} USING gin (search_vector); "
            + string.Join(" ", records.Select(r =>
                $"INSERT INTO {entities} (uuid, group_id, name, labels, created_at, summary, attributes) VALUES "
                + $"('{r.Uuid}', '{r.GroupId}', '{r.Name}', ARRAY['Entity'], now(), '{r.Summary}', '{{}}');")));

        await postgres.GraphOps.BuildIndicesAndConstraintsAsync();

        var memory = new InMemoryGraphDriver();
        await memory.EntityNodeOps.SaveBulkAsync(records);
        foreach (var query in new[] { "alice", "acme", "alice acme bob" })
        {
            var expected = Ids(await memory.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, [Group]));
            Assert.NotEmpty(expected);
            // A fresh driver, as after an application upgrade.
            await using var upgraded = new PostgresDriver(
                TestDatabase.ConnectionString,
                new PostgresGraphOptions { Schema = _schema, EmbeddingDimension = 4 });
            Assert.Equal(expected, Ids(await upgraded.SearchOps.NodeFulltextSearchAsync(query, SearchFilters.None, [Group])));
        }

        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT count(*)::int FROM information_schema.columns WHERE table_schema = $1 AND column_name = 'search_vector'"));
    }

    [PostgresFact]
    public async Task Search_without_the_statistics_triggers_says_how_to_add_them()
    {
        var postgres = await CreateAsync();
        await postgres.EntityNodeOps.SaveAsync(Entity("Alice"));
        await ExecuteAsync($"DROP TRIGGER graphiti_search_statistics_insert ON \"{_schema}\".entities");

        await using var fresh = new PostgresDriver(
            TestDatabase.ConnectionString,
            new PostgresGraphOptions { Schema = _schema, EmbeddingDimension = 4 });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fresh.SearchOps.NodeFulltextSearchAsync("alice", SearchFilters.None, [Group]));
        Assert.Contains("GraphitiSchemaSql.SearchStatistics", error.Message, StringComparison.Ordinal);

        // The script a migration would run puts them back, and may run any number of times.
        var options = new PostgresGraphOptions { Schema = _schema, EmbeddingDimension = 4 };
        await ExecuteAsync(GraphitiSchemaSql.SearchStatistics(options));
        await ExecuteAsync(GraphitiSchemaSql.SearchStatistics(options));
        await using var repaired = new PostgresDriver(TestDatabase.ConnectionString, options);
        Assert.Single(await repaired.SearchOps.NodeFulltextSearchAsync("alice", SearchFilters.None, [Group]));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT sum(docs1)::bigint FROM \"{_schema}\".search_statistics WHERE table_name = 'entities' AND $1 <> ''"));
    }

    [PostgresFact]
    public async Task Language_configurations_rank_with_bm25_over_their_lexemes()
    {
        var postgres = await CreateAsync(new PostgresGraphOptions
        {
            TextSearchConfigs = new Dictionary<string, string> { ["en"] = "english" },
        });
        var once = Entity("Runner", "en", summary: "She runs every morning before work and after lunch.", uuid: "r-1");
        var twice = Entity("Runner", "en", summary: "Running daily; she ran and runs.", uuid: "r-2");
        var stopWords = Entity("The", "en", summary: "the of and the", uuid: "r-3");
        await postgres.EntityNodeOps.SaveBulkAsync([once, twice, stopWords]);

        // "running" and "runs" share the stem "run"; the shorter summary with more of them ranks first.
        Assert.Equal(["r-2", "r-1"],
            Ids(await postgres.SearchOps.NodeFulltextSearchAsync("running", SearchFilters.None, ["en"])));
        // Stop words are not indexed, so they match nothing.
        Assert.Empty(await postgres.SearchOps.NodeFulltextSearchAsync("the and", SearchFilters.None, ["en"]));
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT search_lengths[2] FROM \"{_schema}\".entities WHERE uuid = 'r-3' AND $1 <> ''"));
    }

    // ----- helpers -----

    private async Task<PostgresDriver> CreateAsync(PostgresGraphOptions? options = null)
    {
        var driver = new PostgresDriver(
            TestDatabase.ConnectionString,
            (options ?? new PostgresGraphOptions()) with { Schema = _schema, EmbeddingDimension = 4 });
        await driver.GraphOps.BuildIndicesAndConstraintsAsync();
        _drivers.Add(driver);
        return driver;
    }

    private static async Task SaveBoth(IGraphDriver a, IGraphDriver b, Func<IGraphDriver, Task> write)
    {
        await write(a);
        await write(b);
    }

    private static List<string> Ids(IEnumerable<Node> nodes) => nodes.Select(n => n.Uuid).ToList();

    private static List<string> Ids(IEnumerable<Edge> edges) => edges.Select(e => e.Uuid).ToList();

    /// <summary>Zipf-distributed words from <see cref="Vocabulary"/>: a few very common, most rare.</summary>
    private static string Text(Random random, int words) =>
        string.Join(' ', Enumerable.Range(0, words).Select(_ =>
            Vocabulary[Math.Min(Vocabulary.Length - 1, (int)Math.Floor(Math.Pow(Vocabulary.Length, random.NextDouble())) - 1)]));

    private static string[] MakeVocabulary(int size)
    {
        var random = new Random(3);
        return Enumerable.Range(0, size)
            .Select(i => new string(Enumerable.Range(0, 3 + random.Next(6)).Select(_ => (char)('a' + random.Next(26))).ToArray()) + i)
            .ToArray();
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(_schema);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
