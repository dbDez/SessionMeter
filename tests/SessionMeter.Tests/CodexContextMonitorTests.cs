using SessionMeter.Core.Configuration;
using SessionMeter.Core.Context;
using Xunit;

namespace SessionMeter.Tests;

public sealed class CodexContextMonitorTests
{
    [Fact]
    public void ScanLastUsage_uses_latest_input_tokens_without_double_counting_cached_input()
    {
        string[] lines =
        {
            """{"timestamp":"2026-07-14T07:55:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":120000,"cached_input_tokens":90000,"output_tokens":300},"model_context_window":900000}}}""",
            """{"timestamp":"2026-07-14T07:56:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":32965,"cached_input_tokens":21760,"output_tokens":443},"model_context_window":353400}}}""",
        };

        CodexUsageScan scan = CodexContextMonitor.ScanLastUsage(lines);

        Assert.True(scan.Found);
        Assert.Equal(32_965, scan.UsedTokens);
        Assert.Equal(353_400, scan.ContextWindow);
        Assert.NotNull(scan.Timestamp);
    }

    [Fact]
    public void Read_selects_newest_matching_cwd_and_uses_exact_recorded_window()
    {
        const string cwd = @"C:\Work\ExampleProject";
        const string oldId = "old-session";
        const string newId = "new-session";
        string profile = Path.Combine(Path.GetTempPath(), "sm-codex-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(profile, ".codex", "sessions", "2026", "07", "14");
        Directory.CreateDirectory(sessions);
        string oldTranscript = Path.Combine(sessions, "rollout-old.jsonl");
        string newTranscript = Path.Combine(sessions, "rollout-new.jsonl");
        File.WriteAllLines(oldTranscript,
        [
            $$$"""{"type":"session_meta","payload":{"session_id":"{{{oldId}}}","cwd":"C:\\Work\\ExampleProject"}}""",
            """{"timestamp":"2026-07-14T07:50:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":450000},"model_context_window":900000}}}""",
        ]);
        File.WriteAllLines(newTranscript,
        [
            $$$"""{"type":"session_meta","payload":{"session_id":"{{{newId}}}","cwd":"C:/Work/ExampleProject"}}""",
            """{"timestamp":"2026-07-14T07:56:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":32965,"cached_input_tokens":21760},"model_context_window":353400}}}""",
        ]);
        File.SetLastWriteTimeUtc(newTranscript, DateTime.UtcNow.AddMinutes(1));

        try
        {
            var monitor = new CodexContextMonitor(new MeterConfig());
            ContextReading newest = monitor.Read(cwd, name: "example", userProfile: profile);
            ContextReading exact = monitor.Read(cwd, name: null, userProfile: profile, sessionId: oldId);

            Assert.True(newest.Known);
            Assert.True(newest.WindowDetected);
            Assert.Equal(newId, newest.SessionId);
            Assert.Equal(32_965, newest.UsedTokens);
            Assert.Equal(353_400, newest.WindowTokens);
            Assert.Equal(9.3, newest.Pct);
            Assert.Equal(oldId, exact.SessionId);
            Assert.Equal(50.0, exact.Pct);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void ScanLastUsage_skips_malformed_and_zero_input_events()
    {
        string[] lines =
        {
            "not json",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":15000},"model_context_window":900000}}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":0},"model_context_window":900000}}}""",
        };

        CodexUsageScan scan = CodexContextMonitor.ScanLastUsage(lines);

        Assert.True(scan.Found);
        Assert.Equal(15_000, scan.UsedTokens);
    }
}
