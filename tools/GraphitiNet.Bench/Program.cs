// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

// Keyword-search benchmark for the PostgreSQL driver at about a million mostly Arabic records. Not part of CI.
// See docs/benchmarks/postgresql-keyword-search.md for the commands and the results.
//
// Usage: dotnet run -c Release --project tools/GraphitiNet.Bench -- <command> [options]
//   load     --schema S [--scale 1.0] [--simple] [--parallel 2]   generate and save the corpus (resumable)
//   search   --schema S [--scale 1.0] [--simple] [--n 25] [--only text] [--out results.json]
//   verify   --schema S [--scale 1.0] [--n 40]                     compare with the in-memory driver (--simple schemas)
//   write    --schema S [--scale 1.0] [--simple] [--rows 5000]     time saving new rows, re-saving them, deleting them
//   build    --schema S [--simple]                                 time BuildIndicesAndConstraintsAsync (an upgrade)
//   dump     --schema S [--n 25] [--out dump.txt]                  every case's results, to diff two builds
//   Connection: GRAPHITI_BENCH_POSTGRES (default Host=localhost;Username=postgres;Password=postgres;Database=graphiti_bench)

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GraphitiNet.Bench;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Models;
using GraphitiNet.Search;
using Npgsql;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: load|search|verify|write --schema S [options]");
    return 2;
}

var options = Options.Parse(args[1..]);
var connectionString = Environment.GetEnvironmentVariable("GRAPHITI_BENCH_POSTGRES")
    ?? "Host=localhost;Username=postgres;Password=postgres;Database=graphiti_bench;Maximum Pool Size=20;Timeout=60;Command Timeout=0";
await EnsureDatabaseAsync(connectionString);
var corpus = new Corpus(Scale.Of(options.Scale));
var graphOptions = new PostgresGraphOptions
{
    Schema = options.Schema,
    EmbeddingDimension = 1,
    TextSearchConfigSelector = options.Simple ? null : Corpus.TextSearchConfig,
};

switch (args[0])
{
    case "load":
        await LoadAsync();
        return 0;
    case "search":
        await SearchAsync();
        return 0;
    case "verify":
        return await VerifyAsync();
    case "write":
        await WriteAsync();
        return 0;
    case "build":
        await BuildAsync();
        return 0;
    case "dump":
        await DumpAsync();
        return 0;
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

async Task LoadAsync()
{
    await using var driver = new PostgresDriver(connectionString, graphOptions);
    var clock = Stopwatch.StartNew();
    await driver.GraphOps.BuildIndicesAndConstraintsAsync();
    Console.WriteLine(Invariant($"schema ready in {clock.Elapsed.TotalSeconds:F1}s"));
    await using var data = NpgsqlDataSource.Create(connectionString);
    await using (var command = data.CreateCommand(
        $"CREATE TABLE IF NOT EXISTS {options.Schema}.bench_progress (kind int, chunk int, rows int, ms bigint, "
        + "PRIMARY KEY (kind, chunk))"))
    {
        await command.ExecuteNonQueryAsync();
    }

    const int chunkSize = 5000;
    foreach (var kind in new[] { Kind.Entity, Kind.Fact, Kind.Episode, Kind.Community })
    {
        var done = new HashSet<int>();
        await using (var command = data.CreateCommand($"SELECT chunk FROM {options.Schema}.bench_progress WHERE kind = $1"))
        {
            command.Parameters.AddWithValue((int)kind);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                done.Add(reader.GetInt32(0));
            }
        }

        var count = corpus.Scale.Count(kind);
        var chunks = Enumerable.Range(0, (count + chunkSize - 1) / chunkSize).Where(c => !done.Contains(c)).ToList();
        var kindClock = Stopwatch.StartNew();
        var saved = 0L;
        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = options.Parallel },
            async (chunk, ct) =>
            {
                var indexes = Enumerable.Range(chunk * chunkSize, Math.Min(chunkSize, count - (chunk * chunkSize))).ToList();
                var chunkClock = Stopwatch.StartNew();
                switch (kind)
                {
                    case Kind.Entity:
                        await driver.EntityNodeOps.SaveBulkAsync([.. indexes.Select(corpus.Entity)], ct);
                        break;
                    case Kind.Fact:
                        await driver.EntityEdgeOps.SaveBulkAsync([.. indexes.Select(corpus.Fact)], ct);
                        break;
                    case Kind.Episode:
                        await driver.EpisodeNodeOps.SaveBulkAsync([.. indexes.Select(corpus.Episode)], ct);
                        break;
                    default:
                        await driver.CommunityNodeOps.SaveBulkAsync([.. indexes.Select(corpus.Community)], ct);
                        break;
                }

                var ms = chunkClock.ElapsedMilliseconds;
                await using var command = data.CreateCommand(
                    $"INSERT INTO {options.Schema}.bench_progress VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING");
                command.Parameters.AddWithValue((int)kind);
                command.Parameters.AddWithValue(chunk);
                command.Parameters.AddWithValue(indexes.Count);
                command.Parameters.AddWithValue(ms);
                await command.ExecuteNonQueryAsync(ct);
                var total = Interlocked.Add(ref saved, indexes.Count);
                Console.WriteLine(Invariant(
                    $"{kind} chunk {chunk}: {indexes.Count} rows in {ms} ms; {total}/{count} this run, {total / Math.Max(kindClock.Elapsed.TotalSeconds, 0.001):F0} rows/s"));
            });
    }

    await using (var command = data.CreateCommand(
        $"SELECT kind, sum(rows), sum(ms) FROM {options.Schema}.bench_progress GROUP BY kind ORDER BY kind"))
    {
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine(Invariant(
                $"{(Kind)reader.GetInt32(0)}: {reader.GetInt64(1)} rows, {reader.GetDecimal(2) / 1000m:F0} s of chunk time ({reader.GetInt64(1) / (double)reader.GetDecimal(2) * 1000:F0} rows/s per loader)"));
        }
    }

    await using (var command = data.CreateCommand($"VACUUM ANALYZE"))
    {
        await command.ExecuteNonQueryAsync();
    }

    Console.WriteLine(Invariant($"loaded in {clock.Elapsed.TotalMinutes:F1} min"));
}

