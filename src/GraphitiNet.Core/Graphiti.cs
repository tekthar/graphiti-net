// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/graphiti.py (Graphiti v0.31.2) and changed for .NET.

using System.Diagnostics;
using System.Text;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Maintenance;
using GraphitiNet.Models;
using GraphitiNet.Namespaces;
using GraphitiNet.Prompts;
using GraphitiNet.Search;
using GraphitiNet.Telemetry;
using GraphitiNet.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet;

/// <summary>
/// The entry point: adds episodes to a temporal knowledge graph and searches it. Upstream's <c>Graphiti</c> class,
/// with .NET naming (<c>add_episode</c> is <see cref="AddEpisodeAsync"/>, parameters are camelCase, every method
/// takes a <see cref="CancellationToken"/>). See <c>docs/api-compatibility.md</c> for the full mapping.
/// </summary>
/// <remarks>
/// <para>Add episodes of one group one after another: each reads the graph the previous one wrote. By default
/// graphiti-net enforces this within the process (see <see cref="IGroupLock"/>).</para>
/// <para>Dispose the instance (<c>await using</c>) or call <see cref="CloseAsync"/> to close the driver.</para>
/// </remarks>
public sealed partial class Graphiti : IAsyncDisposable
{
    /// <summary>Upstream <c>EPISODE_WINDOW_LEN</c>: previous episodes <see cref="RetrieveEpisodesAsync"/> returns
    /// by default.</summary>
    public const int EpisodeWindowLen = 3;

    /// <summary>Upstream <c>SAGA_SUMMARY_MAX_EPISODE_CHARS</c>: characters of episode text one saga summary run
    /// packs.</summary>
    public const int SagaSummaryMaxEpisodeChars = 100_000;

    /// <summary>Upstream <c>SAGA_SUMMARY_MAX_PROMPT_BYTES</c>: the UTF-8 size cap of a saga summary prompt.</summary>
    public const int SagaSummaryMaxPromptBytes = 128 * 1024;

    /// <summary>Upstream <c>MAX_SAGA_EPISODES_FOR_SUMMARY</c>: episodes one saga summary run fetches.</summary>
    public const int MaxSagaEpisodesForSummary = 20;

    /// <summary>Upstream <c>SUMMARY_REBUILD_BATCH_SIZE</c>: episodes per LLM call when
    /// <see cref="RemoveEpisodeAsync"/> rebuilds an entity summary.</summary>
    public const int SummaryRebuildBatchSize = 10;

    /// <summary>Upstream <c>SAGA_SUMMARY_MAX_TOKENS</c>: output tokens requested for a saga summary.</summary>
    public const int SagaSummaryMaxTokens = 8192;

    private const string SagaSummarySchemaWrapper = "\n\nRespond with a JSON object in the following format:\n\n";

