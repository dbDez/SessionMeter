namespace SessionMeter.Core.Configuration;

/// <summary>
/// The slim, self-contained configuration for the SessionMeter core (replaces MO's <c>MoConfig</c>). Carries
/// ONLY the four values the usage + context features need, with MO's exact defaults, so the core has zero
/// dependency on MO's configuration graph, hosting, or PKM paths.
/// </summary>
public sealed record MeterConfig
{
    /// <summary>The undocumented programmatic usage endpoint read by <c>session usage</c>.</summary>
    public string OAuthUsageUrl { get; init; } = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The <c>anthropic-beta</c> header value sent with the usage request.</summary>
    public string OAuthBetaHeader { get; init; } = "oauth-2025-04-20";

    /// <summary>
    /// Explicit path to Claude Code's credentials store; blank ⇒ the per-user default
    /// (<c>%USERPROFILE%\.claude\.credentials.json</c>), resolved at runtime.
    /// </summary>
    public string OAuthCredentialsPath { get; init; } = "";

    /// <summary>
    /// The context-window denominator (in tokens) used by <c>session context</c> as the explicit FALLBACK when
    /// window detection isn't possible. Defaults to 200,000 — the standard Claude context window. The 1M-context
    /// beta IS auto-detected: although the session transcript's model field strips the <c>[1m]</c> marker (it
    /// reads e.g. <c>claude-opus-4-8</c>), <see cref="Context.ContextWindowResolver"/> cross-references Claude
    /// Code's own per-project model state in <c>%USERPROFILE%\.claude.json</c> (which keeps the marker) and uses
    /// 1,000,000 when the active model is on the beta. This value applies only when that detection fails.
    /// </summary>
    public long WorkerContextWindow { get; init; } = 200_000;
}
