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

## Phase 2: Bottom Panel Buildout ✅ COMPLETE

**Goal:** Extract existing functionality into proper tabbed bottom panels and add missing tabs. This addresses the scanner, breakpoints, and output being trapped in the center area.

### 2A — Extract & Reorganize Existing Content

| Item | Status | Notes |
|------|--------|-------|
| Scan Results tab | ✅ Done | Full scanner with scan type/data type dropdowns, new/next/reset, result list with Add to Table |
| Breakpoints tab | ✅ Done | Dedicated tab with list, Refresh/Remove/Remove All controls |
| Output / Log tab | ✅ Done | Structured log with timestamps, tool calls, errors |

### 2B — New Bottom Tabs (AI-only → UI Parity)

| Item | Status | Notes |
|------|--------|-------|
| Scripts tab | ✅ Done | List + Enable/Disable/Toggle |
| Snapshots tab | ✅ Done | Capture/Compare/CompareWithLive/Delete + diff viewer |
| Find Results tab | ✅ Done | Display surface ready; wired to context menu handlers |
| Hotkeys tab | ✅ Done | List/Remove (Register via AI or address table context menu) |
| Hit Log sub-tab | ✅ Done | Inside Breakpoints tab |
| Code Cave Hooks panel | ✅ Done | Inside Breakpoints tab |
| Journal / Audit Trail tab | ✅ Done | Patch History + Operations with rollback |

### 2C — Layout & Infrastructure (added during Phase 2)

| Item | Status | Notes |
|------|--------|-------|
| Layout versioning | ✅ Done | Auto-resets stale layout.xml when panel structure changes |
| Token budgeting | ✅ Done | Per-tool caps + ToolResultStore spill-to-store for AI token conservation |
| Dark/light theme parity | ✅ Done | All panels, menus, dropdowns, toolbars themed correctly in both modes |

**All Phase 2 items complete.**

---

## Phase 2.5: Architectural Refactor (MVVM + DI) ✅ COMPLETE

**Goal:** Extract the 3,800+ line MainWindow.xaml.cs into ViewModels with dependency injection before Phase 3 adds ~2,000 more lines. This is a prerequisite — not a nice-to-have.

**Result:** 18 ViewModels extracted (7 more than the 11 planned). DI fully wired in App.xaml.cs with 30+ services registered. MainWindow.xaml.cs reduced to ~1,250 lines (AvalonDock framework wiring). INavigationService, IDialogService, IClipboardService, IDispatcherService all implemented. CommunityToolkit.Mvvm used throughout. ViewModel test coverage at ~55%.

### 2.5A — DI Container & Service Registration

| Item | Details |
|------|---------|
| Add Microsoft.Extensions.DependencyInjection | NuGet package to Desktop project |
| Create `ServiceCollectionExtensions` | Register all existing services (engine, AI, settings, scanning, breakpoints, snapshots, etc.) |
| Wire DI in App.xaml.cs | Build ServiceProvider at startup, resolve MainWindow from container |
| Constructor injection for MainWindow | Replace all `new Service()` calls with injected dependencies |

### 2.5B — Extract ViewModels from MainWindow.xaml.cs

| Item | Lines Moved (est.) | Details |
|------|-------------------|---------|
| `AddressTableViewModel` | ~400 | Address table CRUD, grouping, lock/unlock, context menus, refresh timer |
| `ScannerViewModel` | ~300 | Scan state, new/next/reset, results list, add-to-table |
| `InspectionViewModel` | ~200 | Selected-address inspection, type/value display |
| `BreakpointsViewModel` | ~250 | Breakpoint list, hit log, code cave hooks sub-tabs |
| `AiOperatorViewModel` | ~500 | Chat messages, send/receive, model selection, history, conversations |
| `ScriptsViewModel` | ~150 | Script list, enable/disable/toggle |
| `SnapshotsViewModel` | ~200 | Capture, compare, delete, diff display |
| `ProcessesViewModel` | ~150 | Process list, attach/detach |
| `ToolbarViewModel` | ~200 | All toolbar button commands (save, undo/redo, scan, run/stop, emergency stop) |
| `StatusBarViewModel` | ~100 | Process info, AI status, density preset, scan status |
| `MainWindowViewModel` | ~200 | Layout orchestration, ShowPanel routing, theme management |

