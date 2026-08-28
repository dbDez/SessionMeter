# Session — kill Claude Code's context guessing

**Session.exe** is a tiny, keyless consumer CLI for Claude Code, Codex, and Pi users. It gives you an **accurate
in-session context-window %** (the number native `/context` only shows interactively) plus Claude Code's **live
rate-limit windows** — without an Anthropic API key.

Two commands:

## `session context`

Reads the local Claude Code session transcript for a working directory and reports exactly how full the
context window is — the same input footprint the API counted, not a guess. Add `--pi` or `--codex` to read
Pi's or Codex's local session transcript instead. **Needs no login at all.**

```
> session context
sessionmeter: 18.4% (36,846 / 200,000 tokens) — session ab12cd34, as of 2026-07-04T21:14:07+02:00 · 200K window (assumed)
```

Options:

- `--cwd <path>` — read a specific directory (default: the current directory).
- `--pi` — read Pi's `~/.pi/agent/sessions/` JSONL files instead of Claude Code's `~/.claude/projects/` files.
- `--codex` — read Codex's `~/.codex/sessions/` rollout JSONL files instead of Claude Code's transcript.
- `--session <id>` — with `--pi` or `--codex`, pin one exact session instead of selecting the newest matching CWD.

### Context-window detection

The numerator — tokens used — comes straight from the transcript and is always exact. The **denominator**
is the hard part, because the transcript's model field strips the `[1m]` beta marker (it records e.g.
`claude-opus-4-8` even on the 1M beta), so it cannot reveal the window on its own.

For Claude Code the window is resolved by a ladder, strongest evidence first:

| Tier | Source | Why it is where it is |
|---|---|---|
| **0 — ground truth** | A per-session window cache written by your status line: `%LOCALAPPDATA%\SessionMeter\context-cache\<sessionId>.native-window.json` | Claude Code hands the real window to the status line on every prompt. Remembering it beats inferring it, and it needs **no model list to maintain** — a model released tomorrow reads correctly on its first prompt. **Wiring this up is the single highest-value integration step; see [`INTEGRATION.md`](INTEGRATION.md) §1.** |
| **1–2 — stated fact** | The `[1m]` marker in Claude Code's own per-project model state, `%USERPROFILE%\.claude.json` → `projects["<cwd>"].lastModelUsage` (which *keeps* the marker) | Correct whenever present, but newer model generations have stopped recording it. |
| **3 — known family** | A model family known to ship a large window | Degrades gracefully as the list ages; outranks a blind guess, never outranks a stated fact. |
| **safety net** | `used > window` is impossible | If a reading implies more tokens used than the window holds, the assumption was wrong — the tool escalates rather than print an absurd percentage. |

Absent every tier, it assumes the standard **200,000-token** window. The reading always tells you which
window it used and whether that was detected or assumed:

```
> session context   # on a 1M-context session
sessionmeter: 24.8% (247,834 / 1,000,000 tokens) — session ab12cd34, as of 2026-07-04T21:14:07+02:00 · 1M window (detected)
```

The suffix is one of ` · 1M window (detected)`, ` · 200K window (detected)`, or ` · 200K window (assumed)`.
**Seeing `(assumed)` on a large-window model means Tier 0 is not wired up** — fix the status line before
trusting the number.

For Pi, `session context --pi` reads the active provider/model from Pi's latest assistant message and resolves the
window from `%USERPROFILE%\.pi\agent\models.json`. This supports custom-provider model windows such as `900,000` when they are declared in Pi's model registry.

For Codex, `session context --codex` reads the newest rollout transcript with a matching `session_meta.cwd`.
Its latest `token_count` event records the current `last_token_usage.input_tokens` and exact
`model_context_window`, so SessionMeter does not guess the limit. `cached_input_tokens` is already part of
Codex input accounting and is not double-counted.

> ⚠️ **Undocumented state.** `.claude.json` is **undocumented, internal Claude Code state** (the same caveat
> class as the OAuth usage endpoint below) — its shape may change without notice. Detection parses it
> tolerantly and always falls back to the assumed 200K window on any surprise; it never fails the read.

## `session usage`

Live-reads Claude Code's rate-limit windows (rolling 5-hour session + 7-day) from the OAuth usage endpoint.

```
> session usage
Session usage — live, programmatic (/api/oauth/usage)
────────────────────────────────────────────────
  5-hour session : 32% used · resets 2026-07-04 22:10 local · 2026-07-04 20:10:00Z
  7-day window   : 39% used · resets 2026-07-10 02:00 local · 2026-07-10 00:00:00Z
  binding        : 7-day @ 39%
```

