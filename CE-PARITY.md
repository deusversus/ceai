# CE AI Suite — Cheat Engine 7.5 Feature Parity Tracker

> **Living document.** Updated after each development sprint. Compared against CE 7.5 source code (`cheat-engine-master/`).
>
> **Last updated:** 2026-04-16
> **Method:** 4-agent parallel source comparison reading actual CE 7.5 Delphi/Pascal source and our C#/.NET implementation.
> **Project velocity:** 501 commits in 31 days (~16/day). MVP + Phases 1-12 complete. Solo junior dev + AI pair.

---

## How to Read This Document

- **YES** = Fully implemented, feature-complete relative to CE
- **PARTIAL** = Exists but missing significant sub-features
- **NO** = Completely absent
- **Difficulty** = Estimated based on actual project velocity (a "1-session" feature is ~200-600 LOC delivered in a single Claude session including tests and audit)
- **Priority** = How often a real CE user would hit this gap

---

## Development Velocity Reference

Based on commit history, our actual pace for various feature sizes:

| Scope | Example | Actual Time |
|-------|---------|-------------|
| Single engine feature + tests | Phase 12A (process info P/Invoke) | ~1 hour |
| Multi-layer feature (engine → app → UI → tests) | Phase 12B (PDB symbols end-to-end) | ~1 hour |
| New subsystem (contracts + engine + Lua + AI tools + tests) | Phase 12E Mono bridge (managed side) | ~2 hours |
| Native C agent DLL (~600 LOC) | mono_agent.c | ~1 hour |
| VEH overhaul (7 sub-phases, native + managed) | Phases A-G | ~1 day |
| Scripting sprint (9 sub-phases, all Lua bindings) | S1-S9 compat sprint | ~1 day |
| Full debugging stepping (engine + service + VM + Lua + AI) | Phase 11A | ~2 hours |
| 6 scan features (types + auto-protect + freeze modes) | Memory R/W closure | ~1 hour |

**Rule of thumb:** 1 Claude session ≈ 200-800 LOC of production code + tests + audit. A "MEDIUM" feature is 1-2 sessions. A "HARD" feature is 2-4 sessions. A "VERY HARD" is a multi-day sprint.

---

## Executive Summary

| Metric | Value |
|--------|-------|
| Total CE 7.5 features evaluated | ~300 |
| YES (fully present) | ~65 (22%) |
| PARTIAL (exists, incomplete) | ~55 (18%) |
| NO (completely missing) | ~180 (60%) |
| **Honest overall parity** | **~35-40%** |
| Our self-assessed parity (against MVP targets) | ~98% |
| Lua API coverage | 175 of ~457 globals (38%) |
| Lua class bindings | 6 of 73 class files (8%) |
| Lines of Lua binding code | 5,805 of ~42,650 (14%) |

The MVP targets were "can a beginner do basic game hacking" — and we hit those. The 35-40% number is against CE's full 20-year feature surface.

### What We Do Better Than CE

1. **Architecture** — async, DI, testable, MVVM. CE is synchronous Delphi with global state
2. **AI integration** — 50+ AI tools, agent reasoning loop, streaming, multi-provider. CE has nothing like this
3. **VEH debugger cascade** — Hardware → VEH → PageGuard auto-fallback is cleaner than CE's manual selection
4. **Instruction decoding** — Iced.Intel is more correct than CE's hand-rolled decoder (AVX-512, newer extensions)
5. **CT round-trip** — solid XML parsing with unknown element preservation
6. **Code cave engine** — stealth detour hooking with RIP-relative relocation
7. **Mono bridge** — cleaner named-pipe architecture than CE's monopipe
8. **Speed hack** — inline hooking arguably more robust than CE's DLL injection
9. **3,085 automated tests** — CE has minimal automated testing
10. **Process metadata** — we expose parent PID, command line, elevation, window title; CE exposes less

---

## Category 1: Memory Read/Write

