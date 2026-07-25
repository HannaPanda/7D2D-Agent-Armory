# Agent Armory

Deployable agent gadgets for **7 Days to Die 3.0.1**, inspired by the skill loadouts of
tactical looter shooters. Harmony DLL + XML, one mod folder.

Names, art and numbers are original — the *idea* of a modular gadget belt is the inspiration,
not the source material.

## Modules

| Module | Status | What it does |
|---|---|---|
| **Cluster Seeker** | playable | Throwable pod. Lands, arms, then bursts into autonomous seeker drones that hunt nearby enemies and detonate on contact. If nothing is nearby, the pod follows you until it finds something. |
| Chem Launcher | planned | — |
| Throwable Turret | planned | — |
| Disposable Gun Drone | planned | — |
| Hive | planned | — |

## Install

Copy `AgentArmory/` into `7 Days To Die/Mods/`. The mod ships a DLL, so **EAC must be off**.
Asset bundles are not transferred from server to client — for multiplayer every player needs
the mod installed.

## Repository layout

```
AgentArmory/            the deployable mod folder (this is what goes into Mods/)
  Config/               XML: entityclasses, buffs, items, recipes, Localization
  Resources/            Unity asset bundles
  AgentArmory.dll       built from src/dll
src/
  dll/                  C# sources, one folder per module
    Seeker/             Cluster Seeker: AI tasks, spawner, physics patches
  unity/                Unity source assets (prefabs, materials)
docs/                   architecture notes and conventions
```

## Build

```bash
dotnet build src/dll/AgentArmory.csproj -c Release -o src/dll/out
```

Then copy `src/dll/out/AgentArmory.dll` into `AgentArmory/`.

Asset bundles are built in **Unity 2022.3.62f2** (must match the game's engine version) and
exported to `AgentArmory/Resources/`.

## Notes

The DLL name is part of the public contract: the XML refers to custom classes as
`"ClassName, AgentArmory"`. Renaming the assembly means updating `entityclasses.xml` and
`buffs.xml` in the same commit, otherwise the classes silently fail to resolve.

See [docs/architecture/seeker.md](docs/architecture/seeker.md) for how the Cluster Seeker works
and which engine behaviours it has to work around.

## License

MIT — see [LICENSE](LICENSE).
