// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text;

namespace GraphitiNet.Parity.Tests;

/// <summary>How closely the port's result matches upstream's.</summary>
public sealed record ParityComparison(
    int UpstreamFacts,
    int PortFacts,
    int MatchedFacts,
    IReadOnlyList<string> Differences)
{
    /// <summary>Compares entity names, facts (endpoints, name, text, validity window, expiry) and search results.</summary>
    public static ParityComparison Compare(ParityResult upstream, ParityResult port)
    {
        var differences = new List<string>();
        var upstreamNames = upstream.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var portNames = port.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        differences.AddRange(upstreamNames.Except(portNames).Select(n => $"entity only upstream: {n}"));
        differences.AddRange(portNames.Except(upstreamNames).Select(n => $"entity only in port: {n}"));
        foreach (var u in upstream.Entities)
        {
            var p = port.Entities.FirstOrDefault(e => e.Name == u.Name);
            if (p is not null && !p.Labels.SequenceEqual(u.Labels))
            {
                differences.Add($"labels differ for {u.Name}: upstream [{string.Join(",", u.Labels)}], port [{string.Join(",", p.Labels)}]");
            }

            if (p is not null && p.Summary != u.Summary)
            {
                differences.Add($"summary differs for {u.Name}:\n    upstream: {u.Summary}\n    port:     {p.Summary}");
            }
        }

        var remaining = port.Facts.ToList();
        var matched = 0;
        foreach (var f in upstream.Facts)
        {
            var hit = remaining.FirstOrDefault(p => p == f);
            if (hit is not null)
            {
                remaining.Remove(hit);
                matched++;
            }
            else
            {
                differences.Add($"fact only upstream: {Describe(f)}");
            }
        }

        differences.AddRange(remaining.Select(f => $"fact only in port: {Describe(f)}"));

        foreach (var (u, p) in upstream.Searches.Zip(port.Searches))
        {
            if (!u.Facts.SequenceEqual(p.Facts))
            {
                differences.Add($"search '{u.Query}' facts:\n    upstream: {string.Join(" | ", u.Facts)}\n    port:     {string.Join(" | ", p.Facts)}");
            }

            if (!u.CombinedNodes.SequenceEqual(p.CombinedNodes))
            {
                differences.Add($"search '{u.Query}' nodes:\n    upstream: {string.Join(" | ", u.CombinedNodes)}\n    port:     {string.Join(" | ", p.CombinedNodes)}");
            }
        }

        return new ParityComparison(upstream.Facts.Count, port.Facts.Count, matched, differences);
    }

    private static string Describe(ParityFact f) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{f.Source} -[{f.Name}]-> {f.Target}: \"{f.Fact}\" valid {f.ValidAt:o} invalid {f.InvalidAt:o} expired {f.Expired}");

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"facts: upstream {UpstreamFacts}, port {PortFacts}, matched {MatchedFacts}\n");
        foreach (var d in Differences)
        {
            sb.Append("- ").Append(d).Append('\n');
        }

        return sb.ToString();
    }
}
