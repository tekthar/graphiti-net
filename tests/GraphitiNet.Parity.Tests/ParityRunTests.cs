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

    [Theory]
    [InlineData("customer-support")]
    public Task Bulk_ingestion_matches_upstream_on_recorded_answers(string dataset) =>
        RunAsync(dataset, new InMemoryGraphDriver(), bulk: true);

    // customer-support on PostgreSQL is a known gap: PostgreSQL ranks keyword matches with ts_rank, not BM25, so in one
    // fact-resolution step a different fact makes the candidate cut-off than on Neo4j. Tracked in docs/roadmap.md.
    [Fact(Skip = "Known gap: PostgreSQL keyword ranking (ts_rank, not BM25) changes one fact-resolution candidate set.")]
    public Task Ingestion_on_postgresql_matches_upstream_on_customer_support() =>
        Ingestion_on_postgresql_matches_upstream_on_recorded_answers("customer-support");

    [SkippableTheory]
    [InlineData("customer-support")]
    public Task Bulk_ingestion_on_postgresql_matches_upstream_on_recorded_answers(string dataset) =>
        Ingestion_on_postgresql_matches_upstream_on_recorded_answers(dataset, bulk: true);

    [SkippableTheory]
    [InlineData("arabic")]
    public async Task Ingestion_on_postgresql_matches_upstream_on_recorded_answers(string dataset, bool bulk = false)
    {
        var connection = Environment.GetEnvironmentVariable("GRAPHITI_TEST_POSTGRES")
            ?? "Host=localhost;Username=postgres;Password=postgres;Database=graphiti_pg_tests";
        GraphitiNet.Driver.PostgreSql.PostgresDriver driver;
        try
        {
            driver = new GraphitiNet.Driver.PostgreSql.PostgresDriver(
                connection,
                new GraphitiNet.Driver.PostgreSql.PostgresGraphOptions
                {
                    Schema = "parity_" + dataset.Replace('-', '_') + (bulk ? "_bulk" : ""),
                });
            await driver.GraphOps.BuildIndicesAndConstraintsAsync(deleteExisting: true);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            Skip.If(true, $"PostgreSQL is unreachable: {e.Message}");
            return;
        }

        await RunAsync(dataset, driver, bulk);
    }

    [SkippableTheory]
    [InlineData("customer-support")]
    public Task Bulk_ingestion_on_sqlserver_matches_upstream_on_recorded_answers(string dataset) =>
        Ingestion_on_sqlserver_matches_upstream_on_recorded_answers(dataset, bulk: true);

    [SkippableTheory]
    [InlineData("customer-support")]
    [InlineData("arabic")]
    public async Task Ingestion_on_sqlserver_matches_upstream_on_recorded_answers(string dataset, bool bulk = false)
    {
        var connection = Environment.GetEnvironmentVariable("GRAPHITI_TEST_SQLSERVER")
            ?? "Server=localhost,1433;Database=graphiti_sqlserver_tests;User Id=sa;Password=Graphiti_dev_Passw0rd;"
            + "TrustServerCertificate=True";
        GraphitiNet.Driver.SqlServer.SqlServerDriver driver;
        try
        {
            await EnsureSqlServerDatabaseAsync(connection);
            driver = new GraphitiNet.Driver.SqlServer.SqlServerDriver(
                connection,
                new GraphitiNet.Driver.SqlServer.SqlServerGraphOptions
                {
                    Schema = "parity_" + dataset.Replace('-', '_') + (bulk ? "_bulk" : ""),
                });
            await driver.GraphOps.BuildIndicesAndConstraintsAsync(deleteExisting: true);
        }
        catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or System.Net.Sockets.SocketException
            or TimeoutException)
        {
            Skip.If(true, $"SQL Server is unreachable: {e.Message}");
            return;
        }

        await RunAsync(dataset, driver, bulk);
    }

    /// <summary>Creates the test database named in the connection string if it does not exist.</summary>
    private static async Task EnsureSqlServerDatabaseAsync(string connectionString)
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 };
        var database = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "IF DB_ID(@name) IS NULL EXEC (N'CREATE DATABASE ' + @quoted)",
            connection);
        command.Parameters.AddWithValue("@name", database);
        command.Parameters.AddWithValue("@quoted", "[" + database.Replace("]", "]]", StringComparison.Ordinal) + "]");
        await command.ExecuteNonQueryAsync();
    }

    private async Task RunAsync(string dataset, GraphitiNet.Driver.IGraphDriver driver, bool bulk = false)
    {
        var llm0 = (ReplayLLMClient?)null;
        ParityResult port;
        ReplayLLMClient llm;
        ReplayEmbedder embedder;
        try
        {
            (port, llm, embedder) = await ParityRunner.RunAsync(dataset, driver, l => llm0 = l, bulk);
        }
        catch (UnrecordedCallException)
        {
            output.WriteLine(string.Join("\n", llm0!.Log));
            output.WriteLine("embedding misses: " + string.Join(" | ", ParityRunner.LastEmbedder?.Misses ?? []));
            throw;
        }

        var upstream = ParityResult.LoadUpstream(bulk ? ParityRunner.BulkRecording(dataset) : dataset);
        var report = ParityComparison.Compare(upstream, port);
        output.WriteLine(report.ToString());

        Assert.Empty(llm.Misses);
        Assert.Empty(embedder.Misses);
        Assert.Equal(upstream.Entities.Select(e => e.Name), port.Entities.Select(e => e.Name));
        Assert.Equal(report.UpstreamFacts, report.MatchedFacts);
    }
}
