// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/llm_client/test_cache.py (Graphiti v0.31.2) and changed for .NET. The cache stores JSON
// elements, so upstream's "non-serializable value" case can't happen and is not ported.

using System.Text.Json;
using GraphitiNet.Llm;

namespace GraphitiNet.Tests.Llm;

public sealed class LLMCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graphiti-net-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void AssertJsonEqual(string expected, JsonElement? actual)
    {
        Assert.NotNull(actual);
        Assert.True(JsonElement.DeepEquals(Json(expected), actual.Value), $"{expected} != {actual}");
    }

    [Fact]
    public void GetMissingKey_ReturnsNull()
    {
        using var cache = new LLMCache(Path.Combine(_root, "test_cache"));
        Assert.Null(cache.Get("nonexistent"));
    }

    [Fact]
    public void SetAndGet()
    {
        using var cache = new LLMCache(Path.Combine(_root, "test_cache"));
        cache.Set("key1", Json("""{"content": "hello", "tokens": 42}"""));
        AssertJsonEqual("""{"content": "hello", "tokens": 42}""", cache.Get("key1"));
    }

    [Fact]
    public void SetOverwritesExisting()
    {
        using var cache = new LLMCache(Path.Combine(_root, "test_cache"));
        cache.Set("key1", Json("""{"version": 1}"""));
        cache.Set("key1", Json("""{"version": 2}"""));
        AssertJsonEqual("""{"version": 2}""", cache.Get("key1"));
    }

    [Fact]
    public void MultipleKeys()
    {
        using var cache = new LLMCache(Path.Combine(_root, "test_cache"));
        cache.Set("a", Json("""{"val": 1}"""));
        cache.Set("b", Json("""{"val": 2}"""));
        cache.Set("c", Json("""{"val": 3}"""));
        AssertJsonEqual("""{"val": 1}""", cache.Get("a"));
        AssertJsonEqual("""{"val": 2}""", cache.Get("b"));
        AssertJsonEqual("""{"val": 3}""", cache.Get("c"));
    }

    [Fact]
    public void ComplexNestedValue()
    {
        const string value = """
            {
              "choices": [{"message": {"role": "assistant", "content": "test"}}],
              "usage": {"prompt_tokens": 10, "completion_tokens": 5},
              "nested": {"a": [1, 2, 3], "b": null, "c": true}
            }
            """;
        using var cache = new LLMCache(Path.Combine(_root, "test_cache"));
        cache.Set("complex", Json(value));
        AssertJsonEqual(value, cache.Get("complex"));
    }

    [Fact]
    public void CorruptedEntry_ReturnsNull()
    {
        var directory = Path.Combine(_root, "test_cache");
        using var cache = new LLMCache(directory);
        File.WriteAllText(Path.Combine(directory, "corrupt.json"), "not valid json{{{");
        Assert.Null(cache.Get("corrupt"));
    }

    [Fact]
    public void CreatesDirectory()
    {
        var directory = Path.Combine(_root, "nested", "dir", "cache");
        using var cache = new LLMCache(directory);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        var directory = Path.Combine(_root, "persist_cache");
        using (var c1 = new LLMCache(directory))
        {
            c1.Set("persist_key", Json("""{"data": "survives"}"""));
        }

        using var c2 = new LLMCache(directory);
        AssertJsonEqual("""{"data": "survives"}""", c2.Get("persist_key"));
    }

    [Fact]
    public void KeysCannotEscapeTheDirectory()
    {
        var directory = Path.Combine(_root, "safe");
        using var cache = new LLMCache(directory);
        cache.Set("../outside", Json("{}"));
        Assert.Empty(Directory.GetFiles(_root));
        Assert.NotNull(cache.Get("../outside"));
    }

    [Fact]
    public void DisposeTwice_DoesNotThrow()
    {
        var cache = new LLMCache(Path.Combine(_root, "close_test"));
        cache.Dispose();
        cache.Dispose();
    }
}
