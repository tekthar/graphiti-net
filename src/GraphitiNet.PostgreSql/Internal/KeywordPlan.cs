// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Driver.PostgreSql.Internal;

/// <summary>
/// The collection statistics of a keyword query, over the whole table (every group), as a Lucene index keeps them:
/// per field, how many rows have any term and their total length; and per <c>search_keys</c> key (a term in a field,
/// in one impact class), how many rows hold it. A term's document frequency in a field is the sum over its classes.
/// </summary>
/// <param name="DocCount">Per field, the rows with any term.</param>
/// <param name="TotalLength">Per field, the rows' total length.</param>
/// <param name="KeyCounts">Per key, the rows holding it.</param>
/// <param name="GroupRows">The rows in the searched groups (all rows when every group is searched).</param>
internal sealed record Bm25Statistics(
    long[] DocCount,
    long[] TotalLength,
    IReadOnlyDictionary<string, long> KeyCounts,
    long GroupRows)
{
    /// <summary>How many rows hold <paramref name="term"/> in <paramref name="field"/>.</summary>
    public long DocumentFrequency(string term, int field) =>
        SearchIndex.Keys(field, term).Sum(k => KeyCounts.GetValueOrDefault(k));
}

/// <summary>
/// Plans which rows a BM25 keyword query reads, so that queries made of common words do not score every row that
/// contains one, while returning exactly what scoring every such row would.
/// </summary>
/// <remarks>
/// <para>Every (term, field) pair a row holds is in one impact class (<see cref="SearchIndex.ImpactClass"/>), stored as
/// its key, so the GIN index finds the rows where a term weighs at least so much, and
/// <see cref="SearchIndex.MaxWeight"/> bounds the BM25 score a key can add (<see cref="Unit.Bound"/>: times the
/// term's idf, and its repeats in the query). A row's score is at most the sum of its keys' bounds.</para>
/// <para><b>First pass</b> (Lucene's MaxScore, by impact class rather than by term): the keys with the highest bounds,
/// while the rows holding them stay few, are scored, best first. Their <c>limit</c>-th score is a threshold every
/// answer reaches. A row holding none of those keys scores at most the sum, over the (term, field) pairs, of the
/// highest bound left; when the threshold is above that, the first pass is the answer.</para>
/// <para><b>Second pass</b> (in the spirit of block-max WAND): otherwise only rows whose keys' bounds can add up to
/// the threshold can still be answers. Each pair's remaining classes are grouped into a few tiers, and every
/// combination of tiers whose bounds reach the threshold becomes one conjunction of key sets (the row holds a key of
/// this pair in these tiers, and one of that pair in those...), which the GIN index answers without reading rows. For
/// a query of common words, an answer must hold most of the words with high weights, which few rows do.</para>
/// <para>Every bound is exact arithmetic on the same statistics as the scores, with a margin far above floating-point
/// rounding, so a row left out scores strictly below the threshold: the answer, ties included, is that of a full
/// pass. When pruning cannot help, the plan is the full pass.</para>
/// </remarks>
internal sealed class KeywordPlan
{
    /// <summary>Below this many candidate rows a full pass is cheap: no pruning.</summary>
    public const long FullPassThreshold = 4_000;

    /// <summary>The rows in the searched groups a first pass aims to read.</summary>
    private const double FirstPassRows = 1500;

    /// <summary>What reading one GIN entry costs, relative to reading and scoring one row.</summary>
    private const double PostingCost = 0.02;

    /// <summary>How many searches in this process were answered without a full pass (for tests).</summary>
#pragma warning disable CA2211 // A counter, written only with Interlocked.
    internal static long PrunedSearches;
#pragma warning restore CA2211

    private const int MaxConjunctions = 64;
    private const int MaxDims = 12;
    private const int MaxTiers = 4;
    private const int MaxEnumeration = 20_000;

    private readonly List<Dim> _dims;
    private readonly long _fullPass;
    private readonly long _rows;

