# Conventions

## Assembly name is part of the XML contract

Custom classes are resolved as `"ClassName, AssemblyName"`:

```xml
<property name="AITask-1" value="SeekerDetonate,AgentArmory" .../>
<triggered_effect trigger="onSelfBuffFinish" action="SpawnSeekers, AgentArmory" .../>
```

Renaming the assembly requires updating `entityclasses.xml` and `buffs.xml` in the same commit.
A wrong assembly name does not error loudly — the class simply never resolves.

## Project file

Target `net48` with `NoStdLib` and explicit references to the game's own `mscorlib`/`System`
assemblies. Using `netstandard2.1` pulls in the NETStandard reference pack, whose
`System.Runtime` versions collide with those inside `Assembly-CSharp` (MSB3277 warning flood).

## Unity

Asset bundles must be built with the engine version the game ships — **2022.3.62f2** for 3.0.1.
Author models at true world size, prefab root at scale 1, and never correct an import mistake
with `SizeScale` (see architecture notes on why values below 1 break collision).

Bundles are `UnityFS`-compressed: byte-scanning an exported `.unity3d` will not find a value even
when it is definitely present. But the bundle *can* be unpacked offline —
`tools/bundle_peek.py` does it with the standard library only:

```
python tools/bundle_peek.py AgentArmory/Resources/seekerdrone.unity3d SeekerCharge
```

It answers exactly one question — *is this asset actually inside?* — which is the failure the
game reports only as a piece of leather in your hand. Header layout, both alignment steps and
the LZMA quirk are documented in the script.

Numeric values still have to be checked at runtime: the unpacked blob is a serialized Unity
file, not YAML, so a float is not searchable as text.

### Building the item bundle

`SeekerCluster3D/Assets/Editor/AgentArmoryBuild.cs` adds the menu item
**`Agent Armory → Handmodell + Bundle bauen`**. It regenerates `SeekerCharge.prefab`
programmatically (size derived from the real renderer bounds, pivot on the bounds centre),
builds drone + charge into the single `seekerdrone.unity3d`, and copies it to repo and MO2 with
a timestamped backup beside `Resources/`. Both size and pivot are constants at the top of the
script — never fix a placement problem by scaling the prefab by hand, that is what made the
value unreproducible last time.

## Recipe gating

`Recipe.IsUnlocked` (IL-verified) is the whole story:

```
if (!IsLearnable) return true;                       // no "learnable" tag => always craftable
return EffectManager.GetValue(RecipeTagUnlocked, ..., // matched against the recipe's tags
                              base: player.GetCVar(recipeName)) > 0;
```

So a recipe is unlocked by **either** a CVar named exactly like the *recipe* (not the item — they
merely happen to match here), **or** a `RecipeTagUnlocked` passive effect hitting one of its tags.

This mod uses the CVar route via `thrownSeekerClusterSchematic`, because the tag route would
require an XPath write into vanilla `progression.xml`'s `craftingExplosives` node — a shared node
in a large modlist. A schematic item is self-contained.

Two traps when adding a schematic:

- `schematicMaster` passes down `DescriptionKey`, so the `<item>Desc` naming convention does
  **not** apply — set `DescriptionKey` explicitly or you get the generic blurb.
- `Extends` `param1` is the *exclusion* list (split on `,`). Extending a vanilla item inherits its
  `UnlockedBy`, which only labels the crafting UI; drop it there when it no longer applies.

The exclusion list is stronger than it looks: it also removes whole `<property class="…">`
blocks, not just scalar properties — `DynamicProperties.CopyFrom` iterates `Classes` and skips
any key present in the exclusion set. That is how `thrownSeekerCluster` gets rid of the inherited
`Action1`; without it the charge could still be primed in your hand. Names are `Trim()`ed, so
spaces after the commas are fine here (unlike the AI-task resolution, where they break the split).

## Sounds

A custom sound is a `SoundDataNode` in the mod's `sounds.xml` plus a clip in the bundle. The clip
name after `?` has its extension stripped by `AssetBundleManager._get`
(`GameIO.RemoveFileExtension`), so `?activate` and `?activate.wav` are equivalent.

Where a sound is triggered from matters more than it seems:

- From a **buff**: `<triggered_effect trigger="onSelfBuffStart" action="PlaySound" sound="…"
  play_at_self="true"/>`. This is what the seeker uses — the activation beeps sit on
  `buffSeekerClusterArm`, i.e. on the pod that just acquired a target, not on the item.
- From an **item action**: Action*n* `Sound_start`, played by that action's `ExecuteAction`.
- From an **animation**: an `AnimationEvent` whose `stringParameter` is the node name, routed via
  `AnimationEventBridge.playSound`. Not reachable from XML at all — the name exists only inside
  the animation clip, so grepping the configs and the IL for it finds nothing.

⚠ `Entity.PlayOneShot(clip, sound_in_head, serverSignalOnly, …)`: the third parameter does the
opposite of its name. `false` → `Audio.Manager.BroadcastPlay` (local **and** networked); `true` →
`Audio.Manager.Play` (local only). Never decide which of two competing calls to suppress by
reading the parameter name.

## Localization

**The file must be named `Localization.csv`.** 7DTD 3.x only ever opens that name
(`Localization.LoadPatchDictionaries`); a `Localization.txt` is read by nobody. This repo
shipped one for weeks - there is no error, no warning and no log line, the keys simply render
as their raw names in game.

- **No BOM.** `Localization.loadCsv` requires the first cell to be exactly `KEY`; a BOM makes
  it `﻿Key` and the whole file is rejected.
- **20-column header**, same as vanilla:
  `Key,File,Type,UsedInMainMenu,NoTranslate,KeepLoaded,english,Context / Alternate Text,` then
  the twelve other languages. A row with a different column count silently shifts languages.
- **Prove it loaded**, do not assume: `grep "Loading localization from mod: AgentArmory"` in
  the client log. That line is written only when the file was found.

The release workflow fails the build on a stray `.txt`, on a BOM and on a ragged row.

## Debug logging

Each module keeps a `DBG` constant. Flip these to `false` before release:

- `Seeker/EAISeekerDetonate.cs`
- `Seeker/EAISeekerPod.cs`
- `Seeker/MinEventActionSeekerExplode.cs`
- `Seeker/Patch_SeekerPhysics.cs`
- `Seeker/Patch_SeekerRoll.cs`

⚠ The last two were added late: they had **no flag at all** and kept printing per-entity
capsule and model-offset lines into every player's log - eleven entities per throw. Grepping
for `const bool DBG` is not enough to find that; grep for `Debug.Log` and classify each hit.

**`Debug.LogWarning` stays ungated** in those two files, deliberately. A degenerate capsule, a
rescued drone, a hit hard-lifetime and an unknown entity class are real fault states, and they
are the only trace a bug report from a stranger will carry. Silence them and a report becomes
"it didn't work".

The proof the flag took is the build output: a release build reports `CS0162` (unreachable
code) for every dead `Dbg(...)` call. No `CS0162` means `DBG` is still `true` somewhere.

`InitMod` logs the **Harmony patch count**, not just "initialised". `PatchAll` returning
proves nothing - a patch class that matches no target fails silently, which is exactly how a
Harmony mod dies on a new game build. The test bench asserts the count is non-zero on every
version, so keep the wording of that log line in sync with `test/testbench.mod.json`.
