// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/cross_encoder/test_gemini_reranker_client.py (Graphiti v0.31.2) and changed for .NET: the
// reranker runs over a fake IChatClient instead of a mocked Gemini SDK.

using GraphitiNet.CrossEncoder;
using GraphitiNet.Llm;
using GraphitiNet.Tests.Llm;
using Microsoft.Extensions.AI;

namespace GraphitiNet.Tests.CrossEncoder;

public class ChatClientCrossEncoderTests
{
    // Answers by passage, since passages are scored concurrently.
    private static FakeChatClient ScoresByPassage(Dictionary<string, string> scores) =>
        new((messages, _) =>
        {
            var prompt = messages[^1].Text;
            var passage = scores.Keys.Single(p => prompt.Contains($"Passage: {p}\n", StringComparison.Ordinal));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, scores[passage]));
        });

    private static ChatClientCrossEncoder Create(FakeChatClient chat) => new(chat);

    [Fact]
    public async Task Rank_BasicFunctionality()
    {
        var chat = ScoresByPassage(new()
        {
            ["Paris is the capital and most populous city of France."] = "85",
            ["London is the capital city of England and the United Kingdom."] = "45",
            ["Berlin is the capital and largest city of Germany."] = "20",
        });

        var result = await Create(chat).RankAsync("What is the capital of France?",
        [
            "Paris is the capital and most populous city of France.",
            "London is the capital city of England and the United Kingdom.",
            "Berlin is the capital and largest city of Germany.",
        ]);

        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.InRange(r.Score, 0.0, 1.0));
        Assert.Equal(result.Select(r => r.Score).OrderByDescending(s => s), result.Select(r => r.Score));
        Assert.Equal(0.85, result[0].Score);
        Assert.Equal(0.45, result[1].Score);
        Assert.Equal(0.20, result[2].Score);
    }

    [Fact]
    public async Task Rank_EmptyPassages()
    {
        var chat = new FakeChatClient();
        Assert.Empty(await Create(chat).RankAsync("Test query", []));
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task Rank_SinglePassageGetsFullScoreWithoutACall()
    {
        var chat = new FakeChatClient();

        var result = await Create(chat).RankAsync("Test query", ["Single test passage"]);

        var ranked = Assert.Single(result);
        Assert.Equal("Single test passage", ranked.Passage);
        Assert.Equal(1.0, ranked.Score);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task Rank_ScoreExtractionWithRegex()
    {
        var chat = ScoresByPassage(new()
        {
            ["Passage 1"] = "Score: 90",
            ["Passage 2"] = "The relevance is 65 out of 100",
            ["Passage 3"] = "8",
        });

        var scores = (await Create(chat).RankAsync("Test query", ["Passage 1", "Passage 2", "Passage 3"]))
            .Select(r => r.Score).ToList();

        Assert.Contains(0.90, scores);
        Assert.Contains(0.65, scores);
        Assert.Contains(0.08, scores);
    }

    [Fact]
    public async Task Rank_InvalidScoreHandling()
    {
        var chat = ScoresByPassage(new()
        {
            ["Passage 1"] = "Not a number",
            ["Passage 2"] = "",
            ["Passage 3"] = "95",
        });

        var scores = (await Create(chat).RankAsync("Test query", ["Passage 1", "Passage 2", "Passage 3"]))
            .Select(r => r.Score).ToList();

        Assert.Contains(0.95, scores);
        Assert.Equal(2, scores.Count(s => s == 0.0));
    }

    [Fact]
    public async Task Rank_ScoreClamping()
    {
        var chat = ScoresByPassage(new()
        {
            ["Passage 1"] = "999",
            ["Passage 2"] = "invalid",
            ["Passage 3"] = "50",
        });

        var scores = (await Create(chat).RankAsync("Test query", ["Passage 1", "Passage 2", "Passage 3"]))
            .Select(r => r.Score).ToList();

        Assert.All(scores, s => Assert.InRange(s, 0.0, 1.0));
        Assert.Contains(1.0, scores);
        Assert.Contains(0.0, scores);
        Assert.Contains(0.5, scores);
    }

    [Theory]
    [InlineData("Rate limit exceeded")]
    [InlineData("Quota exceeded for requests")]
    [InlineData("RESOURCE_EXHAUSTED: Request limit exceeded")]
    [InlineData("HTTP 429 Too Many Requests")]
    public async Task Rank_RateLimitErrors(string message)
    {
        var chat = new FakeChatClient((_, _) => throw new InvalidOperationException(message));

        await Assert.ThrowsAsync<RateLimitException>(() => Create(chat).RankAsync("Test query", ["Passage 1", "Passage 2"]));
    }

    [Fact]
    public async Task Rank_GenericError()
    {
        var chat = new FakeChatClient((_, _) => throw new InvalidOperationException("Generic error"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(chat).RankAsync("Test query", ["Passage 1", "Passage 2"]));
        Assert.Contains("Generic error", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rank_ConcurrentRequestsUseUpstreamPromptAndOptions()
    {
        var chat = ScoresByPassage(new() { ["Passage 1"] = "50", ["Passage 2"] = "60", ["Passage 3"] = "70" });

        await Create(chat).RankAsync("Test query", ["Passage 1", "Passage 2", "Passage 3"]);

        Assert.Equal(3, chat.Calls.Count);
        foreach (var (messages, options) in chat.Calls)
        {
            Assert.Equal(ChatRole.System, messages[0].Role);
            Assert.Equal(ChatClientCrossEncoder.SystemInstruction, messages[0].Text);
            Assert.StartsWith(
                "Rate how well this passage answers or relates to the query. Use a scale from 0 to 100.\n\nQuery: Test query\n\nPassage: Passage ",
                messages[1].Text, StringComparison.Ordinal);
            Assert.EndsWith("\n\nProvide only a number between 0 and 100 (no explanation, just the number):",
                messages[1].Text, StringComparison.Ordinal);
            Assert.Equal(0f, options!.Temperature);
            Assert.Equal(3, options.MaxOutputTokens);
        }
    }

    [Fact]
    public async Task Rank_CustomOptionsAreUsed()
    {
        var chat = ScoresByPassage(new() { ["a"] = "1", ["b"] = "2" });
        var reranker = new ChatClientCrossEncoder(chat, new ChatOptions { ModelId = "gpt-5-nano" });

        await reranker.RankAsync("q", ["a", "b"]);

        Assert.All(chat.Calls, c =>
        {
            Assert.Equal("gpt-5-nano", c.Options!.ModelId);
            Assert.Null(c.Options.Temperature);
            Assert.Null(c.Options.MaxOutputTokens);
        });
    }
}