**Target:** MainWindow.xaml.cs drops from ~3,800 to ~500 lines (XAML bindings + minimal code-behind for AvalonDock wiring).

### 2.5C — Supporting Infrastructure

| Item | Details |
|------|---------|
| `RelayCommand` / `AsyncRelayCommand` | ICommand implementations (or use CommunityToolkit.Mvvm) |
| `ObservableObject` base class | INotifyPropertyChanged base for all ViewModels |
| `INavigationService` | Interface for ShowPanel/SwitchTab routing between ViewModels |
| `IDialogService` | Interface for MessageBox/confirmation dialogs (testable) |
| Unit tests for ViewModels | Key logic tests without UI dependency |

---

## Phase 3: Center Document Tabs — Core Feature Windows ✅ COMPLETE

**Goal:** Build the major interactive views that CE users expect. These are the biggest parity gaps.

**Result:** All five center document tabs shipped with full ViewModel + XAML + DI wiring. Gap closure pass completed all remaining items (xrefs, symbol display, tooltips, Find What Writes, Generate Signature, inline assembly editing, risk assessment, pointer validation, side-by-side compare, CE export, AvalonEdit syntax highlighting, register change highlighting). 152 tests passing, 6 new tests added during gap closure.

### 3A — Interactive Disassembler ✅ (Review §2.3: 35% parity → target 70%)

The #1 missing feature per the review. CE's disassembler is its core navigation tool.

| Item | Tools Surfaced | Status |
|------|---------------|--------|
| Scrollable instruction list | `Disassemble`, `DisassembleFunction` | ✅ Done — ListView with GridView columns |
| Go-to address bar | — | ✅ Done — with back/forward navigation history |
| Function boundary markers | `FindFunctionBoundaries` | ✅ Done — CurrentFunctionLabel display |
| Cross-reference annotations | `GetCallerGraph` | ✅ Done — XrefLabel column populated via ResolveXrefTarget |
| Context menu | Multiple | ✅ Done — Set BP, Find What Writes, Generate Signature, Edit Instruction, Follow Jump/Call, Copy |
| Instruction search | `SearchInstructionPattern`, `FindByMemoryOperand` | ✅ Done — regex search bar |
| Instruction info tooltips | `GetInstructionInfo` | ✅ Done — MultiBinding tooltip with address, bytes, module, xref |
| Symbol display | `ResolveSymbol` | ✅ Done — Module+offset column via ResolveModuleOffset |
| Inline assembly editing | `AssembleInstruction` | ✅ Done — dialog → AA script → Keystone assembly |
| Symbol loading | PDB, .NET metadata | ⏳ Phase 7 stretch — module exports only for now |
| Comment / label annotations | — | ✅ Done — data model + Comment column (persistence deferred) |
| Copy disassembly ranges | — | ✅ Done — formatted text to clipboard |
| Risk assessment before breakpoints | `ProbeTargetRisk` | ✅ Done — warns on ret/int3/ntdll targets |
| Signature testing | `TestSignatureUniqueness` | ✅ Done — integrated into Generate Signature (tests + copies) |

### 3B — Script Editor ✅ (Review §2.4: 50% parity → target 80%)

| Item | Tools Surfaced | Status |
|------|---------------|--------|
| Script list (left pane) | `ListScripts` | ✅ Done — ListBox with double-click load |
| Editor pane | `ViewScript`, `EditScript` | ✅ Done — AvalonEdit with AA syntax highlighting |
| New / Save / Delete | `CreateScriptEntry` | ✅ Done |
| Validate / Deep Validate | `ValidateScript`, `ValidateScriptDeep` | ✅ Done — inline validation results |
| Enable / Disable | `EnableScript`, `DisableScript` | ✅ Done |
| Code generation templates | `GenerateAutoAssemblerScript`, `GenerateLuaScript` | ✅ Done — AOB inject, code cave, NOP, JMP |
| Assembly syntax support | — | ✅ Done — AvalonEdit with AutoAssembler.xshd (sections, directives, registers, mnemonics) |

