# CE AI Suite — Project Roadmap

**Derived from:** [PROJECT-REVIEW.md](PROJECT-REVIEW.md) (full project review against CE 7.5 baseline)  
**UI Blueprint:** Session files/UI-DESIGN.md (comprehensive layout spec with tool→UI mappings)  
**Baseline:** Early Alpha (v0.2–0.3), ~25K LOC, 65 tests, ~21% AI↔UI tool parity  
**Goal:** Production-quality memory analysis workstation with full AI integration and manual UI parity

---

## Guiding Principles

1. **AI-first, user-parity goal** — Every capability is built for the AI operator first, then surfaced in manual UI. The app must be fully functional without AI.
2. **No orphan tools** — If it's in `AiToolFunctions.cs`, it needs a UI surface (window, panel, context menu, or toolbar button).
3. **Incremental delivery** — Each phase ships a usable improvement. No "big bang" rewrites.
4. **Preserve what works** — The AI integration (rated 95%) and architecture (rated 75%) are strengths. Don't break them.

---

## Phase 1: Docking Framework & Layout Foundation ✅ COMPLETE

**Goal:** Replace the fixed grid layout with a professional dockable panel system.

| Item | Status | Notes |
|------|--------|-------|
| Install AvalonDock 4.72.1 | ✅ Done | Dirkster.AvalonDock + VS2013 themes (includes resizable panel splitters natively) |
| Migrate MainWindow.xaml to DockingManager | ✅ Done | 6 panels: Processes, Address Table, Inspection, Scanner, Output, AI Operator |
| Dark/Light theme sync for AvalonDock | ✅ Done | ThemeChanged event → Vs2013DarkTheme / Vs2013LightTheme |
| Embed Memory Browser as center tab | ✅ Done | MemoryBrowserControl UserControl, auto-opens on process attach |
| Auto-open Memory Browser setting | ✅ Done | Settings → General → configurable |
| Clean up old MemoryBrowserWindow | ✅ Done | Removed, all callers use embedded tab |
| Bottom status bar (process/center/right) | ✅ Done | StatusBarProcess, StatusBarCenter, StatusBarRight |

**All Phase 1 items complete.**

---

## Phase 2: Bottom Panel Buildout

**Goal:** Extract existing functionality into proper tabbed bottom panels and add missing tabs. This addresses the scanner, breakpoints, and output being trapped in the center area.

### 2A — Extract & Reorganize Existing Content

| Item | Review Source | Details |
|------|-------------|---------|
| Scan Results tab | §2.1, UI-DESIGN §Zone 5 | Extract memory scanner from center area into dedicated bottom tab. Add scan controls (new/refine/reset), result list with "Add to table" action. |
| Breakpoints tab | §2.2, UI-DESIGN §Zone 5 | Extract breakpoint list from inspection panel. Show active breakpoints, hit counts, type, address. Add/Remove/Toggle controls. |
| Output / Log tab | §4 Weaknesses, UI-DESIGN §Zone 5 | New tab for agent status messages, tool call log, error messages. First step toward structured logging. |

### 2B — New Bottom Tabs (AI-only → UI Parity)

| Item | Tools Surfaced | Parity Impact |
|------|---------------|---------------|
| Scripts tab | `ListScripts`, `ToggleScript`, `EnableScript`, `DisableScript` | Scripting: 0% → ~40% |
| Snapshots tab | `CaptureSnapshot`, `CompareSnapshots`, `CompareSnapshotWithLive`, `ListSnapshots`, `DeleteSnapshot` | Snapshots: 0% → 100% |
| Find Results tab | `FindWritersToOffset`, `TraceFieldWriters` → results display | Disassembly & Analysis: 7% → ~20% |
| Hotkeys tab | `SetHotkey`, `ListHotkeys`, `RemoveHotkey` | Hotkeys: 0% → 100% |
| Hit Log sub-tab | `GetBreakpointHitLog`, `DumpRegisterState`, `GetCodeCaveHookHits` | Breakpoints & Hooks: 25% → ~50% |
| Code Cave Hooks panel | `InstallCodeCaveHook`, `RemoveCodeCaveHook`, `DryRunHookInstall`, `ListCodeCaveHooks` | Breakpoints & Hooks: → ~60% |
| Journal / Audit Trail tab | `PatchHistory`, `ListJournalEntries`, `BeginTransaction`, `RollbackTransaction` | Safety: 0% → ~30% |

