// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/test_text_utils.py and tests/utils/test_concatenate_episodes.py (Graphiti v0.31.2) and changed
// for .NET.

using System.Text;
using GraphitiNet.Models;
using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Utils;

public class TextUtilsTests
{
    [Fact]
    public void Truncate_at_sentence_short_text() =>
        Assert.Equal("This is a short sentence.", TextUtils.TruncateAtSentence("This is a short sentence.", 100));

    [Fact]
    public void Truncate_at_sentence_empty() => Assert.Equal("", TextUtils.TruncateAtSentence("", 100));

    [Fact]
    public void Truncate_at_sentence_exact_length()
    {
        var text = new string('A', 100);
        Assert.Equal(text, TextUtils.TruncateAtSentence(text, 100));
    }

    [Theory]
    [InlineData("First sentence. Second sentence. Third sentence. Fourth sentence.", 40, "First sentence. Second sentence.")]
    [InlineData("What is this? This is a test. More text here.", 30, "What is this? This is a test.")]
    [InlineData("Hello world! This is exciting. And more text.", 30, "Hello world! This is exciting.")]
    [InlineData("A. B. C. D. E. F. G. H.", 10, "A. B. C.")]
    [InlineData("First sentence.   Second sentence.", 20, "First sentence.")]
    public void Truncate_at_sentence_boundary(string text, int maxChars, string expected)
    {
        var result = TextUtils.TruncateAtSentence(text, maxChars);
        Assert.Equal(expected, result);
        Assert.True(result.Length <= maxChars + 2);
    }

    [Fact]
    public void Truncate_at_sentence_no_boundary()
    {
        var result = TextUtils.TruncateAtSentence(
            "This is a very long sentence without any punctuation marks near the beginning", 30);
        Assert.True(result.Length <= 30);
        Assert.StartsWith("This is a very long sentence", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal(1000, TextUtils.MaxSummaryChars);
        Assert.Equal(4000, TextUtils.SagaSummaryMaxChars);
    }

    [Fact]
    public void Truncate_at_sentence_realistic_summary()
    {
        const string text =
            "John is a software engineer who works at a tech company in San Francisco. " +
            "He has been programming for over 10 years and specializes in Python and distributed systems. " +
            "John enjoys hiking on weekends and is learning to play guitar. " +
            "He graduated from MIT with a degree in computer science.";
        var result = TextUtils.TruncateAtSentence(text, TextUtils.MaxSummaryChars);
        Assert.True(result.Length <= TextUtils.MaxSummaryChars);
        Assert.EndsWith(".", result, StringComparison.Ordinal);
        Assert.Contains("John is a software engineer", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_utf8_to_bytes_preserves_runes()
    {
        var result = TextUtils.TruncateUtf8ToBytes(new string('é', 10), 7);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= 7);
        Assert.Equal(new string('é', 3), result);
    }

    [Fact]
    public void Truncate_utf8_to_bytes_empty_on_non_positive_budget()
    {
        Assert.Equal("", TextUtils.TruncateUtf8ToBytes("abc", 0));
        Assert.Equal("", TextUtils.TruncateUtf8ToBytes("abc", -1));
    }

    private static EpisodicNode Episode(string content, DateTimeOffset? validAt = null, string? uuid = null) => new()
    {
        Uuid = uuid ?? Guid.NewGuid().ToString(),
        Name = "episode",
        GroupId = "group",
        Source = EpisodeType.Message,
        SourceDescription = "test",
        Content = content,
        ValidAt = validAt ?? new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    private static DateTimeOffset At(int hour, int minute) => new(2025, 1, 1, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Concatenate_single_episode_returns_content_as_is()
    {
        Assert.Equal("Hello world", TextUtils.ConcatenateEpisodes([Episode("Hello world")]));
        Assert.DoesNotContain("[Episode", TextUtils.ConcatenateEpisodes([Episode("No header please")]), StringComparison.Ordinal);
    }

    [Fact]
    public void Concatenate_multiple_episodes_adds_headers_with_timestamps()
    {
        var result = TextUtils.ConcatenateEpisodes(
            [Episode("First", At(10, 0)), Episode("Second", At(10, 5)), Episode("Third", At(10, 10))]);
        Assert.Contains("[Episode 0] (timestamp: 2025-01-01T10:00:00+00:00)\nFirst", result, StringComparison.Ordinal);
        Assert.Contains("[Episode 1] (timestamp: 2025-01-01T10:05:00+00:00)\nSecond", result, StringComparison.Ordinal);
        Assert.Contains("[Episode 2] (timestamp: 2025-01-01T10:10:00+00:00)\nThird", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Concatenate_multiple_episodes_separated_by_blank_line()
    {
        var ts = new DateTimeOffset(2025, 6, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "[Episode 0] (timestamp: 2025-06-15T08:00:00+00:00)\nA\n\n[Episode 1] (timestamp: 2025-06-15T08:00:00+00:00)\nB",
            TextUtils.ConcatenateEpisodes([Episode("A", ts), Episode("B", ts)]));
    }

    [Fact]
    public void Concatenate_timeline_numbers_extraction_targets_and_marks_context_turns()
    {
        var a = Episode("user says hi", At(10, 0), "a");
        var b = Episode("assistant replies", At(10, 1), "b");
        var c = Episode("user follows up", At(10, 2), "c");

        var result = TextUtils.ConcatenateTimeline([a, c], [a, b, c]);

        var episode0 = result.IndexOf("[Episode 0] (timestamp: 2025-01-01T10:00:00+00:00)", StringComparison.Ordinal);
        var context = result.IndexOf("[CONTEXT EPISODE] (timestamp: 2025-01-01T10:01:00+00:00)", StringComparison.Ordinal);
        var episode1 = result.IndexOf("[Episode 1] (timestamp: 2025-01-01T10:02:00+00:00)", StringComparison.Ordinal);
        Assert.True(episode0 >= 0 && episode0 < context && context < episode1);
        Assert.Contains("assistant replies", result, StringComparison.Ordinal);
    }
}
