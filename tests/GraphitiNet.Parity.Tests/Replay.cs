// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;

namespace GraphitiNet.Parity.Tests;

/// <summary>Thrown when the port sends a prompt or embeds a text upstream never did.</summary>
public sealed class UnrecordedCallException(string message) : Exception(message);

/// <summary>
/// An LLM client that answers only from a <see cref="Recording"/>. A prompt worded differently from upstream has no
/// recording, so the call fails and names the closest recorded prompt and where the two first differ.
/// </summary>
public sealed class ReplayLLMClient(Recording recording) : LLMClient(new LLMConfig(model: "replay"))
{
    /// <summary>Prompts that were answered, by key.</summary>
    public ConcurrentDictionary<string, int> Hits { get; } = new();

    /// <summary>Prompts that had no recording.</summary>
    public ConcurrentBag<string> Misses { get; } = [];

    /// <inheritdoc />
    public override Task<JsonElement> GenerateResponseAsync(
        IList<Message> messages,
        Type? responseModel = null,
        int? maxTokens = null,
        ModelSize modelSize = ModelSize.Medium,
        string? groupId = null,
        string? promptName = null,
        bool attributeExtraction = false,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var key = Recording.Key(messages);
        if (recording.Calls.TryGetValue(key, out var call))
        {
            Hits.AddOrUpdate(key, 1, (_, n) => n + 1);
            TokenTracker.Record(promptName, 0, 0);
            return Task.FromResult(call.Response);
        }

        var report = DescribeMiss(promptName, messages);
        Misses.Add(report);
        throw new UnrecordedCallException(report);
    }

    /// <inheritdoc />
    protected override Task<LLMClientResponse> GenerateResponseCoreAsync(
        IList<Message> messages,
        Type? responseModel,
        int maxTokens,
        ModelSize modelSize,
        string? model,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Replay answers in GenerateResponseAsync.");

    private string DescribeMiss(string? promptName, IList<Message> messages)
    {
        var sb = new StringBuilder($"No recorded answer for prompt '{promptName}'.");
        var candidates = recording.Calls.Values.Where(c => c.PromptName == promptName).ToList();
        if (candidates.Count == 0)
        {
            sb.Append(" Upstream never called this prompt in this dataset.");
            return sb.ToString();
        }

        // The closest recorded prompt: the one sharing the longest prefix with ours.
        var ours = string.Join("\n---\n", messages.Select(m => $"[{m.Role}]\n{m.Content}"));
        var best = candidates
            .Select(c => string.Join("\n---\n", c.Messages.Select(m => $"[{m.Role}]\n{m.Content}")))
            .Select(text => (Text: text, Common: CommonPrefix(ours, text)))
            .MaxBy(x => x.Common);
        var at = best.Common;
        sb.Append(CultureInfo.InvariantCulture, $" {candidates.Count} recorded with this name; the closest first differs at character {at}:\n");
        sb.Append(CultureInfo.InvariantCulture, $"  port:     ...{Excerpt(ours, at)}...\n");
        sb.Append(CultureInfo.InvariantCulture, $"  upstream: ...{Excerpt(best.Text, at)}...");
        return sb.ToString();
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    private static string Excerpt(string text, int at)
    {
        var start = Math.Max(0, at - 60);
        var end = Math.Min(text.Length, at + 60);
        return JsonSerializer.Serialize(text[start..end]);
    }
}

/// <summary>An embedder that answers only from a <see cref="Recording"/>.</summary>
public sealed class ReplayEmbedder(Recording recording) : EmbedderClient
{
    /// <summary>Texts that had no recorded embedding.</summary>
    public ConcurrentBag<string> Misses { get; } = [];

    /// <inheritdoc />
    public override Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default)
    {
        if (recording.Embeddings.TryGetValue(inputData, out var vector))
        {
            return Task.FromResult(vector);
        }

        Misses.Add(inputData);
        throw new UnrecordedCallException($"No recorded embedding for {JsonSerializer.Serialize(inputData)}.");
    }
}
