using System.Text;
using System.Text.Json;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Util;

namespace SessionMeter.Core.Context;

/// <summary>
/// Reads a session's <em>external</em> context-window footprint without the session's cooperation. Every
/// Claude Code session continuously appends a transcript at
/// <c>%USERPROFILE%\.claude\projects\&lt;encoded-cwd&gt;\&lt;session-id&gt;.jsonl</c>; the latest assistant
/// message's <c>usage</c> block is the exact input footprint the API counted for that turn. This monitor
/// locates the active transcript (most-recently-modified <c>.jsonl</c> under the encoded folder), scans
/// from the end for the last usage block, sums the three input fields and turns that into a percentage of
/// <see cref="MeterConfig.WorkerContextWindow"/>.
/// </summary>
/// <remarks>
/// The encode + JSONL-scan logic is exposed as <c>public static</c> helpers so it is unit-testable without a
/// live session or the real <c>~/.claude</c> tree.
/// </remarks>
public sealed class ContextMonitor
{
    private readonly MeterConfig _cfg;

    /// <summary>Creates the monitor over the effective configuration (supplies the window denominator).</summary>
    /// <param name="cfg">Effective SessionMeter configuration.</param>
    public ContextMonitor(MeterConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    /// <summary>
    /// Reads the context footprint for the session running in <paramref name="cwd"/>. Never throws on a
    /// missing/empty transcript — returns a well-formed unknown <see cref="ContextReading"/> with a
    /// diagnostic note instead.
    /// </summary>
    /// <param name="cwd">Absolute working directory the <c>claude</c> session runs in.</param>
    /// <param name="name">Display name; null/blank ⇒ a slug of <paramref name="cwd"/>.</param>
    /// <returns>The reading (known or, when no usable transcript exists, a well-formed unknown reading).</returns>
    public ContextReading Read(string cwd, string? name = null)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Read(cwd, name, userProfile);
    }

    /// <summary>
    /// Testable overload: reads the footprint resolving the projects tree under an explicit
    /// <paramref name="userProfile"/> root (so a test can point it at a temp directory).
    /// </summary>
    /// <param name="cwd">Absolute working directory the <c>claude</c> session runs in.</param>
    /// <param name="name">Display name; null/blank ⇒ a slug of <paramref name="cwd"/>.</param>
    /// <param name="userProfile">The user-profile root that holds <c>.claude\projects</c>.</param>
    public ContextReading Read(string cwd, string? name, string userProfile)
        => ReadCore(cwd, name, userProfile, pinnedSessionId: null);

    /// <summary>
    /// Reads ONE named Claude session — never the folder's newest transcript. Added 0.8.2 (2026-10-01):
    /// on a session's first prompt its own transcript has no usage block yet, and the CWD lookup
    /// returned the PREVIOUS session in the same folder (a fresh BufferCopy session was told it stood at
    /// 72% — yesterday's figure — while it was at ~5%). A pinned read answers "unknown" instead.
    /// </summary>
    public ContextReading ReadSession(string cwd, string sessionId, string? name = null, string? userProfile = null)
        => ReadCore(cwd, name,
            userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            pinnedSessionId: sessionId);

