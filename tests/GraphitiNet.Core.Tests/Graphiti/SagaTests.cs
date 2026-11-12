// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/test_saga_refetch_episode_uuids.py and tests/test_saga_summary_budget.py (Graphiti v0.31.2) and
// changed for .NET.

using System.Text;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Utils;
using static GraphitiNet.Tests.GraphitiApi.GraphitiFixture;

namespace GraphitiNet.Tests.GraphitiApi;

public class SagaTests
{
    private const string UpdatedSummary = """{"summary": "updated summary"}""";

    private static DateTimeOffset Dt(int minute) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    // Saves a saga with episodes given as (content, valid_at minute, created_at minute).
    private static async Task<SagaNode> SagaWithEpisodesAsync(
        InMemoryGraphDriver driver,
        string group,
        params (string Content, int ValidAt, int CreatedAt)[] episodes)
    {
        var saga = new SagaNode { Name = "test saga", GroupId = group, CreatedAt = Dt(0) };
        await driver.SagaNodeOps.SaveAsync(saga);
        foreach (var (content, validAt, createdAt) in episodes)
        {
            var episode = new EpisodicNode
            {
                Name = "e",
                GroupId = group,
                Source = EpisodeType.Message,
                SourceDescription = "",
                Content = content,
                ValidAt = Dt(validAt),
                CreatedAt = Dt(createdAt),
            };
            await driver.EpisodeNodeOps.SaveAsync(episode);
            await driver.HasEpisodeEdgeOps.SaveAsync(new HasEpisodeEdge
            {
                GroupId = group, SourceNodeUuid = saga.Uuid, TargetNodeUuid = episode.Uuid, CreatedAt = Dt(0),
            });
        }

        return saga;
    }

    private static (string, int, int)[] DefaultEpisodes() =>
    [
        (new string('a', 40), 1, 11),
        (new string('b', 40), 2, 12),
        (new string('c', 40), 3, 13),
    ];

    [Fact]
    public void Budget_constants_match_upstream()
    {
        Assert.Equal(20, Graphiti.MaxSagaEpisodesForSummary);
        Assert.Equal(100_000, Graphiti.SagaSummaryMaxEpisodeChars);
        Assert.Equal(8192, Graphiti.SagaSummaryMaxTokens);
        Assert.Equal(4000, TextUtils.SagaSummaryMaxChars);
    }

    [Fact]
    public void Selection_respects_the_character_budget()
    {
        var (selected, bytes, dropped) = Graphiti.SelectSagaSummaryEpisodes(
            [new(new string('a', 40), null, Dt(1)), new(new string('b', 40), null, Dt(2)), new(new string('c', 40), null, Dt(3))],
            maxEpisodeChars: 90);
        Assert.Equal([new string('a', 40), new string('b', 40)], selected.Select(e => e.Content));
        Assert.Equal([Dt(1), Dt(2)], selected.Select(e => e.CreatedAt));
        Assert.Equal(80, bytes);
        Assert.True(dropped);
    }

    [Fact]
    public void Selection_counts_characters_not_utf8_bytes()
    {
        var (selected, bytes, dropped) = Graphiti.SelectSagaSummaryEpisodes(
            [new(new string('é', 10), null, Dt(1)), new(new string('a', 10), null, Dt(2))], maxEpisodeChars: 15);
        Assert.Equal([new string('é', 10)], selected.Select(e => e.Content));
        Assert.Equal(Encoding.UTF8.GetByteCount(new string('é', 10)), bytes);
        Assert.True(dropped);
    }

    [Fact]
    public void Selection_truncates_an_oversized_first_episode()
    {
        var (selected, bytes, dropped) = Graphiti.SelectSagaSummaryEpisodes(
            [new(new string('é', 10), null, Dt(1))], maxEpisodeChars: 7);
        Assert.Equal([new string('é', 7)], selected.Select(e => e.Content));
        Assert.Equal(Encoding.UTF8.GetByteCount(new string('é', 7)), bytes);
        Assert.True(dropped);
    }

    [Fact]
    public async Task Get_or_create_saga_refetch_preserves_episode_uuids()
    {
        await using var graphiti = Create(out var driver);
        var existing = new SagaNode
        {
            Name = "my-saga", GroupId = "g1", CreatedAt = Dt(0), FirstEpisodeUuid = "ep-1", LastEpisodeUuid = "ep-2",
        };
        await driver.SagaNodeOps.SaveAsync(existing);

        var saga = await graphiti.GetOrCreateSagaAsync("my-saga", "g1", Dt(5));

        Assert.Equal(existing.Uuid, saga.Uuid);
        Assert.Equal("ep-1", saga.FirstEpisodeUuid);
        Assert.Equal("ep-2", saga.LastEpisodeUuid);
        Assert.Single(await driver.SagaNodeOps.GetByGroupIdsAsync(["g1"]));
    }