### Source comparison: `CEFuncProc.pas` vs `WindowsEngineFacade.cs`

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 1 | Basic ReadProcessMemory/WriteProcessMemory | Full | YES | — | — | |
| 2 | Auto-VirtualProtect on write failure | `rewritedata` | YES | — | — | Added in Phase 12 |
| 3 | FlushInstructionCache on code writes | `rewritecode` | NO | High | 1 session | Writes to .text don't flush icache. Real bug for code patching. |
| 4 | Kernel-mode read/write (via driver) | DBK driver | NO | Low | Shelved | Requires signed kernel driver |
| 5 | Network read/write (ceserver) | TCP protocol | NO | Low | Shelved | Remote debugging infrastructure |
| 6 | DBVM physical memory access | Hypervisor | NO | Low | Shelved | Ring -1 |
| 7 | Data types: Byte through Double + Pointer | 17 types | 13 types | Medium | 1 session | Missing: Binary (bit-level), Custom, Grouped, CodePageString |
| 8 | Binary data type (bit-level R/W) | vtBinary | NO | Medium | 1 session | Bit-level read/write with start bit + length |
| 9 | Custom type system (Lua/AA-defined types) | Full plugin arch | PARTIAL (schema only) | High | 2-3 sessions | Schema exists, runtime conversion is hollow. No script execution for custom types. |
| 10 | Write log / edit history with undo | `frmEditHistoryUnit` | NO | High | 1 session | CE tracks every write with original bytes. We have PatchUndoService but it only covers WriteWithUndo, not direct writes. |
| 11 | Code-write vs data-write distinction | Separate paths | NO | Medium | 1 session | Need `FlushInstructionCache` + breakpoint preservation during code writes |
| 12 | Hex view display modes (chars/shorts/longs) | Multiple modes | NO | Medium | 1 session | |
| 13 | Change protection from hex view UI | Context menu | NO | Low | 1 session | Protection engine exists, just no UI command |
| 14 | Follow pointer from hex view | Click-to-follow | NO | Low | 1 session | |
| 15 | Data inspector write-back | Double-click to edit | NO | Medium | 1 session | Display-only currently |
| 16 | Auto-type detection heuristic | `FindTypeOfData` | NO | Low | 1 session | |
| 17 | read/writeLocal (CE's own memory) | 9+9 functions | NO | Low | 1 session | Rarely needed |

---

## Category 2: Scanning

### Source comparison: `memscan.pas` (~4,000 lines) vs `WindowsScanEngine.cs` (~800 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 18 | **Disk-backed result storage** | File-backed, billions of results | NO (50K cap in-memory) | **CRITICAL** | 3-4 sessions | **THE most important gap.** Unknown Initial Value scan is useless on real games. Need paged file I/O for addresses + memory snapshots. |
| 19 | **IncreasedBy / DecreasedBy scan** | Core scan types | NO | **CRITICAL** | 1 session | Bread-and-butter operations. "Health dropped by 10" needs DecreasedBy. |
| 20 | **String / Unicode scanning** | Text search in memory | NO | **CRITICAL** | 1 session | Finding player names, item names, dialogue. Day-one CE usage. |
| 21 | Percentage-based scans | IncreasedByPercentage etc. | NO | Medium | 1 session | "Increased by 50%" style |
| 22 | Binary (bit pattern) scanning | `vtBinary` with 0/1/?/* wildcards | NO | Medium | 1 session | Flag hunting |
| 23 | "All" type scan (multi-type simultaneous) | Scans Byte+Word+DWord+QWord+Single+Double at once | NO | High | 2 sessions | CE's most powerful first-scan mode |
| 24 | Pointer type scan (validate targets) | Find valid pointers in memory | NO | Medium | 1 session | |
| 25 | Inverse scan (negate match) | `inverseScan` | NO | Low | Trivial | Flip the match boolean |
| 26 | Float rounding modes | Rounded/Truncated/Extreme | PARTIAL (epsilon only) | Medium | 1 session | CE's modes are more intuitive |
| 27 | Float exponent filter | Skip NaN/Inf/huge floats | NO | Medium | Trivial | |
| 28 | Last-digits fast scan mode | Addresses ending in specific digits | NO | Low | 1 session | |
| 29 | Working-set-only scan | Only pages in physical RAM | NO | Low | 1 session | |
| 30 | Lua formula scan | User writes arbitrary comparison | NO | Low | 2 sessions | |
| 31 | Save/load named scan results | Persist to disk, named snapshots | NO | Medium | 1 session | |
| 32 | Compare to saved/first scan | "Same as first scan" | NO | Medium | 1 session | |
| 33 | Multi-AoB scan | Multiple patterns simultaneously | NO | Medium | 1 session | |
| 34 | Nibble wildcards in AoB | `4?` matches 0x40-0x4F | NO | Low | Trivial | |
| 35 | Grouped scan (structure search DSL) | `4:100 F:1.5 *:8 2:50` syntax | PARTIAL (wrong semantics) | High | 2-3 sessions | Our "grouped scan" runs independent scans, not structural offset search |

---

## Category 3: Disassembly & Code Analysis

### Source comparison: `disassembler.pas` (16,590 lines) + `DissectCodeThread.pas` (1,036 lines) vs `WindowsDisassemblyEngine.cs` (149 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 36 | x86/x64 instruction decode | Custom decoder | YES (Iced.Intel) | — | — | We win here — Iced is more maintained |
| 37 | **Backward disassembly (previousopcode)** | Walk backward to find instruction boundary | NO | **CRITICAL** | 1 session | Can't scroll up in disassembler. Fundamental UX gap. |
| 38 | **Code flow analysis (DissectCodeThread)** | Scan all executable memory, build call graph + xref database + string references | NO | **CRITICAL** | 3-4 sessions | Powers jump lines, xrefs, "find what references." Without it, disassembler is a dumb byte decoder. |
| 39 | **Jump line visualization** | Curved colored lines connecting jump sources to targets | NO | High | 2-3 sessions | Most visually distinctive CE feature |
| 40 | **Inline assembler (double-click to edit)** | Click instruction → type new one → assembled in-place | NO | High | 1-2 sessions | Core RE workflow |
| 41 | Context-aware disassembly (flag evaluation) | Green/red conditional jumps based on EFLAGS | NO | High | 1 session | Needs thread context passed to disassembler |
| 42 | Instruction metadata extraction | `isjump`, `iscall`, `isret`, operand values | PARTIAL | Medium | 1 session | Iced.Intel provides this; we don't extract it |
| 43 | Symbol substitution in operands | `call kernel32.Sleep` not `call 0x7FFA...` | PARTIAL | Medium | 1 session | We show symbol separately, not inline |
| 44 | Cross-reference annotations | "Referenced by: 0x1234, 0x5678" | NO | High | Depends on #38 | Requires code analysis engine |
| 45 | String reference detection | Find instructions referencing string data | NO | High | Depends on #38 | |
| 46 | Disassembly save/export | Export to file | NO | Low | Trivial | |
| 47 | Relative base addressing | Show offsets from chosen base | NO | Low | 1 session | |
| 48 | ARM/ARM64/Thumb decode | 4 separate decoders | NO | Low | Shelved | Windows-only tool |
| 49 | Disassembler Lua hooks | `OnDisassembleOverride`, `OnPostDisassemble` | NO | Low | 1 session | |

---

## Category 4: Debugger & Stepping

### Source comparison: `debughelper.pas` (3,562 lines) + `frmTracerUnit.pas` (2,448 lines) vs our VEH+stepping (~4,400 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 50 | Windows Debug API (DebugActiveProcess) | Full event loop | YES | — | — | |
| 51 | VEH debugger | Injected agent, shared memory | YES | — | — | |
| 52 | Hardware breakpoints (DR0-DR3) | Full | YES | — | — | |
| 53 | Software breakpoints (INT3) | Full | YES | — | — | |
| 54 | PAGE_GUARD breakpoints | Full | YES | — | — | |
| 55 | Auto-fallback chain | HW → exception | YES (better) | — | — | Our cascade is cleaner |
| 56 | Conditional breakpoints | Lua + easy mode | YES | — | — | |
| 57 | Thread-specific breakpoints | Thread filter | YES | — | — | |
| 58 | Step Into | Trap Flag | YES | — | — | |
| 59 | **Step Over** | Detect CALL, temp BP at next instruction | **NO (stub)** | **CRITICAL** | 1 session | `StepOverAsync` literally calls `StepInAsync`. Must fix. |
| 60 | Step Out | Read [RSP], temp BP | PARTIAL | Medium | 1 session | |
| 61 | Run To (continue to address) | `co_runtill` | NO | Medium | 1 session | |
| 62 | **Register modification on BP hit** | Auto-set register values when BP fires | NO | **High** | 1-2 sessions | #1 game modding operation: "set health to 999 on write" |
| 63 | **FPU/XMM register display** | Full FP stack + XMM0-15 + YMM | NO | High | 1-2 sessions | Modern games are 90%+ float |
| 64 | **Trace visualization UI** | TreeView with call nesting, register panel, memory display | NO | High | 2-3 sessions | We collect trace data but can't display it |
| 65 | Trace search (Lua-based) | "Find where EAX==0x1234" | NO | Medium | 1 session | |
| 66 | Trace save/load | Serialize to disk | NO | Low | 1 session | |
| 67 | Trace comparison | Side-by-side diff | NO | Low | 2 sessions | |
| 68 | Thread freeze/unfreeze in debugger | Suspend/resume individual threads | NO | Medium | 1 session | |
| 69 | StackWalk64 API | Proper SEH unwinding | NO | Medium | 1 session | We roll our own RBP chain walk |
| 70 | Raw stack viewer | Stack memory with value interpretation | NO | Medium | 1-2 sessions | |
| 71 | Kernel debugger | Ring-0 via driver | NO | Low | Shelved | |
| 72 | DBVM debugger | Hypervisor-invisible BPs | NO | Low | Shelved | |
| 73 | GDB/network debugger | Remote protocol | NO | Low | Shelved | |
| 74 | Find What Code Accesses (read+write) | `bo_FindWhatCodeAccesses` | PARTIAL | Medium | 1 session | We have FindWhatWrites but not reads |
| 75 | Page-wide breakpoints | BP covering entire 4KB page | NO | Low | 1 session | |

---

## Category 5: Address Table & CT Compatibility

### Source comparison: `MemoryRecordUnit.pas` (~1,700 lines) + `addresslist.pas` (~2,000 lines) vs `AddressTableService.cs` (~800 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 76 | Basic entries (static + pointer chains) | Full | YES | — | — | |
| 77 | Groups with cascading activate | Full | PARTIAL | Medium | 1 session | moActivateChildrenAsWell parsed but not executed |
| 78 | CT XML round-trip fidelity | Full | YES | — | — | Unknown elements preserved |
| 79 | **Hotkey execution** | 8 action types, sound, onlyWhileDown | NO (data round-trips, runtime dead) | **CRITICAL** | 2-3 sessions | Most trainers are hotkey-driven |
| 80 | Drop-down value list (runtime) | Click to select value | PARTIAL | Medium | 1 session | Parsed/stored but no click-to-select UI |
| 81 | Custom type evaluation | Lua/AA script-defined types | NO | High | 2-3 sessions | Name preserved, runtime hollow |
| 82 | Address Group Headers (address + children) | IsAddressGroupHeader | NO | Medium | 1 session | Our groups are either/or |
| 83 | Entry sorting (by column) | Full | NO | Medium | 1 session | |
| 84 | Drag-and-drop reorder | Tree DnD | NO | Medium | 1-2 sessions | |
| 85 | In-place editing (double-click) | Full inline editing | PARTIAL | Medium | 1 session | Some edit UI but not inline treeview |
| 86 | Copy/paste entries as XML | Clipboard round-trip | NO | Low | 1 session | |
| 87 | OnActivate/OnDeactivate/OnValueChanged events | Lua callbacks per entry | NO | High | 2 sessions | Many CTs rely on these |
| 88 | OnGetDisplayValue event | Custom display via Lua | NO | Medium | 1 session | |
| 89 | Lua-evaluated pointer offsets | Lua expressions as offsets | NO | High | 2 sessions | CE allows dynamic pointer math |
| 90 | Undo last value change per entry | UndoValue | NO | Low | 1 session | |
| 91 | DontSave flag | Exclude from CT save | NO | Low | Trivial | |

---

## Category 6: Lua Scripting API

### Source comparison: 73 Pascal binding files (42,650 lines) vs 26 C# binding files (5,805 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 92 | Core memory R/W (readByte through readDouble + writes) | ~18 functions | YES (~17) | — | — | |
| 93 | readLocal/writeLocal (CE's own memory) | 18 functions | NO | Low | 1 session | |
| 94 | **Form designer — basic widgets** (Form, Button, Label, Edit, CheckBox, Timer) | Full Delphi wrappers | PARTIAL (~6 widgets) | — | — | |
| 95 | **Form designer — advanced widgets** (Memo, ComboBox, ListBox, ListView, TreeView, Image, Panel, TrackBar, ProgressBar, RadioGroup, Splitter, PaintBox) | 15+ widget types | NO | **High** | 4-6 sessions | Any CT with advanced GUI breaks |
| 96 | **Canvas drawing** (line, rect, ellipse, textOut, pixel) | Full TCanvas wrapper | NO | Medium | 2 sessions | |
| 97 | **Menus** (MainMenu, PopupMenu, MenuItem) | Full menu system | NO | Medium | 2 sessions | |
| 98 | **File dialogs** (OpenDialog, SaveDialog) | Full | NO | Medium | 1 session | |
| 99 | Form DFM loading/saving | loadFromFile/saveToFile | NO | Low | Very Hard | Delphi-specific |
| 100 | Object component hierarchy (50+ properties per level) | Full RTTI-like access | NO | Low | Very Hard | Delphi OOP model |
| 101 | D3D hook / overlay | Sprite, text, texture rendering | NO | Low | Shelved | |
| 102 | Keyboard/mouse input | `isKeyPressed`, `keyDown`, `mouse_event`, `getPixel` | NO | Medium | 1 session | |
| 103 | Process pause/unpause | `pause()`/`unpause()` | NO | Medium | 1 session | |
| 104 | Streams (MemoryStream, FileStream) | Full | NO | Low | 1-2 sessions | |
| 105 | Internet functions | `getInternet` etc. | NO | Low | 1 session | |
| 106 | Pipe client/server | Named pipe IPC | NO | Low | 1 session | |
| 107 | loadTable / saveTable / getCheatTable | Table management | NO | Medium | 1 session | |
| 108 | Table-level Lua auto-execution on load | Runs LuaScript from CT | NO | High | 1 session | Many CTs have Lua init scripts |
| 109 | injectDLL | DLL injection from Lua | NO | Medium | 1 session | |
| 110 | synchronize / queue | Thread-safe UI updates | NO | Medium | 1 session | |

---

## Category 7: Pointer Scanner

### Source comparison: 8+ Pascal files (thousands of lines) vs `PointerScannerService.cs` (~350 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 111 | **Reverse pointer map** | Massive radix-tree reverse lookup | NO | High | 3-4 sessions | Our forward scan is O(n*m); CE's reverse map is O(1) per lookup |
| 112 | **Multi-level recursive scan (depth 7+)** | True recursive DFS using reverse map | PARTIAL (depth 2 hard cap) | High | Depends on #111 | |
| 113 | **Multi-threaded workers** | N worker threads sharing path queue | NO | High | 2 sessions | |
| 114 | Result comparison across restarts | Load two .PTR, find intersection | YES | — | — | |
| 115 | Path validation | Stable/Drifted/Broken | YES | — | — | |
| 116 | Compressed binary result format | Bit-packed for billions of results | NO | Medium | 2 sessions | Our JSON format won't scale |
| 117 | 20+ scan options | Static-only, aligned, no-loop, heap-only, class-pointer-only, negative offsets, etc. | NO (4 options) | Medium | 2-3 sessions | |
| 118 | Multi-threaded rescan/filter | `TRescanWorker` with Lua filter | PARTIAL (single-threaded) | Medium | 1-2 sessions | |
| 119 | Distributed network scanning | Parent/child worker architecture | NO | Low | Shelved | |
| 120 | CUDA GPU acceleration | `cudapointervaluelist.cu` | NO | Low | Shelved | |
| 121 | Resume interrupted scan (full state) | Save path queue to disk | PARTIAL (module index only) | Low | 1 session | |

---

## Category 8: Auto Assembler

### Source comparison: `autoassembler.pas` (4,685 lines) vs `WindowsAutoAssemblerEngine.cs` (1,754 lines)

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 122 | [ENABLE]/[DISABLE], alloc, label, define, assert | Full | YES | — | — | |
| 123 | registersymbol/unregistersymbol | Full | YES | — | — | |
| 124 | aobscan/aobscanmodule/aobscanregion | Full | YES | — | — | |
| 125 | createthread | Full | PARTIAL | Low | 1 session | Missing `createthreadandwait` |
| 126 | readmem | Copy bytes between addresses | NO | Medium | 1 session | Used for save/restore in scripts |
| 127 | loadbinary | Load file into target memory | NO | Low | 1 session | |
| 128 | reassemble | Disassemble + reassemble for relocation | NO | Medium | 1 session | |
| 129 | **{$c} C code blocks (TCC)** | Inline C compilation + injection | NO | High | 3-4 sessions | Many complex CE scripts use this |
| 130 | {$try}/{$except} | AA exception handling | NO | Low | 1 session | |
| 131 | globalalloc/sharedalloc | Cross-script persistent alloc | NO | Medium | 1 session | |
| 132 | fullaccess | Change page protection | NO | Low | Trivial | |
| 133 | Custom AA commands (plugin) | `RegisterAutoAssemblerCommand` | NO | Low | 1-2 sessions | |

---

## Category 9: Process & Structure

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 134 | Process list + filter | Full | YES | — | — | |
| 135 | Process icons | Background icon fetch | NO | Low | 1 session | Cosmetic |
| 136 | Window enumeration tabs (Apps/Processes/Windows) | 3-tab dialog | NO | Low | 1 session | |
| 137 | Create process with debug attach | Launch + attach | NO | Medium | 1 session | |
| 138 | File-as-memory (static analysis) | Open file as process | NO | Low | 2 sessions | |
| 139 | **Structure definitions (persistent, named)** | Full model: named fields, nesting, XML save | NO | High | 2-3 sessions | We do heuristic guessing but no persistent structs |
| 140 | Nested child structures | Pointer → child struct recursion | NO | Medium | 1-2 sessions | |
| 141 | Live structure view with auto-refresh | Timer-driven value updates | NO | Medium | 1-2 sessions | |
| 142 | Code dissection → structure inference | DissectCode informs structure layout | NO | High | Depends on #38 | |
| 143 | Symbol handler with PDB search paths | Background loading, network servers, SQLite cache | PARTIAL | Medium | 2-3 sessions | We use DbgHelp but no search paths or caching |
| 144 | PDB type/structure info | Parse struct layouts from debug symbols | NO | High | 2-3 sessions | |

---

## Category 10: Misc / Infrastructure

| # | Feature | CE 7.5 | Us | Priority | Difficulty | Notes |
|---|---------|--------|----|----------|-----------|-------|
| 145 | Speed hack | Full | YES | — | — | Our inline hooking is good |
| 146 | Trainer generation | Standalone EXE with form designer | PARTIAL (source code only) | Medium | 3-4 sessions | We generate .cs, not standalone .exe |
| 147 | D3D/OpenGL overlay | Full DirectX hook DLL | NO | Low | Shelved | Massive scope |
| 148 | CE Server (Linux/Android remote) | TCP protocol | NO | Low | Shelved | |
| 149 | DBVM hypervisor | Ring -1 | NO | Low | Shelved | |
| 150 | Plugin DLL loading (native) | LoadLibrary + function exports | PARTIAL | Low | 1-2 sessions | We have managed plugins |

---

## Priority Tiers for Future Work

### Tier 0 — Blocks Core Workflows (fix these first)

| # | Gap | Sessions | Impact |
|---|-----|----------|--------|
| 18 | Disk-backed scan results (50K cap → unlimited) | 3-4 | Unblocks the entire Unknown Initial Value workflow |
| 19 | IncreasedBy / DecreasedBy scan types | 1 | Most common next-scan operations |
| 20 | String / Unicode scanning | 1 | Day-one CE usage |
| 59 | Step Over (actual implementation, not stub) | 1 | Debugger is unusable without this |
| 37 | Backward disassembly (previousopcode) | 1 | Can't scroll up in disassembler |
| 79 | Hotkey runtime execution | 2-3 | Most trainers are hotkey-driven |

**Estimated total: ~9-11 sessions**

### Tier 1 — Power User Features

| # | Gap | Sessions |
|---|-----|----------|
| 38 | Code analysis engine (DissectCodeThread equivalent) | 3-4 |
| 39 | Jump line visualization | 2-3 |
| 40 | Inline assembler | 1-2 |
| 62 | Register modification on BP hit | 1-2 |
| 63 | FPU/XMM register display | 1-2 |
| 64 | Trace visualization UI | 2-3 |
| 95 | Form designer — advanced widgets (15+ types) | 4-6 |
| 23 | "All" type scan (multi-type simultaneous) | 2 |
| 35 | Grouped scan fix (structural offset search) | 2-3 |
| 87 | Address table Lua events (OnActivate, etc.) | 2 |
| 108 | Table-level Lua auto-execution on CT load | 1 |
| 111 | Reverse pointer map for efficient deep scanning | 3-4 |

**Estimated total: ~25-35 sessions**

### Tier 2 — Completeness

Everything else in the tables above that isn't shelved. ~50 features at 1-2 sessions each.

**Estimated total: ~50-70 sessions**

### Shelved — Requires External Prerequisites

- Kernel driver (EV cert + WDK)
- DBVM hypervisor
- D3D overlay (massive native DLL)
- CE Server / network debugging
- CUDA GPU scanning
- Distributed pointer scanning

---

## Tracking Changes

| Date | What Changed | New Parity |
|------|-------------|------------|
| 2026-04-16 | Initial creation from 4-agent CE 7.5 source comparison | ~35-40% |
| | | |
