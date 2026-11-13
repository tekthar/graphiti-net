// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using GraphitiNet.Driver;
using GraphitiNet.Models;

namespace GraphitiNet.Driver.Tests.Conformance;

/// <summary>
/// The driver conformance suite: every <see cref="IGraphDriver"/> member and every rule in the contract docs. A driver
/// passes by subclassing this class in its own test project and implementing <see cref="CreateDriverAsync"/>.
/// </summary>
/// <remarks>
/// <para>xUnit creates one instance per test, so <see cref="CreateDriverAsync"/> runs once per test and the driver is
/// disposed after it. Tests use only the public contract.</para>
/// <para><b>Shared databases.</b> Every test writes to fresh, unique group ids (<see cref="NewGroup"/>) and
/// searches only those groups, so tests do not see each other's data and may run in parallel against one database.
/// After each test, its groups are removed with <see cref="IGraphMaintenanceOperations.ClearDataAsync"/>. A few tests
/// exercise whole-graph behaviour (clearing everything, removing every community, searching every group); they run
/// only when <see cref="IsIsolated"/> is <see langword="true"/>.</para>
/// <para><b>Precision.</b> Timestamps used by the tests have millisecond precision, and embeddings are small unit
/// vectors padded with zeros to <see cref="EmbeddingDimension"/>.</para>
/// </remarks>
public abstract partial class GraphDriverConformanceTests : IAsyncLifetime
{
    private readonly List<string> _groups = [];
    private IGraphDriver? _driver;

    /// <summary>The driver under test.</summary>
    protected IGraphDriver Driver
    {
        get
        {
            Skip.If(SkipReason is not null, SkipReason);
            return _driver ?? throw new InvalidOperationException("The driver is not created yet.");
        }
    }

    /// <summary>
    /// Why the backend can't be tested here (for example, the database is unreachable), or <see langword="null"/>. When
    /// set, every test is reported as skipped instead of failing; override <see cref="InitializeAsync"/> to avoid
    /// creating the driver too.
    /// </summary>
    protected virtual string? SkipReason => null;

    /// <summary>
    /// Creates the driver for one test, with its schema in place (call
    /// <see cref="IGraphMaintenanceOperations.BuildIndicesAndConstraintsAsync"/> here if the backend needs it). If
    /// <see cref="IsIsolated"/> is <see langword="true"/> the driver must start empty and see no other test's data.
    /// </summary>
    protected abstract Task<IGraphDriver> CreateDriverAsync();

    /// <summary>
    /// Whether each driver from <see cref="CreateDriverAsync"/> has a store of its own, so whole-graph tests may
    /// clear or read everything. Return <see langword="false"/> when tests share one database; those tests then pass
    /// without checking anything. The default is <see langword="true"/>.
    /// </summary>
    protected virtual bool IsIsolated => true;

    /// <summary>The embedding size the backend stores (at least 3). Test vectors are padded with zeros.</summary>
    protected virtual int EmbeddingDimension => 3;

    /// <inheritdoc />
    public virtual async Task InitializeAsync() => _driver = await CreateDriverAsync();

    /// <inheritdoc />
    public virtual async Task DisposeAsync()
    {
        if (_driver is null)
        {
            return;
        }

        try
        {
            if (_groups.Count > 0)
            {
                await _driver.GraphOps.ClearDataAsync(_groups);
            }
        }
        finally
        {
            await _driver.DisposeAsync();
        }
    }

    /// <summary>A new group id, unique to this test run, cleared after the test.</summary>
    protected string NewGroup()
    {
        var group = "conformance-" + Guid.NewGuid().ToString("N");
        _groups.Add(group);
        return group;
    }

    /// <summary>A vector padded with zeros to <see cref="EmbeddingDimension"/>.</summary>
    protected float[] Vec(params float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var vector = new float[Math.Max(EmbeddingDimension, values.Length)];
        values.CopyTo(vector, 0);
        return vector;
    }

