using System.Reflection;
using System.Text.Json;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Context;
using SessionMeter.Core.Usage;
using SessionMeter.Core.Util;

// Session — a tiny keyless consumer CLI. Dispatches on args[0]:
//   usage    live-read the programmatic OAuth usage endpoint (5-hour + 7-day windows)
//   context  accurate in-session context-window % from a local Claude Code or Pi transcript
//   help     concise help ( -h | --help | no args )
//   version  the assembly's informational version ( --version )
// Both features are keyless: `usage` uses the Claude Code OAuth token (NOT an Anthropic API key);
// `context` reads a local transcript and needs no login at all.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* redirected — ignore */ }

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "help";

Task<int> task = mode switch
{
    "usage" => RunUsageAsync(args),
    "context" => Task.FromResult(RunContext(args)),
    "help" or "-h" or "--help" => Task.FromResult(PrintHelp()),
    "version" or "--version" => Task.FromResult(PrintVersion()),
    _ => Task.FromResult(Unknown(mode)),
};
return await task;

// ── usage: one-shot live read of the programmatic usage endpoint ─────────────────────────────────────
// Keyless — uses the Claude Code OAuth token, NOT the Anthropic API key. Prints both windows + reset
// times, or (for API-key / not-signed-in users) an elegant message pointing at `session context`.
async Task<int> RunUsageAsync(string[] a)
{
    bool raw = false;
    bool codex = false;
    bool cwdSpecified = false;
    string cwd = Directory.GetCurrentDirectory();
    string? sessionId = null;

    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--raw" or "-r": raw = true; break;
            case "--codex": codex = true; break;
            case "--cwd" or "--session":
                if (i + 1 >= a.Length)
                {
                    Console.Error.WriteLine($"session usage: {a[i]} requires a value.");
                    return 2;
                }
                if (a[i] == "--cwd") { cwd = a[++i]; cwdSpecified = true; } else sessionId = a[++i];
                break;
            default:
                Console.Error.WriteLine($"session usage: unknown option '{a[i]}'.");
                return 2;
        }
    }

    if (!codex && (cwdSpecified || sessionId is not null))
    {
        Console.Error.WriteLine("session usage: --cwd and --session are supported only with --codex.");
        return 2;
    }

    if (codex)
    {
        CodexUsageResult codexResult = new CodexUsageSource().Read(cwd, sessionId);
        if (!codexResult.Ok)
        {
            Console.Error.WriteLine($"session usage --codex: {codexResult.Error ?? "no data returned"}");
            return 1;
        }
        if (raw)
        {
            Console.WriteLine("Raw Codex rate_limits snapshot");
            Console.WriteLine("────────────────────────────────────────────────");
            Console.WriteLine(codexResult.Snapshot!.Raw);
            Console.WriteLine();
        }
        PrintCodexUsageSnapshot(codexResult.Snapshot!);
        return 0;
    }

    MeterConfig cfg = new();
    using var http = new HttpClient();
    var src = new OAuthUsageSource(cfg, http);

    using CancellationTokenSource cts = ConsoleCancel();
    OAuthUsageResult result = await src.ProbeAsync(cts.Token);

    if (!result.Ok)
    {
        switch (result.Reason)
        {
            case UsageUnavailableReason.NoCredentialsFile:
            case UsageUnavailableReason.NoOAuthToken:
                // API-key / not-signed-in user — a clean, friendly message (not a raw diagnostic).
                Console.WriteLine(UsageMessages.ApiUserUsageMessage());
                return 3;

            case UsageUnavailableReason.TokenExpired:
                Console.Error.WriteLine(
                    "session usage: your Claude Code session token has expired — run `claude` to refresh it, then retry.");
                return 1;

            // A transient upstream throttle (429) or upstream fault (5xx) that survived the bounded retries.
            // This is an OUTCOME, not a crash, so it goes to STDOUT — stderr is reserved for a genuine
            // failure of the tool or the login, and a shell that paints any stderr write red made a momentary
            // Anthropic rate limit indistinguishable from a broken command.
            //
            // Exit code 4, and deliberately NOT 0. Exiting 0 would claim a measurement that was never taken;
            // no snapshot exists here and none is printed. It is also not 1, because the whole point is that
            // a caller can tell "Anthropic is throttling right now, try again shortly" apart from "your token
            // is dead / this tool is broken". Codes already in use: 0 success · 1 genuine failure (expired
            // token, terminal HTTP status, network error, unknown command) · 2 bad `context` arguments ·
            // 3 API-key / not-signed-in. 4 was free, and verified unclaimed on 2026-08-16: every known
            // consumer of `session usage` — the shipped example hooks and the author's own wrapper scripts —
            // parses STDOUT for the JSON body and none reads the exit code (see examples/hooks/warn-usage.ps1).
            case UsageUnavailableReason.RateLimited:
            case UsageUnavailableReason.UpstreamUnavailable:
                Console.WriteLine(UsageMessages.TransientlyUnavailableMessage(
                    result.Error ?? "the endpoint did not answer", result.Attempts, result.RetryAfter));
                return 4;

            default:
                Console.Error.WriteLine($"session usage: {result.Error ?? "no data returned"}");
                Console.Error.WriteLine($"  endpoint    : {cfg.OAuthUsageUrl}");
                Console.Error.WriteLine($"  credentials : {src.CredentialsPath}");
                return 1;
        }
    }

    if (raw)
    {
        PrintRawUsageBody(result.Snapshot!.Raw);
        Console.WriteLine();
    }
    PrintUsageSnapshot(result.Snapshot!);
    return 0;
}

