<!-- Paste this into your user-level (~/.claude/CLAUDE.md) or project CLAUDE.md. -->
<!-- It gives the agent the standing rules that make the SessionMeter hooks actionable. -->

## Session hand-off & carry-on (with SessionMeter)

- **Hand off before a wall.** When `session context` reaches ~60%, or `session usage` reports the
  5-hour wall ≥85%: finish only the in-flight step, then write/update `HandOff.md` (Decisions /
  What was done / Lessons learned / Outstanding / First action next time), commit and push it, and
  tell me to start a fresh session. Don't start new work.
- **Watch all the walls, not just context.** `session usage` reports the 5-hour, 7-day, and any
  per-model (scoped) walls. The 5-hour wall is the urgent one; the 7-day recovers slowly.
- **Switch model tiers at their wall.** When a per-model (scoped) weekly wall is near-full (~90%),
  dispatch agents on that tier with Opus 4.8 (`model: opus`) instead — a dispatch on an exhausted
  tier fails instantly.
- **Carry on.** When I say **carry on**, read `HandOff.md` in the working directory, summarise in
  one line where we left off, and resume from the first outstanding action.
- **Poll during long runs.** Hooks only fire on prompt submit, so on long autonomous stretches
  check `session usage` / `session context` yourself between steps.
