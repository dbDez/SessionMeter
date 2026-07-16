using System.Text.Json;
using System.Text.RegularExpressions;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Util;

namespace SessionMeter.Core.Context;

/// <summary>Reads Codex context from its local JSONL rollout transcript.</summary>
public sealed class CodexContextMonitor
{
    private readonly MeterConfig _cfg;
    public CodexContextMonitor(MeterConfig cfg) => _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

    public ContextReading Read(string cwd, string? name = null, string? sessionId = null)
        => Read(cwd, name, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sessionId);

    public ContextReading Read(string cwd, string? name, string userProfile, string? sessionId = null)
    {
        string label = string.IsNullOrWhiteSpace(name) ? Naming.Slug(cwd) : name!;
        long fallback = _cfg.WorkerContextWindow > 0 ? _cfg.WorkerContextWindow : ContextWindowResolver.StandardWindow;
        if (string.IsNullOrWhiteSpace(cwd)) return Unknown(label, fallback, "no working directory given");

        string root = Path.Combine(userProfile, ".codex", "sessions");
        CodexTranscript? found = FindActiveTranscript(root, cwd, sessionId);
        if (found is null)
        {
            string selection = string.IsNullOrWhiteSpace(sessionId) ? $"for {cwd}" : $"with id {sessionId} for {cwd}";
            return Unknown(label, fallback, $"no Codex session transcript {selection} (looked under {root})");
        }

        CodexTranscript transcript = found.Value;
        CodexUsageScan scan;
        try { scan = ScanLastUsage(File.ReadLines(transcript.Path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(label, fallback, $"could not read transcript {transcript.Path}: {ex.Message}")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };
        }
        if (!scan.Found)
            return Unknown(label, fallback, $"Codex transcript {transcript.SessionId} has no token-count event yet")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };

        long window = scan.ContextWindow > 0 ? scan.ContextWindow : fallback;
        return new ContextReading(label, transcript.SessionId, scan.UsedTokens, window,
            ContextMonitor.ComputePct(scan.UsedTokens, window), scan.Timestamp, transcript.Path,
            Note: null, WindowDetected: scan.ContextWindow > 0, Model: null);
    }

    private static ContextReading Unknown(string name, long window, string note) => new(name, null, 0, window, 0, null, null, note);

    /// <summary>Finds the newest transcript whose session metadata names the requested working directory.</summary>
    public static CodexTranscript? FindActiveTranscript(string sessionsRoot, string cwd, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot)) return null;
        string wanted = NormalizePath(cwd);
        CodexTranscript? newest = null;
        DateTime newestWrite = DateTime.MinValue;
        foreach (FileInfo file in new DirectoryInfo(sessionsRoot).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                string? header = File.ReadLines(file.FullName).FirstOrDefault();
                bool metadataRead = TryReadSessionMeta(header, out string? transcriptCwd, out string? transcriptSessionId);
                bool cwdMatches = metadataRead && string.Equals(NormalizePath(transcriptCwd), wanted, StringComparison.Ordinal);
                // Codex's large modern headers can contain metadata that an older JSON reader cannot traverse;
                // the exact serialized CWD field remains stable and provides a safe, narrow fallback.
                if (!cwdMatches && !HeaderContainsCwd(header, cwd)) continue;
                string id = transcriptSessionId ?? SessionIdFromPath(file.Name);
                if (!string.IsNullOrWhiteSpace(sessionId) && !string.Equals(sessionId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(sessionId)) return new CodexTranscript(file.FullName, id);
                if (file.LastWriteTimeUtc > newestWrite) { newestWrite = file.LastWriteTimeUtc; newest = new CodexTranscript(file.FullName, id); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return newest;
    }

    private static bool HeaderContainsCwd(string? header, string cwd)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        string escapedCwd = JsonSerializer.Serialize(cwd);
        string forwardSlashCwd = JsonSerializer.Serialize(cwd.Replace('\\', '/'));
        string rawEscapedCwd = cwd.Replace("\\", "\\\\", StringComparison.Ordinal);
        return header.Contains(rawEscapedCwd, StringComparison.OrdinalIgnoreCase) && header.Contains("\"cwd\"", StringComparison.Ordinal) ||
               header.Contains($"\"cwd\":{escapedCwd}", StringComparison.Ordinal) ||
               header.Contains($"\"cwd\":{forwardSlashCwd}", StringComparison.Ordinal);
    }

    private static string SessionIdFromPath(string fileName)
    {
        Match match = Regex.Match(fileName, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>Reads the last token-count event. Cached input is a subset of input_tokens and is not added again.</summary>
    public static CodexUsageScan ScanLastUsage(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        IReadOnlyList<string> all = lines as IReadOnlyList<string> ?? lines.ToList();
        for (int i = all.Count - 1; i >= 0; i--) if (TryReadUsage(all[i], out CodexUsageScan scan)) return scan;
        return new CodexUsageScan(false, 0, 0, null);
    }

    private static bool TryReadSessionMeta(string? line, out string? cwd, out string? sessionId)
    {
        cwd = null; sessionId = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        // The header can contain a very large nested instruction payload. Its identity fields are simple
        // top-level payload strings, so extract just those rather than rejecting an otherwise valid rollout
        // because an unrelated future metadata field exceeds the JSON reader's default nesting limit.
        if (!line.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal)) return false;
        cwd = ReadJsonStringField(line, "cwd");
        sessionId = ReadJsonStringField(line, "session_id") ?? ReadJsonStringField(line, "id");
        return !string.IsNullOrWhiteSpace(cwd);
    }

    private static string? ReadJsonStringField(string json, string field)
    {
        Match match = Regex.Match(json, $"\\\"{Regex.Escape(field)}\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"");
        if (!match.Success) return null;
        try { return JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\""); }
        catch (JsonException) { return null; }
    }

    private static bool TryReadUsage(string line, out CodexUsageScan scan)
    {
        scan = new CodexUsageScan(false, 0, 0, null);
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "type") != "event_msg" ||
                !root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object ||
                ReadString(payload, "type") != "token_count" || !payload.TryGetProperty("info", out JsonElement info) ||
                info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("last_token_usage", out JsonElement usage) ||
                usage.ValueKind != JsonValueKind.Object) return false;
            long used = ReadLong(usage, "input_tokens");
            if (used <= 0) return false;
            scan = new CodexUsageScan(true, used, ReadLong(info, "model_context_window"), ReadIsoTimestamp(root, "timestamp"));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.ToLowerInvariant();
    }
    private static string? ReadString(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long ReadLong(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.TryGetInt64(out long n) ? n : (long)v.GetDouble() : 0;
    private static DateTimeOffset? ReadIsoTimestamp(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) ? parsed : null;
}

public readonly record struct CodexTranscript(string Path, string SessionId);
public readonly record struct CodexUsageScan(bool Found, long UsedTokens, long ContextWindow, DateTimeOffset? Timestamp);
