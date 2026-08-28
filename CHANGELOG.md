# Changelog

All notable changes to SessionMeter, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

> **On the history.** Entries up to and including `v0.7.2` were compiled from the project's own release
> tags when this file was adopted, and are accurate to what each version shipped. They are deliberately
> written for the person *using* the tool rather than the person who committed it.

## [0.7.2] — 2026-08-28

### Added

- **The context window is now read as a fact rather than inferred from the model name.** If your status
  line saves the window size the agent tool reports on every prompt, SessionMeter reads that first,
  before any name-based guess. A model released after this version reads correctly on its first prompt,
  with no update to SessionMeter and no list to maintain. See `INTEGRATION.md` §1 for the few lines this
  takes.

### Fixed

- A window larger than 1M is no longer described as "1M context". The label is computed from the actual
  number, so a future 2M-context model is reported honestly.

## [0.7.1] — 2026-08-28

### Fixed

- **Sessions on the newer model generation were reported as 100% full when they were nowhere near it.**
  Those models are granted a large context window without recording the marker earlier versions looked
  for, so a 1M session was measured against a 200,000-token ruler — a real reading of 409,308 tokens was
  printed as `100.0% (409,308 / 200,000)`. Two independent guards now prevent it: a known-family default,
  and a safety net that refuses to publish a reading whose used tokens exceed the window it assumed.

## [0.7.0] — 2026-08-16

### Changed

- **A rate limit on Anthropic's side is no longer reported as a broken tool.** `session usage` now retries
  a throttle or upstream fault up to three times within a five-second budget, honouring the server's own
  `Retry-After` when it sends one. A throttle that survives the retries prints to standard output as an
  outcome — not to standard error, which shells paint red — and exits with the new code `4`, so a script
  can tell "try again shortly" apart from "your login is broken". No percentage is invented in place of
  the reading that was never taken.

## [0.6.5] — 2026-08-11

### Fixed

- Sessions running in a git worktree resolved to the wrong directory and could report another session's
  reading.

## [0.6.1] — 2026-07-24

### Fixed

- Restored enforcement of the per-model scoped rate wall, which the previous release had removed from the
  binding calculation.

## [0.6.0] — 2026-07-24

### Changed

- Reworked which wall is reported as *binding* when several are in play.

## [0.4.3] — 2026-07-12

### Fixed

- The reading now flags itself when the tokens used exceed the window it was measured against, instead of
  quietly printing an impossible percentage.
- Window detection now picks the entry with the strongest evidence rather than whichever the agent tool
  happened to record first — one working directory can appear under several spellings whose records
  disagree, and the stale one could win.

## [0.4.1] — 2026-07-10

### Added

- Reading Pi sessions (`session context --pi`), including pinning one exact session with `--session`.
- A dark high-contrast application icon, and the ready-made hooks, skill and snippets in `examples/`.

## [0.4.0] — 2026-07-10

### Added

- Windows installer, application icon, MIT licence, and the full setup guide.
- Rate-wall hook example, including automatic switching to a higher model tier at the wall.

## [0.2.0] — 2026-07-04

### Added

- First release. `session context` reports how full the current session's context window is, read from
  the local transcript and needing no login. `session usage` reports the live 5-hour and 7-day rate-limit
  windows for a Claude subscription.
