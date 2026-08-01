# Changelog

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