    private ContextReading ReadCore(string cwd, string? name, string userProfile, string? pinnedSessionId)
    {
        string label = string.IsNullOrWhiteSpace(name) ? Naming.Slug(cwd) : name!;
        long window = _cfg.WorkerContextWindow > 0 ? _cfg.WorkerContextWindow : 200_000;

        if (string.IsNullOrWhiteSpace(cwd))
            return Unknown(label, window, "no working directory given");

        string projectsDir = ProjectsDirFor(userProfile, cwd);
        if (!Directory.Exists(projectsDir))
            return Unknown(label, window, $"no transcript folder for {cwd} (looked in {projectsDir})");

        string? transcript;
        if (!string.IsNullOrWhiteSpace(pinnedSessionId))
        {
            transcript = Path.Combine(projectsDir, pinnedSessionId + ".jsonl");
            if (!File.Exists(transcript))
                return Unknown(label, window, $"no transcript for session {pinnedSessionId} under {projectsDir}");
        }
        else
        {
            transcript = FindActiveTranscript(projectsDir);
            if (transcript is null)
                return Unknown(label, window, $"no .jsonl session transcript under {projectsDir}");
        }

        string sessionId = Path.GetFileNameWithoutExtension(transcript);

        UsageScan scan;
        try
        {
            scan = ScanLastUsage(File.ReadLines(transcript));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(label, window, $"could not read transcript {transcript}: {ex.Message}")
                with { SessionId = sessionId, TranscriptPath = transcript };
        }

        if (!scan.Found)
            return Unknown(label, window, $"transcript {sessionId} has no usage block yet")
                with { SessionId = sessionId, TranscriptPath = transcript };

        // Tier 0 — GROUND TRUTH, added 2026-08-28: a window a statusline integration cached for THIS exact
        // session id after Claude Code told it the real context_window.context_window_size. No model name
        // is even consulted when this hits — see ContextWindowResolver.TryReadNativeWindowCache for why
        // this is the genuinely future-proof tier (works for a model that doesn't exist yet).
        long? nativeWindow = ContextWindowResolver.TryReadNativeWindowCache(_cfg.NativeWindowCacheDir, sessionId);

        long effectiveWindow;
        bool windowDetected;
        string? windowModel;

        if (nativeWindow is { } nw)
        {
            effectiveWindow = nw;
            windowDetected = true;
            windowModel = scan.Model;
        }
        else
        {
            // Fallback tiers 1-3: detect the real context window (200K vs 1M) from Claude Code's own
            // per-project model state; the configured WorkerContextWindow becomes the explicit FALLBACK
            // when detection isn't possible either.
            WindowResolution resolution = ContextWindowResolver.Resolve(cwd, scan.Model, userProfile, window);
            effectiveWindow = resolution.Window;
            windowDetected = resolution.Detected;
            windowModel = resolution.Model ?? scan.Model;

            // Safety net, added 2026-08-28: USED tokens exceeding an UNDETECTED window is itself evidence
            // the assumption is wrong — Claude compacts before a session can genuinely overrun, so e.g.
            // "100.0% (409,308 / 200,000)" is arithmetic announcing its own impossibility, not a real
            // reading (see ContextReading.WindowSourceSuffix's "USED EXCEEDS WINDOW" guard, which used to
            // just label this rather than correct it). Only escalates when NOTHING else recognised the
            // window — an explicit detected fact (tiers 1-2.5, or the native cache above) always stands.
            if (!windowDetected && scan.UsedTokens > effectiveWindow && effectiveWindow < ContextWindowResolver.LargeWindow)
            {
                effectiveWindow = ContextWindowResolver.LargeWindow;
            }
        }

        double pct = ComputePct(scan.UsedTokens, effectiveWindow);
        return new ContextReading(
            label, sessionId, scan.UsedTokens, effectiveWindow, pct, scan.Timestamp, transcript,
            Note: null, WindowDetected: windowDetected, Model: windowModel);
    }

    private static ContextReading Unknown(string name, long window, string note)
        => new(name, null, 0, window, 0, null, null, note);

    // ── public static helpers (unit-testable without a live session) ────────────────────────────────────

    /// <summary>
    /// Encodes an absolute working directory into the Claude Code project folder name: every <c>:</c>,
    /// <c>\</c>, <c>/</c> and <c>.</c> becomes <c>-</c> and case is preserved. Examples:
    /// <c>C:\dev\mo</c> ⇒ <c>C--dev-mo</c>, <c>C:\Dev\PAV\PAVBrain</c> ⇒ <c>C--Dev-PAV-PAVBrain</c>,
    /// <c>C:\Dev\PAV\PAVBrain\.claude\worktrees\X</c> ⇒ <c>C--Dev-PAV-PAVBrain--claude-worktrees-X</c>.
    ///
    /// <para>🛑 THE DOT IS PART OF THE RULE. Omitting it made every worktree session unreadable: Claude Code
    /// puts worktrees under <c>&lt;repo&gt;\.claude\worktrees\</c>, so the encoded name always contains a
    /// dot-segment. The old mapping produced <c>…-.claude-worktrees-…</c> — a folder that never exists — and
    /// the caller's natural fallback to the parent repo then hit <see cref="FindActiveTranscript"/>, which
    /// returns the newest transcript in that folder: a DIFFERENT session's numbers, reported with full
    /// confidence. That produced a false Rule-3 stop-work on 2026-08-11.</para>
    ///
    /// <para>⚠️ A SPACE IS NOT ENCODED — <c>C:\Dev\MQL4\IntervalTrade EA</c> ⇒
    /// <c>C--Dev-MQL4-IntervalTrade EA</c>, observed on disk. This is a separator-and-dot rule, NOT a
    /// sanitise-everything rule; broadening it would break folders that currently resolve.</para>
    /// </summary>
    /// <param name="cwd">The absolute working directory.</param>
    public static string EncodeCwd(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return string.Empty;
        // Claude Code encodes the cwd WITHOUT a trailing separator, so trim one before encoding —
        // otherwise "C:\dev\mo\" would encode to "C--dev-mo-" and miss the real "C--dev-mo" folder.
        cwd = cwd.TrimEnd('\\', '/');
        var sb = new StringBuilder(cwd.Length);
        foreach (char c in cwd)
            sb.Append(c is ':' or '\\' or '/' or '.' ? '-' : c);
        return sb.ToString();
    }

