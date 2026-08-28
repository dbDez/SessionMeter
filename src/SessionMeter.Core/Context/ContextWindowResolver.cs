using System.Text;
using System.Text.Json;

namespace SessionMeter.Core.Context;

/// <summary>
/// Resolves the correct context-window denominator (200K standard vs. 1M beta) for a Claude Code session by
/// consulting Claude Code's own per-project model state in <c>%USERPROFILE%\.claude.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The session transcript's <c>message.model</c> field STRIPS the <c>[1m]</c> beta marker (it reads e.g.
/// <c>claude-opus-4-8</c> even on the 1M-context beta), so the transcript alone cannot tell a 200K window
/// from a 1M one. Claude Code, however, records the model WITH the marker per project under
/// <c>projects["&lt;cwd&gt;"].lastModelUsage</c> — a key like <c>claude-opus-4-8[1m]</c> means the 1M window.
/// </para>
/// <para>
/// <c>.claude.json</c> is <b>undocumented internal Claude Code state</b> (the same caveat class as the
/// undocumented OAuth usage endpoint): it is large, its shape may change without notice, the same working
/// directory appears under several key spellings (<c>C:/dev/myrepo</c>, <c>C:\Dev\MyRepo</c>, <c>C:/Dev/MyRepo</c> …),
/// and <c>lastModelUsage</c> is cumulative (it may list several models). This resolver therefore parses
/// tolerantly with <see cref="JsonDocument"/> navigation — never strong-typed DTOs — and NEVER throws on a
/// shape surprise; it falls back to the supplied standard window instead.
/// </para>
/// </remarks>
public static class ContextWindowResolver
{
    /// <summary>The standard Claude context window, in tokens (the assumed fallback denominator).</summary>
    public const long StandardWindow = 200_000;

    /// <summary>The large ("1M") beta context window, in tokens, selected when the active model key ends with <c>[1m]</c>.</summary>
    public const long LargeWindow = 1_000_000;

    /// <summary>
    /// Resolves the context window for a session running in <paramref name="cwd"/> by reading
    /// <c>&lt;userProfile&gt;\.claude.json</c> and matching the current model. Never throws — any missing file,
    /// parse failure, absent project, or empty <c>lastModelUsage</c> yields a non-detected fallback result.
    /// </summary>
    /// <param name="cwd">The absolute working directory the <c>claude</c> session runs in.</param>
    /// <param name="baseModel">
    /// The transcript's current bare model id (no <c>[1m]</c> marker), e.g. <c>claude-opus-4-8</c>; null/blank
    /// ⇒ the active model is chosen by highest cumulative token usage instead.
    /// </param>
    /// <param name="userProfile">The user-profile root that holds <c>.claude.json</c>.</param>
    /// <param name="fallback">The window returned (with <c>Detected=false</c>) when nothing usable is found.</param>
    /// <returns>
    /// A <see cref="WindowResolution"/>: <c>Detected=true</c> with the matched window and model key on success;
    /// <c>WindowResolution(fallback, false, null)</c> when detection is not possible.
    /// </returns>
    /// <remarks>
    /// When <c>.claude.json</c> yields no per-project detection (a FRESH session has no <c>lastModelUsage</c>
    /// yet — it is checkpoint-written), the fallback path consults a secondary signal before assuming the
    /// standard window: the top-level <c>"model"</c> string in <c>&lt;userProfile&gt;\.claude\settings.json</c>
    /// (the user's SELECTED model, e.g. <c>opus[1m]</c>, which carries the <c>[1m]</c> marker even before any
    /// usage is recorded).
    /// </para>
    /// <para>
    /// Precedence is by STRENGTH OF EVIDENCE, not by source: (1) a cwd key spelling whose
    /// <c>lastModelUsage</c> actually records the transcript's model; (2) the explicitly selected model in
    /// <c>settings.json</c>; (3) the highest-cumulative-usage key in the first usable map. (2) outranks (3)
    /// because <c>lastModelUsage</c> is cumulative history for a directory whereas <c>settings.json</c> states
    /// what this session runs — a guess must never pre-empt a stated fact.
    /// </remarks>
    public static WindowResolution Resolve(string cwd, string? baseModel, string userProfile, long fallback)
    {
        WindowResolution FallBack()
        {
            // Secondary signal: the selected model in ~/.claude/settings.json fills the fresh-session gap
            // where .claude.json has no per-project usage recorded yet.
            if (!string.IsNullOrWhiteSpace(userProfile) && TryConfigLargeWindow(userProfile, out string configModel))
                return new WindowResolution(LargeWindow, Detected: true, Model: configModel);
            // Tertiary signal, added 2026-08-28: a known 5-series model family defaults to 1M with NOTHING
            // persisted anywhere (no .claude.json at all is exactly the "fresh session" case this whole
            // function exists for). Must be checked here too, not just in the main body below — a MISSING
            // .claude.json returns via this FallBack() before the main body ever runs.
            if (!string.IsNullOrWhiteSpace(baseModel) && IsDefaultLargeWindowFamily(baseModel))
                return new WindowResolution(LargeWindow, Detected: true, Model: baseModel);
            return new(fallback, Detected: false, Model: null);
        }

        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(userProfile))
            return FallBack();

