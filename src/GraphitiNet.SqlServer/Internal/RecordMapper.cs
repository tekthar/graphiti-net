// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Linq.Expressions;
using System.Text.Json;
using GraphitiNet.Driver.SqlServer.Entities;
using GraphitiNet.Models;
using Microsoft.Data.SqlClient;

namespace GraphitiNet.Driver.SqlServer.Internal;

/// <summary>
/// Maps rows to core records. Reads never select embeddings (the contract leaves them unloaded), so each type has a
/// LINQ projection and a raw-SQL column list that both skip them.
/// </summary>
internal static class RecordMapper
{
    // ----- Column lists for raw SQL, read back in this order by the Read* methods. -----

    public static string EpisodeColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.name, {a}.labels, {a}.created_at, {a}.source, {a}.source_description, "
        + $"{a}.content, {a}.valid_at, {a}.entity_edges, {a}.episode_metadata";

    public static string EntityColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.name, {a}.labels, {a}.created_at, {a}.summary, {a}.attributes";

    public static string CommunityColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.name, {a}.labels, {a}.created_at, {a}.summary";

    public static string EntityEdgeColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.source_node_uuid, {a}.target_node_uuid, {a}.created_at, {a}.name, {a}.fact, "
        + $"{a}.episodes, {a}.expired_at, {a}.valid_at, {a}.invalid_at, {a}.reference_time, {a}.attributes";

