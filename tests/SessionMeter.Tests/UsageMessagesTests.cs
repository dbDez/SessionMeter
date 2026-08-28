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

    [Fact]
    public void TransientlyUnavailableMessage_blames_upstream_not_the_tool()
    {
        string msg = UsageMessages.TransientlyUnavailableMessage("HTTP 429 — rate limited", attempts: 3, retryAfter: null);

        Assert.Contains("temporarily unavailable", msg);
        Assert.Contains("HTTP 429 — rate limited", msg);
        Assert.Contains("not a fault in this", msg);   // the whole point: this is not a broken tool
        Assert.Contains("3 attempts", msg);            // the measured count, not a guess
    }

    [Fact]
    public void TransientlyUnavailableMessage_quotes_the_endpoints_own_wait_when_it_sent_one()
    {
        string msg = UsageMessages.TransientlyUnavailableMessage(
            "HTTP 429 — rate limited", attempts: 1, retryAfter: TimeSpan.FromSeconds(30));

        Assert.Contains("30 seconds", msg);
        Assert.Contains("after 1 attempt.", msg);      // singular, not "1 attempts"
    }

    [Fact]
    public void TransientlyUnavailableMessage_invents_no_wait_when_the_endpoint_gave_none()
    {
        string msg = UsageMessages.TransientlyUnavailableMessage("HTTP 503 — upstream server error", attempts: 3, retryAfter: null);

        Assert.Contains("did not say how long to wait", msg);
        Assert.DoesNotContain("seconds", msg);   // no fabricated duration anywhere in the text
        Assert.DoesNotContain("minutes", msg);
    }

    [Fact]
    public void TransientlyUnavailableMessage_prints_no_percentage_when_no_snapshot_was_read()
    {
        string msg = UsageMessages.TransientlyUnavailableMessage("HTTP 429 — rate limited", attempts: 3, retryAfter: null);

        Assert.DoesNotContain("%", msg);   // absence must stay visible — no cached or estimated reading
    }

    [Fact]
    public void TransientlyUnavailableMessage_carries_no_brace_that_a_JSON_scraping_consumer_would_grab()
    {
        // Every known consumer of `session usage --raw` locates the JSON body by scanning stdout for the
        // first '{' (see examples/hooks/warn-usage.ps1). This message
        // is printed to stdout, so a brace in the prose would be handed to a JSON parser as if it were the
        // payload. Pin it here rather than discover it in a hook.
        string msg = UsageMessages.TransientlyUnavailableMessage(
            "HTTP 429 — rate limited", attempts: 3, retryAfter: TimeSpan.FromSeconds(30));

        Assert.DoesNotContain('{', msg);
        Assert.DoesNotContain('}', msg);
    }
}
