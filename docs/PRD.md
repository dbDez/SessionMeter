# SessionMeter product requirements

## Product and users

SessionMeter is a keyless CLI and reusable .NET core for developers and automation measuring coding-assistant context and account usage. It reads harness-owned telemetry without changing it.

## Outcomes and scope

Report measured input context, its window and provenance; report account rate windows separately. Support Claude Code, Pi and Codex. Do not estimate monetary cost, infer quota from tokens, change model routing, or collect credentials remotely.

## Requirements

Statuses: IMPLEMENTED means present in source; VERIFIED means exercised by tests and a live probe; PLANNED means not yet complete.

| ID | Requirement | Status | Acceptance |
|---|---|---|---|
| CTX-01 | Claude context detection | IMPLEMENTED | Preserve transcript input/cache accounting and native-window/state resolution. A bare 5-series model key is the 1M window, not 200K evidence; a `compact_boundary` newer than every usage line reports its `postTokens` (0.8.3). |
| CTX-02 | Pi context detection | VERIFIED | Use the provider/model on the last successful usage block for its numerator and denominator; separately expose the latest selected model. Resolve installed, newer persisted, and custom model windows with provider-scoped identity and custom overrides. A switch must never relabel or re-denominate an old measurement. |
| CTX-03 | Codex context detection | VERIFIED | All model names use the recorded window, cached input is not double-counted, missing windows are unknown, CODEX_HOME and explicit/current session identity are respected. Model switches must not label an old measurement as a new model. |
| USE-01 | Claude account usage | IMPLEMENTED | Default usage command preserves OAuth windows, scoped limits, retries and exit codes. |
| USE-02 | Codex account usage | VERIFIED | Explicit usage --codex reads the selected rollout's latest reported primary/secondary and scoped limits, with duration, percentage, reset, scope and observation timestamp. No Claude endpoint or credentials. Null/missing limits are unavailable, never zero. Historical observations are labelled and expired resets are not silently refreshed. |
| CLI-01 | Scriptable selection | VERIFIED | --cwd and --session select a Codex rollout; --raw emits Codex usage JSON; incompatible harness flags fail before any network request. |

## Roadmap and decisions

2026-09-08: retrofit from source, README and the user's request. Complete CTX-03, USE-02 and CLI-01 with regression coverage. Use local Codex telemetry already written during activity; no additional API credential is needed. These snapshots are not live polling while Codex is idle. Existing Claude and Pi behavior remains the compatibility boundary.

2026-09-14: CTX-02 now distinguishes Pi's selected model from the model owning the last persisted usage block,
resolves windows from Pi's built-in catalogue plus `models.json` overlays, and preserves provider identity when
multiple providers use the same model ID. SessionMeter remains a manual Pi diagnostic; Pi's native footer and
auto-compaction are the live control surface. Claude and Codex implementations remain unchanged.

## Verification evidence

2026-09-08 on PIETER-PC: the final Release suite passed 116/116 after the independent review fixes. Live
source and installed-binary probes selected the exact current Codex rollout, reported its recorded
258,400-token context window, and rendered both recorded quota windows with reset and observation times.
`session usage --cwd C:/Dev/SessionMeter` exited 2 before any OAuth output. The installed PATH binary
reported 0.8.0. Final installer: 26,362,425 bytes; SHA-256
`ED1A22257DBC14856AEEADF97C62A4BEA8B4D9F7F893947D641008AEAB5E1290`.

2026-09-14 on PIETER-PC: a clean Release build and the complete suite passed 124/124, including explicit
non-Pi output-shape protection and Pi coverage for built-in/custom provider separation, persisted-catalogue and
`modelOverrides` precedence, and switches before/after the new model's first response. A live source probe and the
silently installed PATH binary both selected the current Pi session and reported
`openai-codex/gpt-5.6-sol` at its detected 272,000-token window. Installed binary: 0.8.1. Final installer:
26,374,181 bytes; SHA-256 `CCCA96FFF7D711B16CA516AA5E359FB8960C96362DB7952590326ED37C8E6048`.
