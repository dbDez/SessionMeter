using System.Net;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Usage;
using Xunit;

namespace SessionMeter.Tests;

/// <summary>
/// Tests for the transient-failure retry added in v0.7.0: the pure <see cref="UsageRetryPolicy"/> decision and
/// the <see cref="OAuthUsageSource.ProbeAsync"/> loop that consumes it.
/// <para>
/// The loop is driven through a stub <see cref="HttpMessageHandler"/> and a recording delay seam, so a real
/// 429 is never needed and no test ever sleeps. What is NOT covered by construction: whether Anthropic's live
/// endpoint really sends <c>Retry-After</c> with its 429, and the wall-clock behaviour of the production
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// </para>
/// </summary>
public sealed class UsageRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private const string GoodBody =
        """
        {
          "five_hour": { "utilization": 22, "resets_at": "2026-08-16T14:00:00Z" },
          "seven_day": { "utilization": 78, "resets_at": "2026-08-20T00:00:00Z" }
        }
        """;

    // ── the pure policy: what is transient ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(599)]
    public void IsTransient_true_for_429_and_5xx(int code) =>
        Assert.True(UsageRetryPolicy.IsTransient(code));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(418)]
    [InlineData(600)]
    public void IsTransient_false_for_terminal_statuses(int code) =>
        Assert.False(UsageRetryPolicy.IsTransient(code));

    // ── the pure policy: Retry-After parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParseRetryAfter_reads_the_delta_seconds_form()
    {
        Assert.True(UsageRetryPolicy.TryParseRetryAfter("120", Now, out TimeSpan delay));
        Assert.Equal(TimeSpan.FromSeconds(120), delay);
    }

    [Fact]
    public void TryParseRetryAfter_reads_the_http_date_form()
    {
        Assert.True(UsageRetryPolicy.TryParseRetryAfter("Sun, 16 Aug 2026 12:00:45 GMT", Now, out TimeSpan delay));
        Assert.Equal(TimeSpan.FromSeconds(45), delay);
    }

    [Fact]
    public void TryParseRetryAfter_http_date_in_the_past_is_zero_never_negative()
    {
        Assert.True(UsageRetryPolicy.TryParseRetryAfter("Sun, 16 Aug 2026 11:59:00 GMT", Now, out TimeSpan delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("-5")]          // a signed value is malformed; it must not become a wait
    [InlineData("31536000000")] // absurd — reported as "no usable hint" rather than clamped to a number the server never sent
    public void TryParseRetryAfter_rejects_unusable_values_without_inventing_one(string? header)
    {
        Assert.False(UsageRetryPolicy.TryParseRetryAfter(header, Now, out TimeSpan delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    // ── the pure policy: the retry decision ──────────────────────────────────────────────────────────

    [Fact]
    public void TryGetDelay_uses_exponential_backoff_when_no_Retry_After()
    {
        Assert.True(UsageRetryPolicy.TryGetDelay(429, 1, null, UsageRetryPolicy.TotalRetryBudget, out TimeSpan first));
        Assert.Equal(TimeSpan.FromSeconds(1), first);

        Assert.True(UsageRetryPolicy.TryGetDelay(429, 2, null, UsageRetryPolicy.TotalRetryBudget, out TimeSpan second));
        Assert.Equal(TimeSpan.FromSeconds(2), second);
    }

    [Fact]
    public void TryGetDelay_stops_at_the_attempt_ceiling()
    {
        Assert.False(UsageRetryPolicy.TryGetDelay(
            429, UsageRetryPolicy.MaxAttempts, null, UsageRetryPolicy.TotalRetryBudget, out _));
    }

    [Fact]
    public void TryGetDelay_never_retries_a_terminal_status()
    {
        Assert.False(UsageRetryPolicy.TryGetDelay(401, 1, null, UsageRetryPolicy.TotalRetryBudget, out _));
        Assert.False(UsageRetryPolicy.TryGetDelay(403, 1, null, UsageRetryPolicy.TotalRetryBudget, out _));
        Assert.False(UsageRetryPolicy.TryGetDelay(404, 1, null, UsageRetryPolicy.TotalRetryBudget, out _));
    }

    [Fact]
    public void TryGetDelay_refuses_a_Retry_After_longer_than_the_remaining_budget()
    {
        // The server named 60 s; the budget is 5 s. Waiting 5 s and asking again would almost certainly be
        // refused again, so the policy gives up and lets the caller quote the server's own number instead.
        Assert.False(UsageRetryPolicy.TryGetDelay(
            429, 1, TimeSpan.FromSeconds(60), UsageRetryPolicy.TotalRetryBudget, out _));
    }

    [Fact]
    public void TryGetDelay_prefers_the_servers_Retry_After_over_the_backoff()
    {
        Assert.True(UsageRetryPolicy.TryGetDelay(
            503, 1, TimeSpan.FromSeconds(3), UsageRetryPolicy.TotalRetryBudget, out TimeSpan delay));
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void TryGetDelay_stops_once_the_time_budget_is_spent()
    {
        Assert.False(UsageRetryPolicy.TryGetDelay(429, 1, null, TimeSpan.Zero, out _));
    }

    // ── the loop: ProbeAsync over a stubbed endpoint ─────────────────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_429_retries_to_the_ceiling_then_reports_RateLimited()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.Equal(UsageRetryPolicy.MaxAttempts, handler.Calls);          // it really did try again
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
        Assert.Equal(UsageUnavailableReason.RateLimited, result.Reason);    // its own identity, not HttpError
        Assert.Null(result.Snapshot);                                       // and NO fabricated reading
        Assert.Equal(UsageRetryPolicy.MaxAttempts, result.Attempts);
    }

    [Fact]
    public async Task ProbeAsync_honours_the_Retry_After_header_and_surfaces_it()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, retryAfter: "3"));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(3)], waits);   // the server's number, not the 1 s backoff
        Assert.Equal(2, handler.Calls);                   // 3 s of a 5 s budget leaves no room for a third try
        Assert.Equal(TimeSpan.FromSeconds(3), result.RetryAfter);
    }

    [Fact]
    public async Task ProbeAsync_Retry_After_beyond_the_budget_gives_up_at_once_and_still_reports_it()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests, retryAfter: "300"));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Empty(waits);
        Assert.Equal(TimeSpan.FromSeconds(300), result.RetryAfter);   // the operator still learns the real wait
        Assert.Equal(UsageUnavailableReason.RateLimited, result.Reason);
    }

    [Fact]
    public async Task ProbeAsync_401_is_not_retried()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.Unauthorized));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);   // a rejected token cannot be fixed by asking again
        Assert.Empty(waits);
        Assert.Equal(UsageUnavailableReason.HttpError, result.Reason);
    }

    [Fact]
    public async Task ProbeAsync_5xx_retries_and_reports_UpstreamUnavailable()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.ServiceUnavailable));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.Equal(UsageRetryPolicy.MaxAttempts, handler.Calls);
        Assert.Equal(UsageUnavailableReason.UpstreamUnavailable, result.Reason);
        Assert.Contains("503", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_recovers_when_a_later_attempt_succeeds()
    {
        var handler = new StubHandler(call => call == 1
            ? Respond(HttpStatusCode.TooManyRequests)
            : Respond(HttpStatusCode.OK, body: GoodBody));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(22, result.Snapshot!.FiveHour!.Percent);   // the real reading, from the retry
        Assert.Equal(78, result.Snapshot.SevenDay!.Percent);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ProbeAsync_first_attempt_success_makes_no_extra_request()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, body: GoodBody));
        using CredentialsFixture creds = CredentialsFixture.Valid();
        var waits = new List<TimeSpan>();
        OAuthUsageSource src = Source(creds, handler, waits);

        OAuthUsageResult result = await src.ProbeAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task ProbeAsync_stops_retrying_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ => Respond(HttpStatusCode.TooManyRequests));
        using CredentialsFixture creds = CredentialsFixture.Valid();

        // The cancel lands on the wait between attempts — the point at which a retry loop must give up.
        var src = new OAuthUsageSource(
            new MeterConfig { OAuthCredentialsPath = creds.Path },
            new HttpClient(handler),
            (_, _) => { cts.Cancel(); return Task.FromCanceled(cts.Token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.ProbeAsync(cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static OAuthUsageSource Source(CredentialsFixture creds, StubHandler handler, List<TimeSpan> waits) =>
        new(new MeterConfig { OAuthCredentialsPath = creds.Path },
            new HttpClient(handler),
            (d, _) => { waits.Add(d); return Task.CompletedTask; });

    private static HttpResponseMessage Respond(HttpStatusCode code, string? retryAfter = null, string body = "")
    {
        var resp = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (retryAfter is not null) resp.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return resp;
    }

    /// <summary>Counts calls and answers each one from a script, so no network is touched.</summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(Calls));
        }
    }

    /// <summary>A temp credentials store carrying a live-looking, unexpired OAuth token.</summary>
    private sealed class CredentialsFixture : IDisposable
    {
        public required string Path { get; init; }

        public static CredentialsFixture Valid()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sm-retry-" + Guid.NewGuid().ToString("N") + ".json");
            long expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
            File.WriteAllText(path, $$"""{ "claudeAiOauth": { "accessToken": "test-token", "expiresAt": {{expires}} } }""");
            return new CredentialsFixture { Path = path };
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
