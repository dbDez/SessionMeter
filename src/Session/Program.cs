using System.Reflection;
using System.Text.Json;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Context;
using SessionMeter.Core.Usage;
using SessionMeter.Core.Util;

// Session — a tiny keyless consumer CLI. Dispatches on args[0]:
//   usage    live-read the programmatic OAuth usage endpoint (5-hour + 7-day windows)
//   context  accurate in-session context-window % from the local session transcript
//   help     concise help ( -h | --help | no args )
//   version  the assembly's informational version ( --version )
// Both features are keyless: `usage` uses the Claude Code OAuth token (NOT an Anthropic API key);
// `context` reads the local session transcript and needs no login at all.
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
    bool raw = a.Any(x => x is "--raw" or "-r");

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
// Keyless — reads the Claude Code session JSONL for a working directory (no login, no Anthropic key).
// Default cwd is the current directory; `--cwd <path>` reads any directory.
int RunContext(string[] a)
{
    MeterConfig cfg = new();
    string cwd = Directory.GetCurrentDirectory();

    for (int i = 1; i < a.Length; i++)
    {
        if (a[i] is "--cwd")
        {
            if (i + 1 >= a.Length)
            {
                Console.Error.WriteLine("session context: --cwd requires a path.");
                return 2;
            }
            cwd = a[++i];
        }
    }

    ContextReading reading = new ContextMonitor(cfg).Read(cwd, name: null);
    Console.WriteLine(reading.ToLine());
    return 0;
}

// ── help / version ───────────────────────────────────────────────────────────────────────────────────
static int PrintHelp()
{
    Console.WriteLine(
"""
Session — accurate in-session context % + live usage windows for Claude Code (keyless)

Usage:
  session context [--cwd <path>]   Accurate context-window %, read from the local Claude Code session
                                   transcript. No args = current directory; --cwd reads any directory.
                                   Kills the guesswork native `/context` only shows interactively.
  session usage [--raw]            Live 5-hour + 7-day rate-limit windows from the OAuth usage endpoint.
                                   --raw also prints the exact JSON body the endpoint returned.
  session help                     This help.
  session version                  Print the version.

Keyless: `context` reads the local session transcript and needs NO login. `usage` needs a Claude
subscription login (Pro/Max OAuth token) — API-key users get a friendly message and should use `context`.

⚠️  `session usage` reads the UNDOCUMENTED, unversioned GET /api/oauth/usage endpoint — it may change or
    disappear without notice. `session context` depends only on the local transcript format.
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
}
