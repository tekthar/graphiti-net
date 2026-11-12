// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;

namespace GraphitiNet.Tests.GraphitiApi;

/// <summary>One LLM call the fake saw.</summary>
internal sealed record FakeLlmCall(
    IReadOnlyList<Message> Messages,
    int MaxTokens,
    GraphitiCallContext? Context)
{
    public string Text => string.Join("\n", Messages.Select(message => message.Content));
}

/// <summary>An LLM client that answers with a function of the call, records every call and the ambient
/// context, and never sleeps between retries.</summary>
internal sealed class ScriptedLlmClient : LLMClient
{
    private readonly Func<FakeLlmCall, string> _respond;
    private readonly Lock _lock = new();

    public ScriptedLlmClient(Func<FakeLlmCall, string> respond)
        : base(new LLMConfig(model: "fake"))
    {
        _respond = respond;
        RetryDelayAsync = (_, _) => Task.CompletedTask;
    }

    public ScriptedLlmClient(string response)
        : this(_ => response)
    {
    }

    public List<FakeLlmCall> Calls { get; } = [];

    public long InputTokensPerCall { get; init; } = 10;

    public long OutputTokensPerCall { get; init; } = 5;

    protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize, string? model,
        CancellationToken cancellationToken)
    {
        var call = new FakeLlmCall(
            messages.Select(m => new Message(m.Role, m.Content)).ToList(),
            maxTokens,
            GraphitiCallContext.Current);
        lock (_lock)
        {
            Calls.Add(call);
        }

        var json = _respond(call);
        return Task.FromResult(new LLMClientResponse(
            JsonDocument.Parse(json).RootElement.Clone(), InputTokensPerCall, OutputTokensPerCall));
    }
}

/// <summary>A deterministic embedder: a small vector derived from the text.</summary>
internal sealed class HashEmbedder : EmbedderClient
{
    public int Calls { get; private set; }

    public List<GraphitiCallContext?> Contexts { get; } = [];

    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        Calls++;
        Contexts.Add(GraphitiCallContext.Current);
        var vector = new float[8];
        foreach (var (c, i) in inputData.Select((c, i) => (c, i)))
        {
            vector[i % 8] += c % 31;
        }

        vector[0] += 1;
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return Task.FromResult(vector.Select(v => v / norm).ToArray());
    }
}

/// <summary>A reranker that scores by passage length.</summary>
internal sealed class LengthCrossEncoder : CrossEncoderClient
{
    public override Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RankedPassage>>(
            passages.Select(p => new RankedPassage(p, p.Length)).OrderByDescending(p => p.Score).ToList());
}

internal static class GraphitiFixture
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public static Graphiti Create(
        out InMemoryGraphDriver driver,
        LLMClient? llm = null,
        HashEmbedder? embedder = null,
        IGroupLock? groupLock = null,
        IModelCallGate? gate = null,
        bool storeRawEpisodeContent = true)
    {
        driver = new InMemoryGraphDriver();
        return new Graphiti(
            driver,
            llm ?? new ScriptedLlmClient("{}"),
            embedder ?? new HashEmbedder(),
            new LengthCrossEncoder(),
            storeRawEpisodeContent: storeRawEpisodeContent,
            groupLock: groupLock ?? new InProcessGroupLock(),
            modelCallGate: gate);
    }

    public static string NewGroup() => "g" + Guid.NewGuid().ToString("N")[..12];

    public static EpisodicNode Episode(string group, string content, DateTimeOffset validAt, string name = "episode") =>
        new()
        {
            Name = name,
            GroupId = group,
            Source = EpisodeType.Message,
            SourceDescription = "conversation message",
            Content = content,
            ValidAt = validAt,
            CreatedAt = validAt,
        };

    public static EntityNode Entity(string group, string name, string summary = "") =>
        new() { Name = name, GroupId = group, Labels = ["Entity", "Person"], Summary = summary, CreatedAt = Now };

    public static EntityEdge Fact(string group, EntityNode source, EntityNode target, string fact,
        params EpisodicNode[] episodes) =>
        new()
        {
            GroupId = group,
            SourceNodeUuid = source.Uuid,
            TargetNodeUuid = target.Uuid,
            Name = "RELATES_TO",
            Fact = fact,
            CreatedAt = Now,
            ValidAt = Now,
            Episodes = [.. episodes.Select(e => e.Uuid)],
        };

    public static EpisodicEdge Mention(EpisodicNode episode, EntityNode node) =>
        new() { GroupId = episode.GroupId, SourceNodeUuid = episode.Uuid, TargetNodeUuid = node.Uuid, CreatedAt = Now };

    /// <summary>Saves a hand-built graph, embedding names and facts, as upstream's tests do with
    /// <c>add_nodes_and_edges_bulk</c>.</summary>
    public static async Task SaveAsync(
        InMemoryGraphDriver driver,
        IReadOnlyList<EpisodicNode> episodes,
        IReadOnlyList<EpisodicEdge> mentions,
        IReadOnlyList<EntityNode> nodes,
        IReadOnlyList<EntityEdge> edges)
    {
        var embedder = new HashEmbedder();
        foreach (var node in nodes)
        {
            node.NameEmbedding ??= await embedder.CreateAsync(node.Name);
        }

        foreach (var edge in edges)
        {
            edge.FactEmbedding ??= await embedder.CreateAsync(edge.Fact);
        }

        await driver.AddNodesAndEdgesBulkAsync(episodes, mentions, nodes, edges);
    }
}