async Task SearchAsync()
{
    await using var driver = new PostgresDriver(connectionString, graphOptions);
    var cases = Suite.Cases(corpus, options.Simple).Where(c => options.Only is null || c.Name.Contains(options.Only, StringComparison.Ordinal)).ToList();
    var results = new List<object>();
    Console.WriteLine("| Case | Cold median / p90 / p99 (ms) | Warm median / p90 / p99 (ms) | Results |");
    Console.WriteLine("| --- | --- | --- | --- |");
    foreach (var c in cases)
    {
        var queries = Suite.Queries(corpus, c.Class, options.N, seed: c.Name.Length * 31 + (int)c.Class);
        var cold = new List<double>();
        var warm = new List<double>();
        var hits = 0;
        foreach (var pass in new[] { cold, warm })
        {
            foreach (var query in queries)
            {
                var clock = Stopwatch.StartNew();
                hits += await Suite.RunAsync(driver, c, query);
                pass.Add(clock.Elapsed.TotalMilliseconds);
            }
        }

        var line = Invariant(
            $"| {c.Name} | {Stats(cold)} | {Stats(warm)} | {hits / (2.0 * queries.Count):F1} |");
        Console.WriteLine(line);
        results.Add(new { c.Name, Cold = cold, Warm = warm });
    }

    if (options.Out is not null)
    {
        await File.WriteAllTextAsync(options.Out, JsonSerializer.Serialize(results));
    }
}

async Task DumpAsync()
{
    // Every case's results, one line per query, to compare two builds (or schemas) with diff.
    await using var driver = new PostgresDriver(connectionString, graphOptions);
    await using var output = File.CreateText(options.Out ?? "dump.txt");
    foreach (var c in Suite.Cases(corpus, options.Simple).Where(c => options.Only is null || c.Name.Contains(options.Only, StringComparison.Ordinal)))
    {
        foreach (var query in Suite.Queries(corpus, c.Class, options.N, seed: c.Name.Length * 31 + (int)c.Class))
        {
            await output.WriteLineAsync($"{c.Name}\t{query}\t{string.Join(",", await Suite.UuidsAsync(driver, c, query))}");
        }
    }
}

async Task<int> VerifyAsync()
{
    if (!options.Simple)
    {
        Console.Error.WriteLine("verify needs a schema loaded with --simple (the in-memory driver has no stemming)");
        return 2;
    }

    var memory = new InMemoryGraphDriver();
    var clock = Stopwatch.StartNew();
    const int batch = 20_000;
    for (var i = 0; i < corpus.Scale.Entities; i += batch)
    {
        await memory.EntityNodeOps.SaveBulkAsync([.. Enumerable.Range(i, Math.Min(batch, corpus.Scale.Entities - i)).Select(corpus.Entity)]);
    }

    for (var i = 0; i < corpus.Scale.Facts; i += batch)
    {
        await memory.EntityEdgeOps.SaveBulkAsync([.. Enumerable.Range(i, Math.Min(batch, corpus.Scale.Facts - i)).Select(corpus.Fact)]);
    }

    for (var i = 0; i < corpus.Scale.Episodes; i += batch)
    {
        await memory.EpisodeNodeOps.SaveBulkAsync([.. Enumerable.Range(i, Math.Min(batch, corpus.Scale.Episodes - i)).Select(corpus.Episode)]);
    }

    await memory.CommunityNodeOps.SaveBulkAsync([.. Enumerable.Range(0, corpus.Scale.Communities).Select(corpus.Community)]);
    Console.WriteLine(Invariant($"in-memory graph built in {clock.Elapsed.TotalSeconds:F0}s"));

    await using var postgres = new PostgresDriver(connectionString, graphOptions);
    var failures = 0;
    var compared = 0;
    foreach (var c in Suite.Cases(corpus, simple: true).Where(c => options.Only is null || c.Name.Contains(options.Only, StringComparison.Ordinal)))
    {
        foreach (var query in Suite.Queries(corpus, c.Class, options.N, seed: c.Name.Length * 31 + (int)c.Class))
        {
            var expected = await Suite.UuidsAsync(memory, c, query);
            var actual = await Suite.UuidsAsync(postgres, c, query);
            compared++;
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                failures++;
                Console.WriteLine($"MISMATCH {c.Name} [{query}]: expected {string.Join(",", expected)} got {string.Join(",", actual)}");
            }
        }

        Console.WriteLine(Invariant($"{c.Name}: compared, {failures} mismatches so far"));
    }

    Console.WriteLine(Invariant($"{compared} queries compared, {failures} mismatches"));
    return failures == 0 ? 0 : 1;
}

