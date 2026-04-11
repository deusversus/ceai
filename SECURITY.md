# CE AI Suite -- Security Document

## 1. Threat Model

### Actors

| Actor | Trust Level | Description |
|-------|-------------|-------------|
| **User** | Trusted | Local operator controlling the application via UI and AI chat |
| **AI Agent (LLM)** | Semi-trusted | Intermediary that interprets user intent and invokes engine tools |
| **Target Game Process** | Untrusted | Foreign process whose memory is read/written; may contain anti-debug or hostile code |
| **Plugin Authors** | Untrusted | Third parties providing DLLs loaded into the application process |
| **CT File Authors** | Untrusted | Providers of Cheat Engine table files that may contain embedded Lua scripts |
| **Network (Catalog)** | Untrusted | Remote HTTP endpoints serving plugin catalog and plugin binaries |

### Primary Risk

The AI agent is an intermediary that could be manipulated -- via prompt injection, hallucinated tool arguments, or malicious context -- into performing unintended operations on the wrong process or with malicious inputs. All destructive engine operations therefore require PID validation against the currently attached process and are gated behind user approval.

## 2. Trust Boundaries

```
User <--chat interface + approval cards--> AI Operator (AgentLoop)
    |                                           |
    | PermissionEngine enforces                 | ValidateDestructiveProcessId()
    | [Destructive] tool approval               | checks PID == AttachedProcessId
    |                                           |
    v                                           v
 UI (WPF/MVVM)                          AI Tool Functions
                                                |
                                                v
                                     Engine (IEngineFacade)
                                                |
                                  P/Invoke: VirtualAlloc,
                                  WriteProcessMemory,
                                  CreateRemoteThread
                                                |
                                                v
                                        Target Process
```

- **User <-> AI Operator**: Chat interface with approval cards for destructive actions.
- **AI Tools <-> Engine**: ProcessId validation and permission checks on every destructive call.
- **Engine <-> Target Process**: P/Invoke calls with journaled undo support.
- **Plugin System <-> Host**: Isolated `AssemblyLoadContext`; plugins have no access to `IServiceProvider`.
- **Network <-> Catalog**: HTTPS mandatory; SHA256 checksums required before any plugin DLL is loaded.

## 3. Attack Surface and Mitigations

| Surface | Risk | Mitigation | Status |
|---------|------|------------|--------|
| ProcessId mismatch | AI writes to wrong process | `ValidateDestructiveProcessId()` rejects if PID != attached process | MITIGATED |
| `CreateRemoteThread` / `LoadLibrary` injection | Core functionality for memory analysis | Operations journaled with undo; marked `[Destructive]` requiring user approval | ACKNOWLEDGED |
| Plugin DLL loading | Malicious code in app process | Isolated `AssemblyLoadContext`; no `IServiceProvider` access; unsigned plugins generate log warning | PARTIAL |
| Catalog plugin downloads | Tampered or malicious binaries | HTTPS enforced; SHA256 checksum mandatory; download rejected if checksum is null or empty | MITIGATED |
| CT file embedded Lua | Arbitrary script execution on import | NOT auto-executed; warning logged when embedded Lua detected; explicit `ExecuteLuaScript` call required | MITIGATED |
| Lua sandbox escape | `os.execute`, `io.open`, `loadstring` | MoonSharp `Preset_HardSandbox`; `os`/`io`/`debug`/`loadstring` blocked; instruction limit + timeout | MITIGATED |
| API key storage | Key theft from disk | DPAPI encryption at rest; `SensitiveString` with pinned + zeroed memory at runtime | MITIGATED |
| Prompt injection via tool results | AI follows injected instructions | `PermissionEngine` enforces approval for destructive tools regardless of LLM reasoning | MITIGATED |
| Path traversal in file operations | Read/write outside intended directories | `LoadCheatTable` and `SaveCheatTable` reject paths containing `..` | MITIGATED |
| Memory write without undo | Irreversible corruption of target process | `PatchUndoService` records original bytes; `ProcessWatchdogService` triggers rollback on anomalies | MITIGATED |

## 4. Security Controls Summary

- **PID validation guard**: Every `[Destructive]` tool call validates `processId == AttachedProcessId`.
- **Destructive tool approval**: `PermissionEngine` + `[Destructive]` attribute require user confirmation.
- **Operation journal with undo**: `OperationJournal` logs all operations; `PatchUndoService` enables rollback.
- **Watchdog with rollback**: `ProcessWatchdogService` monitors process liveness and triggers automatic rollback.
- **Lua sandbox**: MoonSharp `Preset_HardSandbox` with instruction counting and execution timeout.
- **Plugin isolation**: `AssemblyLoadContext` per plugin; no service locator or `IServiceProvider` exposure.
- **Catalog checksums**: SHA256 verification mandatory; null or empty checksums cause download rejection.
- **DPAPI key storage**: API keys encrypted at rest via Windows DPAPI; zeroed from memory on dispose.
- **Path traversal rejection**: File-based tools reject paths containing `..` sequences.

## 5. Reporting Security Issues

If you discover a security vulnerability in CE AI Suite, please report it responsibly.

**Email:** security@ceaisuite.dev

Please do not open public GitHub issues for security vulnerabilities. We will acknowledge receipt within 48 hours and aim to provide a fix or mitigation plan within 7 days.
