// Copyright 2025, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from examples/quickstart/quickstart_neo4j.py (Graphiti v0.31.2) and changed for .NET.
//
// The graph lives in PostgreSQL when a connection string is set (ConnectionStrings__Graph), and in memory otherwise.
// See README.md next to this file for the settings.

using System.Globalization;
using System.Text.Json;
using GraphitiNet;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Search;

//////////////////////////////////////////////////
// CONFIGURATION
//////////////////////////////////////////////////
// Models and storage come from environment variables.
// The OpenAI clients read OPENAI_API_KEY themselves.
//////////////////////////////////////////////////

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("Set OPENAI_API_KEY first.");
    return 1;
}

var model = Setting("LLM_MODEL", "gpt-5-mini");
var llmConfig = new LLMConfig(model: model, smallModel: Setting("SMALL_LLM_MODEL", model));
var embedderConfig = new OpenAIEmbedderConfig
{
    EmbeddingModel = Setting("EMBEDDING_MODEL", OpenAIEmbedderConfig.DefaultEmbeddingModel),
    EmbeddingDim = int.Parse(Setting("EMBEDDING_DIM", "1024"), CultureInfo.InvariantCulture),
};
var rerankerConfig = new LLMConfig(model: Setting("RERANKER_MODEL", OpenAIRerankerClient.DefaultModel));

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Graph");
IGraphDriver driver = string.IsNullOrEmpty(connectionString)
    ? new InMemoryGraphDriver()
    : new PostgresDriver(connectionString, new PostgresGraphOptions
    {
        Schema = "graphiti_quickstart", // the sample clears this schema on every run
        EmbeddingDimension = embedderConfig.EmbeddingDim,
    });
Console.WriteLine(driver is InMemoryGraphDriver
    ? $"Storage: in memory. Models: {model}, {embedderConfig.EmbeddingModel}."
    : $"Storage: PostgreSQL. Models: {model}, {embedderConfig.EmbeddingModel}.");

//////////////////////////////////////////////////
// INITIALIZATION
//////////////////////////////////////////////////
// Create the tables and indexes. This is
// required before using other Graphiti
// functionality, and safe to repeat.
//////////////////////////////////////////////////

var llmClient = new OpenAIClient(llmConfig);
var graphiti = new Graphiti(
    driver,
    llmClient,
    new OpenAIEmbedder(embedderConfig),
    new OpenAIRerankerClient(rerankerConfig, llmClient));