async Task WriteAsync()
{
    // New rows (bulk, then one at a time, as an application ingesting episodes saves them), then re-saving them
    // unchanged (upserts), then deleting them, so the corpus is left as it was.
    await using var driver = new PostgresDriver(connectionString, graphOptions);
    var rows = options.Rows;
    var offset = corpus.Scale.Total + 1_000_000;
    var entities = Enumerable.Range(offset, rows).Select(corpus.Entity).ToList();
    var facts = Enumerable.Range(offset, rows).Select(corpus.Fact).ToList();
    var episodes = Enumerable.Range(offset, rows / 5).Select(corpus.Episode).ToList();
    var singles = Enumerable.Range(offset + rows, 300).Select(corpus.Entity).ToList();
    try
    {
        foreach (var pass in new[] { "insert", "re-save" })
        {
            var clock = Stopwatch.StartNew();
            await driver.EntityNodeOps.SaveBulkAsync(entities);
            var e = clock.Elapsed.TotalSeconds;
            clock.Restart();
            await driver.EntityEdgeOps.SaveBulkAsync(facts);
            var f = clock.Elapsed.TotalSeconds;
            clock.Restart();
            await driver.EpisodeNodeOps.SaveBulkAsync(episodes);
            var p = clock.Elapsed.TotalSeconds;
            clock.Restart();
            foreach (var entity in singles)
            {
                await driver.EntityNodeOps.SaveAsync(entity);
            }

            var s = clock.Elapsed.TotalMilliseconds / singles.Count;
            Console.WriteLine(Invariant(
                $"{pass}: entities {rows / e:F0}/s, facts {rows / f:F0}/s, episodes {episodes.Count / p:F0}/s (bulk); one entity at a time {s:F1} ms each"));
        }
    }
    finally
    {
        var clock = Stopwatch.StartNew();
        await driver.EntityEdgeOps.DeleteByUuidsAsync([.. facts.Select(x => x.Uuid)]);
        await driver.EntityNodeOps.DeleteByUuidsAsync([.. entities.Concat(singles).Select(x => x.Uuid)]);
        await driver.EpisodeNodeOps.DeleteByUuidsAsync([.. episodes.Select(x => x.Uuid)]);
        Console.WriteLine(Invariant($"deleted {facts.Count + entities.Count + singles.Count + episodes.Count} rows in {clock.Elapsed.TotalSeconds:F1}s"));
    }
}

async Task BuildAsync()
{
    await using var driver = new PostgresDriver(connectionString, graphOptions);
    var clock = Stopwatch.StartNew();
    await driver.GraphOps.BuildIndicesAndConstraintsAsync();
    Console.WriteLine(Invariant($"BuildIndicesAndConstraintsAsync took {clock.Elapsed.TotalSeconds:F1}s"));
}


static string Stats(List<double> values)
{
    var sorted = values.Order().ToArray();
    double P(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
    return Invariant($"{P(0.5):F1} / {P(0.9):F1} / {P(0.99):F1}");
}

static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

static async Task EnsureDatabaseAsync(string connectionString)
{
    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    var database = Identifier.Check(builder.Database!);
    builder.Database = "postgres";
    await using var connection = new NpgsqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", connection);
    exists.Parameters.AddWithValue(database);
    if (await exists.ExecuteScalarAsync() is null)
    {
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await create.ExecuteNonQueryAsync();
    }
}

internal sealed record Options(string Schema, double Scale, bool Simple, int Parallel, int N, string? Only, string? Out, int Rows)
{
    public static Options Parse(string[] args)
    {
        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new Options(
            Identifier.Check(Get("--schema") ?? "bench"),
            double.Parse(Get("--scale") ?? "1", CultureInfo.InvariantCulture),
            args.Contains("--simple"),
            int.Parse(Get("--parallel") ?? "2", CultureInfo.InvariantCulture),
            int.Parse(Get("--n") ?? "25", CultureInfo.InvariantCulture),
            Get("--only"),
            Get("--out"),
            int.Parse(Get("--rows") ?? "5000", CultureInfo.InvariantCulture));
    }
}

/// <summary>Command-line names that end up in SQL as identifiers: only lower-case letters, digits and underscores.</summary>
internal static partial class Identifier
{
    public static string Check(string name) =>
        Pattern().IsMatch(name)
            ? name
            : throw new ArgumentException($"'{name}' is not a plain identifier (a-z, 0-9, _; at most 63 characters)");

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