---

## Phase 3: Center Document Tabs — Core Feature Windows

**Goal:** Build the major interactive views that CE users expect. These are the biggest parity gaps.

### 3A — Interactive Disassembler (Review §2.3: 35% parity → target 70%)

The #1 missing feature per the review. CE's disassembler is its core navigation tool.

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Scrollable instruction list | `Disassemble`, `DisassembleFunction` | Address, bytes, mnemonic, operands — themed, navigable |
| Go-to address bar | — | Hex address input with navigation history |
| Function boundary markers | `FindFunctionBoundaries` | Visual start/end markers |
| Cross-reference annotations | `GetCallerGraph` | Clickable xref labels on call/jump targets |
| Context menu | Multiple | Set Breakpoint, Find What Writes, Generate Signature, Copy, Follow Jump/Call |
| Instruction search | `SearchInstructionPattern`, `FindByMemoryOperand` | Regex search bar with result navigation |
| Instruction info tooltips | `GetInstructionInfo` | Hover details |
| Symbol display | `ResolveSymbol` | Module+offset labels (ASLR-aware) |
| Inline assembly editing | `AssembleInstruction` | Click instruction → edit → reassemble |
| Symbol loading | PDB, .NET metadata | Phase 3A: module exports + .NET metadata. Phase 7 stretch: PDB/DWARF symbol loading. |
| Comment / label annotations | — | User annotations on instructions (IDA-style); persisted per session |
| Copy disassembly ranges | — | Select range → copy formatted text to clipboard |
| Risk assessment before breakpoints | `ProbeTargetRisk` | Show risk dialog before committing breakpoint in hot code |
| Signature testing | `TestSignatureUniqueness` | After Generate Signature, verify uniqueness in target |

### 3B — Script Editor (Review §2.4: 50% parity → target 80%)

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Script list (left pane) | `ListScripts` | List with enable/disable status icons |
| Editor pane | `ViewScript`, `EditScript` | Syntax-highlighted Auto Assembler editor (CE format) |
| New / Save / Delete | `CreateScriptEntry` | Script lifecycle management |
| Validate / Deep Validate | `ValidateScript`, `ValidateScriptDeep` | Button toolbar with inline results |
| Enable / Disable | `EnableScript`, `DisableScript` | Toggle buttons |
| Code generation templates | `GenerateAutoAssemblerScript`, `GenerateLuaScript` | Quick-insert: AOB inject, code cave, NOP, JMP |
| Assembly syntax support | — | CE-compatible Auto Assembler syntax; full FASM/NASM compatibility is a Phase 7C stretch goal |

### 3C — Structure Dissector (Review §2.3 gap, §8 #6 High priority)

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Address input | — | Base address with optional auto-detect |
| Structure grid | `DissectStructure` | Offset, type, value, name — editable columns |
| Pointer follow | Address resolution | Click pointer fields → navigate to target |
| Export | `GenerateStructDefinition` | Export as C struct or CE structure definition |
| Side-by-side compare | `CompareSnapshots` | Two instances of same structure |

### 3D — Pointer Scanner (Review §2.6: 30% parity → target 60%)

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Target address input | — | Address, max depth, max offset fields |
| Scan with progress | `ScanForPointers` | Progress bar, cancel support |
| Results list | Scan results | Chains ranked by stability |
| Cross-restart validate | `ValidatePointerPaths` | Validate button with status column |
| Add to address table | — | Send validated chain to address table |
| Stability ranking | `RankPointerPaths` | Visual stability column |

### 3E — Debugger UI (Review §8 #3: Critical)

Interactive debugging view — CE's full debugger interface. Can be integrated into the Disassembler tab or as a separate debug mode.

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Register view | `DumpRegisterState` | All registers displayed, editable, with changed-value highlighting |
| Stack view | `GetCallStack` | Call stack with frame navigation |
| Single-step execution | New engine work | Step Into / Step Over / Step Out / Run to Cursor |
| Instruction-level stepping | New engine work | Step through disassembly one instruction at a time |
| Break-and-trace | New engine work | Trace execution path from a breakpoint (log instructions hit) |
| Trace window | New engine work | Dedicated trace output panel showing step-by-step instruction execution log |
| Debug toolbar | — | Continue, Pause, Step In/Over/Out, Stop buttons |
| Watch expressions | New | User-defined expressions evaluated on break |

---

## Phase 4: Explorer Sidebar & Process Intelligence

