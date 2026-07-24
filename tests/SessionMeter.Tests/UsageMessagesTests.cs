using Xunit;

namespace SessionMeter.Tests;

/// <summary>Tests for the elegant API-user message helper (<see cref="UsageMessages.ApiUserUsageMessage"/>).</summary>
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
}
