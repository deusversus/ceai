---
name: stealth-awareness
description: >
  Knowledge of anti-cheat detection vectors and safe research practices for memory analysis.
  Covers how EAC, BattlEye, and Vanguard detect tools, which operations are visible, and how
  to minimize detection risk during security research. Load when working with protected games.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - anti-cheat
  - detection
  - stealth
  - security
triggers:
  - anti-cheat
  - eac
  - battleye
  - vanguard
  - detection
  - stealth
  - protected
  - ban
  - kernel
  - driver
  - anti-debug
  - crash
  - detected
---

# Stealth Awareness — Anti-Cheat Detection & Safe Practices

## Important Notice

This skill provides knowledge for **security research and educational purposes**. Understanding
detection mechanisms is essential for building robust tools and conducting legitimate analysis.
Always respect game Terms of Service and applicable laws.

## Detection Vectors

Anti-cheat systems detect tools through multiple channels. Understanding these helps
choose safe analysis approaches.

### 1. Process Detection
- **Window enumeration**: Anti-cheats scan for known tool window titles and class names
- **Process list scanning**: Checks running processes against blacklists
- **Module scanning**: Enumerates loaded DLLs in the target process

**Mitigation**: CE AI Suite operates as an external tool without injecting into the game process.
Standard memory reading via Windows API (ReadProcessMemory) is less detectable than injection.

### 2. Debugger Detection
- **IsDebuggerPresent()**: Checks PEB.BeingDebugged flag
- **NtQueryInformationProcess**: Queries debug port and flags
- **Thread context checking**: Detects hardware breakpoints (DR0-DR3)
- **Timing checks**: Debugger single-stepping causes measurable delays
- **INT 2D / INT 3**: Planted exceptions that behave differently under debugger

**Mitigation**: Use `mode=Stealth` (code cave hooks) which never calls DebugActiveProcess.
Avoid Hardware breakpoints in anti-debug games — they write to debug registers.

### 3. Memory Integrity
- **Code section hashing**: Anti-cheat hashes .text sections and detects patches
- **IAT hooking detection**: Import Address Table modifications are flagged
- **Page protection monitoring**: Detects VirtualProtect calls on game code pages
- **Memory scanning**: Searches for known cheat signatures in process memory

**Mitigation**:
- Code cave hooks overwrite only 5 bytes (JMP) — minimize footprint
- Always restore original bytes on disable ([DISABLE] section in scripts)
- Avoid modifying code sections when possible — prefer data modifications
- Use `DryRunHookInstall` to understand exactly what bytes change

### 4. Kernel-Level Detection
- **Driver scanning**: Detects custom/unsigned drivers
- **SSDT hooking detection**: Monitors system service table modifications
- **Boot integrity**: Secure Boot / UEFI verification
- **Hypervisor-based monitoring**: Some anti-cheats use VM-based isolation

**CE AI Suite note**: We operate entirely in user mode — no kernel driver required.
This avoids the most aggressive detection layer entirely.

### 5. Behavioral Analysis
- **Aim patterns**: Machine-like precision triggers detection
- **Reaction times**: Inhuman reaction speeds are flagged
- **Memory access patterns**: Rapid sequential reads of game state may be logged
- **Value anomalies**: Server-side validation catches impossible values

**Mitigation**: When modifying values, use realistic amounts. Don't set health to
999999999 — set it to a plausible value like max health.

## Anti-Cheat Systems Overview

### Easy Anti-Cheat (EAC)
- Kernel driver + user-mode service
- Process/module scanning, code integrity checks
- Server-side reporting for behavioral analysis
- Moderate aggressiveness — focuses on known signatures
- **Weakest against**: External tools, data-only modifications

### BattlEye
- Kernel driver with deep scanning capabilities
- Dynamic code analysis — can detect novel techniques
- Aggressive memory scanning and driver enumeration
- **Weakest against**: External reading (no writing), static analysis

### Vanguard (Riot)
- Always-on kernel driver (loads at boot)
- Most aggressive system-wide monitoring
- Hardware fingerprinting and ban persistence
- Blocks many driver-level cheats pre-emptively
- **Weakest against**: Analysis done while game is not running

## Safe Analysis Strategy

### Minimal Risk Operations (usually undetected)
- ✅ Reading memory with ReadProcessMemory (external, no injection)
- ✅ Module enumeration via ToolHelp API
- ✅ Static analysis of game binaries on disk (offline)
- ✅ Scanning for values (read-only operations)
- ✅ Taking memory snapshots for offline analysis

### Moderate Risk Operations
- ⚠️ Writing to DATA sections (game data modification)
- ⚠️ PageGuard breakpoints (modifies page protection)
- ⚠️ VirtualAllocEx in target process (code cave allocation)
- ⚠️ Running while anti-cheat service is active

### High Risk Operations
- ❌ Writing to CODE sections (.text modification)
- ❌ Hardware debug registers (visible via thread context)
- ❌ DebugActiveProcess API call (instant detection)
- ❌ DLL injection into game process
- ❌ IAT/EAT hooking

### Recommended Workflow for Protected Games
1. **Prefer static analysis** — `FindWritersToOffset`, `FindByMemoryOperand`, `SearchInstructionPattern`
2. **Use Stealth mode** for any runtime hooks (code caves, no debugger)
3. **Minimize write operations** — read and analyze first, write only when necessary
4. **Use singleHit breakpoints** — minimize exposure time
5. **Monitor offline** — analyze snapshots and disassembly rather than live debugging
6. **Keep modifications subtle** — realistic values, minimal code changes

## CE AI Suite's Anti-Detection Design

Our tool's architecture is inherently lower-risk than traditional CE:
- **External process** — no injection into game address space
- **No kernel driver** — avoids kernel-level scanning entirely
- **Stealth hooks via code caves** — no DebugActiveProcess call
- **Read-first philosophy** — extensive analysis before any writes
- **Transaction system** — rollback capability if issues detected

The AI operator should always prefer the least intrusive approach:
1. Static analysis (zero runtime footprint)
2. Memory reading (minimal footprint)
3. Stealth hooks (low footprint, no debugger)
4. PageGuard breakpoints (moderate footprint)
5. Hardware breakpoints (high footprint — last resort)