    /// <summary>
    /// Creates a Graphiti instance. Upstream's <c>Graphiti(...)</c> constructor; the graph driver is required, since
    /// graphiti-net has no default database (use <see cref="CreateInMemory"/> for demos and tests).
    /// </summary>
    /// <param name="graphDriver">Storage.</param>
    /// <param name="llmClient">The LLM; defaults to <see cref="OpenAIClient"/>, which needs <c>OPENAI_API_KEY</c>.</param>
    /// <param name="embedder">The embedder; defaults to <see cref="OpenAIEmbedder"/>.</param>
    /// <param name="crossEncoder">The reranker; defaults to <see cref="OpenAIRerankerClient"/>.</param>
    /// <param name="storeRawEpisodeContent">Whether to keep episode text after processing. With
    /// <see langword="false"/>, <see cref="AddEpisodeAsync"/> stores episodes with empty content.</param>
    /// <param name="maxCoroutines">The concurrency cap per gather (upstream <c>max_coroutines</c>);
    /// <see langword="null"/> uses <c>SEMAPHORE_LIMIT</c> or 20.</param>
    /// <param name="promptLibrary">Prompt overrides (upstream <c>prompt_library</c>); defaults to
    /// <see cref="PromptLibrary.Default"/>.</param>
    /// <param name="promptRoutes">Route single prompts, by name, to other LLM clients. Replaces upstream's
    /// <c>llm_runtime</c> routing.</param>
    /// <param name="groupLock">Serialises writes per group; defaults to <see cref="InProcessGroupLock.Shared"/>.
    /// Pass <see cref="NoGroupLock.Instance"/> for upstream's behaviour (no serialisation). A graphiti-net
    /// addition.</param>
    /// <param name="modelCallGate">Asked before each model call; can refuse it. A graphiti-net addition.</param>
    /// <param name="logger">Where to log; defaults to no logging.</param>
    /// <param name="modelCallRetry">Retries transient provider errors (HTTP 429, 5xx, timeouts) of each LLM,
    /// embedding and rerank call with exponential backoff; <see langword="null"/> (the default) retries nothing
    /// beyond the clients' own retries, as upstream. A graphiti-net addition.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modelCallRetry"/> has a setting out of
    /// range.</exception>
    public Graphiti(
        IGraphDriver graphDriver,
        LLMClient? llmClient = null,
        EmbedderClient? embedder = null,
        CrossEncoderClient? crossEncoder = null,
        bool storeRawEpisodeContent = true,
        int? maxCoroutines = null,
        PromptLibrary? promptLibrary = null,
        IReadOnlyDictionary<string, LLMClient>? promptRoutes = null,
        IGroupLock? groupLock = null,
        IModelCallGate? modelCallGate = null,
        ILogger? logger = null,
        ModelCallRetryOptions? modelCallRetry = null)
    {
        ArgumentNullException.ThrowIfNull(graphDriver);
        modelCallRetry?.Validate();
        Driver = graphDriver;
        StoreRawEpisodeContent = storeRawEpisodeContent;
        MaxCoroutines = maxCoroutines;
        LlmClient = llmClient ?? new OpenAIClient();
        Embedder = embedder ?? new OpenAIEmbedder();
        CrossEncoder = crossEncoder ?? new OpenAIRerankerClient();
        PromptLibrary = promptLibrary ?? PromptLibrary.Default;
        GroupLock = groupLock ?? InProcessGroupLock.Shared;
        ModelCallGate = modelCallGate;
        ModelCallRetry = modelCallRetry;
        Logger = logger ?? NullLogger.Instance;

        // The gate wraps the retries, so it is asked once per logical call.
        EmbedderClient embedderInUse = modelCallRetry is null
            ? Embedder
            : new RetryingEmbedderClient(Embedder, modelCallRetry, Logger);
        CrossEncoderClient crossEncoderInUse = modelCallRetry is null
            ? CrossEncoder
            : new RetryingCrossEncoderClient(CrossEncoder, modelCallRetry, Logger);
        Clients = new GraphitiClients
        {
            Driver = Driver,
            LlmClient = LlmClient,
            Embedder = modelCallGate is null ? embedderInUse : new GatedEmbedderClient(embedderInUse),
            CrossEncoder = modelCallGate is null ? crossEncoderInUse : new GatedCrossEncoderClient(crossEncoderInUse),
            PromptLibrary = PromptLibrary,
            PromptRoutes = promptRoutes,
            MaxCoroutines = maxCoroutines,
            ModelCallRetry = modelCallRetry,

            // Retries of LLM calls are logged here; without retries the pipeline logs nothing, as in 0.1.0.
            Logger = modelCallRetry is null ? NullLogger.Instance : Logger,
        };

        Nodes = new NodeNamespace(Driver, Clients.Embedder);
        Edges = new EdgeNamespace(Driver, Clients.Embedder);
    }

    /// <summary>Storage (upstream <c>driver</c>).</summary>
    public IGraphDriver Driver { get; }

    /// <summary>The default LLM (upstream <c>llm_client</c>).</summary>
    public LLMClient LlmClient { get; }

    /// <summary>The embedder (upstream <c>embedder</c>).</summary>
    public EmbedderClient Embedder { get; }

    /// <summary>The reranker (upstream <c>cross_encoder</c>).</summary>
    public CrossEncoderClient CrossEncoder { get; }

    /// <summary>The prompts in use (upstream <c>prompt_library</c>).</summary>
    public PromptLibrary PromptLibrary { get; }

    /// <summary>The services bundle the pipeline uses (upstream <c>clients</c>). With a model call gate or a retry
    /// policy, its embedder and cross-encoder are gate-checking and retrying wrappers around <see cref="Embedder"/>
    /// and <see cref="CrossEncoder"/>.</summary>
    public GraphitiClients Clients { get; }

    /// <summary>Whether episode text is kept after processing (upstream <c>store_raw_episode_content</c>).</summary>
    public bool StoreRawEpisodeContent { get; }

    /// <summary>The concurrency cap per gather (upstream <c>max_coroutines</c>).</summary>
    public int? MaxCoroutines { get; }

    /// <summary>The LLM client's token usage per prompt (upstream <c>token_tracker</c>). Shared by every call; see
    /// <see cref="AddEpisodeResults.TokenUsage"/> for one call's usage.</summary>
    public TokenUsageTracker TokenTracker => LlmClient.TokenTracker;

    /// <summary>Node storage operations (upstream <c>graphiti.nodes</c>).</summary>
    public NodeNamespace Nodes { get; }

    /// <summary>Edge storage operations (upstream <c>graphiti.edges</c>).</summary>
    public EdgeNamespace Edges { get; }

    /// <summary>The per-group write lock. A graphiti-net addition.</summary>
    public IGroupLock GroupLock { get; }

