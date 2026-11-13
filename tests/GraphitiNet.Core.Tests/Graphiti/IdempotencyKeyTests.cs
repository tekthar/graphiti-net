// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;
using static GraphitiNet.Tests.GraphitiApi.GraphitiFixture;

namespace GraphitiNet.Tests.GraphitiApi;

/// <summary>Idempotency keys on <see cref="Graphiti.AddEpisodeAsync"/> and <see cref="Graphiti.AddEpisodeBulkAsync"/>
/// (tekthar/graphiti-net#9): validation and the in-memory edge cases. The cross-driver criteria are in the acceptance
/// tests (Issue9Tests).</summary>
public class IdempotencyKeyTests
{
    private const string NoEntities = """{"extracted_entities": [], "edges": [], "summaries": []}""";

    private static Task<AddEpisodeResults> AddAsync(
        Graphiti graphiti, string group, string? key, string? uuid = null, string body = "user: hello") =>
        graphiti.AddEpisodeAsync("m", body, "chat", Now, groupId: group, uuid: uuid, idempotencyKey: key);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_key_is_refused(string key)
    {
        await using var graphiti = Create(out _);
        await Assert.ThrowsAsync<ArgumentException>(() => AddAsync(graphiti, NewGroup(), key));
    }

    [Fact]
    public async Task A_key_longer_than_the_limit_is_refused()
    {
        await using var graphiti = Create(out _);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AddAsync(graphiti, NewGroup(), new string('k', Graphiti.MaxIdempotencyKeyLength + 1)));
    }

    [Fact]
    public async Task A_key_and_an_episode_uuid_together_are_refused()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();
        var episode = Episode(group, "hello", Now);
        await driver.EpisodeNodeOps.SaveAsync(episode);

        await Assert.ThrowsAsync<ArgumentException>(() => AddAsync(graphiti, group, "k", episode.Uuid));
        await Assert.ThrowsAsync<ArgumentException>(() => graphiti.AddEpisodeBulkAsync(
            [Raw("a", "k") with { Uuid = episode.Uuid }], group));
    }

    [Fact]
    public async Task A_key_repeated_within_one_batch_is_refused()
    {
        await using var graphiti = Create(out var driver);
        var group = NewGroup();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            graphiti.AddEpisodeBulkAsync([Raw("a", "k"), Raw("b", "k")], group));
        Assert.Empty(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task Keys_are_per_group()
    {
        await using var graphiti = Create(out var driver, new ScriptedLlmClient(NoEntities));
        var first = NewGroup();
        var second = NewGroup();

        var a = await AddAsync(graphiti, first, "k");
        var b = await AddAsync(graphiti, second, "k");

        Assert.NotEqual(a.Episode.Uuid, b.Episode.Uuid);
        Assert.Equal("k", (await driver.EpisodeNodeOps.GetByUuidAsync(b.Episode.Uuid)).IdempotencyKey);
    }

    [Fact]
    public async Task Without_a_key_a_retry_creates_another_episode_as_upstream()
    {
        await using var graphiti = Create(out var driver, new ScriptedLlmClient(NoEntities));
        var group = NewGroup();

        await AddAsync(graphiti, group, key: null);
        await AddAsync(graphiti, group, key: null);

        var episodes = await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]);
        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, e => Assert.Null(e.IdempotencyKey));
    }

    [Fact]
    public async Task An_episode_nothing_was_extracted_from_is_processed_again_without_a_second_episode()
    {
        var llm = new ScriptedLlmClient(NoEntities);
        await using var graphiti = Create(out var driver, llm);
        var group = NewGroup();

        var first = await AddAsync(graphiti, group, "k");
        var calls = llm.Calls.Count;
        var second = await AddAsync(graphiti, group, "k");

        Assert.False(second.Replayed);
        Assert.Equal(first.Episode.Uuid, second.Episode.Uuid);
        Assert.True(llm.Calls.Count > calls);
        Assert.Single(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task A_refused_model_call_with_a_key_is_resumed_by_retrying_with_the_key()
    {
        var allow = false;
        var gate = new DelegateModelCallGate((_, _) => ValueTask.FromResult(allow));
        await using var graphiti = Create(out var driver, new ScriptedLlmClient(NoEntities), gate: gate);
        var group = NewGroup();

        var refused = await Assert.ThrowsAsync<ModelCallRefusedException>(() => AddAsync(graphiti, group, "k"));
        var saved = Assert.Single(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Equal(saved.Uuid, refused.EpisodeUuid);
        Assert.Equal("k", saved.IdempotencyKey);

        allow = true;
        var resumed = await AddAsync(graphiti, group, "k");

        Assert.Equal(saved.Uuid, resumed.Episode.Uuid);
        Assert.Single(await driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
    }

    [Fact]
    public async Task A_resumed_episode_keeps_its_stored_content_and_metadata()
    {
        var allow = false;
        var gate = new DelegateModelCallGate((_, _) => ValueTask.FromResult(allow));
        var llm = new ScriptedLlmClient(NoEntities);
        await using var graphiti = Create(out _, llm, gate: gate);
        var group = NewGroup();
        await Assert.ThrowsAsync<ModelCallRefusedException>(() => graphiti.AddEpisodeAsync(
            "m", "user: the original", "chat", Now, groupId: group,
            episodeMetadata: new Dictionary<string, object?> { ["ticket"] = "T-1" }, idempotencyKey: "k"));

        allow = true;
        var resumed = await graphiti.AddEpisodeAsync("m", "user: a different body", "chat", Now, groupId: group,
            idempotencyKey: "k");

        Assert.Equal("user: the original", resumed.Episode.Content);
        Assert.Equal("T-1", resumed.Episode.EpisodeMetadata!["ticket"]?.ToString());
        Assert.Contains("the original", llm.Calls[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a different body", llm.Calls[^1].Text, StringComparison.Ordinal);
    }

    private static RawEpisode Raw(string body, string? key) => new()
    {
        Name = "m",
        Content = body,
        SourceDescription = "chat",
        Source = EpisodeType.Message,
        ReferenceTime = Now,
        IdempotencyKey = key,
    };
}
