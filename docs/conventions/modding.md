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

Bundles are `UnityFS`-compressed: byte-scanning an exported `.unity3d` for a float will not find
it even when the value is definitely present. Verify bundle contents at runtime instead.

## Debug logging

Each module keeps a `DBG` constant. Flip these to `false` before release:

- `Seeker/EAISeekerDetonate.cs`
- `Seeker/EAISeekerPod.cs`
- `Seeker/MinEventActionSeekerExplode.cs`
