# CE AI Suite

[![CI](https://github.com/deusversus/ceai/actions/workflows/ci.yml/badge.svg)](https://github.com/deusversus/ceai/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/deusversus/ceai/graph/badge.svg)](https://codecov.io/gh/deusversus/ceai)

**An AI-powered game memory editor. Think Cheat Engine, but you can talk to it.**

CE AI Suite is a Windows desktop application that combines professional-grade reverse engineering tools with an AI agent that drives them via natural language. Instead of navigating menus and memorizing workflows, you describe what you want — and the AI executes.

> **Status:** Active development · 500+ commits · 3,085 tests · .NET 10 / WPF · Solo developer + AI pair programming

---

## What Is This?

You attach to a game. You type "find my health value, it's currently 100." The AI scans memory, narrows results as you take damage, resolves a pointer chain, freezes the value, and explains what it did. That's the pitch.

Under the hood, it's a full memory analysis toolkit: value scanning, hardware/software/VEH breakpoints, x86/x64 disassembly, pointer scanning, Auto Assembler scripting, Lua scripting with CE API compatibility, speed hack, Mono/.NET runtime introspection, and cheat table import/export — all wired to an AI agent with 50+ tool functions.

### Demo Workflows

```
You:  "Find my health value in this Unity game — it's a float around 100"
AI:   Scans for float 100.0 → finds 2,847 results
You:  "I just took damage, health is now 85.5"
AI:   Refines with DecreasedBy → narrows to 3 results → resolves pointer chain
      → adds to address table → freezes at 100.0
You:  "Make a god mode script"
AI:   Sets write breakpoint → identifies damage function → writes NOP patch
      → generates Auto Assembler script → validates it works
You:  "What does the player structure look like?"
AI:   Dissects memory around pointer → identifies Float fields (health, mana,
      stamina), Int32 (level, XP), Pointer (inventory, position vector)
```

---

## Features

### AI Agent
- **Natural language game hacking** — describe what you want, AI executes the workflow
- **50+ AI tool functions** spanning memory, scanning, breakpoints, disassembly, scripting, pointer resolution
- **Multi-provider** — Anthropic Claude, OpenAI, Google Gemini, GitHub Copilot, OpenRouter, any OpenAI-compatible API
- **Streaming responses** with token tracking and cost estimation
- **Safety controls** — dangerous operations require approval, emergency rollback on process hang

### Memory Analysis
- **Value scanning** — exact, range, increased/decreased/changed/unchanged, unknown initial value, array of bytes with wildcards
- **15 data types** — Byte, Int16/32/64, UInt16/32/64, Float, Double, Pointer, String, WideString (UTF-16), ByteArray
- **Auto-VirtualProtect** — writes to read-only pages (code sections) succeed transparently
- **Hex editor** — inline byte editing, change highlighting, bookmarks, data inspector, search
- **Batch write + memory fill** — write multiple addresses at once, fill regions with byte patterns
- **Freeze modes** — exact, increment each tick, decrement each tick

### Breakpoints & Debugging
- **4 breakpoint types** — Hardware (DR0-DR3), Software (INT3), PAGE_GUARD, VEH agent
- **Auto-fallback cascade** — Hardware → VEH → PageGuard when DR slots exhausted
- **VEH debugger** — injected native agent DLL, bypasses anti-debug (no DebugActiveProcess)
- **Stealth mode** — DR register cloaking, PEB unlinking, NtGetThreadContext hook
- **Conditional breakpoints** — register compare, memory compare, hit count, Lua callbacks
- **Break-and-trace** — single-step N instructions collecting full register state
- **Interactive stepping** — step in / step over / step out / continue via VEH trap flag
- **Process watchdog** — auto-rollback if target becomes unresponsive after hook install

### Disassembly & Code Analysis
- **x86/x64** — powered by Iced.Intel (more correct than CE's hand-rolled decoder)
- **PDB symbol resolution** — function names, source file + line number from debug symbols
- **Signature generation** — create unique AOB patterns with relocation-aware wildcarding
- **Code cave engine** — stealth JMP hooks with RIP-relative instruction relocation

### Scripting
- **Auto Assembler** — CE-compatible script engine (`[ENABLE]`/`[DISABLE]`, `alloc`, `aobscanmodule`, `registersymbol`, `label`, `createthread`, etc.)
- **Lua 5.2** (MoonSharp) — 175+ CE-compatible globals across 26 binding files
- **CE form designer** — `createForm`, `createButton`, `createEdit`, `createCheckBox`, reactive data binding, dockable script panels
- **Mono/.NET introspection** — `LaunchMonoDataCollector`, `mono_enumDomains`, `mono_findClass`, `mono_invoke_method` for Unity game hacking
- **Speed hack** — inline hooking of `timeGetTime`, `QueryPerformanceCounter`, `GetTickCount64` with live multiplier adjustment

### Cheat Table Compatibility
- **Load/save .CT files** — CE 7.5 XML format with round-trip fidelity
- **Unknown element preservation** — attributes and elements we don't understand are kept intact
- **Pointer chains, scripts, groups, hotkeys, drop-down lists** — all parsed and stored

### Infrastructure
- **3,085 automated tests** — xUnit, CI on every push via GitHub Actions
- **Session persistence** — SQLite-backed save/load with crash recovery (auto-save + recovery.json)
- **Plugin system** — community plugin catalog with SHA256-verified downloads
- **First-run wizard** — guided onboarding for new users

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                     CEAISuite.Desktop                         │
│            WPF · 29 ViewModels · AvalonDock layout            │
├──────────────────────────────────────────────────────────────┤
│                   CEAISuite.Application                       │
│   AI AgentLoop (29 files) · 50+ AI Tools · Services          │
├──────────────────┬──────────────┬────────────────────────────┤
│ Engine.Abstractions │ Engine.Lua    │ AI.Contracts             │
│ IEngineFacade etc.  │ MoonSharp+CE  │ Tool catalog             │
├──────────────────┬──┴──────────────┴────────────────────────┤
│ Engine.Windows   │ Persistence.Sqlite │ Domain                 │
│ P/Invoke · VEH   │ Session storage    │ Models · Crypto        │
├──────────────────┴────────────────────┴──────────────────────┤
│                    native/ (C DLLs)                           │
│    veh_agent.dll (~2,900 LOC) · mono_agent.dll (~700 LOC)    │
└──────────────────────────────────────────────────────────────┘
```

8 managed projects + 2 native C agent DLLs. Clean architecture with dependency injection throughout. Every engine capability is abstracted behind an interface for testability.

---

## Getting Started

### Prerequisites

- **Windows 10/11** (x64)
- [**.NET 10 SDK**](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build & Run

```bash
git clone https://github.com/deusversus/ceai.git
cd ceai
dotnet build
dotnet run --project src/CEAISuite.Desktop
```

### First Launch

1. Open **Settings** → configure an AI provider (GitHub Copilot sign-in, or paste an API key for Anthropic/OpenAI)
2. Select a model from the dropdown
3. **Refresh** the process list → select a target game
4. Type in the chat: *"Scan for my health value, it's currently 100"*

### Run Tests

```bash
dotnet test    # 3,085 tests, ~70 seconds
```

---

## Project Status & Roadmap

This project was built in ~5 weeks by a solo developer using Claude as an AI pair programmer. See [CE-PARITY.md](CE-PARITY.md) for the honest feature comparison against Cheat Engine 7.5 and the development roadmap.

**Honest numbers:** We cover ~35-40% of CE 7.5's total feature surface (CE has 20 years of development). The core scan → find → freeze workflow works. The AI integration is something CE doesn't have at all.

**What's next:** 6 high-priority gaps that block core workflows, then power user features. See CE-PARITY.md for the full prioritized backlog with time estimates.

---

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10 |
| UI | WPF + AvalonDock |
| AI | Microsoft.Extensions.AI + custom AgentLoop |
| Disassembler | Iced.Intel |
| Assembler | Keystone Engine |
| Lua | MoonSharp (5.2) |
| MVVM | CommunityToolkit.Mvvm |
| Database | Microsoft.Data.Sqlite |
| Tests | xUnit (3,085 tests) |
| Native agents | C (MSVC / TCC) |

---

## License

License TBD.

---

<sub>Built in 5 weeks with .NET 10, two native C agent DLLs, and an AI that does the memory reading for you.</sub>