    /// <summary>The absolute <c>&lt;userProfile&gt;\.claude\projects\&lt;encoded-cwd&gt;</c> directory.</summary>
    /// <param name="userProfile">The user-profile root.</param>
    /// <param name="cwd">The session's working directory.</param>
    public static string ProjectsDirFor(string userProfile, string cwd)
        => Path.Combine(userProfile, ".claude", "projects", EncodeCwd(cwd));

    /// <summary>
    /// Picks the active session transcript in a project folder: the most-recently-modified <c>.jsonl</c>
    /// (the same "newest wins" heuristic the chat-image extractor uses). Returns null when the folder holds
    /// no <c>.jsonl</c> file.
    /// </summary>
    /// <param name="projectsDir">The encoded project folder under <c>.claude\projects</c>.</param>
    public static string? FindActiveTranscript(string projectsDir)
    {
        if (string.IsNullOrWhiteSpace(projectsDir) || !Directory.Exists(projectsDir))
            return null;

        return new DirectoryInfo(projectsDir)
            .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    /// <summary>
    /// Scans transcript lines (one JSON object per line) from the END and returns the first (i.e. last in
    /// file order) assistant message carrying a <c>usage</c> object with <c>input_tokens</c>. The used total
    /// is <c>input_tokens + cache_read_input_tokens + cache_creation_input_tokens</c> (each missing field
    /// treated as 0; <c>output_tokens</c> ignored). The assistant <c>message.model</c> on that same line is
    /// captured too (null when absent — the bare base id, no <c>[1m]</c> marker). Malformed lines are skipped,
    /// so a partially-written tail line never aborts the scan.
    /// </summary>
    /// <param name="lines">The transcript lines (e.g. <see cref="File.ReadLines(string)"/>).</param>
    public static UsageScan ScanLastUsage(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Materialise so we can walk from the end; transcripts are line-oriented and modest in size.
        IReadOnlyList<string> all = lines as IReadOnlyList<string> ?? lines.ToList();

        // 0.8.3 (2026-11-10): Claude Code auto-compacts an idle session and writes a `compact_boundary` line
        // whose compactMetadata.postTokens is the new footprint. Until the next reply lands, the newest usage
        // line is the PRE-compaction figure (482,724 on 85c6b6ce while the bar showed ~3%), which fired false
        // Rule-3 stops. A boundary NEWER than every usage line therefore IS the measurement; the older usage
        // line still supplies the model id for window detection.
        long? postTokens = null;
        DateTimeOffset? boundaryTs = null;
        for (int i = all.Count - 1; i >= 0; i--)
        {
            string line = all[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (postTokens is null && TryReadCompactBoundary(line, out long post, out DateTimeOffset? bts))
            {
                postTokens = post;
                boundaryTs = bts;
                continue;
            }
            if (TryReadUsage(line, out long used, out DateTimeOffset? ts, out string? model))
            {
                return postTokens is { } p
                    ? new UsageScan(true, p, boundaryTs, model, Compacted: true)
                    : new UsageScan(true, used, ts, model);
            }
        }
        return postTokens is { } onlyPost
            ? new UsageScan(true, onlyPost, boundaryTs, null, Compacted: true)
            : new UsageScan(false, 0, null, null);
    }

    /// <summary>
    /// Reads a <c>{"type":"system","subtype":"compact_boundary","compactMetadata":{"postTokens":N}}</c> line.
    /// Returns false for any other line, or a boundary without a numeric <c>postTokens</c>.
    /// </summary>
    private static bool TryReadCompactBoundary(string line, out long postTokens, out DateTimeOffset? timestamp)
    {
        postTokens = 0;
        timestamp = null;
        if (!line.Contains("compact_boundary", StringComparison.Ordinal)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("subtype", out JsonElement st) || st.ValueKind != JsonValueKind.String ||
                st.GetString() != "compact_boundary" ||
                !root.TryGetProperty("compactMetadata", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object ||
                !meta.TryGetProperty("postTokens", out JsonElement pt) || pt.ValueKind != JsonValueKind.Number)
                return false;
            postTokens = ReadLong(pt);
            if (root.TryGetProperty("timestamp", out JsonElement tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(tsEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                timestamp = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns a used-token total into a percentage of the window, rounded to one decimal and clamped to
    /// <c>[0, 100]</c> (a window of 0 or less yields 0).
    /// </summary>
    /// <param name="used">The used-token total.</param>
    /// <param name="window">The context-window size in tokens.</param>
    public static double ComputePct(long used, long window)
    {
        if (window <= 0 || used <= 0) return 0;
        double pct = (double)used / window * 100.0;
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        return Math.Round(pct, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Tolerant single-line parse: finds a nested <c>usage</c> object (under <c>message</c> or at top level)
    /// that has <c>input_tokens</c>, sums the three input fields, reads an optional top-level
    /// <c>timestamp</c>, and captures the sibling <c>message.model</c> when present. Returns false for any
    /// line that isn't a usage-bearing object.
    /// </summary>
    private static bool TryReadUsage(string line, out long used, out DateTimeOffset? timestamp, out string? model)
    {
        used = 0;
        timestamp = null;
        model = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!TryFindUsage(root, out JsonElement usage)) return false;
            if (!usage.TryGetProperty("input_tokens", out JsonElement input) ||
                input.ValueKind != JsonValueKind.Number)
                return false;

            used = ReadLong(input)
                 + ReadLong(usage, "cache_read_input_tokens")
                 + ReadLong(usage, "cache_creation_input_tokens");

            if (root.TryGetProperty("timestamp", out JsonElement tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(tsEl.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                timestamp = parsed;

            // The assistant model sits at message.model (sibling of message.usage); tolerate its absence.
            if (root.TryGetProperty("message", out JsonElement msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("model", out JsonElement modelEl) &&
                modelEl.ValueKind == JsonValueKind.String &&
                modelEl.GetString() is { Length: > 0 } m)
                model = m;

            return true;
        }
        catch (JsonException)
        {
            return false; // a partially-written or non-JSON line — skip it.
        }
    }

    /// <summary>Finds the <c>usage</c> object under <c>message.usage</c>, else a top-level <c>usage</c>.</summary>
    private static bool TryFindUsage(JsonElement root, out JsonElement usage)
    {
        if (root.TryGetProperty("message", out JsonElement msg) &&
            msg.ValueKind == JsonValueKind.Object &&
            msg.TryGetProperty("usage", out usage) &&
            usage.ValueKind == JsonValueKind.Object)
            return true;

        if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
            return true;

        usage = default;
        return false;
    }

    private static long ReadLong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number
            ? ReadLong(el)
            : 0;

    private static long ReadLong(JsonElement number)
        => number.TryGetInt64(out long v) ? v : (long)number.GetDouble();
}

/// <summary>
/// Outcome of <see cref="ContextMonitor.ScanLastUsage"/>: whether a usage block was found, the summed input
/// footprint, the timestamp of that assistant message (if present), and the assistant model on that line.
/// </summary>
/// <param name="Found">True when a usage-bearing assistant line was located.</param>
/// <param name="UsedTokens">Sum of input + cache-read + cache-creation tokens.</param>
/// <param name="Timestamp">Timestamp of the measured message, or null when absent.</param>
/// <param name="Model">The bare <c>message.model</c> id on the measured line (no <c>[1m]</c> marker), or null when absent.</param>
/// <param name="Compacted">True when the figure is a newer <c>compact_boundary</c>'s <c>postTokens</c>, not a usage line (0.8.3).</param>
public readonly record struct UsageScan(bool Found, long UsedTokens, DateTimeOffset? Timestamp, string? Model, bool Compacted = false);
