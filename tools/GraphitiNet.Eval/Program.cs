// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

// Builds graphs from LongMemEval conversations with graphiti-net, the way upstream's
// tests/evals/eval_e2e_graph_building.py builds its candidate graphs, and writes every AddEpisodeResults in the JSON
// shape of upstream's AddEpisodeResults.model_dump(mode='json'), so parity/python/lme_eval.py can judge it against a
// baseline built by Python Graphiti.
//
// Usage: dotnet run --project tools/GraphitiNet.Eval -- <longmemeval_oracle.json> <out.json> <users> <messages>
//   (or `qa ...` for the question-answering benchmark; see Qa.cs)
//   Settings: LLM_MODEL, SMALL_LLM_MODEL, EMBEDDING_MODEL, EMBEDDING_DIM, OPENAI_API_KEY, and GRAPHITI_EVAL_POSTGRES
//   (a connection string; the in-memory driver is used when it is not set).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;

switch (args.FirstOrDefault())
{
    case "qa":
        return await Qa.RunAsync(args[1..]);
    case "qa-answer":
        return await Qa.AnswerAgainAsync(args[1..]);
}

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: <longmemeval_oracle.json> <out.json> <users> <messages>");
    return 2;
}

var users = int.Parse(args[2], CultureInfo.InvariantCulture);
var sessionLength = int.Parse(args[3], CultureInfo.InvariantCulture);
using var data = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));

var model = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gpt-5-mini";
var llmConfig = new LLMConfig(model: model, smallModel: Environment.GetEnvironmentVariable("SMALL_LLM_MODEL") ?? model);
var embedderConfig = new OpenAIEmbedderConfig
{
    EmbeddingModel = Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-3-small",
    EmbeddingDim = int.Parse(Environment.GetEnvironmentVariable("EMBEDDING_DIM") ?? "1024", CultureInfo.InvariantCulture),
};

IGraphDriver driver = Environment.GetEnvironmentVariable("GRAPHITI_EVAL_POSTGRES") is { Length: > 0 } connection
    ? new PostgresDriver(connection, new PostgresGraphOptions { Schema = "lme_eval", EmbeddingDimension = embedderConfig.EmbeddingDim })
    : new InMemoryGraphDriver();
await using var graphiti = new Graphiti(
    driver,
    new OpenAIClient(llmConfig),
    new OpenAIEmbedder(embedderConfig),
    new OpenAIRerankerClient(llmConfig));
await graphiti.BuildIndicesAndConstraintsAsync(deleteExisting: true);

var started = DateTimeOffset.UtcNow;
var subgraphs = await Task.WhenAll(Enumerable.Range(0, users).Select(i => BuildSubgraphAsync(i)));
var output = new JsonObject();
foreach (var (userId, results) in subgraphs)
{
    output[userId] = results;
}

await File.WriteAllTextAsync(args[1], output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
var usage = graphiti.TokenTracker.GetTotalUsage();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{users} users x {sessionLength} messages in {(DateTimeOffset.UtcNow - started).TotalSeconds:F0}s, " +
    $"{usage.InputTokens} input + {usage.OutputTokens} output tokens -> {args[1]}"));
return 0;

async Task<(string UserId, JsonArray Results)> BuildSubgraphAsync(int index)
{
    // Same group ids and episode fields as upstream's build_subgraph.
    var userId = $"lme_oracle_experiment_user_{index}";
    var item = data.RootElement[index];
    var sessions = item.GetProperty("haystack_sessions");
    var dates = item.GetProperty("haystack_dates");
    var results = new JsonArray();
    var count = 0;
    for (var s = 0; s < sessions.GetArrayLength(); s++)
    {
        var date = DateTimeOffset.ParseExact(
            dates[s].GetString()! + " +00:00", "yyyy/MM/dd (ddd) HH:mm zzz", CultureInfo.InvariantCulture);
        foreach (var message in sessions[s].EnumerateArray())
        {
            if (count >= sessionLength)
            {
                continue;
            }

            count++;
            var body = $"{message.GetProperty("role").GetString()}: {message.GetProperty("content").GetString()}";
            var result = await graphiti.AddEpisodeAsync(
                name: "",
                episodeBody: body,
                sourceDescription: "",
                referenceTime: date,
                source: EpisodeType.Message,
                groupId: userId + "_candidate");
            results.Add(Dump(result, message.GetProperty("content").GetString()!));
        }
    }

    return (userId, results);
}

static JsonObject Dump(AddEpisodeResults r, string message) => new()
{
    ["episode"] = Episode(r.Episode),
    ["episodic_edges"] = new JsonArray([.. r.EpisodicEdges.Select(e => (JsonNode)EdgeBase(e))]),
    ["nodes"] = new JsonArray([.. r.Nodes.Select(n => (JsonNode)Entity(n))]),
    ["edges"] = new JsonArray([.. r.Edges.Select(e => (JsonNode)Fact(e))]),
    ["communities"] = new JsonArray(),
    ["community_edges"] = new JsonArray(),
    ["_message"] = message,
};

static string? Iso(DateTimeOffset? d) => d?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

static JsonObject Episode(EpisodicNode e) => new()
{
    ["uuid"] = e.Uuid,
    ["name"] = e.Name,
    ["group_id"] = e.GroupId,
    ["labels"] = new JsonArray([.. e.Labels.Select(l => (JsonNode)l)]),
    ["created_at"] = Iso(e.CreatedAt),
    ["source"] = e.Source.ToStorageName(),
    ["source_description"] = e.SourceDescription,
    ["content"] = e.Content,
    ["valid_at"] = Iso(e.ValidAt),
    ["entity_edges"] = new JsonArray([.. e.EntityEdges.Select(u => (JsonNode)u)]),
};

static JsonObject EdgeBase(Edge e) => new()
{
    ["uuid"] = e.Uuid,
    ["group_id"] = e.GroupId,
    ["source_node_uuid"] = e.SourceNodeUuid,
    ["target_node_uuid"] = e.TargetNodeUuid,
    ["created_at"] = Iso(e.CreatedAt),
};

static JsonObject Entity(EntityNode n) => new()
{
    ["uuid"] = n.Uuid,
    ["name"] = n.Name,
    ["group_id"] = n.GroupId,
    ["labels"] = new JsonArray([.. n.Labels.Select(l => (JsonNode)l)]),
    ["created_at"] = Iso(n.CreatedAt),
    ["name_embedding"] = null,
    ["summary"] = n.Summary,
    ["attributes"] = JsonSerializer.SerializeToNode(n.Attributes),
};

static JsonObject Fact(EntityEdge e)
{
    var o = EdgeBase(e);
    o["name"] = e.Name;
    o["fact"] = e.Fact;
    o["fact_embedding"] = null;
    o["episodes"] = new JsonArray([.. e.Episodes.Select(u => (JsonNode)u)]);
    o["expired_at"] = Iso(e.ExpiredAt);
    o["valid_at"] = Iso(e.ValidAt);
    o["invalid_at"] = Iso(e.InvalidAt);
    o["reference_time"] = Iso(e.ReferenceTime);
    o["attributes"] = JsonSerializer.SerializeToNode(e.Attributes);
    return o;
}
