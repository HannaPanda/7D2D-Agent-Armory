# Changelog

## Unreleased

**The shock turret is now a crowd-control turret: it round-robins through the nearest un-shocked
zombies.** Requested after the play test — "das könnte mehr ein Crowd Control Turret werden, wo der
Schaden irrelevant ist". The implementation is one Postfix on `shouldIgnoreTarget` that reports an
already-shocked or shock-immune target as ignorable, and vanilla supplies the rest:
`Update` drops the current target the frame it becomes ignorable and `findTarget` calls
`shouldIgnoreTarget` as its only per-candidate filter over a distance-sorted list — so the next pick
is automatically the nearest zombie that is not yet shocked, with vanilla's own line-of-sight check
and turn handling. A custom target selector would have produced the same order while
re-implementing four things that already work.

- `buffShockImmune` counts as "done" alongside `buffShocked`: during that window `AddBuff` rejects
  the buff, so shooting such a target cannot land a shock and is pure waste.
- **With every zombie in range shocked, the turret stops firing** — the whole fire block hangs on
  `hasTarget`, so it self-throttles to the rate at which there is something new to tag. Fewer shots
  fall out of the targeting, which is what "seltener schießen" actually needed.
- ⚠ **The fire rate stays at 120, because lowering it would make this worse.** `fireRateTimer` keeps
  running while there is no target and is only reset by a shot, so the first shot after each switch
  falls mid-turn, where vanilla does not wait for the barrel to line up. A high cadence supplies the
  next attempt quickly; a low one shrugs off the miss and waits. `TurnSpeed` is the knob that
  reduces the waste, and it goes 32 → **100**. Cells 150 → **240** to cover the switching cost.
- Damage stays at 40 and is now explicitly a side effect, not a design goal.

On the observation that the 40 only seemed to tick on the first hit: **no mechanism for that is
provable in the code.** `Fire` recomputes `GetDamageEntity` and calls `Hit` per ray per shot,
independent of any buff, and the only per-target skip in it is *within a single shot* — a
penetrating round will not hit the same entity twice in one chain (IL_039F..IL_03D9). Rather than
invent a cause, `[AgentArmoryShock]` now logs each target hand-off, which will show the real pattern
next session. The round-robin makes the question mostly moot anyway: one hit per target per cycle is
now the intended behaviour.

**The shock turret shocks now — and it was talking at you.** Play test 2026-08-03: "das macht ja
gar nix. Das redet die ganze Zeit, trifft mit blauem Schaden, aber nur super wenig und schockt
nicht." Three separate defects, all confirmed by reading rather than guessing.

- **It was literally talking.** `FireSound` was `drone_attack_stun`, which sounds like an
  electrical name and is not: it hangs on `AudioSource_VO_Drone.prefab` and plays clips from
  `Sounds/VO/Drone/` (vanilla `sounds.xml:18639`). **VO is voice-over** — the turret played a drone
  voice line once per shot. Now `stunbaton_hit4`, a single electrical impulse that sits tonally
  below the `electric_fence_impact` the shock buff plays on the target.
- **It never shocked, because avoiding vanilla `buffShocked` was a mistake.** That buff was skipped
  on the grounds that its damage depends on foreign context (`stunBaton` tags, `perkIntelMaster5`,
  `_droneStunDamage`). Reading it: the unconditional line sets `$buffShockedDamage = -5` first, all
  three raises sit behind requirements we never meet — `ItemHasTags` tests the *attacking* item and
  our core carries no such tag, and the two CVars live on the *shocked* entity, i.e. the zombie. So
  it is a flat −5/s here, and it brings a 90 % slow decaying to 5 %, the `p_electric_shock`
  particle and a sound, all of which the hand-written replacement lacked.
  It also brings a self-limiter: `buffShockImmune` (5 s, total immunity, since `HasImmunity` clamps
  `BuffResistance 2` to 1) applies because `Hit` passes `_netSync = true`. 4 s shocked, 5 s clear,
  per target — no stunlock, no tuning of ours.
