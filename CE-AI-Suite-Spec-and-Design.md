# CE AI Suite - Project Specification and Design Sheet

## 1. Project Summary

### Working title
CE AI Suite

### Vision
Build a full Cheat Engine-class desktop suite with a built-in AI operator that can understand user goals, inspect live process state, drive memory-analysis workflows, automate repetitive reverse-engineering tasks, and generate reliable artifacts such as scans, pointer chains, patches, scripts, and trainers.

### Product thesis
Traditional memory tools are powerful but labor-intensive. An AI layer should not replace the engine; it should expose the engine as a safe, structured toolset and let users operate at the intent level:

- "Find where battle EXP is calculated."
- "Trace the write path for this address."
- "Build a stable signature for this hook."
- "Turn this manual workflow into a reusable table entry."

### Product shape
This should be a serious desktop application, not a toy helper:

- full memory scanner
- pointer/path tooling
- debugger and breakpoint management
- disassembly and code analysis
- address list and table management
- script execution
- AI-assisted workflow orchestration
- local-first operation

## 2. Goals

### Primary goals

1. Deliver a CE-class workflow for memory analysis and patching.
2. Add an AI control plane that can operate the suite through a structured internal API.
3. Support both expert users and semi-guided users.
4. Preserve manual control at all times; AI should assist, not trap the user.
5. Make investigations reproducible through saved sessions, tool calls, tables, and scripts.

### Secondary goals

1. Expose the toolset over MCP/local RPC for external agent use.
2. Capture investigation history so the AI can resume context.
3. Support script generation for common outcomes: AA scripts, Lua helpers, patch manifests, and trainer exports.

### Non-goals

1. Multiplayer cheating support.
2. Cloud-only execution.
3. Blind fully autonomous patching without user visibility.
4. Re-implementing every historical CE feature before shipping an MVP.

## 3. Intended Use Cases

### Core use cases

1. Scan for a changing value and narrow candidates.
2. Follow pointers and identify stable paths.
3. Find what writes/accesses an address.
4. Trace a function and identify where a gameplay value is computed.
5. Generate a stable hook and patch.
6. Save results as reusable entries/scripts/profiles.
7. Ask the AI to explain or automate the next step.

### Power-user use cases

1. AOB/signature generation.
2. Function boundary and call-flow analysis.
3. Structure dissection and field labeling.
4. Session replay and scripted investigations.
5. Building trainers from existing investigations.

### AI-native use cases

1. "Observe this address over three battles and tell me what changes first."
2. "Compare these two code paths and explain which one is earlier in the reward flow."
3. "Turn this address-watch workflow into a repeatable macro."
4. "Suggest the safest hook point for immediate level-up handling."

## 4. Product Requirements

### Functional requirements

#### 4.1 Process and memory
- attach/detach to running processes
- enumerate modules, base addresses, regions, permissions
- read/write memory primitives:
  - bytes
  - int8/int16/int32/int64
  - float/double
  - strings
  - pointers
- suspend/resume process or threads where permitted

#### 4.2 Scanning
- exact/unknown/increased/decreased/changed/unchanged scans
- value-type aware scanning
- iterative result narrowing
- memory region filtering
- saved scans
- grouped scans and compare sessions

#### 4.3 Pointer and path tools
- pointer chain resolution
- pointer rescans
- path stability ranking
- module-relative addressing

#### 4.4 Debugging/disassembly
- disassembly view
- software/hardware breakpoints where available
- execution/data breakpoint support
- register and stack views
- trace logging
- call/caller navigation
- patch/restore bytes

#### 4.5 Table/address list management
- hierarchical address list
- per-entry type metadata
- scripts and hotkeys
- import/export table format
- comments, labels, tags

#### 4.6 Scripting
- internal automation scripting layer
- script execution against current process/session
- script templates for scans, hooks, signatures, logging

#### 4.7 AI operator
- chat pane connected to current process/session
- explicit tool calls against internal engine
- action preview before writes/patches when risk is nontrivial
- artifact generation:
  - scripts
  - notes
  - address list entries
  - signatures
  - investigation summaries

#### 4.8 Session persistence
- save and restore investigations
- retain scan history, notes, labels, breakpoints, generated scripts
- session transcript of AI actions and outputs

### Non-functional requirements

1. Local-first by default.
2. Fast enough for practical scanning and iteration.
3. Stable under long-running sessions.
4. Strong crash recovery and artifact persistence.
5. Clear audit trail of AI actions.
6. Pluggable LLM backend design.

