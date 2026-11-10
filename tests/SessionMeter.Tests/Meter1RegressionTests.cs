using SessionMeter.Core.Context;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>
/// METER-1 (0.8.3, 2026-11-10): (a) a bare 5-series key in <c>.claude.json</c> is the 1M window, not 200K;
/// (b) a <c>compact_boundary</c> newer than every usage line is the measurement.
/// </summary>
public sealed class Meter1RegressionTests
{
    private static string WriteClaudeJson(string projectsBody)
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-m1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, ".claude.json"),
            $$"""{ "projects": { {{projectsBody}} } }""");
        return profile;
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-fable-5-2")]
    public void Bare_5_series_key_matching_the_transcript_model_is_the_1M_window(string model)
    {
        // The live shape on 2026-11-10: C:/PKM's lastModelUsage recorded "claude-opus-5-5" bare.
        string profile = WriteClaudeJson(
            $$"""
            "C:/PKM": { "lastModelUsage": {
                "{{model}}": { "inputTokens": 1000, "cacheReadInputTokens": 2000, "cacheCreationInputTokens": 3000 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\PKM", model, profile, 200_000);
            Assert.Equal(1_000_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal(model, r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Usage_ranked_guess_on_a_bare_5_series_key_is_the_1M_window()
    {
        string profile = WriteClaudeJson(
            """
            "C:/PKM": { "lastModelUsage": {
                "claude-haiku-4-5-20251001": { "inputTokens": 10, "cacheReadInputTokens": 10, "cacheCreationInputTokens": 10 },
                "claude-opus-5-5": { "inputTokens": 9000, "cacheReadInputTokens": 9000, "cacheCreationInputTokens": 9000 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\PKM", baseModel: null, profile, 200_000);
            Assert.Equal(1_000_000, r.Window);
            Assert.Equal("claude-opus-5-5", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    private const string Usage482k =
        """{"type":"assistant","timestamp":"2026-11-09T14:43:02Z","message":{"model":"claude-opus-5-5","usage":{"input_tokens":2,"cache_read_input_tokens":400000,"cache_creation_input_tokens":82722,"output_tokens":10}}}""";
    private const string Boundary28k =
        """{"type":"system","subtype":"compact_boundary","content":"Conversation compacted","timestamp":"2026-11-09T15:38:10Z","compactMetadata":{"trigger":"auto","preTokens":482724,"postTokens":28179}}""";
    private const string Usage99k =
        """{"type":"assistant","timestamp":"2026-11-09T15:40:00Z","message":{"model":"claude-opus-5-5","usage":{"input_tokens":3,"cache_read_input_tokens":34447,"cache_creation_input_tokens":64704,"output_tokens":5}}}""";

    [Fact]
    public void A_compact_boundary_newer_than_every_usage_line_is_the_measurement()
    {
        UsageScan scan = ContextMonitor.ScanLastUsage(new[] { Usage482k, Boundary28k, """{"type":"user","message":{"content":"summary"}}""" });

        Assert.True(scan.Found);
        Assert.True(scan.Compacted);
        Assert.Equal(28_179, scan.UsedTokens);
        Assert.Equal("claude-opus-5-5", scan.Model);   // model still taken from the older usage line
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T15:38:10Z"), scan.Timestamp);
    }

    [Fact]
    public void A_reply_after_the_boundary_makes_the_usage_line_current_again()
    {
        UsageScan scan = ContextMonitor.ScanLastUsage(new[] { Usage482k, Boundary28k, Usage99k });

        Assert.False(scan.Compacted);
        Assert.Equal(99_154, scan.UsedTokens);
    }

    [Fact]
    public void Without_a_boundary_the_last_usage_line_still_wins()
    {
        UsageScan scan = ContextMonitor.ScanLastUsage(new[] { Usage99k, Usage482k });

        Assert.False(scan.Compacted);
        Assert.Equal(482_724, scan.UsedTokens);
    }
}