static void PrintCodexUsageSnapshot(CodexUsageSnapshot snapshot)
{
    Console.WriteLine("Session usage — Codex recorded snapshot");
    Console.WriteLine("────────────────────────────────────────────────");
    foreach (CodexRateLimitWindow window in snapshot.Windows)
    {
        string reset = window.ResetsAt is { } r
            ? r <= DateTimeOffset.UtcNow
                ? $"reset timestamp expired {r:u}"
                : $"resets {LocalClock.FormatResetLocal(r)} · {r:u}"
            : "reset time not reported";
        string scope = string.IsNullOrWhiteSpace(window.Scope) ? "" : $" · {window.Scope}";
        Console.WriteLine($"  {window.Label,-16}: {window.Percent,5:0.#}% used · {reset}{scope}");
    }

    string observed = snapshot.ObservedAt is { } at
        ? $"{at.ToLocalTime():yyyy-MM-ddTHH:mm:sszzz} ({DescribeAge(DateTimeOffset.UtcNow - at)} old)"
        : "timestamp not reported";
    string limit = snapshot.LimitName ?? snapshot.LimitId ?? "unnamed";
    Console.WriteLine($"  observed       : {observed}");
    Console.WriteLine($"  quota          : {limit} · session {ShortId(snapshot.SessionId)}");
    Console.WriteLine("  freshness      : refreshed by Codex activity; this command does not poll the service");
}

static string ShortId(string value) => string.IsNullOrWhiteSpace(value) ? "?" : value[..Math.Min(8, value.Length)];

static string DescribeAge(TimeSpan age)
{
    if (age < TimeSpan.Zero) return "clock-ahead";
    if (age.TotalMinutes < 2) return $"{Math.Max(0, (int)age.TotalSeconds)}s";
    if (age.TotalHours < 2) return $"{(int)age.TotalMinutes}m";
    if (age.TotalDays < 2) return $"{(int)age.TotalHours}h";
    return $"{(int)age.TotalDays}d";
}

// Pretty-prints the exact JSON body the endpoint returned (for `session usage --raw`). Falls back to the
// verbatim string if it does not parse as JSON. The body may contain an OAuth-scoped token or opaque ids —
// this is a local diagnostic surface only, printed to the operator's own console.
static void PrintRawUsageBody(string body)
{
    Console.WriteLine("Raw /api/oauth/usage body");
    Console.WriteLine("────────────────────────────────────────────────");
    try
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (JsonException)
    {
        Console.WriteLine(body);
    }
}

