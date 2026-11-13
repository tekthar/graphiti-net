// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>One fact the scripted model extracts from an episode.</summary>
/// <param name="Source">Source entity name.</param>
/// <param name="Relation">Relation type, for example <c>WORKS_AT</c>.</param>
/// <param name="Target">Target entity name.</param>
/// <param name="Fact">The fact text.</param>
/// <param name="ValidAt">ISO date the fact became true, or <see langword="null"/>.</param>
/// <param name="Contradicts">Fact texts this fact contradicts, when they are offered as candidates.</param>
/// <param name="DuplicateOf">A fact text this fact restates, when it is offered as a duplicate candidate.</param>
public sealed record ScriptedFact(
    string Source,
    string Relation,
    string Target,
    string Fact,
    string? ValidAt = null,
    IReadOnlyList<string>? Contradicts = null,
    string? DuplicateOf = null);

/// <summary>What the scripted model "reads" in one episode body.</summary>
public sealed record ScriptedEpisode(string Body, IReadOnlyList<string> Entities, IReadOnlyList<ScriptedFact> Facts);

/// <summary>One call the scripted model received.</summary>
public sealed record ScriptedCall(string PromptName, GraphitiCallContext? Context, string Text);

/// <summary>
/// A deterministic LLM that answers each prompt by name from a script keyed by the episode text found in the prompt,
/// so the library's real extraction, deduplication, edge resolution, invalidation and summary code runs:
/// <list type="bullet">
/// <item>entity and fact extraction answer with the scripted entities and facts of the episode in the prompt's current
/// message;</item>
/// <item>entity deduplication marks an entity a duplicate only of a candidate with the same name (case-insensitive);</item>
/// <item>fact deduplication reports the scripted duplicate and contradicted facts by their index in the prompt;</item>
/// <item>summaries are the scripted episode bodies present in the prompt that mention the entity.</item>
/// </list>
/// Any other prompt fails the call.
/// </summary>
public sealed partial class ScriptedLlm : LLMClient
{
    private readonly List<ScriptedEpisode> _episodes;
    private readonly Lock _lock = new();

    public ScriptedLlm(params ScriptedEpisode[] episodes)
        : base(new LLMConfig(model: "scripted"))
    {
        _episodes = [.. episodes];
    }

    public const long InputTokensPerCall = 11;

    public const long OutputTokensPerCall = 7;

    /// <summary>Runs before every answer, with the prompt name: block, cancel or count here.</summary>
    public Func<string, CancellationToken, Task>? BeforeAnswer { get; set; }

    public IReadOnlyList<ScriptedCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    private readonly List<ScriptedCall> _calls = [];