    /// <param name="terms">The query's terms per text search configuration, in query order with repeats.</param>
    /// <param name="statistics">The collection statistics.</param>
    /// <param name="fieldCount">The table's number of searchable fields.</param>
    public KeywordPlan(List<(string Config, List<string> Terms)> terms, Bm25Statistics statistics, int fieldCount)
    {
        Statistics = statistics;
        _dims = [];
        var configIndex = 0;
        foreach (var (_, configTerms) in terms)
        {
            foreach (var group in configTerms.GroupBy(t => t, StringComparer.Ordinal))
            {
                for (var f = 0; f < fieldCount; f++)
                {
                    var n = statistics.DocCount[f];
                    var units = new List<Unit>();
                    long df = 0;
                    for (var c = SearchIndex.UnclassedImpact; c < SearchIndex.ImpactClasses; c++)
                    {
                        var key = SearchIndex.Key(f, group.Key, c);
                        if (statistics.KeyCounts.TryGetValue(key, out var count) && count > 0)
                        {
                            units.Add(new Unit(key, c, count, 0));
                            df += count;
                        }
                    }

                    if (df == 0 || n == 0)
                    {
                        continue;
                    }

                    var idf = Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
                    var average = (double)statistics.TotalLength[f] / n;
                    var mult = group.Count();
                    units = [.. units
                        .Select(u => u with { Bound = mult * idf * SearchIndex.MaxWeight(u.Class, average) })
                        .OrderByDescending(u => u.Bound)
                        .ThenBy(u => u.Count)];
                    _dims.Add(new Dim(configIndex, group.Key, f, df, n, units));
                }
            }

            configIndex++;
        }

        _rows = Math.Max(1, statistics.DocCount.Max());
        var distinctPairs = _dims.GroupBy(d => (d.Term, d.Field)).Sum(g => g.First().DocumentFrequency);
        _fullPass = Math.Min(_rows, distinctPairs);
    }

    public Bm25Statistics Statistics { get; }

    /// <summary>Whether no row holds any query term.</summary>
    public bool IsEmpty => _dims.Count == 0;

