// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/llm_client/test_token_tracker.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Llm;

namespace GraphitiNet.Tests.Llm;

public class TokenUsageTests
{
    [Fact]
    public void Total_tokens_sums_input_and_output()
    {
        Assert.Equal(150, new TokenUsage(100, 50).TotalTokens);
    }

    [Fact]
    public void Default_values_are_zero()
    {
        var usage = new TokenUsage();
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
        Assert.Equal(0, usage.TotalTokens);
    }
}

public class PromptTokenUsageTests
{
    [Fact]
    public void Total_tokens_sums_input_and_output()
    {
        Assert.Equal(1500, new PromptTokenUsage("test", 5, 1000, 500).TotalTokens);
    }

    [Fact]
    public void Average_input_tokens()
    {
        Assert.Equal(250.0, new PromptTokenUsage("test", 4, 1000, 500).AvgInputTokens);
    }

    [Fact]
    public void Average_output_tokens()
    {
        Assert.Equal(125.0, new PromptTokenUsage("test", 4, 1000, 500).AvgOutputTokens);
    }

    [Fact]
    public void Averages_are_zero_with_no_calls()
    {
        var usage = new PromptTokenUsage("test");
        Assert.Equal(0, usage.AvgInputTokens);
        Assert.Equal(0, usage.AvgOutputTokens);
    }
}

public class TokenUsageTrackerTests
{
    [Fact]
    public void Records_a_new_prompt()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record("extract_nodes", 100, 50);

        var usage = tracker.GetUsage();
        Assert.True(usage.ContainsKey("extract_nodes"));
        Assert.Equal(1, usage["extract_nodes"].CallCount);
        Assert.Equal(100, usage["extract_nodes"].TotalInputTokens);
        Assert.Equal(50, usage["extract_nodes"].TotalOutputTokens);
    }

    [Fact]
    public void Accumulates_calls_to_the_same_prompt()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record("extract_nodes", 100, 50);
        tracker.Record("extract_nodes", 200, 100);

        var usage = tracker.GetUsage()["extract_nodes"];
        Assert.Equal(2, usage.CallCount);
        Assert.Equal(300, usage.TotalInputTokens);
        Assert.Equal(150, usage.TotalOutputTokens);
    }

    [Fact]
    public void Records_a_null_prompt_name_as_unknown()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record(null, 100, 50);

        var usage = tracker.GetUsage();
        Assert.True(usage.ContainsKey("unknown"));
        Assert.Equal(1, usage["unknown"].CallCount);
    }

    [Fact]
    public void Records_multiple_prompts()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record("extract_nodes", 100, 50);
        tracker.Record("dedupe_nodes", 200, 100);
        tracker.Record("extract_edges", 150, 75);

        var usage = tracker.GetUsage();
        Assert.Equal(3, usage.Count);
        Assert.Contains("extract_nodes", usage.Keys);
        Assert.Contains("dedupe_nodes", usage.Keys);
        Assert.Contains("extract_edges", usage.Keys);
    }

    [Fact]
    public void GetUsage_returns_a_snapshot()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record("test", 100, 50);

        var first = tracker.GetUsage();
        tracker.Record("test", 1, 1);

        Assert.Equal(100, first["test"].TotalInputTokens);
        Assert.Equal(101, tracker.GetUsage()["test"].TotalInputTokens);
    }

    [Fact]
    public void Totals_across_prompts()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record("extract_nodes", 100, 50);
        tracker.Record("dedupe_nodes", 200, 100);
        tracker.Record("extract_edges", 150, 75);

        var total = tracker.GetTotalUsage();
        Assert.Equal(450, total.InputTokens);
        Assert.Equal(225, total.OutputTokens);
        Assert.Equal(675, total.TotalTokens);
    }

    [Fact]
    public void Totals_are_zero_when_empty()
    {
        var total = new TokenUsageTracker().GetTotalUsage();
        Assert.Equal(0, total.InputTokens);
        Assert.Equal(0, total.OutputTokens);
    }

    [Fact]
    public void Reset_clears_usage()
    {
        var tracker = new TokenUsageTracker();
        tracker.Record("extract_nodes", 100, 50);
        tracker.Record("dedupe_nodes", 200, 100);

        tracker.Reset();

        Assert.Empty(tracker.GetUsage());
        Assert.Equal(0, tracker.GetTotalUsage().TotalTokens);
    }

    [Fact]
    public void Is_safe_across_threads()
    {
        var tracker = new TokenUsageTracker();
        const int threads = 10;
        const int callsPerThread = 100;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, id =>
        {
            for (var i = 0; i < callsPerThread; i++)
            {
                tracker.Record($"prompt_{id}", 10, 5);
            }
        });

        Assert.Equal(threads, tracker.GetUsage().Count);
        var total = tracker.GetTotalUsage();
        Assert.Equal(threads * callsPerThread * 10, total.InputTokens);
        Assert.Equal(threads * callsPerThread * 5, total.OutputTokens);
    }

    [Fact]
    public void Is_safe_across_threads_on_one_prompt()
    {
        var tracker = new TokenUsageTracker();
        const int threads = 10;
        const int callsPerThread = 100;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
        {
            for (var i = 0; i < callsPerThread; i++)
            {
                tracker.Record("shared_prompt", 10, 5);
            }
        });

        var usage = tracker.GetUsage()["shared_prompt"];
        Assert.Equal(threads * callsPerThread, usage.CallCount);
        Assert.Equal(threads * callsPerThread * 10, usage.TotalInputTokens);
        Assert.Equal(threads * callsPerThread * 5, usage.TotalOutputTokens);
    }

    // Not in upstream: print_summary has no test there.
    [Fact]
    public void FormatSummary_lists_prompts_and_totals()
    {
        var tracker = new TokenUsageTracker();
        Assert.Equal("No token usage recorded.", tracker.FormatSummary());

        tracker.Record("small", 10, 5);
        tracker.Record("large", 2000, 1000);

        var lines = tracker.FormatSummary().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var large = lines.FindIndex(l => l.StartsWith("large ", StringComparison.Ordinal));
        var small = lines.FindIndex(l => l.StartsWith("small ", StringComparison.Ordinal));
        Assert.True(large >= 0 && large < small, "rows are sorted by total tokens, most first");
        Assert.Contains("2,000", lines[large], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("TOTAL", StringComparison.Ordinal) && l.Contains("3,015", StringComparison.Ordinal));
    }
}