**Goal:** Build the left sidebar into a proper process exploration tool. Addresses Process & Attachment (20% parity) and Memory (10% parity).

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Module list (filterable) | `ListModules` via engine | Sortable list with base address, size, path. Expandable to show exports/imports. |
| Thread list with status | `GetAllThreadStacks`, `GetCallStack` | Thread ID, state, current instruction, expandable stack |
| Memory regions overview | `QueryMemoryProtection` | Visual memory map with protection flags, module ownership |
| Process details panel | `InspectProcess` | Enhanced info: arch, parent, command line, modules count |
| Workspace panel | `ListSessions`, `LoadCheatTable` | Recent sessions + cheat tables (SQLite-backed persistence) |

---

## Phase 5: Memory Browser Enhancements

**Goal:** Upgrade the embedded memory browser from a simple hex viewer to a proper hex editor. Addresses Memory Read/Write (10% parity → target 80%).

| Item | Tools Surfaced | Details |
|------|---------------|---------|
| Inline hex editing | `WriteMemory` | Click byte → edit → write to process |
| Data inspector panel | `ProbeAddress` | Selected bytes shown as all types (int8/16/32/64, float, double, string, pointer) |
| Memory allocation toolbar | `AllocateMemory`, `FreeMemory` | Allocate code caves from browser |
| Protection display/edit | `QueryMemoryProtection`, `ChangeMemoryProtection` | Status bar shows flags, context menu to change |
| Search within memory | — | Byte pattern search with highlight |
| Copy hex / ASCII ranges | — | Selection + copy support |
| Inline disassembly mode | `Disassemble` | Toggle hex view ↔ disassembly view (like CE) |
| Structure spider | `DissectStructure` | Navigate pointer chains visually, expand nested structs (user-guided with auto-suggestions) |
| Code injection templates | `GenerateCodeCave` | Insert common patterns (NOP slide, JMP hook, infinite health) from template picker |
| Dissect code/data context menus | `DissectStructure`, `Disassemble` | Right-click → "Dissect Data" or "Open in Disassembler" from hex view |

---

## Phase 6: Command Bar & UX Polish

**Goal:** Replace the traditional menu bar with a modern command bar and complete keyboard navigation. Addresses UI/UX (20% parity → target 60%).

### 6A — Command Bar

| Item | Review Source | Details |
|------|-------------|---------|
| Process dropdown + Attach/Detach | UI-DESIGN §Zone 1 | Quick process selection from toolbar |
| Scan button | UI-DESIGN §Zone 1 | Open/focus scanner panel, Ctrl+F |
| Save / Undo / Redo | §2.5, UI-DESIGN §Zone 1 | Ctrl+S, Ctrl+Z, Ctrl+Y |
| Emergency Stop | §3, UI-DESIGN §Zone 1 | Force detach + rollback, Ctrl+Shift+Esc |
| Run Script (F5) | UI-DESIGN §Zone 1 | Toggle selected script |
| Hamburger menu | UI-DESIGN §Zone 1 | File, Edit, View, Tools, Skills, Help |
| Settings gear | UI-DESIGN §Zone 1 | Direct access |

### 6B — Keyboard Navigation & UX

| Item | Review Source | Details |
|------|-------------|---------|
| Full keyboard nav | §2.7 gap | Tab through all panels, Enter to activate, Esc to cancel |
| Process filter/search | §2.7 gap | TextBox filter in process list |
| Address table drag-drop | §2.5 gap | Reorder entries, drag to groups |
| Address table column sorting | §2.5 gap | Click column headers |
| Address table color coding | §2.5 gap | User-selectable per entry |
| In-place description editing | §2.5 gap | Click description → edit inline |
| Change record highlighting | §2.5 gap | Flash value when it changes |
| Tooltips on all elements | §2.7 gap | Contextual help throughout |
| Right-click context menus everywhere | §2.7 gap | Every list, every panel |
| .CT file associations | §2.7 gap | Double-click .CT file → opens in CE AI Suite |
| AI chat transcript search UI | `SearchChatHistory` | Search icon in chat header → search input |
| Screenshot capture integration | `CaptureProcessWindow` | Tools menu → "Capture Window" (saves/attaches to chat) |
| Investigation report export | `SummarizeInvestigation` | File menu → "Export Report" → AI generates markdown summary |

### 6C — Status Bar Enhancements

