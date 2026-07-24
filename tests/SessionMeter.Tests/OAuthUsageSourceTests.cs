using SessionMeter.Core.Configuration;
using SessionMeter.Core.Usage;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>
/// Tests for <see cref="OAuthUsageSource.ParseSnapshot"/> (the pure wire parse, including the anti-false-100%
/// guarantee and the per-model / fleet split) and the credential-absent branches of
/// <see cref="OAuthUsageSource.ProbeAsync"/> (the typed <see cref="UsageUnavailableReason"/> mapping).
/// </summary>
public sealed class OAuthUsageSourceTests
{
    private const string BodyWithPerModelWall =
        """
        {
          "five_hour": { "utilization": 1, "resets_at": "2026-07-04T18:00:00Z" },
          "seven_day": { "utilization": 39, "resets_at": "2026-07-10T00:00:00Z" },
          "limits": [
            { "kind": "session", "group": "session", "percent": 1 },
            { "kind": "weekly_all", "group": "weekly", "percent": 39 },
            { "kind": "weekly_scoped", "group": "weekly", "percent": 100,
              "scope": { "model": { "display_name": "Fable" } } }
          ]
        }
        """;

    [Fact]
    public void ParseSnapshot_utilization_1_stays_1_not_100()
    {
        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(BodyWithPerModelWall);

        Assert.NotNull(snap.FiveHour);
        Assert.Equal(1, snap.FiveHour!.Percent); // NOT 100 — the anti-false-100% guarantee
    }

    [Fact]
    public void ParseSnapshot_weekly_scoped_model_becomes_per_model_limit()
    {
        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(BodyWithPerModelWall);

        UsageLimit? fable = snap.ModelLimits.FirstOrDefault(l => l.ModelName == "Fable");
        Assert.NotNull(fable);
        Assert.True(fable!.IsPerModel);
        Assert.Equal(100, fable.Percent);
    }

    [Fact]
    public void ParseSnapshot_fable_wall_is_retired_and_never_binds()
    {
        // The Fable per-model wall no longer exists Anthropic-side (2026-07-24). Even at 100% the
        // fable-scoped bucket must not win the binding computation — the enforced 7-day (39%) binds.
        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(BodyWithPerModelWall);

        Assert.Equal(39, snap.BindingLimit!.Percent);
        Assert.Equal("7-day", snap.BindingLabel);
        Assert.DoesNotContain("Fable", snap.BindingLabel);
    }

    [Fact]
    public void ParseSnapshot_fable_wall_never_binds_even_when_endpoint_flags_it_active_critical()
    {
        const string body =
            """
            {
              "five_hour": { "utilization": 6, "resets_at": "2026-07-24T16:10:00Z" },
              "seven_day": { "utilization": 19, "resets_at": "2026-07-30T19:00:00Z" },
              "limits": [
                { "kind": "session", "group": "session", "percent": 6 },
                { "kind": "weekly_all", "group": "weekly", "percent": 19 },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 100,
                  "severity": "critical", "is_active": true,
                  "scope": { "model": { "display_name": "Fable" } } }
              ]
            }
            """;

        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(body);

        Assert.Equal(19, snap.BindingLimit!.Percent);       // enforced 7-day wins, not the 100% fable bucket
        Assert.Equal("7-day", snap.BindingLabel);
        Assert.Empty(snap.ActivePerModelWalls);             // an active/critical fable bucket is NOT an active wall

        UsageLimit fable = snap.ModelLimits.Single(l => l.ModelName == "Fable");
        Assert.True(fable.IsFableScoped);
        Assert.False(fable.IsEnforced);                     // the data stays visible, but only informationally
    }

    [Fact]
    public void ParseSnapshot_non_fable_per_model_wall_keeps_enforced_binding_semantics()
    {
        // The exclusion is scoped to fable ONLY: if the API ever ships another per-model wall, it still
        // participates in (and here wins) the binding computation exactly as before.
        const string body =
            """
            {
              "five_hour": { "utilization": 6, "resets_at": "2026-07-24T16:10:00Z" },
              "seven_day": { "utilization": 19, "resets_at": "2026-07-30T19:00:00Z" },
              "limits": [
                { "kind": "session", "group": "session", "percent": 6 },
                { "kind": "weekly_all", "group": "weekly", "percent": 19 },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 97,
                  "scope": { "model": { "display_name": "Haiku" } } }
              ]
            }
            """;

        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(body);

        UsageLimit haiku = snap.ModelLimits.Single(l => l.ModelName == "Haiku");
        Assert.False(haiku.IsFableScoped);
        Assert.True(haiku.IsEnforced);
        Assert.Equal(97, snap.BindingLimit!.Percent);
        Assert.Contains("Haiku", snap.BindingLabel);
    }

    [Fact]
    public void ParseSnapshot_binding_between_enforced_windows_is_unchanged()
    {
        // No fable bucket present: the binding selection between the 5-hour and 7-day windows is exactly
        // the historical max-percent collapse.
        const string body =
            """
            {
              "five_hour": { "utilization": 82, "resets_at": "2026-07-24T16:10:00Z" },
              "seven_day": { "utilization": 19, "resets_at": "2026-07-30T19:00:00Z" },
              "limits": [
                { "kind": "session", "group": "session", "percent": 82 },
                { "kind": "weekly_all", "group": "weekly", "percent": 19 }
              ]
            }
            """;

        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(body);

        Assert.Equal(82, snap.BindingLimit!.Percent);
        Assert.Equal("5-hour session", snap.BindingLabel);
    }

    [Fact]
    public void ParseSnapshot_fleet_windows_exclude_per_model_walls()
    {
        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(BodyWithPerModelWall);

        Assert.All(snap.FleetWindows, w => Assert.False(w.IsPerModel));
        Assert.Equal(39, snap.FleetBindingLimit!.Percent); // the all-models 7-day, NOT the 100% Fable wall
    }

    [Fact]
    public async Task ProbeAsync_missing_credentials_file_reports_NoCredentialsFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), "sm-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var cfg = new MeterConfig { OAuthCredentialsPath = missing };
        using var http = new HttpClient();
        var src = new OAuthUsageSource(cfg, http);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(UsageUnavailableReason.NoCredentialsFile, result.Reason);
    }

    [Fact]
    public async Task ProbeAsync_valid_json_without_access_token_reports_NoOAuthToken()
    {
        string path = Path.Combine(Path.GetTempPath(), "sm-creds-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """{ "someOtherKey": { "foo": "bar" } }""");
        try
        {
            var cfg = new MeterConfig { OAuthCredentialsPath = path };
            using var http = new HttpClient();
            var src = new OAuthUsageSource(cfg, http);

            OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(UsageUnavailableReason.NoOAuthToken, result.Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
