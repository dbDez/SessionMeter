namespace SessionMeter.Core.Usage;

/// <summary>
/// The full programmatic usage reading from the OAuth endpoint — the two legacy top-level windows, the
/// FULL named-window list (<see cref="Limits"/>: includes PER-MODEL weekly walls such as Fable), plus the
/// raw JSON body.
/// <para>
/// TWO distinct binding notions (the display/control decoupling): a FLEET-wide usage-wall decision consumes
/// <see cref="ToReading"/>, which binds ONLY on the ALL-MODELS windows (<see cref="FleetWindows"/> — 5-hour
/// session + 7-day weekly-all); a per-model wall is DELIBERATELY EXCLUDED so it can never checkpoint a
/// fleet of Opus workers. The DISPLAY surfaces (<c>session usage</c>) instead print EVERY window and use
/// <see cref="BindingLimit"/>/<see cref="BindingLabel"/> (which DO consider ENFORCED per-model walls) so the
/// operator still SEES the full picture. Display ≠ control. The RETIRED Fable-scoped wall
/// (<see cref="UsageLimit.IsFableScoped"/> — Anthropic removed its enforcement 2026-07-24) is excluded from
/// BOTH binding notions: it is printed as an informational line only and can never bind or read ACTIVE.
/// </para>
/// </summary>
/// <param name="FiveHour">The rolling 5-hour session window, or null if absent/unparsed.</param>
/// <param name="SevenDay">The all-models 7-day window, or null if absent/unparsed.</param>
/// <param name="Raw">The raw JSON body the snapshot was parsed from (for diagnostics).</param>
/// <param name="Limits">
/// The endpoint's full <c>limits[]</c> array parsed dynamically (session + weekly-all + per-model weekly
/// walls). Null/empty for older bodies or hand-built snapshots — in that case the binding calculation
/// falls back to synthesising from <see cref="FiveHour"/>/<see cref="SevenDay"/> (back-compat).
/// </param>
public sealed record UsageSnapshot(
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    string Raw,
    IReadOnlyList<UsageLimit>? Limits = null)
{
    /// <summary>The parsed per-window limits (never null — empty when the body carried no <c>limits[]</c> array).</summary>
    public IReadOnlyList<UsageLimit> ModelLimits => Limits ?? Array.Empty<UsageLimit>();

    /// <summary>
    /// EVERY window considered for the wall: the endpoint's <c>limits[]</c> when present (session +
    /// weekly-all + per-model), else a two-window fallback synthesised from <see cref="FiveHour"/>/
    /// <see cref="SevenDay"/> (older bodies / hand-built snapshots). The top-level pair duplicates the first
    /// two limits when both are present, so preferring <see cref="Limits"/> avoids double-counting.
    /// </summary>
    public IReadOnlyList<UsageLimit> AllWindows
    {
        get
        {
            if (Limits is { Count: > 0 }) return Limits;
            var list = new List<UsageLimit>(2);
            if (FiveHour is not null)
                list.Add(new UsageLimit("session", "session", FiveHour.Percent, FiveHour.ResetsAt, null, "normal", false));
            if (SevenDay is not null)
                list.Add(new UsageLimit("weekly_all", "weekly", SevenDay.Percent, SevenDay.ResetsAt, null, "normal", false));
            return list;
        }
    }

    /// <summary>
    /// The binding window as a named limit — the one with the HIGHEST <see cref="UsageLimit.Percent"/> across
    /// the ENFORCED windows of <see cref="AllWindows"/> (an enforced per-model wall at 100% therefore binds
    /// even when the all-models windows read low). The retired Fable-scoped wall
    /// (<see cref="UsageLimit.IsFableScoped"/>) is EXCLUDED — Anthropic no longer enforces it (2026-07-24),
    /// so a fable bucket in the payload must never be reported as the binding constraint. On a tie the
    /// earlier window wins (5-hour before weekly, matching the historical collapse). Null when no enforced
    /// window data is present.
    /// </summary>
    public UsageLimit? BindingLimit
    {
        get
        {
            UsageLimit? best = null;
            foreach (UsageLimit w in AllWindows)
                if (w.IsEnforced && (best is null || w.Percent > best.Percent))
                    best = w;
            return best;
        }
    }

    /// <summary>The binding window collapsed to a plain <see cref="WindowUsage"/> (percent + reset), or null.</summary>
    public WindowUsage? Binding
    {
        get
        {
            UsageLimit? b = BindingLimit;
            return b is null ? null : new WindowUsage(b.Percent, b.ResetsAt);
        }
    }

    /// <summary>The binding window's display label (e.g. "7-day · Fable"), or null when no window is present.</summary>
    public string? BindingLabel => BindingLimit?.Label;

    // ── Fleet binding (the CONTROL path, decoupled from per-model display) ──────────────────────────────

    /// <summary>
    /// The FLEET-facing windows: the ALL-MODELS constraints only (<c>session</c> 5-hour + <c>weekly_all</c>
    /// 7-day) — every per-model wall (<see cref="UsageLimit.IsPerModel"/>) is EXCLUDED. This is the set a
    /// fleet-wide usage-wall decision binds on, so a per-model Fable weekly at 100% can never stop a fleet of
    /// Opus workers. (In the <see cref="Limits"/>-absent fallback every synthesised window is already
    /// all-models, so this equals <see cref="AllWindows"/> there — back-compat preserved.)
    /// </summary>
    public IReadOnlyList<UsageLimit> FleetWindows =>
        AllWindows.Where(w => !w.IsPerModel).ToList();

    /// <summary>
    /// The binding ALL-MODELS window — the one with the HIGHEST <see cref="UsageLimit.Percent"/> across
    /// <see cref="FleetWindows"/> (per-model walls excluded). Drives <see cref="ToReading"/>. Null when no
    /// all-models window is present.
    /// </summary>
    public UsageLimit? FleetBindingLimit
    {
        get
        {
            UsageLimit? best = null;
            foreach (UsageLimit w in FleetWindows)
                if (best is null || w.Percent > best.Percent)
                    best = w;
            return best;
        }
    }

    /// <summary>The fleet binding window collapsed to a plain <see cref="WindowUsage"/> (percent + reset), or null.</summary>
    public WindowUsage? FleetBinding
    {
        get
        {
            UsageLimit? b = FleetBindingLimit;
            return b is null ? null : new WindowUsage(b.Percent, b.ResetsAt);
        }
    }

    /// <summary>
    /// The critical, currently-active ENFORCED per-model walls (<see cref="UsageLimit.IsPerModel"/> AND
    /// <see cref="UsageLimit.IsActive"/> AND severity <c>critical</c>) — the seam where a supervisor could
    /// react to one of these (advisory only). The retired Fable-scoped wall is excluded even when the
    /// endpoint still flags it active/critical: it is not an enforceable constraint and must never surface
    /// as one. Empty when none is active.
    /// </summary>
    public IReadOnlyList<UsageLimit> ActivePerModelWalls =>
        AllWindows.Where(w => w.IsPerModel && w.IsEnforced && w.IsActive
            && string.Equals(w.Severity, "critical", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Derives the FLEET <see cref="UsageReading"/> from the <see cref="FleetBinding"/> window (all-models
    /// only — per-model walls excluded, so a Fable weekly never triggers a fleet checkpoint). Returns
    /// <c>(null, null, raw)</c> when no all-models window is present so the caller treats it as a
    /// skipped/failed tick.
    /// </summary>
    public UsageReading ToReading()
    {
        WindowUsage? b = FleetBinding;
        return b is null
            ? new UsageReading(null, null, Raw)
            : new UsageReading(b.Percent, b.ResetsAt, Raw);
    }
}
