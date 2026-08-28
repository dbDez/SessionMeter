namespace SessionMeter.Core.Configuration;

/// <summary>
/// The slim, self-contained configuration for the SessionMeter core. Carries ONLY the values the usage +
/// context features need, so the core has zero dependency on any host application's configuration graph,
/// hosting model, or directory layout — embed it anywhere.
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
    /// every detection tier comes up empty. Defaults to 200,000 — the standard Claude context window. A larger
    /// window IS auto-detected: although the session transcript's model field strips the <c>[1m]</c> marker (it
    /// reads e.g. <c>claude-opus-4-8</c>), <see cref="Context.ContextWindowResolver"/> resolves the real window
    /// from, strongest first, the ground-truth cache in <see cref="NativeWindowCacheDir"/>, then Claude Code's
    /// own per-project model state in <c>%USERPROFILE%\.claude.json</c> (which keeps the marker), then a known
    /// large-window model family. This value applies only when all of those fail.
    /// </summary>
    public long WorkerContextWindow { get; init; } = 200_000;

    /// <summary>
    /// Directory holding per-session GROUND-TRUTH window caches (<c>&lt;sessionId&gt;.native-window.json</c>),
    /// added 2026-08-28 as the future-proof detection tier: any statusline integration that receives Claude
    /// Code's own <c>context_window.context_window_size</c> on stdin (the harness stating a fact, not a guess)
    /// can drop a tiny JSON file here, and <see cref="Context.ContextMonitor"/> will prefer it over every
    /// name-based heuristic — for ANY model, current or future, with zero code changes when a new generation
    /// ships. Defaults to a location SessionMeter itself owns (this project has ZERO dependency on any specific
    /// user's tooling layout — see the class summary), never a caller's config directory. A caller running its
    /// own statusline hook points that hook's writer at this same default, so the compiled CLI and any fast
    /// shell reimplementation of the same reading share one cache. Writer contract and worked example:
    /// <c>INTEGRATION.md</c> §1.
    /// </summary>
    public string NativeWindowCacheDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SessionMeter", "context-cache");
}
