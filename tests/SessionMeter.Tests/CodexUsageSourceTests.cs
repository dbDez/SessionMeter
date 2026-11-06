using SessionMeter.Core.Context;
using SessionMeter.Core.Usage;
using Xunit;

namespace SessionMeter.Tests;

public sealed class CodexUsageSourceTests
{
    [Fact]
    public void ScanLastSnapshot_reads_latest_limits_even_when_info_is_null()
    {
        string[] lines =
        {
            "not json",
            """{"timestamp":"2026-09-08T09:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1}},"rate_limits":{"limit_id":"old","primary":{"used_percent":1,"window_minutes":300,"resets_at":1788880000}}}}""",
            """{"timestamp":"2026-09-08T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":20.5,"window_minutes":300,"resets_at":1788884709},"secondary":{"used_percent":10,"window_minutes":10080,"resets_at":1789450976},"individual_limit":null}}}""",
        };

        CodexUsageSnapshot? snapshot = CodexUsageSource.ScanLastSnapshot(lines, new CodexTranscript("rollout.jsonl", "session-id"));

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal("session-id", snapshot.SessionId);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("5-hour window", snapshot.Windows[0].Label);
        Assert.Equal(20.5, snapshot.Windows[0].Percent);
        Assert.Equal(300, snapshot.Windows[0].WindowMinutes);
        Assert.Equal("7-day window", snapshot.Windows[1].Label);
        Assert.Contains("\"limit_id\": \"codex\"", snapshot.Raw);
    }

    [Fact]
    public void ScanLastSnapshot_preserves_individual_scoped_limit()
    {
        string[] lines =
        {
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":4,"window_minutes":300},"individual_limit":{"limit_id":"model-x","limit_name":"Model X","primary":{"used_percent":88,"window_minutes":1440}}}}}""",
        };

        CodexUsageSnapshot? snapshot = CodexUsageSource.ScanLastSnapshot(lines);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Equal("Model X", snapshot.Windows[1].Scope);
        Assert.Equal(88, snapshot.Windows[1].Percent);
        Assert.Equal(1_440, snapshot.Windows[1].WindowMinutes);
    }

    [Fact]
    public void ScanLastSnapshot_returns_null_for_absent_null_or_malformed_limits()
    {
        string[] lines =
        {
            "not json",
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":null}}""",
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":null,"secondary":null}}}""",
        };

        Assert.Null(CodexUsageSource.ScanLastSnapshot(lines));
    }

    [Fact]
    public void Read_selects_exact_session_from_explicit_codex_home()
    {
        const string cwd = @"C:\Work\Usage";
        string codexHome = Path.Combine(Path.GetTempPath(), "sm-codex-home-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(codexHome, "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllLines(Path.Combine(sessions, "rollout-exact.jsonl"),
        [
            """{"type":"session_meta","payload":{"id":"exact-id","cwd":"C:/Work/Usage"}}""",
            """{"timestamp":"2026-09-08T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":7,"window_minutes":300}}}}""",
        ]);

        try
        {
            CodexUsageResult result = new CodexUsageSource().Read(cwd, codexHome, "exact-id");
            Assert.True(result.Ok);
            Assert.Equal(7, result.Snapshot!.Windows.Single().Percent);
        }
        finally { Directory.Delete(codexHome, recursive: true); }
    }
}
