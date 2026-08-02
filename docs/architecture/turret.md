# Throwable turrets

The second module: a throwable box that deploys an auto-turret where it lands. Six variants,
eight craftable boxes (two of them ammo upgrades). Read this before changing anything about
turrets — most of what follows is engine behaviour that is not guessable from the XML.

Everything below was verified against the installed `Assembly-CSharp.dll` with the
`7d2d-modding` skill's Cecil scripts. IL offsets are from **3.0.1**; the same code is present
on 3.0.0 and 3.1.0.

## Why a turret needs *two* items

This is the single most important fact about the module, and it is the reason the file layout
looks redundant.

A turret's ballistics do **not** come from its `entity_class`. They come from
`EntityTurret.OriginalItemValue` — an `ItemValue` — and the ammunition's contribution is
resolved like this (`ItemValue.ModifyValue`, IL_003c..IL_00a5):

```
ItemClass.Actions[0] as ItemActionRanged
    -> MagazineItemNames[ItemValue.SelectedAmmoTypeIndex]
    -> ItemClass.GetItemClass(name).Effects.ModifyValue(...)
```

So the item that a turret carries **must have an `Action0` of class `Ranged`** with
`Magazine_items`. A throwable has `ThrowAway` in that slot. One item cannot be both.

Hence, per turret type:

| Item | Role | Seen by the player |
|---|---|---|
| `thrownAgentTurret<X>` | the box you craft, hold and throw | yes |
| `agentTurretCore<X>` | data carrier: ammo table, RPM, magazine, perk tag, tint | **never** |

The box points at its core item by name. `SelectedAmmoTypeIndex` picks the variant out of the
core item's `Magazine_items`, which is how the HP and AP boxes share one core item and one
entity class. **The order of `Magazine_items` is a contract** — inserting an entry silently
reassigns every box's ammunition.

## The perk tag does two jobs at once

`MiniTurretFireController.GetDamageEntity` calls

```
EffectManager.GetValue(EntityDamage, coreItem, base: entityDamage, OWNER, tags: coreItem.ItemTags)
```

The **owner** goes into the lookup, so the owning player's perks apply to the turret. Tagging
the core item `perkMachineGunner` therefore both scales the turret with that perk *and*
matches the ammunition's own effect gating.

⚠ **For 7.62 the tag is mandatory, not a bonus.** `ammo762mmBulletHP` and `-AP` declare their
`EntityDamage base_set` with `tags="perkDeadEye,perkMachineGunner"`. A core item without one of
those tags gets base damage **0** and the turret fires harmlessly, with nothing in the log.
9mm and 12 gauge effects are untagged, so there the tag is pure upside. **Check what the
ammunition gates on before choosing tags — do not infer it from the weapon name.**

## Everything exotic runs through `Buff`

The `Buff` property on the entity class is a comma list (spaces are stripped) that becomes
`buffActions` in `ItemActionAttack.Hit`, applied to whatever was hit (IL_0dd6..IL_0e45) with a
proc chance from PassiveEffect 195, base 1.0 — so always.

That is the whole implementation of the flamethrower (`buffBurningHazardPlayer` +
`buffHazard_Slow`, the same pair the vanilla `flamethrowerTrap` uses) and the shock turret
(`buffAgentShock` + `buffHazard_Slow`). Neither needed a line of C#.

The shock turret uses **our own** buff rather than vanilla `buffShocked`, because the latter
decides its damage from `ItemHasTags tags="stunBaton"`, `perkIntellectMastery` and the CVar
`_droneStunDamage` — i.e. the player's stun-baton build would determine how hard a thrown
turret hits.

## Three dead XML properties

Do not rely on these; two of them are set by vanilla itself, which makes them convincing.

- **`AmmoItem`** on the entity class — written to `MiniTurretFireController.ammoItemName` in
  `Init` and **never read anywhere in the assembly**. Ammunition comes from `Magazine_items`.
- **`MaxOwnerDistance`** on the entity class — vanilla sets it on `junkTurretGun`, yet the
  string does not occur in a single instruction. `EntityTurret.maxOwnerDistance` is filled
  exclusively from the `JunkTurretActiveRange` passive effect (IL_0260..IL_028c, **default
  10 m**).
- **`RayCount`** — read, but overwritten in `Fire` by PassiveEffect 16 (`RoundRayCount`) as
  soon as the loaded ammunition sets one. The shotgun's 10 pellets come from the shell, not
  from the entity.

## Two silent kill switches

`EntityTurret.OnUpdateEntity` recomputes `IsOn` from scratch every frame. Any of these turns
the turret off with no message:

1. `IsOn &= PercentUsesLeft > 0`
2. `IsOn &= Meta > 0` — only when `MagazineSize > 0`. **Empty ammo only switches the turret
   off; it does not remove it.** Despawning is ours (`Patch_TurretRuntime`).
3. `IsOn &= Owner != null` — the owning player must be loaded.
4. `IsOn &= DistanceToOwner < JunkTurretActiveRange²` — **default 10 m.** Absurd for a thrown
   turret, so every core item raises it via `passive_effect`.
5. `IsOn &= (active <= JunkTurretActiveCount) || closer_than_the_other || ForceOn`

