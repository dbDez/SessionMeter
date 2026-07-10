# SessionMeter examples

Copy-paste-ready hooks and a skill that wire SessionMeter into Claude Code. All require
`session` on PATH — install SessionMeter first (see [`../SETUP.md`](../SETUP.md)).

| File | What it is |
|---|---|
| `hooks/warn-context.ps1` | UserPromptSubmit hook — nudges as the context window fills |
| `hooks/warn-usage.ps1` | UserPromptSubmit hook — 5-hour / 7-day / per-model rate-wall warnings + model-tier switch |
| `hooks/settings.snippet.jsonc` | how to register both hooks in `settings.json` |
| `skills/carry-on/SKILL.md` | a "carry on" skill — resume from `HandOff.md` in a fresh session |
| `HandOff.template.md` | the hand-off file template |
| `CLAUDE.snippet.md` | standing rules to paste into your `CLAUDE.md` |

## Set up in 3 steps

1. **Install SessionMeter** (see [`../SETUP.md`](../SETUP.md)) so `session` is on PATH.
2. **Add the hooks.** Copy `hooks/warn-context.ps1` and `hooks/warn-usage.ps1` somewhere, edit the
   paths in `hooks/settings.snippet.jsonc`, and merge its `hooks` block into `~/.claude/settings.json`.
3. **Add the rules + carry-on.** Paste `CLAUDE.snippet.md` into your `CLAUDE.md`, and drop
   `skills/carry-on/` into your Claude Code skills folder (or rely on the carry-on rule in the
   snippet).

Now the loop works: **hit a wall → the agent writes `HandOff.md` + commits → you `/clear` → type
`carry on`.** No lost lessons, no re-reading the whole history.

See [`../SETUP.md`](../SETUP.md) § "Hand-off & carry-on" for the full explanation.