### 3C — Structure Dissector ✅ (Review §2.3 gap, §8 #6 High priority)

| Item | Tools Surfaced | Status |
|------|---------------|--------|
| Address input | — | ✅ Done — with region size and type hint |
| Structure grid | `DissectStructure` | ✅ Done — DataGrid with editable names |
| Pointer follow | Address resolution | ✅ Done |
| Export as C struct | `GenerateStructDefinition` | ✅ Done — copies to clipboard |
| Export as CE structure definition | `GenerateStructDefinition` | ✅ Done — XML format with Vartype/Bytesize |
| Side-by-side compare | `CompareSnapshots` | ✅ Done — compare DataGrid with diff highlighting |

### 3D — Pointer Scanner ✅ (Review §2.6: 30% parity → target 60%)

| Item | Tools Surfaced | Status |
|------|---------------|--------|
| Target address input | — | ✅ Done — address, max depth, max offset fields |
| Scan with progress | `ScanForPointers` | ✅ Done — with cancel support |
| Results list | Scan results | ✅ Done — chains with module display |
| Cross-restart validate | `ValidatePointerPaths` | ✅ Done — re-walks chain, reports Stable/Drifted/Broken |
| Add to address table | — | ✅ Done |
| Stability ranking | `RankPointerPaths` | ✅ Done — status column updated by ValidatePathsCommand |

### 3E — Debugger UI ✅ (Review §8 #3: Critical — stepping deferred to Phase 7)

Interactive debugging view — CE's full debugger interface. Stepping commands are stubbed and disabled until Phase 7 engine support.

| Item | Tools Surfaced | Status |
|------|---------------|--------|
| Register view | `DumpRegisterState` | ✅ Done — all registers displayed with changed-value highlighting (red + bold) |
| Stack view | `GetCallStack` | ✅ Done — call stack with frame navigation |
| Single-step execution | New engine work | ⏳ Phase 7 — commands stubbed, disabled |
| Instruction-level stepping | New engine work | ⏳ Phase 7 |
| Break-and-trace | New engine work | ⏳ Phase 7 |
| Trace window | New engine work | ⏳ Phase 7 |
| Debug toolbar | — | ✅ Done — Step In/Over/Out/Continue buttons (disabled until Phase 7) |
| Watch expressions | New | ⏳ Phase 7 |

---

## Phase 4: Explorer Sidebar & Process Intelligence ✅ COMPLETE

**Goal:** Build the left sidebar into a proper process exploration tool. Addresses Process & Attachment (20% parity) and Memory (10% parity).

**Result:** 4 new sidebar tabs (Modules, Threads, Memory Map, Workspace) + enhanced Processes panel with process details. All auto-refresh on process attach. 168 tests passing, 16 new tests added.

| Item | Tools Surfaced | Status |
|------|---------------|--------|
| Module list (filterable) | `AttachAsync` → Modules | ✅ Done — filterable list with base address, size; copy address, navigate to disassembler |
| Thread list with status | `WalkAllThreadsAsync`, `WalkStackAsync` | ✅ Done — thread list with expandable call stack, navigate to instruction |
| Memory regions overview | `EnumerateRegionsAsync` | ✅ Done — protection flags (R/RW/RWX/X), module ownership, filterable by protection |
| Process details panel | `IProcessContext.CurrentInspection` | ✅ Done — enhanced Processes panel shows "x64 \| 47 modules" when attached |
| Workspace panel | `SessionService`, `CheatTableParser` | ✅ Done — list/load/delete sessions + import .CT files |

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

## Phase 6: Command Bar & UX Polish ✅ COMPLETE

**Goal:** Replace the traditional menu bar with a modern command bar and complete keyboard navigation. Addresses UI/UX (20% parity → target 60%).

**Result:** All 3 sub-phases complete. Token usage + scan status + watchdog in status bar. Process list filter. Screenshot capture and report export in Tools menu (same IScreenCaptureEngine as AI tool). Context menus on all panels. Address table: column sorting, value change highlighting (red flash), color coding. 291 tests passing.

### 6A — Command Bar ✅ COMPLETE

