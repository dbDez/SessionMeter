using SessionMeter.Core.Context;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>
/// Tests for <see cref="ContextWindowResolver"/> — 200K-vs-1M detection from a temp <c>.claude.json</c>, cwd-key
/// normalization, highest-usage fallback, and the non-detected fallback paths.
/// </summary>
public sealed class ContextWindowResolverTests
{
    private static string WriteClaudeJson(string projectsBody)
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, ".claude.json"),
            $$"""{ "projects": { {{projectsBody}} } }""");
        return profile;
    }

    [Fact]
    public void OneM_key_with_matching_baseModel_yields_large_window_detected()
    {
        string profile = WriteClaudeJson(
            """
            "C:/dev/mo": { "lastModelUsage": {
                "claude-opus-4-8[1m]": { "inputTokens": 1000, "cacheReadInputTokens": 2000, "cacheCreationInputTokens": 3000 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(1_000_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal("claude-opus-4-8[1m]", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Bare_key_with_matching_baseModel_yields_standard_window_detected()
    {
        string profile = WriteClaudeJson(
            """
            "C:/dev/mo": { "lastModelUsage": {
                "claude-opus-4-8": { "inputTokens": 1000, "cacheReadInputTokens": 2000, "cacheCreationInputTokens": 3000 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal("claude-opus-4-8", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Cwd_key_normalizes_backslashes_and_case()
    {
        // Key stored with backslashes + mixed case; cwd queried with forward slashes + lowercase.
        string profile = WriteClaudeJson(
            """
            "C:\\Dev\\Mo": { "lastModelUsage": {
                "claude-opus-4-8[1m]": { "inputTokens": 10, "cacheReadInputTokens": 20, "cacheCreationInputTokens": 30 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve("C:/dev/mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(1_000_000, r.Window);
            Assert.True(r.Detected);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Missing_claude_json_yields_fallback_not_detected()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); // no .claude.json inside
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
            Assert.Null(r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Cwd_absent_from_projects_yields_fallback_not_detected()
    {
        string profile = WriteClaudeJson(
            """
            "C:/dev/other": { "lastModelUsage": {
                "claude-opus-4-8[1m]": { "inputTokens": 10, "cacheReadInputTokens": 20, "cacheCreationInputTokens": 30 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Empty_lastModelUsage_yields_fallback_not_detected()
    {
        string profile = WriteClaudeJson("""
            "C:/dev/mo": { "lastModelUsage": { } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void No_baseModel_picks_highest_usage_model_and_detects_1m()
    {
        // A tiny haiku entry and a huge opus[1m] entry — highest cumulative usage (opus) must win.
        string profile = WriteClaudeJson(
            """
            "C:/dev/mo": { "lastModelUsage": {
                "claude-haiku-4-5-20251001": { "inputTokens": 1020, "cacheReadInputTokens": 0, "cacheCreationInputTokens": 0 },
                "claude-opus-4-8[1m]":        { "inputTokens": 427113, "cacheReadInputTokens": 114467843, "cacheCreationInputTokens": 4900475 }
            } }
            """);
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", baseModel: null, profile, 200_000);

            Assert.Equal(1_000_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal("claude-opus-4-8[1m]", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    private static void WriteSettingsJson(string profile, string model)
    {
        string dir = Path.Combine(profile, ".claude");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), $$"""{ "model": "{{model}}" }""");
    }

    [Fact]
    public void Config_1m_model_with_no_claude_json_yields_large_window_detected()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); // no .claude.json inside
        WriteSettingsJson(profile, "opus[1m]");
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(1_000_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal("opus[1m]", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Config_1m_model_with_nonmatching_claude_json_project_yields_large_window_detected()
    {
        string profile = WriteClaudeJson(
            """
            "C:/dev/other": { "lastModelUsage": {
                "claude-opus-4-8": { "inputTokens": 10, "cacheReadInputTokens": 20, "cacheCreationInputTokens": 30 }
            } }
            """);
        WriteSettingsJson(profile, "opus[1m]");
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(1_000_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal("opus[1m]", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Config_model_without_1m_marker_yields_fallback_not_detected()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); // no .claude.json inside
        WriteSettingsJson(profile, "opus");
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
            Assert.Null(r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Claude_json_detection_wins_over_disagreeing_settings_json()
    {
        // .claude.json says the bare (200K) key is in use; settings.json claims [1m] — the recorded usage wins.
        string profile = WriteClaudeJson(
            """
            "C:/dev/mo": { "lastModelUsage": {
                "claude-opus-4-8": { "inputTokens": 1000, "cacheReadInputTokens": 2000, "cacheCreationInputTokens": 3000 }
            } }
            """);
        WriteSettingsJson(profile, "opus[1m]");
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.True(r.Detected);
            Assert.Equal("claude-opus-4-8", r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void No_settings_json_and_no_claude_json_yields_fallback_not_detected()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); // neither .claude.json nor .claude\settings.json
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
            Assert.Null(r.Model);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Unparseable_settings_json_yields_fallback_not_detected()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(profile, ".claude"));
        File.WriteAllText(Path.Combine(profile, ".claude", "settings.json"), "{ not json ");
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    [Fact]
    public void Unparseable_claude_json_yields_fallback_not_detected()
    {
        string profile = Path.Combine(Path.GetTempPath(), "sm-cwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, ".claude.json"), "{ this is not json ");
        try
        {
            WindowResolution r = ContextWindowResolver.Resolve(@"C:\dev\mo", "claude-opus-4-8", profile, 200_000);

            Assert.Equal(200_000, r.Window);
            Assert.False(r.Detected);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }
}