- **Damage from 6 to 40** per hit, 90 → 120 rounds/minute, 60 → 150 cells: 80 damage/s and a 6 000
  budget, about half the MG and flame turrets, paid back in control. The 150 cells at 2 rounds/s
  come to exactly the 75 s lifetime.
- `buffAgentShock` and its localization are **removed**, and `buffHazard_Slow` with them —
  `buffShocked` slows without a player gate, so it covers the player in their own beam too.

The lesson is not "prefer vanilla buffs". Two rounds earlier the flamethrower broke by borrowing a
vanilla buff *without reading it*; this one broke by refusing one without reading it. Same missing
step, opposite direction.

**The flame turret is confirmed good.** Play test 2026-08-03: "ordentlichen Schaden", flame out of
the muzzle, turning with the barrel, and the short 6 m reach turned out to be the point rather than
a cost — it is a chokepoint weapon, placed in front of a door a horde is funnelled through. The
visible gap between nozzle and flame start is **kept on purpose** (a real flamethrower has one), so
it is now documented as intended and not as a residual defect.

**The flame turret does real damage now, and the shock turret finally slows anything.** Play test
2026-08-03: the flamethrower took forever to kill anything while the MG turret with HP ammunition
cut through blood-moon hordes. This was not a balance number set too low — it was a design built
on a false premise. The flamethrower kept its damage per hit small on purpose, because "the effect
is in the buffs", and the two buffs it borrowed from the vanilla pipe hazards are **for the
player**:

- `buffHazard_Slow` has exactly one `effect_group` and all of it sits behind
  `<requirement name="EntityTagCompare" tags="player"/>` (vanilla `buffs.xml` from 10515). Against
  a zombie it does nothing at all.
- `buffBurningHazardPlayer` starts `buffHazardBurningElement`, whose non-player branch is a single
  `HealthChangeOT base_subtract 2` (vanilla 7130..7136).

So the flame ran at ~36 damage/s against enemies with several hundred health, and the shock
turret — whose entire purpose is crowd control — had never slowed a zombie.

- **`buffAgentFlameBurn`** fills the branch vanilla leaves open (`!EntityTagCompare tags="player"`):
  8 HP/s for 4 s after the last hit, plus a 40 % slow. `stack_type replace`, because a turret that
  lands up to twelve hits a second must reset a timer, never stack an effect.
- **`buffAgentShock`** gains the same kind of group with a 50 % slow — harder, because slowing is
  the only thing that turret does.
- The two vanilla buffs **stay** on both entity classes: they handle the player case correctly
  (screen effect, 10 HP/s, mobility) and the burning visual via `buffIsOnFire`.
- **`ammoAgentFlameFuel` goes from 3 to 15 damage per ray hit**, which puts the flame turret's
  budget over one box at 13 500 against the MG turret's 12 625 — deliberately level with the
  weapon it is measured against. It pays for that in range: 6 m with falloff from 5 m, against the
  MG's 22 m, in exchange for hitting several targets per jet.
- The bulk of the output stays in **ray damage rather than the burn** on purpose: a buff has no
  instigator the kill-XP path can read, so burn kills would earn the thrower nothing.

`perc_subtract` on `WalkSpeed`/`RunSpeed` is the slow that provably reaches a zombie:
`EntityAlive.MoveEntityHeaded` is the only caller of `GetPassiveEffectSpeedModifier`, which reads
PassiveEffects 133/134/135 on every `EntityAlive`. Vanilla's `buffInjuryStunned00` does the same
and reserves only `AttacksPerMinute` for non-players.

**The flame now comes out of the muzzle instead of floating above it.** Same play test: one flame,
turning correctly, but about a metre and a half too high. `flame_hazard` is authored for a block —
root at the block origin, burner up where the pipe's opening is — so parenting the root to `Muzzle`
put the visible flame off by that internal offset. `TurretFlameJet.AlignEmitter` now **measures**
the offset (`muzzle.InverseTransformPoint(nozzle.position)` while the root sits at zero) and
subtracts it, so `AgentFlameJetOffset` means where the flame starts, from the muzzle. A hand-entered
`0,-1.5,0` would have been calibrated to one eyeball estimate and would break the next time the
prefab changes. `AgentFlameJetAutoAlign="0"` switches the correction off; the measured value is in
the log as "Duesenversatz im Prefab".