| Item | Status | Details |
|------|--------|---------|
| Process dropdown + Attach/Detach | ✅ Done | Toolbar process selection with Attach/Detach buttons |
| Scan button | ✅ Done | Opens/focuses scanner panel |
| Save / Undo / Redo | ✅ Done | Ctrl+S, Ctrl+Z, Ctrl+Y |
| Emergency Stop | ✅ Done | Force detach + rollback, Ctrl+Shift+Esc |
| Run Script (F5) | ✅ Done | Toggle selected script from toolbar |
| Hamburger menu | ✅ Done | File, Edit, View, Tools, Skills, Help |
| Settings gear | ✅ Done | Direct access |
| Token usage display | ✅ Done | Status bar shows `$0.004 | 2.1K↑ 0.8K↓` (via AiOperatorService.TokenBudget) |

### 6B — Keyboard Navigation & UX

| Item | Status | Details |
|------|--------|---------|
| Full keyboard nav | ✅ Done | Address table: Del, Space, F2, Enter, F5, Ctrl+C/X/V, Ctrl+F, Ctrl+Z/Y |
| Process filter/search | ✅ Done | TextBox filter by name or PID in process list sidebar |
| Address table drag-drop | ✅ Done | Drop addresses from external sources; reorder via groups |
| Address table column sorting | ✅ Done | Click column headers (Label, Address, Type, Value) to toggle ascending/descending |
| Address table color coding | ✅ Done | Right-click → Set Color → 8 predefined colors + None |
| In-place description editing | ✅ Done | F2 opens description editor (dialog-based) |
| Change record highlighting | ✅ Done | Values flash red briefly when they change during auto-refresh |
| Tooltips on all elements | ✅ Done | 50+ tooltip elements across toolbar, panels, status bar |
| Right-click context menus everywhere | ✅ Done | Added to Output Log (Copy/Clear), Hotkeys (Remove/Refresh); scanner already had full menu |
| .CT file associations | ⏳ Deferred | Best done with installer/setup — registry writes required |
| AI chat transcript search UI | ✅ Done | Chat history search with FilterChatHistory in AI panel |
| Screenshot capture integration | ✅ Done | Tools menu → "Capture Screenshot" (same IScreenCaptureEngine as AI tool) |
| Investigation report export | ✅ Done | Tools menu → "Export Report…" (same ScriptGenerationService.SummarizeInvestigation as AI tool) |

### 6C — Status Bar Enhancements ✅ COMPLETE

| Item | Status | Details |
|------|--------|---------|
| Process info | ✅ Done | `Attached: game.exe (PID 1234)` |
| AI status | ✅ Done | Ready / Thinking / Tool: X / Error |
| Profile indicator | ✅ Done | Click to cycle Clean/Balanced/Dense preset |
| Watchdog indicator | ✅ Done | `⚠ Rollback 0x{address}` on auto-rollback, auto-clears after 5s |
| Token usage | ✅ Done | `$0.0042 | 2.1K↑ 0.8K↓` — subscribes to AiOperatorService.StatusChanged |
| Scan status | ✅ Done | `42 results found` — mirrors ScannerViewModel.ScanStatus |

---

## Phase 7: Engine Feature Gaps ✅ COMPLETE

**Goal:** Fill the engine-level gaps that limit what both the AI and UI can do. These are the features that require new P/Invoke work, not just UI.

**Result:** All 5 sub-phases complete. Multi-threaded scanning with Parallel.For, conditional/thread-specific breakpoints, break-and-trace, full AA directive set (aobscanmodule, registersymbol, createthread, readmem/writemem, loadlibrary), address table hex/signed/dropdown/group activation, pointer map save/load/compare. 385 tests passing.

### 7A — Scanning Improvements ✅ (Review §2.1: 60% → 90%)

