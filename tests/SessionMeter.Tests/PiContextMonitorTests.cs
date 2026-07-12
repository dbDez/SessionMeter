using SessionMeter.Core.Configuration;
using SessionMeter.Core.Context;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>Tests for Pi's JSONL transcript and model-registry context reader.</summary>
public sealed class PiContextMonitorTests
{
    [Fact]
    public void ScanLastUsage_uses_input_and_cache_fields_and_skips_later_zero_usage_error()
    {
        string[] lines =
        {
            """{"type":"message","timestamp":"2026-07-12T01:00:00Z","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-terra","timestamp":1783820000000,"usage":{"input":100000,"cacheRead":40000,"cacheWrite":5000}}}""",
            """{"type":"message","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-terra","usage":{"input":0,"cacheRead":0,"cacheWrite":0},"stopReason":"error"}}""",
        };

        PiUsageScan scan = PiContextMonitor.ScanLastUsage(lines);

        Assert.True(scan.Found);
        Assert.Equal(145_000, scan.UsedTokens);
        Assert.Equal("pav-foundry", scan.Provider);
        Assert.Equal("gpt-5.6-terra", scan.Model);
        Assert.NotNull(scan.Timestamp);
    }

    [Fact]
    public void Read_resolves_pi_model_window_and_computes_percentage()
    {
        const string cwd = @"C:\Dev\SessionMeter";
        string profile = Path.Combine(Path.GetTempPath(), "sm-pi-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(profile, ".pi", "agent", "sessions", "--C--Dev-SessionMeter--");
        Directory.CreateDirectory(sessions);
        string transcript = Path.Combine(sessions, "2026-07-12T01-00-00Z_11112222-3333-4444-5555-666677778888.jsonl");
        File.WriteAllLines(transcript,
        [
            """{"type":"session","version":3,"id":"11112222-3333-4444-5555-666677778888","timestamp":"2026-07-12T01:00:00Z","cwd":"C:/Dev/SessionMeter"}""",
            """{"type":"message","id":"aaaaaaaa","parentId":null,"timestamp":"2026-07-12T01:00:01Z","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-terra","timestamp":1783820001000,"usage":{"input":100000,"cacheRead":40000,"cacheWrite":5000},"stopReason":"stop"}}""",
        ]);

        string agentDir = Path.Combine(profile, ".pi", "agent");
        File.WriteAllText(Path.Combine(agentDir, "models.json"),
            """{"providers":{"pav-foundry":{"models":[{"id":"gpt-5.6-terra","contextWindow":900000}]}}}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(cwd, name: "sessionmeter", userProfile: profile);

            Assert.True(reading.Known);
            Assert.True(reading.WindowDetected);
            Assert.Equal(145_000, reading.UsedTokens);
            Assert.Equal(900_000, reading.WindowTokens);
            Assert.Equal(16.1, reading.Pct);
            Assert.Equal("gpt-5.6-terra", reading.Model);
            Assert.Contains("900,000", reading.ToLine());
            Assert.Contains("window (detected)", reading.ToLine());
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void Read_with_explicit_session_id_ignores_a_newer_same_cwd_transcript()
    {
        const string cwd = @"C:\Dev\SessionMeter";
        const string oldId = "11111111-1111-1111-1111-111111111111";
        const string newId = "22222222-2222-2222-2222-222222222222";
        string profile = Path.Combine(Path.GetTempPath(), "sm-pi-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(profile, ".pi", "agent", "sessions", "same-cwd");
        Directory.CreateDirectory(sessions);
        string oldTranscript = Path.Combine(sessions, "old.jsonl");
        string newTranscript = Path.Combine(sessions, "new.jsonl");

        File.WriteAllLines(oldTranscript,
        [
            $$$"""{"type":"session","id":"{{{oldId}}}","cwd":"C:/Dev/SessionMeter"}""",
            """{"type":"message","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-terra","usage":{"input":463942,"cacheRead":0,"cacheWrite":0}}}""",
        ]);
        File.WriteAllLines(newTranscript,
        [
            $$$"""{"type":"session","id":"{{{newId}}}","cwd":"C:/Dev/SessionMeter"}""",
            """{"type":"message","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-terra","usage":{"input":7200,"cacheRead":0,"cacheWrite":0}}}""",
        ]);
        File.SetLastWriteTimeUtc(newTranscript, DateTime.UtcNow.AddMinutes(1));

        string agentDir = Path.Combine(profile, ".pi", "agent");
        File.WriteAllText(Path.Combine(agentDir, "models.json"),
            """{"providers":{"pav-foundry":{"models":[{"id":"gpt-5.6-terra","contextWindow":900000}]}}}""");

        try
        {
            var monitor = new PiContextMonitor(new MeterConfig());
            ContextReading newest = monitor.Read(cwd, name: null, userProfile: profile);
            ContextReading exact = monitor.Read(cwd, name: null, userProfile: profile, sessionId: oldId);

            Assert.Equal(newId, newest.SessionId);
            Assert.Equal(0.8, newest.Pct);
            Assert.Equal(oldId, exact.SessionId);
            Assert.Equal(51.5, exact.Pct);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void Read_missing_model_registry_uses_configured_fallback()
    {
        const string cwd = @"C:\Dev\SessionMeter";
        string profile = Path.Combine(Path.GetTempPath(), "sm-pi-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(profile, ".pi", "agent", "sessions", "one");
        Directory.CreateDirectory(sessions);
        File.WriteAllLines(Path.Combine(sessions, "session.jsonl"),
        [
            """{"type":"session","version":3,"id":"11112222","cwd":"C:\\Dev\\SessionMeter"}""",
            """{"type":"message","message":{"role":"assistant","provider":"pav-foundry","model":"unknown","usage":{"input":50000,"cacheRead":0,"cacheWrite":0}}}""",
        ]);

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig { WorkerContextWindow = 200_000 })
                .Read(cwd, name: null, userProfile: profile);

            Assert.True(reading.Known);
            Assert.False(reading.WindowDetected);
            Assert.Equal(200_000, reading.WindowTokens);
            Assert.Equal(25.0, reading.Pct);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }
}
