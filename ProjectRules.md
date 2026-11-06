# SessionMeter project rules

This repository is governed by the canonical application-development rules in
`C:\PKM\new_project_rules.md` and the universal rules in `C:\PKM\GlobalRules.md`.

Project-specific contract:

- `src/Session/Session.csproj` `<Version>` is the only version source; `build.ps1` derives the installer version.
- Preserve Claude, Codex, and Pi as separate provider boundaries. Never route one harness through another's credentials.
- Treat local transcripts and quota telemetry as read-only. Never log credentials or commit local rollouts/handoffs.
- The repository is public. Stage named source/docs/tests only, run the full suite, secret-scan, then commit and tag.
- A release is complete only after clean Release build/tests, installer build, local installed-binary smoke, changelog, commit, and tag.
