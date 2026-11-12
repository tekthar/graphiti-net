// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/driver/driver.py and graphiti_core/driver/operations/*.py (Graphiti v0.31.2) and changed
// for .NET.

using GraphitiNet.Models;
using GraphitiNet.Search;

namespace GraphitiNet.Driver;

/// <summary>
/// A storage backend for the graph. One implementation per database: in-memory, PostgreSQL, SQLite and so on.
/// </summary>
/// <remarks>
/// <para>Upstream drivers expose <c>execute_query</c> plus one operations object per record type. graphiti-net has no
/// shared query language, so the operations objects are the whole contract: core code never sends raw queries.</para>
/// <para>Contract rules every implementation follows (the driver conformance tests check them):</para>
/// <list type="bullet">
/// <item>Saves are upserts keyed by <c>Uuid</c>: saving an existing id replaces its stored fields.</item>
/// <item>Get and search methods do not load embeddings (<c>NameEmbedding</c>, <c>FactEmbedding</c> stay
/// <see langword="null"/>), as upstream; call the <c>LoadEmbeddings</c> methods when they are needed.</item>
/// <item><c>GetByUuidAsync</c> throws <see cref="NodeNotFoundException"/> or <see cref="EdgeNotFoundException"/> when the
/// id does not exist. <c>GetByUuidsAsync</c> skips missing ids.</item>
/// <item><c>GetByGroupIdsAsync</c> pages in descending <c>Uuid</c> order: <c>uuidCursor</c> returns only ids that sort
/// before it (ordinal comparison), and <c>limit</c> caps the page.</item>
/// <item>Deleting a node also deletes every edge that touches it (upstream's <c>DETACH DELETE</c>).</item>
/// <item>Every timestamp is stored and returned in UTC.</item>
/// <item>Labels round-trip as a set (order and duplicates are not kept). Entity nodes always come back with the
/// <c>Entity</c> label, added if it was not saved, as upstream; drivers add or remove no other label.</item>
/// <item>Attribute and episode metadata values round-trip as JSON: what comes back serialises to the same JSON
/// (strings, numbers, booleans, <see langword="null"/>, nested objects and lists).</item>
/// <item>Edges are saved after their endpoints; what happens to an edge whose endpoint does not exist is up to the
/// backend (upstream's Neo4j driver silently skips it).</item>
/// </list>
/// <para>See <c>docs/drivers.md</c> for the full rules and the conformance suite.</para>
/// </remarks>
public interface IGraphDriver : IAsyncDisposable
{
    /// <summary>A short name for the backend, such as <c>memory</c>, <c>postgresql</c> or <c>sqlite</c>.</summary>
    string Provider { get; }

    /// <summary>Episode storage.</summary>
    IEpisodeNodeOperations EpisodeNodeOps { get; }

    /// <summary>Entity storage.</summary>
    IEntityNodeOperations EntityNodeOps { get; }

    /// <summary>Community storage.</summary>
    ICommunityNodeOperations CommunityNodeOps { get; }

    /// <summary>Saga storage.</summary>
    ISagaNodeOperations SagaNodeOps { get; }

    /// <summary>Episode-to-entity link storage.</summary>
    IEpisodicEdgeOperations EpisodicEdgeOps { get; }

    /// <summary>Fact storage.</summary>
    IEntityEdgeOperations EntityEdgeOps { get; }

    /// <summary>Community membership storage.</summary>
    ICommunityEdgeOperations CommunityEdgeOps { get; }

    /// <summary>Saga-to-episode link storage.</summary>
    IHasEpisodeEdgeOperations HasEpisodeEdgeOps { get; }

    /// <summary>Episode chain storage.</summary>
    INextEpisodeEdgeOperations NextEpisodeEdgeOps { get; }

    /// <summary>Full-text, vector and graph search.</summary>
    ISearchOperations SearchOps { get; }

