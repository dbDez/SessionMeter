---
name: carry-on
description: Resume a handed-off session. Use when the user says "carry on" (or "continue", "resume", "pick up where we left off") — read HandOff.md in the working directory, summarise where things stand in one line, then continue from the first outstanding action.
---

# Carry On — resume from a hand-off

This is the resume half of the hand-off / carry-on loop (see `SETUP.md` §
"Hand-off & carry-on"). It pairs with a hand-off rule that writes `HandOff.md`
and commits before a wall hits.

When the user says **carry on** (or "continue", "resume", "pick up where we left off"):

1. **Read `HandOff.md`** in the current working directory.
   - If it's missing, say so and ask what to resume — do not guess.
2. **Summarise in ONE line** where the last session left off and what the first next action is.
3. **Resume** from the first item under `## Outstanding` (or `## First action next time`).

Do not re-read the whole chat history — `HandOff.md` is the memory. That's the point: a fresh,
cleared session starts cheap and accurate off a compact file instead of a bloated context window.

**The full loop:** hit a wall → agent writes `HandOff.md` + commits + pushes → user runs `/clear`
→ user types `carry on` → this skill reads `HandOff.md` and continues.
