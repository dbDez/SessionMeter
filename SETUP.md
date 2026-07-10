# SessionMeter — Setup, Build & Wiring Guide

`Session.exe` (command: `session`) is a tiny, keyless CLI that reports your **exact
in-session context %** and your **live rate-limit windows** for Claude Code — measured
from the real session transcript, never estimated.

- `session context` — needs **no login**. Reads the local Claude Code session transcript.
- `session usage` — needs a **Claude subscription (Pro/Max)** login (OAuth); an API key can't read the windows.

This document covers: installing, wiring it into a Claude Code session, building the
installer from source, how the PATH entry works, and how the app icon is set.

---

## 1. Install (end users)

1. Run **`SessionMeter-Setup-<version>.exe`** (from `dist/installer/`).
   It installs **per-user** — no admin prompt — to
   `%LOCALAPPDATA%\Programs\SessionMeter\` and **adds that folder to your PATH**.
2. Open a **fresh terminal** so the new PATH is picked up.
3. Run `session context`.

```
> session context
sessionmeter: 24.8% (247,834 / 1,000,000 tokens) — session ab12cd34, as of 2026-07-04T21:14:07+02:00 · 1M window (detected)
```

It's a **self-contained single file** — no .NET runtime to install. Uninstalling removes
the PATH entry again.

---

## 2. Wire it into a Claude Code session

Claude Code only shows context fill inside the TUI (`/context`). SessionMeter surfaces it
to anything that can run a command. Two common ways to wire it in:

### A. Status line (recommended — always visible)

Add a `statusLine` to your Claude Code `settings.json`
(`~/.claude/settings.json`, or the project's `.claude/settings.json`):

```jsonc
{
  "statusLine": {
    "type": "command",
    "command": "pwsh -NoProfile -Command \"$c = session context 2>$null; if ($c) { ($c -split '—')[0].Trim() } else { 'session: n/a' }\""
  }
}
```

That prints e.g. `sessionmeter: 24.8% (247,834 / 1,000,000 tokens)` in the status bar,
refreshed by Claude Code as you work. Drop the `-split` if you want the full line.

### B. A context-budget hook (nudge before the context wall)

Any hook can shell out to `session`. A minimal `UserPromptSubmit` hook that warns when the
context window passes 80% (measured, so the model never has to guess):

```powershell
# warn-context.ps1  — UserPromptSubmit hook
$line = session context 2>$null
if ($line -match '(\d+(?:\.\d+)?)%') {
    $pct = [double]$Matches[1]
    if ($pct -ge 80) {
        @{ hookSpecificOutput = @{ hookEventName = 'UserPromptSubmit'
           additionalContext = "Context at $pct% — wrap up or start a fresh session soon." } } |
            ConvertTo-Json -Depth 4 -Compress
    }
}
exit 0
```

### C. A rate-wall hook (5-hour / 7-day / per-model)

`session usage` exposes three different walls — and they are **not equally urgent**:

| Wall | What it does | Suggested nudge / act |
|---|---|---|
| **5-hour session** | truncates the **current** run mid-task | nudge **75%** · act **85%** (finish + hand off) |
| **7-day window** | caps the week; recovers slowly | nudge **90%** · warn **95%** |
| **per-model (scoped)** | one model tier's own weekly cap (e.g. a cheaper/faster tier) | **switch that tier to Opus 4.8 at ~90%** (see below) |

`session usage --raw` prints the raw JSON, which a hook can parse for exact percentages. This
`UserPromptSubmit` hook applies per-wall thresholds and, when a scoped model wall is nearly
exhausted, tells the model to **switch that tier's agents to Opus 4.8**:

```powershell
# warn-usage.ps1  — UserPromptSubmit hook
$raw = (session usage --raw 2>$null | Out-String)
$i = $raw.IndexOf('{'); if ($i -lt 0) { exit 0 }
try { $u = $raw.Substring($i) | ConvertFrom-Json } catch { exit 0 }

$msgs = @()
$five  = [int]$u.five_hour.utilization
$seven = [int]$u.seven_day.utilization
if     ($five  -ge 85) { $msgs += "5-hour wall at $five% — finish the in-flight step, write a handoff, commit + push now." }
elseif ($five  -ge 75) { $msgs += "5-hour wall at $five% — plan the handoff; avoid launching long agents." }
if     ($seven -ge 95) { $msgs += "7-day wall at $seven% — checkpoint and pace remaining runs." }
elseif ($seven -ge 90) { $msgs += "7-day wall at $seven% — approaching the weekly cap." }

# Per-model (scoped) walls live in $u.limits[] as kind 'weekly_scoped'
foreach ($lim in @($u.limits | Where-Object { $_.kind -eq 'weekly_scoped' })) {
    if ([int]$lim.percent -ge 90) {
        $model = $lim.scope.model.display_name
        $msgs += "The '$model' weekly wall is at $($lim.percent)% — switch any agents dispatched on the '$model' tier to Opus 4.8 (model: opus). A dispatch on an exhausted tier fails instantly."
    }
}

