// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

// Episode retrieval and saga lookups.
public abstract partial class GraphDriverConformanceTests
{
    [SkippableFact]
    public async Task Retrieve_episodes_returns_the_latest_n_before_the_reference_time_oldest_first()
    {
        var group = NewGroup();
        var episodes = Enumerable.Range(1, 5).Select(d => Episode(group, $"day {d}", validAt: Day(d))).ToList();
        await Save([.. episodes]);

        var result = await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(4), lastN: 2, groupIds: [group]);
        Assert.Equal(Ids([episodes[2], episodes[3]]), Ids(result));
        AssertEpisode(episodes[3], result[1]);

        // The reference time is inclusive, and fewer episodes than asked for is fine.
        Assert.Equal(
            Ids(episodes.Take(4)),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(4), lastN: 10, groupIds: [group])));
        Assert.Equal(
            Ids(episodes.Skip(2)),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), groupIds: [group])));
        Assert.Empty(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(1).AddMinutes(-1), groupIds: [group]));

        // Offsets do not matter: the comparison is on the instant.
        var sameInstant = Day(4).ToOffset(TimeSpan.FromHours(5));
        Assert.Equal(
            Ids([episodes[2], episodes[3]]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(sameInstant, lastN: 2, groupIds: [group])));
    }

    [SkippableFact]
    public async Task Retrieve_episodes_filters_by_group_and_source()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var message = Episode(group, "message", validAt: Day(1), source: EpisodeType.Message);
        var text = Episode(group, "text", validAt: Day(2), source: EpisodeType.Text);
        var json = Episode(group, "json", validAt: Day(3), source: EpisodeType.Json);
        var otherText = Episode(otherGroup, "other text", validAt: Day(4), source: EpisodeType.Text);
        await Save(message, text, json, otherText);

        Assert.Equal(
            Ids([message, text, json]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), lastN: 10, groupIds: [group])));
        Assert.Equal(
            Ids([message, text, json, otherText]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), lastN: 10, groupIds: [group, otherGroup])));
        Assert.Equal(
            Ids([text, otherText]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
                Day(9), lastN: 10, groupIds: [group, otherGroup], source: EpisodeType.Text)));
        Assert.Equal(
            Ids([json]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
                Day(9), lastN: 10, groupIds: [group], source: EpisodeType.Json)));
        Assert.Empty(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
            Day(9), lastN: 10, groupIds: [group], source: EpisodeType.FactTriple));
    }

    [SkippableFact]
    public async Task Retrieve_episodes_filters_by_saga_name()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var sagaName = "chat " + UniqueWord();
        var saga = Saga(group, sagaName);
        var otherSaga = Saga(group, "other " + UniqueWord());
        var sameNameElsewhere = Saga(otherGroup, sagaName);
        await Save(saga, otherSaga, sameNameElsewhere);

        var e1 = Episode(group, "one", validAt: Day(1));
        var e2 = Episode(group, "two", validAt: Day(2));
        var e3 = Episode(group, "three", validAt: Day(3));
        var e4 = Episode(group, "four", validAt: Day(4), source: EpisodeType.Message);
        var elsewhere = Episode(otherGroup, "elsewhere", validAt: Day(2));
        await Save(e1, e2, e3, e4, elsewhere);
        await Save(
            HasEpisode(group, saga, e1),
            HasEpisode(group, saga, e3),
            HasEpisode(group, saga, e4),
            HasEpisode(group, otherSaga, e2),
            HasEpisode(otherGroup, sameNameElsewhere, elsewhere));

        Assert.Equal(
            Ids([e1, e3, e4]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), lastN: 10, groupIds: [group], saga: sagaName)));
        Assert.Equal(
            Ids([e3, e4]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), lastN: 2, groupIds: [group], saga: sagaName)));
        Assert.Equal(
            Ids([e1, e3]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(3), lastN: 10, groupIds: [group], saga: sagaName)));
        Assert.Equal(
            Ids([e4]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
                Day(9), lastN: 10, groupIds: [group], source: EpisodeType.Message, saga: sagaName)));
        Assert.Equal(
            Ids([elsewhere]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
                Day(9), lastN: 10, groupIds: [otherGroup], saga: sagaName)));

        // As upstream, the saga is looked up in the first group only, and without group ids nothing matches.
        Assert.Equal(
            Ids([e1, e3, e4]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
                Day(9), lastN: 10, groupIds: [group, otherGroup], saga: sagaName)));
        Assert.Equal(
            Ids([elsewhere]),
            Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
                Day(9), lastN: 10, groupIds: [otherGroup, group], saga: sagaName)));
        Assert.Empty(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), lastN: 10, saga: sagaName));
        Assert.Empty(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), lastN: 10, groupIds: [], saga: sagaName));
        Assert.Empty(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(
            Day(9), lastN: 10, groupIds: [group], saga: "no such saga " + UniqueWord()));
    }

    [SkippableFact]
    public async Task Retrieve_episodes_with_no_group_ids_searches_every_group()
    {
        if (!IsIsolated)
        {
            return;
        }

        var group = NewGroup();
        var otherGroup = NewGroup();
        var a = Episode(group, "a", validAt: Day(1));
        var b = Episode(otherGroup, "b", validAt: Day(2));
        await Save(a, b);

        Assert.Equal(Ids([a, b]), Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9))));
        Assert.Equal(Ids([a, b]), Ids(await Driver.EpisodeNodeOps.RetrieveEpisodesAsync(Day(9), groupIds: [])));
    }

    [SkippableFact]
    public async Task Get_by_saga_returns_the_saga_episodes_oldest_first()
    {
        var group = NewGroup();
        var saga = Saga(group, "saga");
        var otherSaga = Saga(group, "other");
        await Save(saga, otherSaga);

        // Ordered by created time, then valid time.
        var first = Episode(group, "first", validAt: Day(5), createdAt: Day(1));
        var secondA = Episode(group, "second a", validAt: Day(2), createdAt: Day(2));
        var secondB = Episode(group, "second b", validAt: Day(3), createdAt: Day(2));
        var third = Episode(group, "third", validAt: Day(1), createdAt: Day(3));
        var notInSaga = Episode(group, "not in saga", createdAt: Day(2));
        await Save(third, secondB, first, secondA, notInSaga);
        await Save(
            HasEpisode(group, saga, third),
            HasEpisode(group, saga, secondB),
            HasEpisode(group, saga, first),
            HasEpisode(group, saga, secondA),
            HasEpisode(group, otherSaga, notInSaga));

        var all = await Driver.EpisodeNodeOps.GetBySagaAsync(saga.Uuid);
        Assert.Equal(Ids([first, secondA, secondB, third]), Ids(all));
        AssertEpisode(first, all[0]);

        // Created strictly after.
        Assert.Equal(
            Ids([third]),
            Ids(await Driver.EpisodeNodeOps.GetBySagaAsync(saga.Uuid, createdAfter: Day(2))));
        Assert.Equal(
            Ids([secondA, secondB, third]),
            Ids(await Driver.EpisodeNodeOps.GetBySagaAsync(saga.Uuid, createdAfter: Day(1).AddMilliseconds(1))));
        Assert.Empty(await Driver.EpisodeNodeOps.GetBySagaAsync(saga.Uuid, createdAfter: Day(3)));
        Assert.Empty(await Driver.EpisodeNodeOps.GetBySagaAsync(Guid.NewGuid().ToString()));
    }

    [SkippableFact]
    public async Task Sagas_can_be_found_by_name_within_a_group()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var saga = Saga(group, "Support chat");
        var other = Saga(group, "Sales chat");
        var elsewhere = Saga(otherGroup, "Support chat");
        await Save(saga, other, elsewhere);

        var found = await Driver.SagaNodeOps.GetByNameAsync("Support chat", group);
        Assert.NotNull(found);
        AssertSaga(saga, found);
        Assert.Equal(elsewhere.Uuid, (await Driver.SagaNodeOps.GetByNameAsync("Support chat", otherGroup))?.Uuid);
        Assert.Null(await Driver.SagaNodeOps.GetByNameAsync("Sales chat", otherGroup));
        Assert.Null(await Driver.SagaNodeOps.GetByNameAsync("support chat", group));
        Assert.Null(await Driver.SagaNodeOps.GetByNameAsync("Unknown", group));
    }

    [SkippableFact]
    public async Task Previous_saga_episode_is_the_latest_other_episode()
    {
        var group = NewGroup();
        var saga = Saga(group, "saga");
        await Save(saga);
        var early = Episode(group, "early", validAt: Day(1), createdAt: Day(9));
        var lateA = Episode(group, "late a", validAt: Day(5), createdAt: Day(1));
        var lateB = Episode(group, "late b", validAt: Day(5), createdAt: Day(2));
        var current = Episode(group, "current", validAt: Day(6), createdAt: Day(3));
        var outside = Episode(group, "outside", validAt: Day(8));
        await Save(early, lateA, lateB, current, outside);
        await Save(
            HasEpisode(group, saga, early),
            HasEpisode(group, saga, lateA),
            HasEpisode(group, saga, lateB),
            HasEpisode(group, saga, current));

        // By valid time, then created time, both descending.
        Assert.Equal(lateB.Uuid, await Driver.SagaNodeOps.GetPreviousEpisodeUuidAsync(saga.Uuid, current.Uuid));
        Assert.Equal(current.Uuid, await Driver.SagaNodeOps.GetPreviousEpisodeUuidAsync(saga.Uuid, lateB.Uuid));

        var lonely = Saga(group, "lonely");
        await Save(lonely);
        var only = Episode(group, "only");
        await Save(only);
        await Save(HasEpisode(group, lonely, only));
        Assert.Null(await Driver.SagaNodeOps.GetPreviousEpisodeUuidAsync(lonely.Uuid, only.Uuid));
        Assert.Null(await Driver.SagaNodeOps.GetPreviousEpisodeUuidAsync(Guid.NewGuid().ToString(), only.Uuid));
    }
}
