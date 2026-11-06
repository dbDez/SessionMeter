using System.Text.Json;
using SessionMeter.Core.Configuration;
using SessionMeter.Core.Util;

namespace SessionMeter.Core.Context;

/// <summary>
/// Reads a Pi coding-agent session's context footprint from its persisted JSONL transcript. Pi records the
/// provider-reported input, cache-read, and cache-write tokens on every assistant message. The measured
/// assistant model supplies the denominator; a later <c>model_change</c> is tracked separately until that newly
/// selected model produces its own usage block. Window resolution follows Pi's built-in catalogue plus the
/// user's <c>models.json</c> overlays.
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
    public ContextReading Read(string cwd, string? name = null, string? sessionId = null)
        => Read(cwd, name, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sessionId);

    /// <summary>
    /// Testable overload that resolves Pi's session and model-registry files under an explicit user-profile root.
    /// Missing or unexpected state returns an unknown reading rather than throwing.
    /// </summary>
    public ContextReading Read(
        string cwd,
        string? name,
        string userProfile,
        string? sessionId = null,
        string? builtInModelsRoot = null)
    {
        string label = string.IsNullOrWhiteSpace(name) ? Naming.Slug(cwd) : name!;
        long fallback = _cfg.WorkerContextWindow > 0 ? _cfg.WorkerContextWindow : ContextWindowResolver.StandardWindow;

        if (string.IsNullOrWhiteSpace(cwd))
            return Unknown(label, fallback, "no working directory given");

        string sessionsRoot = Path.Combine(userProfile, ".pi", "agent", "sessions");
        PiTranscript? foundTranscript = FindActiveTranscript(sessionsRoot, cwd, sessionId);
        if (foundTranscript is null)
        {
            string selection = string.IsNullOrWhiteSpace(sessionId)
                ? $"for {cwd}"
                : $"with id {sessionId} for {cwd}";
            return Unknown(label, fallback, $"no Pi session transcript {selection} (looked under {sessionsRoot})");
        }
        PiTranscript transcript = foundTranscript.Value;

        PiModelState state;
        try
        {
            state = ScanModelState(File.ReadLines(transcript.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(label, fallback, $"could not read transcript {transcript.Path}: {ex.Message}")
                with { SessionId = transcript.SessionId, TranscriptPath = transcript.Path };
        }

        PiUsageScan scan = state.Usage;
        string? selectedProvider = state.SelectedProvider ?? scan.Provider;
        string? selectedModel = state.SelectedModel ?? scan.Model;
        if (!scan.Found)
            return Unknown(label, fallback, $"Pi transcript {transcript.SessionId} has no successful usage block yet")
                with
                {
                    SessionId = transcript.SessionId,
                    TranscriptPath = transcript.Path,
                    SelectedProvider = selectedProvider,
                    SelectedModel = selectedModel,
                };

        long window = fallback;
        bool detected = TryResolveWindow(
            userProfile, scan.Provider, scan.Model, builtInModelsRoot, out long resolvedWindow);
        if (detected)
            window = resolvedWindow;

        return new ContextReading(
            label, transcript.SessionId, scan.UsedTokens, window,
            ContextMonitor.ComputePct(scan.UsedTokens, window), scan.Timestamp, transcript.Path,
            Note: null, WindowDetected: detected, Model: scan.Model,
            Provider: scan.Provider, SelectedProvider: selectedProvider, SelectedModel: selectedModel);
    }

    private static ContextReading Unknown(string name, long window, string note)
        => new(name, null, 0, window, 0, null, null, note);

    /// <summary>
    /// Finds a Pi transcript whose session-header <c>cwd</c> equals <paramref name="cwd"/> after
    /// separator/case normalization. When <paramref name="sessionId"/> is supplied, selects that exact
    /// Pi session header id; otherwise selects the most recently modified matching transcript. Header matching
    /// avoids relying on Pi's implementation-specific session-directory encoding.
    /// </summary>
    public static PiTranscript? FindActiveTranscript(string sessionsRoot, string cwd, string? sessionId = null)
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

                string transcriptSessionId = root.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? Path.GetFileNameWithoutExtension(file.Name)
                    : Path.GetFileNameWithoutExtension(file.Name);

                if (!string.IsNullOrWhiteSpace(sessionId) &&
                    !string.Equals(sessionId, transcriptSessionId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(sessionId))
                    return new PiTranscript(file.FullName, transcriptSessionId);

                if (file.LastWriteTimeUtc > newestWrite)
                {
                    newestWrite = file.LastWriteTimeUtc;
                    newest = new PiTranscript(file.FullName, transcriptSessionId);
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
        => ScanModelState(lines).Usage;

    /// <summary>
    /// Scans the whole transcript for two deliberately separate facts: the last successful usage measurement
    /// and the latest model selected by Pi. They can differ immediately after a mid-session model switch.
    /// </summary>
    public static PiModelState ScanModelState(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        PiUsageScan usage = new(false, 0, null, null, null);
        string? selectedProvider = null;
        string? selectedModel = null;

        foreach (string line in lines)
        {
            if (TryReadUsage(line, out PiUsageScan candidate))
                usage = candidate;

            TryReadModelChange(line, ref selectedProvider, ref selectedModel);
        }

        return new PiModelState(usage, selectedProvider, selectedModel);
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

    private static void TryReadModelChange(string line, ref string? provider, ref string? model)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "model_change")
                return;

            string? changedProvider = ReadString(root, "provider");
            string? changedModel = ReadString(root, "modelId");
            if (string.IsNullOrWhiteSpace(changedProvider) || string.IsNullOrWhiteSpace(changedModel))
                return;

            provider = changedProvider;
            model = changedModel;
        }
        catch (JsonException)
        {
            // A partially written entry does not erase the last complete model selection.
        }
    }

    private static bool TryResolveWindow(
        string userProfile,
        string? provider,
        string? model,
        string? builtInModelsRoot,
        out long window)
    {
        window = 0;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            return false;

        PiBuiltInResolution builtIn = TryResolveBuiltInWindow(
            provider, model, EnumerateBuiltInModelRoots(userProfile, builtInModelsRoot));
        long? catalogWindow = TryResolveStoredWindow(
            Path.Combine(userProfile, ".pi", "agent", "models-store.json"),
            provider, model, builtIn.GeneratedAtUnixMilliseconds) ?? builtIn.Window;
        long? effectiveWindow = ApplyModelsJson(
            Path.Combine(userProfile, ".pi", "agent", "models.json"), provider, model, catalogWindow);

        if (effectiveWindow is not > 0)
            return false;

        window = effectiveWindow.Value;
        return true;
    }

    private static long? ApplyModelsJson(
        string path, string provider, string model, long? builtInWindow)
    {
        if (!File.Exists(path))
            return builtInWindow;

        try
        {
            using JsonDocument doc = ParseJsonFile(path);
            JsonElement root = doc.RootElement;
            if (!TryGetPropertyIgnoreCase(root, "providers", out JsonElement providers) ||
                !TryGetPropertyIgnoreCase(providers, provider, out JsonElement providerConfig) ||
                providerConfig.ValueKind != JsonValueKind.Object)
                return builtInWindow;

            long? effective = builtInWindow;
            if (TryGetPropertyIgnoreCase(providerConfig, "models", out JsonElement models) &&
                models.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement candidate in models.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.Object ||
                        !string.Equals(ReadString(candidate, "id"), model, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Pi treats a matching models[] definition as an upsert. An omitted window receives Pi's
                    // own custom-model default rather than inheriting the built-in model's window.
                    long configured = ReadLong(candidate, "contextWindow");
                    effective = configured > 0 ? configured : 128_000;
                    break;
                }
            }

            if (TryGetPropertyIgnoreCase(providerConfig, "modelOverrides", out JsonElement overrides) &&
                TryGetPropertyIgnoreCase(overrides, model, out JsonElement modelOverride))
            {
                long overridden = ReadLong(modelOverride, "contextWindow");
                if (overridden > 0)
                    effective = overridden;
            }

            return effective;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return builtInWindow;
        }
    }

    private static PiBuiltInResolution TryResolveBuiltInWindow(
        string provider, string model, IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            long? generatedAt = TryReadCatalogueGeneratedAt(root);
            string direct = Path.Combine(root, provider + ".json");
            IEnumerable<string> files = File.Exists(direct)
                ? [direct]
                : Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly);

            foreach (string path in files)
            {
                try
                {
                    using JsonDocument doc = ParseJsonFile(path);
                    foreach (JsonProperty api in doc.RootElement.EnumerateObject())
                    {
                        if (api.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        foreach (JsonProperty candidate in api.Value.EnumerateObject())
                        {
                            JsonElement definition = candidate.Value;
                            if (definition.ValueKind != JsonValueKind.Object ||
                                !string.Equals(ReadString(definition, "provider"), provider, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(ReadString(definition, "id") ?? candidate.Name, model, StringComparison.OrdinalIgnoreCase))
                                continue;

                            long resolved = ReadLong(definition, "contextWindow");
                            if (resolved > 0)
                                return new PiBuiltInResolution(resolved, generatedAt);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A stale or concurrently replaced catalogue file must not prevent trying another root.
                }
            }
        }

        return new PiBuiltInResolution(null, null);
    }

    private static long? TryResolveStoredWindow(
        string path, string provider, string model, long? builtInGeneratedAt)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using JsonDocument doc = ParseJsonFile(path);
            if (!TryGetPropertyIgnoreCase(doc.RootElement, provider, out JsonElement providerStore) ||
                providerStore.ValueKind != JsonValueKind.Object)
                return null;

            long lastModified = ReadLong(providerStore, "lastModified");
            if (builtInGeneratedAt is not null && (lastModified <= 0 || lastModified <= builtInGeneratedAt.Value))
                return null;

            if (!TryGetPropertyIgnoreCase(providerStore, "models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement candidate in models.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(candidate, "id"), model, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadString(candidate, "provider") ?? provider, provider, StringComparison.OrdinalIgnoreCase))
                    continue;

                long window = ReadLong(candidate, "contextWindow");
                return window > 0 ? window : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        return null;
    }

    private static long? TryReadCatalogueGeneratedAt(string root)
    {
        string manifest = Path.Combine(root, ".manifest.json");
        if (!File.Exists(manifest))
            return null;

        try
        {
            using JsonDocument doc = ParseJsonFile(manifest);
            string? generatedAt = ReadString(doc.RootElement, "generatedAt");
            return DateTimeOffset.TryParse(
                generatedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed)
                ? parsed.ToUnixTimeMilliseconds()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateBuiltInModelRoots(string userProfile, string? explicitRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(explicitRoot) && roots.Add(explicitRoot))
            yield return explicitRoot;

        string packageSuffix = Path.Combine(
            "node_modules", "@earendil-works", "pi-coding-agent", "node_modules", "@earendil-works",
            "pi-ai", "dist", "providers", "data");
        string[] prefixes =
        [
            Path.Combine(userProfile, "AppData", "Roaming", "npm"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
                ? Path.Combine(appData, "npm")
                : string.Empty,
            Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX") ?? string.Empty,
            Environment.GetEnvironmentVariable("npm_config_prefix") ?? string.Empty,
        ];

        foreach (string prefix in prefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                continue;

            string candidate = Path.Combine(prefix, packageSuffix);
            if (roots.Add(candidate))
                yield return candidate;
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        foreach (string pathPart in (pathValue ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(pathPart.Trim().Trim('"'), packageSuffix);
            if (roots.Add(candidate))
                yield return candidate;
        }
    }

    private static JsonDocument ParseJsonFile(string path)
        => JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

    private static bool TryGetPropertyIgnoreCase(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
            return false;

        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
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

/// <summary>Pi's last measured usage and latest selected model, which may temporarily differ.</summary>
public readonly record struct PiModelState(PiUsageScan Usage, string? SelectedProvider, string? SelectedModel);

internal readonly record struct PiBuiltInResolution(long? Window, long? GeneratedAtUnixMilliseconds);
