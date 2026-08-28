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
    public void ParseSnapshot_binding_limit_is_max_percent_including_per_model()
    {
        UsageSnapshot snap = OAuthUsageSource.ParseSnapshot(BodyWithPerModelWall);

        Assert.Equal(100, snap.BindingLimit!.Percent);
        Assert.Contains("Fable", snap.BindingLabel);
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
