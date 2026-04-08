# CE AI Suite

[![CI](https://github.com/deusversus/ceai/actions/workflows/ci.yml/badge.svg)](https://github.com/deusversus/ceai/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/deusversus/ceai/graph/badge.svg)](https://codecov.io/gh/deusversus/ceai)

**A Cheat Engine-class memory analysis desktop application with an integrated AI operator.**

CE AI Suite combines professional-grade reverse engineering tools — memory scanning, breakpoints, disassembly, pointer resolution, Auto Assembler scripting — with an AI agent that can autonomously drive these tools to accomplish user goals. Built on .NET 10, WPF, and Microsoft Agent Framework (MAF).

> **Status:** Active development · 71 commits · .NET 10 · Windows only

---

## Table of Contents

- [Overview](#overview)
- [Key Features](#key-features)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [AI Operator](#ai-operator)
- [Agent Skills](#agent-skills)
- [Tool Functions](#tool-functions)
- [Engine Capabilities](#engine-capabilities)
- [Project Structure](#project-structure)
- [Technology Stack](#technology-stack)
- [Configuration](#configuration)
- [Testing](#testing)
- [Contributing](#contributing)
- [License](#license)

---

## Overview

CE AI Suite bridges the gap between powerful memory analysis tools and practical usability. Instead of manually navigating menus, scanning values, setting breakpoints, and interpreting assembly — you describe what you want in natural language, and the AI operator executes the workflow.

**Philosophy:** AI assists, not replaces. Every tool the AI calls is also available through the manual UI. The AI adds intelligence and automation; users retain full control.

### What can it do?

- "Find my health value in this Unity game" → AI selects float scan, narrows results, resolves pointer chain, adds to address table
- "Make a god mode script" → AI finds the damage function via breakpoints, writes an Auto Assembler NOP patch, validates it
- "What's the player structure look like?" → AI dissects memory around the player pointer, identifies fields, maps the object layout
- "Find a stable pointer to this address" → AI runs pointer scan, validates chains across restarts, picks the most stable path

---

## Key Features

### Memory Analysis
- **Value scanning** — Exact, range, fuzzy, increased/decreased, unknown initial value
- **Data types** — Byte, Int16, Int32, Int64, Float, Double, String, ByteArray, Pointer
- **Memory browser** — Hex dump with ASCII, structure-aware interpretation
- **Region enumeration** — Map heap, stack, module, and mapped regions

### Breakpoints & Tracing
- **5 intrusiveness modes** — Auto, Software (INT3), Hardware (DR0-DR3), PageGuard, Stealth (code cave)
- **Breakpoint safety** — Risk assessment, throttle detection, emergency recovery
- **Write tracing** — "Find what writes to this address" with register snapshots
- **Code cave hooks** — JMP-redirect hooks with full register capture

### Disassembly & Code Analysis
- **x86/x64 disassembly** — Powered by Iced disassembler
- **Static analysis** — Find writers to offset, function boundaries, caller graphs
- **Instruction search** — Pattern-based instruction matching across modules
- **AOB signatures** — Generate and validate array-of-bytes signatures

### Scripting
- **Auto Assembler** — CE-compatible script engine (enable/disable sections)
- **Code caves** — Allocate, write, and manage injected code
- **Script validation** — Syntax and semantic checking against live process
- **Templates** — Health lock, multiplier, NOP, conditional, one-hit-kill patterns

### Pointer Resolution
- **Multi-level chains** — CE-style deepest-first pointer chain resolution
- **ASLR handling** — Module-relative addressing with auto-refresh on restart
- **Pointer scanning** — Automated chain discovery with stability ranking
- **Validation** — Cross-restart chain verification

### Address Table
- **Hierarchical tree** — Groups, entries, scripts with CE 7.5-style layout
- **Value freezing** — Lock values at current or specified amount
- **Cheat Table I/O** — Load and save `.CT` files (CE XML format)
- **Hotkey bindings** — Keyboard shortcuts for toggle/freeze operations

### Session Management
- **Save/Load** — Persist full investigation state (address table, breakpoints, scripts, chat history)
- **Undo/Redo** — Transaction-based memory write rollback
- **Chat history** — Searchable conversation archive with session switching
- **Crash protection** — Unhandled exception logging, auto-recovery

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   CEAISuite.Desktop                      │
│              WPF UI · Settings · Auth                    │
├─────────────────────────────────────────────────────────┤
│                 CEAISuite.Application                    │
│     AI Operator · Tool Functions · Address Table         │
│     Session Service · Breakpoint Service · Scripts       │
├──────────────────────┬──────────────────────────────────┤
│  Engine.Abstractions │      AI.Contracts                │
│  IEngineFacade       │      Provider interfaces         │
├──────────────────────┼──────────────────────────────────┤
│  Engine.Windows      │      Persistence.Sqlite          │
│  P/Invoke · Debug API│      SQLite session storage      │
├──────────────────────┴──────────────────────────────────┤
│                    CEAISuite.Domain                      │
│              Shared models · Crypto · Types              │
└─────────────────────────────────────────────────────────┘
```

**Layer responsibilities:**

| Layer | Role |
|-------|------|
| **Desktop** | WPF shell, main window, settings, Copilot auth, chat client factory |
| **Application** | AI operator (MAF agent), 90 tool functions, address table, session management |
| **Engine.Abstractions** | `IEngineFacade` interface, data types, capability flags |
| **Engine.Windows** | Windows API implementation (ReadProcessMemory, debug API, breakpoints) |
| **AI.Contracts** | Provider-agnostic AI interfaces |
| **Persistence.Sqlite** | SQLite-backed session and history storage |
| **Domain** | Core models, DPAPI credential encryption |

---

## Getting Started

### Prerequisites

- **Windows 10/11** (x64)
- [**.NET 10 SDK**](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.201 or later)
- **Visual Studio 2022 17.14+** or **VS Code** with C# Dev Kit (optional)
- **GitHub account** (for Copilot AI provider — free tier works)

### Build

```bash
git clone https://github.com/deusversus/ceai.git
cd ceai
dotnet restore
dotnet build
```

### Run

```bash
dotnet run --project src/CEAISuite.Desktop/CEAISuite.Desktop.csproj
```

### First Launch

1. **Settings** → Click **Sign in with GitHub** to authenticate via device flow
2. **Select a model** from the dropdown (Claude Sonnet 4.6, GPT-5.4, etc.)
3. **Refresh** the process list → select a target process
4. **Chat** with the AI: *"Scan for my health value, it's currently 100"*

### Run Tests

```bash
dotnet test
```

---

## AI Operator

The AI operator is built on [Microsoft Agent Framework (MAF)](https://github.com/microsoft/agents) v1.0.0-rc4. It's a tool-calling agent with access to ~90 functions that map to every capability of the memory engine.

### How It Works

1. **You describe intent** in natural language
2. **The agent plans** a sequence of tool calls
3. **Tools execute** against the live process (with safety checks)
4. **Results stream** back to the chat — the agent interprets and continues
5. **Dangerous operations** (memory writes, breakpoints) require your approval

### AI Providers

| Provider | Auth Method | Notes |
|----------|-------------|-------|
| **GitHub Copilot** | OAuth device flow | Primary. Routes to OpenAI, Anthropic, Google models |
| **OpenAI** | API key | Direct API access |
| **Anthropic** | API key | Direct Claude access |
| **OpenAI-compatible** | API key + endpoint | Any compatible API (local LLMs, etc.) |

### Conversation Compaction

Long conversations are automatically compacted using MAF's `CompactionProvider`. When token count exceeds thresholds, older messages are summarized to maintain context while controlling costs. Usage metrics (input/output/cached tokens, cost estimates) are tracked and displayed.

### Safety

- **6 dangerous tools** require explicit user approval before execution
- **Breakpoint risk assessment** — `ProbeTargetRisk` evaluates addresses before setting breakpoints
- **Emergency recovery** — `EmergencyRestorePageProtection` and `ForceDetachAndCleanup` prevent game hangs
- **Transaction rollback** — Group related writes and undo them atomically
- **Process watchdog** — Auto-rollback if target process becomes unresponsive

---

## Agent Skills

CE AI Suite uses MAF's `FileAgentSkillsProvider` to load domain expertise on demand. Skills follow the [agentskills.io](https://agentskills.io) SKILL.md specification with progressive disclosure — the agent sees skill names and descriptions (~100 tokens each) and loads full content only when relevant.

### Built-in Skills

| Skill | Description |
|-------|-------------|
| **memory-scanning** | Value scanning workflows, data type heuristics, narrowing strategies |
| **code-analysis** | Static/dynamic analysis, TraceFieldWriters, assembly pattern recognition |
| **breakpoint-mastery** | All 5 BP modes, safety rules, decision tree, emergency recovery |
| **script-engineering** | Auto Assembler scripting, code caves, AOB injection patterns |
| **unity-il2cpp** | Unity Il2Cpp reversing — GameAssembly.dll, object layouts, pointer chains |
| **unreal-engine** | UE4/5 GWorld, GNames, GObjects, UObject hierarchy traversal |
| **pointer-resolution** | Pointer chain building, ASLR handling, stability validation |
| **data-mining** | Structure dissection, vtable analysis, field discovery strategies |
| **stealth-awareness** | Anti-cheat detection vectors, safe analysis practices |

### Custom Skills

Drop additional SKILL.md files into `%LOCALAPPDATA%\CEAISuite\skills\` to extend the agent with custom knowledge. Skills are discovered automatically at startup.

---

## Tool Functions

The AI agent has access to approximately **90 tool functions** organized by category:

<details>
<summary><strong>Process & Attachment</strong> (5 functions)</summary>

- `ListProcesses` — Enumerate running processes
- `FindProcess` — Search by name
- `InspectProcess` — Modules, base addresses, architecture
- `AttachProcess` — Attach to target
- `GetCurrentContext` — Session state summary
</details>

<details>
<summary><strong>Memory Operations</strong> (10 functions)</summary>

- `ReadMemory` / `WriteMemory` — Raw byte I/O
- `ProbeAddress` — Analyze contents at address
- `BrowseMemory` — Structure-aware memory view
- `ListMemoryRegions` — Heap, stack, module mapping
- `AllocateMemory` / `FreeMemory` — Code cave management
- `QueryMemoryProtection` / `ChangeMemoryProtection` — PAGE_* flags
</details>

<details>
<summary><strong>Scanning</strong> (3 functions)</summary>

- `StartScan` — Begin value scan (exact, range, fuzzy, unknown)
- `RefineScan` — Narrow results (changed, unchanged, increased, decreased)
- `GetScanResults` — Retrieve matches
</details>

<details>
<summary><strong>Disassembly & Analysis</strong> (15 functions)</summary>

- `Disassemble` — x86/x64 instruction listing
- `FindWritersToOffset` — Static analysis: what writes this field
- `FindFunctionBoundaries` — Function prologue/epilogue detection
- `GetCallerGraph` — Cross-reference analysis
- `TraceFieldWriters` — Combined structure offset + writer discovery
- `GenerateSignature` / `TestSignatureUniqueness` — AOB pattern tools
- `SearchInstructionPattern` — Regex-style assembly search
- `HexDump` — Hex + ASCII memory view
- `CaptureProcessWindow` — Screenshot for AI visual analysis
</details>

<details>
<summary><strong>Breakpoints & Hooks</strong> (12 functions)</summary>

- `SetBreakpoint` / `RemoveBreakpoint` / `ListBreakpoints` — Lifecycle management
- `GetBreakpointHitLog` / `GetBreakpointHealth` — Monitoring
- `ProbeTargetRisk` — Safety assessment (LOW/MEDIUM/HIGH/CRITICAL)
- `EmergencyRestorePageProtection` — Hang recovery
- `ForceDetachAndCleanup` — Nuclear cleanup
- `InstallCodeCaveHook` / `RemoveCodeCaveHook` / `ListCodeCaveHooks` / `GetCodeCaveHookHits` — Stealth hooks
</details>

<details>
<summary><strong>Address Table</strong> (12 functions)</summary>

- `AddToAddressTable` / `RemoveFromAddressTable` — Entry management
- `ListAddressTable` / `GetAddressTableNode` — Queries
- `RenameAddressTableEntry` / `SetEntryNotes` — Metadata
- `CreateAddressGroup` / `MoveEntryToGroup` — Organization
- `RefreshAddressTable` — Re-resolve all pointers
- `FreezeAddress` / `UnfreezeAddress` / `FreezeAddressAtValue` — Value locking
</details>

<details>
<summary><strong>Scripting</strong> (10 functions)</summary>

- `ListScripts` / `ViewScript` — Browse scripts
- `CreateScriptEntry` / `EditScript` — Author scripts
- `EnableScript` / `DisableScript` / `ToggleScript` — Lifecycle
- `ValidateScript` / `ValidateScriptDeep` — Syntax & semantic checks
- `DryRunHookInstall` — Preview byte changes
</details>

<details>
<summary><strong>Pointer Resolution</strong> (4 functions)</summary>

- `ScanForPointers` — Build pointer chains
- `ValidatePointerPaths` — Cross-restart stability ranking
- `RescanPointerPath` — Revalidate after restart
- `ResolveSymbol` — Module+offset → live address
</details>

<details>
<summary><strong>Structure Discovery</strong> (2 functions)</summary>

- `DissectStructure` — Auto-interpret object layout with type inference
- `CompareSnapshotWithLive` — Delta analysis between snapshots
</details>

<details>
<summary><strong>Session & History</strong> (5 functions)</summary>

- `SaveSession` / `LoadSession` / `ListSessions` — Persistence
- `UndoWrite` / `RedoWrite` — Transaction rollback
- `PatchHistory` — Recent write log
</details>

<details>
<summary><strong>Artifacts & Utilities</strong> (6 functions)</summary>

- `GenerateTrainerScript` / `GenerateAutoAssemblerScript` / `GenerateLuaScript` — Code generation
- `SummarizeInvestigation` — Report generation
- `SetHotkey` / `ListHotkeys` / `RemoveHotkey` — Keyboard bindings
- `LoadCheatTable` / `SaveCheatTable` — CE .CT file I/O
</details>

---

## Engine Capabilities

The memory engine is abstracted behind `IEngineFacade`, enabling future cross-platform ports. The current implementation targets Windows via P/Invoke.

### Capability Flags

| Capability | Description |
|-----------|-------------|
| `ProcessEnumeration` | List and inspect running processes |
| `MemoryRead` | Read process memory (ReadProcessMemory) |
| `MemoryWrite` | Write process memory (WriteProcessMemory) |
| `ValueScanning` | Type-aware value scanning with refinement |
| `Disassembly` | x86/x64 instruction decoding (Iced) |
| `BreakpointTracing` | Hardware, software, page guard, and stealth breakpoints |
| `SessionPersistence` | Save/load investigation state |

### Supported Data Types

`Byte` · `Int16` · `Int32` · `Int64` · `Float` · `Double` · `Pointer` · `String` · `ByteArray`

---

## Project Structure

```
ceai/
├── src/
│   ├── CEAISuite.Desktop/            # WPF app, auth, settings UI
│   ├── CEAISuite.Application/        # AI operator, tool functions, services
│   ├── CEAISuite.Engine.Windows/     # Windows memory engine (P/Invoke)
│   ├── CEAISuite.Engine.Abstractions/# IEngineFacade, contracts, types
│   ├── CEAISuite.AI.Contracts/       # AI provider interfaces
│   ├── CEAISuite.Persistence.Sqlite/ # SQLite session storage
│   ├── CEAISuite.Domain/             # Shared models, crypto
│   └── CEAISuite.Tests/              # xUnit tests (580+ tests)
├── skills/                           # 9 AI agent skill modules
│   ├── memory-scanning/
│   ├── code-analysis/
│   ├── breakpoint-mastery/
│   ├── script-engineering/
│   ├── unity-il2cpp/
│   ├── unreal-engine/
│   ├── pointer-resolution/
│   ├── data-mining/
│   └── stealth-awareness/
├── CEAISuite.slnx                    # Solution file
├── CE-AI-Suite-Spec-and-Design.md    # Design document
└── global.json                       # .NET SDK version pin
```

---

## Technology Stack

| Component | Technology | Version |
|-----------|-----------|---------|
| **Runtime** | .NET | 10.0 |
| **UI** | WPF (Windows Presentation Foundation) | — |
| **AI Agent** | Microsoft Agent Framework (MAF) | 1.0.0-rc4 |
| **AI Abstraction** | Microsoft.Extensions.AI | 10.3.0 |
| **Disassembler** | Iced | 1.21.0 |
| **Assembler** | Keystone Engine | 0.9.1.1 |
| **MVVM** | CommunityToolkit.Mvvm | 8.4.0 |
| **Database** | Microsoft.Data.Sqlite | 8.0.8 |
| **AI Providers** | OpenAI, Anthropic, GitHub Copilot | Various |
| **Crypto** | System.Security.Cryptography.ProtectedData | 9.0.5 |
| **Tests** | xUnit | 2.9.3 |

---

## Configuration

### AI Provider Setup

**GitHub Copilot (recommended):**
1. Open **Settings** in the app
2. Select **GitHub Copilot** as provider
3. Click **Sign in with GitHub** — completes OAuth device flow in your browser
4. Select a model from the dropdown

**Direct API key:**
1. Select **OpenAI**, **Anthropic**, or **OpenAI-Compatible** as provider
2. Enter your API key (stored encrypted via DPAPI)
3. For OpenAI-compatible: also set the custom endpoint URL

**Environment variables:** `OPENAI_API_KEY` or `ANTHROPIC_API_KEY` are picked up automatically if no key is configured in settings.

### Settings Storage

Settings are persisted to `%LOCALAPPDATA%\CEAISuite\settings.json` with API keys encrypted via Windows DPAPI. This file is gitignored.

### Themes

Built-in Dark and Light themes with System auto-detection. Toggle via **Settings** or **View** menu.

---

## Testing

```bash
# Run all 580+ tests
dotnet test

# Run specific test category
dotnet test --filter "FullyQualifiedName~BreakpointEngine"
dotnet test --filter "FullyQualifiedName~AddressTableService"
```

### Test Coverage

| Area | Tests | What's Validated |
|------|-------|-----------------|
| **Application & AI Tools** | ~76 | Session management, AI tool functions, label generation, scan workflows |
| **Agent Loop** | ~41 | Streaming, tool execution, context management, error handling |
| **Lua Scripting** | ~87 | Lua engine, CE API bindings, auto-assembler integration, console VM, gap closure |
| **Breakpoint Engine** | ~30 | Mode selection, intrusiveness ordering, code cave JMP generation, risk assessment |
| **Scanner** | ~23 | Scan refinement, undo/redo, grouped scans, benchmarks |
| **Address Table** | ~26 | Entry CRUD, pointer chain resolution, module-relative addressing, improvements |
| **Pointer Scanner** | ~24 | Pointer path discovery, map I/O, comparison, rescan, resume |
| **UI & ViewModels** | ~120 | Lifecycle, data binding, navigation, all major view models |
| **Skills** | ~49 | Frontmatter parsing, loading, permissions, catalog budgets, references, sanitization |
| **Pipeline & Integration** | ~16 | End-to-end scan, session, export, snapshot pipelines |
| **Other** | ~89 | Crash recovery, updates, symbol engine, cheat tables, API keys, auto-assembler |

Tests use a `StubEngineFacade` that provides an in-memory implementation of `IEngineFacade` — no actual process attachment needed.

---

## Contributing

This project is in active development. Key areas for contribution:

- **Linux/macOS engine** — Implement `IEngineFacade` using ptrace / mach_port
- **Additional AI skills** — Drop SKILL.md files into `skills/` following the [agentskills.io spec](https://agentskills.io)
- **More test coverage** — Especially for AI tool functions and scripting engine
- **UI improvements** — Additional panels, visualization, memory map view

### Adding a Custom Skill

1. Create a directory under `skills/` (e.g., `skills/my-skill/`)
2. Add a `SKILL.md` with YAML frontmatter:

```yaml
---
name: my-skill
description: One-line description of what this skill teaches the AI
version: "1.0"
tags: [custom, example]
triggers:
  - "when the user asks about X"
---

# My Skill

Instructions for the AI agent...
```

3. Optionally add reference docs in `skills/my-skill/references/`
4. Rebuild — skills are copied to the output directory automatically

---

## License

License TBD.

---

<sub>Built with .NET 10, Microsoft Agent Framework, and a lot of memory reads.</sub>