    [Fact]
    public async Task Get_or_create_saga_creates_when_absent()
    {
        await using var graphiti = Create(out var driver);

        var saga = await graphiti.GetOrCreateSagaAsync("new-saga", "g1", Dt(5));

        Assert.Equal("new-saga", saga.Name);
        Assert.Null(saga.FirstEpisodeUuid);
        Assert.Null(saga.LastEpisodeUuid);
        Assert.Equal(Dt(5), saga.CreatedAt);
        Assert.Equal(saga.Uuid, (await driver.SagaNodeOps.GetByUuidAsync(saga.Uuid)).Uuid);
    }

    [Fact]
    public async Task Cursor_advances_only_to_the_latest_selected_episode()
    {
        var llm = new ScriptedLlmClient(UpdatedSummary);
        await using var graphiti = Create(out var driver, llm);
        graphiti.SagaSummaryEpisodeCharBudget = 90;
        var group = NewGroup();
        var stored = await SagaWithEpisodesAsync(driver, group, DefaultEpisodes());

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.Equal("updated summary", saga.Summary);
        Assert.Equal(Dt(12), saga.LastSummarizedAt);
        Assert.Equal(Dt(2), saga.LastSummarizedEpisodeValidAt);
        Assert.Equal(3, saga.SummaryEpisodesFetched);
        Assert.Equal(2, saga.SummaryEpisodesSelected);
        Assert.Equal(1, saga.SummaryEpisodesSkipped);
        Assert.Equal(80, saga.SummarySelectedEpisodeBytes);
        Assert.Equal(Graphiti.SagaSummaryMaxTokens, llm.Calls.Single().MaxTokens);

        var reloaded = await driver.SagaNodeOps.GetByUuidAsync(stored.Uuid);
        Assert.Equal("updated summary", reloaded.Summary);
        Assert.Equal(Dt(12), reloaded.LastSummarizedAt);

        // The next run starts after the cursor: only the third episode is left.
        await graphiti.SummarizeSagaAsync(stored.Uuid);
        Assert.Contains(new string('c', 40), llm.Calls[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 40), llm.Calls[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watermark_advances_for_a_partial_fetch_window()
    {
        await using var graphiti = Create(out var driver, new ScriptedLlmClient(UpdatedSummary));
        graphiti.SagaSummaryEpisodeCharBudget = 5_000;
        var stored = await SagaWithEpisodesAsync(driver, NewGroup(),
            Enumerable.Range(1, 20).Select(i => (new string('e', 1_000), i, 10 + i)).ToArray());

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.Equal(20, saga.SummaryEpisodesFetched);
        Assert.Equal(5, saga.SummaryEpisodesSelected);
        Assert.Equal(15, saga.SummaryEpisodesSkipped);
        Assert.Equal(Dt(15), saga.LastSummarizedAt);
        Assert.Equal(Dt(5), saga.LastSummarizedEpisodeValidAt);
    }

    [Fact]
    public async Task Stored_summary_is_cut_to_the_maximum()
    {
        var longSummary = "Kept sentence. " + new string('x', TextUtils.SagaSummaryMaxChars + 50) + " Dropped.";
        var llm = new ScriptedLlmClient($$"""{"summary": "{{longSummary}}"}""");
        await using var graphiti = Create(out var driver, llm);
        var stored = await SagaWithEpisodesAsync(driver, NewGroup(), DefaultEpisodes());

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.True(saga.Summary.Length <= TextUtils.SagaSummaryMaxChars);
        Assert.StartsWith("Kept sentence.", saga.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Dropped.", saga.Summary, StringComparison.Ordinal);
        Assert.Equal(Graphiti.SagaSummaryMaxTokens, llm.Calls.Single().MaxTokens);
    }

    [Fact]
    public async Task Cursor_follows_created_at_not_valid_at()
    {
        await using var graphiti = Create(out var driver, new ScriptedLlmClient(UpdatedSummary));
        graphiti.SagaSummaryEpisodeCharBudget = 50;
        var stored = await SagaWithEpisodesAsync(driver, NewGroup(),
            ("regular earlier ingestion", 20, 9),
            ("backfill newer ingestion but older valid_at", 1, 10));

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.Equal("updated summary", saga.Summary);
        Assert.Equal(Dt(9), saga.LastSummarizedAt);
        Assert.Equal(Dt(20), saga.LastSummarizedEpisodeValidAt);
    }

    [Fact]
    public async Task Prompt_orders_selected_episodes_by_valid_at()
    {
        var llm = new ScriptedLlmClient(UpdatedSummary);
        await using var graphiti = Create(out var driver, llm);
        graphiti.SagaSummaryEpisodeCharBudget = 200;
        var stored = await SagaWithEpisodesAsync(driver, NewGroup(),
            ("regular earlier ingestion", 20, 9),
            ("backfill newer ingestion but older valid_at", 1, 10));

        await graphiti.SummarizeSagaAsync(stored.Uuid);

        var user = llm.Calls.Single().Messages[1].Content;
        Assert.True(user.IndexOf("backfill newer ingestion", StringComparison.Ordinal)
                    < user.IndexOf("regular earlier ingestion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_first_episode_is_truncated_and_the_cursor_advances()
    {
        await using var graphiti = Create(out var driver, new ScriptedLlmClient(UpdatedSummary));
        graphiti.SagaSummaryEpisodeCharBudget = 10;
        var stored = await SagaWithEpisodesAsync(driver, NewGroup(), DefaultEpisodes());

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.Equal(Dt(11), saga.LastSummarizedAt);
        Assert.Equal(3, saga.SummaryEpisodesFetched);
        Assert.Equal(1, saga.SummaryEpisodesSelected);
        Assert.Equal(2, saga.SummaryEpisodesSkipped);
        Assert.Equal(10, saga.SummarySelectedEpisodeBytes);
    }

    [Fact]
    public async Task Prompt_stays_under_the_context_cap()
    {
        var llm = new ScriptedLlmClient(UpdatedSummary);
        await using var graphiti = Create(out var driver, llm);
        graphiti.SagaSummaryPromptByteBudget = 12_000;
        graphiti.SagaSummaryEpisodeCharBudget = 12_000;
        var stored = await SagaWithEpisodesAsync(driver, NewGroup(),
            Enumerable.Range(1, 7).Select(i => (new string('x', 4_000), i, i)).ToArray());

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.True(saga.SummaryEpisodesSelected < 7);
        Assert.True(saga.SummaryEpisodesSkipped > 0);
        Assert.Equal(Dt(saga.SummaryEpisodesSelected), saga.LastSummarizedAt);
        // The budget is measured on the prompt before the client appends the schema and language instruction, as
        // upstream measures it.
        var prompt = PromptLibrary.Default.Build(PromptNames.SummarizeSagasSummarizeSaga, new Dictionary<string, object?>
        {
            ["saga_name"] = "test saga",
            ["existing_summary"] = "",
            ["episodes"] = Enumerable.Repeat(new string('x', 4_000), saga.SummaryEpisodesSelected).ToList(),
        }).AsMessages();
        Assert.True(prompt.Sum(m => Encoding.UTF8.GetByteCount(m.Content)) <= 12_000);
    }

    [Fact]
    public async Task Budget_uses_the_overridden_prompt()
    {
        var episodes = Enumerable.Range(1, 5).Select(i => (new string('x', 4_000), i, i)).ToArray();

        await using var plain = Create(out var plainDriver, new ScriptedLlmClient(UpdatedSummary));
        plain.SagaSummaryPromptByteBudget = 25_000;
        var plainSaga = await SagaWithEpisodesAsync(plainDriver, NewGroup(), episodes);
        var plainResult = await plain.SummarizeSagaAsync(plainSaga.Uuid);

        var library = PromptLibrary.Create(new Dictionary<string, ChatPromptBuilder>
        {
            [PromptNames.SummarizeSagasSummarizeSaga] = context => new ChatPrompt(
                new string('P', 14_000),
                string.Join("\n---\n", (IEnumerable<string>)context["episodes"]!)),
        });
        var routedLlm = new ScriptedLlmClient(UpdatedSummary);
        var routedDriver = new InMemoryGraphDriver();
        await using var routed = new Graphiti(routedDriver, routedLlm, new HashEmbedder(), new LengthCrossEncoder(),
            promptLibrary: library);
        routed.SagaSummaryPromptByteBudget = 25_000;
        var routedSaga = await SagaWithEpisodesAsync(routedDriver, NewGroup(), episodes);
        var routedResult = await routed.SummarizeSagaAsync(routedSaga.Uuid);

        Assert.True(routedResult.SummaryEpisodesSelected < plainResult.SummaryEpisodesSelected);
        Assert.StartsWith(new string('P', 100), routedLlm.Calls.Single().Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_new_episodes_skips_the_llm()
    {
        var llm = new ScriptedLlmClient(UpdatedSummary);
        await using var graphiti = Create(out var driver, llm);
        var stored = await SagaWithEpisodesAsync(driver, NewGroup());

        var saga = await graphiti.SummarizeSagaAsync(stored.Uuid);

        Assert.Empty(llm.Calls);
        Assert.Equal(0, saga.SummaryEpisodesFetched);
    }

    [Fact]
    public async Task Missing_saga_throws()
    {
        await using var graphiti = Create(out _);
        await Assert.ThrowsAsync<NodeNotFoundException>(() => graphiti.SummarizeSagaAsync("missing"));
    }
}
