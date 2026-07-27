using System.Globalization;

namespace SessionMeter.Core.Context;

/// <summary>
/// An external, worker-independent measurement of how full a Claude Code session's context window is,
/// derived from the latest <c>usage</c> block in that session's transcript JSONL (the exact input
/// footprint the API counted). A <see cref="Known"/> reading carries a real percentage; an unknown one
/// (no transcript yet, or a transcript with no usage line) is still well-formed — it reports 0 tokens and
/// a human-readable <see cref="Note"/> rather than throwing.
/// </summary>
/// <param name="Name">The worker name (or a slug of the cwd when read by raw path).</param>
/// <param name="SessionId">The session id (transcript file stem), or null when unknown.</param>
/// <param name="UsedTokens">Input footprint: <c>input + cache_read + cache_creation</c> tokens (0 when unknown).</param>
/// <param name="WindowTokens">The context-window denominator (<see cref="Configuration.MeterConfig.WorkerContextWindow"/>).</param>
/// <param name="Pct">Used as a percentage of the window, rounded to one decimal (0 when unknown).</param>
/// <param name="AsOf">Timestamp of the measured assistant message, or null when unknown.</param>
/// <param name="TranscriptPath">Absolute path to the transcript read, or null when none was found.</param>
/// <param name="Note">A diagnostic note set when the reading is unknown; null on a successful read.</param>
/// <param name="WindowDetected">
/// True when <paramref name="WindowTokens"/> was detected from Claude Code's recorded per-project model state
/// (via <see cref="ContextWindowResolver"/>); false when it is the assumed standard fallback.
/// </param>
/// <param name="Model">The active model id the window was resolved against, or null when unknown.</param>
public sealed record ContextReading(
    string Name,
    string? SessionId,
    long UsedTokens,
    long WindowTokens,
    double Pct,
    DateTimeOffset? AsOf,
    string? TranscriptPath,
    string? Note = null,
    bool WindowDetected = false,
    string? Model = null)
{
    /// <summary>True when a real usage measurement was found (a transcript with a usage block).</summary>
    public bool Known => Note is null && SessionId is not null;

    /// <summary>
    /// Renders the one-line CLI form, e.g.
    /// <c>pavbrain: 18.4% (36,846 / 200,000 tokens) — session ab12cd, as of 2026-06-30T01:40:12+02:00</c>.
    /// An unknown reading renders <c>&lt;name&gt;: context unknown — &lt;note&gt;</c>.
    /// </summary>
    public string ToLine()
    {
        if (!Known)
            return $"{Name}: context unknown — {Note ?? "no usage data"}";

        string sid = SessionId is { Length: > 0 } s ? s[..Math.Min(8, s.Length)] : "?";
        string asOf = AsOf is { } t
            ? t.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            : "unknown time";
        return $"{Name}: {Pct.ToString("0.0", CultureInfo.InvariantCulture)}% "
             + $"({UsedTokens.ToString("N0", CultureInfo.InvariantCulture)} / "
             + $"{WindowTokens.ToString("N0", CultureInfo.InvariantCulture)} tokens) — "
             + $"session {sid}, as of {asOf}{WindowSourceSuffix()}";
    }

    /// <summary>
    /// The short window-source suffix appended to the known-form line, e.g. <c> · 1M window (detected)</c>,
    /// <c> · 200K window (detected)</c>, or <c> · 200K window (assumed)</c> when detection failed.
    /// </summary>
    private string WindowSourceSuffix()
    {
        string size = WindowTokens == 1_000_000 ? "1M" : WindowTokens == 200_000 ? "200K" : $"{WindowTokens:N0}";
        string source = WindowDetected ? "detected" : "assumed";

        // A reading at or past 100% is far more likely a WRONG DENOMINATOR than a genuinely exhausted window:
        // Claude compacts before a session can actually overrun, so "100.0% (335,065 / 200,000)" is arithmetic
        // announcing its own impossibility — used tokens exceeded the window it was measured against. Say so
        // instead of reporting it as a usage figure. On 2026-07-27 a 1M session read exactly that for hours and
        // nothing flagged it; the number was believed and would have fired spurious Rule 3 checkpoints.
        if (UsedTokens > WindowTokens)
            return $" · {size} window ({source}) · ⚠ USED EXCEEDS WINDOW — window detection is wrong, treat this % as unreliable";

        return $" · {size} window ({source})";
    }
}
