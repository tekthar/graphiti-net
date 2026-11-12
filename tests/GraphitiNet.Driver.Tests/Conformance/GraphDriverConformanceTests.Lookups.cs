// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

// Indexed lookups that deduplication and search depend on: entities by exact name, entities by attribute values, and
// the episodes mentioning many entities at once.
public abstract partial class GraphDriverConformanceTests
{
    [Fact]
    public async Task Entities_by_names_match_exactly_within_the_given_groups()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var alice = Entity(group, "Alice Smith");
        var aliceTwin = Entity(group, "Alice Smith");
        var lower = Entity(group, "alice smith");
        var spaced = Entity(group, "Alice Smith ");
        var bob = Entity(group, "Bob");
        var elsewhere = Entity(otherGroup, "Alice Smith");
        await Save(alice, aliceTwin, lower, spaced, bob, elsewhere);

        var found = await Driver.EntityNodeOps.GetByNamesAsync(["Alice Smith", "Bob", "Nobody"], [group]);

        AssertSameNodes([alice, aliceTwin, bob], found);
        Assert.All(found, n => Assert.Null(n.NameEmbedding));
        AssertSameNodes(
            [alice, aliceTwin, elsewhere],
            await Driver.EntityNodeOps.GetByNamesAsync(["Alice Smith"], [group, otherGroup]));
        Assert.Empty(await Driver.EntityNodeOps.GetByNamesAsync([], [group]));
        Assert.Empty(await Driver.EntityNodeOps.GetByNamesAsync(["Alice Smith"], []));
    }

    [Fact]
    public async Task Entities_by_attribute_values_match_every_value_in_one_group()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var created = Day(3);
        var acme = Entity(group, "Acme", labels: ["Company"], attributes: Attributes(("ticker", "ACME"), ("employees", 50L)),
            createdAt: created);
        var acmeLater = Entity(group, "Acme Inc", labels: ["Company"],
            attributes: Attributes(("ticker", "ACME"), ("employees", 50L), ("listed", true)), createdAt: created.AddHours(1));
        var otherTicker = Entity(group, "Initech", labels: ["Company"], attributes: Attributes(("ticker", "INTC")));
        var person = Entity(group, "Acme Person", labels: ["Person"], attributes: Attributes(("ticker", "ACME")));
        var caseDiffers = Entity(group, "acme", labels: ["Company"], attributes: Attributes(("ticker", "acme")));
        var noTicker = Entity(group, "Nameless", labels: ["Company"]);
        var elsewhere = Entity(otherGroup, "Acme", labels: ["Company"], attributes: Attributes(("ticker", "ACME")));
        await Save(acme, acmeLater, otherTicker, person, caseDiffers, noTicker, elsewhere);

        async Task<IReadOnlyList<EntityNode>> Find(
            IReadOnlyDictionary<string, object?> values, IReadOnlyList<string>? labels = null, int limit = 100) =>
            await Driver.EntityNodeOps.GetByAttributeValuesAsync(group, values, labels, limit);

        var byTicker = await Find(Attributes(("ticker", "ACME")), ["Company"]);
        Assert.Equal(Ids([acme, acmeLater]), Ids(byTicker));
        Assert.All(byTicker, n => Assert.Null(n.NameEmbedding));
        Assert.Equal(
            Ids([acme, acmeLater, person]).Order(),
            Ids(await Find(Attributes(("ticker", "ACME")))).Order());
        Assert.Equal(Ids([acme, acmeLater]), Ids(await Find(Attributes(("ticker", "ACME")), ["Entity", "Company"])));

        // Every value must match; numbers compare by value.
        Assert.Equal(Ids([acme, acmeLater]), Ids(await Find(Attributes(("ticker", "ACME"), ("employees", 50.0)))));
        Assert.Equal(Ids([acmeLater]), Ids(await Find(Attributes(("ticker", "ACME"), ("listed", true)))));
        Assert.Empty(await Find(Attributes(("ticker", "ACME"), ("employees", 51L))));
        Assert.Empty(await Find(Attributes(("ticker", "ACME"), ("missing", "x"))));
        Assert.Empty(await Find(Attributes(("ticker", "Acme")), ["Company"]));
        Assert.Single(await Find(Attributes(("ticker", "ACME")), ["Company"], limit: 1));
        Assert.Empty(await Find(Attributes()));
    }

    [Fact]
    public async Task Episode_ids_for_many_entities_come_from_their_own_group_in_one_call()
    {
        var group = NewGroup();
        var otherGroup = NewGroup();
        var e1 = Episode(group, "one", validAt: Day(2));
        var e2 = Episode(group, "two", validAt: Day(1));
        var foreign = Episode(otherGroup, "foreign", validAt: Day(1));
        var alice = Entity(group, "Alice");
        var bob = Entity(group, "Bob");
        var carol = Entity(group, "Carol");
        await Save(e1, e2, foreign);
        await Save(alice, bob, carol);
        await Save(
            Mention(group, e1, alice), Mention(group, e2, alice), Mention(group, e1, bob),
            Mention(otherGroup, foreign, bob));
        var unknown = Guid.NewGuid().ToString();

        var found = await Driver.EpisodeNodeOps.GetEpisodeUuidsByEntityNodeUuidsAsync(
            [alice.Uuid, bob.Uuid, carol.Uuid, unknown, alice.Uuid]);

        Assert.Equal(4, found.Count);
        Assert.Equal(new[] { e1.Uuid, e2.Uuid }.Order(), found[alice.Uuid].Order());
        Assert.Equal([e1.Uuid], found[bob.Uuid]);
        Assert.Empty(found[carol.Uuid]);
        Assert.Empty(found[unknown]);
        Assert.Empty(await Driver.EpisodeNodeOps.GetEpisodeUuidsByEntityNodeUuidsAsync([]));
    }

    private static Dictionary<string, object?> Attributes(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
}
