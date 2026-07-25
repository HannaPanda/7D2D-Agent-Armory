# Changelog

## [Unreleased]

### Added
- **Agent Armory** — project renamed from "Cluster Seeker"; the seeker is now one module of a
  planned gadget set (chem launcher, throwable turret, disposable gun drone, hive).
- Per-tick step limiter (`Patch_SeekerStepLimiter`) that binds actual movement speed to the XML
  `MoveSpeedAggro`. Without it the drones moved up to 22 m/s at a configured 2.2.

### Changed
- Collision capsule authored in real metres in the prefab (r 0.24 / h 0.55 / centre 0.275) and
  `SizeScale` raised to 1.0 (child) / 1.818 (pod). `SizeScale` below 1 provably breaks the
  capsule — see docs.
- Child speed 2.7 → 4.0, pod 2.2 → 6.5, both within the tested step/radius budget.
- Child lifetime 10 s → 18 s: seekers were despawning mid-chase against fast zombies.
- Children now spawn in a tight ring around the pod instead of at a searched spawn point.

### Removed
- `Patch_SeekerSpeedGovernor` — scaled `GetPassiveEffectSpeedModifier`, which turned out to
  govern a code path the drones do not use. Measurably no effect.
