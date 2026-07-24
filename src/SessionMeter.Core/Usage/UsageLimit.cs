namespace SessionMeter.Core.Usage;

/// <summary>
/// One named rate-limit window from the OAuth usage endpoint's <c>limits[]</c> array. This is the GENERAL
/// shape behind the two legacy top-level windows (<c>five_hour</c>/<c>seven_day</c>): the array also carries
/// PER-MODEL weekly walls (<c>weekly_scoped</c> entries with a <c>scope.model</c>) that the top-level
/// fields never expose. Every entry is parsed dynamically so the binding calculation cannot sail past an
/// enforced per-model wall that is exhausted while the all-models windows still read low. Exception: the
/// RETIRED Fable-scoped wall (<see cref="IsFableScoped"/>) is parsed and displayed but never enforced.
/// </summary>
/// <param name="Kind">The endpoint's window kind (e.g. <c>session</c>, <c>weekly_all</c>, <c>weekly_scoped</c>).</param>
/// <param name="Group">The window group (e.g. <c>session</c>, <c>weekly</c>).</param>
/// <param name="Percent">Utilisation as a whole-number percentage (0–100), read VERBATIM (no fraction heuristic).</param>
/// <param name="ResetsAt">When this window resets, or null if the endpoint omitted it.</param>
/// <param name="ModelName">The scoped model's display name (e.g. "Fable") for a per-model window, else null.</param>
/// <param name="Severity">The endpoint's severity for this window (e.g. <c>normal</c>, <c>critical</c>).</param>
/// <param name="IsActive">Whether the endpoint flags this window as the currently-binding constraint.</param>
public sealed record UsageLimit(
    string Kind,
    string Group,
    int Percent,
    DateTimeOffset? ResetsAt,
    string? ModelName,
    string Severity,
    bool IsActive)
{
    /// <summary>
    /// A human-readable label for CLI/GUI display: the group label, suffixed with the model name for a
    /// per-model window (e.g. "7-day · Fable"). Never used for identity — display only.
    /// </summary>
    public string Label =>
        ModelName is { Length: > 0 } m ? $"{GroupLabel} · {m}" : GroupLabel;

    /// <summary>
    /// True when this window is scoped to a SINGLE model (a per-model weekly wall) — its
    /// <see cref="ModelName"/> is populated (the endpoint's <c>weekly_scoped</c> + <c>scope.model</c> shape).
    /// Such a wall constrains only the workers actually running that model; it must NEVER drive a FLEET-wide
    /// checkpoint (that is the job of the all-models windows — <c>session</c> + <c>weekly_all</c>). This is the
    /// predicate the fleet-binding split (<see cref="UsageSnapshot.FleetWindows"/>) filters on.
    /// </summary>
    public bool IsPerModel => ModelName is { Length: > 0 };

    /// <summary>
    /// True when this window is the RETIRED Fable-scoped per-model wall. Anthropic removed the Fable
    /// per-model rate wall (2026-07-24) — the endpoint may still RETURN a fable-scoped bucket, but it is no
    /// longer an enforced constraint. Matched on <see cref="ModelName"/> containing "fable"
    /// (case-insensitive) so display-name variants ("Fable", "Fable 5") all qualify. Scoped to fable ONLY:
    /// any other per-model wall the endpoint may add keeps full enforced semantics.
    /// </summary>
    public bool IsFableScoped =>
        ModelName is { Length: > 0 } m && m.Contains("fable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this window is a REAL, enforced constraint that may participate in the binding-window
    /// computation (<see cref="UsageSnapshot.BindingLimit"/>). Everything except the retired Fable-scoped
    /// wall (<see cref="IsFableScoped"/>) is enforced — a fable bucket is informational display-only and
    /// must never bind or be surfaced as ACTIVE.
    /// </summary>
    public bool IsEnforced => !IsFableScoped;

    private string GroupLabel => Kind switch
    {
        "session" => "5-hour session",
        _ when string.Equals(Group, "weekly", StringComparison.OrdinalIgnoreCase) => "7-day",
        _ when !string.IsNullOrWhiteSpace(Kind) => Kind,
        _ => "window",
    };
}
