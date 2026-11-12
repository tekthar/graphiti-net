// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphitiNet.Prompts;

namespace GraphitiNet.Parity.Tests;

/// <summary>One recorded LLM call from Python Graphiti.</summary>
public sealed record RecordedCall(
    string? PromptName,
    string? ResponseModel,
    IReadOnlyList<(string Role, string Content)> Messages,
    JsonElement Response);

/// <summary>
/// A recording made by <c>parity/python/run_upstream.py --record</c>: every prompt upstream sent with its answer, and
/// every embedding.
/// </summary>
public sealed class Recording
{
    private Recording(
        IReadOnlyDictionary<string, RecordedCall> calls,
        IReadOnlyDictionary<string, float[]> embeddings,
        JsonElement settings)
    {
        Calls = calls;
        Embeddings = embeddings;
        Settings = settings;
    }

    /// <summary>Recorded LLM calls by <see cref="Key"/>.</summary>
    public IReadOnlyDictionary<string, RecordedCall> Calls { get; }

    /// <summary>Recorded embeddings by input text.</summary>
    public IReadOnlyDictionary<string, float[]> Embeddings { get; }

    /// <summary>The models and settings the recording was made with.</summary>
    public JsonElement Settings { get; }

    /// <summary>The embedding size the recording was made with.</summary>
    public int EmbeddingDim => Settings.GetProperty("embedding_dim").GetInt32();

    /// <summary>
    /// The replay key: SHA-256 of the prompt messages as the prompt library built them, as
    /// <c>recording_key()</c> in <c>run_upstream.py</c>.
    /// </summary>
    public static string Key(IEnumerable<(string Role, string Content)> messages)
    {
        var joined = string.Join("␞", messages.Select(m => $"{m.Role}␟{m.Content}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    /// <summary>The replay key of library-built messages.</summary>
    public static string Key(IEnumerable<Message> messages) => Key(messages.Select(m => (m.Role, m.Content)));

    /// <summary>Loads <c>parity/recordings/&lt;dataset&gt;.recording.json</c> from the test output folder.</summary>
    public static Recording Load(string dataset)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ParityPaths.Recording(dataset)));
        var root = doc.RootElement;
        var calls = new Dictionary<string, RecordedCall>();
        foreach (var call in root.GetProperty("llm").EnumerateObject())
        {
            var v = call.Value;
            calls[call.Name] = new RecordedCall(
                v.GetProperty("prompt_name").GetString(),
                v.GetProperty("response_model").GetString(),
                v.GetProperty("messages").EnumerateArray()
                    .Select(m => (m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!))
                    .ToList(),
                v.GetProperty("response").Clone());
        }

        var embeddings = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var e in root.GetProperty("embeddings").EnumerateObject())
        {
            var bytes = Convert.FromBase64String(e.Value.GetString()!);
            var vector = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            embeddings[e.Name] = vector;
        }

        return new Recording(calls, embeddings, root.GetProperty("settings").Clone());
    }
}

/// <summary>Where the parity files are copied in the test output.</summary>
public static class ParityPaths
{
    /// <summary>The dataset file.</summary>
    public static string Dataset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "parity", "datasets", $"{name}.json");

    /// <summary>The recording file.</summary>
    public static string Recording(string name) =>
        Path.Combine(AppContext.BaseDirectory, "parity", "recordings", $"{name}.recording.json");

    /// <summary>Upstream's result file.</summary>
    public static string Upstream(string name) =>
        Path.Combine(AppContext.BaseDirectory, "parity", "recordings", $"{name}.upstream.json");
}
