# Changelog

## Unreleased

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

**Play-tested so far:** the Gun Sentry only, but thoroughly — ten thrown over a 2.5 hour
session, all ten retired cleanly (seven on an empty magazine, three on lifetime), full-circle
traverse confirmed, and the fire sound now audible. ⚠ *Why* it was silent before was never
isolated: it had inherited a valid `junkturret_fire` all along, and the explicit per-type sound
that fixed it was going in regardless. The other five types have not been thrown, and the
flamethrower's per-shot flame effect still needs a look. Details in
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
