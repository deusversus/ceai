---
name: unity-il2cpp
description: >
  Expert knowledge for reverse engineering Unity games using the Il2Cpp backend. Covers
  GameAssembly.dll analysis, Il2Cpp metadata structures, pointer chain patterns, and
  field offset discovery. Load when the target is a Unity game.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - unity
  - il2cpp
  - gameassembly
  - mono
  - game-engine
triggers:
  - unity
  - il2cpp
  - gameassembly
  - GameAssembly.dll
  - mono
  - il2cppdumper
  - managed
  - C#
  - csharp
  - global-metadata
---

# Unity Il2Cpp Reverse Engineering

## Identifying a Unity Il2Cpp Game

Check for these files in the game directory:
- `GameAssembly.dll` — the compiled Il2Cpp binary (ALL game code is here)
- `global-metadata.dat` — type/method/field metadata
- `UnityPlayer.dll` — Unity runtime
- `<GameName>_Data/` folder with Unity assets

If `Assembly-CSharp.dll` exists and is NOT stripped → Mono backend (simpler, use dnSpy directly).
If `GameAssembly.dll` exists → Il2Cpp backend (this skill applies).

## Key Concepts

### Il2Cpp Compilation Pipeline
```
C# source → IL bytecode → Il2Cpp transpiler → C++ source → Native binary (GameAssembly.dll)
```
- All managed types become native C++ structs
- Method bodies become native functions
- Metadata (names, types) stored separately in global-metadata.dat
- Field offsets are FIXED per build (consistent until game update)

### Memory Layout of an Il2Cpp Object
```
Offset 0x00: Il2CppClass* klass     (pointer to class metadata)
Offset 0x08: MonitorData* monitor   (thread sync data)
Offset 0x10: [First instance field]  (actual object data starts here)
Offset 0x14: [Second field]
...
```
- Objects always start with a vtable/class pointer at offset 0
- Instance fields begin at offset 0x10 (16 bytes from object start)
- Field order follows C# declaration order (but may have alignment padding)

### Static Fields
- Stored in a separate static field area, NOT on the object itself
- Accessed via: `Il2CppClass → static_fields pointer → offset`
- Pattern: `GameAssembly.dll+XXXXX → pointer → static_fields → offset`
- Usually 2-3 pointer levels deep from GameAssembly.dll base

## Analysis Workflow

### Step 1: Identify the Module
```
InspectProcess → look for GameAssembly.dll in module list
```
Note the base address — all Il2Cpp code lives in this module.

### Step 2: Find Your Target Value
Use standard memory scanning (see memory-scanning skill). Once found:

### Step 3: Map the Object Structure
1. `DissectStructure` at the found address with a negative offset to find the object start
   - The object start has a valid pointer at offset 0x00 (klass pointer)
   - Instance fields begin at 0x10
2. Browse backward from your value's address to find the object base
3. Record the field offset (your value's address - object base address)

### Step 4: Build a Pointer Chain
Typical Il2Cpp pointer chain for instance fields:
```
GameAssembly.dll + StaticOffset → Ptr1 → Ptr2 → ObjectBase + FieldOffset → Value
```
1. `ScanForPointers` targeting the object base address
2. Look for chains rooted in GameAssembly.dll (these are stable across sessions)
3. The first pointer usually goes through a static class reference
4. `ValidatePointerPaths` to test stability

### Step 5: Verify Across Restarts
1. Save the pointer chain to the address table
2. Restart the game
3. `RefreshAddressTable` — if value resolves correctly, the chain is stable

## Common Unity Patterns

### Player Singleton Access
Many Unity games have a singleton player object:
```
GameAssembly.dll+XXXXXX → PlayerManager.instance → Player → HP
```
- The first offset is a static field reference to the singleton
- Stable across game sessions (only changes on game updates)

### MonoBehaviour Fields
Unity components derive from MonoBehaviour:
```
Component → GameObject → Transform → Position (float3)
Component → specific fields at known offsets
```

### List/Array Access
Il2Cpp generic collections (List<T>):
```
Offset 0x00: klass pointer
Offset 0x10: items (pointer to backing array)
Offset 0x18: count (int32)
```
Backing array:
```
Offset 0x00: klass pointer
Offset 0x10: length (int32)
Offset 0x18: [First element]
Offset 0x20: [Second element] (if elements are 8 bytes)
```

### Dictionary<K,V>
```
Offset 0x10: buckets array pointer
Offset 0x18: entries array pointer
Offset 0x20: count
Offset 0x28: freeList
```

## Tools Integration

### Using with External Tools
If the user has Il2CppDumper output:
1. The dump provides class/field names + offsets
2. Use these offsets directly with the klass pointer pattern
3. Verify with `DissectStructure` against live memory

### AOB Signatures in Il2Cpp
GameAssembly.dll code signatures are generally stable within the same build:
- `GenerateSignature` at the instruction address
- `TestSignatureUniqueness` to verify uniqueness within GameAssembly.dll
- AOBs break on game updates (recompilation shuffles code)

## ASLR Handling

GameAssembly.dll base address changes EVERY launch due to ASLR.
- Always use `module+offset` notation (e.g., `GameAssembly.dll+9A18E8`)
- `ResolveSymbol` converts this to a live address at runtime
- Pointer chains rooted in `GameAssembly.dll+offset` are ASLR-resilient
- Raw addresses (0x7FF...) are NOT stable across restarts

See `references/structures.md` for detailed Il2Cpp class/object memory layouts.
