// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search_config.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Models;

namespace GraphitiNet.Search;

/// <summary>How facts (entity edges) are found. Upstream values: <c>cosine_similarity</c>, <c>bm25</c>,
/// <c>breadth_first_search</c>.</summary>
public enum EdgeSearchMethod
{
    /// <summary>Vector search on the fact embedding.</summary>
    CosineSimilarity,

    /// <summary>Full-text search.</summary>
    Bm25,

    /// <summary>Breadth-first search from origin nodes.</summary>
    Bfs,
}

/// <summary>How entities are found. Upstream values: <c>cosine_similarity</c>, <c>bm25</c>,
/// <c>breadth_first_search</c>.</summary>
public enum NodeSearchMethod
{
    /// <summary>Vector search on the name embedding.</summary>
    CosineSimilarity,

    /// <summary>Full-text search.</summary>
    Bm25,

    /// <summary>Breadth-first search from origin nodes.</summary>
    Bfs,
}

/// <summary>How episodes are found. Upstream value: <c>bm25</c>.</summary>
public enum EpisodeSearchMethod
{
    /// <summary>Full-text search.</summary>
    Bm25,
}

/// <summary>How communities are found. Upstream values: <c>cosine_similarity</c>, <c>bm25</c>.</summary>
public enum CommunitySearchMethod
{
    /// <summary>Vector search on the name embedding.</summary>
    CosineSimilarity,

    /// <summary>Full-text search.</summary>
    Bm25,
}

/// <summary>How fact results are reranked.</summary>
public enum EdgeReranker
{
    /// <summary>Reciprocal rank fusion of the search methods' rankings (<c>reciprocal_rank_fusion</c>).</summary>
    Rrf,

    /// <summary>Facts whose source entity is next to the centre node first (<c>node_distance</c>).</summary>
    NodeDistance,

    /// <summary>Facts stated by the most episodes first (<c>episode_mentions</c>).</summary>
    EpisodeMentions,

    /// <summary>Maximal marginal relevance (<c>mmr</c>).</summary>
    Mmr,

    /// <summary>A cross-encoder scores each fact against the query (<c>cross_encoder</c>).</summary>
    CrossEncoder,
}

/// <summary>How entity results are reranked.</summary>
public enum NodeReranker
{
    /// <summary>Reciprocal rank fusion of the search methods' rankings (<c>reciprocal_rank_fusion</c>).</summary>
    Rrf,

    /// <summary>Entities next to the centre node first (<c>node_distance</c>).</summary>
    NodeDistance,

    /// <summary>Entities mentioned by the most episodes first (<c>episode_mentions</c>).</summary>
    EpisodeMentions,

    /// <summary>Maximal marginal relevance (<c>mmr</c>).</summary>
    Mmr,

    /// <summary>A cross-encoder scores each entity name against the query (<c>cross_encoder</c>).</summary>
    CrossEncoder,
}

/// <summary>How episode results are reranked.</summary>
public enum EpisodeReranker
{
    /// <summary>Reciprocal rank fusion (<c>reciprocal_rank_fusion</c>).</summary>
    Rrf,

    /// <summary>A cross-encoder scores each episode's content against the query (<c>cross_encoder</c>).</summary>
    CrossEncoder,
}

/// <summary>How community results are reranked.</summary>
public enum CommunityReranker
{
    /// <summary>Reciprocal rank fusion (<c>reciprocal_rank_fusion</c>).</summary>
    Rrf,

    /// <summary>Maximal marginal relevance (<c>mmr</c>).</summary>
    Mmr,

    /// <summary>A cross-encoder scores each community name against the query (<c>cross_encoder</c>).</summary>
    CrossEncoder,
}

/// <summary>Settings for searching facts.</summary>
public sealed record EdgeSearchConfig
{
    /// <summary>The search methods to run; their results are fused by the reranker.</summary>
    public required IReadOnlyList<EdgeSearchMethod> SearchMethods { get; init; }

    /// <summary>The reranker.</summary>
    public EdgeReranker Reranker { get; init; } = EdgeReranker.Rrf;

    /// <summary>Minimum cosine similarity for vector search.</summary>
    public double SimMinScore { get; init; } = SearchDefaults.DefaultMinScore;

    /// <summary>Relevance-versus-diversity weight for MMR: 1 is pure relevance.</summary>
    public double MmrLambda { get; init; } = SearchDefaults.DefaultMmrLambda;

    /// <summary>The breadth-first search hop limit.</summary>
    public int BfsMaxDepth { get; init; } = SearchDefaults.MaxSearchDepth;
}

/// <summary>Settings for searching entities.</summary>
public sealed record NodeSearchConfig
{
    /// <summary>The search methods to run; their results are fused by the reranker.</summary>
    public required IReadOnlyList<NodeSearchMethod> SearchMethods { get; init; }

    /// <summary>The reranker.</summary>
    public NodeReranker Reranker { get; init; } = NodeReranker.Rrf;

    /// <summary>Minimum cosine similarity for vector search.</summary>
    public double SimMinScore { get; init; } = SearchDefaults.DefaultMinScore;

