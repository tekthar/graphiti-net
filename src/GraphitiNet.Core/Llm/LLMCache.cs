// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/cache.py (Graphiti v0.31.2) and changed for .NET.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphitiNet.Llm;

/// <summary>
/// A simple on-disk cache of LLM responses, stored as JSON. Upstream keeps the entries in a SQLite file
/// (<c>cache.db</c>); this port keeps one JSON file per key in the directory, which needs no native dependency. Only
/// JSON is stored, so reading an entry never runs code.
/// </summary>
public sealed class LLMCache : IDisposable
{
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    /// <summary>Opens (and creates, if needed) a cache in <paramref name="directory"/>.</summary>
    public LLMCache(string directory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        DirectoryPath = directory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The directory that holds the entries.</summary>
    public string DirectoryPath { get; }

    /// <summary>The cached value for <paramref name="key"/>, or <see langword="null"/> if there is none or the
    /// entry is corrupted.</summary>
    public JsonElement? Get(string key)
    {
        var path = PathFor(key);
        string text;
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path, Encoding.UTF8);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            ProviderLog.CorruptedCacheEntry(_logger, key);
            return null;
        }
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing any earlier value.</summary>
    public void Set(string key, JsonElement value)
    {
        var path = PathFor(key);
        var json = value.GetRawText();
        lock (_lock)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Releases the cache. Kept for parity with upstream's <c>close()</c>; there is nothing to release.</summary>
    public void Dispose()
    {
    }

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        // Keys are hex digests in practice; escape anything else so a key can't leave the directory.
        var safe = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                safe.Append(c);
            }
            else
            {
                safe.Append('%').Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return Path.Combine(DirectoryPath, safe + ".json");
    }
}