| Item | UI-DESIGN §Zone 6 | Details |
|------|-------------------|---------|
| Process info | — | `game.exe (PID 1234, x64)` |
| Watchdog indicator | `ProcessWatchdog` | ✓ active / ⚠ crashed / — inactive |
| AI status | Agent events | Ready / Thinking / Tool: X / Error |
| Token usage | Token tracking | `Tokens: 2.1K prompt / 0.8K response` |
| Scan status | Scanner state | `42 results (Int32, exact)` |
| Profile indicator | Token limits | Click to change preset |

---

## Phase 7: Engine Feature Gaps

**Goal:** Fill the engine-level gaps that limit what both the AI and UI can do. These are the features that require new P/Invoke work, not just UI.

### 7A — Scanning Improvements (Review §2.1: 60% → target 85%)

| Item | Review Source | Details |
|------|-------------|---------|
| Multi-threaded scanning | §8 #5 Critical | Partition regions across threads, proper progress reporting |
| Fast scan alignment | §2.1 gap | Configurable 1/2/4/8 byte alignment for speed |
| Undo scan | §2.1 gap | Revert to previous narrowing step (store scan history) |
| Hex/decimal toggle in scan UI | §2.1 gap | Per-scan display mode |
| Round float tolerance | §2.1 gap | Configurable epsilon for float comparisons |
| Larger region support | §2.1 gap | Remove 16MB/region cap, handle full 48-bit address space |
| Writable-only toggle | §2.1 gap | Filter by page protection during scan |
| Grouped scans | §2.1 gap | Scan for multiple values simultaneously |
| Paused process scanning | §2.1 gap | Suspend target during scan for consistency |
| Bit-level scanning | §2.1 gap | Scan for individual bit changes |
| Custom type definitions | §2.1 gap | User-defined structures as scan types |
| Memory-mapped file scanning | §2.1 gap | Scan memory-mapped files in target |

### 7B — Breakpoint & Debugging Improvements (Review §2.2: 70% → target 85%)

| Item | Review Source | Details |
|------|-------------|---------|
| Conditional breakpoints | §2.2 gap, §8 #8 High | Break only when expression is true (register/memory conditions) |
| Break-and-trace | §2.2 gap | Trace execution path from breakpoint |
| Changed register highlighting | §2.2 gap | Highlight registers that changed since last hit |
| Thread-specific breakpoints | §2.2 gap | Break only on specific thread |
| Breakpoint scripting | §2.2 gap, §8 Lua | Lua-controlled breakpoint behavior (depends on Phase 8) |

### 7C — Auto Assembler Improvements (Review §2.4: 50% → target 75%)

> **Priority note:** `aobscanmodule` is CE's most-used AA directive (§8 #7 High). Implement it first within this phase, or promote to Phase 3B if the Script Editor ships earlier.

| Item | Review Source | Details |
|------|-------------|---------|
| `aobscanmodule` directive | §2.4 gap, §8 #7 High | AOB scan within a specific module — CE's most-used AA directive |
| `registersymbol` / `unregistersymbol` | §2.4 gap, §8 #7 High | Share symbols between scripts |
| `createthread` directive | §2.4 gap | Create remote thread in target |
| `readmem` / `writemem` directives | §2.4 gap | Memory block copy in scripts |
| Script variables | §2.4 gap | AA-level variable declarations |
| `{$strict}` / `{$luacode}` pragmas | §2.4 gap | Compatibility with CE scripts |
| `loadlibrary` directive | §2.4 gap | Load DLL into target process from script |
| Include files | §2.4 gap | Shared script libraries via `{$include}` |

### 7D — Address Table Improvements (Review §2.5: 65% → target 90%)

| Item | Review Source | Details |
|------|-------------|---------|
| Show as signed/unsigned toggle | §2.5 gap | Per-entry display mode |
| Show as hex toggle per entry | §2.5 gap | Per-entry hex/decimal (partially exists via context menu) |
| Increase/decrease value hotkeys | §2.5 gap | +/- hotkeys for quick value adjustment |
| Group header activation | §2.5 gap | Enable all children when group is checked |
| Dropdown value selection | §2.5 gap | Combo-box for enum-style values |

### 7E — Pointer Scanner Improvements (Review §2.6: 30% → target 70%)

