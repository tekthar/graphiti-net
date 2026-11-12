// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.InMemory;
using GraphitiNet.Maintenance;
using static GraphitiNet.Tests.Maintenance.EdgeTestData;

namespace GraphitiNet.Tests.Maintenance;

public class GraphDataOperationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RetrieveEpisodes_ReturnsTheLastNOldestFirst()
    {
        var driver = new InMemoryGraphDriver();
        for (var i = 0; i < 5; i++)
        {
            await driver.EpisodeNodeOps.SaveAsync(Episode($"e{i}", $"content {i}", T0.AddDays(i)));
        }

        await driver.EpisodeNodeOps.SaveAsync(Episode("other", "other group", T0, groupId: "group_2"));

        var episodes = await GraphDataOperations.RetrieveEpisodesAsync(driver, T0.AddDays(3), groupIds: ["group_1"]);

        Assert.Equal(GraphDataOperations.EpisodeWindowLen, episodes.Count);
        Assert.Equal(["e1", "e2", "e3"], episodes.Select(e => e.Uuid));
    }

    [Fact]
    public async Task ClearData_DeletesOnlyTheGivenGroups()
    {
        var driver = new InMemoryGraphDriver();
        await driver.EpisodeNodeOps.SaveAsync(Episode("a", groupId: "g1"));
        await driver.EpisodeNodeOps.SaveAsync(Episode("b", groupId: "g2"));

        await GraphDataOperations.ClearDataAsync(driver, ["g1"]);
        Assert.Equal(["b"], (await driver.EpisodeNodeOps.GetByGroupIdsAsync(["g1", "g2"])).Select(e => e.Uuid));

        await GraphDataOperations.ClearDataAsync(driver);
        Assert.Empty(await driver.EpisodeNodeOps.GetByGroupIdsAsync(["g1", "g2"]));
    }
}
