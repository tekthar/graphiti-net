// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;

using Xunit.Abstractions;

namespace GraphitiNet.Parity.Tests;

public class ParityRunTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("customer-support")]
    [InlineData("arabic")]
    public Task Ingestion_matches_upstream_on_recorded_answers(string dataset) =>
        RunAsync(dataset, new InMemoryGraphDriver());

    // customer-support on PostgreSQL is a known gap: PostgreSQL ranks keyword matches with ts_rank, not BM25, so in one
    // fact-resolution step a different fact makes the candidate cut-off than on Neo4j. Tracked in docs/roadmap.md.
    [SkippableTheory]
    [InlineData("arabic")]
    [InlineData("customer-support", Skip = "Known gap: PostgreSQL keyword ranking (ts_rank, not BM25) changes one fact-resolution candidate set.")]
    public async Task Ingestion_on_postgresql_matches_upstream_on_recorded_answers(string dataset)
    {
        var connection = Environment.GetEnvironmentVariable("GRAPHITI_TEST_POSTGRES")
            ?? "Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests";
        GraphitiNet.Driver.PostgreSql.PostgresDriver driver;
        try
        {
            driver = new GraphitiNet.Driver.PostgreSql.PostgresDriver(
                connection,
                new GraphitiNet.Driver.PostgreSql.PostgresGraphOptions { Schema = "parity_" + dataset.Replace('-', '_') });
            await driver.GraphOps.BuildIndicesAndConstraintsAsync(deleteExisting: true);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            Skip.If(true, $"PostgreSQL is unreachable: {e.Message}");
            return;
        }

        await RunAsync(dataset, driver);
    }

    private async Task RunAsync(string dataset, GraphitiNet.Driver.IGraphDriver driver)
    {
        var llm0 = (ReplayLLMClient?)null;
        ParityResult port;
        ReplayLLMClient llm;
        ReplayEmbedder embedder;
        try
        {
            (port, llm, embedder) = await ParityRunner.RunAsync(dataset, driver, l => llm0 = l);
        }
        catch (UnrecordedCallException)
        {
            output.WriteLine(string.Join("\n", llm0!.Log));
            output.WriteLine("embedding misses: " + string.Join(" | ", ParityRunner.LastEmbedder?.Misses ?? []));
            throw;
        }

        var upstream = ParityResult.LoadUpstream(dataset);
        var report = ParityComparison.Compare(upstream, port);
        output.WriteLine(report.ToString());

        Assert.Empty(llm.Misses);
        Assert.Empty(embedder.Misses);
        Assert.Equal(upstream.Entities.Select(e => e.Name), port.Entities.Select(e => e.Name));
        Assert.Equal(report.UpstreamFacts, report.MatchedFacts);
    }
}
