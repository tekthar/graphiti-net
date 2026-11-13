// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Data.SqlTypes;

namespace GraphitiNet.Driver.SqlServer.Entities;

// EF Core entity classes for graphiti-net's tables. They mirror the core records in GraphitiNet.Models; the driver maps
// between the two. Hosts that add the tables to their own DbContext (AddGraphiti) see these types in their model.
// Lists map to JSON arrays in nvarchar(max) columns (EF Core primitive collections), JSON objects to nvarchar(max)
// (kept as raw JSON text here), embeddings to the native vector(N) type. Two more tables hold the keyword index the
// driver maintains for BM25 ranking (SQL Server's full-text search ranks differently and is missing from the Linux
// containers): SearchTermRecord and SearchFieldRecord.

/// <summary>Columns every graph record has.</summary>
public abstract class GraphRecord
{
    /// <summary>The record's id (primary key).</summary>
    public string Uuid { get; set; } = "";

    /// <summary>The graph partition.</summary>
    public string GroupId { get; set; } = "";

    /// <summary>When graphiti-net recorded it (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Columns every node has.</summary>
public abstract class NodeRecord : GraphRecord
{
    /// <summary>The node's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Type labels.</summary>
    public List<string> Labels { get; set; } = [];
}

/// <summary>Columns every edge has.</summary>
public abstract class EdgeRecord : GraphRecord
{
    /// <summary>The node the edge starts from.</summary>
    public string SourceNodeUuid { get; set; } = "";

    /// <summary>The node the edge points to.</summary>
    public string TargetNodeUuid { get; set; } = "";
}

/// <summary>An episode row (<c>episodes</c>).</summary>
public sealed class EpisodeRecord : NodeRecord
{
    /// <summary>The source type's stored name: <c>message</c>, <c>json</c>, <c>text</c> or <c>fact_triple</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>Where the input came from.</summary>
    public string SourceDescription { get; set; } = "";

    /// <summary>The raw input.</summary>
    public string Content { get; set; } = "";

    /// <summary>The episode's reference time (UTC).</summary>
    public DateTimeOffset ValidAt { get; set; }

    /// <summary>Ids of the facts the episode produced.</summary>
    public List<string> EntityEdges { get; set; } = [];

    /// <summary>Caller metadata as a JSON object, or <see langword="null"/>.</summary>
    public string? EpisodeMetadata { get; set; }
}

/// <summary>An entity row (<c>entities</c>).</summary>
public sealed class EntityNodeRecord : NodeRecord
{
    /// <summary>The evolving summary.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Custom attributes as a JSON object.</summary>
    public string Attributes { get; set; } = "{}";

    /// <summary>The name embedding (<c>vector(N)</c>).</summary>
    public SqlVector<float>? NameEmbedding { get; set; }
}

/// <summary>A community row (<c>communities</c>).</summary>
public sealed class CommunityRecord : NodeRecord
{
    /// <summary>The members' summary.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The name embedding (<c>vector(N)</c>).</summary>
    public SqlVector<float>? NameEmbedding { get; set; }
}

/// <summary>A saga row (<c>sagas</c>).</summary>
public sealed class SagaRecord : NodeRecord
{
    /// <summary>The saga summary.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The first episode.</summary>
    public string? FirstEpisodeUuid { get; set; }

    /// <summary>The latest episode.</summary>
    public string? LastEpisodeUuid { get; set; }

    /// <summary>When the summary was last updated.</summary>
    public DateTimeOffset? LastSummarizedAt { get; set; }

    /// <summary>The latest episode reference time the summary covers.</summary>
    public DateTimeOffset? LastSummarizedEpisodeValidAt { get; set; }
}

/// <summary>An episode-to-entity link row (<c>episodic_edges</c>, upstream <c>MENTIONS</c>).</summary>
public sealed class EpisodicEdgeRecord : EdgeRecord;

/// <summary>A fact row (<c>entity_edges</c>, upstream <c>RELATES_TO</c>).</summary>
public sealed class EntityEdgeRecord : EdgeRecord
{
    /// <summary>The relationship name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The fact text.</summary>
    public string Fact { get; set; } = "";

    /// <summary>The fact embedding (<c>vector(N)</c>).</summary>
    public SqlVector<float>? FactEmbedding { get; set; }

    /// <summary>Ids of the episodes that state the fact.</summary>
    public List<string> Episodes { get; set; } = [];

    /// <summary>When the fact was superseded.</summary>
    public DateTimeOffset? ExpiredAt { get; set; }

    /// <summary>When the fact became true.</summary>
    public DateTimeOffset? ValidAt { get; set; }

    /// <summary>When the fact stopped being true.</summary>
    public DateTimeOffset? InvalidAt { get; set; }

    /// <summary>The reference time of the episode that produced the fact.</summary>
    public DateTimeOffset? ReferenceTime { get; set; }

    /// <summary>Custom attributes as a JSON object.</summary>
    public string Attributes { get; set; } = "{}";
}

/// <summary>A community membership row (<c>community_edges</c>, upstream <c>HAS_MEMBER</c>).</summary>
public sealed class CommunityEdgeRecord : EdgeRecord;

/// <summary>A saga-to-episode row (<c>has_episode_edges</c>, upstream <c>HAS_EPISODE</c>).</summary>
public sealed class HasEpisodeEdgeRecord : EdgeRecord;

/// <summary>An episode chain row (<c>next_episode_edges</c>, upstream <c>NEXT_EPISODE</c>).</summary>
public sealed class NextEpisodeEdgeRecord : EdgeRecord;

/// <summary>
/// One term of one searchable field of one record (<c>search_terms</c>): the keyword index the driver writes with
/// every save of an episode, entity, community or fact, and reads for BM25 ranking.
/// </summary>
public sealed class SearchTermRecord
{
    /// <summary>The record type: 1 episodes, 2 entities, 3 communities, 4 facts.</summary>
    public byte DocKind { get; set; }

    /// <summary>The record's id.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>The field's position among the record type's searchable fields.</summary>
    public byte Field { get; set; }

    /// <summary>The lower-cased term.</summary>
    public string Term { get; set; } = "";

    /// <summary>The record's group, so a search scores only the groups it asks for.</summary>
    public string GroupId { get; set; } = "";

    /// <summary>How many times the term occurs in the field.</summary>
    public int Frequency { get; set; }

    /// <summary>The field's length in terms.</summary>
    public int FieldLength { get; set; }
}

/// <summary>
/// The length in terms of one non-empty searchable field of one record (<c>search_fields</c>), for BM25's collection
/// statistics (document count and average field length).
/// </summary>
public sealed class SearchFieldRecord
{
    /// <summary>The record type: 1 episodes, 2 entities, 3 communities, 4 facts.</summary>
    public byte DocKind { get; set; }

    /// <summary>The record's id.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>The field's position among the record type's searchable fields.</summary>
    public byte Field { get; set; }

    /// <summary>The field's length in terms (at least 1).</summary>
    public int FieldLength { get; set; }
}
