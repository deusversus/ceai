---
name: data-mining
description: >
  Expert workflows for discovering game data structures, mapping object layouts, finding vtables,
  and identifying field types through memory analysis. Load when exploring unknown memory regions,
  reverse engineering game objects, or building a structural understanding of game data.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - structures
  - dissection
  - vtable
  - fields
  - layout
triggers:
  - structure
  - dissect
  - class
  - fields
  - vtable
  - object
  - layout
  - reverse
  - map
  - unknown memory
  - explore memory
---

# Data Mining — Structure Discovery

## When to Use Data Mining

- You found one value (e.g., health) and want to map the entire player object
- You see a pointer and want to understand what it points to
- You need to find related values (MaxHP near HP, Mana near Health)
- You want to understand class inheritance and virtual function dispatch

## Primary Workflow: DissectStructure

### Step 1: Find the Object Base
If you found a value at address X with field offset Y:
```
Object base = X - Y
```
For example, HP at 0x1A2B3C with offset 0x14 → object starts at 0x1A2B28.

If you don't know the offset, browse backward:
1. `BrowseMemory` starting ~0x100 bytes before your known address
2. Look for a valid pointer at the start (vtable/class pointer)
3. Valid pointers to code sections (0x7FF...) at offset 0 are strong indicators

### Step 2: Dissect the Structure
```
DissectStructure(address=objectBase, size=256, typeHint="auto")
```
- `auto` mode detects pointers, floats, ints, and strings
- Use `typeHint="int32"` for game stat blocks (avoids float false positives)
- Use `typeHint="pointers"` when mapping an object with many references
- Use `typeHint="float"` for position/physics data

### Step 3: Interpret Results
DissectStructure returns typed interpretations at each offset:
- **Pointer** (value looks like a valid address) → follow it to find referenced objects
- **Float** (value in reasonable float range) → likely game stat or coordinate
- **Int32** (small integer) → counter, ID, flag, or enum
- **String** (pointer to valid string data) → object name, item name, etc.

### Step 4: Correlate with Gameplay
1. Note which values change during gameplay vs static
2. Use `CaptureSnapshot` before and after an event, then `CompareSnapshotWithLive`
3. Fields that changed during combat are likely HP/damage/buff related
4. Fields that changed during movement are likely position/velocity

## Vtable Analysis

### Identifying Virtual Functions
The first pointer in a C++ object is the vtable:
```
Object+0x00 → vtable (array of function pointers)
  vtable[0] → destructor (~Object)
  vtable[1] → first virtual method
  vtable[2] → second virtual method
  ...
```

To analyze:
1. `ReadMemory` at object+0x00 → get vtable address
2. `BrowseMemory` at vtable address → see function pointers
3. `Disassemble` at individual function pointers → understand each method
4. `FindFunctionBoundaries` to map each virtual function's extent
5. `GetCallerGraph` to see who calls specific vtable entries

### Class Identification via Vtable
Objects of the same class share the same vtable pointer:
1. Read vtable pointers from multiple objects
2. Objects with identical vtable pointers are the same class
3. This helps distinguish player entities from enemies, NPCs from items, etc.

## Field Discovery Strategies

### The "Change and Compare" Method
1. `CaptureSnapshot` of the object region
2. Do something in-game (take damage, gain gold, move)
3. `CompareSnapshotWithLive` → see exactly which fields changed
4. Repeat with different actions to map each field
5. Build a field map: offset → name → type

### The "Known Neighbor" Method
Game structs often group related values:
```
[HP] [MaxHP] [MP] [MaxMP] [STR] [DEX] [INT] [LUK]
```
If you found HP at offset 0x14:
- MaxHP is likely at 0x18 (next 4 bytes)
- MP at 0x1C, MaxMP at 0x20, etc.
- `DissectStructure` with `typeHint="int32"` or `"float"` to scan the neighborhood

### The "Cross-Reference" Method
1. Find multiple instances of the same object type (e.g., multiple enemies)
2. Compare their structures — fields that differ are instance-specific (HP, position)
3. Fields that are identical are class-level (type ID, vtable, template data)

## Common Game Object Patterns

### Player Character
```
+0x00  void*   vtable
+0x08  int32   entityType / flags
+0x10  float   posX
+0x14  float   posY
+0x18  float   posZ
+0x1C  float   health
+0x20  float   maxHealth
+0x24  float   mana
+0x28  float   maxMana
+0x30  int32   level
+0x34  int32   experience
+0x38  void*   inventoryPtr
+0x40  void*   equipmentPtr
```

### Inventory Slot
```
+0x00  int32   itemId
+0x04  int32   quantity
+0x08  int32   durability
+0x0C  int32   flags (equipped, locked, etc.)
```

### Position + Physics
```
+0x00  float   posX
+0x04  float   posY
+0x08  float   posZ
+0x0C  float   velX (velocity)
+0x10  float   velY
+0x14  float   velZ
+0x18  float   rotYaw
+0x1C  float   rotPitch
+0x20  float   rotRoll
```

## Tips

- **Alignment**: Fields are typically aligned to their size (4-byte int at 4-byte boundary)
- **Padding**: Gaps between fields are common for alignment or reserved space
- **Pointer density**: Areas with many valid pointers are likely object references, not data
- **Null pointers**: Consecutive null pointers (0x0000000000000000) suggest unused optional fields
- **Magic numbers**: 0xDEADBEEF, 0xCDCDCDCD, 0xFEEEFEEE indicate debug/uninitialized memory