try
{
    await graphiti.BuildIndicesAndConstraintsAsync();

    // Start from an empty graph, so every run prints the same kind of output. In PostgreSQL this clears only the
    // sample's own schema.
    await graphiti.ClearDataAsync();

    //////////////////////////////////////////////////
    // ADDING EPISODES
    //////////////////////////////////////////////////
    // Episodes are the primary units of information
    // in Graphiti. They can be text or structured JSON
    // and are automatically processed to extract entities
    // and relationships.
    //////////////////////////////////////////////////

    var episodes = new (string Content, EpisodeType Type, string Description)[]
    {
        (
            "Kamala Harris is the Attorney General of California. She was previously " +
            "the district attorney for San Francisco.",
            EpisodeType.Text,
            "podcast transcript"),
        ("As AG, Harris was in office from January 3, 2011 – January 3, 2017", EpisodeType.Text, "podcast transcript"),
        (
            JsonSerializer.Serialize(new
            {
                name = "Gavin Newsom",
                position = "Governor",
                state = "California",
                previous_role = "Lieutenant Governor",
                previous_location = "San Francisco",
            }),
            EpisodeType.Json,
            "podcast metadata"),
        (
            JsonSerializer.Serialize(new
            {
                name = "Gavin Newsom",
                position = "Governor",
                term_start = "January 7, 2019",
                term_end = "Present",
            }),
            EpisodeType.Json,
            "podcast metadata"),
    };

    for (var i = 0; i < episodes.Length; i++)
    {
        await graphiti.AddEpisodeAsync(
            name: $"Freakonomics Radio {i}",
            episodeBody: episodes[i].Content,
            sourceDescription: episodes[i].Description,
            referenceTime: DateTimeOffset.UtcNow,
            source: episodes[i].Type);
        Console.WriteLine($"Added episode: Freakonomics Radio {i} ({episodes[i].Type.ToStorageName()})");
    }

    //////////////////////////////////////////////////
    // BASIC SEARCH
    //////////////////////////////////////////////////
    // The simplest way to retrieve relationships (edges)
    // is SearchAsync, which runs a hybrid search combining
    // semantic similarity and BM25-style keyword retrieval.
    //////////////////////////////////////////////////

    const string query = "Who was the California Attorney General?";
    Console.WriteLine($"\nSearching for: '{query}'");
    var results = await graphiti.SearchAsync(query);

    Console.WriteLine("\nSearch Results:");
    PrintFacts(results);

    //////////////////////////////////////////////////
    // CENTER NODE SEARCH
    //////////////////////////////////////////////////
    // For more contextually relevant results, use a
    // center node to rerank search results by their
    // graph distance to a specific node.
    //////////////////////////////////////////////////

    if (results.Count > 0)
    {
        var centerNodeUuid = results[0].SourceNodeUuid;
        Console.WriteLine("\nReranking search results based on graph distance:");
        Console.WriteLine($"Using center node UUID: {centerNodeUuid}");

        var reranked = await graphiti.SearchAsync(query, centerNodeUuid: centerNodeUuid);

        Console.WriteLine("\nReranked Search Results:");
        PrintFacts(reranked);
    }
    else
    {
        Console.WriteLine("No results found in the initial search to use as center node.");
    }

    //////////////////////////////////////////////////
    // NODE SEARCH USING SEARCH RECIPES
    //////////////////////////////////////////////////
    // Predefined search recipes cover common
    // scenarios. NodeHybridSearchRrf retrieves
    // nodes (entities) instead of edges (facts).
    //////////////////////////////////////////////////

    Console.WriteLine("\nPerforming node search with the standard recipe NodeHybridSearchRrf:");

    // Recipes are immutable records: copy one with a different limit.
    var nodeSearchConfig = SearchConfigRecipes.NodeHybridSearchRrf with { Limit = 5 };
    var nodeResults = await graphiti.SearchAsync("California Governor", nodeSearchConfig);

    Console.WriteLine("\nNode Search Results:");
    foreach (var node in nodeResults.Nodes)
    {
        Console.WriteLine($"Node UUID: {node.Uuid}");
        Console.WriteLine($"Node Name: {node.Name}");
        var summary = node.Summary.ReplaceLineEndings(" ");
        summary = summary.Length > 100 ? summary[..100] + "..." : summary;
        Console.WriteLine($"Content Summary: {summary}");
        Console.WriteLine($"Node Labels: {string.Join(", ", node.Labels)}");
        Console.WriteLine($"Created At: {Format(node.CreatedAt)}");
        if (node.Attributes.Count > 0)
        {
            Console.WriteLine("Attributes:");
            foreach (var (key, value) in node.Attributes)
            {
                Console.WriteLine($"  {key}: {value}");
            }
        }

        Console.WriteLine("---");
    }

    var usage = graphiti.TokenTracker.GetTotalUsage();
    Console.WriteLine($"\nTokens used: {usage.InputTokens} input, {usage.OutputTokens} output.");
}
finally
{
    //////////////////////////////////////////////////
    // CLEANUP
    //////////////////////////////////////////////////
    // Always close Graphiti when finished, to release
    // the driver's resources.
    //////////////////////////////////////////////////

    await graphiti.CloseAsync();
    Console.WriteLine("\nConnection closed");
}

return 0;

static string Setting(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

static string Format(DateTimeOffset value) => value.ToString("u", CultureInfo.InvariantCulture);

static void PrintFacts(IReadOnlyList<EntityEdge> facts)
{
    foreach (var fact in facts)
    {
        Console.WriteLine($"UUID: {fact.Uuid}");
        Console.WriteLine($"Fact: {fact.Fact}");
        if (fact.ValidAt is { } validAt)
        {
            Console.WriteLine($"Valid from: {Format(validAt)}");
        }

        if (fact.InvalidAt is { } invalidAt)
        {
            Console.WriteLine($"Valid until: {Format(invalidAt)}");
        }

        Console.WriteLine("---");
    }
}
