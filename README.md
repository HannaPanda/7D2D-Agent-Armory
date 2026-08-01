# Agent Armory - 7 Days to Die (V3.0 / V3.1)

Deployable agent gadgets, inspired by the skill loadouts of tactical looter shooters.
Harmony DLL + XML, one mod folder.

Names, art and numbers are original - the *idea* of a modular gadget belt is the inspiration,
not the source material.

## The Cluster Seeker

A throwable pod that does the aiming for you.

- **Throw it, that is the whole input.** No pin, no timer, no right-click. The charge goes off
  on impact and the pod is out.
- **The pod follows you** while nothing is in range, so throwing one early is not a waste.
- **Ten seeker drones** burst out when enemies come close. Each picks its own target, chases it
  for up to 18 seconds and detonates on contact - 180 damage in a 4 m radius.
- **Almost no block damage** (15 in a 1 m radius). The pod is a delivery charge; the damage
  lives in the drones, so it will not eat your own base.
- **Unlocked by the Explosives skill at level 35**, the same rank as the Timed Charge - or
  earlier from the **Cluster Seeker Schematic**, which drops from loot and appears at traders.
- **Crafted at the workbench**: 3 mechanical parts, 18 gunpowder, 3 forged steel,
  2 electrical parts, 2 springs.
- Localized into **13 languages** (EN, DE, ES, FR, IT, JA, KO, PL, PT-BR, RU, TR, ZH-Hans,
  ZH-Hant).

Planned modules: chem launcher, throwable turret, disposable gun drone, hive.

## Status

**0.9.0 - beta.** The Seeker plays the way it is meant to; the beta label is about what is
around it.

**Played and verified on V 3.1.0** - hand model, icon, sound, pod landing and following, ten
drones hunting and detonating, the skill entry, all looked at rather than assumed.
**V 3.0.0 and V 3.0.1** pass stage 1 (mod loads, 4 Harmony patches applied, 0 errors /
exceptions / XML problems) but have no GUI run against *this* build. 3.0.1 was played for days
during development - on a pre-0.9.0 build, inside a large modlist - and neither of those makes
it a verification of what ships here.

