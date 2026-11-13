// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Upstream v0.31.2 has no unit tests for openai_reranker_client.py; these follow its behaviour.

using System.Net;
using System.Text.Json.Nodes;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Llm;
using GraphitiNet.Tests.Llm;

namespace GraphitiNet.Tests.CrossEncoder;

public class OpenAIRerankerClientTests
{
    private static string LogprobResponse(string token, double logprob) => new JsonObject
    {
        ["id"] = "chatcmpl-test",
        ["object"] = "chat.completion",
        ["created"] = 0,
        ["model"] = "gpt-4.1-mini",
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = token },
                ["finish_reason"] = "length",
                ["logprobs"] = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["token"] = token,
                            ["logprob"] = logprob,
                            ["top_logprobs"] = new JsonArray
                            {
                                new JsonObject { ["token"] = token, ["logprob"] = logprob },
                                new JsonObject { ["token"] = token == "True" ? "False" : "True", ["logprob"] = -5.0 },
                            },
                        },
                    },
                },
            },
        },
    }.ToJsonString();

    [Fact]
    public async Task Rank_ScoresByLogprobAndSorts()
    {
        // One request at a time, so the queued answers line up with the passages.
        var handler = new FakeHttpHandler()
            .Enqueue(LogprobResponse("True", Math.Log(0.9)))
            .Enqueue(LogprobResponse("False", Math.Log(0.8)))
            .Enqueue(LogprobResponse(" true", Math.Log(0.95)));
        var reranker = new OpenAIRerankerClient(new LLMConfig(), FakeOpenAI.Client(handler)) { MaxConcurrency = 1 };

        var result = await reranker.RankAsync(
            "What is the capital of France?",
            ["Paris is in France.", "Bananas are yellow.", "France's capital is Paris."]);

        Assert.Equal(["France's capital is Paris.", "Paris is in France.", "Bananas are yellow."],
            result.Select(r => r.Passage));
        Assert.Equal(0.95, result[0].Score, 6);
        Assert.Equal(0.9, result[1].Score, 6);
        Assert.Equal(0.2, result[2].Score, 6);
    }

    [Fact]
    public async Task Rank_SendsUpstreamRequest()
    {
        var handler = new FakeHttpHandler().Enqueue(LogprobResponse("True", 0));
        var reranker = new OpenAIRerankerClient(new LLMConfig(), FakeOpenAI.Client(handler));

        await reranker.RankAsync("query text", ["passage text"]);

        var body = Assert.Single(handler.Requests).Body!;
        Assert.Equal(OpenAIRerankerClient.DefaultModel, (string?)body["model"]);
        Assert.Equal(0, (int)body["temperature"]!);
        Assert.Equal(1, (int)body["max_tokens"]!);
        Assert.True((bool)body["logprobs"]!);
        Assert.Equal(2, (int)body["top_logprobs"]!);
        Assert.Equal(1, (int)body["logit_bias"]!["6432"]!);
        Assert.Equal(1, (int)body["logit_bias"]!["7983"]!);
        var messages = body["messages"]!.AsArray();
        Assert.Equal("You are an expert tasked with determining whether the passage is relevant to the query",
            (string?)messages[0]!["content"]);
        const string indent = "                           ";
        Assert.Equal(
            $"\n{indent}Respond with \"True\" if PASSAGE is relevant to QUERY and \"False\" otherwise.\n"
            + $"{indent}<PASSAGE>\n{indent}passage text\n{indent}</PASSAGE>\n"
            + $"{indent}<QUERY>\n{indent}query text\n{indent}</QUERY>\n{indent}",
            (string?)messages[1]!["content"]);
    }

    [Fact]
    public async Task Rank_UsesConfiguredModel()
    {
        var handler = new FakeHttpHandler().Enqueue(LogprobResponse("True", 0));
        var reranker = new OpenAIRerankerClient(new LLMConfig(model: "gpt-4o-mini"), FakeOpenAI.Client(handler));

        await reranker.RankAsync("q", ["p"]);

        Assert.Equal("gpt-4o-mini", (string?)handler.Requests[0].Body!["model"]);
    }

    [Fact]
    public async Task Rank_SharesTheLlmClientsSdkClient()
    {
        var handler = new FakeHttpHandler().Enqueue(LogprobResponse("True", 0));
        var llm = new OpenAIClient(client: FakeOpenAI.Client(handler));
        var reranker = new OpenAIRerankerClient(null, llm);

        await reranker.RankAsync("q", ["p"]);

        Assert.Same(llm.Client, reranker.Client);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rank_RateLimitIsTranslated()
    {
        var handler = new FakeHttpHandler().Enqueue(FakeOpenAI.Error("slow down"), HttpStatusCode.TooManyRequests);
        var reranker = new OpenAIRerankerClient(new LLMConfig(), FakeOpenAI.Client(handler));

        await Assert.ThrowsAsync<RateLimitException>(() => reranker.RankAsync("q", ["p"]));
    }

    [Fact]
    public async Task Rank_MissingLogprobsFails()
    {
        var handler = new FakeHttpHandler().Enqueue(FakeOpenAI.ChatCompletion("True"));
        var reranker = new OpenAIRerankerClient(new LLMConfig(), FakeOpenAI.Client(handler));

        // Upstream skips the score and then fails zip(strict=True).
        await Assert.ThrowsAsync<InvalidOperationException>(() => reranker.RankAsync("q", ["p"]));
    }

    [Fact]
    public async Task Rank_EmptyPassagesMakesNoCalls()
    {
        var handler = new FakeHttpHandler();
        var reranker = new OpenAIRerankerClient(new LLMConfig(), FakeOpenAI.Client(handler));

        Assert.Empty(await reranker.RankAsync("q", []));
        Assert.Empty(handler.Requests);
    }
}
