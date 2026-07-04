using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Serialization;

namespace SessionMeter.Core.Usage;

/// <summary>
/// Reads the logged-in Claude Code account's structured usage from the undocumented
/// <c>GET /api/oauth/usage</c> endpoint, authenticated with the OAuth access token from the Claude Code
/// credential store (<c>%USERPROFILE%\.claude\.credentials.json</c> → <c>claudeAiOauth.accessToken</c>).
/// The token is re-read FRESH on every poll (never cached) so this picks up Claude Code's own token
/// refreshes; it is NEVER logged. This source has zero logging dependency and surfaces failures via a typed
/// <see cref="OAuthUsageResult"/> instead.
/// CONFIRM: the endpoint, headers and JSON shape are undocumented — verify with <c>session usage</c>.
/// </summary>
public sealed class OAuthUsageSource
{
    private static readonly JsonSerializerOptions CredJson = new() { PropertyNameCaseInsensitive = true };

    private readonly MeterConfig _cfg;
    private readonly HttpClient _http;

    /// <summary>Creates the OAuth usage source.</summary>
    /// <param name="cfg">Effective configuration (endpoint, beta header, credentials path).</param>
    /// <param name="http">HTTP client used for the GET (one host, long-lived — a singleton is fine).</param>
    public OAuthUsageSource(MeterConfig cfg, HttpClient http)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Resolved credentials-store path: explicit config, else the per-user default.</summary>
    public string CredentialsPath =>
        string.IsNullOrWhiteSpace(_cfg.OAuthCredentialsPath)
            ? DefaultCredentialsPath()
            : _cfg.OAuthCredentialsPath;

    /// <summary>
    /// Reads the full two-window snapshot (used by <c>session usage</c>). Returns null on any failure.
    /// Never throws except on cancellation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<UsageSnapshot?> ReadSnapshotAsync(CancellationToken ct)
    {
        OAuthUsageResult result = await ProbeAsync(ct);
        return result.Snapshot;
    }

    /// <summary>
    /// Diagnostic probe: returns either a snapshot or a human-readable error string plus a typed
    /// <see cref="UsageUnavailableReason"/> (no logging, no throwing except on cancellation), so
    /// <c>session usage</c> can tell the user exactly why a live check failed (missing credentials, expired
    /// token, HTTP 401/404, …) and tailor the message for API-key users.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<OAuthUsageResult> ProbeAsync(CancellationToken ct)
    {
        string path = CredentialsPath;
        string token;
        try
        {
            if (!File.Exists(path))
                return new OAuthUsageResult(null, $"credentials file not found at {path}",
                    UsageUnavailableReason.NoCredentialsFile);

            string raw = await File.ReadAllTextAsync(path, ct);
            CredentialsFile? creds = JsonSerializer.Deserialize<CredentialsFile>(raw, CredJson);
            OAuthCreds? oauth = creds?.ClaudeAiOauth;
            if (oauth is null || string.IsNullOrWhiteSpace(oauth.AccessToken))
                return new OAuthUsageResult(null, $"no claudeAiOauth.accessToken in {path}",
                    UsageUnavailableReason.NoOAuthToken);

            if (oauth.ExpiresAt is long exp)
            {
                DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(exp);
                if (expiresAt <= DateTimeOffset.UtcNow)
                    return new OAuthUsageResult(null,
                        $"token expired at {expiresAt:u} — run `claude` to refresh it",
                        UsageUnavailableReason.TokenExpired);
            }

            token = oauth.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new OAuthUsageResult(null, $"could not read credentials at {path}: {ex.Message}",
                UsageUnavailableReason.NoCredentialsFile);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _cfg.OAuthUsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrWhiteSpace(_cfg.OAuthBetaHeader))
                req.Headers.TryAddWithoutValidation("anthropic-beta", _cfg.OAuthBetaHeader);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new OAuthUsageResult(null, DescribeStatus((int)resp.StatusCode),
                    UsageUnavailableReason.HttpError);

            string body = await resp.Content.ReadAsStringAsync(ct);
            return new OAuthUsageResult(ParseSnapshot(body), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new OAuthUsageResult(null, $"request to {_cfg.OAuthUsageUrl} failed: {ex.Message}",
                UsageUnavailableReason.NetworkError);
        }
    }

    /// <summary>
    /// Pure JSON → <see cref="UsageSnapshot"/> parse (no HTTP), factored out for unit testing. Tolerates
    /// missing/renamed fields: a missing window or a window without <c>utilization</c> becomes null, and
    /// <c>utilization</c> is read as a PERCENT (0–100) verbatim, rounded and clamped. The endpoint's percent
    /// semantics are live-verified (2026-06-29 returned 100/39; 2026-07-02 returned 1 for a 1%-used week).
    /// The old "≤ 1.0 ⇒ fraction ×100" heuristic is deliberately GONE: on a fresh weekly window it turned
    /// a real 1% into 100%, convincing the supervisor the wall was hit — it checkpointed the whole fleet
    /// into WAITING_RESET for a week. A false-low (if the API ever switched to fractions) is recoverable;
    /// a false-100% freezes the fleet.
    /// </summary>
    /// <param name="json">The raw response body.</param>
    public static UsageSnapshot ParseSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new UsageSnapshot(null, null, json ?? string.Empty);

