using System.Text.Json;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Util;

namespace SessionMeter.Core.Context;

/// <summary>
/// Reads a Pi coding-agent session's context footprint from its persisted JSONL transcript. Pi records the
/// provider-reported input, cache-read, and cache-write tokens on every assistant message; its model registry
/// supplies the configured context-window denominator for the active provider/model pair.
/// </summary>
public sealed class PiContextMonitor
{
    private readonly MeterConfig _cfg;

    /// <summary>Creates a monitor over the effective SessionMeter configuration.</summary>
    /// <param name="cfg">Effective SessionMeter configuration.</param>
    public PiContextMonitor(MeterConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    /// <summary>Reads the newest Pi session for <paramref name="cwd"/> under the current user's profile.</summary>
    public ContextReading Read(string cwd, string? name = null)
        => Read(cwd, name, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Testable overload that resolves Pi's session and model-registry files under an explicit user-profile root.
    /// Missing or unexpected state returns an unknown reading rather than throwing.
    /// </summary>
    public ContextReading Read(string cwd, string? name, string userProfile)
    {
        string label = string.IsNullOrWhiteSpace(name) ? Naming.Slug(cwd) : name!;
        long fallback = _cfg.WorkerContextWindow > 0 ? _cfg.WorkerContextWindow : ContextWindowResolver.StandardWindow;

        if (string.IsNullOrWhiteSpace(cwd))
            return Unknown(label, fallback, "no working directory given");

        string sessionsRoot = Path.Combine(userProfile, ".pi", "agent", "sessions");
        PiTranscript? foundTranscript = FindActiveTranscript(sessionsRoot, cwd);
        if (foundTranscript is null)
            return Unknown(label, fallback, $"no Pi session transcript for {cwd} (looked under {sessionsRoot})");
        PiTranscript transcript = foundTranscript.Value;

        PiUsageScan scan;
        try
        {
            scan = ScanLastUsage(File.ReadLines(transcript.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(label, fallback, $"could not read transcript {transcript.Path}: {ex.Message}")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };
        }

        if (!scan.Found)
            return Unknown(label, fallback, $"Pi transcript {transcript.SessionId} has no successful usage block yet")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };

        long window = fallback;
        bool detected = TryResolveWindow(userProfile, scan.Provider, scan.Model, out long resolvedWindow);
        if (detected)
            window = resolvedWindow;

        return new ContextReading(
            label, transcript.SessionId, scan.UsedTokens, window,
            ContextMonitor.ComputePct(scan.UsedTokens, window), scan.Timestamp, transcript.Path,
            Note: null, WindowDetected: detected, Model: scan.Model);
    }

    private static ContextReading Unknown(string name, long window, string note)
        => new(name, null, 0, window, 0, null, null, note);

    /// <summary>
    /// Finds the most recently modified Pi transcript whose session-header <c>cwd</c> equals
    /// <paramref name="cwd"/> after separator/case normalization. Header matching avoids relying on Pi's
    /// implementation-specific session-directory encoding.
    /// </summary>
    public static PiTranscript? FindActiveTranscript(string sessionsRoot, string cwd)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot))
            return null;

        string wanted = NormalizePath(cwd);
        PiTranscript? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        foreach (FileInfo file in new DirectoryInfo(sessionsRoot).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                string? header = File.ReadLines(file.FullName).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                using JsonDocument doc = JsonDocument.Parse(header);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "session" ||
                    !root.TryGetProperty("cwd", out JsonElement sessionCwd) || sessionCwd.ValueKind != JsonValueKind.String ||
                    !string.Equals(NormalizePath(sessionCwd.GetString()), wanted, StringComparison.Ordinal))
                    continue;

                string sessionId = root.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? Path.GetFileNameWithoutExtension(file.Name)
                    : Path.GetFileNameWithoutExtension(file.Name);

                if (file.LastWriteTimeUtc > newestWrite)
                {
                    newestWrite = file.LastWriteTimeUtc;
                    newest = new PiTranscript(file.FullName, sessionId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A concurrently written or malformed session must not prevent finding another valid session.
            }
        }

        return newest;
    }

    /// <summary>
    /// Scans Pi JSONL entries from the end and returns the latest successful assistant usage record. Error
    /// entries with zero usage are deliberately skipped so a transient provider failure cannot erase the last
    /// measured context reading.
    /// </summary>
    public static PiUsageScan ScanLastUsage(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        IReadOnlyList<string> all = lines as IReadOnlyList<string> ?? lines.ToList();
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (TryReadUsage(all[i], out PiUsageScan scan))
                return scan;
        }

        return new PiUsageScan(false, 0, null, null, null);
    }

    private static bool TryReadUsage(string line, out PiUsageScan scan)
    {
        scan = new PiUsageScan(false, 0, null, null, null);
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "message" ||
                !root.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out JsonElement role) || role.GetString() != "assistant" ||
                !message.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
                return false;

            long used = ReadLong(usage, "input") + ReadLong(usage, "cacheRead") + ReadLong(usage, "cacheWrite");
            if (used <= 0)
                return false;

            string? provider = ReadString(message, "provider");
            string? model = ReadString(message, "model");
            DateTimeOffset? timestamp = ReadUnixMilliseconds(message, "timestamp") ?? ReadIsoTimestamp(root, "timestamp");
            scan = new PiUsageScan(true, used, timestamp, provider, model);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryResolveWindow(string userProfile, string? provider, string? model, out long window)
    {
        window = 0;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            return false;

        string path = Path.Combine(userProfile, ".pi", "agent", "models.json");
        if (!File.Exists(path))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("providers", out JsonElement providers) || providers.ValueKind != JsonValueKind.Object ||
                !providers.TryGetProperty(provider, out JsonElement providerConfig) || providerConfig.ValueKind != JsonValueKind.Object ||
                !providerConfig.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement candidate in models.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(candidate, "id"), model, StringComparison.OrdinalIgnoreCase))
                    continue;

                long contextWindow = ReadLong(candidate, "contextWindow");
                if (contextWindow <= 0)
                    return false;

                window = contextWindow;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        return false;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string normalized = path.Replace('\\', '/').TrimEnd('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.ToLowerInvariant();
    }

    private static string? ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt64(out long number) ? number : (long)value.GetDouble()
            : 0;

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long milliseconds))
            return null;

        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTimeOffset? ReadIsoTimestamp(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
           DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;
}

/// <summary>Pi transcript identity resolved from its session header.</summary>
public readonly record struct PiTranscript(string Path, string SessionId);

/// <summary>Latest successful Pi assistant usage record.</summary>
public readonly record struct PiUsageScan(bool Found, long UsedTokens, DateTimeOffset? Timestamp, string? Provider, string? Model);