## 5. Recommended Architecture

## 5.1 High-level recommendation

Use a **hybrid architecture**:

- a **native desktop engine** for process, memory, scan, debugger, and UI concerns
- an **internal automation API** over the engine
- an **AI orchestration layer** that consumes the API
- an optional **external MCP gateway** for IDEs/agents

This is better than directly embedding all AI logic into engine internals because it creates a stable boundary between memory operations and agent behavior.

## 5.2 Architecture diagram

```text
+--------------------------------------------------------------+
|                        Desktop Application                   |
|                                                              |
|  +-------------------+     +------------------------------+  |
|  |   Native UI       |<--->|   Investigation Session      |  |
|  |  tables/scans/    |     |   state, notes, artifacts    |  |
|  |  debugger/chat    |     +------------------------------+  |
|  +-------------------+                    ^                  |
|            |                               |                 |
|            v                               |                 |
|  +--------------------------------------------------------+ |
|  |                Internal Automation API                 | |
|  | read_memory | write_memory | scan | disassemble | ... | |
|  +--------------------------------------------------------+ |
|            |                               ^                 |
|            v                               |                 |
|  +-------------------+     +------------------------------+  |
|  | Core Engine       |     | AI Orchestrator             |  |
|  | process/memory/   |     | tool planner + LLM adapter  |  |
|  | scan/debugger     |     | approvals + summaries       |  |
|  +-------------------+     +------------------------------+  |
|                                           |                  |
|                                           v                  |
|                              +----------------------------+  |
|                              | Optional MCP/RPC Gateway   |  |
|                              +----------------------------+  |
+--------------------------------------------------------------+
```

## 6. Core Subsystems

### 6.1 Engine subsystem
Responsible for:

- process enumeration and attach
- module/region maps
- raw memory operations
- scan engine
- pointer engine
- disassembler/debugger integration
- patch application and revert

Preferred implementation style:

- native language for core memory/debugger paths
- clean service boundaries
- deterministic, testable APIs

### 6.2 Investigation model
Represents all user/AI work:

- target process
- selected modules
- watch addresses
- scan sessions
- labels/comments
- breakpoints and hit logs
- generated scripts
- AI transcript and artifacts

### 6.3 AI orchestration subsystem
Responsible for:

- translating user intent into tool calls
- maintaining task state
- summarizing findings
- asking for confirmation on risky operations
- generating scripts, signatures, notes, and follow-up actions

### 6.4 UI subsystem
Views:

- process picker
- memory viewer
- scanner
- disassembler/debugger
- address list/table tree
- script editor
- AI chat/operator panel
- session log/artifact panel

## 7. AI Tool Contract

The AI must operate through explicit tools, not hidden internal shortcuts.

### Required AI-facing operations

#### Read/inspect
- `list_processes`
- `attach_process`
- `list_modules`
- `read_memory`
- `read_pointer_chain`
- `read_registers`
- `read_stack`
- `disassemble`
- `analyze_function`
- `list_breakpoints`
- `inspect_address_list`

#### Search/analyze
- `start_scan`
- `refine_scan`
- `scan_results`
- `scan_aob`
- `find_writes_to_address`
- `find_accesses_to_address`
- `find_references`
- `trace_callers`
- `dissect_structure`
- `compare_snapshots`

#### Modify
- `write_memory`
- `patch_bytes`
- `restore_patch`
- `set_breakpoint`
- `remove_breakpoint`
- `run_script`
- `create_table_entry`
- `update_table_entry`
- `save_session`

#### Artifact generation
- `generate_signature`
- `generate_aa_script`
- `generate_lua_script`
- `export_table`
- `summarize_investigation`

### Tool contract rules

1. All tool calls must be logged.
2. Write/patch operations should support preview and dry-run where possible.
3. Destructive or ambiguous writes should require confirmation.
4. Tool outputs must be structured for both UI and machine use.

## 8. Data Model

### Core entities

#### Project
- id
- name
- game/process profile
- default module assumptions
- saved tables/scripts

#### InvestigationSession
- id
- target process id/name
- module snapshot
- timestamped actions
- AI transcript
- findings

#### AddressEntry
- id
- label
- address expression
- resolved address
- type
- current value
- notes
- tags

#### ScanSession
- id
- scan type
- initial constraints
- refinement history
- result set

#### Patch
- id
- target address/module
- original bytes
- patched bytes
- enable/disable script
- verification status