| Item | Status | Notes |
|------|--------|-------|
| Multi-threaded scanning | ✅ Done | Parallel.For with configurable MaxDegreeOfParallelism, progress reporting |
| Fast scan alignment | ✅ Done | ScanOptions.Alignment (1/2/4/8 byte, default = value size) |
| Undo scan | ✅ Done | ScanService undo stack (max depth 20), CanUndo property |
| Hex/decimal toggle in scan UI | ✅ Done | "Hex" checkbox in scanner panel + ScanOptions.ShowAsHex |
| Round float tolerance | ✅ Done | ScanOptions.FloatEpsilon for configurable epsilon |
| Larger region support | ✅ Done | Full 48-bit address space scanning |
| Writable-only toggle | ✅ Done | ScanOptions.WritableOnly (default true) + UI checkbox |
| Grouped scans | ✅ Done | GroupedScanAsync interface + ScanOptions support |
| Paused process scanning | ✅ Done | SuspendProcess flag in ScanOptions |
| Bit-level scanning | ✅ Done | ScanType.BitChanged + ScanOptions.BitPosition |
| Custom type definitions | ✅ Done | Register/lookup/unregister custom scan types |
| Memory-mapped file scanning | ✅ Done | IncludeMemoryMappedFiles flag in ScanOptions |

### 7B — Breakpoint & Debugging Improvements ✅ (Review §2.2: 70% → 85%)

| Item | Status | Notes |
|------|--------|-------|
| Conditional breakpoints | ✅ Done | BreakpointCondition with RegisterCompare, MemoryCompare, HitCount types |
| Break-and-trace | ✅ Done | TraceFromBreakpointAsync with static instruction-level trace engine |
| Changed register highlighting | ✅ Done | DebuggerViewModel diff from previous snapshot (red + bold) |
| Thread-specific breakpoints | ✅ Done | BreakpointDescriptor.ThreadFilter field |
| Breakpoint scripting | ⏳ Phase 8 | Depends on Lua engine |

### 7C — Auto Assembler Improvements ✅ (Review §2.4: 50% → 80%)

| Item | Status | Notes |
|------|--------|-------|
| `aobscanmodule` directive | ✅ Done | Regex-parsed, scans within specific module |
| `registersymbol` / `unregistersymbol` | ✅ Done | Concurrent symbol table with GetRegisteredSymbols/ResolveSymbol |
| `createthread` directive | ✅ Done | CreateRemoteThread P/Invoke |
| `readmem` / `writemem` directives | ✅ Done | Memory block copy in execution phases 11-12 |
| Script variables | ✅ Done | AA-level variable/define declarations |
| `{$strict}` / `{$luacode}` pragmas | ✅ Done | Strict mode enforced; luacode gracefully skipped (Lua in Phase 8) |
| `loadlibrary` directive | ✅ Done | CreateRemoteThread + LoadLibraryW pattern |
| Include files | ✅ Done | `{$include}` preprocessing |

### 7D — Address Table Improvements ✅ (Review §2.5: 65% → 90%)

| Item | Status | Notes |
|------|--------|-------|
| Show as signed/unsigned toggle | ✅ Done | Per-entry ShowAsSigned property + context menu toggle |
| Show as hex toggle per entry | ✅ Done | Per-entry ShowAsHex + "(Hex)" type suffix display |
| Increase/decrease value hotkeys | ✅ Done | Ctrl+Up/Down with hex-aware parsing |
| Group header activation | ✅ Done | ActivateGroupRecursiveAsync toggles all children |
| Dropdown value selection | ✅ Done | DropDownList dictionary + ConfigureDropDown dialog + CT DropDownListLink import |

### 7E — Pointer Scanner Improvements ✅ (Review §2.6: 30% → 70%)

| Item | Status | Notes |
|------|--------|-------|
| Pointer map file format (.PTR) | ✅ Done | JSON serialization with NuintJsonConverter for hex addresses |
| Multi-pointer scan comparison | ✅ Done | CompareMaps method for cross-run analysis |
| Configurable max depth/offset | ✅ Done | MaxDepth/MaxOffset properties in PointerScannerViewModel |
| Module-filtered scanning | ✅ Done | Module filter support in scan options |
| Scan cancel/resume | ✅ Done | CancellationTokenSource + CanResume state preservation |

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

### 9A — Dependency Injection ← MOVED to Phase 2.5

*DI and MVVM refactor promoted to Phase 2.5 as a prerequisite for Phase 3. See Phase 2.5A for details.*

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

### 9E — UX Gaps (discovered during Phase 2 audit)

