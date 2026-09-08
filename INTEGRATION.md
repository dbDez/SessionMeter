# Integrating SessionMeter into an agent harness — without timeouts or wrong numbers

> **Read this before wiring `session` into a status line, a hook, or an agent's rule-set.**
> `SETUP.md` tells you *how to wire it in*. This file tells you *how to keep it correct* — the four
> failure modes that produce a silently wrong reading or no reading at all, and the contract that
> prevents each one.
>
> Every failure below exits 0 or prints nothing. **None of them announce themselves.** That is why
> they get a document.

---

## The one-paragraph version

A harness integration is correct when: **(1)** the harness feeds SessionMeter the real context-window
size it already knows, via the Tier-0 cache; **(2)** every hook that shells out to `session` has a
timeout of **at least 30 seconds**; **(3)** nothing calls `session` when the harness has already
handed the caller a number; and **(4)** the caller treats exit code 4 as "no measurement", not as a
failure. Get those four right and the reading is ground truth. Get any one wrong and it is a guess
wearing a decimal point.

---

## 1. Feed the ground-truth window — the Tier-0 cache

### The problem it solves

SessionMeter's numerator (tokens used) comes from the session transcript and is always right. The
**denominator** — the context-window size — is the hard part, because the transcript's model field
strips the `[1m]` beta marker. Historically the tool cross-referenced Claude Code's own per-project
model state for that marker.

**Model naming conventions change.** When Claude Code's 5-series models stopped persisting the
`[1m]` marker, the marker-based detection fell back to a 200,000-token assumption on a session whose
real window was 1,000,000 — producing readings like `100.0% (409,308 / 200,000) — USED EXCEEDS
WINDOW`. Any detection scheme built on model *names* has this defect latent in it; the next naming
change re-arms it.

### The contract

The harness already knows the true window — it hands it to the status line on every prompt. So do
not guess it: **remember it where both readers look.**

| Field | Value |
|---|---|
| **Location** | `%LOCALAPPDATA%\SessionMeter\context-cache\<sessionId>.native-window.json` |
| **Canonical definition** | `MeterConfig.NativeWindowCacheDir` (`src/SessionMeter.Core/Configuration/MeterConfig.cs`) |
| **Written by** | The harness status-line script, once per prompt |
| **Read by** | `ContextWindowResolver` Tier 0 — *before any model name is consulted* |
| **Shape** | `{"window":1000000,"model":"claude-opus-5","at":"2026-08-28T09:12:44.1234567+02:00"}` |

`window` is the only required field; `model` and `at` are diagnostic. A missing file, a malformed
body, a read failure, or a non-positive `window` yields no result and falls through to the older
tiers — the cache can never make a reading *worse* than it would have been.

### The writer (PowerShell status line, Claude Code)

Claude Code pipes a JSON snapshot to the status-line command on stdin. When it carries a window
size, persist it:

```powershell
# $json = the status-line payload parsed from stdin; $sessionId = its session_id
$window = 0
if ($json.context_window.context_window_size) { $window = [long]$json.context_window.context_window_size }
elseif ($json.context_window.total_tokens)    { $window = [long]$json.context_window.total_tokens }

if ($sessionId -and $window -gt 0) {
    try {
        $cacheDir = Join-Path $env:LOCALAPPDATA 'SessionMeter\context-cache'
        if (-not (Test-Path -LiteralPath $cacheDir)) {
            New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
        }
        [pscustomobject]@{ window = $window; model = $model; at = (Get-Date).ToString('o') } |
            ConvertTo-Json -Compress |
            Set-Content -LiteralPath (Join-Path $cacheDir "$sessionId.native-window.json") -Encoding utf8
    } catch { }
}
```

The `try/catch` is deliberate and is the one place a silent swallow is correct: a cache write that
fails must never break the status line, and the consequence of losing it is a fallback to the older
tiers, not a wrong number.

### Why this is the future-proof fix

There is **no model list to maintain anywhere**. A model neither the CLI nor the harness scripts have
ever heard of reads correctly on its first prompt, because the number is not inferred — it is
reported. Verified against a hypothetical 2M-window model with zero list entries.

### The full detection ladder

| Tier | Source | Notes |
|---|---|---|
| **0** | The native-window cache above | Ground truth. Checked before any model name. |
| **1–2** | The `[1m]` bracket marker in the harness's recorded per-project model state | Correct when present; absent on newer model generations. |
| **default-family** | A known large-window model family | Degrades gracefully as a list goes stale; outranks a blind usage-ranked guess, never outranks a stated fact. |
| **escalation net** | `used > window` is impossible | If a reading implies more tokens used than the window holds, the assumption was wrong — escalate rather than print an absurd percentage. |

Tiers 1 upward are **fallbacks, not the design.** If your integration relies on them, Tier 0 is not
wired up.

---

## 2. Give every hook at least 30 seconds

A hook that shells out to `session` starts a **fresh PowerShell process**. On a cold `pwsh`, module
autoload and JIT can consume most of ten seconds before the tool's own work begins.

**A hook that exceeds its timeout is cancelled silently.** No error surfaces in the transcript UI, no
partial output appears, and the reading simply never arrives — indistinguishable from "nothing to
report". A meter you believe is running and is not is worse than no meter, because it is trusted.

