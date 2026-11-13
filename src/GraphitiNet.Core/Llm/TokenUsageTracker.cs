// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from graphiti_core/llm_client/token_tracker.py (Graphiti v0.31.2) and changed for .NET.

using System.Globalization;
using System.Text;

namespace GraphitiNet.Llm;

/// <summary>Token usage for one LLM call, or a total.</summary>
public readonly record struct TokenUsage(long InputTokens = 0, long OutputTokens = 0)
{
    /// <summary>Input plus output tokens.</summary>
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>Token usage accumulated for one prompt.</summary>
public sealed record PromptTokenUsage(
    string PromptName,
    long CallCount = 0,
    long TotalInputTokens = 0,
    long TotalOutputTokens = 0)
{
    /// <summary>Input plus output tokens.</summary>
    public long TotalTokens => TotalInputTokens + TotalOutputTokens;

    /// <summary>Mean input tokens per call, or 0 if there were no calls.</summary>
    public double AvgInputTokens => CallCount > 0 ? (double)TotalInputTokens / CallCount : 0;

    /// <summary>Mean output tokens per call, or 0 if there were no calls.</summary>
    public double AvgOutputTokens => CallCount > 0 ? (double)TotalOutputTokens / CallCount : 0;
}

/// <summary>How <see cref="TokenUsageTracker.FormatSummary"/> orders its rows.</summary>
public enum TokenUsageSortBy
{
    /// <summary>Most total tokens first.</summary>
    TotalTokens,

    /// <summary>Most input tokens first.</summary>
    InputTokens,

    /// <summary>Most output tokens first.</summary>
    OutputTokens,

    /// <summary>Most calls first.</summary>
    CallCount,

    /// <summary>Prompt name, A to Z.</summary>
    PromptName,
}

/// <summary>Thread-safe tracker of LLM token usage per prompt.</summary>
public sealed class TokenUsageTracker
{
    /// <summary>The key used when a call has no prompt name.</summary>
    public const string UnknownPromptName = "unknown";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, PromptTokenUsage> _usage = [];

    /// <summary>Records one call's token usage.</summary>
    /// <param name="promptName">The prompt's name, such as <c>extract_nodes.extract_message</c>.</param>
    /// <param name="inputTokens">Input tokens used.</param>
    /// <param name="outputTokens">Output tokens generated.</param>
    public void Record(string? promptName, long inputTokens, long outputTokens)
    {
        var key = string.IsNullOrEmpty(promptName) ? UnknownPromptName : promptName;

        lock (_lock)
        {
            var current = _usage.GetValueOrDefault(key) ?? new PromptTokenUsage(key);
            _usage[key] = current with
            {
                CallCount = current.CallCount + 1,
                TotalInputTokens = current.TotalInputTokens + inputTokens,
                TotalOutputTokens = current.TotalOutputTokens + outputTokens,
            };
        }
    }

    /// <summary>A snapshot of usage per prompt.</summary>
    public IReadOnlyDictionary<string, PromptTokenUsage> GetUsage()
    {
        lock (_lock)
        {
            return new Dictionary<string, PromptTokenUsage>(_usage);
        }
    }

    /// <summary>Total usage across all prompts.</summary>
    public TokenUsage GetTotalUsage()
    {
        lock (_lock)
        {
            return new TokenUsage(
                _usage.Values.Sum(u => u.TotalInputTokens),
                _usage.Values.Sum(u => u.TotalOutputTokens));
        }
    }

    /// <summary>Clears all recorded usage.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _usage.Clear();
        }
    }

    /// <summary>
    /// A text table of usage per prompt, with totals. Upstream prints this (<c>print_summary</c>); a library should
    /// not write to the console, so this returns the text for the caller to log or print.
    /// </summary>
    public string FormatSummary(TokenUsageSortBy sortBy = TokenUsageSortBy.TotalTokens)
    {
        var usage = GetUsage();
        if (usage.Count == 0)
        {
            return "No token usage recorded.";
        }

        var rows = sortBy switch
        {
            TokenUsageSortBy.InputTokens => usage.Values.OrderByDescending(u => u.TotalInputTokens),
            TokenUsageSortBy.OutputTokens => usage.Values.OrderByDescending(u => u.TotalOutputTokens),
            TokenUsageSortBy.CallCount => usage.Values.OrderByDescending(u => u.CallCount),
            TokenUsageSortBy.PromptName => usage.Values.OrderBy(u => u.PromptName, StringComparer.Ordinal),
            _ => usage.Values.OrderByDescending(u => u.TotalTokens),
        };

        var c = CultureInfo.InvariantCulture;
        var rule = new string('=', 100);
        var thin = new string('-', 100);
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine(rule).AppendLine("TOKEN USAGE SUMMARY").AppendLine(rule);
        sb.AppendLine(c, $"{"Prompt Type",-45} {"Calls",8} {"Input",12} {"Output",12} {"Total",12} {"Avg In",10} {"Avg Out",10}");
        sb.AppendLine(thin);
        foreach (var u in rows)
        {
            sb.AppendLine(c,
                $"{u.PromptName,-45} {u.CallCount,8} {u.TotalInputTokens,12:N0} {u.TotalOutputTokens,12:N0} {u.TotalTokens,12:N0} {u.AvgInputTokens,10:N1} {u.AvgOutputTokens,10:N1}");
        }

        var total = GetTotalUsage();
        sb.AppendLine(thin);
        sb.AppendLine(c,
            $"{"TOTAL",-45} {usage.Values.Sum(u => u.CallCount),8} {total.InputTokens,12:N0} {total.OutputTokens,12:N0} {total.TotalTokens,12:N0}");
        sb.AppendLine(rule);
        return sb.ToString();
    }
}
