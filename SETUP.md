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

### B. A UserPromptSubmit hook (optional — nudge near a wall)

Any hook can shell out to `session`. A minimal PowerShell hook that warns when the context
window passes 80% (measured, so the model never has to guess):

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

Register it in `settings.json`:

```jsonc
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command",
        "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/path/to/warn-context.ps1",
        "timeout": 10 } ] }
    ]
  }
}
```

Use `session usage` the same way to watch the **5-hour / 7-day** rate walls that truncate
long runs. Both commands are safe to call frequently — `context` just reads a local file;
`usage` reads your existing OAuth window.

> **Tip:** on a long-running or headless loop, poll `session context --cwd <repo>` per
> iteration to checkpoint before the context wall, and `session usage` to stop before a
> rate wall resets.

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
