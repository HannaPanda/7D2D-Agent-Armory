# AGENTS.md - Agent Armory (7 Days to Die 3.0.1 mod)

Onboarding for AI agents working on this repo. Read this first.

## What this project is

A modular set of deployable agent gadgets for **7 Days to Die 3.0.1**, inspired by the skill
loadouts of tactical looter shooters. Original names, art and numbers — the inspiration is the
*concept* of a gadget belt, not the source material.

Modules:

- **Cluster Seeker** — throwable pod that lands, arms, and bursts into autonomous seeker drones
  which hunt nearby enemies and detonate on contact. With no enemies around, the pod follows
  the player. *Playable.*
- **Throwable turrets** — a box you throw that deploys an auto-turret where it lands. Six
  variants (gun, machine gun, shotgun, sniper, flamethrower, shock), eight craftable boxes.
  Single use: fixed ammunition, a lifetime, no picking it back up. *Deploys and fires; sound
  and the flame effect still need a play test — see `docs/architecture/turret.md`.*

Planned: chem launcher, disposable gun drone, hive. The chem launcher and the hive are meant to
cover healing, which is why there is deliberately **no support turret**.

## Repository layout

| Path | What |
|---|---|
| `AgentArmory/` | the deployable 7DTD mod (ModInfo.xml, Config/, Resources/, AgentArmory.dll) |
| `src/dll/` | C# Harmony source + `.csproj`; **one folder per module** (`Seeker/`), shared code in `Shared/` |
| `src/unity/` | Unity source assets (prefabs, materials) — bundles are built in Unity 2022.3.62f2 |
| `docs/` | architecture notes and conventions |

## Docs map

| Doc | Read it when |
|---|---|
| `docs/architecture/seeker.md` | working on the seeker, or on movement/collision of any drone-like entity |
| `docs/architecture/turret.md` | working on turrets, or on anything that spawns an `EntityTurret`, reads ammo/perk effects, or adds a throwable that deploys something |
| `docs/conventions/modding.md` | touching the csproj, the assembly name, Unity export, or debug flags |

## Before you change anything

- **Prefer the `7d2d-modding` skill** for any engine/API question. It interrogates the real
  `Assembly-CSharp.dll` via Mono.Cecil instead of guessing, and its `LEARNINGS.md` records the
  traps this project already fell into. Several bugs here took multiple rounds precisely because
  an assumption was made instead of dumping the IL — including unit assumptions, which are as
  worth verifying as signatures.
- The repo mirrors a live MO2 deployment at
  `C:\Modlists\Smorgasbord\mods\AgentArmory\AgentArmory\` — keep them in sync.
- 7DTD locks the DLL while running. Close the game before deploying.

## Traps that already cost time

- `SizeScale < 1` provably breaks the collision capsule (`Entity.SetCCScale` pins the radius
  factor at 1 while scaling centre and height). Author at true size; keep `SizeScale >= 1`.
- `MoveSpeed`/`MoveSpeedAggro` do **not** control movement for `RootMotion=false` entities.
  `Patch_SeekerStepLimiter` is what re-binds them.
- A subclass of `EAIApproachAndAttackTarget` that overrides `CanExecute` without calling base
  must also override `Continue()`.
- Asset bundles are compressed, so a byte scan finds nothing — but they **can** be verified
  offline: `tools/bundle_peek.py` unpacks UnityFS with the stdlib and lists the asset names.
  Use it before blaming the XML for a missing model.
- **`--` inside an XML comment breaks the whole file.** This has now cost a run twice. Use
  `:`, `,` or `=` instead, and validate every Config file before deploying:
  `python -c "import xml.etree.ElementTree as ET,glob;[ET.parse(f) for f in glob.glob('*.xml')]"`.
  The damage is not local — one bad file drops out of the load entirely, and every other file
  that names one of its items then fails too, pointing at something that is perfectly fine.
- **Every item needs a `Meshfile`**, even a pure data carrier that never reaches an inventory
  (`ItemClassesFromXml.parseItem` throws when `CanHold()` and the property is absent, and
  `CanHold` defaults to true). Same for `Material`.

Full reasoning and the IL evidence for each: `docs/architecture/seeker.md`.