    /// <summary>Relevance-versus-diversity weight for MMR: 1 is pure relevance.</summary>
    public double MmrLambda { get; init; } = SearchDefaults.DefaultMmrLambda;

    /// <summary>The breadth-first search hop limit.</summary>
    public int BfsMaxDepth { get; init; } = SearchDefaults.MaxSearchDepth;
}

/// <summary>Settings for searching episodes.</summary>
public sealed record EpisodeSearchConfig
{
    /// <summary>The search methods to run. Upstream always runs full-text search for episodes, whatever this
    /// holds.</summary>
    public required IReadOnlyList<EpisodeSearchMethod> SearchMethods { get; init; }

    /// <summary>The reranker.</summary>
    public EpisodeReranker Reranker { get; init; } = EpisodeReranker.Rrf;

    /// <summary>Minimum cosine similarity for vector search (unused for episodes, kept as upstream).</summary>
    public double SimMinScore { get; init; } = SearchDefaults.DefaultMinScore;

    /// <summary>MMR weight (unused for episodes, kept as upstream).</summary>
    public double MmrLambda { get; init; } = SearchDefaults.DefaultMmrLambda;

    /// <summary>Hop limit (unused for episodes, kept as upstream).</summary>
    public int BfsMaxDepth { get; init; } = SearchDefaults.MaxSearchDepth;
}

/// <summary>Settings for searching communities.</summary>
public sealed record CommunitySearchConfig
{
    /// <summary>The search methods. Upstream always runs both full-text and vector search for communities, whatever
    /// this holds; it only decides whether the query is embedded.</summary>
    public required IReadOnlyList<CommunitySearchMethod> SearchMethods { get; init; }

    /// <summary>The reranker.</summary>
    public CommunityReranker Reranker { get; init; } = CommunityReranker.Rrf;

    /// <summary>Minimum cosine similarity for vector search.</summary>
    public double SimMinScore { get; init; } = SearchDefaults.DefaultMinScore;

    /// <summary>Relevance-versus-diversity weight for MMR: 1 is pure relevance.</summary>
    public double MmrLambda { get; init; } = SearchDefaults.DefaultMmrLambda;

    /// <summary>Hop limit (unused for communities, kept as upstream).</summary>
    public int BfsMaxDepth { get; init; } = SearchDefaults.MaxSearchDepth;
}

/// <summary>
/// What to search and how. Each scope (facts, entities, episodes, communities) is searched only when its config is
/// set. Records are immutable: use <c>with</c> to change a recipe, for example
/// <c>SearchConfigRecipes.EdgeHybridSearchRrf with { Limit = 5 }</c>.
/// </summary>
public sealed record SearchConfig
{
    /// <summary>Fact search, or <see langword="null"/> to skip facts.</summary>
    public EdgeSearchConfig? EdgeConfig { get; init; }

    /// <summary>Entity search, or <see langword="null"/> to skip entities.</summary>
    public NodeSearchConfig? NodeConfig { get; init; }

    /// <summary>Episode search, or <see langword="null"/> to skip episodes.</summary>
    public EpisodeSearchConfig? EpisodeConfig { get; init; }

    /// <summary>Community search, or <see langword="null"/> to skip communities.</summary>
    public CommunitySearchConfig? CommunityConfig { get; init; }

    /// <summary>The maximum number of results per scope.</summary>
    public int Limit { get; init; } = SearchDefaults.DefaultSearchLimit;

    /// <summary>Results the reranker scores below this are dropped.</summary>
    public double RerankerMinScore { get; init; }
}

/// <summary>Search results per scope, each list best first, with the reranker's scores.</summary>
/// <remarks>As upstream, the score lists are cut to the same limit as the results but are not guaranteed to line up
/// with them one to one for every reranker (for example, the node-distance edge reranker scores source nodes).</remarks>
public sealed record SearchResults
{
    /// <summary>Facts found.</summary>
    public IReadOnlyList<EntityEdge> Edges { get; init; } = [];

    /// <summary>Reranker scores for <see cref="Edges"/>.</summary>
    public IReadOnlyList<double> EdgeRerankerScores { get; init; } = [];

    /// <summary>Entities found.</summary>
    public IReadOnlyList<EntityNode> Nodes { get; init; } = [];

    /// <summary>Reranker scores for <see cref="Nodes"/>.</summary>
    public IReadOnlyList<double> NodeRerankerScores { get; init; } = [];

    /// <summary>
    /// For each entity in <see cref="Nodes"/>, the ids of the readable episodes that mention it. Filled only when the
    /// search ran with an <see cref="IEpisodeAccessFilter"/> (graphiti-net extension); otherwise
    /// <see langword="null"/>, and <see cref="SearchUtils.GetMentioningEpisodeUuidsAsync"/> fetches them. Facts carry
    /// theirs in <see cref="EntityEdge.Episodes"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? NodeEpisodeUuids { get; init; }