    public static EpisodicNode ReadEpisode(SqlDataReader r) => ToModel(new EpisodeRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        Name = r.GetString(2),
        Labels = Strings(r.GetString(3)),
        CreatedAt = r.GetDateTimeOffset(4),
        Source = r.GetString(5),
        SourceDescription = r.GetString(6),
        Content = r.GetString(7),
        ValidAt = r.GetDateTimeOffset(8),
        EntityEdges = Strings(r.GetString(9)),
        EpisodeMetadata = r.IsDBNull(10) ? null : r.GetString(10),
    });

    public static EntityNode ReadEntity(SqlDataReader r) => ToModel(new EntityNodeRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        Name = r.GetString(2),
        Labels = Strings(r.GetString(3)),
        CreatedAt = r.GetDateTimeOffset(4),
        Summary = r.GetString(5),
        Attributes = r.GetString(6),
    });

    public static CommunityNode ReadCommunity(SqlDataReader r) => ToModel(new CommunityRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        Name = r.GetString(2),
        Labels = Strings(r.GetString(3)),
        CreatedAt = r.GetDateTimeOffset(4),
        Summary = r.GetString(5),
    });

    public static EntityEdge ReadEntityEdge(SqlDataReader r) => ToModel(new EntityEdgeRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        SourceNodeUuid = r.GetString(2),
        TargetNodeUuid = r.GetString(3),
        CreatedAt = r.GetDateTimeOffset(4),
        Name = r.GetString(5),
        Fact = r.GetString(6),
        Episodes = Strings(r.GetString(7)),
        ExpiredAt = NullableTime(r, 8),
        ValidAt = NullableTime(r, 9),
        InvalidAt = NullableTime(r, 10),
        ReferenceTime = NullableTime(r, 11),
        Attributes = r.GetString(12),
    });

    private static List<string> Strings(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static DateTimeOffset? NullableTime(SqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetDateTimeOffset(ordinal);

    // ----- LINQ projections: every column except embeddings. -----

    public static readonly Expression<Func<EpisodeRecord, EpisodeRecord>> EpisodeProjection = e => new EpisodeRecord
    {
        Uuid = e.Uuid,
        GroupId = e.GroupId,
        Name = e.Name,
        Labels = e.Labels,
        CreatedAt = e.CreatedAt,
        Source = e.Source,
        SourceDescription = e.SourceDescription,
        Content = e.Content,
        ValidAt = e.ValidAt,
        EntityEdges = e.EntityEdges,
        EpisodeMetadata = e.EpisodeMetadata,
    };

    public static readonly Expression<Func<EntityNodeRecord, EntityNodeRecord>> EntityProjection =
        e => new EntityNodeRecord
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            Name = e.Name,
            Labels = e.Labels,
            CreatedAt = e.CreatedAt,
            Summary = e.Summary,
            Attributes = e.Attributes,
        };

    public static readonly Expression<Func<CommunityRecord, CommunityRecord>> CommunityProjection =
        e => new CommunityRecord
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            Name = e.Name,
            Labels = e.Labels,
            CreatedAt = e.CreatedAt,
            Summary = e.Summary,
        };

    public static readonly Expression<Func<EntityEdgeRecord, EntityEdgeRecord>> EntityEdgeProjection =
        e => new EntityEdgeRecord
        {
            Uuid = e.Uuid,
            GroupId = e.GroupId,
            SourceNodeUuid = e.SourceNodeUuid,
            TargetNodeUuid = e.TargetNodeUuid,
            CreatedAt = e.CreatedAt,
            Name = e.Name,
            Fact = e.Fact,
            Episodes = e.Episodes,
            ExpiredAt = e.ExpiredAt,
            ValidAt = e.ValidAt,
            InvalidAt = e.InvalidAt,
            ReferenceTime = e.ReferenceTime,
            Attributes = e.Attributes,
        };

    // ----- Records to core models. Times are returned in UTC whatever offset a host stored. -----

    public static EpisodicNode ToModel(EpisodeRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt.ToUniversalTime(),
        Source = EpisodeTypes.FromStorageName(r.Source),
        SourceDescription = r.SourceDescription,
        Content = r.Content,
        ValidAt = r.ValidAt.ToUniversalTime(),
        EntityEdges = r.EntityEdges,
        EpisodeMetadata = JsonValues.DeserializeNullable(r.EpisodeMetadata),
    };

    public static EntityNode ToModel(EntityNodeRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt.ToUniversalTime(),
        Summary = r.Summary,
        Attributes = JsonValues.Deserialize(r.Attributes),
    };

    public static CommunityNode ToModel(CommunityRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt.ToUniversalTime(),
        Summary = r.Summary,
    };

    public static SagaNode ToModel(SagaRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt.ToUniversalTime(),
        Summary = r.Summary,
        FirstEpisodeUuid = r.FirstEpisodeUuid,
        LastEpisodeUuid = r.LastEpisodeUuid,
        LastSummarizedAt = r.LastSummarizedAt?.ToUniversalTime(),
        LastSummarizedEpisodeValidAt = r.LastSummarizedEpisodeValidAt?.ToUniversalTime(),
    };

    public static EntityEdge ToModel(EntityEdgeRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        SourceNodeUuid = r.SourceNodeUuid,
        TargetNodeUuid = r.TargetNodeUuid,
        CreatedAt = r.CreatedAt.ToUniversalTime(),
        Name = r.Name,
        Fact = r.Fact,
        Episodes = r.Episodes,
        ExpiredAt = r.ExpiredAt?.ToUniversalTime(),
        ValidAt = r.ValidAt?.ToUniversalTime(),
        InvalidAt = r.InvalidAt?.ToUniversalTime(),
        ReferenceTime = r.ReferenceTime?.ToUniversalTime(),
        Attributes = JsonValues.Deserialize(r.Attributes),
    };

    public static TEdge ToEdge<TEdge>(EdgeRecord r)
        where TEdge : Edge
    {
        var createdAt = r.CreatedAt.ToUniversalTime();
        Edge edge = typeof(TEdge) switch
        {
            var t when t == typeof(EpisodicEdge) => new EpisodicEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = createdAt,
            },
            var t when t == typeof(CommunityEdge) => new CommunityEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = createdAt,
            },
            var t when t == typeof(HasEpisodeEdge) => new HasEpisodeEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = createdAt,
            },
            var t when t == typeof(NextEpisodeEdge) => new NextEpisodeEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = createdAt,
            },
            _ => throw new NotSupportedException(typeof(TEdge).Name),
        };
        return (TEdge)edge;
    }
}
