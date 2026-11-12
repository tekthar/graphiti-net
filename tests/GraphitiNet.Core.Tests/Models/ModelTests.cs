// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Tests.Models;

public class ModelTests
{
    [Fact]
    public void Nodes_get_a_uuid_and_utc_creation_time()
    {
        var a = new EntityNode { Name = "Alice", GroupId = "g" };
        var b = new EntityNode { Name = "Bob", GroupId = "g" };

        Assert.True(Guid.TryParse(a.Uuid, out _));
        Assert.Equal(a.Uuid.ToLowerInvariant(), a.Uuid);
        Assert.NotEqual(a.Uuid, b.Uuid);
        Assert.Equal(TimeSpan.Zero, a.CreatedAt.Offset);
        Assert.Empty(a.Labels);
        Assert.Equal("", a.Summary);
        Assert.Null(a.NameEmbedding);
        Assert.Empty(a.Attributes);
    }

    [Fact]
    public void Labels_are_copied_on_assignment()
    {
        var labels = new List<string> { "Person" };
        var node = new EntityNode { Name = "Alice", GroupId = "g", Labels = labels };

        labels.Add("not a label");

        Assert.Equal(["Person"], node.Labels);
    }

    [Fact]
    public void Entity_edge_starts_open_with_no_episodes()
    {
        var edge = new EntityEdge
        {
            GroupId = "g",
            SourceNodeUuid = "a",
            TargetNodeUuid = "b",
            CreatedAt = DateTimeOffset.UtcNow,
            Name = "WORKS_AT",
            Fact = "Alice works at Acme",
        };

        Assert.Empty(edge.Episodes);
        Assert.Null(edge.ValidAt);
        Assert.Null(edge.InvalidAt);
        Assert.Null(edge.ExpiredAt);
        Assert.Null(edge.FactEmbedding);
    }

    [Fact]
    public void Saga_node_starts_empty()
    {
        var saga = new SagaNode { Name = "support chat", GroupId = "g" };

        Assert.Equal("", saga.Summary);
        Assert.Null(saga.FirstEpisodeUuid);
        Assert.Null(saga.LastEpisodeUuid);
        Assert.Null(saga.LastSummarizedAt);
        Assert.Null(saga.LastSummarizedEpisodeValidAt);
    }

    [Theory]
    [InlineData(EpisodeType.Message, "message")]
    [InlineData(EpisodeType.Json, "json")]
    [InlineData(EpisodeType.Text, "text")]
    [InlineData(EpisodeType.FactTriple, "fact_triple")]
    public void Episode_type_round_trips_through_its_storage_name(EpisodeType type, string name)
    {
        Assert.Equal(name, type.ToStorageName());
        Assert.Equal(type, EpisodeTypes.FromStorageName(name));
    }

    [Theory]
    [InlineData("Message")]
    [InlineData("email")]
    [InlineData("")]
    public void Unknown_episode_type_names_are_rejected(string name)
    {
        Assert.Throws<NotSupportedException>(() => EpisodeTypes.FromStorageName(name));
    }
}