    /// <summary>Schema, clean-up and community helpers.</summary>
    IGraphMaintenanceOperations GraphOps { get; }

    /// <summary>
    /// Saves an ingestion result in one transaction where the backend supports it. Ported from upstream's
    /// <c>add_nodes_and_edges_bulk_tx</c>; embeddings must already be set on the records.
    /// </summary>
    Task AddNodesAndEdgesBulkAsync(
        IReadOnlyList<EpisodicNode> episodicNodes,
        IReadOnlyList<EpisodicEdge> episodicEdges,
        IReadOnlyList<EntityNode> entityNodes,
        IReadOnlyList<EntityEdge> entityEdges,
        CancellationToken cancellationToken = default);
}

/// <summary>Operations shared by every record type.</summary>
/// <typeparam name="T">The record type.</typeparam>
public interface IRecordOperations<T>
{
    /// <summary>Inserts or replaces the record.</summary>
    Task SaveAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces each record.</summary>
    Task SaveBulkAsync(IReadOnlyList<T> items, CancellationToken cancellationToken = default);

    /// <summary>Deletes the record. Deleting a node also deletes the edges that touch it.</summary>
    Task DeleteAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>Deletes the records with these ids; unknown ids are ignored.</summary>
    Task DeleteByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default);

    /// <summary>Gets one record.</summary>
    /// <exception cref="NodeNotFoundException">No node has this id.</exception>
    /// <exception cref="EdgeNotFoundException">No edge has this id.</exception>
    Task<T> GetByUuidAsync(string uuid, CancellationToken cancellationToken = default);

    /// <summary>Gets the records that exist among these ids.</summary>
    Task<IReadOnlyList<T>> GetByUuidsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken = default);

    /// <summary>Gets records in these groups, in descending id order, one page at a time.</summary>
    Task<IReadOnlyList<T>> GetByGroupIdsAsync(
        IReadOnlyList<string> groupIds,
        int? limit = null,
        string? uuidCursor = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Operations for node types that can be deleted per group.</summary>
/// <typeparam name="T">The node type.</typeparam>
public interface INodeOperations<T> : IRecordOperations<T>
    where T : Node
{
    /// <summary>Deletes every node of this type in a group, with the edges that touch them.</summary>
    Task DeleteByGroupIdAsync(string groupId, CancellationToken cancellationToken = default);
}

/// <summary>Episode storage.</summary>
public interface IEpisodeNodeOperations : INodeOperations<EpisodicNode>
{
    /// <summary>The episodes that mention an entity (through episodic edges).</summary>
    Task<IReadOnlyList<EpisodicNode>> GetByEntityNodeUuidAsync(
        string entityNodeUuid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// For each entity id, the ids of the episodes in the entity's own group that mention it, without duplicates, in
    /// <see cref="GetByEntityNodeUuidAsync"/>'s order. Every requested id is a key; an unknown entity maps to an empty
    /// list. The search episode-access filter uses it to fetch every entity's episodes in one call.
    /// </summary>
    /// <remarks>The default implementation calls <see cref="GetByEntityNodeUuidAsync"/> once per entity. It cannot see
    /// the entity's group, so it keeps every mentioning episode (core always writes a mention in the entity's group,
    /// so this differs only for hand-made cross-group links). Backends should override it with one query that applies
    /// the group rule.</remarks>
    async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetEpisodeUuidsByEntityNodeUuidsAsync(
        IReadOnlyList<string> entityNodeUuids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityNodeUuids);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var uuid in entityNodeUuids.Distinct(StringComparer.Ordinal))
        {
            var episodes = await GetByEntityNodeUuidAsync(uuid, cancellationToken).ConfigureAwait(false);
            result[uuid] = [.. episodes.Select(e => e.Uuid).Distinct(StringComparer.Ordinal)];
        }

        return result;
    }

    /// <summary>
    /// The <paramref name="lastN"/> most recent episodes with <c>ValidAt</c> at or before
    /// <paramref name="referenceTime"/>, returned oldest first (upstream sorts by <c>valid_at</c> descending, takes
    /// <c>last_n</c>, then reverses).
    /// </summary>
    /// <param name="referenceTime">Only episodes valid at or before this time.</param>
    /// <param name="lastN">How many episodes.</param>
    /// <param name="groupIds">Restrict to these groups, or all groups if <see langword="null"/> or empty (as
    /// upstream).</param>
    /// <param name="source">Restrict to one source type.</param>
    /// <param name="saga">Restrict to episodes of the saga with this name in the first of
    /// <paramref name="groupIds"/>, as upstream; with no group ids, nothing matches. The group ids do not further
    /// restrict the saga's episodes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        DateTimeOffset referenceTime,
        int lastN = 3,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The episodes in a saga, oldest first (by <c>CreatedAt</c>, then <c>ValidAt</c>). With
    /// <paramref name="createdAfter"/>, only episodes created strictly after it. Replaces upstream's raw query in
    /// <c>Graphiti._saga_get_episode_contents</c>.
    /// </summary>
    Task<IReadOnlyList<EpisodicNode>> GetBySagaAsync(
        string sagaUuid,
        DateTimeOffset? createdAfter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Entity storage.</summary>
public interface IEntityNodeOperations : INodeOperations<EntityNode>
{
    /// <summary>Loads <see cref="EntityNode.NameEmbedding"/> from storage.</summary>
    Task LoadEmbeddingsAsync(EntityNode node, CancellationToken cancellationToken = default);

    /// <summary>Loads <see cref="EntityNode.NameEmbedding"/> for each node.</summary>
    Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityNode> nodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Entities in any of <paramref name="groupIds"/> whose name equals one of <paramref name="names"/> exactly
    /// (case-sensitive), as upstream's <c>EntityNode.get_by_names</c>. Empty names or groups return nothing. Order is
    /// not specified. Entity deduplication uses it for exact-name candidates.
    /// </summary>
    /// <remarks>The default implementation pages through <see cref="IRecordOperations{T}.GetByGroupIdsAsync"/>;
    /// backends should override it with an indexed lookup.</remarks>
    async Task<IReadOnlyList<EntityNode>> GetByNamesAsync(
        IReadOnlyList<string> names,
        IReadOnlyList<string> groupIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(groupIds);
        var nameSet = names.Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.Ordinal);
        var groups = groupIds.Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.Ordinal).ToList();
        if (nameSet.Count == 0 || groups.Count == 0)
        {
            return [];
        }

        const int pageSize = 1000;
        var result = new List<EntityNode>();
        string? cursor = null;
        while (true)
        {
            var page = await GetByGroupIdsAsync(groups, pageSize, cursor, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Where(n => nameSet.Contains(n.Name)));
            if (page.Count < pageSize)
            {
                return result;
            }

            cursor = page[^1].Uuid;
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> entities in <paramref name="groupId"/> whose <see cref="EntityNode.Attributes"/>
    /// equal every given value and, if <paramref name="nodeLabels"/> is given, that carry all those labels (every
    /// entity carries <c>Entity</c>). Values compare as JSON: strings exactly (case-sensitive), numbers by value
    /// (<c>1</c> equals <c>1.0</c>), booleans and <see langword="null"/> as themselves; a missing attribute never
    /// matches. No attributes match nothing. Ordered by <c>CreatedAt</c>, then <c>Uuid</c>. Upstream's optional
    /// <c>node_get_by_attribute_values</c>, used to find identity-property dedup candidates.
    /// </summary>
    /// <remarks>Optional, as upstream (none of upstream's bundled drivers implement it): the default throws
    /// <see cref="NotSupportedException"/>, and deduplication then finds identity matches only among the name and
    /// similarity candidates.</remarks>
    /// <exception cref="NotSupportedException">The backend does not support the lookup.</exception>
    Task<IReadOnlyList<EntityNode>> GetByAttributeValuesAsync(
        string groupId,
        IReadOnlyDictionary<string, object?> attributes,
        IReadOnlyList<string>? nodeLabels = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This driver does not support looking up entities by attribute values.");
}

/// <summary>Community storage.</summary>
public interface ICommunityNodeOperations : INodeOperations<CommunityNode>
{
    /// <summary>Loads <see cref="CommunityNode.NameEmbedding"/> from storage.</summary>
    Task LoadNameEmbeddingAsync(CommunityNode node, CancellationToken cancellationToken = default);
}

/// <summary>Saga storage.</summary>
public interface ISagaNodeOperations : INodeOperations<SagaNode>
{
    /// <summary>The saga with this name in this group, or <see langword="null"/>. Replaces upstream's raw query in
    /// <c>Graphiti._get_or_create_saga</c>.</summary>
    Task<SagaNode?> GetByNameAsync(string name, string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The id of the saga's latest episode other than <paramref name="currentEpisodeUuid"/> (by <c>ValidAt</c>, then
    /// <c>CreatedAt</c>, both descending), or <see langword="null"/>. Replaces upstream's raw query in
    /// <c>Graphiti._saga_get_previous_episode_uuid</c>.
    /// </summary>
    Task<string?> GetPreviousEpisodeUuidAsync(
        string sagaUuid,
        string currentEpisodeUuid,
        CancellationToken cancellationToken = default);
}

/// <summary>Episode-to-entity link storage.</summary>
public interface IEpisodicEdgeOperations : IRecordOperations<EpisodicEdge>;

/// <summary>Fact storage.</summary>
public interface IEntityEdgeOperations : IRecordOperations<EntityEdge>
{
    /// <summary>Facts from <paramref name="sourceNodeUuid"/> to <paramref name="targetNodeUuid"/> (that direction
    /// only).</summary>
    Task<IReadOnlyList<EntityEdge>> GetBetweenNodesAsync(
        string sourceNodeUuid,
        string targetNodeUuid,
        CancellationToken cancellationToken = default);

    /// <summary>Facts that start or end at a node.</summary>
    Task<IReadOnlyList<EntityEdge>> GetByNodeUuidAsync(string nodeUuid, CancellationToken cancellationToken = default);

    /// <summary>Loads <see cref="EntityEdge.FactEmbedding"/> from storage.</summary>
    Task LoadEmbeddingsAsync(EntityEdge edge, CancellationToken cancellationToken = default);

    /// <summary>Loads <see cref="EntityEdge.FactEmbedding"/> for each edge.</summary>
    Task LoadEmbeddingsBulkAsync(IReadOnlyList<EntityEdge> edges, CancellationToken cancellationToken = default);
}

/// <summary>Community membership storage.</summary>
public interface ICommunityEdgeOperations : IRecordOperations<CommunityEdge>;

/// <summary>Saga-to-episode link storage.</summary>
public interface IHasEpisodeEdgeOperations : IRecordOperations<HasEpisodeEdge>;

/// <summary>Episode chain storage.</summary>
public interface INextEpisodeEdgeOperations : IRecordOperations<NextEpisodeEdge>;

/// <summary>
/// Search primitives. Ported from upstream's <c>SearchOperations</c>; rerankers, fusion and configuration live in core,
/// not in drivers.
/// </summary>
/// <remarks>
/// <para><b>Group ids:</b> <see langword="null"/> means every group; an empty list matches nothing.</para>
/// <para><b>Full-text:</b> entity nodes match on name and summary; facts on name and fact text; episodes on content,
/// source and source description (as upstream's <c>episode_content</c> index; the source is its stored name, such as
/// <c>message</c> or <c>json</c>); communities on name. A record matches when it contains any query term (Lucene's default
/// OR), ignoring case and punctuation. Results are ordered by the backend's relevance score, best first. An empty or
/// whitespace query returns nothing.</para>
/// <para><b>Similarity:</b> the score is Neo4j's <c>vector.similarity.cosine</c>, which is <c>(1 + cosine) / 2</c> in
/// [0, 1] (orthogonal vectors score 0.5), because upstream's thresholds are tuned on that scale: its default
/// <c>min_score</c> of 0.6 means a raw cosine above 0.2. Only results scoring strictly more than
/// <c>minScore</c> (<c>score &gt; min_score</c>, as upstream), best first. Records with no embedding never match. The
/// source and target restriction of fact search applies with or without group ids (upstream applies it only with group
/// ids, but core always passes none).</para>
/// <para><b>Breadth-first:</b> paths start at the origin nodes (any node id; in practice entities and episodes) and
/// follow facts from source to target only, and episode-to-entity links from the episode, as upstream's
/// <c>(origin)-[:RELATES_TO|MENTIONS*1..maxDepth]-&gt;(n:Entity)</c>. Every link is one hop, so from an episode the
/// mention is the first hop. <c>maxDepth</c> below 1 returns nothing; drivers do not cap it (core caps it at 3,
/// upstream's <c>MAX_SEARCH_DEPTH</c>). Entity search returns, without duplicates, every entity at the end of a path of
/// 1 to <c>maxDepth</c> hops that is in the same group as the path's origin; the origin itself is returned only when a
/// cycle leads back to it within <c>maxDepth</c> hops. Group ids restrict both the origin and the result. Fact search
/// returns, without duplicates, every fact on such a path, which is every fact whose source is at most
/// <c>maxDepth - 1</c> hops from an origin (the origin itself is 0); group ids restrict the facts only. Result order is
/// not specified.</para>
/// <para><b>Filters:</b> entity searches apply <see cref="SearchFilters.NodeLabels"/> and
/// <see cref="SearchFilters.PropertyFilters"/> (on <see cref="EntityNode.Attributes"/>) and ignore the fact-only
/// fields. Fact searches apply every field: node labels to both endpoints, property filters to
/// <see cref="EntityEdge.Attributes"/>. Episode full-text search takes a filter but does not apply it, as upstream.
/// Every entity has the implicit label <c>Entity</c>. An empty list in a filter matches nothing. Comparisons with a
/// missing value are false; only <see cref="ComparisonOperator.IsNull"/> matches it. Dates compare as instants;
/// property values compare as numbers when both are numbers, as ordinal strings when both are strings, and never
/// across types.</para>
/// </remarks>
public interface ISearchOperations
{
    /// <summary>Entity full-text search.</summary>
    Task<IReadOnlyList<EntityNode>> NodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Entity vector search on the name embedding.</summary>
    Task<IReadOnlyList<EntityNode>> NodeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default);

    /// <summary>Entities reachable from the origin nodes.</summary>
    Task<IReadOnlyList<EntityNode>> NodeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        SearchFilters searchFilter,
        int maxDepth,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Fact full-text search.</summary>
    Task<IReadOnlyList<EntityEdge>> EdgeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Fact vector search on the fact embedding, optionally only between two given nodes.</summary>
    Task<IReadOnlyList<EntityEdge>> EdgeSimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        string? sourceNodeUuid,
        string? targetNodeUuid,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default);

    /// <summary>Facts reachable from the origin nodes.</summary>
    Task<IReadOnlyList<EntityEdge>> EdgeBfsSearchAsync(
        IReadOnlyList<string> originUuids,
        int maxDepth,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Episode full-text search.</summary>
    Task<IReadOnlyList<EpisodicNode>> EpisodeFulltextSearchAsync(
        string query,
        SearchFilters searchFilter,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Community full-text search.</summary>
    Task<IReadOnlyList<CommunityNode>> CommunityFulltextSearchAsync(
        string query,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Community vector search on the name embedding.</summary>
    Task<IReadOnlyList<CommunityNode>> CommunitySimilaritySearchAsync(
        IReadOnlyList<float> searchVector,
        IReadOnlyList<string>? groupIds = null,
        int limit = SearchDefaults.RelevantSchemaLimit,
        double minScore = SearchDefaults.DefaultMinScore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="candidateUuids"/> are joined to <paramref name="centerNodeUuid"/> by at least one fact,
    /// in either direction. Core's node-distance reranker (upstream <c>node_distance_reranker</c>) builds on this.
    /// </summary>
    Task<IReadOnlySet<string>> GetConnectedNodeUuidsAsync(
        string centerNodeUuid,
        IReadOnlyList<string> candidateUuids,
        CancellationToken cancellationToken = default);
}

/// <summary>Schema, clean-up and community helpers.</summary>
public interface IGraphMaintenanceOperations
{
    /// <summary>Deletes every record (nodes, edges, and edges touching those nodes) in these groups, or in the whole
    /// graph if <see langword="null"/>. An empty list deletes nothing.</summary>
    Task ClearDataAsync(IReadOnlyList<string>? groupIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates tables and indexes if they do not exist. With <paramref name="deleteExisting"/>, drops them first.
    /// Backends managed by EF Core migrations may treat this as "apply migrations".
    /// </summary>
    Task BuildIndicesAndConstraintsAsync(bool deleteExisting = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The entity neighbourhood used for community detection: for each entity in the group, its neighbouring entities
    /// in the same group (joined by a fact in either direction) and how many facts join them. Every entity in the group
    /// appears as a key, even with no neighbours. Core runs label propagation over it (upstream's
    /// <c>get_community_clusters</c>).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Neighbor>>> GetEntityProjectionAsync(
        string groupId,
        CancellationToken cancellationToken = default);

    /// <summary>The distinct group ids that have at least one entity.</summary>
    Task<IReadOnlyList<string>> GetEntityGroupIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes communities (and their membership edges) in these groups, or everywhere if
    /// <see langword="null"/> or empty.</summary>
    Task RemoveCommunitiesAsync(IReadOnlyList<string>? groupIds = null, CancellationToken cancellationToken = default);

    /// <summary>Entities mentioned by any of these episodes, without duplicates.</summary>
    Task<IReadOnlyList<EntityNode>> GetMentionedNodesAsync(
        IReadOnlyList<EpisodicNode> episodes,
        CancellationToken cancellationToken = default);

    /// <summary>Communities with any of these entities as a member, without duplicates.</summary>
    Task<IReadOnlyList<CommunityNode>> GetCommunitiesByNodesAsync(
        IReadOnlyList<EntityNode> nodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Communities whose members are joined to this entity by a fact (in either direction), one entry per (fact,
    /// membership edge) pair, as upstream's
    /// <c>MATCH (c:Community)-[:HAS_MEMBER]-&gt;(m:Entity)-[:RELATES_TO]-(n:Entity {uuid: $entity_uuid})</c>: a community
    /// appears once for every fact between the entity and one of its members. Core picks the most frequent. Used by
    /// upstream's <c>determine_entity_community</c>.
    /// </summary>
    Task<IReadOnlyList<CommunityNode>> GetNeighborCommunitiesAsync(
        string entityUuid,
        CancellationToken cancellationToken = default);

    /// <summary>How many episode-to-entity links point at each entity (upstream's <c>count(*)</c> over
    /// <c>MENTIONS</c>). Every requested id is a key; ids with no mentions (or unknown ids) map to 0.</summary>
    Task<IReadOnlyDictionary<string, int>> CountEpisodeMentionsAsync(
        IReadOnlyList<string> entityUuids,
        CancellationToken cancellationToken = default);
}

/// <summary>A neighbouring entity and the number of facts joining it to the entity in question.</summary>
/// <param name="NodeUuid">The neighbour's id.</param>
/// <param name="EdgeCount">How many facts join them.</param>
public readonly record struct Neighbor(string NodeUuid, int EdgeCount);
