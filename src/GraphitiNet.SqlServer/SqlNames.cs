// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Driver.SqlServer;

/// <summary>Table names and schema-qualified identifiers for raw SQL.</summary>
internal sealed class SqlNames
{
    public const string Episodes = "episodes";
    public const string Entities = "entities";
    public const string Communities = "communities";
    public const string Sagas = "sagas";
    public const string EpisodicEdges = "episodic_edges";
    public const string EntityEdges = "entity_edges";
    public const string CommunityEdges = "community_edges";
    public const string HasEpisodeEdges = "has_episode_edges";
    public const string NextEpisodeEdges = "next_episode_edges";
    public const string SearchTerms = "search_terms";
    public const string SearchFields = "search_fields";

    /// <summary>Every table.</summary>
    public static readonly IReadOnlyList<string> AllTables =
    [
        EpisodicEdges, EntityEdges, CommunityEdges, HasEpisodeEdges, NextEpisodeEdges,
        Episodes, Entities, Communities, Sagas, SearchTerms, SearchFields,
    ];

    /// <summary>The edge tables.</summary>
    public static readonly IReadOnlyList<string> EdgeTables =
        [EpisodicEdges, EntityEdges, CommunityEdges, HasEpisodeEdges, NextEpisodeEdges];

    /// <summary>The node tables.</summary>
    public static readonly IReadOnlyList<string> NodeTables = [Episodes, Entities, Communities, Sagas];

    public SqlNames(SqlServerGraphOptions options)
    {
        // The schema and prefix were validated as plain identifiers, so bracketing them is safe.
        Schema = options.Schema;
        Prefix = options.TablePrefix;
    }

    public string Schema { get; }

    /// <summary>The table name prefix.</summary>
    public string Prefix { get; }

    /// <summary>The table's name in the database (with the prefix).</summary>
    public string Name(string table) => Prefix + table;

    /// <summary>The schema-qualified, bracketed table name (with the prefix).</summary>
    public string Table(string table) => Quote(Schema) + "." + Quote(Prefix + table);

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