    /// <summary>The model call gate, if any. A graphiti-net addition.</summary>
    public IModelCallGate? ModelCallGate { get; }

    /// <summary>The retry policy around each model call, if any. A graphiti-net addition.</summary>
    public ModelCallRetryOptions? ModelCallRetry { get; }

    /// <summary>Where the instance logs.</summary>
    public ILogger Logger { get; }

    // Upstream's module-level budgets, which its tests monkeypatch; tests here change them per instance.
    internal int SagaSummaryEpisodeCharBudget { get; set; } = SagaSummaryMaxEpisodeChars;

    internal int SagaSummaryPromptByteBudget { get; set; } = SagaSummaryMaxPromptBytes;

    internal int SummaryRebuildBatch { get; set; } = SummaryRebuildBatchSize;

    /// <summary>
    /// Creates a Graphiti instance over a new <see cref="InMemoryGraphDriver"/>: nothing to install, for demos,
    /// tests and small apps. The graph lives in memory (see <see cref="InMemoryGraphDriver.SaveToFileAsync"/> to
    /// keep it).
    /// </summary>
    /// <param name="llmClient">The LLM; defaults to <see cref="OpenAIClient"/>.</param>
    /// <param name="embedder">The embedder; defaults to <see cref="OpenAIEmbedder"/>.</param>
    /// <param name="crossEncoder">The reranker; defaults to <see cref="OpenAIRerankerClient"/>.</param>
    /// <param name="storeRawEpisodeContent">Whether to keep episode text after processing.</param>
    /// <param name="maxCoroutines">The concurrency cap per gather.</param>
    /// <param name="promptLibrary">Prompt overrides.</param>
    /// <param name="promptRoutes">Per-prompt LLM routes.</param>
    public static Graphiti CreateInMemory(
        LLMClient? llmClient = null,
        EmbedderClient? embedder = null,
        CrossEncoderClient? crossEncoder = null,
        bool storeRawEpisodeContent = true,
        int? maxCoroutines = null,
        PromptLibrary? promptLibrary = null,
        IReadOnlyDictionary<string, LLMClient>? promptRoutes = null) =>
        new(
            new InMemoryGraphDriver(),
            llmClient,
            embedder,
            crossEncoder,
            storeRawEpisodeContent,
            maxCoroutines,
            promptLibrary,
            promptRoutes);

    /// <summary>
    /// The default group id for a storage provider (upstream <c>get_default_group_id</c>): <c>_</c> for FalkorDB,
    /// empty for everything else.
    /// </summary>
    /// <param name="provider">The driver's <see cref="IGraphDriver.Provider"/>.</param>
    public static string GetDefaultGroupId(string provider) =>
        string.Equals(provider, "falkordb", StringComparison.Ordinal) ? "_" : "";

    /// <summary>Closes the driver (upstream <c>close</c>).</summary>
    public async Task CloseAsync() => await Driver.DisposeAsync().ConfigureAwait(false);

