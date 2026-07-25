# CLAUDE.md

Project context and workflows for this repo live in **[AGENTS.md](AGENTS.md)** - read it first.

@AGENTS.md

## Quick reminders for Claude Code

- This is a **7 Days to Die 3.0.1** mod. The repo mirrors a live MO2 deployment at
  `C:\Modlists\Smorgasbord\mods\AgentArmory\AgentArmory\` - keep them in sync.
- Prefer the **`7d2d-modding` skill** for any engine/API question; it interrogates the real
  `Assembly-CSharp.dll` instead of guessing, and its `LEARNINGS.md` records the traps.
- Before deploying the DLL, make sure 7DTD is **not running** (it locks the file).
- The assembly name is part of the XML contract (`"ClassName, AgentArmory"`). Renaming it means
  updating `entityclasses.xml` and `buffs.xml` in the same commit - a wrong name fails silently.
- Movement debugging: read `docs/architecture/seeker.md` first. The ruled-out list there is
  measured, not guessed; re-testing those costs a play session each.