| Item | Review Source | Details |
|------|-------------|---------|
| Pointer map file format (.PTR) | §2.6 gap | Save/load pointer maps for offline analysis |
| Multi-pointer scan comparison | §2.6 gap | Compare maps from different runs |
| Configurable max depth/offset | §2.6 gap | User-tunable constraints |
| Module-filtered scanning | §2.6 gap | Only scan pointers in specific modules |
| Scan cancel/resume | §2.6 gap | Progress UI with cancel + resume capability |

---

## Phase 8: Lua Scripting Engine

**Goal:** Add Lua scripting — CE's most powerful feature and the #2 critical gap after tool parity. This enables community scripts, complex automation, and CE table compatibility.

| Item | Review Source | Details |
|------|-------------|---------|
| Lua 5.4 runtime integration | §2.4, §8 #2 Critical | Embed NLua or MoonSharp in the application |
| CE API bindings | §2.4 | `readInteger`, `writeFloat`, `getAddress`, `getProcessId`, etc. |
| Form designer bindings | §2.4 | `createForm`, `createButton`, etc. for trainer UIs |
| `{$luacode}` pragma in AA | §2.4 | Inline Lua in Auto Assembler scripts |
| Lua console / REPL | — | Interactive scripting panel (bottom tab) |
| Script file management | — | Load/save .lua files, recent scripts |
| CE table Lua extraction | — | Run Lua from imported .CT files |

---

## Phase 9: Infrastructure & Quality

**Goal:** Address the architectural and operational gaps from the review. These are force multipliers that make everything else easier.

### 9A — Dependency Injection (Review §4, §8 #9 High)

| Item | Details |
|------|---------|
| DI container (Microsoft.Extensions.DependencyInjection) | Replace `new`-ing services in MainWindow code-behind |
| Service registration | Register all services (engine, AI, settings, tools) in DI container |
| Constructor injection | All windows/controls receive dependencies via constructor |
| Testability improvement | Services become mockable; enables true integration tests |

### 9B — CI/CD Pipeline (Review §4, §8 #10 High)

| Item | Details |
|------|---------|
| GitHub Actions workflow | Build + test on push/PR |
| Automated test execution | All 65+ xUnit tests run on every commit |
| Release builds | Automated publish to GitHub Releases |
| Code coverage reporting | Track coverage trends |

### 9C — Logging & Telemetry (Review §4 Weakness)