        string path = Path.Combine(userProfile, ".claude.json");
        if (!File.Exists(path))
            return FallBack();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return FallBack();
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("projects", out JsonElement projects) ||
                projects.ValueKind != JsonValueKind.Object)
                return FallBack();

            string wantCwd = Normalize(cwd);

            // The same cwd appears under many key spellings, and their lastModelUsage maps DISAGREE — one can be
            // stale while another holds the live model. Taking the first usable map is therefore wrong: on
            // 2026-07-27 the lower-cased spelling of one working directory listed only Haiku + Sonnet 5, while
            // the actual session model was recorded as claude-opus-5[1m] under that same directory's mixed-case
            // spelling — so a 1M session was measured against a 200K window and 278,521 tokens rendered as
            // "100.0%" instead of ~28%.
            //
            // Two passes, authoritative first:
            //   1. a spelling whose map actually CONTAINS the transcript's model (exact or [1m]) — real evidence;
            //   2. only if none does, the legacy highest-cumulative-usage guess from the first usable map.
            // Ordering by evidence rather than by key order makes the result independent of .claude.json's
            // arbitrary property order, which is not a contract we control.
            if (!string.IsNullOrWhiteSpace(baseModel))
            {
                foreach (JsonProperty project in projects.EnumerateObject())
                {
                    if (!TryGetUsage(project, wantCwd, out JsonElement usage))
                        continue;

                    if (TryChooseModel(usage, baseModel, requireBaseModel: true, out string modelKey, out long window))
                        return new WindowResolution(window, Detected: true, Model: modelKey);
                }
            }

            // 2. The user's EXPLICIT selection outranks a usage-ranked guess. lastModelUsage is cumulative
            //    HISTORY for a directory; settings.json "model" is what this session was actually started on.
            //    Observed live 2026-07-27: no spelling recorded claude-opus-5 at all (the model was new and
            //    lastModelUsage is checkpoint-written), while settings.json already said "opus[1m]". The stale
            //    map still answered first with sonnet-5 ⇒ 200K, so this signal was never consulted and a 1M
            //    session read 100% full. A guess must never pre-empt a stated fact.
            if (TryConfigLargeWindow(userProfile, out string selectedModel))
                return new WindowResolution(LargeWindow, Detected: true, Model: selectedModel);

            // 2.5. A known model FAMILY whose current default in THIS harness is already the 1M window —
            //    added 2026-08-28. The 5-series generation (opus-5, sonnet-5, fable-5) ships WITHOUT the
            //    historical "[1m]" beta-suffix convention tiers 1-2 look for: Claude Code grants these models
            //    1M context by default, with nothing persisted anywhere on disk to say so. Observed live
            //    the same day: settings.json's "model" was the bare string "sonnet" (no bracket at
            //    all) and every spelling's lastModelUsage was empty (checkpoint not yet written), so tiers
            //    1-2 both had zero evidence, fell through to the 200K blind fallback, and a live 1M session
            //    (409,308 tokens) rendered as 100.0% against 200,000 — see ContextReading.WindowSourceSuffix's
            //    "USED EXCEEDS WINDOW" guard, which was firing on exactly this. A model-family match is a
            //    real fact about the harness's current defaults, just not THIS session's own recorded state,
            //    so it outranks tier 3's blind highest-usage guess but not an explicit stated fact (1-2).
            //    Keep DefaultLargeWindowFamilies in sync with the harness's actual defaults as new
            //    generations ship — a model that falls off this list simply degrades to the older tiers
            //    rather than lying.
            if (!string.IsNullOrWhiteSpace(baseModel) && IsDefaultLargeWindowFamily(baseModel))
                return new WindowResolution(LargeWindow, Detected: true, Model: baseModel);

            // 3. Weakest evidence: the highest-cumulative-usage key in the first usable map for this cwd.
            foreach (JsonProperty project in projects.EnumerateObject())
            {
                if (!TryGetUsage(project, wantCwd, out JsonElement usage))
                    continue;

                if (TryChooseModel(usage, baseModel, requireBaseModel: false, out string modelKey, out long window))
                    return new WindowResolution(window, Detected: true, Model: modelKey);
            }

            return new WindowResolution(fallback, Detected: false, Model: null);
        }
    }

    /// <summary>
    /// Convenience overload: resolves against the real <c>%USERPROFILE%</c>
    /// (<see cref="Environment.SpecialFolder.UserProfile"/>) with the standard 200,000-token fallback.
    /// </summary>
    /// <param name="cwd">The absolute working directory the <c>claude</c> session runs in.</param>
    /// <param name="baseModel">The transcript's current bare model id, or null/blank to pick by usage.</param>
    /// <returns>The resolved window (detected) or the standard 200K fallback.</returns>
    public static WindowResolution Resolve(string cwd, string? baseModel)
        => Resolve(cwd, baseModel, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StandardWindow);

    /// <summary>
    /// Returns true when <paramref name="project"/> is a spelling of <paramref name="wantCwd"/> carrying a
    /// usable (object-shaped) <c>lastModelUsage</c>, yielding it in <paramref name="usage"/>.
    /// </summary>
    private static bool TryGetUsage(JsonProperty project, string wantCwd, out JsonElement usage)
    {
        usage = default;

        if (!string.Equals(Normalize(project.Name), wantCwd, StringComparison.Ordinal))
            return false;

        if (project.Value.ValueKind != JsonValueKind.Object ||
            !project.Value.TryGetProperty("lastModelUsage", out usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return false;

        return true;
    }

    /// <summary>
    /// Chooses the active model key inside a non-empty <c>lastModelUsage</c> object and maps it to a window.
    /// When <paramref name="baseModel"/> is supplied, prefers the <c>&lt;baseModel&gt;[1m]</c> key (⇒ 1M) then
    /// the exact <c>&lt;baseModel&gt;</c> key (⇒ 200K), case-insensitively; otherwise (or when neither key is
    /// present) picks the key with the highest cumulative token usage and detects by its <c>[1m]</c> suffix.
    /// </summary>
    /// <param name="requireBaseModel">
    /// When true, succeeds ONLY on a real <paramref name="baseModel"/> match and never falls back to the
    /// usage-ranked guess. This is what lets <see cref="Resolve"/> prefer a key spelling that actually records
    /// the live model over one that merely happens to be listed first (see the two-pass note in Resolve).
    /// </param>
    private static bool TryChooseModel(JsonElement usage, string? baseModel, bool requireBaseModel, out string modelKey, out long window)
    {
        modelKey = string.Empty;
        window = StandardWindow;

        if (!string.IsNullOrWhiteSpace(baseModel))
        {
            string large = baseModel + "[1m]";
            foreach (JsonProperty model in usage.EnumerateObject())
            {
                if (string.Equals(model.Name, large, StringComparison.OrdinalIgnoreCase))
                {
                    modelKey = model.Name;
                    window = LargeWindow;
                    return true;
                }
            }
            foreach (JsonProperty model in usage.EnumerateObject())
            {
                if (string.Equals(model.Name, baseModel, StringComparison.OrdinalIgnoreCase))
                {
                    modelKey = model.Name;
                    window = StandardWindow;
                    return true;
                }
            }
        }

        // Authoritative pass: this map does not record the live model, so it is not evidence about the window.
        // Report no-match and let Resolve try the next key spelling rather than guessing from unrelated models.
        if (requireBaseModel)
            return false;

        // No baseModel, or its keys are absent: pick the highest-usage model and detect by suffix.
        string? bestKey = null;
        long bestTokens = -1;
        foreach (JsonProperty model in usage.EnumerateObject())
        {
            long tokens = SumTokens(model.Value);
            if (tokens > bestTokens)
            {
                bestTokens = tokens;
                bestKey = model.Name;
            }
        }

        if (bestKey is null)
            return false;

        modelKey = bestKey;
        window = IsLarge(bestKey) ? LargeWindow : StandardWindow;
        return true;
    }

    /// <summary>
    /// Reads the user's SELECTED model from <c>&lt;userProfile&gt;\.claude\settings.json</c> (top-level
    /// <c>"model"</c> string) and reports whether it denotes the 1M-context beta (contains <c>[1m]</c>,
    /// case-insensitive). Never throws — a missing file, parse failure, or shape surprise returns false.
    /// </summary>
    /// <param name="userProfile">The user-profile root that holds <c>.claude\settings.json</c>.</param>
    /// <param name="model">The selected model string (e.g. <c>opus[1m]</c>) when this returns true.</param>
    /// <returns>True only when a top-level <c>model</c> string exists and contains <c>[1m]</c>.</returns>
    private static bool TryConfigLargeWindow(string userProfile, out string model)
    {
        model = string.Empty;

        string path = Path.Combine(userProfile, ".claude", "settings.json");
        if (!File.Exists(path))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("model", out JsonElement modelEl) ||
                modelEl.ValueKind != JsonValueKind.String)
                return false;

            string? selected = modelEl.GetString();
            if (string.IsNullOrWhiteSpace(selected) ||
                !selected.Contains("[1m]", StringComparison.OrdinalIgnoreCase))
                return false;

            model = selected;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when a model key denotes the 1M-context beta (ends with <c>[1m]</c>, case-insensitive).</summary>
    private static bool IsLarge(string modelKey)
        => modelKey.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Model-id prefixes that default to the 1M-token context window in THIS harness as of 2026-08, with no
    /// separate <c>[1m]</c> opt-in marker recorded anywhere on disk (unlike the 4.x generation, where 1M is a
    /// beta the user selects and Claude Code DOES persist a suffix for). Update this list when a new model
    /// generation changes its default window; a model not listed here simply falls through to the older
    /// bracket-suffix / usage-ranked detection tiers, so a stale list degrades gracefully instead of lying.
    /// </summary>
    private static readonly string[] DefaultLargeWindowFamilies =
    {
        "claude-opus-5", "claude-sonnet-5", "claude-fable-5",
    };

    /// <summary>True when <paramref name="baseModel"/> starts with a known 1M-by-default model family.</summary>
    private static bool IsDefaultLargeWindowFamily(string baseModel)
    {
        foreach (string family in DefaultLargeWindowFamilies)
        {
            if (baseModel.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Tier 0 — GROUND TRUTH, checked before any model name is even consulted. Reads a per-session window
    /// cached by a statusline integration that receives Claude Code's own <c>context_window.context_window_size</c>
    /// on stdin every prompt. Added 2026-08-28: this is what makes detection genuinely future-proof — a
    /// brand-new model needs no code change here at all, as long as one statusline render has happened for
    /// this session. Never throws; any missing file, parse failure, or non-positive value simply yields no
    /// result so the caller falls through to the older tiers. Writer contract: <c>INTEGRATION.md</c> §1.
    /// </summary>
    /// <param name="cacheDir">The directory holding <c>&lt;sessionId&gt;.native-window.json</c> files.</param>
    /// <param name="sessionId">The transcript's session id (file stem).</param>
    public static long? TryReadNativeWindowCache(string cacheDir, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(cacheDir) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        string path = Path.Combine(cacheDir, $"{sessionId}.native-window.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("window", out JsonElement windowEl) &&
                windowEl.ValueKind == JsonValueKind.Number &&
                windowEl.TryGetInt64(out long window) &&
                window > 0)
            {
                return window;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through to null — a corrupt/partial cache write must never crash a reading.
        }
        return null;
    }

    /// <summary>Sums a model's cumulative footprint: <c>inputTokens + cacheReadInputTokens + cacheCreationInputTokens</c>.</summary>
    private static long SumTokens(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) return 0;
        return ReadLong(model, "inputTokens")
             + ReadLong(model, "cacheReadInputTokens")
             + ReadLong(model, "cacheCreationInputTokens");
    }

    private static long ReadLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
            return 0;
        return el.TryGetInt64(out long v) ? v : (long)el.GetDouble();
    }

    /// <summary>
    /// Normalizes a path key for tolerant comparison: lowercase, <c>\</c> ⇒ <c>/</c>, collapse repeated
    /// slashes, and trim any trailing slash. This lets <c>C:\Dev\MyRepo</c>, <c>C:/dev/myrepo</c> and
    /// <c>C:/Dev/MyRepo//</c> all compare equal.
    /// </summary>
    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var sb = new StringBuilder(path.Length);
        bool lastSlash = false;
        foreach (char c in path)
        {
            char lc = char.ToLowerInvariant(c is '\\' ? '/' : c);
            if (lc == '/')
            {
                if (lastSlash) continue; // collapse repeats
                lastSlash = true;
            }
            else
            {
                lastSlash = false;
            }
            sb.Append(lc);
        }
        int end = sb.Length;
        while (end > 0 && sb[end - 1] == '/') end--; // trim trailing slash(es)
        return sb.ToString(0, end);
    }
}

/// <summary>
/// The outcome of <see cref="ContextWindowResolver.Resolve(string, string?, string, long)"/>: the chosen
/// context-window denominator, whether it was detected from Claude Code's recorded state, and which model key
/// it was matched to.
/// </summary>
/// <param name="Window">The context-window denominator in tokens (200,000, 1,000,000, or the fallback).</param>
/// <param name="Detected">
/// True when <paramref name="Window"/> came from <c>.claude.json</c>; false when it is the assumed standard
/// fallback (no usable per-project model state was found).
/// </param>
/// <param name="Model">The matched model key (e.g. <c>claude-opus-4-8[1m]</c>), or null when not detected.</param>
public sealed record WindowResolution(long Window, bool Detected, string? Model);
