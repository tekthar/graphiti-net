// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Search;

namespace GraphitiNet.Tests.Differential;

/// <summary>search_utils.py and helpers.normalize_l2 against upstream's recorded results.</summary>
public class SearchUtilsDifferentialTests
{
    private const string File = "search_utils";

    // NumPy and the port sum in different orders.
    private const double Tolerance = 1e-9;

    [Fact]
    public void CalculateCosineSimilarity_matches_upstream()
    {
        var m = new Mismatches("calculate_cosine_similarity");
        foreach (var c in Fixture.Cases(File, "calculate_cosine_similarity"))
        {
            var a = Fixture.Floats(c.Input.GetProperty("a"));
            var b = Fixture.Floats(c.Input.GetProperty("b"));
            if (Fixture.Raises(c.Output) is { } error)
            {
                m.Check(c, Throws<ArgumentException>(() => SearchUtils.CalculateCosineSimilarity(a, b)),
                    () => $"upstream raised {error}; the port did not throw");
                continue;
            }

            m.Equal(c, c.Output, SearchUtils.CalculateCosineSimilarity(a, b), relTol: Tolerance);
        }

        m.AssertNone();
    }

    [Fact]
    public void NormalizeL2_matches_upstream()
    {
        var m = new Mismatches("normalize_l2");
        foreach (var c in Fixture.Cases(File, "normalize_l2"))
        {
            m.Equal(c, c.Output, SearchUtils.NormalizeL2(Fixture.Floats(c.Input.GetProperty("v"))), relTol: Tolerance);
        }

        m.AssertNone();
    }

    [Fact]
    public void Rrf_matches_upstream()
    {
        var m = new Mismatches("rrf");
        foreach (var c in Fixture.Cases(File, "rrf"))
        {
            var results = c.Input.GetProperty("results").EnumerateArray()
                .Select(r => (IReadOnlyList<string>)Fixture.Strings(r)).ToList();
            var rankConst = c.Input.GetProperty("rank_const").GetInt32();
            var minScore = c.Input.GetProperty("min_score").GetDouble();
            if (Fixture.Raises(c.Output) is { } error)
            {
                m.Check(c, Throws<DivideByZeroException>(() => SearchUtils.Rrf(results, rankConst, minScore)),
                    () => $"upstream raised {error}; the port did not throw DivideByZeroException");
                continue;
            }

            // Same additions in the same order as upstream, so the scores must be identical.
            var (uuids, scores) = SearchUtils.Rrf(results, rankConst, minScore);
            m.Equal(c, c.Output, new object[] { uuids, scores });
        }

        m.AssertNone();
    }

    [Fact]
    public void MaximalMarginalRelevance_matches_upstream()
    {
        var m = new Mismatches("maximal_marginal_relevance");
        foreach (var c in Fixture.Cases(File, "maximal_marginal_relevance"))
        {
            var query = Fixture.Floats(c.Input.GetProperty("query"));
            var candidates = c.Input.GetProperty("candidates").EnumerateArray()
                .Select(p => new KeyValuePair<string, float[]>(p[0].GetString()!, Fixture.Floats(p[1])))
                .ToList();
            var lambda = c.Input.GetProperty("mmr_lambda").GetDouble();
            var minScore = c.Input.GetProperty("min_score").GetDouble();
            if (Fixture.Raises(c.Output) is { } error)
            {
                m.Check(c, Throws<ArgumentException>(() =>
                        SearchUtils.MaximalMarginalRelevance(query, candidates, lambda, minScore)),
                    () => $"upstream raised {error}; the port did not throw");
                continue;
            }

            var (uuids, scores) = SearchUtils.MaximalMarginalRelevance(query, candidates, lambda, minScore);
            m.Equal(c, c.Output, new object[] { uuids, scores }, relTol: Tolerance);
        }

        m.AssertNone();
    }

    internal static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
