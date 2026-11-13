// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Models;

namespace GraphitiNet.Driver.PostgreSql.Tests;

/// <summary>Builders for test records. Embeddings are 4-dimensional.</summary>
internal static class GraphBuilders
{
    public const string Group = "group-a";

    public static readonly DateTimeOffset T0 = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static EpisodicNode Episode(
        string content = "Alice met Bob at the cafe",
        string group = Group,
        DateTimeOffset? validAt = null,
        DateTimeOffset? createdAt = null,
        EpisodeType source = EpisodeType.Message,
        string sourceDescription = "chat",
        string? uuid = null) => new()
        {
            Uuid = uuid ?? Guid.NewGuid().ToString(),
            Name = "episode",
            GroupId = group,
            Source = source,
            SourceDescription = sourceDescription,
            Content = content,
            ValidAt = validAt ?? T0,
            CreatedAt = createdAt ?? T0,
        };

    public static EntityNode Entity(
        string name,
        string group = Group,
        string summary = "",
        float[]? embedding = null,
        IReadOnlyList<string>? labels = null,
        IDictionary<string, object?>? attributes = null,
        string? uuid = null) => new()
        {
            Uuid = uuid ?? Guid.NewGuid().ToString(),
            Name = name,
            GroupId = group,
            Summary = summary,
            NameEmbedding = embedding,
            Labels = labels ?? ["Entity"],
            Attributes = attributes ?? new Dictionary<string, object?>(),
            CreatedAt = T0,
        };

    public static EntityEdge Fact(
        EntityNode source,
        EntityNode target,
        string fact = "related",
        string name = "RELATES_TO",
        float[]? embedding = null,
        DateTimeOffset? validAt = null,
        DateTimeOffset? invalidAt = null,
        DateTimeOffset? expiredAt = null,
        DateTimeOffset? createdAt = null,
        string? group = null,
        IDictionary<string, object?>? attributes = null) => new()
        {
            GroupId = group ?? source.GroupId,
            SourceNodeUuid = source.Uuid,
            TargetNodeUuid = target.Uuid,
            Name = name,
            Fact = fact,
            FactEmbedding = embedding,
            ValidAt = validAt,
            InvalidAt = invalidAt,
            ExpiredAt = expiredAt,
            CreatedAt = createdAt ?? T0,
            Attributes = attributes ?? new Dictionary<string, object?>(),
        };

    public static EpisodicEdge Mention(EpisodicNode episode, EntityNode entity) => new()
    {
        GroupId = episode.GroupId,
        SourceNodeUuid = episode.Uuid,
        TargetNodeUuid = entity.Uuid,
        CreatedAt = T0,
    };

    public static CommunityNode Community(string name, string group = Group, float[]? embedding = null) => new()
    {
        Name = name,
        GroupId = group,
        Summary = name + " summary",
        NameEmbedding = embedding,
        CreatedAt = T0,
    };

    public static CommunityEdge Member(CommunityNode community, Node member) => new()
    {
        GroupId = community.GroupId,
        SourceNodeUuid = community.Uuid,
        TargetNodeUuid = member.Uuid,
        CreatedAt = T0,
    };

    public static SagaNode Saga(string name, string group = Group) => new()
    {
        Name = name,
        GroupId = group,
        CreatedAt = T0,
    };

    public static HasEpisodeEdge HasEpisode(SagaNode saga, EpisodicNode episode) => new()
    {
        GroupId = saga.GroupId,
        SourceNodeUuid = saga.Uuid,
        TargetNodeUuid = episode.Uuid,
        CreatedAt = T0,
    };

    public static NextEpisodeEdge Next(EpisodicNode from, EpisodicNode to) => new()
    {
        GroupId = from.GroupId,
        SourceNodeUuid = from.Uuid,
        TargetNodeUuid = to.Uuid,
        CreatedAt = T0,
    };
}