⚠ Which particle system counts as the nozzle is still an assumption — the first with emission
enabled — and the jet's **forward direction** is still the inferred `0,-90,0`. The play test
confirms it turns with the barrel and that there is one of it, not that it points forward.

**The flame turret now burns one flame instead of accumulating them.** Thrown into a fight it
produced a growing pile of flames at fixed world positions that stayed there after the turret
itself was gone. Cause, not guesswork: `MiniTurretFireController.Fire` spawns the muzzle effect
once *per shot* and passes `-1` as the causing entity, which is exactly the value that switches
off vanilla's own per-entity recycling cap (IL_006F). Cleanup is left to the prefab — fine for
`gunfire_pistol`, which is a one-shot, and fatal for `flame_hazard`, which is the pipe-hazard
burner and runs forever. Four rounds a second meant four eternal flames a second, and with no
parent transform each one was pinned to the global particle root at one moment's position.

- **Fix: `TurretFlameJet` makes one copy, as a child of the `Muzzle` transform**, using the same
  mechanism vanilla's `AttachParticleEffectToEntity` uses for persistent particles on entities.
  Being a child means it turns with the barrel, survives an origin shift, and is destroyed with
  the turret's model — a leftover after teardown is now impossible rather than cleaned up.
- **Emission follows the shot:** on with the first round, off 0.45 s after the last. The delay has
  to exceed the 0.25 s shot interval or the jet flickers at the fire rate. Existing particles burn
  out rather than blinking off.
- `flametrap_lp` runs as a **loop** (one start, one stop) with `flametrap_start` / `flametrap_end`
  on the edges, instead of re-triggering `flametrap_start` four times a second.
- Vanilla's per-shot spawn is switched off by blanking the controller field, not by an empty XML
  property, because IL_0503 tests `IsNullOrEmpty` and skips the block provably — where an empty
  XML value would only *probably* override the one inherited from `junkTurretGun`.
- Ammunition needed no change: vanilla already decrements it once per shot, and shots only happen
  while there is a target, so fuel drains while burning and holds while idle.
- Position, rotation, scale and all three sounds are `<property>` entries on the entity class,
  because one of them is a guess (see below) and a guess should cost an XML edit.

⚠ **The flame's rotation is inferred, not measured.** `flame_hazard` is taken to emit along its
local +X — read off the hazard blocks, which lay their damage volume out towards +X from the
particle offset — and `0,-90,0` turns that onto the muzzle's forward axis. If the jet comes out
sideways in play, `0,90,0` is the counter-test. Also **host-only in multiplayer**: `Fire` returns
early on clients, so the trigger never reaches them.

**A thrown charge now deploys where it lands, not where it grazes.** Standing on a narrow
ledge and throwing the Cluster Seeker down over the edge left the pod hanging in mid-air on
the tile you were standing on; only a throw aimed well away from you worked.

- **`ExplodeOnHit` does not mean "explodes on hitting the ground".** `EntityItem
  .OnCollisionEnter` sets `Entity.isCollided` for *any* contact that passes `CanCollide`, with
  no test of the contact normal or the speed — the normal/velocity arithmetic further down
  that method only decides whether an impact *sound* plays. `ItemClassTimeBomb.OnDroppedUpdate`
  then detonates on `bExplodeOnHitGround && isCollided`. Brushing the vertical face of the
  block you are standing on is a full trigger, and that position is what
  `GameManager.ExplosionServer` hands to the pod and turret spawners.
- **Fix: a new `ExplodeOnLandingOnly` property on the throwable**, honoured by
  `Patch_LandingOnlyCharge`. Only a contact the charge actually comes down *on* counts —
  contact normal at least 0.5 upward, **and not a creature**. Walls, edges and ceilings are
  ignored, so the charge falls past them and deploys below you. Set on the Cluster Seeker and,
  through `Extends`, on every turret box, where the same defect meant a grazed wall built the
  turret against it.
