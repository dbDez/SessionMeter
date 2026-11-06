using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Util;

namespace SessionMeter.Core.Context;

/// <summary>Reads Codex context from its local JSONL rollout transcript.</summary>
public sealed class CodexContextMonitor
{
    private readonly MeterConfig _cfg;

    public CodexContextMonitor(MeterConfig cfg) => _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

    public ContextReading Read(string cwd, string? name = null, string? sessionId = null)
    {
        string? selectedSession = FirstNonBlank(sessionId,
            Environment.GetEnvironmentVariable("CODEX_THREAD_ID"),
            Environment.GetEnvironmentVariable("CODEX_SESSION_ID"));
        return ReadFromHome(cwd, name, ResolveCodexHome(), selectedSession);
    }

    /// <summary>Test seam that derives Codex home from an explicit profile and ignores process environment.</summary>
    public ContextReading Read(string cwd, string? name, string userProfile, string? sessionId = null)
        => ReadFromHome(cwd, name, Path.Combine(userProfile, ".codex"), sessionId);

    private ContextReading ReadFromHome(string cwd, string? name, string codexHome, string? sessionId)
    {
        string label = string.IsNullOrWhiteSpace(name) ? Naming.Slug(cwd) : name!;
        if (string.IsNullOrWhiteSpace(cwd)) return Unknown(label, "no working directory given");

        string root = Path.Combine(codexHome, "sessions");
        CodexTranscript? found;
        try { found = FindActiveTranscript(root, cwd, sessionId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(label, $"could not inspect Codex sessions under {root}: {ex.Message}");
        }
        if (found is null)
        {
            string selection = string.IsNullOrWhiteSpace(sessionId) ? $"for {cwd}" : $"with id {sessionId} for {cwd}";
            return Unknown(label, $"no Codex session transcript {selection} (looked under {root})");
        }

        CodexTranscript transcript = found.Value;
        CodexUsageScan scan;
        try { scan = ScanLastUsage(ReadSharedLines(transcript.Path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(label, $"could not read transcript {transcript.Path}: {ex.Message}")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };
        }

        if (!scan.Found)
            return Unknown(label, $"Codex transcript {transcript.SessionId} has no token-count event yet")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };
        if (scan.ContextWindow <= 0)
            return Unknown(label, $"Codex transcript {transcript.SessionId} did not report a model context window")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path, AsOf = scan.Timestamp, Model = scan.Model };

        return new ContextReading(label, transcript.SessionId, scan.UsedTokens, scan.ContextWindow,
            ContextMonitor.ComputePct(scan.UsedTokens, scan.ContextWindow), scan.Timestamp, transcript.Path,
            Note: null, WindowDetected: true, Model: scan.Model);
    }

    private static ContextReading Unknown(string name, string note) => new(name, null, 0, 0, 0, null, null, note);

    /// <summary>Returns CODEX_HOME when set, otherwise the conventional profile-local Codex directory.</summary>
    public static string ResolveCodexHome(string? userProfile = null, string? codexHome = null)
    {
        string? configured = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        string profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }

