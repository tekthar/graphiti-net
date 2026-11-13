// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Linq.Expressions;
using GraphitiNet.Driver.PostgreSql.Entities;
using GraphitiNet.Models;
using Npgsql;

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// Maps rows to core records. Reads never select embeddings or the generated search vectors (the contract leaves
/// embeddings unloaded), so each type has a LINQ projection and a raw-SQL column list that both skip them.
/// </summary>
internal static class RecordMapper
{
    // ----- Column lists for raw SQL, read back in this order by the Read* methods. -----

    public static string EpisodeColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.name, {a}.labels, {a}.created_at, {a}.source, {a}.source_description, "
        + $"{a}.content, {a}.valid_at, {a}.entity_edges, {a}.episode_metadata::text, {a}.idempotency_key";

    public static string EntityColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.name, {a}.labels, {a}.created_at, {a}.summary, {a}.attributes::text";

    public static string CommunityColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.name, {a}.labels, {a}.created_at, {a}.summary";

    public static string EntityEdgeColumns(string a) =>
        $"{a}.uuid, {a}.group_id, {a}.source_node_uuid, {a}.target_node_uuid, {a}.created_at, {a}.name, {a}.fact, "
        + $"{a}.episodes, {a}.expired_at, {a}.valid_at, {a}.invalid_at, {a}.reference_time, {a}.attributes::text";

    public static EpisodicNode ReadEpisode(NpgsqlDataReader r) => ToModel(new EpisodeRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        Name = r.GetString(2),
        Labels = [.. r.GetFieldValue<string[]>(3)],
        CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
        Source = r.GetString(5),
        SourceDescription = r.GetString(6),
        Content = r.GetString(7),
        ValidAt = r.GetFieldValue<DateTimeOffset>(8),
        EntityEdges = [.. r.GetFieldValue<string[]>(9)],
        EpisodeMetadata = r.IsDBNull(10) ? null : r.GetString(10),
        IdempotencyKey = r.IsDBNull(11) ? null : r.GetString(11),
    });

    public static EntityNode ReadEntity(NpgsqlDataReader r) => ToModel(new EntityNodeRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        Name = r.GetString(2),
        Labels = [.. r.GetFieldValue<string[]>(3)],
        CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
        Summary = r.GetString(5),
        Attributes = r.GetString(6),
    });

    public static CommunityNode ReadCommunity(NpgsqlDataReader r) => ToModel(new CommunityRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        Name = r.GetString(2),
        Labels = [.. r.GetFieldValue<string[]>(3)],
        CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
        Summary = r.GetString(5),
    });

    public static EntityEdge ReadEntityEdge(NpgsqlDataReader r) => ToModel(new EntityEdgeRecord
    {
        Uuid = r.GetString(0),
        GroupId = r.GetString(1),
        SourceNodeUuid = r.GetString(2),
        TargetNodeUuid = r.GetString(3),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(4),
        Name = r.GetString(5),
        Fact = r.GetString(6),
        Episodes = [.. r.GetFieldValue<string[]>(7)],
        ExpiredAt = NullableTime(r, 8),
        ValidAt = NullableTime(r, 9),
        InvalidAt = NullableTime(r, 10),
        ReferenceTime = NullableTime(r, 11),
        Attributes = r.GetString(12),
    });

    private static DateTimeOffset? NullableTime(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateTimeOffset>(ordinal);

    // ----- LINQ projections: every column except embeddings and search vectors. -----

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
        IdempotencyKey = e.IdempotencyKey,
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

    // ----- Records to core models. -----

    public static EpisodicNode ToModel(EpisodeRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt,
        Source = EpisodeTypes.FromStorageName(r.Source),
        SourceDescription = r.SourceDescription,
        Content = r.Content,
        ValidAt = r.ValidAt,
        EntityEdges = r.EntityEdges,
        EpisodeMetadata = JsonValues.DeserializeNullable(r.EpisodeMetadata),
        IdempotencyKey = r.IdempotencyKey,
    };

    public static EntityNode ToModel(EntityNodeRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt,
        Summary = r.Summary,
        Attributes = JsonValues.Deserialize(r.Attributes),
    };

    public static CommunityNode ToModel(CommunityRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt,
        Summary = r.Summary,
    };

    public static SagaNode ToModel(SagaRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        Name = r.Name,
        Labels = r.Labels,
        CreatedAt = r.CreatedAt,
        Summary = r.Summary,
        FirstEpisodeUuid = r.FirstEpisodeUuid,
        LastEpisodeUuid = r.LastEpisodeUuid,
        LastSummarizedAt = r.LastSummarizedAt,
        LastSummarizedEpisodeValidAt = r.LastSummarizedEpisodeValidAt,
    };

    public static EntityEdge ToModel(EntityEdgeRecord r) => new()
    {
        Uuid = r.Uuid,
        GroupId = r.GroupId,
        SourceNodeUuid = r.SourceNodeUuid,
        TargetNodeUuid = r.TargetNodeUuid,
        CreatedAt = r.CreatedAt,
        Name = r.Name,
        Fact = r.Fact,
        Episodes = r.Episodes,
        ExpiredAt = r.ExpiredAt,
        ValidAt = r.ValidAt,
        InvalidAt = r.InvalidAt,
        ReferenceTime = r.ReferenceTime,
        Attributes = JsonValues.Deserialize(r.Attributes),
    };

    public static TEdge ToEdge<TEdge>(EdgeRecord r)
        where TEdge : Edge
    {
        Edge edge = typeof(TEdge) switch
        {
            var t when t == typeof(EpisodicEdge) => new EpisodicEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = r.CreatedAt,
            },
            var t when t == typeof(CommunityEdge) => new CommunityEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = r.CreatedAt,
            },
            var t when t == typeof(HasEpisodeEdge) => new HasEpisodeEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = r.CreatedAt,
            },
            var t when t == typeof(NextEpisodeEdge) => new NextEpisodeEdge
            {
                Uuid = r.Uuid, GroupId = r.GroupId, SourceNodeUuid = r.SourceNodeUuid,
                TargetNodeUuid = r.TargetNodeUuid, CreatedAt = r.CreatedAt,
            },
            _ => throw new NotSupportedException(typeof(TEdge).Name),
        };
        return (TEdge)edge;
    }

    /// <summary>Converts a time to UTC, which Npgsql requires for <c>timestamptz</c>.</summary>
    public static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    public static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();
}