- **A body is not ground either.** Turret boxes had been landing on zombies' heads and
  building the turret up there — and that case passes an upward-normal test on its own, so the
  contact is additionally rejected when `GameUtils.GetHitRootEntity` resolves the struck
  collider to an entity (the same lookup the original method makes for its impact sound; it
  answers for `E_BP_*` body parts and `E_Vehicle`, and null for blocks and terrain). The charge
  slides off and deploys on the floor.
- The patch is a prefix that suppresses the whole handler, not a postfix that undoes
  `isCollided`: on a client that is physics master, `OnCollisionEnter` sends
  `PhysicsMasterSendToServer` immediately, and `NetPackageEntityPhysics.Setup` carries
  `isCollided` as a flag bit — a postfix would come too late and the server would still
  detonate the charge in multiplayer.
- **There was never a fuse as a safety net**, despite a comment in `items.xml` that said so.
  `explodeAfterTicks` is only ever armed by `FuseStartOnDrop` (set on neither vanilla parent)
  or by `FusePrimeOnActivate` (deliberately off here), so `Meta` stays 0 and every tick returns
  immediately. The collision is the only trigger there is. Adding `FuseStartOnDrop` would be
  the wrong repair — it also arms a charge merely dropped out of the inventory.

Headless on 3.0.0, 3.0.1 and 3.1.0: mod loaded, 8 Harmony patches (up from 7, so the new
target resolves on all three), 0 ERR / 0 EXC / 0 XML problems. The behaviour itself is not
covered by that — a headless run throws nothing.

**New module: throwable turrets.** A box you throw like a shaped charge; it deploys an
auto-turret where it lands. Single use — fixed ammunition, a lifetime, and no picking it back
up. Six variants across eight craftable boxes, each gated behind and scaling with the weapon
perk that matches its ammunition.

| Box | Ammunition | Perk | Rounds | Lifetime |
|---|---|---|---|---|
| Gun Sentry | 9mm HP / AP | Gunslinger | 120 | 90 s |
| MG Sentry | 7.62 HP / AP | Machine Gunner | 250 | 75 s |
| Shotgun Sentry | 12 ga | Boomstick | 24 | 90 s |
| Sniper Sentry | 7.62 AP | Dead Eye | 20 | 120 s |
| Flame Sentry | fuel | Turrets | 300 | 60 s |
| Shock Sentry | cells | Electrocutioner | 60 | 75 s |

- **A turret's stats live in a second, hidden item, not in its entity class.**
  `ItemValue.ModifyValue` resolves ammunition through `Actions[0] as ItemActionRanged ->
  MagazineItemNames[SelectedAmmoTypeIndex]`, so the carrier needs an `Action0` of class
  `Ranged` — which a throwable, with `ThrowAway` in that slot, cannot have. Hence one core
  item plus one box per variant; the HP and AP boxes differ only in the ammo index.
- **The perk tag both unlocks and scales.** `GetDamageEntity` passes the *owner* into
  `EffectManager.GetValue`, so the thrower's perk ranks apply to the turret. For 7.62 the tag
  is mandatory rather than a bonus: that ammunition gates its base damage on
  `perkDeadEye,perkMachineGunner`, and without one of them a turret fires for zero.
- **Flamethrower and shock turret are pure XML.** The `Buff` property on the entity class
  feeds `buffActions` into `ItemActionAttack.Hit`, which applies them to whatever is hit. The
  flamethrower reuses the vanilla hazard buffs; the shock turret brings its own rather than
  inherit `buffShocked`, whose damage is decided by the player's stun-baton perks.
- **Recipes unlock from combat perks**, which vanilla never does — every `RecipeTagUnlocked`
  in the game sits in a `crafting_skill`. It works because `Progression.ModifyValue` iterates a
  flat index over all progression values with no filter on their kind. Six schematics remain as
  the second route for anyone adding the mod to a running save.
