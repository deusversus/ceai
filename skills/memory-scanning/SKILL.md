---
name: memory-scanning
description: >
  Expert workflows for finding game values in process memory using scan-and-narrow techniques.
  Load this skill when the user wants to find health, gold, ammo, coordinates, or any gameplay
  value. Covers data type selection, unknown initial value strategies, and refinement cycles.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - scanning
  - memory
  - values
  - search
triggers:
  - scan
  - search
  - find value
  - health
  - gold
  - ammo
  - money
  - mana
  - stamina
  - coordinates
  - experience
  - XP
  - score
  - lives
  - unknown value
---

# Memory Scanning — Expert Workflows

## Core Workflow: Finding a Known Value

1. **Attach**: If no process attached → `FindProcess` → `AttachProcess`
2. **Ask**: Get the current in-game value from the user (exact number)
3. **Initial scan**: `StartScan` with `ExactValue` + best-guess data type
4. **Narrow**: If results > 50 → ask user to change the value in-game → `RefineScan`
5. **Iterate**: Use `Increased` / `Decreased` / `ExactValue` until < 5 results
6. **Capture**: `AddToAddressTable` with a descriptive label
7. **Lock**: If user wants to freeze → `FreezeAddress` or `FreezeAddressAtValue`

## Data Type Selection Guide

Pick the right scan type on the FIRST try — saves multiple scan cycles:

| Value Type | Try First | Try Second | Notes |
|---|---|---|---|
| Health / HP / MP | Float | Int32 | Modern games almost always use float |
| Gold / Currency | Int32 | Int64 | Large currencies may use Int64 |
| Item counts | Int32 | Int16 | Small inventories may use Int16 |
| Coordinates (x,y,z) | Float | Double | Almost always Float; physics engines use Double |
| Boolean flags | Byte | Int32 | Check for 0/1 pattern |
| Experience / XP | Int32 | Float | RPGs vary widely |
| Timers / Cooldowns | Float | Double | Usually seconds as floating point |
| Percentages | Float | Int32 | Float 0.0-1.0 or Int32 0-100 |
| Damage values | Float | Int32 | Depends on the engine |
| Speed / Velocity | Float | Double | Always floating point |

## Unknown Initial Value Strategy

When the user doesn't know the exact number (e.g., "find my speed"):

1. `StartScan` with `UnknownInitialValue` — captures ALL memory
2. Trigger a change in-game (move, take damage, etc.)
3. `RefineScan` with `Increased` or `Decreased` as appropriate
4. Repeat steps 2-3 at least 3-4 times with different changes
5. Final pass: `RefineScan` with `Unchanged` while the value is stable
6. This typically narrows to < 20 results after 4-5 rounds

## Advanced Narrowing Techniques

### Value-Between Scans
If the value fluctuates within a range (e.g., health bar between 50-100):
- Use `ValueBetween` scan type with min/max bounds
- Useful for health bars where exact value isn't shown

### Fuzzy Float Scanning
Game floats rarely store exact values (99.99 not 100.0):
- Use a tolerance of ±1.0 for initial float scans
- Tighten tolerance during refinement

### Bit Mask Scanning
For flags stored as bitfields:
- Read the raw bytes with `ProbeAddress`
- Identify which bit(s) toggle with state changes
- Write the specific bit pattern with `WriteMemory`

## Region Filtering

Narrow scan scope for faster results:
- **Heap memory** (most game values live here) — default scan covers this
- **Module .data sections** — for static/global variables
- Use `ListMemoryRegions` to identify writable regions
- Exclude system DLLs, read-only regions, and executable sections

## Common Pitfalls

- **Encrypted values**: Some games XOR values in memory. If scans consistently fail, the value may be obfuscated. Try scanning for the XOR of value with common keys (0xDEAD, 0xFFFF).
- **Displayed vs stored**: The displayed value may differ from stored (e.g., displayed "100 HP" stored as 1000 internally — multiplied by 10 for display). Try scanning for value × 10, × 100.
- **Double-stored values**: Some games store the same value in two locations (display copy + real copy). Modify both or only the authoritative one.
- **Server-validated values**: Online games may validate values server-side. Changes revert on the next server sync.

See `references/data-types.md` for detailed per-genre data type patterns.