Options:

- `--raw` / `-r` — also print the exact JSON body the endpoint returned.

**`usage` needs a Claude _subscription_ login (Pro/Max).** Signing in to Claude Code with a subscription
stores an OAuth token that this command uses — an Anthropic **API key cannot** read these windows. If you're
on an API key (or aren't signed in), `session usage` prints a short, friendly message and points you at
`session context`, which works for you regardless:

```
> session usage
Session usage — unavailable

`session usage` reads Claude Code's live rate-limit windows (5-hour + 7-day),
which require a Claude subscription login (Pro/Max). That signs you in with an
OAuth token — an Anthropic API key can't read these windows.

It looks like you're using an API key (or aren't signed in to Claude Code).
  • Run `claude` and sign in with your Claude subscription to enable usage windows.
  • `session context` works for you regardless — it reads the local session
    transcript and needs no login.
```

### When the endpoint is throttling (HTTP 429 / 5xx)

A rate limit on Anthropic's side is **temporary and is not a fault in this tool** — but it used to be
indistinguishable from one: a single 429 wrote a diagnostic to stderr and exited 1, which any shell that
paints stderr red renders as a broken command.

Since v0.7.0 a transient status (429 or any 5xx) is **retried up to 3 times total**, honouring the endpoint's
`Retry-After` header when it sends one (both the delta-seconds and HTTP-date forms). All waiting is drawn from
a single **5-second budget**, so the worst case stays predictable for interactive use and for prompt hooks. If
the endpoint names a wait longer than the budget, the tool does not pretend — it stops immediately and quotes
the server's own number to you.

A throttle that survives the retries prints to **stdout**, not stderr, because it is an outcome rather than a
crash. No percentage is printed at all: no snapshot was read, and nothing cached or estimated is shown in its
place. Terminal failures — an expired token, 401/403/404, a parse error — still go to stderr.

```
> session usage
Session usage — temporarily unavailable

HTTP 429 — rate limited, after 3 attempts.

This is an upstream rate limit or outage on Anthropic's side, not a fault in this
tool and not a problem with your login. No usage snapshot was read, so none is
shown — no cached or estimated percentages are printed in place of a live reading.

  • The endpoint asked for a wait of 30 seconds before the next try.
  • `session context` is unaffected — it reads the local session transcript and
    needs no network and no login.
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | A genuine failure — expired token, a terminal HTTP status, a network error, an unknown command. |
| 2 | Bad arguments to `session context`. |
| 3 | API-key user / not signed in to Claude Code (`session usage` only). |
| 4 | Upstream temporarily unavailable after the retries — a 429 or 5xx (`session usage` only). |

Code 4 is deliberately **not** 0: no snapshot was obtained, and exiting 0 would claim a measurement that was
never taken. It is deliberately **not** 1 either, so a script can tell "Anthropic is throttling, try again
shortly" apart from "the login or the tool is broken".

> ⚠️ **Undocumented endpoint.** `session usage` reads `GET /api/oauth/usage`, which is **undocumented and
> unversioned** — Anthropic may change or remove it without notice. `session context` depends only on the
> local transcript format and has no such dependency.

## Provenance

Session began as the metering code inside a larger private agent orchestrator, and was **extracted — not
forked** — into a standalone tool. The usage and context logic was lifted into a dependency-light shared
core (`SessionMeter.Core`) carrying no dependency on that host's configuration, hosting, logging, or the
Anthropic SDK, so the core can be referenced from any .NET project as easily as the CLI can be run from a
shell.

Everything it reads is a local file the agent tool already writes. There is no service to sign up for and
no key to configure:

| Command | What it reads |
|---|---|
| `session usage` | The Claude Code OAuth token in `~/.claude/.credentials.json` (`claudeAiOauth.accessToken`) |
| `session context` | Claude Code transcript JSONL under `~/.claude/projects/` |
| `session context --pi` | Pi transcript JSONL under `~/.pi/agent/sessions/` |
| `session context --codex` | Codex rollout JSONL under `~/.codex/sessions/` |

## Build & run

```
dotnet build SessionMeter.sln -c Release
dotnet test  SessionMeter.sln -c Release

# run
dotnet run --project src/Session -c Release -- context
dotnet run --project src/Session -c Release -- usage
```

The built binary is `src/Session/bin/Release/net10.0/Session.exe`.

## Layout

```
SessionMeter.sln
src/SessionMeter.Core   dependency-light shared core (usage + context)
src/Session             the Session.exe CLI
tests/SessionMeter.Tests xunit tests over the pure core
```
