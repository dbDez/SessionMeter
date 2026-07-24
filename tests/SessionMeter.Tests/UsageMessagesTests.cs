using SessionMeter.Core.Usage;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>
/// Tests for the elegant API-user message helper (<see cref="UsageMessages.ApiUserUsageMessage"/>) and the
/// scoped-wall display line (<see cref="UsageMessages.ScopedWallLine"/>).
/// </summary>
public sealed class UsageMessagesTests
{
    [Fact]
    public void ApiUserUsageMessage_points_at_context_and_hides_endpoint_url()
    {
        string msg = UsageMessages.ApiUserUsageMessage();

        Assert.Contains("context", msg); // steers API-key users to the working command
        Assert.DoesNotContain("api.anthropic.com", msg); // no raw endpoint URL leaks into the friendly text
        Assert.DoesNotContain("/api/oauth/usage", msg);
    }

    [Fact]
    public void ScopedWallLine_fable_bucket_is_informational_and_never_shows_active()
    {
        // Even when the endpoint still flags the retired fable bucket active/critical, the line must read
        // as informational only — no ACTIVE, no severity, an explicit "no longer enforced" suffix.
        var fable = new UsageLimit("weekly_scoped", "weekly", 21,
            DateTimeOffset.Parse("2026-07-30T19:00:00Z"), "Fable", "critical", IsActive: true);

        string line = UsageMessages.ScopedWallLine(fable);

        Assert.Contains("7-day · Fable", line);
        Assert.Contains("21% used", line);
        Assert.Contains("informational — no longer enforced", line);
        Assert.DoesNotContain("ACTIVE", line);
        Assert.DoesNotContain("critical", line);
    }

    [Fact]
    public void ScopedWallLine_enforced_per_model_wall_keeps_severity_and_active_flags()
    {
        var haiku = new UsageLimit("weekly_scoped", "weekly", 97,
            DateTimeOffset.Parse("2026-07-30T19:00:00Z"), "Haiku", "critical", IsActive: true);

        string line = UsageMessages.ScopedWallLine(haiku);

        Assert.Contains("7-day · Haiku", line);
        Assert.Contains("97% used", line);
        Assert.Contains("critical", line);
        Assert.Contains("ACTIVE", line);
        Assert.DoesNotContain("informational", line);
    }
}