#### AIActionLog
- id
- prompt/intent
- tool calls
- outputs
- approvals
- artifacts created

## 9. UX Design Principles

1. Manual-first, AI-accelerated.
2. The user can always inspect what the AI did.
3. AI suggestions should appear as proposed actions, not hidden side effects.
4. The user should be able to promote AI findings into permanent artifacts with one click.
5. Debugger context should be easy to share with the AI.

### Recommended UX pattern

- left: scans/address list/session tree
- center: memory/disassembly/debugger view
- right: AI operator pane
- bottom: action log / console / watch hits

## 10. Safety and Guardrails

### Scope guardrails
- default positioning should emphasize offline/single-player use and authorized software analysis
- clear warnings for risky writes and process instability
- explicit confirmation before broad patching or script execution against unknown targets

### Technical guardrails
- patch backup and restore
- session autosave
- crash recovery
- write throttling for repeated automation
- sandbox mode for script dry-runs where feasible

### AI guardrails
- no silent write operations in high-risk contexts
- no hidden background patching
- clear distinction between observed facts and AI hypotheses

## 11. Integration Strategy

### Option A: Embedded AI in a fork
Pros:
- tightest UI integration
- direct access to internal objects
- best end-user experience

Cons:
- higher complexity
- harder to swap models/providers
- more engine/UI coupling

### Option B: Companion service controlling app internals
Pros:
- cleaner separation
- easier MCP support
- easier testing and model swapping

Cons:
- more IPC surface
- slightly weaker sense of "built-in" AI

### Recommendation
Implement the AI as an **internal service with a strict tool API**, even if it ships inside the app. That preserves modularity while still feeling built-in.

## 12. Suggested Technical Direction

### Reference base
Use Cheat Engine 7.5 source as:

- reference for scan pipeline
- pointer tooling inspiration
- table model and UX precedent
- debugger/disassembler integration reference

Do not blindly inherit architecture without first defining modern boundaries.

### Proposed implementation stack

#### Core engine
- native systems language for memory/debugger/scanner paths
- maintain clean abstractions around OS-specific APIs

#### Desktop app
- native Windows desktop UI or cross-platform desktop UI if justified

#### AI layer
- local model or remote provider abstraction
- prompt/tool runtime
- MCP-compatible schema for tools

#### Persistence
- SQLite for sessions, scans, artifacts, tool logs
- file-based export for tables/scripts/profiles

## 13. MVP Definition

### MVP goal
Ship a usable AI-assisted memory analysis tool, not the entire historical CE surface.

### MVP feature set

1. Process attach and memory read/write.
2. Exact/increased/decreased/unknown scans.
3. Address list with labels and notes.
4. Basic disassembly at addresses.
5. Breakpoint support for "what writes/accesses".
6. AI operator pane with a tool-backed agent.
7. Script generation for common hooks.
8. Session save/load.

### Post-MVP

1. Pointer rescans.
2. Structure dissection.
3. Signature generation.
4. Trainer builder.
5. External MCP endpoint.
6. Multi-model backend support.

## 14. Milestones

### Milestone 1 - Core foundation
- engine skeleton
- process attach/read/write
- module map
- session model

### Milestone 2 - Scanner
- initial scan/refine flow
- result management
- value watchers

### Milestone 3 - Debugger/disassembly
- disassembly view
- write/access tracing
- breakpoint logging

### Milestone 4 - AI integration
- tool contract implementation
- operator pane
- action log and approvals

### Milestone 5 - Reusable artifacts
- script generation
- table export/import
- saved profiles/sessions

## 15. Major Risks

1. Tight coupling between UI and engine if based too directly on old CE architecture.
2. Debugger complexity and stability on modern Windows.
3. AI hallucinating unsafe patches if tool outputs are underspecified.
4. Performance bottlenecks in scans if architecture is too chatty.
5. Scope explosion from trying to clone all of CE before validating the AI workflow.

## 16. Open Questions

1. Full fork or companion-first architecture?
2. Native language choice for the engine?
3. Do we preserve CE table compatibility exactly, partially, or via importer/exporter?
4. Will AI run local-only, remote-only, or provider-pluggable?
5. Should the first release target only Windows x64?

## 17. Immediate Next Step Recommendation

Before writing code, do a focused architecture pass on:

1. engine boundary
2. AI tool contract
3. session/data model
4. compatibility strategy with CE concepts and artifacts

That design work will determine whether this becomes a maintainable product or just another brittle plugin experiment.
