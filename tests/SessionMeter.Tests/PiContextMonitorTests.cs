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
                .Read(cwd, name: null, userProfile: profile, builtInModelsRoot: Path.Combine(profile, "no-builtins"));

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

    [Fact]
    public void Read_resolves_builtin_openai_codex_gpt_5_6_sol_window()
    {
        const string cwd = @"C:\Work\BuiltIn";
        string profile = NewProfile();
        string catalog = WriteBuiltInCatalog(profile,
            """{"openai-codex-responses":{"gpt-5.6-sol":{"id":"gpt-5.6-sol","provider":"openai-codex","contextWindow":272000}}}""");
        WriteTranscript(profile, cwd,
            """{"type":"model_change","provider":"openai-codex","modelId":"gpt-5.6-sol"}""",
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.6-sol","usage":{"input":136000,"cacheRead":0,"cacheWrite":0}}}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(
                cwd, null, profile, builtInModelsRoot: catalog);

            Assert.True(reading.WindowDetected);
            Assert.Equal(272_000, reading.WindowTokens);
            Assert.Equal(50.0, reading.Pct);
            Assert.Equal("openai-codex", reading.Provider);
            Assert.Equal("gpt-5.6-sol", reading.Model);
            Assert.Equal("openai-codex", reading.SelectedProvider);
            Assert.Equal("gpt-5.6-sol", reading.SelectedModel);
            Assert.Contains("model openai-codex/gpt-5.6-sol", reading.ToLine());
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Read_custom_provider_with_same_model_id_stays_provider_scoped()
    {
        const string cwd = @"C:\Work\Custom";
        string profile = NewProfile();
        string catalog = WriteBuiltInCatalog(profile,
            """{"openai-codex-responses":{"gpt-5.6-sol":{"id":"gpt-5.6-sol","provider":"openai-codex","contextWindow":272000}}}""");
        WriteModelsJson(profile,
            """{"providers":{"pav-foundry":{"models":[{"id":"gpt-5.6-sol","contextWindow":1050000}]}}}""");
        WriteTranscript(profile, cwd,
            """{"type":"model_change","provider":"pav-foundry","modelId":"gpt-5.6-sol"}""",
            """{"type":"message","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-sol","usage":{"input":105000,"cacheRead":0,"cacheWrite":0}}}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(
                cwd, null, profile, builtInModelsRoot: catalog);

            Assert.Equal(1_050_000, reading.WindowTokens);
            Assert.Equal(10.0, reading.Pct);
            Assert.Equal("pav-foundry", reading.Provider);
            Assert.DoesNotContain("openai-codex", reading.ToLine());
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Read_after_switch_but_before_response_keeps_measurement_on_old_model()
    {
        const string cwd = @"C:\Work\SwitchPending";
        string profile = NewProfile();
        string catalog = WriteBuiltInCatalog(profile,
            """{"openai-codex-responses":{"gpt-5.6-sol":{"id":"gpt-5.6-sol","provider":"openai-codex","contextWindow":272000}}}""");
        WriteModelsJson(profile,
            """{"providers":{"pav-foundry":{"models":[{"id":"gpt-5.6-sol","contextWindow":1050000}]}}}""");
        WriteTranscript(profile, cwd,
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.6-sol","usage":{"input":136000,"cacheRead":0,"cacheWrite":0}}}""",
            """{"type":"model_change","provider":"pav-foundry","modelId":"gpt-5.6-sol"}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(
                cwd, null, profile, builtInModelsRoot: catalog);

            Assert.Equal(272_000, reading.WindowTokens);
            Assert.Equal(50.0, reading.Pct);
            Assert.Equal("openai-codex", reading.Provider);
            Assert.Equal("pav-foundry", reading.SelectedProvider);
            Assert.Contains("measured openai-codex/gpt-5.6-sol", reading.ToLine());
            Assert.Contains("selected pav-foundry/gpt-5.6-sol", reading.ToLine());
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Read_after_first_response_on_switched_model_uses_new_models_window()
    {
        const string cwd = @"C:\Work\SwitchComplete";
        string profile = NewProfile();
        string catalog = WriteBuiltInCatalog(profile,
            """{"openai-codex-responses":{"gpt-5.6-sol":{"id":"gpt-5.6-sol","provider":"openai-codex","contextWindow":272000}}}""");
        WriteModelsJson(profile,
            """{"providers":{"pav-foundry":{"models":[{"id":"gpt-5.6-sol","contextWindow":1050000}]}}}""");
        WriteTranscript(profile, cwd,
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.6-sol","usage":{"input":136000,"cacheRead":0,"cacheWrite":0}}}""",
            """{"type":"model_change","provider":"pav-foundry","modelId":"gpt-5.6-sol"}""",
            """{"type":"message","message":{"role":"assistant","provider":"pav-foundry","model":"gpt-5.6-sol","usage":{"input":105000,"cacheRead":0,"cacheWrite":0}}}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(
                cwd, null, profile, builtInModelsRoot: catalog);

            Assert.Equal(1_050_000, reading.WindowTokens);
            Assert.Equal(10.0, reading.Pct);
            Assert.Equal("pav-foundry", reading.Provider);
            Assert.Equal(reading.Provider, reading.SelectedProvider);
            Assert.Equal(reading.Model, reading.SelectedModel);
            Assert.DoesNotContain("awaiting selected-model usage", reading.ToLine());
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Read_applies_models_json_override_on_top_of_builtin_window()
    {
        const string cwd = @"C:\Work\Override";
        string profile = NewProfile();
        string catalog = WriteBuiltInCatalog(profile,
            """{"openai-codex-responses":{"gpt-5.6-sol":{"id":"gpt-5.6-sol","provider":"openai-codex","contextWindow":272000}}}""");
        WriteModelsJson(profile,
            """{"providers":{"openai-codex":{"modelOverrides":{"gpt-5.6-sol":{"contextWindow":300000}}}}}""");
        WriteTranscript(profile, cwd,
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.6-sol","usage":{"input":150000,"cacheRead":0,"cacheWrite":0}}}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(
                cwd, null, profile, builtInModelsRoot: catalog);
            Assert.Equal(300_000, reading.WindowTokens);
            Assert.Equal(50.0, reading.Pct);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Read_uses_newer_persisted_pi_catalog_over_installed_catalog()
    {
        const string cwd = @"C:\Work\StoredCatalog";
        string profile = NewProfile();
        string catalog = WriteBuiltInCatalog(profile,
            """{"openai-codex-responses":{"gpt-future":{"id":"gpt-future","provider":"openai-codex","contextWindow":200000}}}""");
        File.WriteAllText(Path.Combine(catalog, ".manifest.json"),
            """{"generatedAt":"2026-09-01T00:00:00Z"}""");
        string agentDir = Path.Combine(profile, ".pi", "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "models-store.json"),
            """{"openai-codex":{"lastModified":1788739200000,"models":[{"id":"gpt-future","provider":"openai-codex","contextWindow":300000}]}}""");
        WriteTranscript(profile, cwd,
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-future","usage":{"input":100000}}}""");

        try
        {
            ContextReading reading = new PiContextMonitor(new MeterConfig()).Read(
                cwd, null, profile, builtInModelsRoot: catalog);
            Assert.Equal(300_000, reading.WindowTokens);
            Assert.Equal(33.3, reading.Pct);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void ScanModelState_tracks_selected_and_measured_models_independently()
    {
        PiModelState state = PiContextMonitor.ScanModelState(
        [
            """{"type":"model_change","provider":"openai-codex","modelId":"gpt-5.6-sol"}""",
            """{"type":"message","message":{"role":"assistant","provider":"openai-codex","model":"gpt-5.6-sol","usage":{"input":1000}}}""",
            """{"type":"model_change","provider":"pav-foundry","modelId":"gpt-5.6-sol"}""",
        ]);

        Assert.Equal("openai-codex", state.Usage.Provider);
        Assert.Equal("pav-foundry", state.SelectedProvider);
        Assert.Equal("gpt-5.6-sol", state.SelectedModel);
    }

    private static string NewProfile()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-pi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        return profile;
    }

    private static string WriteBuiltInCatalog(string profile, string json)
    {
        string root = Path.Combine(profile, "builtin-models");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "openai-codex.json"), json);
        return root;
    }

    private static void WriteModelsJson(string profile, string json)
    {
        string agentDir = Path.Combine(profile, ".pi", "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "models.json"), json);
    }

    private static void WriteTranscript(string profile, string cwd, params string[] entries)
    {
        string sessions = Path.Combine(profile, ".pi", "agent", "sessions", "fixture");
        Directory.CreateDirectory(sessions);
        var lines = new List<string>
        {
            $$$"""{"type":"session","version":3,"id":"test-session","cwd":"{{{cwd.Replace("\\", "/")}}}"}""",
        };
        lines.AddRange(entries);
        File.WriteAllLines(Path.Combine(sessions, "session.jsonl"), lines);
    }
}
