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

    /// <summary>
    /// 🛑 THE DOT IS A SEPARATOR TOO, and omitting it made SessionMeter blind to EVERY worktree session.
    ///
    /// <para>Claude Code's own worktrees live under <c>&lt;repo&gt;\.claude\worktrees\&lt;name&gt;</c>, so a
    /// session started with EnterWorktree always has a dot-segment in its cwd. Encoding only <c>:</c>,
    /// <c>\</c> and <c>/</c> produced <c>…-.claude-worktrees-…</c>, a folder that does not exist, and the
    /// caller then fell back to the parent repo — where "newest transcript wins" confidently returned a
    /// DIFFERENT session's percentage with no error. A wrong context reading triggered a false Rule 3
    /// stop-work on 2026-08-11.</para>
    ///
    /// <para>The expected value below is not derived from the rule — it is the REAL folder name observed on
    /// disk at <c>C:\Users\&lt;user&gt;\.claude\projects\</c> for that exact cwd, which is what makes this a
    /// test of Claude Code's behaviour rather than of our restatement of it.</para>
    /// </summary>
    [Theory]
    [InlineData(
        @"C:\Dev\PAV\PAVBrain\.claude\worktrees\SelfAwareness",
        "C--Dev-PAV-PAVBrain--claude-worktrees-SelfAwareness")]
    [InlineData(@"C:\dev\my.app", "C--dev-my-app")]
    public void EncodeCwd_maps_the_dot_as_well_so_worktree_sessions_resolve(string cwd, string expected)
        => Assert.Equal(expected, ContextMonitor.EncodeCwd(cwd));

    /// <summary>
    /// The negative control that stops the fix from over-reaching: a SPACE is NOT encoded. Observed on disk
    /// as <c>C--Dev-MQL4-IntervalTrade EA</c>, so a "sanitise every non-alphanumeric" implementation would
    /// break a folder that currently resolves correctly.
    /// </summary>
    [Fact]
    public void EncodeCwd_preserves_spaces()
        => Assert.Equal("C--Dev-MQL4-IntervalTrade EA", ContextMonitor.EncodeCwd(@"C:\Dev\MQL4\IntervalTrade EA"));

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
    public void Read_escalates_to_1M_when_undetected_usage_exceeds_the_200K_assumption()
    {
        // 2026-08-28 root cause: an UNRECOGNISED model with no bracket/family evidence anywhere still ran a
        // real session past 200K tokens (409,308 observed live). Reporting "100.0% (409,308 /
        // 200,000)" is arithmetic announcing its own impossibility — Claude compacts before a session can
        // genuinely overrun a window it is actually bound by. The escalation safety net must catch this even
        // for a model name the family tier has never heard of.
        const string cwd = @"C:\dev\smtest2";
        string profile = Path.Combine(Path.GetTempPath(), "sm-int-" + Guid.NewGuid().ToString("N"));

        string projectDir = ContextMonitor.ProjectsDirFor(profile, cwd);
        Directory.CreateDirectory(projectDir);
        string transcript = Path.Combine(projectDir, "aaaa1111-2222-3333-4444-555566667777.jsonl");
        File.WriteAllText(transcript,
            """{"timestamp":"2026-08-28T07:00:00+00:00","message":{"model":"claude-mystery-6","usage":{"input_tokens":300000,"cache_read_input_tokens":100000,"cache_creation_input_tokens":9308}}}""");
        // Deliberately NO .claude.json and NO settings.json — nothing on disk names this model at all.

        try
        {
            var monitor = new ContextMonitor(new MeterConfig()); // fallback still 200K
            ContextReading reading = monitor.Read(cwd, name: "smtest2", userProfile: profile);

            Assert.True(reading.Known);
            Assert.False(reading.WindowDetected); // still not a stated/known fact — an inferred correction
            Assert.Equal(1_000_000, reading.WindowTokens);
            Assert.Equal(409_308, reading.UsedTokens);
            Assert.Equal(40.9, reading.Pct); // 409,308 / 1,000,000, NOT the impossible 100% against 200K
            Assert.DoesNotContain("USED EXCEEDS WINDOW", reading.ToLine());
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void Read_does_not_escalate_when_a_detected_window_is_merely_surprising()
    {
        // Precedence guard: escalation is a LAST-RESORT correction for an undetected assumption, never a
        // second-guess of a real detected fact (e.g. an explicit bare 200K key genuinely at/over capacity).
        const string cwd = @"C:\dev\smtest3";
        string profile = Path.Combine(Path.GetTempPath(), "sm-int-" + Guid.NewGuid().ToString("N"));

        string projectDir = ContextMonitor.ProjectsDirFor(profile, cwd);
        Directory.CreateDirectory(projectDir);
        string transcript = Path.Combine(projectDir, "bbbb1111-2222-3333-4444-555566667777.jsonl");
        File.WriteAllText(transcript,
            """{"timestamp":"2026-08-28T07:00:00+00:00","message":{"model":"claude-opus-4-8","usage":{"input_tokens":190000,"cache_read_input_tokens":9000,"cache_creation_input_tokens":6000}}}""");
        File.WriteAllText(Path.Combine(profile, ".claude.json"),
            """
            { "projects": { "C:\\dev\\smtest3": { "lastModelUsage": {
                "claude-opus-4-8": { "inputTokens": 190000, "cacheReadInputTokens": 9000, "cacheCreationInputTokens": 6000 }
            } } } }
            """);

        try
        {
            var monitor = new ContextMonitor(new MeterConfig());
            ContextReading reading = monitor.Read(cwd, name: "smtest3", userProfile: profile);

            Assert.True(reading.WindowDetected);
            Assert.Equal(200_000, reading.WindowTokens); // NOT escalated — this IS a real, stated 200K session
            Assert.Equal(100.0, reading.Pct); // clamped, genuinely near-full
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void Read_prefers_the_native_window_cache_over_every_name_based_tier_for_an_unknown_future_model()
    {
        // 2026-08-28: the genuine future-proofing test. "claude-zeta-9" is in NO bracket list, NO family
        // list, and NO .claude.json/settings.json evidence exists — yet a statusline integration (see
        // INTEGRATION.md §1 for the writer contract) cached the REAL window Claude Code reported for this
        // exact session. Tier 0 must win over the 200K blind fallback with zero knowledge of the model.
        const string cwd = @"C:\dev\smtest4";
        string profile = Path.Combine(Path.GetTempPath(), "sm-int-" + Guid.NewGuid().ToString("N"));
        string cacheDir = Path.Combine(Path.GetTempPath(), "sm-cache-" + Guid.NewGuid().ToString("N"));

        string projectDir = ContextMonitor.ProjectsDirFor(profile, cwd);
        Directory.CreateDirectory(projectDir);
        const string sessionId = "cccc1111-2222-3333-4444-555566667777";
        string transcript = Path.Combine(projectDir, $"{sessionId}.jsonl");
        File.WriteAllText(transcript,
            """{"timestamp":"2026-08-28T07:00:00+00:00","message":{"model":"claude-zeta-9","usage":{"input_tokens":50000,"cache_read_input_tokens":10000,"cache_creation_input_tokens":2000}}}""");

        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, $"{sessionId}.native-window.json"),
            """{"window":2000000,"model":"Zeta 9","at":"2026-08-28T09:00:00+02:00"}""");

        try
        {
            var monitor = new ContextMonitor(new MeterConfig { NativeWindowCacheDir = cacheDir });
            ContextReading reading = monitor.Read(cwd, name: "smtest4", userProfile: profile);

            Assert.True(reading.Known);
            Assert.True(reading.WindowDetected);
            Assert.Equal(2_000_000, reading.WindowTokens); // the CACHED ground truth, not a guess
            Assert.Equal(62_000, reading.UsedTokens);
            Assert.Equal(3.1, reading.Pct);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void Read_falls_through_to_older_tiers_when_no_native_window_cache_exists()
    {
        // Precedence/regression guard: absence of a cache file must not break anything — the 5-series
        // family tier (fixed earlier the same day) must still fire normally.
        const string cwd = @"C:\dev\smtest5";
        string profile = Path.Combine(Path.GetTempPath(), "sm-int-" + Guid.NewGuid().ToString("N"));
        string cacheDir = Path.Combine(Path.GetTempPath(), "sm-cache-" + Guid.NewGuid().ToString("N")); // never created

        string projectDir = ContextMonitor.ProjectsDirFor(profile, cwd);
        Directory.CreateDirectory(projectDir);
        string transcript = Path.Combine(projectDir, "dddd1111-2222-3333-4444-555566667777.jsonl");
        File.WriteAllText(transcript,
            """{"timestamp":"2026-08-28T07:00:00+00:00","message":{"model":"claude-sonnet-5","usage":{"input_tokens":50000,"cache_read_input_tokens":10000,"cache_creation_input_tokens":2000}}}""");

        try
        {
            var monitor = new ContextMonitor(new MeterConfig { NativeWindowCacheDir = cacheDir });
            ContextReading reading = monitor.Read(cwd, name: "smtest5", userProfile: profile);

            Assert.True(reading.WindowDetected);
            Assert.Equal(1_000_000, reading.WindowTokens); // family tier, not the cache (which doesn't exist)
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
