using SessionMeter.Core.Configuration;
using SessionMeter.Core.Context;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>Tests for the pure, static context-scan helpers and the unknown-reading path of <see cref="ContextMonitor"/>.</summary>
public sealed class ContextMonitorTests
{
    [Theory]
    [InlineData(@"C:\dev\mo", "C--dev-mo")]
    [InlineData(@"C:\Dev\PAV\PAVBrain", "C--Dev-PAV-PAVBrain")]
    [InlineData(@"C:\dev\mo\", "C--dev-mo")] // trailing slash trimmed before encoding
    public void EncodeCwd_maps_separators_and_trims_trailing(string cwd, string expected)
        => Assert.Equal(expected, ContextMonitor.EncodeCwd(cwd));

    [Fact]
    public void ScanLastUsage_last_usage_line_wins_and_sums_three_input_fields()
    {
        string[] lines =
        {
            """{"message":{"usage":{"input_tokens":10,"cache_read_input_tokens":20,"cache_creation_input_tokens":30}}}""",
            """{"message":{"usage":{"input_tokens":100,"cache_read_input_tokens":200,"cache_creation_input_tokens":300}}}""",
        };

        UsageScan scan = ContextMonitor.ScanLastUsage(lines);

        Assert.True(scan.Found);
        Assert.Equal(600, scan.UsedTokens); // 100 + 200 + 300 — the LAST usage-bearing line
    }

    [Fact]
    public void ScanLastUsage_skips_malformed_trailing_line()
    {
        string[] lines =
        {
            """{"message":{"usage":{"input_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""",
            """{"message":{"usage":{"input_tokens":7,""", // partially-written tail — must be skipped
        };

        UsageScan scan = ContextMonitor.ScanLastUsage(lines);

        Assert.True(scan.Found);
        Assert.Equal(5, scan.UsedTokens); // falls back to the last WELL-FORMED usage line
    }

    [Fact]
    public void ScanLastUsage_no_usage_returns_not_found()
    {
        string[] lines =
        {
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"summary"}""",
        };

        UsageScan scan = ContextMonitor.ScanLastUsage(lines);

        Assert.False(scan.Found);
        Assert.Equal(0, scan.UsedTokens);
    }

    [Theory]
    [InlineData(100_000, 200_000, 50.0)]
    [InlineData(36_846, 200_000, 18.4)] // one-decimal rounding
    [InlineData(500_000, 200_000, 100.0)] // clamps at 100
    [InlineData(50, 0, 0.0)]              // window <= 0 => 0
    [InlineData(0, 200_000, 0.0)]         // used <= 0 => 0
    public void ComputePct_clamps_and_rounds_to_one_decimal(long used, long window, double expected)
        => Assert.Equal(expected, ContextMonitor.ComputePct(used, window));

    [Fact]
    public void ScanLastUsage_captures_message_model_on_the_measured_line()
    {
        string[] lines =
        {
            """{"message":{"model":"claude-opus-4-8","usage":{"input_tokens":100,"cache_read_input_tokens":200,"cache_creation_input_tokens":300}}}""",
        };

        UsageScan scan = ContextMonitor.ScanLastUsage(lines);

        Assert.True(scan.Found);
        Assert.Equal("claude-opus-4-8", scan.Model);
    }

    [Fact]
    public void Read_detects_1M_window_from_claude_json_and_computes_pct_against_one_million()
    {
        const string cwd = @"C:\dev\smtest";
        string profile = Path.Combine(Path.GetTempPath(), "sm-int-" + Guid.NewGuid().ToString("N"));

        // 1) transcript: <profile>\.claude\projects\<encoded>\<session>.jsonl with a model + usage line.
        string projectDir = ContextMonitor.ProjectsDirFor(profile, cwd);
        Directory.CreateDirectory(projectDir);
        string transcript = Path.Combine(projectDir, "11112222-3333-4444-5555-666677778888.jsonl");
        File.WriteAllText(transcript,
            """{"timestamp":"2026-07-04T21:00:00+00:00","message":{"model":"claude-opus-4-8","usage":{"input_tokens":200000,"cache_read_input_tokens":40000,"cache_creation_input_tokens":10000}}}""");

        // 2) .claude.json marking the [1m] beta for that cwd (backslash key to also exercise normalization).
        File.WriteAllText(Path.Combine(profile, ".claude.json"),
            """
            { "projects": { "C:\\dev\\smtest": { "lastModelUsage": {
                "claude-opus-4-8[1m]": { "inputTokens": 200000, "cacheReadInputTokens": 40000, "cacheCreationInputTokens": 10000 }
            } } } }
            """);
        try
        {
            var monitor = new ContextMonitor(new MeterConfig()); // fallback still 200K
            ContextReading reading = monitor.Read(cwd, name: "smtest", userProfile: profile);

            Assert.True(reading.Known);
            Assert.True(reading.WindowDetected);
            Assert.Equal(1_000_000, reading.WindowTokens);
            Assert.Equal(250_000, reading.UsedTokens);          // 200000 + 40000 + 10000
            Assert.Equal(25.0, reading.Pct);                     // 250K / 1M — NOT 100% against 200K
            Assert.Contains("· 1M window (detected)", reading.ToLine());
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void Read_missing_transcript_folder_yields_wellformed_unknown_reading()
    {
        // Point the userProfile at an empty temp dir so no .claude\projects tree exists.
        string tempProfile = Path.Combine(Path.GetTempPath(), "sm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        try
        {
            var monitor = new ContextMonitor(new MeterConfig());
            ContextReading reading = monitor.Read(@"C:\dev\does-not-exist", name: null, userProfile: tempProfile);

            Assert.False(reading.Known);
            Assert.NotNull(reading.Note);
            Assert.Equal(0, reading.UsedTokens);
            Assert.Equal(200_000, reading.WindowTokens); // the default denominator
            Assert.Contains("context unknown", reading.ToLine());
        }
        finally
        {
            Directory.Delete(tempProfile, recursive: true);
        }
    }
}