    protected override async Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize, string? model,
        CancellationToken cancellationToken)
    {
        var context = GraphitiCallContext.Current;
        var promptName = context?.PromptName ?? "";
        var text = string.Join("\n", messages.Select(m => m.Content));
        lock (_lock)
        {
            _calls.Add(new ScriptedCall(promptName, context, text));
        }

        if (BeforeAnswer is { } hook)
        {
            await hook(promptName, cancellationToken).ConfigureAwait(false);
        }

        var json = promptName switch
        {
            "extract_nodes.extract_message" => ExtractEntities(Section(text, "CURRENT MESSAGE")),
            "extract_nodes.extract_text" => ExtractEntities(Section(text, "TEXT")),
            "extract_edges.edge" => ExtractFacts(Section(text, "CURRENT_MESSAGE")),
            "dedupe_nodes.nodes" => DedupeEntities(text),
            "dedupe_edges.resolve_edge" => ResolveFact(text),
            "extract_edges.extract_timestamps" => """{"valid_at": null, "invalid_at": null}""",
            "extract_nodes.extract_summaries_batch" => Summaries(text, "MESSAGES"),
            "extract_nodes.extract_entity_summaries_from_episodes" => Summaries(text, "EPISODES"),
            _ => throw new InvalidOperationException($"The scripted model has no answer for prompt '{promptName}'."),
        };
        return new LLMClientResponse(JsonDocument.Parse(json).RootElement.Clone(), InputTokensPerCall,
            OutputTokensPerCall);
    }

    private ScriptedEpisode EpisodeIn(string section) =>
        _episodes.Where(e => section.Contains(e.Body, StringComparison.Ordinal))
            .OrderByDescending(e => e.Body.Length)
            .FirstOrDefault()
        ?? throw new InvalidOperationException($"No scripted episode in: {section}");

    private string ExtractEntities(string section) => JsonSerializer.Serialize(new
    {
        extracted_entities = EpisodeIn(section).Entities.Select(name => new { name, entity_type_id = 0 }),
    });

    private string ExtractFacts(string section) => JsonSerializer.Serialize(new
    {
        edges = EpisodeIn(section).Facts.Select(f => new
        {
            source_entity_name = f.Source,
            target_entity_name = f.Target,
            relation_type = f.Relation,
            fact = f.Fact,
            valid_at = f.ValidAt,
            invalid_at = (string?)null,
        }),
    });

    private static string DedupeEntities(string text)
    {
        using var extracted = JsonDocument.Parse(Section(text, "ENTITIES"));
        using var existing = JsonDocument.Parse(Section(text, "EXISTING ENTITIES"));
        var resolutions = extracted.RootElement.EnumerateArray().Select(entity =>
        {
            var name = entity.GetProperty("name").GetString()!;
            var match = existing.RootElement.EnumerateArray()
                .Where(c => string.Equals(c.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.GetProperty("candidate_id").GetInt32())
                .DefaultIfEmpty(-1)
                .First();
            return new { id = entity.GetProperty("id").GetInt32(), name, duplicate_candidate_id = match };
        }).ToList();
        return JsonSerializer.Serialize(new { entity_resolutions = resolutions });
    }

    private string ResolveFact(string text)
    {
        var newFact = Section(text, "NEW FACT").Trim();
        var scripted = _episodes.SelectMany(e => e.Facts).FirstOrDefault(f => f.Fact == newFact);
        var existing = IndexedFacts(Section(text, "EXISTING FACTS"));
        var candidates = IndexedFacts(Section(text, "FACT INVALIDATION CANDIDATES"));
        var duplicates = existing.Where(f => scripted?.DuplicateOf == f.Fact).Select(f => f.Idx);
        var contradicted = existing.Concat(candidates)
            .Where(f => scripted?.Contradicts?.Contains(f.Fact) == true)
            .Select(f => f.Idx);
        return JsonSerializer.Serialize(new { duplicate_facts = duplicates, contradicted_facts = contradicted });
    }

    private static List<(int Idx, string Fact)> IndexedFacts(string section) =>
        [.. IndexedFactPattern().Matches(section)
            .Select(m => (int.Parse(m.Groups["idx"].Value, CultureInfo.InvariantCulture), m.Groups["fact"].Value))];

    private string Summaries(string text, string episodesTag)
    {
        var episodesSection = Section(text, episodesTag);
        var present = _episodes.Where(e => episodesSection.Contains(e.Body, StringComparison.Ordinal)).ToList();
        using var entities = JsonDocument.Parse(Section(text, "ENTITIES"));
        var summaries = new List<object>();
        foreach (var entity in entities.RootElement.EnumerateArray())
        {
            var name = entity.GetProperty("name").GetString()!;
            var sentences = present
                .Where(e => e.Entities.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Select(e => e.Body)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (sentences.Count > 0)
            {
                summaries.Add(new { name, summary = string.Join(" ", sentences) });
            }
        }

        return JsonSerializer.Serialize(new { summaries });
    }

    /// <summary>The text between the last <c>&lt;tag&gt;</c> and the <c>&lt;/tag&gt;</c> after it.</summary>
    private static string Section(string text, string tag)
    {
        var open = $"<{tag}>";
        var start = text.LastIndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Prompt has no <{tag}> section.");
        }

        start += open.Length;
        var end = text.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return text[start..end].Trim();
    }

    // Python's str() of [{'idx': 0, 'fact': '...'}]; scripted facts contain no quotes.
    [GeneratedRegex("""'idx': (?<idx>\d+), 'fact': (?<q>['"])(?<fact>.*?)\k<q>\}""")]
    private static partial Regex IndexedFactPattern();
}

/// <summary>
/// A deterministic bag-of-words embedder: each word adds to one of <see cref="Dimension"/> buckets (stable FNV-1a
/// hash), normalised. Texts sharing words are similar. Records the call context of every call.
/// </summary>
public sealed class WordHashEmbedder : EmbedderClient
{
    public const int Dimension = 64;

    private readonly Lock _lock = new();
    private readonly List<GraphitiCallContext?> _contexts = [];

    public IReadOnlyList<GraphitiCallContext?> Contexts
    {
        get
        {
            lock (_lock)
            {
                return [.. _contexts];
            }
        }
    }

    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _contexts.Add(GraphitiCallContext.Current);
        }

        return Task.FromResult(Embed(inputData));
    }

    public static float[] Embed(string text)
    {
        var vector = new float[Dimension];
        vector[0] = 0.01f;
        foreach (var word in Words(text))
        {
            vector[Bucket(word)] += 1;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return [.. vector.Select(v => v / norm)];
    }

    public static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static int Bucket(string word)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(word))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return (int)(1 + (hash % (Dimension - 1)));
    }
}

/// <summary>A reranker that scores passages by the words they share with the query.</summary>
public sealed class WordOverlapCrossEncoder : CrossEncoderClient
{
    public override Task<IReadOnlyList<RankedPassage>> RankAsync(
        string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        var queryWords = WordHashEmbedder.Words(query).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<RankedPassage>>(passages
            .Select(p => new RankedPassage(p, WordHashEmbedder.Words(p).Count(queryWords.Contains)))
            .OrderByDescending(p => p.Score)
            .ToList());
    }
}