        UsageDto? dto = JsonSerializer.Deserialize<UsageDto>(json, CoreJson.Snake);
        return new UsageSnapshot(
            ToWindow(dto?.FiveHour),
            ToWindow(dto?.SevenDay),
            json,
            ToLimits(dto?.Limits));
    }

    private static WindowUsage? ToWindow(WindowDto? dto)
    {
        if (dto?.Utilization is not double u) return null;
        int rounded = (int)Math.Round(Math.Clamp(u, 0.0, 100.0), MidpointRounding.AwayFromZero);
        return new WindowUsage(rounded, dto.ResetsAt);
    }

    /// <summary>
    /// Maps the endpoint's <c>limits[]</c> array to the general named-window model. Each entry's
    /// <c>percent</c> is read VERBATIM as a 0–100 integer (clamped, NO fraction heuristic — the same
    /// false-100% safeguard as <see cref="ToWindow"/>: a fresh window at 1% must stay 1%, never become 100%
    /// and freeze the fleet). Entries without a <c>percent</c> are dropped (nothing to bind on). Returns null
    /// when the body carried no array, so <see cref="UsageSnapshot"/> falls back to the top-level windows.
    /// </summary>
    private static IReadOnlyList<UsageLimit>? ToLimits(IReadOnlyList<LimitDto>? dtos)
    {
        if (dtos is null) return null;
        var list = new List<UsageLimit>(dtos.Count);
        foreach (LimitDto d in dtos)
        {
            if (d.Percent is not int p) continue;
            list.Add(new UsageLimit(
                Kind: d.Kind ?? string.Empty,
                Group: d.Group ?? string.Empty,
                Percent: Math.Clamp(p, 0, 100),
                ResetsAt: d.ResetsAt,
                ModelName: d.Scope?.Model?.DisplayName,
                Severity: d.Severity ?? "normal",
                IsActive: d.IsActive ?? false));
        }
        return list;
    }

    private static string DescribeStatus(int code) => code switch
    {
        401 => "HTTP 401 — token rejected/unauthorised",
        403 => "HTTP 403 — forbidden",
        404 => "HTTP 404 — endpoint unavailable",
        429 => "HTTP 429 — rate limited",
        _ => $"HTTP {code}",
    };

    private static string DefaultCredentialsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");

    // ── Wire DTOs ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>On-the-wire shape of the OAuth usage response (snake_case via <see cref="CoreJson.Snake"/>).</summary>
    private sealed record UsageDto
    {
        public WindowDto? FiveHour { get; init; }
        public WindowDto? SevenDay { get; init; }
        public List<LimitDto>? Limits { get; init; }
    }

    /// <summary>One window object: raw <c>utilization</c> + ISO <c>resets_at</c>.</summary>
    private sealed record WindowDto
    {
        public double? Utilization { get; init; }
        public DateTimeOffset? ResetsAt { get; init; }
    }

    /// <summary>One entry of the <c>limits[]</c> array: kind/group/percent/severity/reset + optional model scope.</summary>
    private sealed record LimitDto
    {
        public string? Kind { get; init; }
        public string? Group { get; init; }
        public int? Percent { get; init; }
        public string? Severity { get; init; }
        public DateTimeOffset? ResetsAt { get; init; }
        public ScopeDto? Scope { get; init; }
        public bool? IsActive { get; init; }
    }

    /// <summary>A limit's <c>scope</c> object — currently just the scoped model.</summary>
    private sealed record ScopeDto
    {
        public ModelDto? Model { get; init; }
    }

    /// <summary>The scoped model's identity (<c>display_name</c> is the human label, e.g. "Fable").</summary>
    private sealed record ModelDto
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
    }

    /// <summary>Top-level shape of <c>.credentials.json</c> (camelCase, matched case-insensitively).</summary>
    private sealed record CredentialsFile
    {
        [JsonPropertyName("claudeAiOauth")]
        public OAuthCreds? ClaudeAiOauth { get; init; }
    }

    /// <summary>The <c>claudeAiOauth</c> object: access token + epoch-millisecond expiry.</summary>
    private sealed record OAuthCreds
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expiresAt")]
        public long? ExpiresAt { get; init; }
    }
}

/// <summary>
/// Why a live usage read was unavailable. Drives the CLI's account-type-aware messaging: the two
/// credential-absent reasons (<see cref="NoCredentialsFile"/>, <see cref="NoOAuthToken"/>) identify an
/// API-key / not-signed-in user who should be shown the friendly "use <c>session context</c>" message
/// rather than a raw diagnostic.
/// </summary>
public enum UsageUnavailableReason
{
    /// <summary>No failure — a snapshot was obtained.</summary>
    None,

    /// <summary>The credentials file was missing (or could not be read/parsed) — treat as not signed in.</summary>
    NoCredentialsFile,

    /// <summary>The credentials file exists but carries no <c>claudeAiOauth.accessToken</c> — an API-key user.</summary>
    NoOAuthToken,

    /// <summary>The OAuth token is present but expired — run <c>claude</c> to refresh it.</summary>
    TokenExpired,

    /// <summary>The endpoint returned a non-success HTTP status.</summary>
    HttpError,

    /// <summary>A network/transport error (HTTP request, JSON, or timeout) occurred during the request.</summary>
    NetworkError,
}

/// <summary>
/// Outcome of <see cref="OAuthUsageSource.ProbeAsync"/>: a <see cref="UsageSnapshot"/> on success, or a
/// human-readable <see cref="Error"/> plus a typed <see cref="Reason"/> explaining why the live check failed.
/// </summary>
/// <param name="Snapshot">The parsed snapshot, or null on failure.</param>
/// <param name="Error">A diagnostic message, or null on success.</param>
/// <param name="Reason">The typed failure reason (<see cref="UsageUnavailableReason.None"/> on success).</param>
public sealed record OAuthUsageResult(
    UsageSnapshot? Snapshot,
    string? Error,
    UsageUnavailableReason Reason = UsageUnavailableReason.None)
{
    /// <summary>True when a snapshot was obtained and no error occurred.</summary>
    public bool Ok => Snapshot is not null && Error is null;
}
