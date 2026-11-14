// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

// Episode idempotency keys (tekthar/graphiti-net#9): stored, unique per group, looked up and claimed atomically.
public abstract partial class GraphDriverConformanceTests
{
    private static EpisodicNode KeyedEpisode(string groupId, string name, string? key) => new()
    {
        Name = name,
        GroupId = groupId,
        Content = name,
        ValidAt = Day(1),
        Source = EpisodeType.Text,
        SourceDescription = "test",
        CreatedAt = NextCreatedAt(),
        IdempotencyKey = key,
    };

    [SkippableFact]
    public async Task Idempotency_keys_round_trip_and_are_looked_up_per_group_exactly()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var keyed = KeyedEpisode(group, "keyed", "order-42");
        var plain = KeyedEpisode(group, "plain", null);
        var elsewhere = KeyedEpisode(otherGroup, "elsewhere", "order-43");
        await Save(keyed, plain, elsewhere);

        AssertEpisode(keyed, await Driver.EpisodeNodeOps.GetByUuidAsync(keyed.Uuid));
        AssertEpisode(plain, await Driver.EpisodeNodeOps.GetByUuidAsync(plain.Uuid));
        AssertEpisode(keyed, (await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(group, "order-42"))!);

        // Keys compare exactly, and only within the group.
        Assert.Null(await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(group, "ORDER-42"));
        Assert.Null(await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(group, "order-4"));
        Assert.Null(await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(otherGroup, "order-42"));
        Assert.Null(await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(group, "order-43"));

        // Saving the episode again keeps the key; the episode can be found by it after any update.
        keyed.Content = "updated";
        await Save(keyed);
        Assert.Equal("updated", (await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(group, "order-42"))!.Content);
    }

    [SkippableFact]
    public async Task An_idempotency_key_is_unique_per_group()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var first = KeyedEpisode(group, "first", "k");
        await Save(first);

        // Another episode with the key in the same group is refused (with the backend's exception).
        await Assert.ThrowsAnyAsync<Exception>(() => Save(KeyedEpisode(group, "second", "k")));
        await Assert.ThrowsAnyAsync<Exception>(() => Driver.AddNodesAndEdgesBulkAsync(
            [KeyedEpisode(group, "third", "k")], [], [], []));
        Assert.Equal([first.Uuid], Ids(await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group])));

        // The same key in another group, and any number of episodes without a key, are fine.
        await Save(KeyedEpisode(otherGroup, "other", "k"));
        await Save(KeyedEpisode(group, "no key 1", null), KeyedEpisode(group, "no key 2", null));
        Assert.Equal(3, (await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group])).Count);

        // Deleting the episode frees its key.
        await Driver.EpisodeNodeOps.DeleteAsync(first);
        var again = KeyedEpisode(group, "again", "k");
        await Save(again);
        Assert.Equal(again.Uuid, (await Driver.EpisodeNodeOps.GetByIdempotencyKeyAsync(group, "k"))!.Uuid);
    }

    [SkippableFact]
    public async Task Add_or_get_by_idempotency_key_saves_a_new_key_and_returns_the_holder_of_a_used_one()
    {
        var group = NewGroup();
        var first = KeyedEpisode(group, "first", "k");

        var saved = await Driver.EpisodeNodeOps.AddOrGetByIdempotencyKeyAsync(first);
        AssertEpisode(first, saved);
        AssertEpisode(first, await Driver.EpisodeNodeOps.GetByUuidAsync(first.Uuid));

        var second = KeyedEpisode(group, "second", "k");
        var existing = await Driver.EpisodeNodeOps.AddOrGetByIdempotencyKeyAsync(second);
        AssertEpisode(first, existing);
        Assert.Empty(await Driver.EpisodeNodeOps.GetByUuidsAsync([second.Uuid]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Driver.EpisodeNodeOps.AddOrGetByIdempotencyKeyAsync(KeyedEpisode(group, "no key", null)));
    }

    [SkippableFact]
    public async Task Concurrent_add_or_get_with_one_idempotency_key_saves_one_episode()
    {
        var group = NewGroup();
        var candidates = Enumerable.Range(0, 8).Select(i => KeyedEpisode(group, $"candidate {i}", "race")).ToList();

        var results = await Task.WhenAll(candidates.Select(candidate =>
            Task.Run(() => Driver.EpisodeNodeOps.AddOrGetByIdempotencyKeyAsync(candidate))));

        var stored = Assert.Single(await Driver.EpisodeNodeOps.GetByGroupIdsAsync([group]));
        Assert.Contains(stored.Uuid, candidates.Select(c => c.Uuid));
        Assert.All(results, result => Assert.Equal(stored.Uuid, result.Uuid));
    }
}