⚠ Number 5 has a trap: `perkTurrets` sets `JunkTurretActiveCount` with `base_set 0` and **no
`tags` attribute** (vanilla `progression.xml:2999`), so it hits *every* item including ours. A
player *with* the turret perk would get fewer simultaneous Agent turrets than one without.
`ForceOn` bypasses the counter, but it is a one-frame grace — `OnUpdateEntity` resets it to
false at @994. `Patch_TurretRuntime` therefore re-sets it every frame.

## Facing and traverse

Two separate things decide what a turret can shoot at, and both bit us in the first play test:
it appeared to only see enemies in a limited wedge, pointing in an arbitrary direction.

**`YawRange` is halved.** `Init` computes `value * 0.5` and stores `yawRange = (-x, +x)`
(IL_0197..IL_01b5). Vanilla `junkTurretGun` uses 90, which is **±45°**, not 90° of coverage.

**The limit filters target *acquisition*, not just aiming.** `findTarget` → `canHitEntity` →
`trackTarget`, and `trackTarget` returns false for anything outside the yaw or pitch window.
A turret genuinely does not notice an enemy behind it.

**The window is anchored to the entity's spawn rotation.** `EntityTurret.CenteredYaw` is read
in several places and **never written anywhere in the assembly**, so it stays 0 and the arc sits
symmetrically around the entity's forward vector. For a *placed* turret that is fine — the
player aims it. For a thrown box it is not, and the first implementation made it worse by
taking `owner.rotation.y` **at the moment of the explosion**, i.e. wherever the thrower happened
to be looking a second or more after the throw.

Both are fixed:

- **`YawRange="360"` on all six.** That yields `(-180, +180)`, and since `Mathf.DeltaAngle`
  only ever returns -180..180, every target passes. `AutoTurretYawLerp` clamps nothing — it
  simply lerps the head's `localRotation` to `BaseRotation.y + Yaw` — so this is safe
  mechanically. The remaining risk is cosmetic: whether the borrowed junk-turret model looks
  right doing a full revolution. If it does not, ~300 (±150°) is the compromise.
  `PitchRange` stays limited on purpose.
- **`TurretDeployer.ChooseFacing`** picks the initial heading: nearest awake `EntityEnemy`
  within 25 m, else the **throw vector** (landing position minus thrower position, which is
  geometrically fixed and does not drift while the box is in the air), else the thrower's
  facing. This still matters at 360° because the head has to physically traverse.

⚠ **Consequence for balance: `TurnSpeed` is now the main differentiator between turret types.**
The arc no longer separates them; how fast they can bring the gun round does. The sniper went
from 14 to 20 °/s for exactly this reason — at 14, a target directly behind cost thirteen
seconds of a 120-second lifetime before the first shot.

## Deployment

`TurretDeployer.Deploy` replays `ItemActionSpawnTurret.ExecuteAction` (IL_01b7..IL_0376)
field for field: `SetSpawnerSource(StaticSpawner)`, `factionId`, `factionRank - 1`,
`belongsPlayerId`, `OriginalItemValue`, `groundPosition`, `OwnerID`, `ForceOn`,
`AddOwnedEntity`, then `SpawnEntityInWorld`. **Omitting any one of them yields a turret that
stands there and does nothing, without a single log line.**

`OwnerID` is resolved server-side via `PersistentPlayerList.GetPlayerDataFromEntityID(id).PrimaryId`.
Vanilla uses `PlatformManager.InternalLocalUserIdentifier`, which is correct only on the
thrower's own client — our deployment hangs off `ExplosionServer` and runs on the server.

The throw→deploy hook is a **second, separate postfix** on `GameManager.ExplosionServer`
alongside the Seeker's. They filter on their own items and cannot see each other; merging them
would put the working Seeker path at risk for no gain.

## Lifetime lives in the ItemValue metadata

⚠ **This was built as a buff first, on a premise that is false.** The reasoning was "buff
durations are saved with the entity", which is true for players and for nothing else:

- `EntityAlive.Write` calls `Entity.Write` and then writes a single `Int32`. No buffs.
- `EntityBuffs.Write`/`Read` has exactly **two** callers in the whole assembly:
  `PlayerDataFile.FromPlayer`/`ToPlayer` and `NetPackageEntityStatsBuff`.

So a buff on a non-player entity does not survive a save. A turret that outlived a reload would
have lost its clock, `onSelfBuffFinish` would never have fired, and it would have stood there
forever — precisely the failure the buff was chosen to prevent.

⚠ **The bug is real; the symptom first cited for it was not.** `TurretTracker … loaded 2` in a
session with no turret thrown was read as two immortal turrets. It is not: `turrets.dat` holds
two ids that predate the whole module, and `TurretTracker.Save` logs
`saving {total} ({turretsActive} + {turretsUnloaded})` — both sat in `turretsUnloaded`, i.e.
two player-placed turrets in unloaded chunks. A live agent turret pushed the line to
`3 (1 + 2)` and back. The persistence defect stands on the `Write` evidence above and on
nothing else.

