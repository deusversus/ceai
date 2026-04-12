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

## Phase 8: Lua Scripting Engine ✅ COMPLETE

**Goal:** Add Lua scripting — CE's most powerful feature and the #2 critical gap after tool parity. This enables community scripts, complex automation, and CE table compatibility.

**Result:** MoonSharp (Lua 5.2, pure C#) embedded with sandboxed execution. 7 sub-phases: core engine, CE API bindings (20+ functions), AA integration ({$luacode} + LuaCall), REPL console tab, CT import Lua execution + 3 AI tools, form designer (createForm/Button/Label/Edit/CheckBox/Timer), breakpoint scripting callbacks. 489 tests passing.

| Item | Review Source | Status | Details |
|------|-------------|--------|---------|
| Lua runtime integration | §2.4, §8 #2 Critical | ✅ Done | MoonSharp 2.0 (Lua 5.2, pure C#, sandboxed — OS/IO/LoadMethods blocked, bit32 included) |
| CE API bindings | §2.4 | ✅ Done | `readInteger`, `writeFloat`, `getAddress`, `openProcess`, `getProcessId`, `readBytes`, `autoAssemble`, + 15 more |
| Form designer bindings | §2.4 | ✅ Done | `createForm`, `createButton`, `createLabel`, `createEdit`, `createCheckBox`, `createTimer` with WPF host |
| `{$luacode}` pragma in AA | §2.4 | ✅ Done | Blocks execute via ILuaScriptEngine; LuaCall() directives invoke Lua functions |
| Lua console / REPL | — | ✅ Done | Bottom tab with Execute/Evaluate/Clear/Reset, Lua.xshd syntax highlighting |
| Script file management | — | ✅ Done | AI tools: ExecuteLuaScript, ValidateLuaScript, EvaluateLuaExpression |
| CE table Lua extraction | — | ✅ Done | CheatTableFile.LuaScript auto-executes on CT import |
| Breakpoint scripting | §2.2 (deferred from 7B) | ✅ Done | RegisterBreakpointCallback + InvokeBreakpointCallbackAsync with register table |

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

**Goal:** Long-term features that bring CE AI Suite toward full CE parity and beyond. Organized into 9 sub-phases ordered by feasibility and impact.

**Complexity key:** Low · Medium · Medium-High · High · Very High

---

### Phase 10A — Plugin System UI ✅ COMPLETE

**Goal:** Surface the already-built `PluginHost`/`ICeaiPlugin` backend (in `AgentLoop/PluginSystem.cs`) with a first-class management UI so users can install, browse, and unload community plugins.

**Why:** The engine is 100% done. This sub-phase costs minimal work and immediately unlocks the community ecosystem.

**Result:** PluginManagerViewModel with plugin discovery, catalog refresh, install from disk and online catalog. PluginHost promoted to DI singleton with AssemblyLoadContext isolation. AI tools: `ListPlugins`, `GetPluginTools`.

**Complexity:** Low

| Item | Details |
|------|---------|
| `PluginManagerService` | Thin observable wrapper around `PluginHost`; exposes `LoadedPlugins`, `InstallFromFileAsync`, `UnloadAsync` |
| `PluginManagerViewModel` | `ObservableCollection<PluginDisplayItem>` with `InstallCommand`, `UnloadCommand`, `OpenPluginDirectoryCommand`, `RefreshCommand` |
| `PluginManagerPanel.xaml` | New left-sidebar anchorable panel (alongside Modules/Threads/Memory Map/Workspace); shows name, version, description, tool count, status |
| `LayoutVersion` bump | `19` → `20` |
| AI tools (`AiToolFunctions.Plugins.cs`) | `ListPlugins()` `[ReadOnly]`, `GetPluginTools(pluginName)` `[ReadOnly]` |
| DI | `AddSingleton<PluginManagerService>()`, `AddSingleton<PluginManagerViewModel>()` — promote `PluginHost` to DI singleton |
| Tests | `PluginManagerViewModelTests.cs` — load/unload/error states with mock `PluginHost` |

---

### Phase 10B — Trainer Generation GUI ✅ COMPLETE

**Goal:** Build a dialog that takes selected address table entries and emits a standalone `.exe` trainer that locks values in the target process while running — CE's most visible "killer app" feature.

**Approach:** Roslyn `CSharpCompilation` generates a self-contained C# source using P/Invoke `WriteProcessMemory` in a loop, compiled in-process to a `.exe`. No dotnet SDK required on end-user machines.

**Result:** `RoslynTrainerCompiler` generates + compiles C# trainer source via Roslyn. `TrainerGeneratorDialog.xaml` with entry checklist, preview source, and build button. AI tools: `GenerateTrainer`, `PreviewTrainerSource`.

**Complexity:** Medium

| Item | Details |
|------|---------|
| `TrainerContracts.cs` (Abstractions) | `TrainerEntry`, `TrainerConfig`, `TrainerBuildResult` records; `ITrainerGeneratorService` interface |
| `RoslynTrainerGeneratorService` (Application) | Generates + compiles C# trainer source via Roslyn; `PreviewSource()` for "View Source" button |
| `TrainerGeneratorDialog.xaml` | Entry checklist (pre-populated from selection), target process, title, refresh interval slider (50–1000ms), Preview Source button, Build Trainer button with progress bar |
| Tools menu entry | `Tools` menu → "Generate Trainer…" |
| `AiToolFunctions.Trainer.cs` | `GenerateTrainer(title, entryIds[], outputPath)` `[Destructive]`, `PreviewTrainerSource(entryIds[])` `[ReadOnly]` |
| NuGet | `Microsoft.CodeAnalysis.CSharp` added to `CEAISuite.Application.csproj` |
| DI | `AddSingleton<ITrainerGeneratorService, RoslynTrainerGeneratorService>()` |
| Tests | `TrainerGeneratorServiceTests.cs` — verify generated source compiles cleanly via `CSharpCompilation.Emit`; no runtime execution in unit tests |

---

### Phase 10C — AI Co-Pilot Mode ✅ COMPLETE

**Goal:** Allow the AI to issue whitelisted UI commands — navigate to a panel, populate scan forms, set address entry values, attach a process — as staged actions shown to the user before execution. Strictly MVVM command invocation through a defined whitelist; not general UI automation.

**Why now:** Phase 2.5 built MVVM specifically for this. `PermissionEngine`, `HookRegistry`, `SkillSystem`, and `AgentStreamEvent.ApprovalRequested` all exist and are waiting for this wiring.

**Result:** `UiCommandBus` with whitelist enforcement, thread-safe dispatch, and ViewModel subscriptions. AI tools: `GetUiCommandWhitelist`, `ExecuteUiCommand`. Settings toggle for approval requirements.

**Complexity:** Medium

| Item | Details |
|------|---------|
| `IUiCommandBus` + command records (Application) | `NavigatePanelCommand`, `PopulateScanFormCommand`, `AddEntryToTableCommand`, `SetEntryValueCommand`, `AttachProcessCommand`; `UiCommandWhitelist` static set |
| `UiCommandBus` implementation | `Dispatch(UiCommand)` + `CommandReceived` event; ViewModels subscribe in constructor |
| ViewModel subscriptions | `ScannerViewModel`, `AddressTableViewModel`, `INavigationService` handle their respective commands |
| `AiToolFunctions.CoPilot.cs` | `GetUiCommandWhitelist()` `[ReadOnly]`, `ExecuteUiCommand(commandType, parametersJson)` `[Destructive]` → routes through `PermissionEngine` → triggers `ApprovalRequested`, `GetCurrentUiState()` `[ReadOnly]` |
| Settings page | "AI Co-Pilot" section: enable/disable toggle, per-command-type whitelist checkboxes, "Require approval for all co-pilot actions" toggle |
| AI panel badge | "Co-Pilot" mode indicator in AI Operator panel when active |
| DI | `AddSingleton<IUiCommandBus, UiCommandBus>()` |
| Tests | `UiCommandBusTests.cs` — dispatch, whitelist enforcement, unknown command rejection; `AiToolFunctionsCopilotTests.cs` — verify approval flow triggered |

---

### Phase 10D — Speed Hack ✅ COMPLETE

**Goal:** Intercept and scale `timeGetTime`, `QueryPerformanceCounter`, `GetTickCount`, and optionally `Sleep` to slow or accelerate game timers. No kernel required.

**Approach:** Initially implemented as IAT patching, but **rewritten to inline hooking** after IAT patching failed on real games (forwarding stubs, delay-loaded DLLs, and multi-module IAT tables made IAT-only approaches unreliable). The final implementation uses Iced disassembler for instruction boundary detection and RIP-relative relocation, with fixed-point scaling in injected trampolines. Removal sets multiplier to 1.0× before unhooking to prevent timing discontinuities.

**Complexity:** Medium-High

**Lessons learned:** IAT patching alone is insufficient for production use — games load timing functions through forwarding stubs, delay-load tables, and statically linked CRT copies that bypass the IAT. Inline hooking at the function entry point is the only reliable approach. Thread suspension during unhooking causes input freezes; ramping to 1.0× then unhooking without suspension is the correct removal sequence.

| Item | Details |
|------|---------|
| `SpeedHackContracts.cs` (Abstractions) | `SpeedHackConfig` (multiplier, per-function toggles), `SpeedHackState`, `ISpeedHackEngine` |
| `WindowsSpeedHackEngine` (Engine.Windows) | Inline hooking with Iced decoder for instruction boundaries + RIP-relative relocation; follows forwarding stubs before hooking; fixed-point multiplier scaling |
| `SpeedHackService` (Application) | Rate-limit guards, state tracking, safe apply/remove |
| `SpeedHackViewModel` (Desktop) | Speed slider (0.1×–8.0×), multiplier readout, per-function toggles, Apply/Remove buttons, anti-cheat warning label |
| UI placement | New bottom panel tab or toolbar popout |
| `AiToolFunctions.SpeedHack.cs` | `GetSpeedHackState(processId)` `[ReadOnly]`, `SetSpeedMultiplier(processId, multiplier)` `[Destructive]`, `RemoveSpeedHack(processId)` `[Destructive]` |
| DI | `AddSingleton<ISpeedHackEngine, WindowsSpeedHackEngine>()`, `AddSingleton<SpeedHackService>()`, `AddSingleton<SpeedHackViewModel>()` |
| Tests | `SpeedHackServiceTests.cs` — unit tests with mock engine; integration test against `TestHarnessProcess` measuring tick delta before/after 0.5× |

---

### Phase 10E — VEH Debugging ✅ COMPLETE

**Goal:** Add a Vectored Exception Handler-based debugger mode that intercepts hardware breakpoints (`EXCEPTION_SINGLE_STEP` via Trap Flag, `EXCEPTION_BREAKPOINT` via INT3) without `DebugActiveProcess` attachment — bypassing common anti-debug checks (`IsDebuggerPresent`, `NtQueryInformationProcess(ProcessDebugPort)`).

**Approach:** Native C VEH agent DLL (398 LOC) injected via `loadlibrary`/`createthread` infrastructure. Communicates via shared memory (`MemoryMappedFile` IPC). `BreakpointMode` enum gains a `VEH` option. CI workflow compiles the agent with MSVC and embeds it as a resource.

**Result:** `WindowsVehDebugger` (797 LOC) manages agent injection, breakpoint set/remove, and hit streaming. `VehDebugService` adapts to `BreakpointService`. 20+ dedicated VEH tests. AI tools: `SetVehBreakpoint`, `GetVehStatus`.

**Complexity:** High

| Item | Details |
|------|---------|
| `VehContracts.cs` (Abstractions) | `IVehDebugger`, `VehBreakpoint`, `VehHitEvent` records |
| `VehAgent/` (Engine.Windows) | Native-AOT C# shim: calls `AddVectoredExceptionHandler`, communicates via `MemoryMappedFile` IPC |
| `WindowsVehDebugger` (Engine.Windows) | Injects agent, manages breakpoint set/remove, exposes `IObservable<VehHitEvent> HitStream` |
| `VehDebugService` (Application) | High-level service; `BreakpointService` delegates to `IVehDebugger` when `BreakpointMode.VEH` selected |
| `AiToolFunctions.VehDebug.cs` | `SetVehBreakpoint(processId, address, type)` `[Destructive]`, `GetVehStatus(processId)` `[ReadOnly]` |
| UI | `BreakpointMode` dropdown in Breakpoints panel gains `VEH` option; dedicated section in Debugger tab |
| DI | `AddSingleton<IVehDebugger, WindowsVehDebugger>()`, `AddSingleton<VehDebugService>()` |
| Tests | `VehDebugServiceTests.cs` — unit tests with mock; integration test against `TestHarnessProcess` with known trap address |
| Dependencies | Shares injection infrastructure pattern with 10E (overlay); do after 10D |

---

### ~~Phase 10F — D3D/OpenGL Overlay~~ → Demoted to Phase 11 / Community Plugin

**Original goal:** In-game value overlay via `IDXGISwapChain::Present` vtable hooking.

**Why demoted:** Very High complexity (vtable hooks, native DLL, IPC) with the highest crash risk of any feature, and the use case is largely obsoleted by multi-monitor setups. CE AI Suite on a second monitor provides the same information with zero injection risk and zero anti-cheat exposure. If a user needs an overlay, the plugin system (10A) provides the extension point for a community-contributed implementation.

**Status:** Deferred to Phase 11 or community plugin. Design spec preserved above for reference.

---

### Phase 10G — Community Distribution ✅ COMPLETE

**Goal:** Complete the ecosystem story: versioned releases with a hosted update manifest, a `ceai://install-plugin?url=...` URI scheme for community catalog installs, and a GitHub Pages portal.

**Result:** `PluginCatalogService` fetches catalog from GitHub Pages with SHA256 verification and HTTPS enforcement. `ceai://install-plugin` protocol handler wired in `App.xaml.cs`. Online catalog tab in Plugin Manager.

**Complexity:** Low *(almost entirely DevOps + web)*

| Item | Details |
|------|---------|
| `.github/workflows/update-manifest.yml` | On release, publish `update.json` to GitHub Pages |
| `scripts/publish-manifest.ps1` | Generates `update.json` from release assets (version, download URL, SHA256) |
| `PluginCatalogService` (Application) | Fetches community catalog JSON from GitHub Pages; `FetchCatalogAsync()`, `DownloadAndVerifyAsync()` |
| `PluginCatalogViewModel` (Desktop) | Extends `PluginManagerViewModel` (10A) with "Online Catalog" tab: community plugins with Install button |
| `ceai://` protocol handler | `.reg` script in `scripts/`; `App.xaml.cs` startup args parsing triggers catalog install |
| DI | `AddSingleton<PluginCatalogService>()` |
| Tests | Mock `HttpClient` in `PluginCatalogServiceTests.cs`; extend `UpdateServiceTests.cs` for manifest schema validation; no live network calls in CI |
| Dependencies | Requires 10A (Plugin Manager UI) |

---

### ~~Phase 10H — Multi-Platform Exploration~~ → Demoted to Phase 11

**Original goal:** Audit abstraction layer portability and build a `CEAISuite.Engine.Linux` stub.

**Why demoted:** The target audience debugs Windows game processes. Linux/macOS users running games through Wine/Proton are a tiny niche with fundamentally different process model challenges. Being Windows-only is not a limitation for this product category. Multi-platform is a "cherry on top" after the core product is complete, not a prerequisite.

**Portability assessment (preserved for future reference):**

| Layer | Portability | Notes |
|-------|-------------|-------|
| `Engine.Abstractions` | 100% portable | No OS-specific types |
| `Engine.Lua` | 100% portable | MoonSharp is pure C# |
| `Application` | ~90% portable | Only `IScreenCaptureEngine` + `GlobalHotkeyService` use Windows APIs |
| `Engine.Windows` | 0% portable | All P/Invoke |
| `Desktop` (WPF) | 0% portable | Would need Avalonia or MAUI replacement |

---

### Phase 10I — Kernel-Mode Debugging *(Phase 11 candidate)*

**Goal:** A signed Windows kernel driver (`CEAISuiteKm.sys`) providing ring-0 capabilities: bypass `ObRegisterCallbacks`-based access restrictions, bypass anti-debug checks at the kernel level, optional SSDT interception.

**This item is listed for completeness but is recommended as Phase 11.** Prerequisites beyond the current team's scope:
- EV code signing certificate (~$500/year + token)
- Dedicated kernel developer familiar with WDK and Windows internals
- Per-Windows-version offset tables for SSDT/`DbgkpXxx` structures
- CI/CD pipeline for kernel binary signing and release
- Compliance review (many anti-cheats blacklist known driver names/hashes)

**Complexity:** Very High

| Item | Details |
|------|---------|
| `CEAISuite.KernelDriver/` | WDK C project (not C#); three IOCTLs: `OPEN_PROTECTED_PROCESS`, `READ_MEMORY`, `WRITE_MEMORY` |
| `KernelContracts.cs` (Abstractions) | `IKernelDebugger` interface |
| `WindowsKernelBridge` (Engine.Windows) | User-mode bridge communicating with driver via `DeviceIoControl` |
| `KernelDebugService` (Application) | Extends `BreakpointService` with kernel-mode path |
| **Recommendation** | Do not begin until 10A–10H are complete and a kernel engineer is available |

---

### Phase 10J — Adversarial Robustness & Battle Testing ✅ COMPLETE

**Goal:** Systematically harden every engine interface against real-world hostile conditions — processes that exit mid-operation, memory protection changes, malformed inputs, concurrent access, and edge-case Win32 error codes. Transform the test suite from "validates happy paths with mocks" to "proves the engine survives anything a real target throws at it."

**Why this is critical:** Phases 1–9 were built at high velocity with AI assistance. The code is structurally correct and compiles, but the mock-based test suite cannot catch failures that only occur in live adversarial conditions. This phase bridges that gap before Tier 2/3 features (speed hack, VEH) add more injection surface area.

**Result:** Adversarial test harness with hostile behaviors. Fault injection tests for all Win32 error codes. Malformed CT corpus tests (XXE, Billion Laughs, deep nesting, encoding). Concurrent tool stress tests. Dedicated CI job (`adversarial`) runs on every push.

**Complexity:** Medium

| Item | Details |
|------|---------|
| **Adversarial test harness process** | New `CEAISuite.Tests.AdversaryHarness` project — a purpose-built .exe that deliberately exhibits hostile behavior: rapidly changing values (1ms timer), pointer chains that re-allocate periodically, regions alternating `PAGE_READWRITE`/`PAGE_NOACCESS`, threads triggering `EXCEPTION_SINGLE_STEP`, AOB signatures that relocate every 5s |
| **Engine fault injection tests** | For each `I*Engine` method: enumerate every Win32 error code the underlying P/Invoke can return (`ERROR_PARTIAL_COPY`, `ERROR_ACCESS_DENIED`, `STATUS_PROCESS_IS_TERMINATING`, etc.) and write a test for each. Verify graceful degradation, not crashes. |
| **Process-disappears-mid-scan** | Start a scan, kill the harness process mid-way, verify `ScanService` returns partial results + error status (no `AccessViolationException` escaping to UI) |
| **Memory protection race** | `QueryMemoryProtection` returns RW, harness flips to `PAGE_GUARD` before `ReadMemory`, verify engine catches `STATUS_GUARD_PAGE_VIOLATION` |
| **Breakpoint flood test** | Set BP on a hot-path function (10K+ calls/sec), verify hit log rate-limits and does not OOM or deadlock the UI thread |
| **Malformed CT corpus** | Collect 50+ real-world `.CT` files from CE forums; parse all, verify no unhandled exceptions (encoding issues, missing tags, Lua syntax errors, references to nonexistent modules) |
| **Concurrent AI tool stress** | Dispatch `ScanForPointers` + `WriteMemory` + `DissectStructure` simultaneously against the harness; verify no data races or deadlocks |
| **CI integration** | Harness .exe built as a second project; CI launches it, runs adversarial test suite, kills it on completion. Separate GitHub Actions job with `continue-on-error: false` |

**AI workflow for ongoing robustness:**
1. For each new engine feature, AI generates adversarial test cases alongside implementation
2. CI runs adversarial suite on every push (not just unit tests)
3. Periodic "chaos audit" — AI reviews each `I*Engine` interface for uncovered failure modes and extends the harness

---

### Phase 10K — Security Review Checkpoint ✅ COMPLETE

**Goal:** Audit the supply-chain and injection attack surfaces introduced by the plugin system (10A), community catalog downloads (10G), and DLL injection infrastructure (10D/10E) before community distribution goes live.

**Why:** The combination of "download DLLs from a catalog" + "load them into the app process" + "inject code into target processes" is a textbook supply-chain attack vector. This checkpoint ensures the architecture is secure before external users depend on it.

**Result:** `SECURITY.md` threat model with trust boundaries and mitigations. PID validation guards on all engine operations. Catalog SHA256 checksum enforcement. CT Lua execution warnings. Security validation test suite (`SecurityValidationTests.cs`, `CredentialSecurityTests.cs`).

**Complexity:** Low-Medium *(audit + targeted hardening, not a rewrite)*

| Item | Details |
|------|---------|
| Plugin DLL signing | Plugins must be signed (or at minimum SHA256-verified against the catalog manifest) before `PluginHost.LoadAsync` accepts them |
| Catalog HTTPS + pinning | `PluginCatalogService` must enforce HTTPS and optionally pin the GitHub Pages TLS certificate |
| Sandbox audit | Verify `ICeaiPlugin` interface does not expose raw `IServiceProvider` or allow plugins to resolve security-sensitive services (process handles, file system access beyond plugin directory) |
| Lua sandbox escape review | Re-audit MoonSharp `Preset_HardSandbox` — verify no `os`, `io`, `loadfile`, `dofile`, `debug` library access. Extend `LuaSandboxEscapeTests.cs` with latest known escape vectors |
| Injection code audit | Review all `CreateRemoteThread` + `LoadLibrary` call sites for injection into unintended processes. Verify `SpeedHackEngine` and `VehDebugger` validate `processId` against attached process only |
| CT Lua execution | Review `CheatTableFile.LuaScript` auto-execution — should it prompt the user before running Lua from an imported CT file? (Currently auto-runs; this may need a consent gate) |
| Report | Produce `SECURITY.md` documenting the threat model, trust boundaries, and mitigations |

**Timing:** Run after 10A + 10G are built, before community catalog goes live.

---

### Phase 10L — Stabilization Pass ✅ COMPLETE

**Goal:** Regression benchmark and stability verification gate between Tier 2 and Tier 3 features. After speed hack (10D) adds inline hooking and before VEH debugging (10E) adds exception handler injection, verify the engine hasn't regressed.

**Result:** Scan benchmarks, 100-cycle attach/detach leak detection, speed hack apply/remove resource leak checks, crash recovery stress tests. Dedicated CI job (`stabilization`) runs on every push. Leak tests use behavioral deltas (not hard GC thresholds) for CI stability.

**Complexity:** Low

| Item | Details |
|------|---------|
| Benchmark regression | Run `ScanBenchmarkTests.cs` thresholds against the adversarial harness (10J) with speed hack active — verify scan performance doesn't regress >10% |
| Memory leak check | Attach/detach cycle 100× against harness; verify no handle leaks (`Process.HandleCount` delta ≤ 2) |
| Crash recovery stress | Force-kill the app 10× during active scans/breakpoints; verify crash recovery (`recovery.json`) restores address table every time |
| CI gate | Stabilization tests must pass before 10E/10F branches can merge |

---

## Phase 11: Full Debugger & Kernel Access

**Goal:** Complete the last two major parity gaps — interactive debugger stepping and kernel-mode access for anti-debug bypass.

### Phase 11A — Debugger Stepping *(first priority)*

**Goal:** Wire the stubbed Step In/Over/Out/Continue buttons in the Debugger tab to a real stepping engine. This is the last major CE parity gap that affects every user.

**Why first:** Stepping is universal — every CE user expects it. VEH debugging (10E) ships the `EXCEPTION_SINGLE_STEP` exception infrastructure; this phase wires it into the Debugger UI. Without stepping, the Debugger tab is a read-only register viewer. With stepping, it becomes a real debugger.

**Complexity:** High

| Item | Details |
|------|---------|
| `ISteppingEngine` (Abstractions) | `StepInAsync`, `StepOverAsync`, `StepOutAsync`, `ContinueAsync`; `StepCompleted` event with register snapshot |
| `WindowsSteppingEngine` (Engine.Windows) | Sets Trap Flag (TF) in EFLAGS via `SetThreadContext` for step-in; temporary breakpoint at next instruction for step-over; stack frame return address breakpoint for step-out |
| VEH integration | When `BreakpointMode.VEH` is active, stepping uses the VEH handler (10E) for `EXCEPTION_SINGLE_STEP` dispatch instead of `WaitForDebugEvent` |
| `DebuggerViewModel` updates | Enable Step In/Over/Out/Continue buttons; bind to `ISteppingEngine` commands; update register view on each step completion |
| Watch expressions | Evaluate user-defined expressions against current register/memory state at each step |
| Trace window | Record instruction history during step sequences; display as scrollable trace log |
| AI tools | `StepIn(processId)`, `StepOver(processId)`, `StepOut(processId)`, `Continue(processId)` — all `[Destructive]` |
| Tests | Stepping tests against adversarial harness (10J); verify TF is cleared after single step; verify step-over doesn't step into calls |

### Phase 11B — Kernel-Mode Debugging

**Goal:** A signed Windows kernel driver (`CEAISuiteKm.sys`) providing ring-0 capabilities: bypass `ObRegisterCallbacks`-based process access restrictions and anti-debug checks at the kernel level.

**Why second:** Kernel access is powerful but niche. Most games CE users target don't have kernel-level anti-debug. Games protected by EAC/BattlEye/Vanguard are a subset of the user base, and those users already know they need kernel tools. Stepping helps *everyone*; kernel helps the advanced minority.

**Complexity:** Very High

**Prerequisites:**
- EV code signing certificate (~$500/year + hardware token)
- Dedicated kernel developer familiar with WDK and Windows internals
- Per-Windows-version offset tables for SSDT/`DbgkpXxx` structures
- CI/CD pipeline for kernel binary signing and release
- Compliance review (many anti-cheats blacklist known driver names/hashes)

| Item | Details |
|------|---------|
| `CEAISuite.KernelDriver/` | WDK C project (not C#); three IOCTLs: `OPEN_PROTECTED_PROCESS`, `READ_MEMORY`, `WRITE_MEMORY` |
| `KernelContracts.cs` (Abstractions) | `IKernelDebugger` interface |
| `WindowsKernelBridge` (Engine.Windows) | User-mode bridge communicating with driver via `DeviceIoControl` |
| `KernelDebugService` (Application) | Extends `BreakpointService` with kernel-mode path |

### Phase 11 — Additional Deferred Items

| Item | Original Phase | Why Deferred |
|------|---------------|--------------|
| **D3D/OpenGL overlay** (10F) | Phase 10 | Very High complexity, highest crash risk, obsoleted by multi-monitor setups. Better suited as a community plugin via the 10A plugin system. |
| **Multi-platform port** (10H) | Phase 10 | Target audience debugs Windows game processes. Linux/macOS via Wine/Proton is a tiny niche with fundamentally different process model challenges. Cherry on top after the core product is finished, not a prerequisite. |
| **PDB/.NET symbol loading** | Phase 3A | Currently module exports only. Full PDB/DWARF/.NET metadata symbol resolution would enrich disassembler labels, stack traces, and structure dissection. Medium complexity (DbgHelp P/Invoke or similar). Not blocking any workflow — addresses resolve by module+offset — but would significantly improve readability. |

---

## Phase 10 Priority Order

| Tier | Sub-Phase | Status | Rationale |
|------|-----------|--------|-----------|
| **Tier 1** (near-term) | **10A** Plugin System UI | ✅ Done | Backend done; pure UI wiring; unlocks ecosystem immediately |
| | **10C** AI Co-Pilot Mode | ✅ Done | All plumbing exists; MVVM + `PermissionEngine` are waiting for this |
| | **10B** Trainer Generation | ✅ Done | Highest-visibility user feature; self-contained Roslyn addition |
| | **10G** Community Distribution | ✅ Done | Low complexity DevOps; must immediately follow 10A to make plugins useful |
| **Gate 1** | **10J** Adversarial Robustness | ✅ Done | Battle-test the engine before adding injection features; adversarial harness + fault injection + malformed CT corpus |
| | **10K** Security Review | ✅ Done | Audit plugin loading, catalog downloads, and injection code paths before community distribution goes live |
| **Tier 2** (medium-term) | **10D** Speed Hack | ✅ Done | Rewritten from IAT patching to inline hooking; high user demand; no kernel required |
| **Gate 2** | **10L** Stabilization Pass | ✅ Done | Regression benchmarks + memory leak checks + crash recovery stress before VEH injection |
| **Tier 3** (long-term) | **10E** VEH Debugging | ✅ Done | Native C agent DLL + shared memory IPC; anti-debug bypass |
| **Phase 11A** | Debugger stepping | Planned | Last universal parity gap; builds on VEH from 10E; every user needs this |
| **Phase 11B** | Kernel driver | Future | Niche but powerful; requires EV cert + dedicated kernel engineer |

---

## Parity Tracking

Current → Target parity by category after each phase:

| Category | After Ph 7 ✅ | After Ph 8 ✅ | After Ph 10 ✅ | Target |
|----------|-----------|-----------|------------|--------|
| Process & Attachment | 45% | 45% | 50% | 80%* |
| Memory Read/Write | 80% | 80% | 80% | 90% |
| Scanning | 90% | 90% | 90% | 95% |
| Disassembly & Analysis | 70% | 70% | 70% | 85% |
| Breakpoints & Hooks | 85% | 90% | 95% | 95%** |
| Address Table | 90% | 90% | 90% | 95% |
| Scripting | 80% | 95% | 95% | 95% |
| Pointer Resolution | 70% | 70% | 70% | 80% |
| Structure Discovery | 100% | 100% | 100% | 100% |
| Snapshots | 100% | 100% | 100% | 100% |
| Session & History | 75% | 75% | 75% | 80% |
| Safety & Watchdog | 50% | 50% | 70% | 80% |
| Hotkeys | 100% | 100% | 100% | 100% |
| **Overall** | **~82%** | **~88%** | **~91%** | **95%+** |

*Process & Attachment parity improves further with future engine enhancements (parent process, command line).
**Breakpoints & Hooks reaches 95% with VEH debugging (10E) providing anti-debug bypass. Target raised from 90% to 95%.

Phase 10 parity changes: Breakpoints & Hooks +5% (VEH debugging mode), Safety & Watchdog +20% (security review, adversarial testing, stabilization gate, PID validation), Process & Attachment +5% (speed hack process interaction). Overall target raised to 95%+ reflecting achievable ceiling with Phase 11 stepping.

*Full historical parity table (Phases 2–8) preserved in git history.*

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
    Phase 8 ✅ (Lua) ← MoonSharp + CE API + REPL + forms + BP scripting; 489 tests
    │
    Phase 9 ✅ (Infrastructure) ← CI/CD, Serilog, telemetry, benchmarks, progress bars, wizard; 579 tests
    │
    └── Phase 10 ✅ (Advanced) ← all sub-phases complete; 2,558 tests
            ├── Tier 1 ✅: 10A Plugin UI + 10C Co-Pilot + 10B Trainers + 10G Distribution
            ├── Gate 1 ✅: 10J Adversarial Testing + 10K Security Review
            ├── Tier 2 ✅: 10D Speed Hack (rewritten: IAT → inline hooking)
            ├── Gate 2 ✅: 10L Stabilization (benchmarks + leak detection + crash recovery)
            └── Tier 3 ✅: 10E VEH Debugging (native C agent + shared memory IPC)
            │
    Phase 11 (Full Debugger & Kernel)
            ├── 11A Debugger Stepping (builds on 10E VEH; last universal parity gap)
            └── 11B Kernel Driver (EV cert + WDK; niche anti-debug bypass)
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
9. ✅ Phase 9 — Done (CI/CD + Codecov, Serilog logging, crash telemetry opt-in, progress indicators, first-run wizard, UI lifecycle tests, benchmark hardening; 579 tests)
10. ✅ Phase 8 — Done (MoonSharp Lua 5.2 engine, CE API, REPL, forms, BP scripting; 489 tests)
11. ✅ Phase 10 — Done (Plugin UI, Co-Pilot, Trainers, Speed Hack, VEH Debug, Community Distribution, Adversarial Testing, Security Review, Stabilization; 2,558 tests)

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
| **8** | Lua | ✅ Complete | MoonSharp Lua 5.2 engine: CE API bindings (20+ functions), {$luacode}/LuaCall AA integration, REPL console, CT Lua execution, form designer, breakpoint scripting, 3 AI tools; 489 tests |
| **9** | Infrastructure | ✅ Complete | CI/CD (GitHub Actions + Codecov), Serilog structured logging (file + Output panel), crash telemetry opt-in, breakpoint/snapshot progress indicators, first-run wizard (3-page onboarding), UI lifecycle smoke tests, benchmark hardening; 579 tests |
| **10** | Advanced | ✅ Complete | Plugin UI (10A), AI Co-Pilot (10C), Trainer Generation (10B), Community Distribution (10G), Adversarial Testing (10J), Security Review (10K), Speed Hack (10D, inline hooking), Stabilization (10L), VEH Debugging (10E); 2,558 tests |
| **11** | Debugger & Kernel | Planned | 11A Debugger Stepping (last universal parity gap) → 11B Kernel Driver (niche anti-debug bypass) |