    /// <summary>Finds the exact selected rollout, or the newest rollout whose structured metadata matches the cwd.</summary>
    public static CodexTranscript? FindActiveTranscript(string sessionsRoot, string cwd, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionsRoot) || !Directory.Exists(sessionsRoot)) return null;
        string wanted = NormalizePath(cwd);

        CodexTranscript? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        foreach (FileInfo file in new DirectoryInfo(sessionsRoot).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            string? header;
            try { header = ReadFirstLineShared(file.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (!TryReadSessionMeta(header, out string? transcriptCwd, out string? transcriptSessionId, out string? transcriptId)) continue;
            string id = transcriptSessionId ?? transcriptId ?? SessionIdFromPath(file.Name);
            if (!string.Equals(NormalizePath(transcriptCwd), wanted, StringComparison.Ordinal)) continue;
            bool exactMatch = string.IsNullOrWhiteSpace(sessionId) ||
                string.Equals(sessionId, transcriptSessionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sessionId, transcriptId, StringComparison.OrdinalIgnoreCase);
            if (!exactMatch) continue;
            if (!string.IsNullOrWhiteSpace(sessionId)) return new CodexTranscript(file.FullName, id, transcriptId);
            if (file.LastWriteTimeUtc > newestWrite)
            {
                newestWrite = file.LastWriteTimeUtc;
                newest = new CodexTranscript(file.FullName, id, transcriptId);
            }
        }
        return newest;
    }

    private static string SessionIdFromPath(string fileName)
    {
        Match match = Regex.Match(fileName, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : Path.GetFileNameWithoutExtension(fileName);
    }

    internal static string? ReadFirstLineShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadLine();
    }

    internal static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    /// <summary>Reads the last token event, associated with the most recent preceding turn-context model.</summary>
    public static CodexUsageScan ScanLastUsage(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? model = null;
        CodexUsageScan last = new(false, 0, 0, null, null);
        foreach (string line in lines)
        {
            if (TryReadTurnModel(line, out string? nextModel)) model = nextModel;
            if (TryReadUsage(line, model, out CodexUsageScan scan)) last = scan;
        }
        return last;
    }

    private static bool TryReadSessionMeta(string? line, out string? cwd, out string? sessionId, out string? id)
    {
        cwd = null;
        sessionId = null;
        id = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line), new JsonReaderOptions { MaxDepth = 256 });
            string? type = null;
            int payloadDepth = -1;
            bool nextObjectIsPayload = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject && nextObjectIsPayload)
                {
                    payloadDepth = reader.CurrentDepth;
                    nextObjectIsPayload = false;
                    continue;
                }
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.CurrentDepth == 1 && reader.ValueTextEquals("type"))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String) type = reader.GetString();
                    continue;
                }
                if (reader.CurrentDepth == 1 && reader.ValueTextEquals("payload"))
                {
                    nextObjectIsPayload = true;
                    continue;
                }
                if (payloadDepth >= 0 && reader.CurrentDepth == payloadDepth + 1)
                {
                    if (reader.ValueTextEquals("cwd"))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.String) cwd = reader.GetString();
                    }
                    else if (reader.ValueTextEquals("session_id"))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.String) sessionId = reader.GetString();
                    }
                    else if (reader.ValueTextEquals("id"))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.String) id = reader.GetString();
                    }
                }
                if (type == "session_meta" && cwd is not null && sessionId is not null && id is not null) return true;
            }
            return type == "session_meta" && !string.IsNullOrWhiteSpace(cwd);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadTurnModel(string line, out string? model)
    {
        model = null;
        if (!TryReadRoot(line, out JsonDocument? doc)) return false;
        using (JsonDocument parsed = doc!)
        {
            JsonElement root = parsed.RootElement;
            if (ReadString(root, "type") != "turn_context" || !TryObject(root, "payload", out JsonElement payload)) return false;
            model = ReadString(payload, "model");
            return !string.IsNullOrWhiteSpace(model);
        }
    }

    private static bool TryReadUsage(string line, string? model, out CodexUsageScan scan)
    {
        scan = new(false, 0, 0, null, null);
        if (!TryReadRoot(line, out JsonDocument? doc)) return false;
        using (JsonDocument parsed = doc!)
        {
            JsonElement root = parsed.RootElement;
            if (ReadString(root, "type") != "event_msg" || !TryObject(root, "payload", out JsonElement payload) ||
                ReadString(payload, "type") != "token_count" || !TryObject(payload, "info", out JsonElement info) ||
                !TryObject(info, "last_token_usage", out JsonElement usage)) return false;
            long used = ReadLong(usage, "input_tokens");
            if (used <= 0) return false;
            scan = new(true, used, ReadLong(info, "model_context_window"), ReadIsoTimestamp(root, "timestamp"), model);
            return true;
        }
    }

    private static bool TryReadRoot(string? line, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4_096 });
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { doc?.Dispose(); doc = null; return false; }
    }

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.ToLowerInvariant();
    }

    internal static bool TryObject(JsonElement parent, string name, out JsonElement value)
        => parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    internal static string? ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static long ReadLong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
    internal static double? ReadDouble(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : null;
    internal static DateTimeOffset? ReadIsoTimestamp(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
           DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) ? parsed : null;
    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public readonly record struct CodexTranscript(string Path, string SessionId, string? ThreadId = null);
public readonly record struct CodexUsageScan(bool Found, long UsedTokens, long ContextWindow, DateTimeOffset? Timestamp, string? Model);
