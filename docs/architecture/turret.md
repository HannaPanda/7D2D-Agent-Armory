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

This is how the flamethrower and the shock turret get their exotic effects, and neither needed a
line of C#.

### ⚠ Being too careful with a vanilla buff cost the shock turret everything

The shock turret spent two rounds on **our own** `buffAgentShock` rather than vanilla
`buffShocked`, on the reasoning that the latter decides its damage from foreign context:
`ItemHasTags tags="stunBaton"`, `perkIntelMaster5` and the CVar `_droneStunDamage`. The caution
was misplaced, and reading the buff instead of skimming it would have shown that:

- The line that sets the damage first is **unconditional** — `$buffShockedDamage = -5`, the
  "Not a Stun Baton" case. All three raises sit behind requirements.
- `ItemHasTags` tests the attacking item, and our turret core carries no `stunBaton` tag.
- `perkIntelMaster5` and `_droneStunDamage` are CVars on the **buffed** entity, i.e. on the
  zombie. A zombie has neither.

So in our context `buffShocked` is a flat −5/s with no foreign influence at all — and it brings
what a hand-written buff would have to reproduce: a **90 % slow decaying to 5 %** over the buff's
duration (vanilla `buffs.xml` from 6763, gated `$shockDurationMax GTE 4`, and `GetBuffDuration`
fills that CVar from the buff's own `duration value="4"`), the `p_electric_shock` particle on
Spine1, and `electric_fence_impact`.

It also brings a **self-limiter** worth more than the effect itself: when the shock ends, vanilla
adds `buffShockImmune` — 5 s, `BuffResistance base_set 2`, and `HasImmunity` clamps that to 0..1
and rolls against it, so it is total immunity. `ItemActionAttack.Hit` passes `_netSync = true`
(IL_0e4c), which is what makes `AddBuff`'s immunity check apply at all (IL_004C). The result is
4 s shocked, 5 s clear, per target — a turret that cannot stunlock, without a line of our own
tuning.

`buffAgentShock` is gone. `buffHazard_Slow` went with it: `buffShocked` slows without a player
gate, so it covers the player walking into their own beam too.

The rule this leaves behind is not "always use the vanilla buff" but: **read the buff, then
decide.** The first mistake below was borrowing one without reading it; this one was refusing one
without reading it. Same missing step.

### ⚠ A vanilla hazard buff is not a zombie buff

Both turrets originally borrowed the pipe-hazard pair `buffBurningHazardPlayer` +
`buffHazard_Slow`, on the reasoning that the vanilla fire traps use them and they therefore
needed no balancing of our own. **"Player" in that name is literal, and the reasoning was
wrong.**

- `buffHazard_Slow` has exactly one `effect_group`, and all of it sits behind
  `<requirement name="EntityTagCompare" tags="player"/>` (vanilla `buffs.xml` from 10515). On a
  zombie the buff does **nothing** — not a little, nothing.
- `buffBurningHazardPlayer` starts `buffHazardBurningElement`, whose non-player branch is a
  single `HealthChangeOT base_subtract 2` (vanilla 7130..7136).

So "the damage per hit is small because the effect is in the buff" was not a tight balance
number, it was a design built into a branch vanilla leaves nearly empty. The flame turret ran at
~36 damage/s against blood-moon enemies with several hundred health, and the shock turret — whose
*only* job is crowd control — never slowed a zombie at all.

The fix for the flamethrower is `buffAgentFlameBurn`, gated `!EntityTagCompare tags="player"` —
exactly the branch vanilla leaves open. The two vanilla buffs **stay on that entity class**: they
handle the player case properly (screen effect, 10 HP/s, mobility) and the burning *visual* via
`buffIsOnFire`. The shock turret took the other route and dropped both in favour of `buffShocked`
(above).

The general rule, worth applying before borrowing any vanilla buff: **read its
`effect_group` requirements before assuming it works on your target.** A hazard, a trap and a
weapon are three different audiences.

### Slowing a non-player is possible, and this is the proof

`perc_subtract` on `WalkSpeed`/`RunSpeed` is the mechanism that provably reaches a zombie.
`EntityAlive.MoveEntityHeaded` is the **only** caller of `GetPassiveEffectSpeedModifier`, and that
reads PassiveEffects 133/134/135 (WalkSpeed/RunSpeed/CrouchSpeed) on every `EntityAlive`, not just
on players. Vanilla does the same in `buffInjuryStunned00` and puts its `!player` reservation only
on `AttacksPerMinute` — which it would not need if the buff never reached non-players.

### Buff damage earns no XP

`AddBuff` *does* record an instigator (`Hit` passes the attacker's `entityId`, IL_0e47) — the
problem is one layer down: `MinEventActionModifyStats` with `stat="Health"` goes straight to
`Stat.set_Value` (IL_01B4..IL_01C2). No `DamageSource`, no `DamageEntity`, so nothing the kill-XP
path could read even in principle. Same class of problem as the seeker's detonation (see
`MinEventActionSeekerExplode`).

That is why the flame turret's burn is deliberately sized as an after-burner (8 HP/s for 4 s) and
the bulk of every turret's output sits in **ray damage**, which is attributed to the thrower
correctly.

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

## The flame turret does its own muzzle effect

Play test 2026-08-03: the flame turret produced **an unbounded, growing pile of flames** that
stayed in the world after the turret was gone. Not a strobe, not a balance question — a leak. The
per-shot spawn was the wrong mechanism from the start, and three properties of vanilla's call
explain why. From `MiniTurretFireController.Fire`, IL_0503..IL_055C:

```
if (!IsNullOrEmpty(muzzleFireParticle))
    FireControllerUtils.SpawnParticleEffect(
        new ParticleEffect(muzzleFireParticle, Muzzle.position + Origin.position,
                           Muzzle.rotation, 1f, Color.white, fireSound, null, 1f, ""), -1);
```

1. **The `-1` disables the recycler.** `ParticleEffect.SpawnParticleEffect` keeps a per-entity
   list and, past `cEntitySameParticleMax` copies of the same particle, repositions the oldest
   instead of adding another — but only when it is given a real entity id. IL_006F takes `-1`
   straight to the instantiate path. There is no cap.
2. **Cleanup is the prefab's own job.** `gunfire_pistol` is a one-shot and disposes of itself.
   `flame_hazard` is the pipe-hazard burner (`pipeFireHazard*` in vanilla `blocks.xml`) and runs
   forever by design. Four rounds a second therefore meant four eternal flames a second.
3. **No parent means no owner.** With no parent transform the copy is attached to the global
   `ParticleEffect.RootT` (IL_0377) at one moment's world position and rotation. It does not
   turn with the barrel, and nothing destroys it when the turret goes.

`TurretFlameJet` replaces it with **one** copy, created on the first shot as a child of the
`Muzzle` transform — the same mechanism vanilla's own `AttachParticleEffectToEntity` uses for
persistent particles on entities (IL_029A: `SetParent(parent, false)` plus a local offset and
rotation). Being a child settles three problems at once for free: it turns with the barrel, it
survives an origin shift, and it is destroyed with the turret's model. A leftover after teardown
is now structurally impossible rather than merely handled.

On/off is driven from the shot itself: the `Fire` postfix stamps a timestamp, the per-frame tick
stops emission once `LingerSeconds` (0.45 s) have passed without one. That threshold has to stay
above the shot interval — at 240 rounds per minute that is 0.25 s — or the jet flickers at the
fire rate, which is the strobe this was supposed to avoid. `StopEmitting` rather than
`StopEmittingAndClear`, so the flame burns out instead of blinking off.

**Ammunition already behaves the way a fuel tank should.** Vanilla decrements `AmmoCount` once
per `Fire` (IL_0612..IL_0624), and `Fire` only runs while there is a target — so fuel drains
while burning and holds while idle, with no extra code.

Vanilla's per-shot spawn is switched off in `Patch_TurretFlameJet` by blanking the controller's
`muzzleFireParticle` field, not by an empty XML property. The XML route would depend on how
`DynamicProperties` resolves an empty value against the one inherited from `junkTurretGun` — an
assumption, where the code is provable: IL_0503 tests `IsNullOrEmpty` and skips the whole block.
That block holds the fire *sound* too, which is why `agentTurretFlame` carries no `FireSound`;
`flametrap_lp` is started and stopped as a loop instead (`Loop="true"`, vanilla
`sounds.xml:12535`), with `flametrap_start` / `flametrap_end` on the edges.

The looping sound is the one part that does **not** die with the model — it hangs on the entity id
in the audio manager. `TurretRetire` stops it explicitly, and the tick stops it as well when it
finds the particle transform gone, which covers teardown paths we never see (chunk unload).

### The offset counts from the nozzle, because the prefab's root is not its nozzle

Play test 2026-08-03, second round: one flame, turning with the barrel — and floating roughly a
metre and a half **above** the muzzle.

`flame_hazard` is authored for a *block*. Its root sits at the block origin and the burner sits
higher up, where the pipe's opening is. Parent the root to `Muzzle` and the visible flame lands
off by exactly that internal offset.

`AlignEmitter` **measures** it instead of guessing: after `SetParent` the root is at
`localPosition = 0`, so `muzzle.InverseTransformPoint(nozzle.position)` is the prefab-internal
offset already turned by the configured rotation and scale. Setting the root to
`wantedOffset - thatVector` puts the **nozzle** on `wantedOffset`. `AgentFlameJetOffset` therefore
means what its name says — where the flame starts, measured from the muzzle.

Measuring rather than hand-entering `0,-1.5,0` matters for a specific reason: a hand-entered value
is calibrated against one eyeball estimate and goes wrong again the next time TFP touches the
prefab. The measured one is correct by construction.

The one assumption left is *which* particle system is the nozzle when the effect has several
(flame, sparks, smoke): the first with emission enabled wins. If the jet ever sits wrong again, the
measured number in the log (`[AgentArmoryFlame]`, "Duesenversatz im Prefab") says whether the wrong
system won, and `AgentFlameJetAutoAlign="0"` turns the correction off for manual work.

✔ **Confirmed in play 2026-08-03, and the geometry is now settled — do not "fix" it further.**
`AgentFlameJetRotation="0,-90,0"` was inferred (the hazard blocks lay their damage volume out
towards +X from the particle offset, so the prefab emits along its local +X; a muzzle transform
faces +Z) and the play test confirms it: one flame, out of the muzzle, turning with the barrel.

There is a **visible gap between the nozzle and where the flame begins**, and it is deliberate:
the mod author kept it because a real flamethrower has one. It is a residue of the auto-alignment
anchoring on the first emitting system rather than on the exact tip, i.e. it comes out of the
prefab, not out of `AgentFlameJetOffset`. Anyone tempted to close that gap in a later round should
read this sentence first.

### Damage: what the flame turret is measured against

The reference point is the MG turret, because that is the one that visibly works: 250 rounds of
7.62 HP at 50.5 each is a **12 625** budget over the box's life, ~420 damage/s single target at up
to 22 m.

The flame turret is deliberately put on the same total budget by a different route: 15 per ray hit,
3 rays, 240 rounds/minute — up to 180 damage/s and **13 500** over 300 units of fuel. What it pays
for that is range (6 m, with falloff from 5 m, against the MG's 22 m); what it gets back is that
one jet hits several targets and leaves them burning and slowed.

✔ Confirmed in play 2026-08-03: "ordentlichen Schaden", and the 6 m turned out to be a **feature**
rather than a cost — the turret's role is a chokepoint weapon, placed in front of a doorway a horde
is funnelled through. Worth keeping in mind before anyone extends its range to make it more
"competitive": the short reach is what gives it a role the MG turret does not have.

The shock turret sits at roughly half that budget on purpose: 40 per hit, 120 rounds/minute, 150
cells — 80 damage/s and **6 000**. It buys the difference back in control (see `buffShocked`
above), and its 150 cells at 2 rounds/s come to exactly the 75 s lifetime, so tank and clock run
out together instead of the turret vanishing with half a magazine.

`MaxDistance` is the number that governs engagement, and it governs both halves of it: `Init`
reads the entity-class property into the `maxDistance` field (IL_013A..IL_0163), and `findTarget`
uses that same field for the acquisition bounds *and* for the line-of-sight ray (IL_0077,
IL_013F). So a short-range turret does not waste ammunition firing at targets it cannot reach —
worth knowing, because it rules out the other obvious explanation for "it kills nothing".

⚠ **Multiplayer: the jet is host-only.** `Fire` returns early on a client (IL_0000), so the
trigger never arrives there, and an `Object.Instantiate` is not replicated the way
`SpawnParticleEffectServer` is. Making it show on remote clients needs the effect driven from a
replicated signal — a buff with `AttachParticleEffectToEntity`, or a net package. Untested either
way; nothing else in this module has been exercised in multiplayer.