if ($msgs.Count) {
    @{ hookSpecificOutput = @{ hookEventName = 'UserPromptSubmit'
       additionalContext = ($msgs -join "`n") } } | ConvertTo-Json -Depth 4 -Compress
}
exit 0
```

### Register the hooks

Point Claude Code at whichever hooks you want in `settings.json`
(`~/.claude/settings.json`, or a project's `.claude/settings.json`):

```jsonc
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [
        { "type": "command", "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/path/to/warn-context.ps1", "timeout": 10 },
        { "type": "command", "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/path/to/warn-usage.ps1",   "timeout": 10 }
      ] }
    ]
  }
}
```

### Auto-switch a cheaper model tier to Opus 4.8 at its wall

If your workflow dispatches sub-agents on a cheaper/faster model tier (Claude Code exposes
per-model weekly walls — a promo tier like **Fable** is a common example), that tier has its
**own** weekly cap, separate from the 5-hour and 7-day walls. When it's exhausted, a dispatch
on that tier **dies instantly** (zero tool-uses, a clobbered "out of credits" message that
looks like a crash).

So make it a standing rule for your agents: **when a model tier's weekly wall is near-full
(~90%), dispatch that tier's agents on Opus 4.8 instead** (pass `model: opus` on the agent
call). It's a *model-switch*, not a stop-work signal — the work continues, just on a tier that
still has headroom. The hook in section C surfaces exactly this the moment the scoped wall
climbs.

> **Tip:** on a long-running or headless loop, hooks only fire on prompt submit — so **poll
> `session usage` yourself between steps** too. Check `session context --cwd <repo>` to
> checkpoint before the context wall, and `session usage` to catch a rate wall (or a model-tier
> wall) *before* it bites.

---

## 3. Build the installer from source

### Prerequisites

| Tool | Why | Notes |
|---|---|---|
| **.NET 10 SDK** | compiles `Session.exe` | `dotnet --version` ≥ 10.0 |
| **Inno Setup 6.1+** | builds the installer | needs 6.1+ for the download page API; `ISCC.exe` auto-located |
| **ImageMagick** *(optional)* | only to regenerate `assets/session.ico` from the PNG | not needed for a normal build |

### Build

```powershell
pwsh -NoProfile -File .\build.ps1
```

Output: `dist/installer/SessionMeter-Setup-<version>.exe`.

### What `build.ps1` does

1. Locates `ISCC.exe` across the standard install locations.
2. Resolves the **canonical `dist/installer`** (the main checkout, even when run from a git
   worktree) so builds never scatter.
3. **Syncs the version** — reads `<Version>` from `src/Session/Session.csproj` (the single
   source of truth) and rewrites `#define MyAppVersion` in `SessionMeter.iss`.
4. Publishes **self-contained single-file** for `win-x64`
   (`dotnet publish … --self-contained true -p:PublishSingleFile=true`).
5. Compiles `SessionMeter.iss` with Inno Setup.

To cut a new release: bump `<Version>` in `src/Session/Session.csproj`, then run `build.ps1`.

---

## 4. How the PATH entry works

The installer adds itself to the **per-user** PATH (`HKCU\Environment`), so no admin rights
are needed. In `SessionMeter.iss`:

```ini
[Setup]
ChangesEnvironment=yes          ; broadcast WM_SETTINGCHANGE so Explorer sees the new PATH

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}'))
```

- `NeedsAddPath` (in the `[Code]` section) checks the install dir isn't already on PATH, so
  re-installing never duplicates the entry.
- `CurUninstallStepChanged` strips the entry back out on uninstall.
- **Already-open terminals keep the old PATH** — that's why step 2 above says open a fresh one.

Because the binary is `Session.exe`, Windows' `PATHEXT` resolves the bare command `session`
(case-insensitive) to it — so hooks and scripts can call `session` directly.

---

## 5. How the app icon is set

One `.ico` drives the exe, the installer, and Add/Remove Programs:

- **`assets/session.ico`** — a multi-resolution icon (256/128/64/48/32/16) built from
  `assets/session.png` with
  `magick assets/session.png -define icon:auto-resize=256,128,64,48,32,16 assets/session.ico`.
- **The exe** carries it via `<ApplicationIcon>..\..\assets\session.ico</ApplicationIcon>` in
  `src/Session/Session.csproj` — so `Session.exe` shows the icon in Explorer.
- **The installer** carries it via `SetupIconFile=assets\session.ico`, ships the `.ico`, and
  points `UninstallDisplayIcon` at the installed `Session.exe`.

To restyle: replace `assets/session.png`, regenerate `session.ico` with the ImageMagick line
above, and rebuild — the exe and installer both pick up the new icon.

---

## 6. Uninstall

Add/Remove Programs → **SessionMeter** → Uninstall. It removes the binary and strips the
install dir from your PATH.

---

## Caveats

`session context`'s **1M-vs-200K detection** and all of `session usage` read **undocumented,
unversioned** internal Claude Code state / endpoints that Anthropic may change without notice.
`session context`'s core reading depends only on the local transcript format and is the
sturdier of the two. SessionMeter only ever **reads** these — it never writes to them.

---

MIT licensed — free to use, fork, and ship. See `LICENSE`.
