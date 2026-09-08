using System.Globalization;
using System.Text.Json;
using SessionMeter.Core.Context;

namespace SessionMeter.Core.Usage;

/// <summary>Reads Codex quota snapshots recorded in local rollout token-count events.</summary>
public sealed class CodexUsageSource
{
    public CodexUsageResult Read(string cwd, string? sessionId = null)
    {
        string? selected = FirstNonBlank(sessionId,
            Environment.GetEnvironmentVariable("CODEX_THREAD_ID"),
            Environment.GetEnvironmentVariable("CODEX_SESSION_ID"));
        return ReadFromHome(cwd, CodexContextMonitor.ResolveCodexHome(), selected);
    }

    /// <summary>Pure test seam that does not consult CODEX_HOME or current-session environment variables.</summary>
    public CodexUsageResult Read(string cwd, string codexHome, string? sessionId)
        => ReadFromHome(cwd, codexHome, sessionId);

    private static CodexUsageResult ReadFromHome(string cwd, string codexHome, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return CodexUsageResult.Unavailable("no working directory given");
        string root = Path.Combine(codexHome, "sessions");
        CodexTranscript? transcript = CodexContextMonitor.FindActiveTranscript(root, cwd, sessionId);
        if (transcript is null)
        {
            string selected = string.IsNullOrWhiteSpace(sessionId) ? cwd : $"session {sessionId} for {cwd}";
            return CodexUsageResult.Unavailable($"no Codex rollout found for {selected} under {root}");
        }

        try
        {
            CodexUsageSnapshot? snapshot = ScanLastSnapshot(CodexContextMonitor.ReadSharedLines(transcript.Value.Path), transcript.Value);
            return snapshot is null
                ? CodexUsageResult.Unavailable($"Codex rollout {transcript.Value.SessionId} has no rate-limit snapshot yet", transcript.Value)
                : new CodexUsageResult(snapshot, null, transcript.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CodexUsageResult.Unavailable($"could not read rollout {transcript.Value.Path}: {ex.Message}", transcript.Value);
        }
    }

    public static CodexUsageSnapshot? ScanLastSnapshot(IEnumerable<string> lines, CodexTranscript transcript = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        CodexUsageSnapshot? last = null;
        foreach (string line in lines)
            if (TryParseSnapshot(line, transcript, out CodexUsageSnapshot? snapshot)) last = snapshot;
        return last;
    }

    private static bool TryParseSnapshot(string line, CodexTranscript transcript, out CodexUsageSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (CodexContextMonitor.ReadString(root, "type") != "event_msg" ||
                !CodexContextMonitor.TryObject(root, "payload", out JsonElement payload) ||
                CodexContextMonitor.ReadString(payload, "type") != "token_count" ||
                !CodexContextMonitor.TryObject(payload, "rate_limits", out JsonElement limits)) return false;

            List<CodexRateLimitWindow> windows = [];
            AddWindow(limits, "primary", "Primary", null, windows);
            AddWindow(limits, "secondary", "Secondary", null, windows);
            if (limits.TryGetProperty("individual_limit", out JsonElement individual) && individual.ValueKind == JsonValueKind.Object)
            {
                string? scope = CodexContextMonitor.ReadString(individual, "limit_name") ?? CodexContextMonitor.ReadString(individual, "limit_id");
                bool nested = AddWindow(individual, "primary", "Individual primary", scope, windows);
                nested |= AddWindow(individual, "secondary", "Individual secondary", scope, windows);
                if (!nested) AddDirectWindow(individual, "Individual", scope, windows);
            }
            if (windows.Count == 0) return false;

            string raw = JsonSerializer.Serialize(limits, new JsonSerializerOptions { WriteIndented = true });
            snapshot = new CodexUsageSnapshot(transcript.SessionId, transcript.Path,
                CodexContextMonitor.ReadIsoTimestamp(root, "timestamp"),
                CodexContextMonitor.ReadString(limits, "limit_id"),
                CodexContextMonitor.ReadString(limits, "limit_name"), windows, raw);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool AddWindow(JsonElement parent, string property, string fallbackLabel, string? scope, List<CodexRateLimitWindow> windows)
    {
        if (!CodexContextMonitor.TryObject(parent, property, out JsonElement value)) return false;
        return AddDirectWindow(value, fallbackLabel, scope, windows);
    }

    private static bool AddDirectWindow(JsonElement value, string fallbackLabel, string? scope, List<CodexRateLimitWindow> windows)
    {
        double? percent = CodexContextMonitor.ReadDouble(value, "used_percent");
        if (percent is null) return false;
        long minutes = CodexContextMonitor.ReadLong(value, "window_minutes");
        long resetSeconds = CodexContextMonitor.ReadLong(value, "resets_at");
        DateTimeOffset? reset = resetSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetSeconds) : null;
        string label = minutes switch
        {
            300 => "5-hour window",
            10_080 => "7-day window",
            > 0 => $"{minutes.ToString(CultureInfo.InvariantCulture)}-minute window",
            _ => fallbackLabel,
        };
        windows.Add(new CodexRateLimitWindow(label, percent.Value, minutes > 0 ? minutes : null, reset, scope));
        return true;
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record CodexRateLimitWindow(string Label, double Percent, long? WindowMinutes, DateTimeOffset? ResetsAt, string? Scope);
public sealed record CodexUsageSnapshot(string SessionId, string TranscriptPath, DateTimeOffset? ObservedAt,
    string? LimitId, string? LimitName, IReadOnlyList<CodexRateLimitWindow> Windows, string Raw);
public sealed record CodexUsageResult(CodexUsageSnapshot? Snapshot, string? Error, CodexTranscript Transcript)
{
    public bool Ok => Snapshot is not null;
    public static CodexUsageResult Unavailable(string error, CodexTranscript transcript = default) => new(null, error, transcript);
}
