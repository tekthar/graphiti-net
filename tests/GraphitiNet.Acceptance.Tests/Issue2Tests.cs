// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using static GraphitiNet.Acceptance.Tests.Support;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>
/// AbpAgent's suggested order of work, tekthar/graphiti-net#2. The parity criteria (customer-support and Arabic
/// datasets on recorded answers) are covered by <c>tests/GraphitiNet.Parity.Tests</c>; see docs/acceptance.md.
/// </summary>
public class Issue2Tests
{
    private static readonly ScriptedEpisode Paris = new(
        "Alice lives in Paris.",
        ["Alice", "Paris"],
        [new("Alice", "LIVES_IN", "Paris", "Alice lives in Paris.", ValidAt: "2026-01-10T09:00:00Z")]);

    private static readonly ScriptedEpisode Acme = new(
        "Bob works at Acme.",
        ["Bob", "Acme"],
        [new("Bob", "WORKS_AT", "Acme", "Bob works at Acme.", ValidAt: "2026-03-01T09:00:00Z")]);

    private static readonly ScriptedEpisode London = new(
        "Alice moved to London.",
        ["Alice", "London"],
        [new("Alice", "LIVES_IN", "London", "Alice lives in London.", ValidAt: "2026-06-01T09:00:00Z",
            Contradicts: ["Alice lives in Paris."])]);

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Episode_text_to_search_result_finds_the_current_fact_and_the_closed_fact(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(Paris, Acme, London));
        var group = Harness.NewGroup();

        var first = await h.AddAsync(group, Paris.Body, Jan);
        await h.AddAsync(group, Acme.Body, Mar);
        var third = await h.AddAsync(group, London.Body, Jun);

        // The third episode closed the first one's fact.
        Assert.Contains(third.Edges, e => e.Fact == "Alice lives in Paris." && e.InvalidAt == Jun);

        var facts = await h.Graphiti.SearchAsync("Where does Alice live? Alice lives in", groupIds: [group]);

        var current = Assert.Single(facts, e => e.Fact == "Alice lives in London.");
        Assert.Equal(Jun, current.ValidAt);
        Assert.Null(current.InvalidAt);
        Assert.Null(current.ExpiredAt);
        Assert.Equal([third.Episode.Uuid], current.Episodes);

        var closed = Assert.Single(facts, e => e.Fact == "Alice lives in Paris.");
        Assert.Equal(Jan, closed.ValidAt);
        Assert.Equal(Jun, closed.InvalidAt);
        Assert.NotNull(closed.ExpiredAt);
        Assert.Equal([first.Episode.Uuid], closed.Episodes);

        // Both facts hang off the one Alice.
        Assert.Equal(current.SourceNodeUuid, closed.SourceNodeUuid);
        Assert.Single(await h.EntitiesAsync(group), n => n.Name == "Alice");
    }
}
