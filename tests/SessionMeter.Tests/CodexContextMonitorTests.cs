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
        Assert.Null(scan.Model);
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

    [Fact]
    public void ScanLastUsage_associates_measurement_with_preceding_turn_model_across_switches()
    {
        string[] lines =
        {
            """{"type":"turn_context","payload":{"model":"gpt-any-future-model"}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000},"model_context_window":250000}}}""",
            """{"type":"turn_context","payload":{"model":"gpt-other-model"}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":2000},"model_context_window":500000}}}""",
        };

        CodexUsageScan scan = CodexContextMonitor.ScanLastUsage(lines);

        Assert.Equal("gpt-other-model", scan.Model);
        Assert.Equal(2_000, scan.UsedTokens);
        Assert.Equal(500_000, scan.ContextWindow);
    }

    [Fact]
    public void Read_returns_unknown_when_codex_did_not_record_a_window()
    {
        const string cwd = @"C:\Work\NoWindow";
        string profile = Path.Combine(Path.GetTempPath(), "sm-codex-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(profile, ".codex", "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllLines(Path.Combine(sessions, "rollout-no-window.jsonl"),
        [
            """{"type":"session_meta","payload":{"id":"no-window","cwd":"C:/Work/NoWindow"}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000}}}}""",
        ]);

        try
        {
            ContextReading reading = new CodexContextMonitor(new MeterConfig()).Read(cwd, "test", profile, "no-window");
            Assert.False(reading.Known);
            Assert.Equal(0, reading.WindowTokens);
            Assert.Contains("did not report", reading.Note);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void FindActiveTranscript_uses_structured_metadata_and_never_matches_cwd_inside_instructions()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "rollout-wrong.jsonl"),
            """{"type":"session_meta","payload":{"id":"wrong","cwd":"C:/Other","base_instructions":"example cwd C:\\Target"}}""");
        File.WriteAllText(Path.Combine(root, "rollout-right.jsonl"),
            """{"type":"session_meta","payload":{"id":"right","cwd":"c:\\target"}}""");

        try
        {
            CodexTranscript? found = CodexContextMonitor.FindActiveTranscript(root, "C:/TARGET", "right");
            CodexTranscript? wrong = CodexContextMonitor.FindActiveTranscript(root, "C:/Target", "wrong");
            Assert.Equal("right", found?.SessionId);
            Assert.Null(wrong);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindActiveTranscript_accepts_thread_id_when_session_id_differs()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-codex-ids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "rollout.jsonl"),
            """{"type":"session_meta","payload":{"session_id":"parent-session","id":"current-thread","cwd":"C:/Work/Ids"}}""");

        try
        {
            CodexTranscript? byThread = CodexContextMonitor.FindActiveTranscript(root, "C:/Work/Ids", "current-thread");
            CodexTranscript? bySession = CodexContextMonitor.FindActiveTranscript(root, "C:/Work/Ids", "parent-session");
            Assert.Equal("parent-session", byThread?.SessionId);
            Assert.Equal("current-thread", byThread?.ThreadId);
            Assert.Equal("parent-session", bySession?.SessionId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveCodexHome_prefers_explicit_codex_home()
    {
        string resolved = CodexContextMonitor.ResolveCodexHome("C:/profile", "C:/portable/codex");
        Assert.Equal(Path.GetFullPath("C:/portable/codex"), resolved);
    }

    [Fact]
    public void Read_can_measure_a_rollout_while_codex_holds_it_open_for_writing()
    {
        const string cwd = @"C:\Work\Live";
        string profile = Path.Combine(Path.GetTempPath(), "sm-codex-live-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(profile, ".codex", "sessions");
        Directory.CreateDirectory(sessions);
        string path = Path.Combine(sessions, "rollout-live.jsonl");
        File.WriteAllLines(path,
        [
            """{"type":"session_meta","payload":{"id":"live-id","cwd":"C:/Work/Live"}}""",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":25000},"model_context_window":250000}}}""",
        ]);

        try
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            ContextReading reading = new CodexContextMonitor(new MeterConfig()).Read(cwd, "live", profile, "live-id");
            Assert.True(reading.Known);
            Assert.Equal(10, reading.Pct);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }
}
