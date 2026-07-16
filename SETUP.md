# SessionMeter — Setup, Build & Wiring Guide

`Session.exe` (command: `session`) is a tiny, keyless CLI that reports your **exact
in-session context %** for Claude Code, Codex, and Pi, plus your **live rate-limit windows** for
Claude Code — measured from the real session transcript, never estimated.

- `session context` — needs **no login**. Reads the local Claude Code session transcript.
- `session context --pi` — needs **no login**. Reads the local Pi session transcript and Pi model registry.
- `session context --codex` — needs **no login**. Reads the local Codex rollout transcript.
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

### D. Hand-off & carry-on — never lose a session to a wall

The hooks *warn* you; this pattern lets you actually *survive* a wall or a full context window
without losing decisions, lessons, or your place. It's a two-file idea: a **hand-off file** the
agent writes before the wall, and a **carry-on** trigger a fresh session uses to resume.

**1. The hand-off file (`HandOff.md`).** When a wall or context limit approaches, have the agent
write a `HandOff.md` in the working directory capturing everything a clean session needs:

```markdown
# HandOff — <project> — <date>
## Decisions made        (choices locked in this session)
## What was done         (with file paths / commit hashes)
## Lessons learned        (gotchas worth keeping — the durable notes)
## Outstanding            (next actions, in priority order, with paths)
## First action next time (the single thing to do first)
```

Keep it at the working-dir root and **commit it** so it travels across machines and survives a
context clear. This file — not the chat scrollback — is your memory; scrollback is gone after
you clear.

**2. Tell the agent to write it automatically.** Add a standing rule to your `CLAUDE.md`
(user-level or project):

> When `session context` reaches ~60%, or `session usage` reports the 5-hour wall ≥85%: finish
> only the in-flight step, then write/update `HandOff.md` (decisions, lessons, outstanding, first
> next action), commit and push it, and tell me to start a fresh session. Don't start new work.

The section-C / context hooks already inject that reminder at the thresholds — this rule tells the
agent what to *do* with it.

**3. Save lessons safely.** Treat the `## Lessons learned` block as durable: anything you learned
that you'd hate to rediscover goes there (or into a committed `notes/` folder). Because it's in a
committed file, it outlives the session, the context clear, and a move to another machine.

**4. The carry-on trigger.** Give a fresh session a one-word resume. Add to your `CLAUDE.md`:

> When I say **carry on**, read `HandOff.md` in the working directory, summarise in one line where
> we left off, then resume from the first outstanding action.

(You can also make this a Claude Code skill or slash command so it's discoverable in the menu.)

**5. What you (the user) type at the wall.** The loop is:

1. The agent hits the threshold, writes `HandOff.md`, commits + pushes, and says:
   *"Hand-off written — start a new session and type `carry on`."*
2. You **clear the context** — run `/clear` (or just open a new session).
3. You type **`carry on`**.
4. The fresh session reads `HandOff.md`, tells you where you were, and continues — no re-reading
   the whole history, no lost lessons.

**Why clear instead of continuing?** A `/clear`-ed session re-reads only the compact `HandOff.md`,
so it starts cheap and accurate. Continuing a near-full session keeps paying for the bloated
context on every turn — and risks the very wall you're trying to dodge.

> **Ready-made copies** of both hooks, a `carry-on` skill, the `HandOff.md` template, and a
> `CLAUDE.md` snippet live in [`examples/`](examples/) — copy them in and edit the paths.

---

## 3. Wire it into Pi

Pi has no Claude-style hooks, but the command works directly from any shell and can be used by a Pi extension
or footer/status integration:

```powershell
session context --pi --cwd C:\Users\pieters
```

It reads `%USERPROFILE%\.pi\agent\sessions\` for the newest matching session header and uses
`%USERPROFILE%\.pi\agent\models.json` to resolve the active provider/model context window. For PAV GPT-5.6
Terra/Sol/Luna, GPT-5.5, and GPT-5.4 this currently resolves to `900000` when Pi's model registry is configured
that way.

---

## 4. Use it with Codex

Codex records a `token_count` event in each rollout transcript, including the exact current input-token footprint
and context-window size. Measure the active Codex session for a directory with:

```powershell
session context --codex --cwd C:\PKM
```

To inspect a particular Codex session instead of the newest matching transcript, add `--session <id>`.
SessionMeter only reads `%USERPROFILE%\.codex\sessions\`; it never changes Codex configuration or transcripts.

---

## 5. Build the installer from source

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

## 6. How the PATH entry works

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

## 7. How the app icon is set

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

## 8. Uninstall

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