- **The lifetime clock lives in the turret's item metadata**, after the first attempt put it
  in a buff on the strength of an unchecked claim. Buffs on a non-player entity are *not*
  saved: `EntityAlive.Write` writes no buffs, and `EntityBuffs.Write` has exactly two callers
  in the assembly, `PlayerDataFile` and a net package. A turret that outlived a reload would
  have lost its clock and stood there forever - the very failure the buff was chosen to
  prevent. `EntityTurret.Write` does serialise `OriginalItemValue`, whose `Metadata` dictionary
  is a typed, persisted key-value store meant for exactly this. (The log line first taken as
  proof that this had already happened turned out to be two player-placed turrets in unloaded
  chunks; the defect rests on the `Write` evidence, not on that.)
- **Turrets cover a full circle.** `YawRange` is halved on parse, so the inherited 90 was
  plus/minus 45 degrees - and the limit filters target *acquisition*, not just aiming, so a
  turret genuinely did not notice anything behind it. At 360 every target passes, since
  `Mathf.DeltaAngle` only returns -180..180. The initial heading now comes from the nearest
  enemy, or failing that the throw vector, rather than wherever the thrower happened to be
  looking when the box landed. `TurnSpeed` is the main difference between the types now.
- **Empty ammunition only switches a vanilla turret off; ours despawns.** Likewise the 10 m
  owner leash (`JunkTurretActiveRange`) and the active-turret counter are raised or bypassed —
  the latter matters because `perkTurrets` sets the counter with an untagged `base_set 0`,
  which would otherwise give a player *with* the turret perk fewer turrets than one without.
- Two defects the multi-version test caught before they ever ran: **every item needs a
  `Meshfile`**, even one that never reaches an inventory — and the resulting parse failure
  drops the whole `items.xml`, so the visible error names an item that is perfectly fine. And
  the timed charge this box extends has **no `ExplodeOnHit` and `StickPercent=1`**, so without
  correction the box would have stuck to walls and built its turret in mid-air.

**Play-tested so far:** the Gun Sentry thoroughly — ten thrown over a 2.5 hour session, all ten
retired cleanly (seven on an empty magazine, three on lifetime), full-circle traverse confirmed,
fire sound audible. ⚠ *Why* it was silent before was never isolated: it had inherited a valid
`junkturret_fire` all along, and the explicit per-type sound that fixed it was going in
regardless. The **flamethrower is confirmed working end to end** after three rounds of fixes —
damage, flame position, one flame, rotation, sound. The **shock turret has been thrown twice** and
was rebuilt twice; it is now a different weapon (crowd control by round-robin) and **nothing of that
rebuild has been seen**. MG, shotgun and sniper have never been thrown at all. Details in
`docs/architecture/turret.md`.

## 0.9.0 - 2026-08-01 (Beta)

First public release, and a **beta on purpose**: the Cluster Seeker plays the way it should,
but it has only been played on two game builds and one known cosmetic defect is shipping with
it (see below). One module so far: the **Cluster Seeker** - a throwable pod that lands,
follows you, and bursts into ten autonomous drones the moment enemies come close.

- **The Cluster Seeker now appears in the Explosives skill screen**, as a fourth item on the
  existing Tier 3 row. Two rounds to get there, and the first answer was wrong:
  - A **sixth `display_entry` is never drawn.** `XUiC_SkillCraftingInfoWindow.UpdateSkill`
    iterates the *widgets* and pulls data by index (`entry.Data = DisplayDataList[index]`),
    and the layout supplies exactly five: `windows.xml`, `windowSkillCraftingInfo`,
    `<grid rows="5" cols="1" repeat_content="true">`. `craftingExplosives` already fills all
    five with T1..T5, so an appended entry falls off the end - silently, no error, no gap.
  - Fixed by **extending the existing tier-3 entry** instead of appending a new one, which
    also happens to be where the Seeker belongs. `icon`, `name_key` and `unlock_level` are
    position-matched comma lists and `unlock_tier` is the 1-based index into them.
  - Corrected on the way: **`display_entry icon` is an icon-atlas *sprite* name, not an item
    name.** Vanilla proof in that very row - `resourceGunPowderBundle` carries
    `CustomIcon="resourceGunPowder"` and the entry lists `resourceGunPowder`. True, but it was
    not the cause; the row count was.

