// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/utils/content_chunking.py and the CHUNK_* settings of graphiti_core/helpers.py
// (Graphiti v0.31.2) and changed for .NET.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;

namespace GraphitiNet.Utils;

/// <summary>
/// Splits large episode content into overlapping chunks at natural boundaries: JSON elements or keys, paragraphs
/// and sentences, or chat messages. A standalone utility, as upstream: <see cref="Graphiti.AddEpisodeAsync"/> does
/// not chunk, so a host chunks content itself and adds each chunk as an episode.
/// </summary>
/// <remarks>
/// Sizes are estimated at <see cref="CharsPerToken"/> characters per token. Characters are UTF-16 code units, where
/// upstream counts code points; they differ only for characters outside the Basic Multilingual Plane, such as emoji.
/// JSON chunks are written the way Python's <c>json.dumps</c> writes them.
/// </remarks>
public static partial class ContentChunking
{
    /// <summary>Upstream <c>CHARS_PER_TOKEN</c>: the characters-per-token estimate.</summary>
    public const int CharsPerToken = 4;

    /// <summary>Upstream <c>MAX_COMBINATIONS_TO_EVALUATE</c>: above this many combinations,
    /// <see cref="GenerateCoveringChunks{T}"/> samples instead of enumerating.</summary>
    public const int MaxCombinationsToEvaluate = 1000;

    /// <summary>Upstream <c>CHUNK_TOKEN_SIZE</c>: target chunk size in tokens; the <c>CHUNK_TOKEN_SIZE</c>
    /// environment variable, or 3000.</summary>
    public static int ChunkTokenSize { get; internal set; } = ReadInt("CHUNK_TOKEN_SIZE", 3000);

    /// <summary>Upstream <c>CHUNK_OVERLAP_TOKENS</c>: overlap between chunks in tokens; the
    /// <c>CHUNK_OVERLAP_TOKENS</c> environment variable, or 200.</summary>
    public static int ChunkOverlapTokens { get; internal set; } = ReadInt("CHUNK_OVERLAP_TOKENS", 200);

    /// <summary>Upstream <c>CHUNK_MIN_TOKENS</c>: content shorter than this is never chunked; the
    /// <c>CHUNK_MIN_TOKENS</c> environment variable, or 1000.</summary>
    public static int ChunkMinTokens { get; internal set; } = ReadInt("CHUNK_MIN_TOKENS", 1000);

    /// <summary>Upstream <c>CHUNK_DENSITY_THRESHOLD</c>: entities per token above which content counts as dense;
    /// the <c>CHUNK_DENSITY_THRESHOLD</c> environment variable, or 0.15.</summary>
    public static double ChunkDensityThreshold { get; internal set; } = ReadDouble("CHUNK_DENSITY_THRESHOLD", 0.15);