| Item | Details |
|------|---------|
| Progress indicators | Spinners/progress bars for scans, breakpoint operations, snapshot captures. Status text alone isn't enough visual feedback for long operations. |
| First-run experience | Onboarding dialog for new users — attach walkthrough, scan tutorial, AI operator intro. Empty state currently gives no guidance. |
| Auto-update mechanism | Check for updates on startup, notify user. No silent updates — user-controlled. |
| Crash recovery | Auto-save address table periodically. On crash, offer to restore last session. |

### 9F — Dependency Hygiene (discovered during Phase 2 audit)

| Item | Risk | Details |
|------|------|---------|
| Microsoft.Agents.AI.OpenAI | Medium | Pre-release (1.0.0-rc4) — monitor for breaking API changes. Pin version, test on upgrade. |
| keystoneengine.csharp | Low | NuGet binding from 2018. Works fine but no upstream maintenance. Consider vendoring if issues arise. |

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
| AI co-pilot mode | Phase 2 discussion | AI controls interface elements (fill forms, navigate tabs, stage actions for user review). Whitelisted actions only — not a general UI automation. Requires Phase 2.5 MVVM for clean command routing. |
| Multi-platform exploration | §1 | Investigate macOS/Linux via cross-platform engine abstractions |
| Community distribution | §5 Ecosystem | Package management, update channel, website |

---

## Parity Tracking

Current → Target parity by category after each phase:

| Category | After Ph 2 ✅ | After Ph 2.5 ✅ | After Ph 3 ✅ | After Ph 4 ✅ | After Ph 5 ✅ | After Ph 6 ✅ | After Ph 7 ✅ | Target |
|----------|-------------|-------------|-----------|-----------|-----------|-----------|-----------|--------|
| Process & Attachment | 20% | 20% | 20% | 45% | 45% | 45% | 45% | 80%* |
| Memory Read/Write | 10% | 10% | 10% | 15% | 80% | 80% | 80% | 90% |
| Scanning | 67% | 67% | 67% | 67% | 67% | 67% | 90% | 95% |
| Disassembly & Analysis | 20% | 20% | 70% | 70% | 70% | 70% | 70% | 85% |
| Breakpoints & Hooks | 50% | 50% | 55% | 55% | 55% | 55% | 85% | 90% |
| Address Table | 67% | 67% | 67% | 67% | 67% | 67% | 90% | 95% |
| Scripting | 40% | 40% | 80% | 80% | 80% | 80% | 80% | 95%** |
| Pointer Resolution | 0% | 0% | 60% | 60% | 60% | 60% | 70% | 80% |
| Structure Discovery | 0% | 0% | 100% | 100% | 100% | 100% | 100% | 100% |
| Snapshots | 100% | 100% | 100% | 100% | 100% | 100% | 100% | 100% |
| Session & History | 60% | 60% | 60% | 75% | 75% | 75% | 75% | 80% |
| Safety & Watchdog | 30% | 30% | 30% | 30% | 30% | 30% | 50% | 80% |
| Hotkeys | 100% | 100% | 100% | 100% | 100% | 100% | 100% | 100% |
| **Overall** | **~42%** | **~42%** | **~62%** | **~65%** | **~72%** | **~72%** | **~82%** | **90%+** |

*Process & Attachment parity improves further with future engine enhancements (parent process, command line).
**Scripting reaches 95% after Phase 8 (Lua Engine).

---

## Critical Path

The review (§8) ranks gaps by impact. Here is the critical path through the phases:

```
Phase 1 ✅ (Foundation)
    │
    Phase 2 ✅ (Bottom Panels) ← Snapshots, Hotkeys at 100%; Scripts, Journal, Breakpoints surfaced
    │
    Phase 2.5 ✅ (MVVM + DI Refactor) ← 18 ViewModels, DI container, CommunityToolkit.Mvvm
    │
    Phase 3 ✅ (Core Windows) ← all 5 tabs + gap closure complete
    │       ├── Phase 3A ✅ Disassembler (xrefs, symbols, tooltips, Find What Writes, signatures, inline edit)
    │       ├── Phase 3B ✅ Script Editor (AvalonEdit syntax highlighting, templates, validation)
    │       ├── Phase 3C ✅ Structure Dissector (CE export, side-by-side compare)
    │       ├── Phase 3D ✅ Pointer Scanner (cross-restart validation, stability ranking)
    │       └── Phase 3E ✅ Debugger UI (register change highlighting; stepping deferred to Phase 7)
    │
    Phase 4 ✅ (Explorer Sidebar) ← modules, threads, memory map, workspace, process details
    │
    Phase 5 ✅ (Memory Browser+) ← hex editing, data inspector, protection tools, structure spider
    │
    Phase 6 ✅ (Command Bar & UX) ← token display, scan status, watchdog, process filter, screenshot/report, context menus, column sorting, color coding
    │
    Phase 7 ✅ (Engine Gaps) ← multi-threaded scan, conditional BPs, trace, AA directives, address table, pointer maps
    │
    ├── Phase 8 (Lua) ← §8 #2 Critical, independent of UI phases
    │
    ├── Phase 9 (CI/CD, Logging, Testing, UX gaps) ← CI/CD should be early
    │       └── includes: auto-update, crash recovery, progress indicators, first-run onboarding
    │
    └── Phase 10 (Advanced) ← long-term; includes AI co-pilot mode
```

**Highest-impact order (updated):**
1. ✅ Phase 1 — Done
2. ✅ Phase 2 — Done (Snapshots, Hotkeys at 100%; 7 new bottom tabs)
3. ✅ Phase 2.5 — Done (18 ViewModels, DI container, MVVM infrastructure)
4. ✅ Phase 3 — Done (5 center tabs + full gap closure; 152 tests)
5. ✅ Phase 4 — Done (4 explorer sidebar tabs + process details; 168 tests)
6. ✅ Phase 5 — Done (Memory Browser+: hex editing, data inspector, structure spider; 291 tests)
7. ✅ Phase 6 — Done (UX Polish: token/scan/watchdog status bar, process filter, screenshot/report, sorting, color coding; 291 tests)
8. ✅ Phase 7 — Done (Engine gaps: multi-threaded scan, conditional BPs, trace, AA directives, address table, pointer maps; 385 tests)
9. **Phase 9B — CI/CD (should be set up now)**
10. Phase 8 — Lua (CE's killer feature)

---

## Summary

| Phase | Theme | Status | Key Outcome |
|-------|-------|--------|-------------|
| **1** | Foundation | ✅ Complete | Dockable panels, Memory Browser tab, theme sync |
| **2** | Bottom Panels | ✅ Complete | 10 new tabs/panels; Snapshots, Hotkeys at 100% parity; token budgeting |
| **2.5** | MVVM + DI Refactor | ✅ Complete | 18 ViewModels, DI container, CommunityToolkit.Mvvm, INavigationService, IDialogService |
| **3** | Core Windows | ✅ Complete | Disassembler (xrefs, symbols, tooltips, inline edit), Script Editor (AvalonEdit), Structure Dissector (CE export, compare), Pointer Scanner (validation), Debugger (register highlighting); 152 tests |
| **4** | Explorer Sidebar | ✅ Complete | Modules (filterable), Threads (expandable stacks), Memory Map (protection flags), Workspace (sessions + CT import), Process details; 168 tests |
| **5** | Memory Browser+ | ✅ Complete | Hex editing, data inspector, protection tools, structure spider |
| **6** | UX Polish | ✅ Complete | Token/scan/watchdog status bar, process filter, screenshot/report export, column sorting, color coding, context menus; 291 tests |
| **7** | Engine Gaps | ✅ Complete | Multi-threaded scan, bit-level scan, conditional/thread BPs, break-and-trace, AA directives (aobscanmodule, registersymbol, createthread, readmem/writemem, loadlibrary), address table hex/signed/dropdown/groups, pointer map save/load/compare; 385 tests |
| **8** | Lua | Planned | Full Lua 5.4 scripting engine with CE API bindings |
| **9** | Infrastructure | Planned | CI/CD, logging, expanded tests, auto-update, crash recovery |
| **10** | Advanced | Long-term | Trainers, overlays, kernel debugging, plugins, AI co-pilot mode |
