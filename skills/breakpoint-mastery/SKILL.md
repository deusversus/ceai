---
name: breakpoint-mastery
description: >
  Complete guide to all 5 breakpoint intrusiveness modes, their compatibility matrix, safety
  rules, and emergency recovery procedures. Load this skill when setting breakpoints, monitoring
  memory accesses, hooking code execution, or dealing with anti-debug games.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - breakpoints
  - debugging
  - hooks
  - monitoring
triggers:
  - breakpoint
  - hook
  - debug
  - monitor
  - watch
  - hardware
  - page guard
  - stealth
  - code cave
  - anti-debug
  - what writes
  - what accesses
  - freeze
  - crash
---

# Breakpoint Mastery — Modes, Safety, and Recovery

## The 5 Breakpoint Modes

### 1. Auto (Default)
Engine picks the least intrusive mode:
- Execute breakpoints → Hardware
- Write/ReadWrite breakpoints → PageGuard
- Software breakpoints → Software

Use when you don't have a strong preference. Good starting point.

### 2. Stealth (Code Cave JMP Detour)
- **NO debugger attached** — completely invisible to anti-debug
- Redirects execution via a JMP to an allocated code cave
- Captures register snapshots in a ring buffer
- ⚠️ ONLY works on **executable code addresses**
- Cannot monitor data writes (auto-downgrades to PageGuard if attempted)
- Best for: anti-cheat games, long-running monitoring, execution tracing

Use `InstallCodeCaveHook` directly for full control over register capture.

### 3. PageGuard (PAGE_GUARD Protection)
- Uses memory protection flags to trigger exceptions on access
- Less intrusive than hardware BPs for monitoring writes/reads
- Still requires debugger attachment

**⚠️ CRITICAL SAFETY RULES:**
- NEVER use on heap pages with >10 co-resident address table entries (guard storms hang target)
- `ProbeTargetRisk` and `SetBreakpoint` will BLOCK PageGuard when co-tenancy > 10
- ALWAYS prefer code-cave hooks over data breakpoints when possible
- Use ONLY on isolated pages (stack-local, module .data, low co-tenancy heap)
- If `ProbeTargetRisk` returns risk=CRITICAL → abort, use static analysis instead

### 4. Hardware (Debug Registers DR0-DR3)
- Uses CPU debug registers — limited to 4 active breakpoints
- Requires thread suspension to write CONTEXT
- Best for: single-shot analysis ("find what writes/reads this address")
- ⚠️ Suspends ALL threads — can freeze anti-debug-sensitive games

### 5. Software (INT3 Patch)
- Patches instruction bytes with INT3 (0xCC)
- Most intrusive — modifies code in place
- Cannot monitor data writes — only executable code
- Best for: specific instruction tracing where other modes fail

## Compatibility Matrix

| Mode | Execute | Write/ReadWrite | Notes |
|---|---|---|---|
| Stealth | ✅ Code cave (safest) | ❌ Auto-downgrades to PageGuard | No debugger needed |
| PageGuard | ✅ Works (but use Stealth instead) | ✅ Recommended for data monitoring | Check co-tenancy first |
| Hardware | ✅ Works | ✅ Works | Max 4 active, thread suspend |
| Software | ✅ INT3 patching | ❌ Rejected with error | Most intrusive |
| Auto | ✅ → Hardware | ✅ → PageGuard | Engine selects optimal |

## Decision Tree: Choosing the Right Mode

```
Is it a DATA address (you want to monitor writes)?
├─ Yes → ProbeTargetRisk first
│   ├─ Risk ≤ MEDIUM → PageGuard with singleHit=true
│   ├─ Risk HIGH → PageGuard with singleHit=true (carefully)
│   └─ Risk CRITICAL → Static analysis only (FindWritersToOffset)
│
└─ No, it's CODE (you want to trace execution)
    ├─ Anti-debug game? → Stealth (code cave)
    ├─ Need register snapshots? → Stealth (code cave)
    ├─ Quick one-shot? → Hardware with singleHit=true
    └─ Deep trace? → Hardware (up to 4) or Stealth
```

## Safe Hook Workflow (Always Follow This)

1. `ProbeTargetRisk` → assess risk level and recommended modes
2. `CheckAddressSafety` → verify no prior freeze history at this address
3. `CheckHookConflicts` → ensure no overlapping hooks/patches
4. `DryRunHookInstall` → preview what bytes will be overwritten
5. `BeginTransaction` → group operations for atomic rollback
6. `SetBreakpoint` or `InstallCodeCaveHook` → install with monitoring
7. Verify via `GetBreakpointHitLog` or `GetCodeCaveHookHits`
8. If issues → `RollbackTransaction` to undo all changes

## Safety Features

### Hit-Rate Throttle
- Breakpoints firing >200 times/second are AUTO-DISABLED
- Hit log is preserved — check status after setting
- Prevents game freezes from hot addresses

### Single-Hit Mode
- Pass `singleHit=true` for risky targets
- BP fires once, captures data, auto-removes itself
- Ideal for "find what writes this address"

### Hot Address Rules
When monitoring addresses written every frame:
1. ALWAYS use `singleHit=true`
2. OR use `mode=PageGuard` (not Hardware) to minimize thread suspension
3. NEVER use Stealth for data writes (auto-downgraded)

## Emergency Recovery

**If target hangs after a breakpoint:**
1. `EmergencyRestorePageProtection` → restores all page guards via fresh handle (no locks)
2. `ForceDetachAndCleanup` → nuclear option: restores guards, detaches debugger, tears down session
3. `GetBreakpointHealth` → check if a BP is degraded/faulted/throttled

Use these tools IMMEDIATELY if the target becomes unresponsive.

## Anti-Debug Game Strategy

1. ALWAYS use `mode=Stealth` or `InstallCodeCaveHook` for execution hooks
2. Code cave hooks use JMP redirection — no `DebugActiveProcess` call
3. If Hardware mode freezes the game → remove BP, switch to Stealth
4. Code caves capture register snapshots readable via `GetCodeCaveHookHits`
5. For data write monitoring in anti-debug games:
   - `mode=PageGuard` with `singleHit=true` — BUT only if risk ≤ MEDIUM
   - If risk CRITICAL → static analysis + Stealth code-cave on the discovered writer

See `references/mode-selection.md` for detailed scenario-based selection guide.