The clock now lives in `OriginalItemValue.Metadata`, which **is** persisted:
`EntityTurret.Write` serialises `OriginalItemValue` (IL_001d), and `ItemValue.Write` writes
`UseTimes`, `Quality`, `Meta` *and* the `Metadata` dictionary. `SetMetadata(string, float)` /
`TryGetMetadata(string, out float)` are a typed store meant for exactly this.

`UseTimes` would also have persisted and was the other candidate, but it feeds
`PercentUsesLeft`, which vanilla already uses to gate `IsOn` — that would have coupled two
mechanics that have no business being coupled.

Two details in `TurretLifetime` worth keeping:

- **Time comes from `Time.time` differences, not `Time.deltaTime`.** `OnUpdateEntity` is not
  guaranteed to be one call per frame. If it ran on a fixed 20 Hz tick at 60 fps, a sum of
  `deltaTime` would accumulate a third of real time and every turret would live three times as
  long — slowly enough to pass as "feels a bit long" in testing.
- **A turret with no clock in its metadata is retired on sight.** That covers turrets saved by
  the buff-based version and any future metadata loss. The alternative — assume a fresh
  lifetime — turns a lost clock into a turret that lives longer than it should, which is the
  bug we came from.

Consequence to be aware of: the clock only advances while the entity is loaded. A turret in an
unloaded chunk pauses. That is ordinary entity behaviour, and for a 60–120 s consumable it is
not worth engineering around.

## Pickup is blocked at `CanInteract`

`EntityTurret.CanInteract(int)` has exactly one caller in the whole assembly:
`AllowActivationCommand`, for the `"take"` command (IL_0015). Returning false there blocks
pickup and nothing else. `AllowActivationCommand` itself takes a `ReadOnlySpan<char>`; a ref
struct in a Harmony signature is needless risk when a caller with an `int` does the same job.

## Art: one prefab, six colours

All six entity classes `extends="junkTurretGun"` and keep its prefab. That prefab carries the
`Muzzle` node plus the `AutoTurretYawLerp`, `AutoTurretPitchLerp`, `Laser` and `Cone`
components that `EntityTurret`/`MiniTurretFireController` expect; a custom FBX would have to
reproduce all of them.

Visual distinction is free: `EntityTurret.OnUpdateEntity` reads `TintColor` from the **core
item's** properties and pushes it into `UpdateLight.SetTintColorForItem`
(IL_00d2..IL_0159). One prefab, one property, six recognisable turrets.

## Fire sound: audible, cause of the earlier silence not isolated

**Resolved for the Gun Sentry** — play test 2026-08-02 evening, ten turrets thrown over 2.5
hours, sound confirmed audible by ear, and `Patch_TurretFireProbe` reporting
`sound='Pistol_Fire' particle='gunfire_pistol' muzzle=ok` on every one of them.

⚠ **What made the difference was not established, and the obvious explanation is wrong.** The
silent turret was not missing its properties: it inherited `FireSound="junkturret_fire"` and
`ParticlesMuzzleFire="gunfire_junkturret"` from `junkTurretGun`, both valid. The audible one
sets `Pistol_Fire` / `gunfire_pistol` explicitly. Two differences between those sound nodes
could matter and neither was isolated — `junkturret_fire` hangs on
`AudioSource_JunkWeaponFire.prefab` (a different 3D rolloff, not inspectable offline) and caps
at `MaxVoices=5` against the pistol's 30. A third possibility is that it was quietly audible
all along and got missed at 10 % master volume. Explicit per-type sounds were required by the
design anyway, so this was not chased further.

The path itself was never broken, and walking it in IL was the only way to be sure of that:

```
Fire() -> if (!IsNullOrEmpty(muzzleFireParticle)) { new ParticleEffect(…, fireSound, …);
          FireControllerUtils.SpawnParticleEffect(pe, -1) }
       -> GameManager.SpawnParticleEffectClient -> ParticleEffect.SpawnParticleEffect
       -> PlaySoundInClient -> Audio.Manager.Play(pos, name, -1, false, vol)
```

Inside `SpawnParticleEffect` the sound is played *before* any particle resolution
(IL_0000..IL_004e), so an invalid particle name cannot swallow it — but an **empty** one can,
because then the whole block in `Fire` is skipped. Never leave `ParticlesMuzzleFire` blank on a
turret that should be audible.

Two things were ruled out along the way and are worth not re-testing: `volumeScale` is a
hard-coded `1` at the call site (IL_0547), and `-1` as the causing-entity id is what vanilla
itself passes (IL_0556) — the sound is played before that value is even looked at.

`Patch_TurretFireProbe` stays on until the other five types have been heard; it logs the
runtime sound, particle and muzzle once per turret, which is one line instead of a round of
hypotheses. `DBG` goes to `false` before release.

**Flame effect — needs a look, not an argument.** `ParticlesMuzzleFire` is spawned *per shot*,
and `FireSound` rides along on the same call. That is not a continuous jet. At 240 rounds per
minute the flame turret produces four bursts a second, which may read as a pulsing flamethrower
or as a strobe. If it looks wrong, the fix is a looping particle plus `flametrap_lp` started and
stopped from C# while the turret fires, and both properties move to empty.