Other 3.x builds are untested rather than unsupported. The mod is a Harmony DLL, so the tested
list has to be re-established for **every** release; the procedure is under
[Testing](#testing) below.

### The skill-screen entry, and why a sixth one is invisible

Worth knowing before you add a `display_entry` to any vanilla skill: **the UI draws a fixed
number of rows, and a data entry beyond that count is silently dropped.**

`XUiC_SkillCraftingInfoWindow.UpdateSkill` iterates `this.levelEntries` - the *widget* list -
and assigns data by index (`entry.Data = DisplayDataList[index]`). It never iterates the data.
The widget count comes from the layout: `Data/Config/XUi_InGame/windows.xml`, window
`windowSkillCraftingInfo`, `<grid rows="5" cols="1" repeat_content="true">` → **five**.
`craftingExplosives` already ships five entries (T1..T5), so an appended sixth exists in memory
and is never drawn. No error, no warning, no gap in the list.

`progression.xml` therefore **extends the existing tier-3 entry** rather than adding one:
`icon`, `name_key` and `unlock_level` are position-matched comma lists, and the `unlock_tier`
on an `unlock_entry` is the 1-based index into them. The icon strip below has
`<grid rows="2" cols="7">` = 14 slots, so a fourth item is free.

Two traps that cost a round each:

- **`display_entry icon` is an icon-atlas *sprite* name, not an item name.** Vanilla proof in
  that same row: `resourceGunPowderBundle` carries `CustomIcon="resourceGunPowder"`, and the
  entry lists `resourceGunPowder`. Everywhere else the two names coincide, so the distinction
  only surfaces once a mod gives an item a `CustomIcon` under a different name. Real, but it
  was *not* why the entry was missing - proving a thing is wrong is not proving it is the
  cause.
- **Patch operations run in document order.** The `<append>` that adds the fourth
  `unlock_entry` matches on `@name_key`, which an earlier `<set>` has already rewritten, so it
  must spell the *new* value. An `<append>` that matches nothing fails silently.

## Requirements

- **EasyAntiCheat must be OFF** - this mod ships a Harmony DLL. Single-player and private
  servers.
- **No other mods needed.**
- **Multiplayer: every player needs it installed, and the server too.** Asset bundles are not
  transferred from server to client, so a client without the mod cannot render the pod or the
  drones.

## Installation

1. Install the zip with Vortex or MO2, or extract the `AgentArmory` folder into your
   `7 Days To Die/Mods/` folder.
2. Launch with EAC disabled.

Adding it mid-save is fine. Removing it is safe as long as no pod or drone is alive in the
world at the time - those are custom entity classes, and a save that still contains one cannot
resolve it without the mod.

## Repository layout

```
AgentArmory/            the deployable mod folder (this is what goes into Mods/)
  Config/               XML: entityclasses, buffs, items, recipes, progression, sounds, loot,
                        traders, Localization
  Resources/            Unity asset bundles
  UIAtlases/            custom item icons (160x160 PNG)
  AgentArmory.dll       built from src/dll
src/
  dll/                  C# sources, one folder per module
    Seeker/             Cluster Seeker: AI tasks, spawner, physics patches
  unity/                Unity source assets (prefabs, materials, sounds)
test/                   test-bench configuration (see Testing)
tools/                  helper scripts, e.g. bundle_peek.py
nexus/                  Nexus mod-page description and its images
docs/                   architecture notes and conventions
```

## Build

```bash
dotnet build src/dll/AgentArmory.csproj -c Release -o src/dll/out
```

Then copy `src/dll/out/AgentArmory.dll` into `AgentArmory/`.

Asset bundles are built in **Unity 2022.3.62f2** (must match the game's engine version) from
the Unity project, via *Agent Armory ▸ Handmodell + Bundle bauen* - one menu item that
configures the prefab and the audio clip, builds `seekerdrone.unity3d` and copies it into
`AgentArmory/Resources/`.

Before tagging a release, flip `DBG` to `false` in `Seeker/EAISeekerDetonate.cs`,
`Seeker/EAISeekerPod.cs` and `Seeker/MinEventActionSeekerExplode.cs`. A release build then
shows `CS0162` (unreachable code) for the dead `Dbg(...)` calls - that warning is the proof
the flag took.

## Testing

The mod is registered with the [test bench](https://github.com/HannaPanda/7D2D-TestBench)
through `test/testbench.mod.json`:

```bash
tb run --mod agentarmory --profile matrix --json   # headless, every configured version
tb run --mod agentarmory --version 3.1.0 --stage gui --visual defer --json
tb report --mod agentarmory --json                 # matrix + the TESTED_VERSIONS line
```

Headless proves the mod loads, Harmony patches apply and no XML breaks. It proves nothing
about this mod's actual content: **a missing bundle asset does not log an error** -
`ItemClass.CloneModel` falls back to `leather.fbx` in silence - so models, icon, sound and
drone behaviour need the GUI stage and a human look.

## Release

Push a `v*` tag; `.github/workflows/release.yml` validates the XML, checks the installable
structure, zips `AgentArmory/` and publishes the GitHub Release. The Nexus upload step runs
only once the repository variable `NEXUSMODS_FILE_ID` is set.

`TESTED_VERSIONS` in that workflow is the single source for the version claim in the release
body and the Nexus file description. Keep it in sync with this README and
`nexus/description.bbcode`, and only ever list builds that passed both test stages.

## Notes

The DLL name is part of the public contract: the XML refers to custom classes as
`"ClassName, AgentArmory"`. Renaming the assembly means updating `entityclasses.xml` and
`buffs.xml` in the same commit, otherwise the classes silently fail to resolve.

See [docs/architecture/seeker.md](docs/architecture/seeker.md) for how the Cluster Seeker works
and which engine behaviours it has to work around.

## License

MIT for the code, XML, build scripts and docs - see [LICENSE](LICENSE).

⚠ **The drone mesh, its material, the inventory icon and the activation sound are NOT covered
by it.** The mesh is a commercial asset under a CGTrader Royalty Free License bought by the
author; that licence permits use and modification in own projects but forbids redistributing
the file and forbids AI training. MIT would otherwise hand everyone a redistribution right that
is not the author's to give. Details and the exact paths: [THIRD-PARTY.md](THIRD-PARTY.md).

Forking is fine - replace the art with your own.
