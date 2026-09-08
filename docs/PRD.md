# SessionMeter product requirements

## Product and users

SessionMeter is a keyless CLI and reusable .NET core for developers and automation measuring coding-assistant context and account usage. It reads harness-owned telemetry without changing it.

## Outcomes and scope

Report measured input context, its window and provenance; report account rate windows separately. Support Claude Code, Pi and Codex. Do not estimate monetary cost, infer quota from tokens, change model routing, or collect credentials remotely.

## Requirements

Statuses: IMPLEMENTED means present in source; VERIFIED means exercised by tests and a live probe; PLANNED means not yet complete.

| ID | Requirement | Status | Acceptance |
|---|---|---|---|
| CTX-01 | Claude context detection | IMPLEMENTED | Preserve transcript input/cache accounting and native-window/state resolution. |
| CTX-02 | Pi context detection | IMPLEMENTED | Preserve Pi transcript accounting and model registry resolution. |
| CTX-03 | Codex context detection | VERIFIED | All model names use the recorded window, cached input is not double-counted, missing windows are unknown, CODEX_HOME and explicit/current session identity are respected. Model switches must not label an old measurement as a new model. |
| USE-01 | Claude account usage | IMPLEMENTED | Default usage command preserves OAuth windows, scoped limits, retries and exit codes. |
| USE-02 | Codex account usage | VERIFIED | Explicit usage --codex reads the selected rollout's latest reported primary/secondary and scoped limits, with duration, percentage, reset, scope and observation timestamp. No Claude endpoint or credentials. Null/missing limits are unavailable, never zero. Historical observations are labelled and expired resets are not silently refreshed. |
| CLI-01 | Scriptable selection | VERIFIED | --cwd and --session select a Codex rollout; --raw emits Codex usage JSON; incompatible harness flags fail before any network request. |

## Roadmap and decisions

2026-09-08: retrofit from source, README and the user's request. Complete CTX-03, USE-02 and CLI-01 with regression coverage. Use local Codex telemetry already written during activity; no additional API credential is needed. These snapshots are not live polling while Codex is idle. Existing Claude and Pi behavior remains the compatibility boundary.

## Verification evidence

2026-09-08 on PIETER-PC: the final Release suite passed 116/116 after the independent review fixes. Live
source and installed-binary probes selected the exact current Codex rollout, reported its recorded
258,400-token context window, and rendered both recorded quota windows with reset and observation times.
`session usage --cwd C:/Dev/SessionMeter` exited 2 before any OAuth output. The installed PATH binary
reported 0.8.0. Final installer: 26,362,425 bytes; SHA-256
`ED1A22257DBC14856AEEADF97C62A4BEA8B4D9F7F893947D641008AEAB5E1290`.
