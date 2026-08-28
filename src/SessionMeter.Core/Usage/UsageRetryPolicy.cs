using System.Globalization;

namespace SessionMeter.Core.Usage;

/// <summary>
/// The pure retry decision for <see cref="OAuthUsageSource.ProbeAsync"/> — no HTTP, no clock, no I/O, so the
/// whole matrix is unit-testable. Two rules shape it:
/// <list type="bullet">
///   <item><description>
///     Only a TRANSIENT status is retried (429 and 5xx). 401/403/404 are terminal: the token is rejected,
///     the account is forbidden, or the undocumented endpoint is gone. Re-sending the identical request with
///     the identical token cannot change any of those, so retrying them is theatre that only burns the
///     operator's time.
///   </description></item>
///   <item><description>
///     Every wait is drawn from ONE total budget (<see cref="TotalRetryBudget"/>). <c>session usage</c> runs
///     interactively and inside prompt hooks, so the tool's worst case has to stay predictable — the budget
///     bounds the added latency no matter what the endpoint asks for.
///   </description></item>
/// </list>
/// </summary>
public static class UsageRetryPolicy
{
    /// <summary>
    /// Total HTTP attempts, including the first one — so at most two retries. Three attempts spread over the
    /// budget below is enough to ride out a momentary throttle without turning a hook into a stall.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// The ceiling on the TOTAL time spent waiting between attempts (the request time itself is not counted —
    /// that is <see cref="HttpClient.Timeout"/>'s job and is deliberately left alone). Five seconds is chosen
    /// against the calling context, not the endpoint: <c>session usage</c> is typed at a prompt and is also
    /// called from Claude Code prompt hooks that block the user's turn while they run. Beyond a few seconds
    /// an operator reads the tool as hung, which is a worse failure than reporting the throttle honestly.
    /// </summary>
    public static readonly TimeSpan TotalRetryBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Exponential backoff by attempt number (1-based): 1 s after the first failure, 2 s after the second.
    /// Used only when the endpoint did not send a <c>Retry-After</c> — a server that names its own wait wins.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    /// <summary>
    /// Any <c>Retry-After</c> longer than this is treated as unusable rather than clamped. Clamping would make
    /// the tool report a wait the endpoint never asked for, and the whole point of the value is to quote the
    /// server verbatim to the operator.
    /// </summary>
    private static readonly TimeSpan MaxUsableRetryAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// True when an HTTP status is worth a second attempt: 429 (throttled) or any 5xx (upstream fault).
    /// Everything else — notably 401, 403 and 404 — is terminal.
    /// </summary>
    /// <param name="statusCode">The HTTP status code the endpoint returned.</param>
    public static bool IsTransient(int statusCode) =>
        statusCode == 429 || statusCode is >= 500 and <= 599;

    /// <summary>
    /// Parses an HTTP <c>Retry-After</c> header value in either RFC 9110 form: delta-seconds
    /// (<c>Retry-After: 120</c>) or an HTTP-date (<c>Retry-After: Wed, 21 Oct 2015 07:28:00 GMT</c>). A date
    /// already in the past yields <see cref="TimeSpan.Zero"/>, never a negative wait. Returns false — meaning
    /// "no usable hint, fall back to backoff" — for a blank, malformed, negative or absurd value; it never
    /// invents a number.
    /// </summary>
    /// <param name="headerValue">The raw header value, or null when the header was absent.</param>
    /// <param name="now">The reference instant used to convert an HTTP-date into a wait.</param>
    /// <param name="delay">The parsed wait when this returns true; otherwise <see cref="TimeSpan.Zero"/>.</param>
    public static bool TryParseRetryAfter(string? headerValue, DateTimeOffset now, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        string s = headerValue.Trim();

        // delta-seconds. NumberStyles.None rejects a sign, so "-5" falls through and is then rejected as a
        // date too — a malformed value must not become a wait.
        if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds > (long)MaxUsableRetryAfter.TotalSeconds) return false;
            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        // HTTP-date. AssumeUniversal covers the trailing "GMT" form the spec mandates.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset when))
        {
            TimeSpan remaining = when - now;
            if (remaining > MaxUsableRetryAfter) return false;
            delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Decides whether to make another attempt and how long to wait first. Returns false — give up now — when
    /// the status is terminal, the attempt budget is spent, the time budget is spent, or the wait the endpoint
    /// asked for is longer than the time budget that remains. That last case is deliberate: waiting 5 s when
    /// the server said 60 s is a request that is near-certain to be refused again, so the honest move is to
    /// stop and report the server's own number to the operator.
    /// </summary>
    /// <param name="statusCode">The status code of the attempt that just failed.</param>
    /// <param name="attempt">The 1-based number of the attempt that just failed.</param>
    /// <param name="retryAfter">The endpoint's parsed <c>Retry-After</c>, or null when it sent none.</param>
    /// <param name="budgetRemaining">How much of <see cref="TotalRetryBudget"/> is still unspent.</param>
    /// <param name="delay">The wait to observe before the next attempt when this returns true.</param>
    public static bool TryGetDelay(
        int statusCode,
        int attempt,
        TimeSpan? retryAfter,
        TimeSpan budgetRemaining,
        out TimeSpan delay)
    {
        delay = TimeSpan.Zero;

        if (!IsTransient(statusCode)) return false;
        if (attempt < 1 || attempt >= MaxAttempts) return false;
        if (budgetRemaining <= TimeSpan.Zero) return false;

        TimeSpan wait = retryAfter ?? Backoff[Math.Min(attempt - 1, Backoff.Length - 1)];
        if (wait > budgetRemaining) return false;

        delay = wait;
        return true;
    }
}
