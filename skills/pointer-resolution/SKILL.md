---
name: pointer-resolution
description: >
  Expert workflows for building stable pointer chains that survive game restarts (ASLR).
  Covers pointer scanning, multi-level chain construction, stability validation, and
  rescan strategies. Load when building persistent address tracking or fixing broken pointers.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - pointers
  - ASLR
  - stability
  - chains
triggers:
  - pointer
  - chain
  - offset
  - stable address
  - base address
  - ASLR
  - dynamic address
  - restart
  - pointer scan
  - static pointer
  - green address
---

# Pointer Resolution — Stable Address Chains

## Why Pointers Matter

Game values live at **dynamic addresses** — they change every time the game starts (ASLR)
or when the game allocates new objects. A pointer chain provides a **stable path** from a
known base (module address) through a series of offsets to reach the value reliably.

```
Static base (module+offset) → Ptr1 + offset → Ptr2 + offset → Value
         (stable)              (dereference)     (dereference)    (target)
```

## Pointer Chain Building Workflow

### Step 1: Find the Dynamic Address
Use standard memory scanning (see memory-scanning skill) to find the current address of your target value.

### Step 2: Scan for Pointers
```
ScanForPointers(targetAddress, maxLevel=5, maxOffset=2048)
```
- `maxLevel`: How many pointer dereferences to follow (5 is default, increase for deep chains)
- `maxOffset`: Maximum offset at each level (2048 default, increase for large structures)

### Step 3: Filter Results
Good pointer chains have these properties:
- **Rooted in a module** (e.g., `GameAssembly.dll+XXXXX`) — NOT a heap address
- **Shorter is better** — 2-3 levels preferred over 5-6
- **Small offsets preferred** — large offsets (>0x1000) are less likely stable
- **Consistent offsets** — if multiple chains share the same final offsets, those offsets are likely correct

### Step 4: Validate
```
ValidatePointerPaths(paths, processId)
```
- Tests each path resolves to the expected value
- Ranks by stability score
- Identifies which paths are most likely to survive restarts

### Step 5: Rescan After Restart
1. Restart the game
2. Re-find the value via scanning
3. `RescanPointerPath` with the saved path and new target address
4. Paths that still resolve correctly are stable
5. Repeat 2-3 times for confidence

## Pointer Chain Anatomy

```
GameAssembly.dll+9A18E8 → [+0x00] → [+0x28] → [+0x10] → [+0x14]
│                          │          │          │          └── Final offset to value
│                          │          │          └── 3rd dereference
│                          │          └── 2nd dereference  
│                          └── 1st dereference (read pointer at base+0x00)
└── Static base (survives ASLR because it's module-relative)
```

In the address table, this appears as:
```
Base: "GameAssembly.dll"+9A18E8
Offsets: 0, 28, 10, 14
Type: Int32 (or Float, etc.)
```

## Common Chain Patterns

### Unity Il2Cpp Games
```
GameAssembly.dll+Static → [+0xB8] → [+0x00] → [+0x10] → [+FieldOffset]
  (class static ref)      (static fields)  (object ptr)  (instance base)  (field)
```
Typically 3-4 levels. See unity-il2cpp skill for details.

### Unreal Engine Games
```
Game.exe+GWorld → [+0x180] → [+0x38] → [+0x0] → [+0x30] → [+ActorOffset]
  (GWorld ptr)    (GameInstance) (LocalPlayers) ([0])  (PlayerController) (field)
```
Typically 4-6 levels due to deep object hierarchy.

### Native C++ Games
```
Game.exe+StaticVar → [+0x08] → [+FieldOffset]
  (global pointer)    (object ptr)  (field)
```
Often just 2-3 levels for simpler engines.

## Troubleshooting Broken Chains

### "Address not found" after restart
1. The base module offset may have changed (game update)
2. Re-scan for the value and re-run pointer scan
3. Compare old and new chains — if only the base offset changed, update it

### Chain resolves to wrong value
1. One of the intermediate pointers may be stale
2. Walk the chain manually: `ReadMemory` at each level to verify each dereference
3. The object may have been deallocated and reallocated at a different address
4. The chain may only be valid during specific game states (e.g., in-game, not in menus)

### Too many results from pointer scan
1. Increase `maxOffset` filter stringency (smaller max offset)
2. Require the chain to be rooted in a specific module
3. Rescan after restart to eliminate transient chains
4. Filter by chain depth (prefer shorter)

### No results from pointer scan
1. Increase `maxLevel` (deeper chains needed)
2. Increase `maxOffset` (structure offsets may be large)
3. The value may be accessed via a computed address (not a simple pointer chain)
4. Try scanning for the object base address instead of the specific field

## Advanced: AOB-Based Pointer Recovery

When pointer chains break on updates, AOB signatures can re-discover the base:
1. Find the code instruction that references the static pointer
2. `GenerateSignature` at that instruction
3. The AOB pattern encodes the RIP-relative offset to the static pointer
4. On update: scan for the AOB, extract the new static offset, rebuild the chain

This is the most update-resilient approach — combines AOB stability with pointer chain depth.
