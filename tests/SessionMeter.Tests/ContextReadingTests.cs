using SessionMeter.Core.Context;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>Tests for <see cref="ContextReading.ToLine"/> in both the known and unknown forms.</summary>
public sealed class ContextReadingTests
{
    [Fact]
    public void ToLine_known_form_contains_percent_tokens_and_session_id8()
    {
        var reading = new ContextReading(
            Name: "pavbrain",
            SessionId: "ab12cd34-ffff-0000-1111-222233334444",
            UsedTokens: 36_846,
            WindowTokens: 200_000,
            Pct: 18.4,
            AsOf: DateTimeOffset.UtcNow,
            TranscriptPath: @"C:\x\y.jsonl");

        string line = reading.ToLine();

        Assert.True(reading.Known);
        Assert.Contains("%", line);
        Assert.Contains("36,846", line);
        Assert.Contains("200,000", line);
        Assert.Contains("session ab12cd34", line); // first 8 chars of the id
        Assert.Contains("· 200K window (assumed)", line); // undetected default → assumed 200K
    }

    [Fact]
    public void ToLine_appends_1M_detected_suffix_when_window_detected_at_one_million()
    {
        var reading = new ContextReading(
            Name: "sessionmeter",
            SessionId: "ab12cd34",
            UsedTokens: 250_000,
            WindowTokens: 1_000_000,
            Pct: 25.0,
            AsOf: DateTimeOffset.UtcNow,
            TranscriptPath: @"C:\x\y.jsonl",
            Note: null,
            WindowDetected: true,
            Model: "claude-opus-4-8[1m]");

        string line = reading.ToLine();

        Assert.Contains("1,000,000", line);
        Assert.Contains("· 1M window (detected)", line);
    }

    [Fact]
    public void ToLine_appends_200K_detected_suffix_when_window_detected_at_standard()
    {
        var reading = new ContextReading(
            Name: "sessionmeter",
            SessionId: "ab12cd34",
            UsedTokens: 50_000,
            WindowTokens: 200_000,
            Pct: 25.0,
            AsOf: DateTimeOffset.UtcNow,
            TranscriptPath: @"C:\x\y.jsonl",
            Note: null,
            WindowDetected: true,
            Model: "claude-opus-4-8");

        string line = reading.ToLine();

        Assert.Contains("· 200K window (detected)", line);
    }

    [Fact]
    public void ToLine_unknown_form_contains_context_unknown_and_note()
    {
        var reading = new ContextReading(
            Name: "sessionmeter",
            SessionId: null,
            UsedTokens: 0,
            WindowTokens: 200_000,
            Pct: 0,
            AsOf: null,
            TranscriptPath: null,
            Note: "no transcript folder");

        string line = reading.ToLine();

        Assert.False(reading.Known);
        Assert.Contains("context unknown", line);
        Assert.Contains("no transcript folder", line);
    }
}
