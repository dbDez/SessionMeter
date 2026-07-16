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

For Claude Code, `session context` **auto-detects a 1M-context model** and uses a **1,000,000-token** denominator; otherwise
it assumes the **standard 200,000-token** window.

The session transcript's model field strips the `[1m]` beta marker (it reads e.g. `claude-opus-4-8` even on the beta),
so it can't reveal the window on its own — instead the tool cross-references **Claude Code's own recorded per-project
model state** in `%USERPROFILE%\.claude.json` (under `projects["<cwd>"].lastModelUsage`, which *keeps* the `[1m]`
marker). The reading tells you which window it used and whether that was detected or assumed:

```
> session context   # on a claude-opus-4-8[1m] beta session
sessionmeter: 24.8% (247,834 / 1,000,000 tokens) — session ab12cd34, as of 2026-07-04T21:14:07+02:00 · 1M window (detected)
```

The suffix is one of ` · 1M window (detected)`, ` · 200K window (detected)`, or ` · 200K window (assumed)`
(the fallback when no per-project model state is found).

For Pi, `session context --pi` reads the active provider/model from Pi's latest assistant message and resolves the
window from `%USERPROFILE%\.pi\agent\models.json`. This supports PAV GPT windows such as `900,000` for
`gpt-5.6-terra`, `gpt-5.6-sol`, `gpt-5.6-luna`, `gpt-5.5`, and `gpt-5.4`.

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

> ⚠️ **Undocumented endpoint.** `session usage` reads `GET /api/oauth/usage`, which is **undocumented and
> unversioned** — Anthropic may change or remove it without notice. `session context` depends only on the
> local transcript format and has no such dependency.

## Provenance

Session is **extracted from [MO (Master Orchestrator)](https://github.com/) — it is _not_ a fork.** The
usage + context logic was lifted into a dependency-light shared core (`SessionMeter.Core`) with no
dependency on MO's config, hosting, logging, or the Anthropic SDK. It reads the Claude Code OAuth token from
`~/.claude/.credentials.json` (`claudeAiOauth.accessToken`) for `usage`; Claude Code transcript JSONL under
`~/.claude/projects/` for `context`; Pi transcript JSONL under `~/.pi/agent/sessions/` for `context --pi`; and
Codex rollout JSONL under `~/.codex/sessions/` for `context --codex`.

**Future convergence:** MO may later reference `SessionMeter.Core` directly so the two tools share exactly
one usage/context implementation and never diverge.

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