- **Minimum: `"timeout": 30`** for any hook invoking `session`.
- **Use 40** for hooks that make a network call (rate-wall checks hitting the usage endpoint).
- **Never** leave a `session`-invoking hook on a harness default under 30.

```jsonc
{
  "type": "command",
  "command": "pwsh -NoProfile -ExecutionPolicy Bypass -File C:/path/to/context-hook.ps1",
  "timeout": 30
}
```

Audit the whole set, not the hook you just edited — a single sub-30 straggler is the one that
disappears.

---

## 3. Do not call `session` when the harness already told you the number

If the harness hands the status line a valid `used_percentage`, **render that**. Calling
SessionMeter in that path is not a second opinion; it is a second *risk*:

- it launches a process on every prompt for a number already in hand, and
- for Claude Code it selects the **newest transcript for the current working directory** — so with
  two concurrent sessions in one directory it can return the *other* session's reading.

**A fresh session's `0%` is a valid measurement, not a missing one.** Treating `0` as "absent" is the
bug that triggers a startup-time call and the cross-session borrow. Guard on `$null`, never on zero.

> **Known limit.** `--session <id>` pins an exact transcript for `--pi` and `--codex` only. On the
> Claude Code path there is no session pin — resolution is by working directory, and two live
> sessions sharing one directory can still swap. Keep concurrent sessions in separate directories
> where the reading matters.

---

## 4. Treat the exit codes as the contract they are

| Code | Meaning | What the caller must do |
|---|---|---|
| 0 | Success | Use the reading. |
| 1 | Genuine failure — expired token, terminal HTTP status, network error, unknown command | Surface it. Do not retry unchanged. |
| 2 | Bad arguments to `session context` or `session usage` | Fix the call site. |
| 3 | API-key user / not signed in (`session usage` only) | `session usage` is unavailable; `session context` still works. |
| 4 | Upstream throttling after retries — 429/5xx (`session usage` only) | **No measurement was taken.** Say so; do not print a stale or assumed figure. |

Code 4 is deliberately neither 0 nor 1: exiting 0 would claim a measurement that never happened, and
1 would conflate "Anthropic is throttling, try again shortly" with "the login or the tool is broken".

**Never let a wrapper swallow a non-zero code.** A thin wrapper that reports success over a failed
inner call is the same lie as the inner call exiting 0 — and it is harder to find.

---

## 5. Harness boundary — which meters apply where

- **Claude Code** — `session context` (context) **and** `session usage` (5-hour / 7-day / per-model
  rate walls). Both apply.
- **Pi** — `session context --pi --cwd "<forward-slash-path>"` **only**. Pi is API-backed and has no
  Claude account rate walls; `session usage` must never drive a Pi decision.
- **Codex** — `session context --codex` and `session usage --codex`. The rollout records the exact
  `model_context_window` and quota snapshots, so no window or percentage inference is involved. Usage is
  refreshed by Codex activity rather than polled; use its printed observation age. `CODEX_HOME` and the
  current `CODEX_THREAD_ID` / `CODEX_SESSION_ID` are respected automatically.

Quote the forward-slash path and keep it quoted when calling through a POSIX shell — an unquoted
Windows backslash path collapses silently.

---

## 6. Verifying an integration

Run these in order. Each one fails loudly if the corresponding contract is broken.

```powershell
# 1. Tier 0 is being fed — a file appears for the live session within one prompt
Get-ChildItem "$env:LOCALAPPDATA\SessionMeter\context-cache\*.native-window.json" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 3 Name, LastWriteTime

# 2. The window in the cache matches what the reading uses
Get-Content "$env:LOCALAPPDATA\SessionMeter\context-cache\<sessionId>.native-window.json"
session context --cwd C:/your/project

# 3. No hook is under 30s
Select-String -Path "$env:USERPROFILE\.claude\settings.json" -Pattern '"timeout"'

# 4. The reading is sane — no percentage above 100, no USED EXCEEDS WINDOW
session context --cwd C:/your/project

# 5. Inside Codex, both commands select this exact rollout and report their observation time
session context --codex --cwd C:/your/project
session usage --codex --cwd C:/your/project
```

A healthy line looks like:

```
sessionmeter: 51% (512,004 / 1,000,000 tokens) — session ab12cd34, as of 2026-08-28T09:41:03+02:00 · 1M window (detected)
```

`(assumed)` in that suffix on a large-window model means **Tier 0 is not wired up** — fix the status
line before trusting the number.

---

## 7. The failure mode behind all of this

The 2026-08-28 incident that produced this document had one root cause with **two independent
victims**: a compiled tool and a separate script that reimplemented the same detection for speed both
relied on the `[1m]` marker, and a model-generation naming change broke both, in the same way, at the
same time. Fixing the compiled tool left the script still fabricating `269% (538,707 / 200,000)`.

**When a convention-drift bug surfaces, search for every reimplementation of the affected logic
before declaring it fixed.** "Fixed the tool" and "fixed the wrapper that duplicates the tool's logic"
are not the same fix, and the wrapper is usually the one nobody remembers exists.

---

**See also:** `SETUP.md` (installation and wiring), `README.md` (commands, detection, exit codes).
