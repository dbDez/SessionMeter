# warn-context.ps1 — Claude Code UserPromptSubmit hook
# Nudges as the context window fills, using SessionMeter's keyless `session context`.
# Register it in settings.json (see settings.snippet.jsonc). Requires `session` on PATH.
# Fail-open: any error -> exit 0 with no output, so it can never block your prompt.

$ErrorActionPreference = 'SilentlyContinue'
try {
    $line = session context 2>$null
    if ($line -match '(\d+(?:\.\d+)?)%') {
        $pct = [double]$Matches[1]
        $msg = $null
        if ($pct -ge 60) {
            $msg = "Context at $pct% — finish the in-flight step, write/update HandOff.md " +
                   "(Decisions / What was done / Lessons / Outstanding / First action next time), " +
                   "commit + push, then start a fresh session and 'carry on'."
        } elseif ($pct -ge 50) {
            $msg = "Context at $pct% — plan to hand off soon: keep HandOff.md current so a fresh session can 'carry on' cheaply."
        }
        if ($msg) {
            @{ hookSpecificOutput = @{ hookEventName = 'UserPromptSubmit'; additionalContext = $msg } } |
                ConvertTo-Json -Depth 4 -Compress
        }
    }
} catch { }
exit 0
