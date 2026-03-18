---
name: code-analysis
description: >
  Expert workflows for analyzing what code reads or writes a memory address, tracing data flow,
  and understanding game logic through static and dynamic analysis. Load this skill when the user
  asks "what writes to this", "what accesses this", or wants to understand how a value is computed.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - analysis
  - disassembly
  - tracing
  - reverse-engineering
triggers:
  - what writes
  - what accesses
  - what reads
  - analyze code
  - disassemble
  - trace
  - find instruction
  - data flow
  - call stack
  - function
---

# Code Analysis — Expert Workflows

## Primary Workflow: TraceFieldWriters (Preferred)

For address table entries, always start with `TraceFieldWriters`:
1. Call `TraceFieldWriters` with the entry ID or label
2. It automatically extracts the structure offset from parent-relative metadata
3. Searches module code for displacement-based memory operands
4. Tries adjacent offsets if primary search finds nothing
5. Identifies containing functions for any writers found
6. Returns actionable next steps

This is the fastest path from "what writes to this?" to an answer.

## Manual Analysis Workflow

When TraceFieldWriters doesn't cover it (non-table addresses, complex patterns):

### Step 1: Risk Assessment
```
ProbeTargetRisk(address) → risk level + recommended approach
```
- LOW risk → safe for any breakpoint mode
- MEDIUM risk → prefer PageGuard or Stealth
- HIGH risk → prefer static analysis, use singleHit=true if BP needed
- CRITICAL risk → static analysis ONLY (FindWritersToOffset, FindByMemoryOperand)

### Step 2: Choose Analysis Path

**Dynamic path** (catches runtime behavior):
1. `SetBreakpoint` with appropriate mode (see breakpoint-mastery skill)
2. Ask user to trigger the game event
3. `GetBreakpointHitLog` to see which instruction wrote
4. `Disassemble` at the hit address + surrounding context (±20 instructions)
5. Trace backward to find where the value is CALCULATED, not just stored

**Static path** (no debugger, no game interaction):
1. `FindWritersToOffset` with the structure offset (include includeReads=true for full data flow)
2. `FindByMemoryOperand` for structured operand search
3. `FindFunctionBoundaries` to see the full function containing writers
4. `GetCallerGraph` to understand who calls the writing function
5. `SearchInstructionPattern` for broader pattern matching

## Reading Assembly: Decision Patterns

### Common Write Patterns
```asm
mov [rax+14h], ebx      ; Direct field write — ebx contains the new value
                         ; rax = struct pointer, 0x14 = field offset
                         ; Trace: where does ebx come from? Look up.

movss [rcx+08h], xmm0   ; Float field write — xmm0 has the value
                         ; Common in Unity/UE for position/health

add [rdi+20h], eax       ; Additive modification — value += eax
                         ; The "add" instruction IS the computation

sub [rsi+10h], edx       ; Subtractive — damage dealt, resource consumed
                         ; edx = damage amount, trace where it comes from

imul eax, [rcx+30h], 2   ; Multiplication — value × 2
                         ; This is often a level-up multiplier or scaling
```

### Tracing Value Origin
When you find the write instruction, trace backward:
1. Identify which register holds the new value
2. Scan upward for instructions that set that register
3. Look for: `mov` from another address (copy), `add`/`sub`/`imul` (computation),
   `call` followed by `mov eax,...` (function return value)
4. If the value comes from a function call → `GetCallerGraph` to find that function
5. The BEST hook point is where the value is computed, not where it's stored

### Identifying the "Real" Writer
Games often have multiple code paths writing the same field:
- Initialization (game load) — writes default value
- Normal gameplay update — the one you usually want
- UI refresh — reads and re-writes display copy
- Save/Load — serialization write

Use `GetBreakpointHitLog` with multiple hits to see ALL writers, then identify which one fires during the relevant gameplay event.

## Advanced: Understanding Game Logic

### Damage Calculation
```
Typical flow: AttackPower × Multiplier - Defense = FinalDamage
              FinalDamage written to HP field
```
- Hook the final `sub` or `mov` to HP for a damage multiplier
- Hook the calculation function for full control

### Level/XP System
```
Typical flow: CurrentXP + GainedXP → compare to XPTable[Level] → if >= threshold, level up
```
- Hook the `add` instruction on XP for an XP multiplier
- Modify the comparison to always pass for instant level-up

See `references/x86-patterns.md` for common assembly instruction patterns.