    /// <summary>A UTC time on day <paramref name="day"/> of January 2025, at millisecond precision.</summary>
    protected static DateTimeOffset Day(int day, int hour = 0, int minute = 0) =>
        new(2025, 1, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A word no other test uses, for full-text tests that search every group.</summary>
    protected static string UniqueWord() => "zq" + Guid.NewGuid().ToString("N")[..12];

    // ---------------------------------------------------------------- record factories (not saved)

    private static int s_sequence;

    private static DateTimeOffset NextCreatedAt() =>
        Day(1).AddMilliseconds(Interlocked.Increment(ref s_sequence) % 100_000);

    protected static EntityNode Entity(
        string groupId,
        string name,
        string summary = "",
        IReadOnlyList<string>? labels = null,
        float[]? embedding = null,
        IDictionary<string, object?>? attributes = null,
        DateTimeOffset? createdAt = null) => new()
    {
        Name = name,
        GroupId = groupId,
        Summary = summary,
        Labels = labels ?? [],
        NameEmbedding = embedding,
        Attributes = attributes ?? new Dictionary<string, object?>(),
        CreatedAt = createdAt ?? NextCreatedAt(),
    };

    protected static EntityEdge Fact(
        string groupId,
        EntityNode source,
        EntityNode target,
        string name = "RELATES_TO",
        string fact = "",
        float[]? embedding = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? validAt = null,
        DateTimeOffset? invalidAt = null,
        DateTimeOffset? expiredAt = null,
        IDictionary<string, object?>? attributes = null) => new()
    {
        GroupId = groupId,
        SourceNodeUuid = source.Uuid,
        TargetNodeUuid = target.Uuid,
        Name = name,
        Fact = fact.Length > 0 ? fact : $"{source.Name} {name} {target.Name}",
        FactEmbedding = embedding,
        CreatedAt = createdAt ?? NextCreatedAt(),
        ValidAt = validAt,
        InvalidAt = invalidAt,
        ExpiredAt = expiredAt,
        Attributes = attributes ?? new Dictionary<string, object?>(),
    };

    protected static EpisodicNode Episode(
        string groupId,
        string name,
        string content = "",
        DateTimeOffset? validAt = null,
        EpisodeType source = EpisodeType.Text,
        string sourceDescription = "test",
        DateTimeOffset? createdAt = null) => new()
    {
        Name = name,
        GroupId = groupId,
        Content = content.Length > 0 ? content : name,
        ValidAt = validAt ?? Day(1),
        Source = source,
        SourceDescription = sourceDescription,
        CreatedAt = createdAt ?? NextCreatedAt(),
    };

    protected static CommunityNode Community(
        string groupId,
        string name,
        float[]? embedding = null,
        string summary = "") => new()
    {
        Name = name,
        GroupId = groupId,
        NameEmbedding = embedding,
        Summary = summary,
        CreatedAt = NextCreatedAt(),
    };

    protected static SagaNode Saga(string groupId, string name, DateTimeOffset? createdAt = null) => new()
    {
        Name = name,
        GroupId = groupId,
        CreatedAt = createdAt ?? NextCreatedAt(),
    };

    protected static EpisodicEdge Mention(string groupId, EpisodicNode episode, EntityNode entity) => new()
    {
        GroupId = groupId,
        SourceNodeUuid = episode.Uuid,
        TargetNodeUuid = entity.Uuid,
        CreatedAt = NextCreatedAt(),
    };

    protected static CommunityEdge Member(string groupId, CommunityNode community, Node member) => new()
    {
        GroupId = groupId,
        SourceNodeUuid = community.Uuid,
        TargetNodeUuid = member.Uuid,
        CreatedAt = NextCreatedAt(),
    };

    protected static HasEpisodeEdge HasEpisode(string groupId, SagaNode saga, EpisodicNode episode) => new()
    {
        GroupId = groupId,
        SourceNodeUuid = saga.Uuid,
        TargetNodeUuid = episode.Uuid,
        CreatedAt = NextCreatedAt(),
    };

    protected static NextEpisodeEdge NextEpisode(string groupId, EpisodicNode from, EpisodicNode to) => new()
    {
        GroupId = groupId,
        SourceNodeUuid = from.Uuid,
        TargetNodeUuid = to.Uuid,
        CreatedAt = NextCreatedAt(),
    };

    // ---------------------------------------------------------------- saving helpers

    protected async Task Save(params EntityNode[] nodes) => await Driver.EntityNodeOps.SaveBulkAsync(nodes);

    protected async Task Save(params EntityEdge[] edges) => await Driver.EntityEdgeOps.SaveBulkAsync(edges);

    protected async Task Save(params EpisodicNode[] nodes) => await Driver.EpisodeNodeOps.SaveBulkAsync(nodes);

    protected async Task Save(params EpisodicEdge[] edges) => await Driver.EpisodicEdgeOps.SaveBulkAsync(edges);

    protected async Task Save(params CommunityNode[] nodes) => await Driver.CommunityNodeOps.SaveBulkAsync(nodes);

    protected async Task Save(params CommunityEdge[] edges) => await Driver.CommunityEdgeOps.SaveBulkAsync(edges);

    protected async Task Save(params SagaNode[] nodes) => await Driver.SagaNodeOps.SaveBulkAsync(nodes);

    protected async Task Save(params HasEpisodeEdge[] edges) => await Driver.HasEpisodeEdgeOps.SaveBulkAsync(edges);

    protected async Task Save(params NextEpisodeEdge[] edges) => await Driver.NextEpisodeEdgeOps.SaveBulkAsync(edges);

    // ---------------------------------------------------------------- assertions

    /// <summary>The ids, in order.</summary>
    protected static List<string> Ids(IEnumerable<Node> nodes) => nodes.Select(n => n.Uuid).ToList();

    /// <summary>The ids, in order.</summary>
    protected static List<string> Ids(IEnumerable<Edge> edges) => edges.Select(e => e.Uuid).ToList();

    /// <summary>Asserts the same ids in any order, without duplicates.</summary>
    protected static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var actualList = actual.ToList();
        Assert.Equal(actualList.Count, actualList.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            expected.Order(StringComparer.Ordinal).ToList(),
            actualList.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Asserts the same nodes in any order, without duplicates.</summary>
    protected static void AssertSameNodes(IEnumerable<Node> expected, IEnumerable<Node> actual) =>
        AssertSameSet(Ids(expected), Ids(actual));

    /// <summary>Asserts the same edges in any order, without duplicates.</summary>
    protected static void AssertSameEdges(IEnumerable<Edge> expected, IEnumerable<Edge> actual) =>
        AssertSameSet(Ids(expected), Ids(actual));

    /// <summary>Asserts the same instant, returned in UTC.</summary>
    protected static void AssertUtc(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.Equal(expected.UtcDateTime, actual.UtcDateTime);
    }

    /// <summary>Asserts the same instant (or both missing), returned in UTC.</summary>
    protected static void AssertUtc(DateTimeOffset? expected, DateTimeOffset? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        AssertUtc(expected.Value, actual.Value);
    }

    /// <summary>Asserts two value dictionaries are equal as JSON (key order and number representation aside).</summary>
    protected static void AssertJsonEqual(IDictionary<string, object?>? expected, IDictionary<string, object?>? actual)
    {
        var expectedJson = JsonSerializer.SerializeToElement(expected);
        var actualJson = JsonSerializer.SerializeToElement(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedJson, actualJson),
            $"Expected {expectedJson.GetRawText()} but got {actualJson.GetRawText()}");
    }

    protected static void AssertEntity(EntityNode expected, EntityNode actual)
    {
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.GroupId, actual.GroupId);

        // Every entity carries the Entity label, as upstream.
        AssertSameSet(expected.Labels.Append("Entity").Distinct(StringComparer.Ordinal), actual.Labels);
        AssertUtc(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Null(actual.NameEmbedding);
        AssertJsonEqual(expected.Attributes, actual.Attributes);
    }

    protected static void AssertFact(EntityEdge expected, EntityEdge actual)
    {
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.GroupId, actual.GroupId);
        Assert.Equal(expected.SourceNodeUuid, actual.SourceNodeUuid);
        Assert.Equal(expected.TargetNodeUuid, actual.TargetNodeUuid);
        AssertUtc(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Fact, actual.Fact);
        Assert.Null(actual.FactEmbedding);
        Assert.Equal(expected.Episodes, actual.Episodes);
        AssertUtc(expected.ExpiredAt, actual.ExpiredAt);
        AssertUtc(expected.ValidAt, actual.ValidAt);
        AssertUtc(expected.InvalidAt, actual.InvalidAt);
        AssertUtc(expected.ReferenceTime, actual.ReferenceTime);
        AssertJsonEqual(expected.Attributes, actual.Attributes);
    }

