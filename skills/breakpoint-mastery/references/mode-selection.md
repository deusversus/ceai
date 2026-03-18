# Breakpoint Mode Selection — Scenario Guide

## Scenario 1: "Find what writes to my health"
**Approach**: Dynamic analysis with single-hit breakpoint
1. ProbeTargetRisk on the health address
2. If risk ≤ MEDIUM: SetBreakpoint(address, type=Write, mode=Auto, singleHit=true)
3. Ask user to take damage in-game
4. GetBreakpointHitLog → instruction address
5. Disassemble at hit address

## Scenario 2: "Monitor all writes to this address over time"
**Approach**: PageGuard with logging
1. ProbeTargetRisk first
2. SetBreakpoint(address, type=Write, mode=PageGuard, singleHit=false)
3. Let it collect hits during gameplay
4. GetBreakpointHitLog → see all writers + frequencies
5. Remove when done to prevent performance impact

## Scenario 3: "Hook a function to capture its arguments"
**Approach**: Stealth code cave hook
1. DryRunHookInstall to preview
2. InstallCodeCaveHook at function entry
3. GetCodeCaveHookHits to see register snapshots
4. RCX/RDX/R8/R9 will contain the function arguments (x64)

## Scenario 4: "Game crashes when I set breakpoints" (anti-debug)
**Approach**: Stealth-only pipeline
1. NEVER use Hardware or Software modes
2. For code tracing: InstallCodeCaveHook
3. For data monitoring: Static analysis first (FindWritersToOffset)
4. Once you find the writer instruction, hook IT with Stealth
5. This avoids DebugActiveProcess entirely

## Scenario 5: "I need to monitor 5+ addresses simultaneously"
**Approach**: Mix of modes
- Hardware: Limited to 4 (DR0-DR3)
- PageGuard: Unlimited but performance-sensitive
- Stealth: Unlimited but only for code addresses
- Strategy: Use Stealth for code hooks, PageGuard sparingly for data, remove after use

## Scenario 6: "Value changes but breakpoint never fires"
**Diagnosis**:
1. The write may happen via a different code path (DMA, memcpy, etc.)
2. Try FindWritersToOffset for static analysis of ALL potential writers
3. The value might be written by a different thread — use GetAllThreadStacks
4. The address may have moved (ASLR) — RefreshAddressTable first
5. The write may use a different instruction form — SearchInstructionPattern