    /// <summary>Episodes found.</summary>
    public IReadOnlyList<EpisodicNode> Episodes { get; init; } = [];

    /// <summary>Reranker scores for <see cref="Episodes"/>.</summary>
    public IReadOnlyList<double> EpisodeRerankerScores { get; init; } = [];

    /// <summary>Communities found.</summary>
    public IReadOnlyList<CommunityNode> Communities { get; init; } = [];

    /// <summary>Reranker scores for <see cref="Communities"/>.</summary>
    public IReadOnlyList<double> CommunityRerankerScores { get; init; } = [];

    /// <summary>Concatenates several results, scope by scope, in order. No deduplication, as upstream.</summary>
    /// <param name="resultsList">The results to merge.</param>
    /// <returns>One result holding everything; empty if the list is empty.</returns>
    public static SearchResults Merge(IEnumerable<SearchResults> resultsList)
    {
        ArgumentNullException.ThrowIfNull(resultsList);

        var edges = new List<EntityEdge>();
        var edgeScores = new List<double>();
        var nodes = new List<EntityNode>();
        var nodeScores = new List<double>();
        var episodes = new List<EpisodicNode>();
        var episodeScores = new List<double>();
        var communities = new List<CommunityNode>();
        var communityScores = new List<double>();
        Dictionary<string, IReadOnlyList<string>>? nodeEpisodeUuids = null;

        foreach (var result in resultsList)
        {
            if (result.NodeEpisodeUuids is not null)
            {
                nodeEpisodeUuids ??= new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                foreach (var (uuid, episodeUuids) in result.NodeEpisodeUuids)
                {
                    nodeEpisodeUuids[uuid] = episodeUuids;
                }
            }

            edges.AddRange(result.Edges);
            edgeScores.AddRange(result.EdgeRerankerScores);
            nodes.AddRange(result.Nodes);
            nodeScores.AddRange(result.NodeRerankerScores);
            episodes.AddRange(result.Episodes);
            episodeScores.AddRange(result.EpisodeRerankerScores);
            communities.AddRange(result.Communities);
            communityScores.AddRange(result.CommunityRerankerScores);
        }

        return new SearchResults
        {
            Edges = edges,
            EdgeRerankerScores = edgeScores,
            Nodes = nodes,
            NodeRerankerScores = nodeScores,
            NodeEpisodeUuids = nodeEpisodeUuids,
            Episodes = episodes,
            EpisodeRerankerScores = episodeScores,
            Communities = communities,
            CommunityRerankerScores = communityScores,
        };
    }
}

/// <summary>Upstream's string values for the search enums, as used in trace attributes.</summary>
internal static class SearchEnumValues
{
    public static string Value(EdgeSearchMethod method) => method switch
    {
        EdgeSearchMethod.CosineSimilarity => "cosine_similarity",
        EdgeSearchMethod.Bm25 => "bm25",
        EdgeSearchMethod.Bfs => "breadth_first_search",
        _ => method.ToString(),
    };

    public static string Value(NodeSearchMethod method) => method switch
    {
        NodeSearchMethod.CosineSimilarity => "cosine_similarity",
        NodeSearchMethod.Bm25 => "bm25",
        NodeSearchMethod.Bfs => "breadth_first_search",
        _ => method.ToString(),
    };

    public static string Value(EpisodeSearchMethod method) => method switch
    {
        EpisodeSearchMethod.Bm25 => "bm25",
        _ => method.ToString(),
    };

    public static string Value(CommunitySearchMethod method) => method switch
    {
        CommunitySearchMethod.CosineSimilarity => "cosine_similarity",
        CommunitySearchMethod.Bm25 => "bm25",
        _ => method.ToString(),
    };

    public static string Value(EdgeReranker reranker) => reranker switch
    {
        EdgeReranker.Rrf => "reciprocal_rank_fusion",
        EdgeReranker.NodeDistance => "node_distance",
        EdgeReranker.EpisodeMentions => "episode_mentions",
        EdgeReranker.Mmr => "mmr",
        EdgeReranker.CrossEncoder => "cross_encoder",
        _ => reranker.ToString(),
    };

    public static string Value(NodeReranker reranker) => reranker switch
    {
        NodeReranker.Rrf => "reciprocal_rank_fusion",
        NodeReranker.NodeDistance => "node_distance",
        NodeReranker.EpisodeMentions => "episode_mentions",
        NodeReranker.Mmr => "mmr",
        NodeReranker.CrossEncoder => "cross_encoder",
        _ => reranker.ToString(),
    };

    public static string Value(EpisodeReranker reranker) => reranker switch
    {
        EpisodeReranker.Rrf => "reciprocal_rank_fusion",
        EpisodeReranker.CrossEncoder => "cross_encoder",
        _ => reranker.ToString(),
    };

    public static string Value(CommunityReranker reranker) => reranker switch
    {
        CommunityReranker.Rrf => "reciprocal_rank_fusion",
        CommunityReranker.Mmr => "mmr",
        CommunityReranker.CrossEncoder => "cross_encoder",
        _ => reranker.ToString(),
    };
}