    protected static void AssertEpisode(EpisodicNode expected, EpisodicNode actual)
    {
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.GroupId, actual.GroupId);
        AssertUtc(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.SourceDescription, actual.SourceDescription);
        Assert.Equal(expected.Content, actual.Content);
        AssertUtc(expected.ValidAt, actual.ValidAt);
        Assert.Equal(expected.EntityEdges, actual.EntityEdges);
        AssertJsonEqual(expected.EpisodeMetadata, actual.EpisodeMetadata);
    }

    protected static void AssertCommunity(CommunityNode expected, CommunityNode actual)
    {
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.GroupId, actual.GroupId);
        AssertUtc(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Null(actual.NameEmbedding);
    }

    protected static void AssertSaga(SagaNode expected, SagaNode actual)
    {
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.GroupId, actual.GroupId);
        AssertUtc(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.FirstEpisodeUuid, actual.FirstEpisodeUuid);
        Assert.Equal(expected.LastEpisodeUuid, actual.LastEpisodeUuid);
        AssertUtc(expected.LastSummarizedAt, actual.LastSummarizedAt);
        AssertUtc(expected.LastSummarizedEpisodeValidAt, actual.LastSummarizedEpisodeValidAt);
    }

    protected static void AssertPlainEdge(Edge expected, Edge actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Uuid, actual.Uuid);
        Assert.Equal(expected.GroupId, actual.GroupId);
        Assert.Equal(expected.SourceNodeUuid, actual.SourceNodeUuid);
        Assert.Equal(expected.TargetNodeUuid, actual.TargetNodeUuid);
        AssertUtc(expected.CreatedAt, actual.CreatedAt);
    }
}
