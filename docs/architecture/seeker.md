# Cluster Seeker — architecture and hard-won engine facts

## Flow

```
thrownSeekerCluster (item)
   -> Harmony postfix on GameManager.ExplosionServer  (Patch_ExplosionServer)
   -> spawns entitySeekerCluster (the POD), carries belongsPlayerId for XP credit
   -> EAISeekerPod: LANDING -> SETTLE -> enemy scan -> ARM or FOLLOW
   -> buffSeekerClusterArm finishes
   -> MinEventActionSpawnSeekers spawns N entitySeekerChild in a ring around the pod
   -> EAISeekerDetonate per child: acquire, chase, detonate
   -> MinEventActionSeekerExplode credits the throwing player
```

## Engine facts this module depends on

These were all established by reading the IL of `Assembly-CSharp.dll`. They are not documented
anywhere and several of them cost multiple debugging rounds.

### `SizeScale` below 1 breaks the collision capsule

`Entity.SetCCScale(float scale)`:

```csharp
PhysicsTransform.localScale = Vector3.one;
center = cc.GetCenter() * scale;
height = cc.GetHeight() * scale;
float rf = Utils.FastMax(scale, 1f);          // never below 1
cc.SetSize(center, height, cc.GetRadius() * rf);
```

Centre and height scale with `SizeScale`; the **radius does not**. For any `SizeScale < 1` the
radius grows relative to the height by `1/scale`, and `KinematicCharacterMotor.ValidateData`
then clamps the height up to `radius * 2`. The result is a degenerate sphere whose hemisphere
centres coincide, so every ground probe and sweep returns nothing.

**Rule: author the model at true size and keep `SizeScale >= 1`.** The smaller entity is the
base at 1.0; larger variants use the ratio (here the pod at 1.818). Above 1 `FastMax` is a
no-op and all three values scale consistently.

### Capsule height and centre belong to the engine

Only the radius is a free parameter. Height and centre must satisfy `centre.y == height/2` and
`radius < height/2`. Setting an own scheme desynchronises the collider from `physicsHeight` and
`scaledExtent`, which the ground check reads — measured effect: `onGround` true in 0 of 52
samples, and pods rising 2–3 m while explicitly stopped.

### `MoveSpeed` does not control movement when `RootMotion=false`

`EntityAlive.MakeMotionMoveToward` evaluates its `minMotion`/`maxMotion` arguments **only** in
the `RootMotion == true` branch. Without root motion it just writes `moveDirection` and returns.
`Entity.Move` then normalises the direction, so the speed comes solely from
`GetPassiveEffectSpeedModifier() * 2 * (MovementRunning ? 0.35 : 0.12)` with base constants
`cPlayerSpeedModifierRunning = 1.6` / `Walking = 0.8`.

Consequence: `MoveSpeed` and `MoveSpeedAggro` feed pathfinding and the move helper's target
logic, not the actual displacement. This mod re-binds them via `Patch_SeekerStepLimiter`.

Switching to `RootMotion=true` is not a shortcut: the movement magnitude then comes from
`accumulatedRootMotion`, which the Animator feeds through `NotifyRootMotion`. A skeleton with
no root-motion animation clips supplies nothing.

### `Continue()` must be overridden

`EAIApproachAndAttackTarget.Continue()` compares the current target against the protected
`entityTarget` field, which only `base.CanExecute()` ever sets. A subclass that overrides
`CanExecute` without calling base must also override `Continue()`, or the task list ends the
task every tick — `Reset()` → `base.Reset()` → `moveHelper.Stop()` → `navigator.clearPath()`,
destroying every movement command in the tick it was issued.

## Known open issue

Something moves the drones directly, bypassing `Entity.motion` — measured single-tick
displacements up to **72 m** while horizontal motion read zero and the world origin had not
shifted. Ruled out: the collision capsule, origin shifting, the movement state, the base class
`Update` (not called), the rescue net (vertical only), and the roll visual (touches
`model.localPosition` only).

`Patch_SeekerStepLimiter` caps the symptom — it fires roughly 13 times per second per moving
drone, so the source is still active. If movement problems ever return, this is where to look;
the items in the ruled-out list are not worth re-testing.