// ── usage rendering ──────────────────────────────────────────────────────────────────────────────────
static void PrintUsageSnapshot(UsageSnapshot snap)
{
    Console.WriteLine("Session usage — live, programmatic (/api/oauth/usage)");
    Console.WriteLine("────────────────────────────────────────────────");
    PrintUsageWindow("5-hour session", snap.FiveHour);
    PrintUsageWindow("7-day window  ", snap.SevenDay);

    // Per-model / any additional walls the endpoint reports. The two above are the all-models windows;
    // entries here beyond those (e.g. a per-model Fable weekly) are what the top-level fields miss.
    var extra = snap.ModelLimits
        .Where(l => l.ModelName is { Length: > 0 } || l.Kind is not ("session" or "weekly_all"))
        .ToList();
    if (extra.Count > 0)
    {
        Console.WriteLine("  ── per-model / scoped walls ──");
        foreach (UsageLimit l in extra)
        {
            string reset = l.ResetsAt is { } r
                ? $"resets {LocalClock.FormatResetLocal(r)} · {r:u}"
                : "reset time not reported";
            string flags = l.IsActive ? " · ACTIVE" : "";
            string sev = string.Equals(l.Severity, "normal", StringComparison.OrdinalIgnoreCase) ? "" : $" · {l.Severity}";
            Console.WriteLine($"  {l.Label,-16}: {l.Percent,3}% used · {reset}{sev}{flags}");
        }
    }

    UsageLimit? b = snap.BindingLimit;
    if (b is null)
        Console.WriteLine("  binding        : (none — no window data returned)");
    else
        Console.WriteLine($"  binding        : {b.Label} @ {b.Percent}%");
}

static void PrintUsageWindow(string label, WindowUsage? w)
{
    if (w is null) { Console.WriteLine($"  {label} : (not reported)"); return; }
    // Local rendering is ceiled to the minute; the exact second stays visible in the raw UTC.
    string reset = w.ResetsAt is { } r
        ? $"resets {LocalClock.FormatResetLocal(r)} · {r:u}"
        : "reset time not reported";
    Console.WriteLine($"  {label} : {w.Percent,3}% used · {reset}");
}

// ── context: one-shot external context-% read of a session's transcript ──────────────────────────────
// Keyless — reads a local Claude Code, Codex, or Pi session JSONL for a working directory.
// Default cwd is the current directory; `--cwd <path>` reads any directory; `--pi` selects Pi.
int RunContext(string[] a)
{
    MeterConfig cfg = new();
    string cwd = Directory.GetCurrentDirectory();
    string? sessionId = null;
    bool pi = false;
    bool codex = false;

    for (int i = 1; i < a.Length; i++)
    {
        if (a[i] is "--pi")
        {
            pi = true;
            continue;
        }
        if (a[i] is "--codex")
        {
            codex = true;
            continue;
        }

        if (a[i] is "--cwd" or "--session")
        {
            if (i + 1 >= a.Length)
            {
                Console.Error.WriteLine($"session context: {a[i]} requires a value.");
                return 2;
            }

            if (a[i] == "--cwd")
                cwd = a[++i];
            else
                sessionId = a[++i];
        }
    }

    if (pi && codex)
    {
        Console.Error.WriteLine("session context: choose either --pi or --codex.");
        return 2;
    }
    if (!pi && !codex && !string.IsNullOrWhiteSpace(sessionId))
    {
        Console.Error.WriteLine("session context: --session is supported only with --pi or --codex.");
        return 2;
    }

    ContextReading reading = pi
        ? new PiContextMonitor(cfg).Read(cwd, name: null, sessionId)
        : codex
            ? new CodexContextMonitor(cfg).Read(cwd, name: null, sessionId)
            : new ContextMonitor(cfg).Read(cwd, name: null);
    Console.WriteLine(reading.ToLine());
    return 0;
}

// ── help / version ───────────────────────────────────────────────────────────────────────────────────
static int PrintHelp()
{
    Console.WriteLine(
"""
Session — accurate in-session context % + Claude Code/Codex usage windows (keyless)

Usage:
  session context [--pi|--codex] [--cwd <path>] [--session <id>]
                                   Accurate context-window %, read from a local Claude Code transcript
                                   by default, a Pi transcript with --pi, or a Codex transcript with --codex.
                                   No args = current directory; --cwd reads any directory. With --pi or --codex, --session pins the exact
                                   session ID instead of selecting the newest CWD transcript. Kills the guesswork native `/context` only
                                   shows interactively.
  session usage [--raw]            Live Claude 5-hour + 7-day windows from the OAuth usage endpoint.
  session usage --codex [--cwd <path>] [--session <id>] [--raw]
                                   Latest Codex rate-limit snapshot recorded in a local rollout.
  session help                     This help.
  session version                  Print the version.

Keyless: `context` and `usage --codex` read local transcripts. Claude `usage` needs an Anthropic subscription
login (Pro/Max OAuth token). Codex usage is an observed snapshot refreshed by Codex activity, not a live poll.

⚠️  `session usage` reads the UNDOCUMENTED, unversioned GET /api/oauth/usage endpoint — it may change or
    disappear without notice. `session context` depends only on the local Claude Code, Codex, or Pi transcript format.
""");
    return 0;
}