    /// <summary>Closes the driver.</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>Creates the storage schema and indexes (upstream <c>build_indices_and_constraints</c>).</summary>
    /// <param name="deleteExisting">Drop existing indexes first.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task BuildIndicesAndConstraintsAsync(
        bool deleteExisting = false,
        CancellationToken cancellationToken = default) =>
        Driver.GraphOps.BuildIndicesAndConstraintsAsync(deleteExisting, cancellationToken);

    /// <summary>
    /// The <paramref name="lastN"/> most recent episodes at or before <paramref name="referenceTime"/>, oldest first
    /// (upstream <c>retrieve_episodes</c>).
    /// </summary>
    /// <param name="referenceTime">Only episodes valid at or before this time.</param>
    /// <param name="lastN">How many episodes.</param>
    /// <param name="groupIds">The groups; <see langword="null"/> means every group.</param>
    /// <param name="source">Only episodes of this kind.</param>
    /// <param name="saga">Only episodes in the saga with this name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesAsync(
        DateTimeOffset referenceTime,
        int lastN = EpisodeWindowLen,
        IReadOnlyList<string>? groupIds = null,
        EpisodeType? source = null,
        string? saga = null,
        CancellationToken cancellationToken = default) =>
        RetrieveEpisodesWithDriverAsync(referenceTime, lastN, groupIds, source, saga, Driver, cancellationToken);

    /// <summary>
    /// Clears the graph: every node and edge in the groups (episodes, entities, facts, mentions, sagas, communities
    /// and the links between them), or the whole graph when <paramref name="groupIds"/> is <see langword="null"/>.
    /// Upstream's <c>clear_data</c> (a module function there). Waits for each group's write lock.
    /// </summary>
    /// <param name="groupIds">The groups; <see langword="null"/> clears everything, an empty list nothing.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task ClearDataAsync(
        IReadOnlyList<string>? groupIds = null,
        CancellationToken cancellationToken = default)
    {
        Validation.ValidateGroupIds(groupIds);

        // Clearing the whole graph waits for every group that holds data, so it can't race a running write.
        var lockedGroups = groupIds
                           ?? await Driver.GraphOps.GetEntityGroupIdsAsync(cancellationToken).ConfigureAwait(false);
        await using var groupLocks = await AcquireGroupLocksAsync(lockedGroups, cancellationToken)
            .ConfigureAwait(false);
        await Driver.GraphOps.ClearDataAsync(groupIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hybrid search for facts (upstream <c>search</c>): full-text and similarity search fused with RRF, or reranked
    /// by distance to <paramref name="centerNodeUuid"/> when given.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="centerNodeUuid">Rerank by graph distance to this entity.</param>
    /// <param name="groupIds">The groups to search; <see langword="null"/> means every group.</param>
    /// <param name="numResults">How many facts.</param>
    /// <param name="searchFilter">Filters; <see langword="null"/> means none.</param>
    /// <param name="episodeAccessFilter">Hides facts behind episodes the caller may not read, before the limit. A
    /// graphiti-net addition.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The facts, best first.</returns>
    /// <remarks>Upstream sets the limit on its shared recipe object, which leaks into later searches; here each call
    /// uses a fresh copy.</remarks>
    public async Task<IReadOnlyList<EntityEdge>> SearchAsync(
        string query,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? groupIds = null,
        int numResults = SearchDefaults.DefaultSearchLimit,
        SearchFilters? searchFilter = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        var config = (centerNodeUuid is null
            ? SearchConfigRecipes.EdgeHybridSearchRrf
            : SearchConfigRecipes.EdgeHybridSearchNodeDistance) with { Limit = numResults };

        var results = await SearchCoreAsync(
                query,
                config,
                groupIds,
                centerNodeUuid,
                null,
                searchFilter,
                null,
                episodeAccessFilter,
                cancellationToken)
            .ConfigureAwait(false);
        return results.Edges;
    }

    /// <summary>
    /// Configurable search over facts, entities, episodes and communities (upstream <c>search_</c>). Same as
    /// <see cref="SearchAdvancedAsync"/>.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="config">What to search and how; see <see cref="SearchConfigRecipes"/>.</param>
    /// <param name="groupIds">The groups to search; <see langword="null"/> means every group.</param>
    /// <param name="centerNodeUuid">The centre entity for node-distance reranking.</param>
    /// <param name="bfsOriginNodeUuids">Where breadth-first search starts.</param>
    /// <param name="searchFilter">Filters; <see langword="null"/> means none.</param>
    /// <param name="queryVector">A precomputed query embedding.</param>
    /// <param name="episodeAccessFilter">Hides results behind episodes the caller may not read, before the limit. A
    /// graphiti-net addition.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The results per scope, best first, with their reranker scores.</returns>
    public Task<SearchResults> SearchAsync(
        string query,
        SearchConfig config,
        IReadOnlyList<string>? groupIds = null,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? bfsOriginNodeUuids = null,
        SearchFilters? searchFilter = null,
        IReadOnlyList<float>? queryVector = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SearchCoreAsync(
            query,
            config,
            groupIds,
            centerNodeUuid,
            bfsOriginNodeUuids,
            searchFilter,
            queryVector,
            episodeAccessFilter,
            cancellationToken);
    }

    /// <summary>
    /// Configurable search (upstream <c>search_</c>), with upstream's default config
    /// <see cref="SearchConfigRecipes.CombinedHybridSearchCrossEncoder"/>.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="config">What to search and how; <see langword="null"/> means
    /// <see cref="SearchConfigRecipes.CombinedHybridSearchCrossEncoder"/>.</param>
    /// <param name="groupIds">The groups to search; <see langword="null"/> means every group.</param>
    /// <param name="centerNodeUuid">The centre entity for node-distance reranking.</param>
    /// <param name="bfsOriginNodeUuids">Where breadth-first search starts.</param>
    /// <param name="searchFilter">Filters; <see langword="null"/> means none.</param>
    /// <param name="queryVector">A precomputed query embedding.</param>
    /// <param name="episodeAccessFilter">Hides results behind episodes the caller may not read. A graphiti-net
    /// addition.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public Task<SearchResults> SearchAdvancedAsync(
        string query,
        SearchConfig? config = null,
        IReadOnlyList<string>? groupIds = null,
        string? centerNodeUuid = null,
        IReadOnlyList<string>? bfsOriginNodeUuids = null,
        SearchFilters? searchFilter = null,
        IReadOnlyList<float>? queryVector = null,
        IEpisodeAccessFilter? episodeAccessFilter = null,
        CancellationToken cancellationToken = default) =>
        SearchCoreAsync(
            query,
            config ?? SearchConfigRecipes.CombinedHybridSearchCrossEncoder,
            groupIds,
            centerNodeUuid,
            bfsOriginNodeUuids,
            searchFilter,
            queryVector,
            episodeAccessFilter,
            cancellationToken);

    /// <summary>The facts and mentioned entities of these episodes (upstream
    /// <c>get_nodes_and_edges_by_episode</c>).</summary>
    /// <param name="episodeUuids">The episodes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Results with <see cref="SearchResults.Edges"/> and <see cref="SearchResults.Nodes"/> set.</returns>
    public async Task<SearchResults> GetNodesAndEdgesByEpisodeAsync(
        IReadOnlyList<string> episodeUuids,
        CancellationToken cancellationToken = default)
    {
        var episodes = await Driver.EpisodeNodeOps.GetByUuidsAsync(episodeUuids, cancellationToken)
            .ConfigureAwait(false);

        var edgesList = await Concurrency.SemaphoreGatherAsync(
                episodes.Select<EpisodicNode, Func<Task<IReadOnlyList<EntityEdge>>>>(episode =>
                    () => Driver.EntityEdgeOps.GetByUuidsAsync(episode.EntityEdges, cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        var nodes = await SearchUtils.GetMentionedNodesAsync(Driver, episodes, cancellationToken).ConfigureAwait(false);
        return new SearchResults { Edges = edgesList.SelectMany(edges => edges).ToList(), Nodes = nodes };
    }

    /// <summary>
    /// Clusters the entities into communities and summarises each (upstream <c>build_communities</c>). Existing
    /// communities of the groups are removed first.
    /// </summary>
    /// <param name="groupIds">The groups; <see langword="null"/> means the whole graph.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The communities and their membership edges, saved.</returns>
    public async Task<(IReadOnlyList<CommunityNode> Communities, IReadOnlyList<CommunityEdge> CommunityEdges)>
        BuildCommunitiesAsync(
            IReadOnlyList<string>? groupIds = null,
            CancellationToken cancellationToken = default)
    {
        Validation.ValidateGroupIds(groupIds);

        // graphiti-net: a write like the others, so it cannot save summaries of text a concurrent strict removal is
        // erasing. Without group ids, the groups that hold entities now are locked.
        var lockedGroups = groupIds
                           ?? await Driver.GraphOps.GetEntityGroupIdsAsync(cancellationToken).ConfigureAwait(false);
        await using var groupLocks = await AcquireGroupLocksAsync(lockedGroups, cancellationToken)
            .ConfigureAwait(false);
        using var scope = BeginOperation("build_communities", groupIds is { Count: 1 } ? groupIds[0] : null);

        await CommunityOperations.RemoveCommunitiesAsync(Driver, groupIds, cancellationToken).ConfigureAwait(false);

        var (communityNodes, communityEdges) = await CommunityOperations
            .BuildCommunitiesAsync(Clients, groupIds, MaxCoroutines, cancellationToken)
            .ConfigureAwait(false);

        await Concurrency.SemaphoreGatherAsync(
                communityNodes.Select<CommunityNode, Func<Task>>(node => async () =>
                    node.NameEmbedding = await LLMUtils
                        .GenerateEmbeddingAsync(Clients.Embedder, node.Name, cancellationToken)
                        .ConfigureAwait(false)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        await Concurrency.SemaphoreGatherAsync(
                communityNodes.Select<CommunityNode, Func<Task>>(node =>
                    () => Driver.CommunityNodeOps.SaveAsync(node, cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);
        await Concurrency.SemaphoreGatherAsync(
                communityEdges.Select<CommunityEdge, Func<Task>>(edge =>
                    () => Driver.CommunityEdgeOps.SaveAsync(edge, cancellationToken)),
                MaxCoroutines,
                cancellationToken)
            .ConfigureAwait(false);

        return (communityNodes, communityEdges);
    }

    /// <summary>
    /// Incrementally summarises a saga from the episodes added since its last summary (upstream
    /// <c>summarize_saga</c>).
    /// </summary>
    /// <remarks>
    /// <para><see cref="SagaNode.LastSummarizedAt"/> is the ingestion-time watermark: the next run picks up episodes
    /// created after it. It advances only through the episodes actually sent to the LLM, so episodes left out by the
    /// size budgets are summarised on a later run. <see cref="SagaNode.LastSummarizedEpisodeValidAt"/> is the
    /// event-time watermark: the latest episode reference time the summary covers.</para>
    /// <para>One run fetches at most <see cref="MaxSagaEpisodesForSummary"/> episodes, packs at most
    /// <see cref="SagaSummaryMaxEpisodeChars"/> characters of them, oldest first, and drops the newest until the
    /// prompt is under <see cref="SagaSummaryMaxPromptBytes"/>.</para>
    /// </remarks>
    /// <param name="sagaId">The saga's id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The saga, saved with its new summary (unchanged if there was nothing new).</returns>
    /// <exception cref="NodeNotFoundException">No saga has this id.</exception>
    public async Task<SagaNode> SummarizeSagaAsync(string sagaId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sagaId);
        var found = await Driver.SagaNodeOps.GetByUuidAsync(sagaId, cancellationToken).ConfigureAwait(false);

        // graphiti-net: hold the group's lock, so a concurrent episode cannot lose its saga pointers to this save,
        // and a strict removal cannot be undone by a summary of the removed episode. Read again under the lock.
        await using var groupLock = await GroupLock.AcquireAsync(found.GroupId, cancellationToken)
            .ConfigureAwait(false);
        var saga = await Driver.SagaNodeOps.GetByUuidAsync(sagaId, cancellationToken).ConfigureAwait(false);
        using var scope = BeginOperation("summarize_saga", saga.GroupId);

        var since = saga.LastSummarizedAt;
        var existingSummary = TextUtils.TruncateAtSentence(saga.Summary ?? "", TextUtils.SagaSummaryMaxChars);

        var episodesData = await SagaGetEpisodeContentsAsync(sagaId, since, MaxSagaEpisodesForSummary, cancellationToken)
            .ConfigureAwait(false);

        if (episodesData.Count == 0)
        {
            saga.SummaryEpisodesFetched = 0;
            saga.SummaryEpisodesSelected = 0;
            saga.SummaryEpisodesSkipped = 0;
            saga.SummarySelectedEpisodeBytes = 0;
            GraphitiLog.NoNewSagaEpisodes(Logger, sagaId);
            return saga;
        }

        var totalEpisodeCount = episodesData.Count;
        var (selected, selectedEpisodeBytes, _) = SelectSagaSummaryEpisodes(episodesData, SagaSummaryEpisodeCharBudget);
        var episodes = selected.ToList();
        saga.SummaryEpisodesFetched = totalEpisodeCount;
        saga.SummaryEpisodesSelected = episodes.Count;
        saga.SummaryEpisodesSkipped = totalEpisodeCount - episodes.Count;
        saga.SummarySelectedEpisodeBytes = selectedEpisodeBytes;
        if (episodes.Count == 0)
        {
            GraphitiLog.NoSagaContentFits(Logger, sagaId);
            return saga;
        }

        Dictionary<string, object?> PromptContext(IEnumerable<SagaEpisodeContent> chosen) => new()
        {
            ["saga_name"] = saga.Name,
            ["existing_summary"] = existingSummary,
            ["episodes"] = chosen
                .OrderBy(episode => episode.ValidAt ?? episode.CreatedAt)
                .ThenBy(episode => episode.CreatedAt)
                .Select(episode => episode.Content)
                .ToList(),
        };

        var schemaReserve = Encoding.UTF8.GetByteCount(SagaSummarySchemaWrapper)
                            + Encoding.UTF8.GetByteCount(ResponseModelSchema.Serialize(typeof(SagaSummary)));
        while (episodes.Count > 0)
        {
            var messages = Clients.PromptLibrary
                .Build(PromptNames.SummarizeSagasSummarizeSaga, PromptContext(episodes))
                .AsMessages();
            var promptBytes = messages.Sum(message => Encoding.UTF8.GetByteCount(message.Content)) + schemaReserve;
            if (promptBytes <= SagaSummaryPromptByteBudget)
            {
                break;
            }

            if (episodes.Count == 1)
            {
                var only = episodes[0];
                var over = promptBytes - SagaSummaryPromptByteBudget;
                var truncated = TextUtils.TruncateUtf8ToBytes(
                    only.Content,
                    Math.Max(0, Encoding.UTF8.GetByteCount(only.Content) - over));
                episodes = truncated.Length > 0 ? [only with { Content = truncated }] : [];
                break;
            }

            episodes.RemoveAt(episodes.Count - 1);
        }

        saga.SummaryEpisodesSelected = episodes.Count;
        saga.SummaryEpisodesSkipped = totalEpisodeCount - episodes.Count;
        saga.SummarySelectedEpisodeBytes = episodes.Sum(episode => Encoding.UTF8.GetByteCount(episode.Content));
        if (episodes.Count == 0)
        {
            GraphitiLog.NoSagaContentFits(Logger, sagaId);
            return saga;
        }

        var response = await Clients.CompletePromptAsync(
                PromptNames.SummarizeSagasSummarizeSaga,
                PromptContext(episodes),
                typeof(SagaSummary),
                maxTokens: SagaSummaryMaxTokens,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var summary = response.ValueKind == System.Text.Json.JsonValueKind.Object
                      && response.TryGetProperty("summary", out var value)
                      && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
        if (summary.Length > TextUtils.SagaSummaryMaxChars)
        {
            summary = TextUtils.TruncateAtSentence(summary, TextUtils.SagaSummaryMaxChars);
        }

        saga.Summary = summary;
        saga.LastSummarizedAt = episodes.Max(episode => episode.CreatedAt);
        var validAts = episodes.Where(episode => episode.ValidAt is not null).Select(episode => episode.ValidAt!.Value)
            .ToList();
        if (validAts.Count > 0)
        {
            var watermark = validAts.Max();
            if (saga.LastSummarizedEpisodeValidAt is null || watermark > saga.LastSummarizedEpisodeValidAt)
            {
                saga.LastSummarizedEpisodeValidAt = watermark;
            }
        }

        await Driver.SagaNodeOps.SaveAsync(saga, cancellationToken).ConfigureAwait(false);
        GraphitiLog.SagaSummaryUpdated(Logger, sagaId);
        return saga;
    }

    /// <summary>
    /// Picks a contiguous, oldest-first batch of saga episodes within a character budget (upstream
    /// <c>select_saga_summary_episodes</c>). If the oldest episode alone is over the budget, it is cut to the budget
    /// rather than skipped. Characters are UTF-16 code units.
    /// </summary>
    /// <param name="episodesData">The episodes, oldest first by ingestion.</param>
    /// <param name="maxEpisodeChars">The budget.</param>
    /// <returns>The selected episodes, their UTF-8 size, and whether any were left out or cut.</returns>
    public static (IReadOnlyList<SagaEpisodeContent> Selected, int SelectedBytes, bool Dropped)
        SelectSagaSummaryEpisodes(
            IReadOnlyList<SagaEpisodeContent> episodesData,
            int maxEpisodeChars = SagaSummaryMaxEpisodeChars)
    {
        ArgumentNullException.ThrowIfNull(episodesData);
        var selected = new List<SagaEpisodeContent>();
        var selectedChars = 0;
        var dropped = false;

        foreach (var episode in episodesData)
        {
            var contentChars = episode.Content.Length;
            if (selectedChars + contentChars <= maxEpisodeChars)
            {
                selected.Add(episode);
                selectedChars += contentChars;
                continue;
            }

            if (selected.Count == 0)
            {
                var truncated = episode.Content[..Math.Max(0, Math.Min(maxEpisodeChars, episode.Content.Length))];
                if (truncated.Length > 0)
                {
                    selected.Add(episode with { Content = truncated });
                }
            }

            dropped = true;
            break;
        }

        var selectedBytes = selected.Sum(episode => Encoding.UTF8.GetByteCount(episode.Content));
        return (selected, selectedBytes, dropped);
    }

    // _resolve_request_scope: no per-group database here, so the shared driver and clients serve every group.
    internal (string GroupId, IGraphDriver Driver, GraphitiClients Clients) ResolveRequestScope(string? groupId)
    {
        if (groupId is null)
        {
            return (GetDefaultGroupId(Driver.Provider), Driver, Clients);
        }

        Validation.ValidateGroupId(groupId);
        return (groupId, Driver, Clients);
    }

    internal static Task<IReadOnlyList<EpisodicNode>> RetrieveEpisodesWithDriverAsync(
        DateTimeOffset referenceTime,
        int lastN,
        IReadOnlyList<string>? groupIds,
        EpisodeType? source,
        string? saga,
        IGraphDriver driver,
        CancellationToken cancellationToken) =>
        driver.EpisodeNodeOps.RetrieveEpisodesAsync(referenceTime, lastN, groupIds, source, saga, cancellationToken);

    // _get_or_create_saga.
    internal async Task<SagaNode> GetOrCreateSagaAsync(
        string sagaName,
        string groupId,
        DateTimeOffset createdAt,
        IGraphDriver? driver = null,
        CancellationToken cancellationToken = default)
    {
        driver ??= Driver;
        var existing = await driver.SagaNodeOps.GetByNameAsync(sagaName, groupId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var saga = new SagaNode { Name = sagaName, GroupId = groupId, CreatedAt = createdAt };
        await driver.SagaNodeOps.SaveAsync(saga, cancellationToken).ConfigureAwait(false);
        return saga;
    }

    // _saga_get_previous_episode_uuid.
    internal Task<string?> SagaGetPreviousEpisodeUuidAsync(
        string sagaUuid,
        string currentEpisodeUuid,
        IGraphDriver? driver = null,
        CancellationToken cancellationToken = default) =>
        (driver ?? Driver).SagaNodeOps.GetPreviousEpisodeUuidAsync(sagaUuid, currentEpisodeUuid, cancellationToken);

    // _saga_get_episode_contents, with upstream's Cypher fallback: episodes created after `since`, oldest first,
    // `limit` of them, skipping empty content.
    internal async Task<IReadOnlyList<SagaEpisodeContent>> SagaGetEpisodeContentsAsync(
        string sagaUuid,
        DateTimeOffset? since = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var episodes = await Driver.EpisodeNodeOps.GetBySagaAsync(sagaUuid, since, cancellationToken)
            .ConfigureAwait(false);
        return episodes
            .Take(limit)
            .Where(episode => !string.IsNullOrEmpty(episode.Content))
            .Select(episode => new SagaEpisodeContent(episode.Content, episode.ValidAt, episode.CreatedAt))
            .ToList();
    }

    private async Task<SearchResults> SearchCoreAsync(
        string query,
        SearchConfig config,
        IReadOnlyList<string>? groupIds,
        string? centerNodeUuid,
        IReadOnlyList<string>? bfsOriginNodeUuids,
        SearchFilters? searchFilter,
        IReadOnlyList<float>? queryVector,
        IEpisodeAccessFilter? episodeAccessFilter,
        CancellationToken cancellationToken)
    {
        using var scope = BeginOperation("search", groupIds is { Count: 1 } ? groupIds[0] : null);
        return await SearchEngine.SearchAsync(
                Driver,
                Clients.Embedder,
                Clients.CrossEncoder,
                query,
                groupIds,
                config,
                searchFilter ?? new SearchFilters(),
                centerNodeUuid,
                bfsOriginNodeUuids,
                queryVector,
                MaxCoroutines,
                episodeAccessFilter,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Takes the locks of several groups in a fixed order, so two calls over overlapping groups cannot deadlock.
    private async Task<IAsyncDisposable> AcquireGroupLocksAsync(
        IEnumerable<string> groupIds,
        CancellationToken cancellationToken)
    {
        var held = new HeldGroupLocks();
        try
        {
            foreach (var groupId in groupIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                held.Add(await GroupLock.AcquireAsync(groupId, cancellationToken).ConfigureAwait(false));
            }

            return held;
        }
        catch
        {
            await held.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IDisposable BeginOperation(
        string operation,
        string? groupId,
        TokenUsageTracker? usage = null,
        EpisodicNode? episode = null) =>
        GraphitiCallContext.Begin(new GraphitiCallContext
        {
            Operation = operation,
            GroupId = groupId,
            EpisodeUuid = episode?.Uuid,
            EpisodeMetadata = episode?.EpisodeMetadata,
            Usage = usage ?? new TokenUsageTracker(),
            ModelCallGate = ModelCallGate,
        });

    private static IDisposable BeginEpisode(EpisodicNode episode)
    {
        var current = GraphitiCallContext.Current
                      ?? throw new InvalidOperationException("No Graphiti operation is running.");
        return GraphitiCallContext.Begin(current with
        {
            EpisodeUuid = episode.Uuid,
            EpisodeMetadata = episode.EpisodeMetadata,
        });
    }

    private static Activity? StartActivity(string name) =>
        GraphitiTelemetry.ActivitySource.StartActivity("graphiti." + name);

    private static void RecordError(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddException(exception);
    }

    private static double ElapsedMilliseconds(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

/// <summary>Group locks held together, released in reverse order.</summary>
internal sealed class HeldGroupLocks : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _held = [];

    public void Add(IAsyncDisposable handle) => _held.Add(handle);

    public async ValueTask DisposeAsync()
    {
        for (var i = _held.Count - 1; i >= 0; i--)
        {
            await _held[i].DisposeAsync().ConfigureAwait(false);
        }

        _held.Clear();
    }
}

/// <summary>One saga episode as saga summarisation sees it (upstream's <c>(content, valid_at, created_at)</c>
/// tuple).</summary>
/// <param name="Content">The episode text.</param>
/// <param name="ValidAt">The episode's reference time.</param>
/// <param name="CreatedAt">When the episode was ingested.</param>
public readonly record struct SagaEpisodeContent(string Content, DateTimeOffset? ValidAt, DateTimeOffset CreatedAt);

/// <summary>Log messages of <see cref="Graphiti"/>; texts follow upstream's log lines.</summary>
internal static partial class GraphitiLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "No new episodes found for saga {SagaId}, skipping summary")]
    public static partial void NoNewSagaEpisodes(ILogger logger, string sagaId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No saga episode content fit the summary byte budget for saga {SagaId}")]
    public static partial void NoSagaContentFits(ILogger logger, string sagaId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated summary for saga {SagaId}")]
    public static partial void SagaSummaryUpdated(ILogger logger, string sagaId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed {Operation} in {DurationMs} ms")]
    public static partial void Completed(ILogger logger, string operation, double durationMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to rebuild the summary of entity {NodeUuid} after removal of episode {EpisodeUuid}")]
    public static partial void SummaryRebuildFailed(ILogger logger, Exception exception, string nodeUuid,
        string episodeUuid);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Edge UUID {OldUuid} already exists with different source/target nodes. Generated new UUID {NewUuid} to avoid overwriting.")]
    public static partial void EdgeUuidRegenerated(ILogger logger, string oldUuid, string newUuid);
}
