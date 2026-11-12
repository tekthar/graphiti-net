// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/search/search_config_recipes.py (Graphiti v0.31.2) and changed for .NET.

namespace GraphitiNet.Search;

/// <summary>
/// Ready-made search configurations, as upstream's <c>search_config_recipes.py</c>.
/// </summary>
/// <remarks>
/// Each property returns a new <see cref="SearchConfig"/> on every read, and the records are immutable, so changing
/// a recipe (<c>SearchConfigRecipes.EdgeHybridSearchRrf with { Limit = 5 }</c>) never affects other callers. Upstream
/// mutates the shared module-level recipe in <c>Graphiti.search</c> (<c>search_config.limit = num_results</c>); that
/// leak is not reproduced.
/// </remarks>
public static class SearchConfigRecipes
{
    /// <summary>Hybrid search with RRF reranking over facts, entities, episodes and communities
    /// (<c>COMBINED_HYBRID_SEARCH_RRF</c>).</summary>
    public static SearchConfig CombinedHybridSearchRrf => new()
    {
        EdgeConfig = new EdgeSearchConfig
        {
            SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity],
            Reranker = EdgeReranker.Rrf,
        },
        NodeConfig = new NodeSearchConfig
        {
            SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.CosineSimilarity],
            Reranker = NodeReranker.Rrf,
        },
        EpisodeConfig = new EpisodeSearchConfig
        {
            SearchMethods = [EpisodeSearchMethod.Bm25],
            Reranker = EpisodeReranker.Rrf,
        },
        CommunityConfig = new CommunitySearchConfig
        {
            SearchMethods = [CommunitySearchMethod.Bm25, CommunitySearchMethod.CosineSimilarity],
            Reranker = CommunityReranker.Rrf,
        },
    };

    /// <summary>Hybrid search with MMR reranking (lambda 1) over facts, entities and communities, and RRF over
    /// episodes (<c>COMBINED_HYBRID_SEARCH_MMR</c>).</summary>
    public static SearchConfig CombinedHybridSearchMmr => new()
    {
        EdgeConfig = new EdgeSearchConfig
        {
            SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity],
            Reranker = EdgeReranker.Mmr,
            MmrLambda = 1,
        },
        NodeConfig = new NodeSearchConfig
        {
            SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.CosineSimilarity],
            Reranker = NodeReranker.Mmr,
            MmrLambda = 1,
        },
        EpisodeConfig = new EpisodeSearchConfig
        {
            SearchMethods = [EpisodeSearchMethod.Bm25],
            Reranker = EpisodeReranker.Rrf,
        },
        CommunityConfig = new CommunitySearchConfig
        {
            SearchMethods = [CommunitySearchMethod.Bm25, CommunitySearchMethod.CosineSimilarity],
            Reranker = CommunityReranker.Mmr,
            MmrLambda = 1,
        },
    };

    /// <summary>Full-text, similarity and breadth-first search with cross-encoder reranking over every scope
    /// (<c>COMBINED_HYBRID_SEARCH_CROSS_ENCODER</c>).</summary>
    public static SearchConfig CombinedHybridSearchCrossEncoder => new()
    {
        EdgeConfig = new EdgeSearchConfig
        {
            SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity, EdgeSearchMethod.Bfs],
            Reranker = EdgeReranker.CrossEncoder,
        },
        NodeConfig = new NodeSearchConfig
        {
            SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.CosineSimilarity, NodeSearchMethod.Bfs],
            Reranker = NodeReranker.CrossEncoder,
        },
        EpisodeConfig = new EpisodeSearchConfig
        {
            SearchMethods = [EpisodeSearchMethod.Bm25],
            Reranker = EpisodeReranker.CrossEncoder,
        },
        CommunityConfig = new CommunitySearchConfig
        {
            SearchMethods = [CommunitySearchMethod.Bm25, CommunitySearchMethod.CosineSimilarity],
            Reranker = CommunityReranker.CrossEncoder,
        },
    };

    /// <summary>Hybrid fact search with RRF reranking (<c>EDGE_HYBRID_SEARCH_RRF</c>).</summary>
    public static SearchConfig EdgeHybridSearchRrf => EdgeHybrid(EdgeReranker.Rrf);

    /// <summary>Hybrid fact search with MMR reranking (<c>EDGE_HYBRID_SEARCH_MMR</c>).</summary>
    public static SearchConfig EdgeHybridSearchMmr => EdgeHybrid(EdgeReranker.Mmr);

    /// <summary>Hybrid fact search with node-distance reranking; needs a centre node
    /// (<c>EDGE_HYBRID_SEARCH_NODE_DISTANCE</c>).</summary>
    public static SearchConfig EdgeHybridSearchNodeDistance => EdgeHybrid(EdgeReranker.NodeDistance);

    /// <summary>Hybrid fact search with episode-mentions reranking
    /// (<c>EDGE_HYBRID_SEARCH_EPISODE_MENTIONS</c>).</summary>
    public static SearchConfig EdgeHybridSearchEpisodeMentions => EdgeHybrid(EdgeReranker.EpisodeMentions);

    /// <summary>Full-text, similarity and breadth-first fact search with cross-encoder reranking
    /// (<c>EDGE_HYBRID_SEARCH_CROSS_ENCODER</c>).</summary>
    public static SearchConfig EdgeHybridSearchCrossEncoder => new()
    {
        EdgeConfig = new EdgeSearchConfig
        {
            SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity, EdgeSearchMethod.Bfs],
            Reranker = EdgeReranker.CrossEncoder,
        },
        Limit = 10,
    };

    /// <summary>Hybrid entity search with RRF reranking (<c>NODE_HYBRID_SEARCH_RRF</c>).</summary>
    public static SearchConfig NodeHybridSearchRrf => NodeHybrid(NodeReranker.Rrf);

    /// <summary>Hybrid entity search with MMR reranking (<c>NODE_HYBRID_SEARCH_MMR</c>).</summary>
    public static SearchConfig NodeHybridSearchMmr => NodeHybrid(NodeReranker.Mmr);

    /// <summary>Hybrid entity search with node-distance reranking; needs a centre node
    /// (<c>NODE_HYBRID_SEARCH_NODE_DISTANCE</c>).</summary>
    public static SearchConfig NodeHybridSearchNodeDistance => NodeHybrid(NodeReranker.NodeDistance);

    /// <summary>Hybrid entity search with episode-mentions reranking
    /// (<c>NODE_HYBRID_SEARCH_EPISODE_MENTIONS</c>).</summary>
    public static SearchConfig NodeHybridSearchEpisodeMentions => NodeHybrid(NodeReranker.EpisodeMentions);

    /// <summary>Full-text, similarity and breadth-first entity search with cross-encoder reranking
    /// (<c>NODE_HYBRID_SEARCH_CROSS_ENCODER</c>).</summary>
    public static SearchConfig NodeHybridSearchCrossEncoder => new()
    {
        NodeConfig = new NodeSearchConfig
        {
            SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.CosineSimilarity, NodeSearchMethod.Bfs],
            Reranker = NodeReranker.CrossEncoder,
        },
        Limit = 10,
    };

    /// <summary>Hybrid community search with RRF reranking (<c>COMMUNITY_HYBRID_SEARCH_RRF</c>).</summary>
    public static SearchConfig CommunityHybridSearchRrf => CommunityHybrid(CommunityReranker.Rrf);

    /// <summary>Hybrid community search with MMR reranking (<c>COMMUNITY_HYBRID_SEARCH_MMR</c>).</summary>
    public static SearchConfig CommunityHybridSearchMmr => CommunityHybrid(CommunityReranker.Mmr);

    /// <summary>Hybrid community search with cross-encoder reranking, limit 3
    /// (<c>COMMUNITY_HYBRID_SEARCH_CROSS_ENCODER</c>).</summary>
    public static SearchConfig CommunityHybridSearchCrossEncoder =>
        CommunityHybrid(CommunityReranker.CrossEncoder) with { Limit = 3 };

    private static SearchConfig EdgeHybrid(EdgeReranker reranker) => new()
    {
        EdgeConfig = new EdgeSearchConfig
        {
            SearchMethods = [EdgeSearchMethod.Bm25, EdgeSearchMethod.CosineSimilarity],
            Reranker = reranker,
        },
    };

    private static SearchConfig NodeHybrid(NodeReranker reranker) => new()
    {
        NodeConfig = new NodeSearchConfig
        {
            SearchMethods = [NodeSearchMethod.Bm25, NodeSearchMethod.CosineSimilarity],
            Reranker = reranker,
        },
    };

    private static SearchConfig CommunityHybrid(CommunityReranker reranker) => new()
    {
        CommunityConfig = new CommunitySearchConfig
        {
            SearchMethods = [CommunitySearchMethod.Bm25, CommunitySearchMethod.CosineSimilarity],
            Reranker = reranker,
        },
    };
}