- **Throw it and nothing else.** No pin to pull, no timer to set, no right-click to arm. The
  charge goes off on impact and the pod is out.
  - `FusePrimeOnActivate="false"` makes `ItemClassTimeBomb.OnDroppedUpdate` skip its
    `Meta > 0` check entirely, and the inherited `Action1` is dropped through the `Extends`
    exclusion list, so there is no arming action left to trigger by accident.
- **The pod waits with you.** With nothing in range it follows you around instead of sitting
  where it landed, so a pod thrown early is not a pod wasted.
- **Ten seekers per pod**, each chasing its own target for up to 18 seconds and detonating on
  contact for 180 damage in a 4 m radius. They spawn in a ring around the pod and split the
  available targets between them instead of all piling onto the nearest zombie.
- **Two ways to learn it.** The **Explosives** crafting skill unlocks the recipe at **level
  35**, the same rank as the Timed Charge - and the **Cluster Seeker Schematic** still drops
  from loot and shows up at traders, so a lucky find gets you there sooner.
  - `Recipe.IsUnlocked` ORs a recipe-name CVar (the schematic) with a `RecipeTagUnlocked`
    passive effect (the skill), so the two routes coexist without either weakening the other.
- **Its own model, icon and sound.** A custom drone mesh in your hand, in flight and on the
  ground, a custom inventory icon, and a dedicated activation sound that plays at the pod in
  the second before it splits.
- **It is a delivery charge, not a bomb.** The throw itself does 1 damage and no block damage
  at all. Everything that happens comes from the drones, so it will not chew up your own base.
- **Localized into 13 languages** - EN, DE, ES, FR, IT, JA, KO, PL, PT-BR, RU, TR, ZH-Hans,
  ZH-Hant.

### Notes from development

- Per-tick step limiter (`Patch_SeekerStepLimiter`) binds actual movement speed to the XML
  `MoveSpeedAggro`. Without it the drones moved up to 22 m/s at a configured 2.2, because
  `MakeMotionMoveToward` ignores those values when `RootMotion=false`.
- Collision capsule authored in real metres in the prefab (r 0.24 / h 0.55 / centre 0.275) and
  `SizeScale` raised to 1.0 (child) / 1.818 (pod). `SizeScale` below 1 provably breaks the
  capsule: `Entity.SetCCScale` scales centre and height but pins the radius factor at 1.
- Child speed 2.7 -> 4.0, pod 2.2 -> 6.5, both inside the tested step/radius budget.
- Child lifetime 10 s -> 18 s: seekers were despawning mid-chase against fast zombies.
- Children spawn in a tight ring around the pod instead of at a searched spawn point.
- Removed `Patch_SeekerSpeedGovernor` - it scaled `GetPassiveEffectSpeedModifier`, a code path
  the drones never take. Measurably no effect.
- Removed the two pin-pull sound patches. The grenade "clink" had two independent sources (a
  `Sound_start` on Action1, inherited from another mod's patched contact grenade, and an
  AnimationEvent baked into the animation clip); dropping the arming step removed both at the
  root rather than suppressing them one at a time.
- The localization file was named `Localization.txt` and was therefore **never read**. 7DTD
  3.x only ever opens `Localization.csv` (`Localization.LoadPatchDictionaries`), and a missing
  file logs nothing at all - the give-away was that 266 mods appeared in
  `[MODS] Loading localization from mod:` and this one did not. Renamed, widened to the
  20-column header the game expects, and the release workflow now fails on a `.txt`, on a BOM
  and on a ragged row.
- `InitMod` logs the Harmony patch **count**. `PatchAll` returning proves nothing: a patch
  class that matches no target fails silently, which is exactly how a Harmony mod dies on a
  new game build. The test bench asserts the count is non-zero on every version.
- Debug logging in the Seeker classes is compiled out for release (`DBG = false`). Two files
  had no such flag at all and were still printing per-entity capsule and model-offset lines -
  eleven entities per throw, into everyone's log. `Patch_SeekerPhysics` and `Patch_SeekerRoll`
  now have one too. Their `LogWarning` calls stay **un**gated on purpose: a broken capsule, a
  rescued drone or a hit hard-lifetime are real fault states, and they are the only trace a
  bug report from a stranger would carry.