    /// <summary>Every key of the query's terms that some row holds: the candidates of a full pass.</summary>
    public IReadOnlyList<string> AllKeys() => [.. _dims.SelectMany(d => d.Units).Select(u => u.Key).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Whether a full pass should read the searched groups' rows and test their keys, rather than find the rows with a
    /// query term through the GIN index: when the groups hold far fewer rows than the terms do.
    /// </summary>
    public bool GroupsFirst => Statistics.GroupRows < _fullPass / 4;

    /// <summary>
    /// The first pass, or <see langword="null"/> when a full pass is cheap or nothing can be pruned: the keys to read
    /// (<see cref="FirstPass.Keys"/>) and the score above which their best rows are the answer
    /// (<see cref="FirstPass.Rest"/>).
    /// </summary>
    public FirstPass? First(int attempt)
    {
        // The rows a full pass reads in the searched groups, assuming terms spread evenly over the groups.
        var share = Math.Clamp((double)Statistics.GroupRows / _rows, 1e-6, 1);
        if (_fullPass * share < FullPassThreshold)
        {
            return null;
        }

        // Enough keys that about FirstPassRows of their rows are in the searched groups, four times more each attempt.
        var budget = (long)Math.Min(_fullPass / 4.0, FirstPassRows * Math.Pow(4, attempt) / share);
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0L;
        foreach (var unit in _dims.SelectMany(d => d.Units).OrderByDescending(u => u.Bound).ThenBy(u => u.Count))
        {
            if (chosen.Contains(unit.Key))
            {
                continue;
            }

            // Keys too frequent for the budget are left to the second pass; rarer ones further down still fit.
            if (rows + unit.Count > budget)
            {
                continue;
            }

            rows += unit.Count;
            chosen.Add(unit.Key);
        }

        if (chosen.Count == 0 || _dims.All(d => d.Units.All(u => chosen.Contains(u.Key))))
        {
            return null;
        }

        var rest = RemainingBounds(chosen).Max(config => config.Value.Sum());
        return new FirstPass([.. chosen], Margin(rest, up: true), chosen);
    }

    /// <summary>
    /// The second pass after <paramref name="first"/> returned a full page whose last score is
    /// <paramref name="threshold"/>: a SQL condition on <c>{alias}.search_keys</c> holding every row not read yet that
    /// could score <paramref name="threshold"/> or more (written with <paramref name="array"/>, which adds a text
    /// array parameter), or <see langword="null"/> when it would read about as many rows as a full pass.
    /// </summary>
    public string? Second(FirstPass first, double threshold, string alias, Func<IEnumerable<string>, string> array)
    {
        var floor = Margin(threshold, up: false);
        var conjunctions = new List<List<string[]>>();
        var estimate = 0.0;
        foreach (var config in _dims.GroupBy(d => d.Config))
        {
            var dims = config
                .Select(d => d.Units.Where(u => !first.Chosen.Contains(u.Key)).ToList())
                .Select((units, i) => (Units: units, Dim: config.ElementAt(i)))
                .Where(d => d.Units.Count > 0)
                .ToList();
            if (dims.Sum(d => d.Units[0].Bound) < floor)
            {
                continue;
            }

            var tiers = dims.Select(d => new List<int> { d.Units.Count }).ToList();
            var best = dims.Count <= MaxDims ? Enumerate(dims, tiers, floor) : null;
            if (best is null)
            {
                // Too many combinations of (term, field) pairs: combine each term's fields into one pair, which may
                // add up to the sum of their bounds, and keep that coarser cover (one tier each).
                dims = [.. dims.GroupBy(d => d.Dim.Term, StringComparer.Ordinal).Select(g =>
                {
                    var bound = g.Sum(d => d.Units[0].Bound);
                    return (Units: g.SelectMany(d => d.Units).Select(u => u with { Bound = bound }).ToList(),
                        Dim: g.First().Dim with { Rows = g.Max(d => d.Dim.Rows) });
                })];
                tiers = [.. dims.Select(d => new List<int> { d.Units.Count })];
                best = dims.Count <= MaxDims ? Enumerate(dims, tiers, floor) : null;
                if (best is null)
                {
                    return null;
                }

                conjunctions.AddRange(best.Conjunctions);
                estimate += best.Estimate;
                continue;
            }

            // Split tiers while that lowers the estimated rows read.
            for (var round = 0; round < 8; round++)
            {
                (List<List<int>> Tiers, Cover Cover)? better = null;
                for (var d = 0; d < dims.Count; d++)
                {
                    if (tiers[d].Count >= MaxTiers)
                    {
                        continue;
                    }

                    for (var cut = 1; cut < dims[d].Units.Count; cut++)
                    {
                        if (tiers[d].Contains(cut))
                        {
                            continue;
                        }

                        var trial = tiers.Select(t => new List<int>(t)).ToList();
                        trial[d].Add(cut);
                        trial[d].Sort();
                        var cover = Enumerate(dims, trial, floor);
                        if (cover is not null && cover.Estimate < (better?.Cover.Estimate ?? best.Estimate * 0.9))
                        {
                            better = (trial, cover);
                        }
                    }
                }

                if (better is null)
                {
                    break;
                }

                (tiers, best) = better.Value;
            }

            conjunctions.AddRange(best.Conjunctions);
            estimate += best.Estimate;
        }

        if (conjunctions.Count == 0)
        {
            // No row left out can reach the threshold (the first pass's own check is stricter, by its margin).
            return "false";
        }

        // The estimate takes the terms as independent, which overstates the rows holding several common ones: give up
        // only when it exceeds every row holding a query term.
        var fullCost = _fullPass;
        if (estimate > fullCost || conjunctions.Count > MaxConjunctions)
        {
            return null;
        }

        return string.Join(" OR ", conjunctions.Select(c =>
            "(" + string.Join(" AND ", c.Select(keys => $"{alias}.search_keys && {array(keys)}")) + ")"));
    }

    /// <summary>
    /// For each configuration, each (term, field) pair's highest bound among the keys not in
    /// <paramref name="chosen"/> (zero when all are): what a row holding none of them can score at most, summed.
    /// </summary>
    private Dictionary<int, List<double>> RemainingBounds(HashSet<string> chosen) =>
        _dims.GroupBy(d => d.Config).ToDictionary(
            g => g.Key,
            g => g.Select(d => d.Units.Where(u => !chosen.Contains(u.Key)).Select(u => u.Bound).DefaultIfEmpty(0).Max()).ToList());

    /// <summary>
    /// The conjunctions covering every combination of tiers whose bounds reach <paramref name="floor"/>, and the rows
    /// they are estimated to read, or <see langword="null"/> when there are too many.
    /// </summary>
    /// <param name="dims">Each pair's remaining keys, best bound first.</param>
    /// <param name="tiers">Each pair's tier ends (exclusive indexes into its keys, ascending, the last its count):
    /// tier <c>i</c> holds the keys from the previous end to its end.</param>
    /// <param name="floor">The bound a row must reach to be read.</param>
    private Cover? Enumerate(List<(List<Unit> Units, Dim Dim)> dims, List<List<int>> tiers, double floor)
    {
        // A row's level in a pair: the tier its key is in (0 the best), or absent. Its bound is that tier's best bound.
        var bounds = dims.Select((d, i) => tiers[i].Select((_, t) => d.Units[t == 0 ? 0 : tiers[i][t - 1]].Bound).ToArray()).ToArray();
        var maxRest = new double[dims.Count + 1];
        for (var i = dims.Count - 1; i >= 0; i--)
        {
            maxRest[i] = maxRest[i + 1] + bounds[i][0];
        }

        var level = new int[dims.Count];
        var found = new List<int[]>();
        var visited = 0;
        var overflow = false;

        // Levels per pair: -1 absent, else a tier index; a minimal combination cannot lower any level (to a worse
        // tier, or to absent) and still reach the floor.
        void Walk(int i, double sum)
        {
            if (overflow || ++visited > MaxEnumeration)
            {
                overflow = true;
                return;
            }

            if (sum + maxRest[i] < floor)
            {
                return;
            }

            if (i == dims.Count)
            {
                for (var d = 0; d < dims.Count; d++)
                {
                    if (level[d] < 0)
                    {
                        continue;
                    }

                    var lower = level[d] + 1 < bounds[d].Length ? bounds[d][level[d] + 1] : 0;
                    if (sum - bounds[d][level[d]] + lower >= floor)
                    {
                        return;
                    }
                }

                found.Add((int[])level.Clone());
                if (found.Count > MaxConjunctions)
                {
                    overflow = true;
                }

                return;
            }

            level[i] = -1;
            Walk(i + 1, sum);
            for (var t = bounds[i].Length - 1; t >= 0; t--)
            {
                level[i] = t;
                Walk(i + 1, sum + bounds[i][t]);
            }

            level[i] = -1;
        }

        Walk(0, 0);
        if (overflow)
        {
            return null;
        }

        var conjunctions = new List<List<string[]>>();
        var estimate = 0.0;
        foreach (var combination in found)
        {
            var sets = new List<string[]>();
            var fraction = 1.0;
            var postings = 0L;
            for (var d = 0; d < dims.Count; d++)
            {
                if (combination[d] < 0)
                {
                    continue;
                }

                var keys = dims[d].Units.Take(tiers[d][combination[d]]).ToList();
                sets.Add([.. keys.Select(u => u.Key)]);
                postings += keys.Sum(u => u.Count);
                fraction *= Math.Min(1.0, (double)keys.Sum(u => u.Count) / dims[d].Dim.Rows);
            }

            conjunctions.Add(sets);
            // Rows read, plus the GIN entries read to find them (much cheaper each), plus a fixed cost per scan.
            estimate += (fraction * _rows) + (postings * PostingCost) + 100;
        }

        return new Cover(conjunctions, estimate);
    }

    /// <summary>Moves a bound by far more than floating-point rounding, so that comparisons with it stay strict.</summary>
    private static double Margin(double value, bool up)
    {
        var margin = (Math.Abs(value) * 1e-9) + 1e-9;
        return up ? value + margin : value - margin;
    }

    /// <summary>One key of a (term, field) pair: its impact class, how many rows hold it, and what it adds at most.</summary>
    internal sealed record Unit(string Key, int Class, long Count, double Bound);

    /// <summary>A (term, field) pair of one configuration's query terms, with its keys, best bound first.</summary>
    internal sealed record Dim(int Config, string Term, int Field, long DocumentFrequency, long Rows, List<Unit> Units);

    internal sealed record FirstPass(IReadOnlyList<string> Keys, double Rest, HashSet<string> Chosen);

    private sealed record Cover(List<List<string[]>> Conjunctions, double Estimate);
}