static int PrintVersion()
{
    string version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
    Console.WriteLine($"Session {version}");
    return 0;
}

static int Unknown(string mode)
{
    Console.Error.WriteLine($"Unknown command '{mode}'. Try `session help`.");
    return 1;
}

static CancellationTokenSource ConsoleCancel()
{
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    return cts;
}

/// <summary>
/// Pure, testable CLI message text for the <c>session usage</c> command. Kept out of the top-level program
/// body so the account-type-aware message can be unit-tested.
/// </summary>
public static class UsageMessages
{
    /// <summary>
    /// The elegant, account-type-aware message shown when <c>session usage</c> can't read the OAuth windows
    /// because the user is on an Anthropic API key (or isn't signed in to Claude Code). It points the user at
    /// the working <c>session context</c> command and deliberately contains NO raw endpoint URL.
    /// </summary>
    public static string ApiUserUsageMessage() =>
"""
Session usage — unavailable

`session usage` reads Claude Code's live rate-limit windows (5-hour + 7-day),
which require a Claude subscription login (Pro/Max). That signs you in with an
OAuth token — an Anthropic API key can't read these windows.

It looks like you're using an API key (or aren't signed in to Claude Code).
  • Run `claude` and sign in with your Claude subscription to enable usage windows.
  • `session context` works for you regardless — it reads the local session
    transcript and needs no login.
""";

    /// <summary>
    /// The message shown when the usage endpoint was throttling (HTTP 429) or failing (5xx) and was still
    /// doing so after the bounded retries. Its job is to separate "upstream is busy" from "this tool is
    /// broken", because they used to read identically.
    /// <para>
    /// Two hard constraints. First, NO figure is invented: the attempt count is the measured one, and a wait
    /// time is quoted only when the endpoint actually sent a <c>Retry-After</c> — otherwise the text says
    /// plainly that no wait time was given. No usage percentage appears at all, cached or estimated, because
    /// no snapshot was read.
    /// </para>
    /// <para>
    /// Second, the text contains NO brace character. Every known consumer of <c>session usage --raw</c> finds
    /// the JSON body by scanning stdout for the first <c>{</c>; a brace in this prose would be handed to a
    /// JSON parser. <c>UsageMessagesTests</c> pins that.
    /// </para>
    /// </summary>
    /// <param name="diagnostic">The status description, e.g. "HTTP 429 — rate limited".</param>
    /// <param name="attempts">How many HTTP attempts were actually made.</param>
    /// <param name="retryAfter">The endpoint's <c>Retry-After</c> wait, or null when it sent none.</param>
    public static string TransientlyUnavailableMessage(string diagnostic, int attempts, TimeSpan? retryAfter)
    {
        string tries = attempts == 1 ? "1 attempt" : $"{attempts} attempts";
        string wait = retryAfter is { } r
            ? $"The endpoint asked for a wait of {DescribeWait(r)} before the next try."
            : "The endpoint did not say how long to wait, so no retry time can be quoted here.";

        return $"""
Session usage — temporarily unavailable

{diagnostic}, after {tries}.

This is an upstream rate limit or outage on Anthropic's side, not a fault in this
tool and not a problem with your login. No usage snapshot was read, so none is
shown — no cached or estimated percentages are printed in place of a live reading.

  • {wait}
  • `session context` is unaffected — it reads the local session transcript and
    needs no network and no login.
""";
    }

    /// <summary>Renders a wait as whole seconds (rounded up), or whole minutes once it passes two minutes.</summary>
    private static string DescribeWait(TimeSpan wait)
    {
        int seconds = (int)Math.Ceiling(wait.TotalSeconds);
        if (seconds < 120) return seconds == 1 ? "1 second" : $"{seconds} seconds";
        int minutes = (int)Math.Ceiling(wait.TotalMinutes);
        return $"about {minutes} minutes";
    }
}
