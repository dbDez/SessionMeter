# warn-usage.ps1 — Claude Code UserPromptSubmit hook
# Watches the rate walls via SessionMeter's `session usage --raw`. The walls are NOT equal:
#   * 5-hour session  — critical: truncates the CURRENT run. nudge 75% / act 85%.
#   * 7-day window    — recovers slowly. nudge 90% / warn 95%.
#   * per-model (scoped) wall — a cheaper/faster model tier's own weekly cap. At ~90% it's a
#       MODEL-SWITCH signal: dispatch that tier's agents on Opus 4.8, because a dispatch on an
#       exhausted tier fails instantly (0 tool-uses, clobbered message). NOT a stop-work signal.
#       EXCEPTION: the Fable tier's wall was retired by Anthropic (2026-07) — the endpoint may
#       still return a fable-scoped bucket, but it is informational only and is skipped below.
# Requires `session` on PATH + a Claude subscription login (usage needs OAuth). Fail-open.

$ErrorActionPreference = 'SilentlyContinue'
try {
    $raw = (session usage --raw 2>$null | Out-String)
    $i = $raw.IndexOf('{'); if ($i -lt 0) { exit 0 }
    $u = $raw.Substring($i) | ConvertFrom-Json

    $msgs = @()
    $five  = [int]$u.five_hour.utilization
    $seven = [int]$u.seven_day.utilization

    if     ($five  -ge 85) { $msgs += "5-hour wall at $five% — finish the in-flight step, write HandOff.md, commit + push NOW, then start a fresh session. Don't launch new work." }
    elseif ($five  -ge 75) { $msgs += "5-hour wall at $five% — plan the hand-off; avoid launching long agents." }

    if     ($seven -ge 95) { $msgs += "7-day wall at $seven% — checkpoint (HandOff.md + commit) and pace remaining runs." }
    elseif ($seven -ge 90) { $msgs += "7-day wall at $seven% — approaching the weekly cap." }

    # Per-model (scoped) walls live in limits[] as kind 'weekly_scoped'. Fable-scoped buckets are
    # skipped: that wall no longer exists (informational only) and must never trigger a model switch.
    foreach ($lim in @($u.limits | Where-Object { $_.kind -eq 'weekly_scoped' -and $_.scope.model.display_name -notmatch 'fable' })) {
        if ([int]$lim.percent -ge 90) {
            $model = $lim.scope.model.display_name
            $msgs += "The '$model' model tier is at $($lim.percent)% of its weekly wall — dispatch any agents on the '$model' tier with Opus 4.8 (model: opus) instead. A dispatch on an exhausted tier fails instantly. This is a model-switch, not a stop."
        }
    }

    if ($msgs.Count) {
        @{ hookSpecificOutput = @{ hookEventName = 'UserPromptSubmit'; additionalContext = ($msgs -join "`n") } } |
            ConvertTo-Json -Depth 4 -Compress
    }
} catch { }
exit 0