    /// <summary>Estimated tokens: characters divided by <see cref="CharsPerToken"/> (upstream
    /// <c>estimate_tokens</c>).</summary>
    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length / CharsPerToken;
    }

    /// <summary>
    /// Whether content should be chunked (upstream <c>should_chunk</c>): only when it is at least
    /// <see cref="ChunkMinTokens"/> long and dense with entities (many JSON elements or keys, or many capitalised
    /// words in text).
    /// </summary>
    public static bool ShouldChunk(string content, EpisodeType episodeType)
    {
        var tokens = EstimateTokens(content);
        if (tokens < ChunkMinTokens)
        {
            return false;
        }

        return EstimateHighDensity(content, episodeType, tokens);
    }

    /// <summary>Whether JSON content is dense (upstream <c>_json_likely_dense</c>): many array elements, or many keys
    /// within two levels, per 1000 tokens. Invalid JSON falls back to <see cref="TextLikelyDense"/>.</summary>
    public static bool JsonLikelyDense(string content, int tokens)
    {
        if (!TryParse(content, out var data))
        {
            return TextLikelyDense(content, tokens);
        }

        switch (data)
        {
            case JsonArray array:
            {
                var density = tokens > 0 ? (double)array.Count / tokens * 1000 : 0;
                return density > ChunkDensityThreshold * 1000;
            }

            case JsonObject obj:
            {
                var keyCount = CountJsonKeys(obj, 2);
                var density = tokens > 0 ? (double)keyCount / tokens * 1000 : 0;
                return density > ChunkDensityThreshold * 1000;
            }

            default:
                return false;
        }
    }

    /// <summary>Keys in a JSON object, counting nested objects (also inside arrays) up to
    /// <paramref name="maxDepth"/> levels (upstream <c>_count_json_keys</c>).</summary>
    public static int CountJsonKeys(JsonObject data, int maxDepth = 2, int currentDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (currentDepth >= maxDepth)
        {
            return 0;
        }

        var count = data.Count;
        foreach (var (_, value) in data)
        {
            switch (value)
            {
                case JsonObject nested:
                    count += CountJsonKeys(nested, maxDepth, currentDepth + 1);
                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is JsonObject itemObject)
                        {
                            count += CountJsonKeys(itemObject, maxDepth, currentDepth + 1);
                        }
                    }

                    break;
            }
        }

        return count;
    }

    /// <summary>Whether text is dense (upstream <c>_text_likely_dense</c>): capitalised words, not starting a
    /// sentence, per 1000 tokens, above half the JSON threshold.</summary>
    public static bool TextLikelyDense(string content, int tokens)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (tokens == 0)
        {
            return false;
        }

        var words = PySplit(content);
        if (words.Count == 0)
        {
            return false;
        }

        var capitalizedCount = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (i == 0)
            {
                continue;
            }

            var previous = PythonText.RStrip(words[i - 1]);
            if (previous.Length == 0 || ".!?".Contains(previous[^1], StringComparison.Ordinal))
            {
                continue;
            }

            var cleaned = words[i].Trim(WordPunctuation);
            if (cleaned.Length > 0 && char.IsUpper(cleaned[0]) && !PyIsUpper(cleaned))
            {
                capitalizedCount++;
            }
        }

        var density = (double)capitalizedCount / tokens * 1000;
        return density > ChunkDensityThreshold * 500;
    }

    /// <summary>
    /// Splits JSON content keeping its structure (upstream <c>chunk_json_content</c>): arrays at element boundaries,
    /// objects at top-level key boundaries, with the last elements or keys repeated as overlap. Invalid JSON falls
    /// back to <see cref="ChunkTextContent"/>; a scalar is returned as is.
    /// </summary>
    /// <param name="content">The JSON text.</param>
    /// <param name="chunkSizeTokens">Target chunk size; <see langword="null"/> or 0 means
    /// <see cref="ChunkTokenSize"/>.</param>
    /// <param name="overlapTokens">Overlap; <see langword="null"/> or 0 means <see cref="ChunkOverlapTokens"/>.</param>
    public static IReadOnlyList<string> ChunkJsonContent(string content, int? chunkSizeTokens = null,
        int? overlapTokens = null)
    {
        var chunkSize = Or(chunkSizeTokens, ChunkTokenSize);
        var overlap = Or(overlapTokens, ChunkOverlapTokens);
        var chunkSizeChars = chunkSize * CharsPerToken;
        var overlapChars = overlap * CharsPerToken;

        if (!TryParse(content, out var data))
        {
            return ChunkTextContent(content, chunkSize, overlap);
        }

        return data switch
        {
            JsonArray array => ChunkJsonArray(array.ToList(), chunkSizeChars, overlapChars),
            JsonObject obj => ChunkJsonObject(obj, chunkSizeChars, overlapChars),
            _ => [content],
        };
    }

    /// <summary>
    /// Splits text at paragraphs, then sentences, then words, with overlap (upstream <c>chunk_text_content</c>).
    /// </summary>
    /// <param name="content">The text.</param>
    /// <param name="chunkSizeTokens">Target chunk size; <see langword="null"/> or 0 means
    /// <see cref="ChunkTokenSize"/>.</param>
    /// <param name="overlapTokens">Overlap; <see langword="null"/> or 0 means <see cref="ChunkOverlapTokens"/>.</param>
    public static IReadOnlyList<string> ChunkTextContent(string content, int? chunkSizeTokens = null,
        int? overlapTokens = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var chunkSizeChars = Or(chunkSizeTokens, ChunkTokenSize) * CharsPerToken;
        var overlapChars = Or(overlapTokens, ChunkOverlapTokens) * CharsPerToken;

        if (content.Length <= chunkSizeChars)
        {
            return [content];
        }

        var paragraphs = ParagraphSplit().Split(content);
        var chunks = new List<string>();
        var currentChunk = new List<string>();
        var currentSize = 0;

        foreach (var rawParagraph in paragraphs)
        {
            var paragraph = PythonText.Strip(rawParagraph);
            if (paragraph.Length == 0)
            {
                continue;
            }

            var paraSize = paragraph.Length;
            if (paraSize > chunkSizeChars)
            {
                if (currentChunk.Count > 0)
                {
                    chunks.Add(string.Join("\n\n", currentChunk));
                    currentChunk = [];
                    currentSize = 0;
                }

                chunks.AddRange(ChunkBySentences(paragraph, chunkSizeChars, overlapChars));
                continue;
            }

            if (currentChunk.Count > 0 && currentSize + paraSize + 2 > chunkSizeChars)
            {
                chunks.Add(string.Join("\n\n", currentChunk));
                var overlapText = GetOverlapText(string.Join("\n\n", currentChunk), overlapChars);
                if (overlapText.Length > 0)
                {
                    currentChunk = [overlapText];
                    currentSize = overlapText.Length;
                }
                else
                {
                    currentChunk = [];
                    currentSize = 0;
                }
            }

            currentChunk.Add(paragraph);
            currentSize += paraSize + 2;
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(string.Join("\n\n", currentChunk));
        }

        return chunks.Count > 0 ? chunks : [content];
    }

    /// <summary>
    /// Splits a conversation without splitting a message (upstream <c>chunk_message_content</c>): a JSON array of
    /// messages at element boundaries, <c>Speaker: text</c> messages at speaker turns, otherwise at lines.
    /// </summary>
    /// <param name="content">The conversation.</param>
    /// <param name="chunkSizeTokens">Target chunk size; <see langword="null"/> or 0 means
    /// <see cref="ChunkTokenSize"/>.</param>
    /// <param name="overlapTokens">Overlap; <see langword="null"/> or 0 means <see cref="ChunkOverlapTokens"/>.</param>
    public static IReadOnlyList<string> ChunkMessageContent(string content, int? chunkSizeTokens = null,
        int? overlapTokens = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var chunkSizeChars = Or(chunkSizeTokens, ChunkTokenSize) * CharsPerToken;
        var overlapChars = Or(overlapTokens, ChunkOverlapTokens) * CharsPerToken;

        if (content.Length <= chunkSizeChars)
        {
            return [content];
        }

        if (TryParse(content, out var data) && data is JsonArray array)
        {
            return ChunkJsonArray(array.ToList(), chunkSizeChars, overlapChars);
        }

        if (SpeakerPattern().IsMatch(content))
        {
            return ChunkSpeakerMessages(content, chunkSizeChars, overlapChars);
        }

        return ChunkByLines(content, chunkSizeChars, overlapChars);
    }

    /// <summary>
    /// Groups items into chunks of at most <paramref name="k"/> so that every pair of items shares a chunk (upstream
    /// <c>generate_covering_chunks</c>): a greedy covering design, exhaustive when there are at most
    /// <see cref="MaxCombinationsToEvaluate"/> combinations and sampled otherwise, with pairs the greedy pass missed
    /// added as two-item chunks.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <param name="k">The chunk size.</param>
    /// <returns>Each chunk's items and their indices in <paramref name="items"/>.</returns>
    public static IReadOnlyList<(IReadOnlyList<T> Items, IReadOnlyList<int> Indices)> GenerateCoveringChunks<T>(
        IReadOnlyList<T> items, int k)
    {
        ArgumentNullException.ThrowIfNull(items);
        var n = items.Count;
        if (n <= k)
        {
            return [(items, Enumerable.Range(0, n).ToList())];
        }

        var uncoveredPairs = new HashSet<(int, int)>();
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                uncoveredPairs.Add((i, j));
            }
        }

        var chunks = new List<(IReadOnlyList<T>, IReadOnlyList<int>)>();
        var useSampling = Combinations(n, k) > MaxCombinationsToEvaluate;

        int CoveredCount(int[] indices)
        {
            var count = 0;
            for (var a = 0; a < indices.Length; a++)
            {
                for (var b = a + 1; b < indices.Length; b++)
                {
                    if (uncoveredPairs.Contains(Pair(indices[a], indices[b])))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        while (uncoveredPairs.Count > 0)
        {
            int[]? bestChunkIndices = null;
            var bestCoveredCount = 0;

            IEnumerable<int[]> candidates = useSampling ? SampleCombinations(n, k) : EnumerateCombinations(n, k);
            foreach (var chunkIndices in candidates)
            {
                var coveredCount = CoveredCount(chunkIndices);
                if (coveredCount > bestCoveredCount)
                {
                    bestCoveredCount = coveredCount;
                    bestChunkIndices = (int[])chunkIndices.Clone();
                }
            }

            if (bestChunkIndices is null || bestCoveredCount == 0)
            {
                break;
            }

            for (var a = 0; a < bestChunkIndices.Length; a++)
            {
                for (var b = a + 1; b < bestChunkIndices.Length; b++)
                {
                    uncoveredPairs.Remove(Pair(bestChunkIndices[a], bestChunkIndices[b]));
                }
            }

            chunks.Add((bestChunkIndices.Select(index => items[index]).ToList(), bestChunkIndices.ToList()));
        }

        foreach (var (first, second) in uncoveredPairs)
        {
            chunks.Add(([items[first], items[second]], [first, second]));
        }

        return chunks;
    }

    private static bool EstimateHighDensity(string content, EpisodeType episodeType, int tokens) =>
        episodeType == EpisodeType.Json ? JsonLikelyDense(content, tokens) : TextLikelyDense(content, tokens);

    private static List<string> ChunkJsonArray(List<JsonNode?> data, int chunkSizeChars, int overlapChars)
    {
        if (data.Count == 0)
        {
            return ["[]"];
        }

        var chunks = new List<string>();
        var currentElements = new List<JsonNode?>();
        var currentSize = 2;

        foreach (var element in data)
        {
            var elementSize = PythonJson.Dumps(element).Length + 2;
            if (currentElements.Count > 0 && currentSize + elementSize > chunkSizeChars)
            {
                chunks.Add(DumpList(currentElements));
                currentElements = GetOverlapElements(currentElements, overlapChars);
                currentSize = currentElements.Count > 0 ? DumpList(currentElements).Length : 2;
            }

            currentElements.Add(element);
            currentSize += elementSize;
        }

        if (currentElements.Count > 0)
        {
            chunks.Add(DumpList(currentElements));
        }

        return chunks.Count > 0 ? chunks : ["[]"];
    }

    private static List<JsonNode?> GetOverlapElements(List<JsonNode?> elements, int overlapChars)
    {
        var overlap = new List<JsonNode?>();
        var currentSize = 2;
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            var elementSize = PythonJson.Dumps(elements[i]).Length + 2;
            if (currentSize + elementSize > overlapChars)
            {
                break;
            }

            overlap.Insert(0, elements[i]);
            currentSize += elementSize;
        }

        return overlap;
    }

    private static List<string> ChunkJsonObject(JsonObject data, int chunkSizeChars, int overlapChars)
    {
        if (data.Count == 0)
        {
            return ["{}"];
        }

        var chunks = new List<string>();
        var currentDict = new List<KeyValuePair<string, JsonNode?>>();
        var currentSize = 2;

        foreach (var entry in data)
        {
            var entrySize = DumpEntries([entry]).Length;
            if (currentDict.Count > 0 && currentSize + entrySize > chunkSizeChars)
            {
                chunks.Add(DumpEntries(currentDict));
                currentDict = GetOverlapEntries(currentDict, overlapChars);
                currentSize = currentDict.Count > 0 ? DumpEntries(currentDict).Length : 2;
            }

            // dict[key] = value: a repeated key (from the overlap) keeps its position.
            var existing = currentDict.FindIndex(pair => pair.Key == entry.Key);
            if (existing >= 0)
            {
                currentDict[existing] = entry;
            }
            else
            {
                currentDict.Add(entry);
            }

            currentSize += entrySize;
        }

        if (currentDict.Count > 0)
        {
            chunks.Add(DumpEntries(currentDict));
        }

        return chunks.Count > 0 ? chunks : ["{}"];
    }

    private static List<KeyValuePair<string, JsonNode?>> GetOverlapEntries(
        List<KeyValuePair<string, JsonNode?>> entries, int overlapChars)
    {
        var overlap = new List<KeyValuePair<string, JsonNode?>>();
        var currentSize = 2;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entrySize = DumpEntries([entries[i]]).Length;
            if (currentSize + entrySize > overlapChars)
            {
                break;
            }

            overlap.Insert(0, entries[i]);
            currentSize += entrySize;
        }

        return overlap;
    }

    private static List<string> ChunkBySentences(string text, int chunkSizeChars, int overlapChars)
    {
        var sentences = SentenceSplit().Split(text);
        var chunks = new List<string>();
        var currentChunk = new List<string>();
        var currentSize = 0;

        foreach (var rawSentence in sentences)
        {
            var sentence = PythonText.Strip(rawSentence);
            if (sentence.Length == 0)
            {
                continue;
            }

            var sentSize = sentence.Length;
            if (sentSize > chunkSizeChars)
            {
                if (currentChunk.Count > 0)
                {
                    chunks.Add(string.Join(' ', currentChunk));
                    currentChunk = [];
                    currentSize = 0;
                }

                chunks.AddRange(ChunkBySize(sentence, chunkSizeChars, overlapChars));
                continue;
            }

            if (currentChunk.Count > 0 && currentSize + sentSize + 1 > chunkSizeChars)
            {
                chunks.Add(string.Join(' ', currentChunk));
                var overlapText = GetOverlapText(string.Join(' ', currentChunk), overlapChars);
                if (overlapText.Length > 0)
                {
                    currentChunk = [overlapText];
                    currentSize = overlapText.Length;
                }
                else
                {
                    currentChunk = [];
                    currentSize = 0;
                }
            }

            currentChunk.Add(sentence);
            currentSize += sentSize + 1;
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(string.Join(' ', currentChunk));
        }

        return chunks;
    }

    private static List<string> ChunkBySize(string text, int chunkSizeChars, int overlapChars)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + chunkSizeChars, text.Length);
            if (end < text.Length && end > start)
            {
                var spaceIndex = text.LastIndexOf(' ', end - 1, end - start);
                if (spaceIndex > start)
                {
                    end = spaceIndex;
                }
            }

            chunks.Add(PythonText.Strip(text[start..end]));

            var minProgress = Math.Max(1, chunkSizeChars - overlapChars);
            start = Math.Max(start + minProgress, end - overlapChars);
        }

        return chunks;
    }

    private static string GetOverlapText(string text, int overlapChars)
    {
        if (text.Length <= overlapChars)
        {
            return text;
        }

        var overlapStart = text.Length - overlapChars;
        var spaceIndex = text.IndexOf(' ', overlapStart);
        return spaceIndex != -1 ? text[(spaceIndex + 1)..] : text[overlapStart..];
    }

    private static List<string> ChunkSpeakerMessages(string content, int chunkSizeChars, int overlapChars)
    {
        var messages = SpeakerSplit().Split(content)
            .Select(PythonText.Strip)
            .Where(message => message.Length > 0)
            .ToList();
        if (messages.Count == 0)
        {
            return [content];
        }

        var chunks = new List<string>();
        var currentMessages = new List<string>();
        var currentSize = 0;

        foreach (var message in messages)
        {
            var messageSize = message.Length;
            if (messageSize > chunkSizeChars)
            {
                if (currentMessages.Count > 0)
                {
                    chunks.Add(string.Join('\n', currentMessages));
                    currentMessages = [];
                    currentSize = 0;
                }

                chunks.Add(message);
                continue;
            }

            if (currentMessages.Count > 0 && currentSize + messageSize + 1 > chunkSizeChars)
            {
                chunks.Add(string.Join('\n', currentMessages));
                currentMessages = GetOverlapMessages(currentMessages, overlapChars);
                currentSize = currentMessages.Sum(m => m.Length) + currentMessages.Count - 1;
            }

            currentMessages.Add(message);
            currentSize += messageSize + 1;
        }

        if (currentMessages.Count > 0)
        {
            chunks.Add(string.Join('\n', currentMessages));
        }

        return chunks.Count > 0 ? chunks : [content];
    }

    private static List<string> GetOverlapMessages(List<string> messages, int overlapChars)
    {
        var overlap = new List<string>();
        var currentSize = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var messageSize = messages[i].Length + 1;
            if (currentSize + messageSize > overlapChars)
            {
                break;
            }

            overlap.Insert(0, messages[i]);
            currentSize += messageSize;
        }

        return overlap;
    }

    private static List<string> ChunkByLines(string content, int chunkSizeChars, int overlapChars)
    {
        var lines = content.Split('\n');
        var chunks = new List<string>();
        var currentLines = new List<string>();
        var currentSize = 0;

        foreach (var line in lines)
        {
            var lineSize = line.Length + 1;
            if (currentLines.Count > 0 && currentSize + lineSize > chunkSizeChars)
            {
                chunks.Add(string.Join('\n', currentLines));
                var overlap = GetOverlapText(string.Join('\n', currentLines), overlapChars);
                if (overlap.Length > 0)
                {
                    currentLines = [.. overlap.Split('\n')];
                    currentSize = overlap.Length;
                }
                else
                {
                    currentLines = [];
                    currentSize = 0;
                }
            }

            currentLines.Add(line);
            currentSize += lineSize;
        }

        if (currentLines.Count > 0)
        {
            chunks.Add(string.Join('\n', currentLines));
        }

        return chunks.Count > 0 ? chunks : [content];
    }

    private static (int, int) Pair(int a, int b) => a < b ? (a, b) : (b, a);

    private static double Combinations(int n, int k)
    {
        double result = 1;
        for (var i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
        }

        return Math.Round(result);
    }

    // itertools.combinations(range(n), k), in lexicographic order.
    private static IEnumerable<int[]> EnumerateCombinations(int n, int k)
    {
        var indices = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return indices;
            var i = k - 1;
            while (i >= 0 && indices[i] == i + n - k)
            {
                i--;
            }

            if (i < 0)
            {
                yield break;
            }

            indices[i]++;
            for (var j = i + 1; j < k; j++)
            {
                indices[j] = indices[j - 1] + 1;
            }
        }
    }

    // Up to MaxCombinationsToEvaluate distinct random combinations, giving up after three times as many draws.
    private static IEnumerable<int[]> SampleCombinations(int n, int k)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pool = Enumerable.Range(0, n).ToArray();
        var totalAttempts = 0;
        var samplesEvaluated = 0;
        while (samplesEvaluated < MaxCombinationsToEvaluate)
        {
            totalAttempts++;
            if (totalAttempts > MaxCombinationsToEvaluate * 3)
            {
                yield break;
            }

            // random.sample(range(n), k), sorted: a partial Fisher-Yates shuffle of the pool.
            for (var i = 0; i < k; i++)
            {
                var j = Random.Shared.Next(i, n);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var combination = pool[..k];
            Array.Sort(combination);
            if (!seen.Add(string.Join(',', combination)))
            {
                continue;
            }

            samplesEvaluated++;
            yield return combination;
        }
    }

    private static string DumpList(List<JsonNode?> elements) =>
        "[" + string.Join(", ", elements.Select(element => PythonJson.Dumps(element))) + "]";

    private static string DumpEntries(IEnumerable<KeyValuePair<string, JsonNode?>> entries) =>
        "{" + string.Join(", ", entries.Select(entry =>
        {
            var sb = new System.Text.StringBuilder();
            PythonJson.WriteString(sb, entry.Key);
            return sb.Append(": ").Append(PythonJson.Dumps(entry.Value)).ToString();
        })) + "}";

    private static bool TryParse(string content, out JsonNode? data)
    {
        try
        {
            data = JsonNode.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            data = null;
            return false;
        }
        catch (ArgumentException)
        {
            data = null;
            return false;
        }
    }

    // Python's str.split(): runs of whitespace, no empty items.
    private static List<string> PySplit(string text) =>
        Whitespace().Split(text).Where(word => word.Length > 0).ToList();

    // Python's str.isupper(): at least one cased character, and no lowercase one.
    private static bool PyIsUpper(string text)
    {
        var hasCased = false;
        foreach (var c in text)
        {
            if (char.IsLower(c))
            {
                return false;
            }

            if (char.IsUpper(c))
            {
                hasCased = true;
            }
        }

        return hasCased;
    }

    private static int Or(int? value, int fallback) => value is { } v && v != 0 ? v : fallback;

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private static double ReadDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private static readonly char[] WordPunctuation = ['.', ',', '!', '?', ';', ':', '\'', '"', '(', ')', '[', ']', '{', '}'];

    // \s here and below is Python's, which also matches U+001C to U+001F.
    [GeneratedRegex(@"\n[\s\x1c-\x1f]*\n")]
    private static partial Regex ParagraphSplit();

    [GeneratedRegex(@"(?<=[.!?])[\s\x1c-\x1f]+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"[\s\x1c-\x1f]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_\s\x1c-\x1f]*):(.+?)(?=^[A-Za-z_][A-Za-z0-9_\s\x1c-\x1f]*:|$)",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SpeakerPattern();

    [GeneratedRegex(@"(?=^[A-Za-z_][A-Za-z0-9_\s\x1c-\x1f]*:)", RegexOptions.Multiline)]
    private static partial Regex SpeakerSplit();
}
