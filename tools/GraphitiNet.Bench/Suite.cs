// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver;
using GraphitiNet.Search;

namespace GraphitiNet.Bench;

/// <summary>The query classes and scopes the benchmark measures.</summary>
internal static class Suite
{
    public static IEnumerable<Case> Cases(Corpus corpus, bool simple)
    {
        _ = corpus;
        var scopes = new List<(string Name, string[]? Groups)>
        {
            ("large", ["org-alpha"]),
            ("large-ar", ["org-beta"]),
            ("small", ["person-007"]),
            ("person+unit+project+site", ["person-003", "unit-04", "project-005", "site-02"]),
            ("large+3", ["org-alpha", "person-003", "unit-04", "project-005"]),
            ("all", null),
        };
        if (simple)
        {
            scopes.RemoveAll(s => s.Name == "large-ar");
        }

        foreach (var type in new[] { "entities", "facts", "episodes", "communities" })
        {
            foreach (var cls in Enum.GetValues<QueryClass>())
            {
                if (cls == QueryClass.Names && type is "episodes" or "communities")
                {
                    continue;
                }

                foreach (var (scope, groups) in scopes)
                {
                    yield return new Case($"{type} {cls} {scope}", type, cls, groups, Filtered: false);
                    if (type is "entities" or "facts" && scope is "large" or "all")
                    {
                        yield return new Case($"{type} {cls} {scope} filtered", type, cls, groups, Filtered: true);
                    }
                }
            }
        }
    }

    public static List<string> Queries(Corpus corpus, QueryClass cls, int count, int seed)
    {
        var random = new Random(seed);
        var queries = new List<string>();
        string Rank(int from, int to) => corpus.Word(from + random.Next(to - from));
        for (var i = 0; i < count; i++)
        {
            var words = cls switch
            {
                QueryClass.Rare => Enumerable.Range(0, 2 + random.Next(2)).Select(_ => Rank(5_000, 40_000)),
                QueryClass.Mid => Enumerable.Range(0, 2 + random.Next(3)).Select(_ => Rank(100, 1_000)),
                QueryClass.Common => Enumerable.Range(0, 2 + random.Next(3)).Select(_ => Rank(0, 25)),
                QueryClass.Mixed => [Rank(0, 25), Rank(0, 25), Rank(100, 1_000), Rank(2_000, 40_000)],
                _ => [Names[random.Next(Names.Length)]],
            };
            queries.Add(string.Join(' ', words));
        }

        return queries;
    }

    private static readonly string[] Names =
    [
        "محمد العتيبي", "أحمد القحطاني", "فاطمة الشمري", "شركة الدوسري", "مشروع الرياض", "سارة الغامدي",
        "عبدالله المطيري", "نورة الحربي", "علي", "الرياض", "شركة", "مشروع",
    ];

    public static async Task<int> RunAsync(IGraphDriver driver, Case c, string query) =>
        (await UuidsAsync(driver, c, query)).Count;

    public static async Task<List<string>> UuidsAsync(IGraphDriver driver, Case c, string query)
    {
        var search = driver.SearchOps;
        switch (c.Type)
        {
            case "entities":
                var filter = c.Filtered ? new SearchFilters { NodeLabels = ["Person", "Organization"] } : SearchFilters.None;
                return [.. (await search.NodeFulltextSearchAsync(query, filter, c.Groups, 10)).Select(n => n.Uuid)];
            case "facts":
                var factFilter = c.Filtered
                    ? new SearchFilters
                    {
                        ValidAt = [[new DateFilter { Date = Corpus.T0.AddDays(365), ComparisonOperator = ComparisonOperator.GreaterThanEqual }]],
                    }
                    : SearchFilters.None;
                return [.. (await search.EdgeFulltextSearchAsync(query, factFilter, c.Groups, 10)).Select(n => n.Uuid)];
            case "episodes":
                return [.. (await search.EpisodeFulltextSearchAsync(query, SearchFilters.None, c.Groups, 10)).Select(n => n.Uuid)];
            default:
                return [.. (await search.CommunityFulltextSearchAsync(query, c.Groups, 10)).Select(n => n.Uuid)];
        }
    }
}

internal enum QueryClass
{
    Rare,
    Mid,
    Common,
    Mixed,
    Names,
}

internal sealed record Case(string Name, string Type, QueryClass Class, string[]? Groups, bool Filtered);