| Item | Details |
|------|---------|
| Structured logging (Serilog) | Replace `Debug.WriteLine` with proper structured logging |
| File sink | Log to `%LOCALAPPDATA%\CEAISuite\logs\` |
| Output panel integration | Route log events to the Output tab |
| Optional crash reporting | Opt-in error telemetry for field debugging |

### 9D — Testing Expansion (Review §5: 50% → target 70%)

| Item | Details |
|------|---------|
| Integration tests | Test full AI→Engine→Memory pipelines with mock processes |
| UI automation tests | Basic smoke tests for window lifecycle |
| Scan engine benchmarks | Performance regression tests for scanning |
| CT import/export round-trip tests | Ensure CE table compatibility |

---

## Phase 10: Advanced Features & Ecosystem

**Goal:** Long-term features that bring CE AI Suite toward full CE parity and beyond.

| Item | Review Source | Details |
|------|-------------|---------|
| Trainer generation GUI | §2.7 gap, §8 #15 | Build standalone .exe trainers from address table |
| Speed hack | §2.7 gap | Game clock manipulation (kernel timer hooking) |
| D3D/OpenGL overlay | §2.7 gap | In-game value display |
| Kernel-mode debugging | §2.2 gap | Kernel driver for anti-debug bypass |
| VEH debugging | §2.2 gap | Vectored Exception Handler debugging mode |
| Plugin system | §5 Ecosystem (5%) | Community-contributed extensions |
| Multi-platform exploration | §1 | Investigate macOS/Linux via cross-platform engine abstractions |
| Community distribution | §5 Ecosystem | Package management, update channel, website |

---

## Parity Tracking

Current → Target parity by category after each phase:

| Category | Current | After Ph 2 | After Ph 3 | After Ph 5 | After Ph 7 | Target |
|----------|---------|-----------|-----------|-----------|-----------|--------|
| Process & Attachment | 20% | 20% | 20% | 20% | 20% | 80%* |
| Memory Read/Write | 10% | 10% | 10% | 80% | 80% | 90% |
| Scanning | 67% | 67% | 67% | 67% | 90% | 95% |
| Disassembly & Analysis | 7% | 20% | 70% | 70% | 70% | 85% |
| Breakpoints & Hooks | 25% | 50% | 50% | 50% | 80% | 90% |
| Address Table | 67% | 67% | 67% | 67% | 90% | 95% |
| Scripting | 0% | 40% | 80% | 80% | 80% | 95%** |
| Pointer Resolution | 0% | 0% | 60% | 60% | 70% | 80% |
| Structure Discovery | 0% | 0% | 100% | 100% | 100% | 100% |
| Snapshots | 0% | 100% | 100% | 100% | 100% | 100% |
| Session & History | 60% | 60% | 60% | 60% | 60% | 80% |
| Safety & Watchdog | 0% | 30% | 30% | 30% | 50% | 80% |
| Hotkeys | 0% | 100% | 100% | 100% | 100% | 100% |
| **Overall** | **~21%** | **~42%** | **~62%** | **~72%** | **~82%** | **90%+** |

*Process & Attachment parity improves with Phase 4 (Explorer Sidebar).  
**Scripting reaches 95% after Phase 8 (Lua Engine).

---

## Critical Path

The review (§8) ranks gaps by impact. Here is the critical path through the phases:

```
Phase 1 ✅ (Foundation)
    │
    ├── Phase 2 (Bottom Panels) ← closes Snapshots, Hotkeys, partial Scripts, Journal/Audit
    │       │
    │       ├── Phase 3A (Disassembler) ← §8 #3 Critical, #4 Critical + symbols, annotations
    │       ├── Phase 3B (Script Editor) ← §8 #2 via UI surface
    │       ├── Phase 3C (Structure Dissector) ← §8 #6 High
    │       ├── Phase 3D (Pointer Scanner)
    │       └── Phase 3E (Debugger UI) ← §8 #3 Critical (stepping, registers, call stack)
    │
    ├── Phase 4 (Explorer Sidebar) ← can run parallel to Phase 3
    ├── Phase 5 (Memory Browser+) ← can run parallel to Phase 3
    │
    ├── Phase 6 (Command Bar & UX) ← depends on Phases 2-5 for content
    │       └── includes: SearchChatHistory UI, CaptureProcessWindow, SummarizeInvestigation export
    │
    ├── Phase 7A (Multi-threaded scanning) ← §8 #5 Critical, independent
    ├── Phase 7B-E (Engine gaps) ← independent of UI phases
    │       └── includes: bit-level scanning, thread-specific BPs, loadlibrary, .CT associations
    │
    ├── Phase 8 (Lua) ← §8 #2 Critical, independent of UI phases
    │
    ├── Phase 9 (DI, CI/CD, Logging) ← can run anytime, sooner = better
    │
    └── Phase 10 (Advanced) ← long-term
```

**Highest-impact order:**
1. ✅ Phase 1 — Done
2. Phase 2 — Quick wins (Snapshots, Hotkeys go from 0% to 100%, Journal/Audit Trail)
3. Phase 3A — Disassembler (largest single feature gap, includes symbol loading)
4. Phase 3E — Debugger UI (§8 #3 Critical — stepping, registers, call stack)
5. Phase 7A — Multi-threaded scanning (performance critical)
6. Phase 3B — Script Editor (enables manual scripting workflow)
7. Phase 8 — Lua (CE's killer feature)
8. Phase 9A — DI (architectural debt, grows more painful over time)

---

## Summary

| Phase | Theme | Key Outcome |
|-------|-------|-------------|
| **1** ✅ | Foundation | Dockable panels, Memory Browser tab, theme sync |
| **2** | Bottom Panels | 7 new tabs; Snapshots, Hotkeys, Journal reach 100% parity |
| **3** | Core Windows | Disassembler + symbols, Script Editor, Structure Dissector, Pointer Scanner, **Debugger UI** |
| **4** | Explorer Sidebar | Module list (w/ exports), thread list, memory map |
| **5** | Memory Browser+ | Hex editing, data inspector, protection tools, structure spider |
| **6** | UX Polish | Command bar, keyboard nav, drag-drop, chat search UI, investigation export |
| **7** | Engine Gaps | Multi-threaded scan, bit-level scan, conditional BPs, AA directives + loadlibrary |
| **8** | Lua | Full Lua 5.4 scripting engine with CE API bindings |
| **9** | Infrastructure | DI, CI/CD, logging, expanded tests |
| **10** | Advanced | Trainers, overlays, kernel debugging, plugins |
