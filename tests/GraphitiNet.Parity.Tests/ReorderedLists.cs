// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GraphitiNet.Parity.Tests;

/// <summary>
/// Matches a prompt to a recorded one when the only difference is the order of an indexed candidate list, and
/// translates the recorded answer's indexes to the port's order.
/// </summary>
/// <remarks>
/// Candidate lists come from search, and their order depends on keyword ranking, which differs between Lucene (upstream
/// on Neo4j) and graphiti-net's drivers, and even between upstream runs (Lucene's statistics count deleted documents
/// until segments merge). Replaying through a reordering checks the pipeline's logic separately from ranking; ranking
/// is compared separately through search results.
/// </remarks>
public static partial class ReorderedLists
{
    /// <summary>Prompts whose answers refer to candidates by index, and the answer fields holding those indexes.</summary>
    private static readonly Dictionary<string, string[]> IndexedAnswers = new()
    {
        ["dedupe_edges.resolve_edge"] = ["duplicate_facts", "contradicted_facts"],
    };

    /// <summary>The prompt with each indexed entry's number replaced by a placeholder, entries sorted by content.</summary>
    public static string Canonical(string content) =>
        FactList().Replace(content, m =>
        {
            var entries = FactEntry().Matches(m.Value).Select(e => e.Groups["fact"].Value).Order(StringComparer.Ordinal);
            return "[" + string.Join(", ", entries.Select(f => "{'idx': #, 'fact': " + f + "}")) + "]";
        });

    /// <summary>
    /// Finds the recorded call that matches <paramref name="portUser"/> up to list order and returns its answer with
    /// the indexes translated, or <see langword="null"/>.
    /// </summary>
    public static JsonElement? TryReplay(string? promptName, string portSystem, string portUser, IEnumerable<RecordedCall> calls)
    {
        if (promptName is null || !IndexedAnswers.TryGetValue(promptName, out var fields))
        {
            return null;
        }

        var canonical = Canonical(portUser);
        foreach (var call in calls.Where(c => c.PromptName == promptName))
        {
            if (call.Messages[0].Content != portSystem || Canonical(call.Messages[^1].Content) != canonical)
            {
                continue;
            }

            // Recorded index -> port index, pairing equal facts in order of appearance.
            var portByFact = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            foreach (Match e in FactEntry().Matches(portUser))
            {
                var fact = e.Groups["fact"].Value;
                if (!portByFact.TryGetValue(fact, out var queue))
                {
                    portByFact[fact] = queue = new Queue<int>();
                }

                queue.Enqueue(int.Parse(e.Groups["idx"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            var map = new Dictionary<int, int>();
            foreach (Match e in FactEntry().Matches(call.Messages[^1].Content))
            {
                map[int.Parse(e.Groups["idx"].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                    portByFact[e.Groups["fact"].Value].Dequeue();
            }

            var answer = JsonNode.Parse(call.Response.GetRawText())!.AsObject();
            foreach (var field in fields)
            {
                if (answer[field] is JsonArray indexes)
                {
                    answer[field] = new JsonArray(indexes
                        .Select(i => (JsonNode?)JsonValue.Create(map.GetValueOrDefault(i!.GetValue<int>(), i.GetValue<int>())))
                        .ToArray());
                }
            }

            return JsonSerializer.SerializeToElement(answer);
        }

        return null;
    }

    [GeneratedRegex(@"\[(?:\{'idx': \d+, 'fact': (?:'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*"")\}(?:, )?)+\]")]
    private static partial Regex FactList();

    [GeneratedRegex(@"\{'idx': (?<idx>\d+), 'fact': (?<fact>'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*"")\}")]
    private static partial Regex FactEntry();
}
