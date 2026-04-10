# System Amelioration Plan — AI Operator Subsystem

## Context

Four parallel audits were conducted against the AI Operator subsystem:
1. **Wiring & Completeness** — internal consistency, dead code, missing connections
2. **Claude Code Comparison** — feature parity gaps with the TypeScript original
3. **Tool Stress Test** — tool registration, dispatch, safety, edge cases
4. **Tool Accessibility & UI Parity** — discoverability, UI equivalents, bidirectional parity

This plan addresses **every finding** from all four audits, organized into implementation work packages ordered by priority.

---

## Work Package 1: Critical Safety & Correctness (Do First)

### WP1.1 — Add ~20 uncategorized tools to ToolCategories
- **Source:** Audit 3 Finding 1.1
- **Problem:** Tools exist via reflection but aren't in any category — the AI can never discover them via `request_tools`
- **Tools missing from categories:**
  - `ExecuteLuaScript`, `ValidateLuaScript`, `EvaluateLuaExpression` → add to new "lua" category
  - `SetConditionalBreakpoint`, `TraceFromAddress` → add to "breakpoints" category
  - `UnregisterBreakpointLuaCallback` → add to "breakpoints" category
  - `GroupedScan`, `UndoScan` → add to "scanning" or "scanning_advanced" category
  - `ResumePointerScan`, `RescanAllPointerPaths`, `SavePointerMap`, `LoadPointerMap`, `ComparePointerMaps` → add to "pointer_scanning" category
  - `LoadSymbolsForModule`, `ResolveAddressToSymbol` → add to "disassembly" or new "symbols" category
  - `DeleteSession` → add to "session" category
  - `TraceFieldWriters` → add to "analysis" category
- **File:** `src/CEAISuite.Application/AiOperatorService.cs` (ToolCategories dictionary)
- **Verification:** After fix, calling `request_tools("lua")` returns the 3 Lua tools

### WP1.2 — Mark `ExecuteLuaScript` as `[Destructive]`
- **Source:** Audit 3 Finding 1.2
- **Problem:** Lua can call `writeFloat`, `writeInteger`, `autoAssemble()` — arbitrary memory writes without user approval
- **Fix:** Add `[Destructive]` attribute and add `"ExecuteLuaScript"` to `DangerousTools.Names`
- **File:** `src/CEAISuite.Application/AiToolFunctions.Lua.cs`

### WP1.3 — Fix `AttachProcess` attribute
- **Source:** Audit 3 Finding 6.5
- **Problem:** Marked `[ReadOnlyTool]` but opens a process handle — could be speculatively executed during streaming
- **Fix:** Remove `[ReadOnlyTool]`, leave as default (requires no special attribute)
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP1.4 — Fix `ScanForPointers` using throwaway service instance
- **Source:** Audit 3 Finding 3.5
- **Problem:** Creates `new PointerScannerService(engineFacade)` instead of using the injected one — `ResumePointerScan` can never resume
- **Fix:** Use the constructor-injected `pointerScannerService` field
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP1.5 — Fix `SavePointerMap` re-scanning
- **Source:** Audit 3 Finding 3.6
- **Problem:** Re-runs the scan instead of saving previous results
- **Fix:** Store scan results from most recent `ScanForPointers` call in a field, save those
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP1.6 — Fix `SetHotkey` optimistic state
- **Source:** Audit 3 Finding 3.4
- **Problem:** Sets `IsScriptEnabled = true` before async operation completes — inconsistent state on failure
- **Fix:** Move state update into the ContinueWith success path
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

---

## Work Package 1B: Chat Pipeline Critical Fixes
- **Source:** CHAT-PIPELINE-AUDIT.md (separate prior audit)

### WP1B.1 — Save chat on general exceptions
- **Problem:** When a general `Exception` occurs during AI processing, the error message is added to display history but `SaveCurrentChat()` is never called. User message + tool results are lost on restart.
- **Fix:** Add `SaveCurrentChat()` in the `catch(Exception)` block after adding the error message to `_displayHistory`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs:882-889`

### WP1B.2 — Add debounced auto-save timer
- **Problem:** Chats are only saved on completion/cancellation/switch. If the app crashes during a long tool execution or stuck LLM call, messages are lost.
- **Fix:** After any message is added to `_displayHistory`, start a 10-second debounce timer. If it fires without reset, call `SaveCurrentChat()`. Catches crash-during-execution gap.
- **File:** `src/CEAISuite.Application/AiOperatorService.cs` (new timer field + logic)

### WP1B.3 — Restore system context on chat resume
- **Problem:** `ReplayFromSaved()` only reconstructs user/assistant/tool messages — no system context is re-injected. Resumed sessions have no awareness of prior context.
- **Fix:** After `ReplayFromSaved()` completes in `SwitchChatAsync()`, inject a system reminder to `_historyManager` with: chat title, message count, last topics discussed (similar to `PostCompactionRestorer`)
- **File:** `src/CEAISuite.Application/AiOperatorService.cs:1612`

### WP1B.4 — Change compaction circuit breaker to exponential backoff
- **Problem:** After 3 consecutive compaction failures, compaction is permanently disabled for the session. Long conversations can't recover.
- **Fix:** Replace permanent disable with exponential backoff: after 3 failures, skip compaction for 5 turns. If 4th attempt succeeds, reset. If it fails, skip 10 turns. Cap at 20-turn intervals.
- **File:** `src/CEAISuite.Application/AgentLoop/AgentLoop.cs:328, 365-366`

**Note:** Chat Pipeline Issue #5 (MicroCompaction locking) is already covered by WP4.1.

---

## Work Package 2: Hook & Agent Loop Wiring Gaps

### WP2.1 — Wire 3 unwired hook types into AgentLoop
- **Source:** Audit 1 Finding H1
- **Problem:** `PostLlmHook`, `PreCompactionHook`, `PostCompactionHook` are fully implemented in `HookRegistry` but `AgentLoop.RunTurnAsync()` never calls them
- **Fix:**
  - Call `RunPostLlmHooksAsync()` after LLM response is received (after streaming completes)
  - Call `RunPreCompactionHooksAsync()` before compaction pipeline runs
  - Call `RunPostCompactionHooksAsync()` after compaction pipeline completes
- **Files:** `src/CEAISuite.Application/AgentLoop/AgentLoop.cs`

### WP2.2 — Wire or auto-deny subagent destructive tool approvals
- **Source:** Audit 1 Finding M5
- **Problem:** `SubagentRequest.ApprovalBubbleCallback` is declared but never wired — destructive tools in subagents timeout after 5 min and auto-deny
- **Fix:** In `SubagentManager.Spawn()`, wire the callback to bubble approval requests to the parent agent's channel, or add a pre-tool hook in subagent mode that auto-denies destructive tools with a clear message
- **File:** `src/CEAISuite.Application/AgentLoop/SubagentSystem.cs`

### WP2.3 — Wire `AuthRefreshCallback` and `AllowNonStreamingFallback` options
- **Source:** Audit 1 Finding M3
- **Problem:** Declared in `AgentLoopOptions` but never consumed
- **Fix:**
  - Wire `AuthRefreshCallback` into `RetryPolicy` auth error handling
  - Wire `AllowNonStreamingFallback` into streaming timeout recovery path
- **Files:** `AgentLoop.cs`, `RetryPolicy.cs`, `AgentLoopOptions.cs`

### WP2.4 — Wire `PostCompactionRestorer.AddressTableSummary`
- **Source:** Audit 1 Finding M4
- **Problem:** Always null — after compaction, address table context is not explicitly restored
- **Fix:** Extract address table summary from context provider during `CaptureSnapshot()`
- **File:** `src/CEAISuite.Application/AgentLoop/PostCompactionRestorer.cs`

---

## Work Package 3: Dead Code Cleanup

### WP3.1 — Remove dead enum values
- **Source:** Audit 1 Findings H2, H3
- **Items:**
  - `AgentTransition.CompactionRetry` — in loop condition but never set
  - `AgentTransition.Failed` — defined but never assigned
- **Fix:** Remove from enum and all references
- **File:** `src/CEAISuite.Application/AgentLoop/AgentLoopState.cs`, `AgentLoop.cs`

### WP3.2 — Remove dead types
- **Source:** Audit 1 Findings M1, M2, M6
- **Items:**
  - `ContextSection` / `ContextInjectionFormat` — superseded by `PromptSection`
  - `HookCondition` / `HookTrustLevel` — defined but never referenced
  - `SubagentRequest.WorkingDirectory` — dead property
- **Files:** `ContextSection.cs`, `HookSystem.cs`, `SubagentSystem.cs`

---

## Work Package 4: Thread Safety & Robustness

### WP4.1 — Fix `MicroCompaction` shared object mutation
- **Source:** Audit 1 Finding M7
- **Problem:** Mutates `msg.Contents[j]` on shared objects without going through `ChatHistoryManager`'s lock
- **Fix:** Use `ChatHistoryManager.ReplaceToolResult(callId, newContent)` instead of direct mutation
- **File:** `src/CEAISuite.Application/AgentLoop/MicroCompaction.cs`

### WP4.2 — Fix non-atomic `_totalToolCalls` increment
- **Source:** Audit 3 Finding 3.1
- **Problem:** Serial path uses `_totalToolCalls++` (non-atomic) while parallel path correctly uses `Interlocked.Increment`
- **Fix:** Use `Interlocked.Increment(ref _totalToolCalls)` in both paths
- **File:** `src/CEAISuite.Application/AgentLoop/ToolExecutor.cs`

### WP4.3 — Fix non-thread-safe `_consecutiveOverloadCount`
- **Source:** Audit 1 Finding L3
- **Fix:** Use `Interlocked.Increment`/`Interlocked.Exchange`
- **File:** `src/CEAISuite.Application/AgentLoop/RetryPolicy.cs`

### WP4.4 — Add process liveness checks to unchecked tools
- **Source:** Audit 3 Findings 6.1-6.3
- **Problem:** `ProbeAddress`, `BrowseMemory`, `HexDump`, `Disassemble` don't check `IsProcessAlive(processId)` before memory reads
- **Fix:** Add early-return guard `if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";`
- **Files:** `AiToolFunctions.cs`, `AiToolFunctions.Disassembly.cs`

### WP4.5 — Add CancellationToken to heavy tool methods
- **Source:** Audit 3 Finding 3.2
- **Problem:** None of the tool methods accept CancellationToken — heavy analysis tools can't be cancelled
- **Fix:** Add `CancellationToken cancellationToken = default` to: `FindWritersToOffset`, `FindFunctionBoundaries`, `ScanForPointers`, `GetCallerGraph`, `SearchInstructionPattern`, `FindByMemoryOperand`, `TraceFieldWriters`
- **Files:** All AiToolFunctions partials with heavy tools

---

## Work Package 5: Claude Code Feature Parity (Important Gaps)

### WP5.1 — Reactive compaction (recover from prompt_too_long)
- **Source:** Audit 2 Gap #1
- **Problem:** CE AI Suite only does proactive compaction checks — an API `prompt_too_long` error crashes instead of recovering
- **Fix:** In `RetryPolicy` or `AgentLoop`, catch `prompt_too_long` errors, trigger emergency compaction, and retry the turn
- **Files:** `AgentLoop.cs`, `RetryPolicy.cs`, `ErrorClassifier.cs`

### WP5.2 — Improve token counting accuracy
- **Source:** Audit 2 Gap #8
- **Problem:** `chars/4` estimate vs actual API-reported `input_tokens` — compaction triggers too early or too late
- **Fix:** After each API response, read `usage.input_tokens` from the response and use it for threshold calculations instead of the character estimate
- **Files:** `AgentLoop.cs`, `TokenBudget.cs`

### WP5.3 — Withhold recoverable errors from UI
- **Source:** Audit 2 Gap #6
- **Problem:** Errors show immediately in UI even if recovery succeeds, causing confusing flicker
- **Fix:** Buffer error events; only emit to channel if recovery fails. If recovery succeeds, discard the error event.
- **File:** `AgentLoop.cs`

### WP5.4 — Sibling error cancellation for parallel tools
- **Source:** Audit 2 Gaps #4, #5
- **Problem:** When a Bash/destructive tool fails, parallel siblings continue to completion
- **Fix:** Create per-batch `CancellationTokenSource`; on first critical tool failure, cancel siblings. Introduce per-tool child CTS linked to the parent.
- **File:** `src/CEAISuite.Application/AgentLoop/ToolExecutor.cs`

### WP5.5 — Expand streaming tool execution
- **Source:** Audit 2 Gap #3
- **Problem:** Only read-only tools execute speculatively during streaming; Claude Code executes all concurrency-safe tools
- **Fix:** Extend speculative execution to all `[ConcurrencySafe]` tools, not just `[ReadOnlyTool]`
- **File:** `src/CEAISuite.Application/AgentLoop/ToolExecutor.cs`

### WP5.6 — Relevance-based memory prefetch
- **Source:** Audit 2 Gap #7
- **Problem:** All memories injected into context, wasting tokens
- **Fix:** Before injecting memories, score them against the current query (keyword match or lightweight AI side-call) and only inject relevant ones
- **File:** `src/CEAISuite.Application/AgentLoop/MemorySystem.cs`

---

## Work Package 6: Minor Fixes & Polish

### WP6.1 — Fix `SkillSystem` catalog mutation on load
- **Source:** Audit 1 Finding L8
- **Problem:** `LoadSkillWithArgs()` permanently modifies the catalog entry with substituted instructions
- **Fix:** Create a per-activation copy instead of mutating the original
- **File:** `src/CEAISuite.Application/AgentLoop/SkillSystem.cs`

### WP6.2 — Fix `StdioMcpTransport.ConnectAsync` spurious async
- **Source:** Audit 1 Finding L5
- **Fix:** Remove `async` from method signature, return `Task.CompletedTask`
- **File:** `src/CEAISuite.Application/AgentLoop/McpClient.cs`

### WP6.3 — Improve `ContextManagementSerializer` completeness
- **Source:** Audit 1 Finding L1
- **Fix:** Include `trigger_threshold` and `retention_target` in serialization
- **File:** `src/CEAISuite.Application/AgentLoop/ContextManagementStrategy.cs`

### WP6.4 — Improve cron validation range checks
- **Source:** Audit 1 Finding L2
- **Fix:** Add semantic range checks in `IsValid()` (minute 0-59, hour 0-23, etc.)
- **File:** `src/CEAISuite.Application/AgentLoop/CeaiTaskScheduler.cs`

### WP6.5 — Improve `MemorySystem.IsSimilar` for hex addresses
- **Source:** Audit 1 Finding L7
- **Fix:** Exempt tokens matching `0x[0-9A-Fa-f]+` from the word overlap comparison
- **File:** `src/CEAISuite.Application/AgentLoop/MemorySystem.cs`

### WP6.6 — Cache permission glob regexes
- **Source:** Audit 3 Finding 5.4
- **Fix:** Cache compiled `Regex` per glob pattern in a `ConcurrentDictionary`
- **File:** `src/CEAISuite.Application/AgentLoop/PermissionEngine.cs`

### WP6.7 — Pre-build tool name dictionary in ToolExecutor
- **Source:** Audit 3 Finding 2.1
- **Fix:** Replace linear scan in `ResolveFunction` with `Dictionary<string, AIFunction>`
- **File:** `src/CEAISuite.Application/AgentLoop/ToolExecutor.cs`

### WP6.8 — Add `[ConcurrencySafe]` to `UnregisterBreakpointLuaCallback`
- **Source:** Audit 3 Finding 5.2
- **File:** `src/CEAISuite.Application/AiToolFunctions.Breakpoints.cs`

---

## Work Package 7: Tool Accessibility — 22 Uncategorized Tools
- **Source:** Audit 4 Part A

Audit 4 produced a complete tool inventory. 22 tools are public methods on `AiToolFunctions` but appear in NO category and are NOT in the Core set. The AI agent cannot discover them via `list_tool_categories` or `request_tools`. This is the expansion of WP1.1 with the full list from Audit 4.

### WP7.1 — Create new "lua" category
- **Tools:** `ExecuteLuaScript`, `ValidateLuaScript`, `EvaluateLuaExpression`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

### WP7.2 — Add to existing "breakpoints" category
- **Tools:** `SetConditionalBreakpoint`, `TraceFromAddress`, `RegisterBreakpointLuaCallback`, `UnregisterBreakpointLuaCallback`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

### WP7.3 — Add to existing "scanning_advanced" category
- **Tools:** `UndoScan`, `GroupedScan`, `ResumePointerScan`, `RescanAllPointerPaths`, `SavePointerMap`, `LoadPointerMap`, `ComparePointerMaps`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

### WP7.4 — Add to existing "scripts" category
- **Tools:** `ExecuteAutoAssemblerScript`, `DisableAutoAssemblerScript`, `ListRegisteredSymbols`, `ResolveRegisteredSymbol`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

### WP7.5 — Add to existing "disassembly" category
- **Tools:** `TraceFieldWriters`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

### WP7.6 — Add to existing "sessions" category
- **Tools:** `DeleteSession`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

### WP7.7 — Create new "symbols" category
- **Tools:** `LoadSymbolsForModule`, `ResolveAddressToSymbol`
- **File:** `src/CEAISuite.Application/AiOperatorService.cs`

---

## Work Package 8: Incorrect Tool Safety Attributes
- **Source:** Audit 4 Part D

### WP8.1 — Fix `SaveCheatTable` attribute
- **Problem:** Tagged `[ReadOnlyTool]` but writes a file to disk
- **Fix:** Change to `[ConcurrencySafe]`
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP8.2 — Fix `SavePointerMap` attribute
- **Problem:** Tagged `[ReadOnlyTool]` but writes a file to disk
- **Fix:** Change to `[ConcurrencySafe]`
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP8.3 — Fix `UndoScan` attribute
- **Problem:** Tagged `[ReadOnlyTool]` but modifies scan service internal state
- **Fix:** Change to `[ConcurrencySafe]`
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP8.4 — Add `[Destructive]` to `ExecuteAutoAssemblerScript`
- **Problem:** Already in `DangerousTools.Names` but verify attribute is correct
- **File:** `src/CEAISuite.Application/AiToolFunctions.Scripts.cs`

---

## Work Package 9: UI Parity — AI Tools Missing UI Surface
- **Source:** Audit 4 Part B

These AI tools have no manual UI equivalent. For each, add the described UI surface.

### WP9.1 — Add "Trace Field Writers" to address table context menu
- **Problem:** `TraceFieldWriters` is a flagship AI analysis tool with no UI trigger
- **Fix:** Add right-click → "Trace What Writes This" context menu item on address table entries, opens a results dialog
- **Files:** `AddressTableViewModel.cs`, `MainWindow.xaml` (context menu)

### WP9.2 — Add "Check Hook Conflicts" pre-flight to hook install UI
- **Problem:** `CheckHookConflicts` is AI-only; UI users get no warning before conflicting hooks
- **Fix:** Auto-run conflict check before `InstallCodeCaveHook` in BreakpointsViewModel and show warning dialog if conflicts found
- **File:** `BreakpointsViewModel.cs`

### WP9.3 — Add safety watchdog panel or status indicators
- **Problem:** `CheckAddressSafety`, `ListUnsafeAddresses`, `ClearUnsafeAddress` are AI-only
- **Fix:** Add a "Safety" indicator to the status bar or a small panel showing unsafe address count with clear option
- **Files:** `StatusBarViewModel.cs` or new watchdog indicator in `MainWindow.xaml`

---

## Work Package 10: UI Parity — UI Actions Missing AI Tools
- **Source:** Audit 4 Part C

These UI actions have no AI tool equivalent. For each, add the described tool method.

### WP10.1 — Add `ModifyAddressTableEntry` tool
- **Problem:** AI has no way to change an entry's address, type, or display value after creation
- **Fix:** New tool method that accepts entryId + optional address/type/value/showAsHex/showAsSigned
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP10.2 — Add `AssembleInstruction` tool
- **Problem:** DisassemblerViewModel has EditInstruction but AI has no inline assembly tool
- **Fix:** New tool wrapping the Keystone assembler (already used by AA engine)
- **File:** `src/CEAISuite.Application/AiToolFunctions.Disassembly.cs`

### WP10.3 — Add `ExportStructDefinition` tool
- **Problem:** StructureDissectorViewModel can export to C/CE format but AI cannot
- **Fix:** New tool wrapping `GenerateStructDefinition` with format parameter (C/CE)
- **File:** `src/CEAISuite.Application/AiToolFunctions.Analysis.cs`

### WP10.4 — Add `RemoveAllBreakpoints` tool
- **Problem:** BreakpointsViewModel has RemoveAll but AI must remove one at a time
- **Fix:** New batch-remove tool
- **File:** `src/CEAISuite.Application/AiToolFunctions.Breakpoints.cs`

### WP10.5 — Add `ResetScan` tool
- **Problem:** ScannerViewModel has ResetScan but AI has no explicit reset
- **Fix:** New tool that clears scan state without starting a new scan
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP10.6 — Add `ResetLuaEngine` tool
- **Problem:** LuaConsoleViewModel has ResetEngine but AI cannot reset Lua state
- **Fix:** New tool wrapping engine reset
- **File:** `src/CEAISuite.Application/AiToolFunctions.Lua.cs`

### WP10.7 — Add `NopRegion` tool
- **Problem:** MemoryBrowserViewModel has NopSelection but AI has no NOP-fill tool
- **Fix:** New tool that writes 0x90 bytes over a given address range (with safety checks)
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

### WP10.8 — Add `IncrementValue` / `DecrementValue` tools
- **Problem:** AddressTableViewModel has Ctrl+Up/Down but AI has no dedicated increment/decrement
- **Fix:** New tool that reads current value, adds/subtracts delta, writes back
- **File:** `src/CEAISuite.Application/AiToolFunctions.cs`

---

## Implementation Order

| Priority | Package | Scope | Est. Items |
|----------|---------|-------|-----------|
| 1 | **WP1** Critical Safety | Fix broken attributes, throwaway services, optimistic state | 6 items |
| 2 | **WP1B** Chat Pipeline | Save on error, auto-save timer, resume context, circuit breaker | 4 items |
| 3 | **WP8** Incorrect Attributes | Fix 4 more wrong safety attributes from Audit 4 | 4 items |
| 4 | **WP7** Tool Categorization | Add 22 tools to categories so AI can discover them | 7 items |
| 5 | **WP4** Thread Safety | Atomic ops, liveness checks, CancellationToken | 5 items |
| 6 | **WP2** Hook/Loop Wiring | Wire 3 hooks, subagent approvals, unused options | 4 items |
| 7 | **WP10** New AI Tools | 8 new tool methods for UI parity | 8 items |
| 8 | **WP3** Dead Code | Remove dead enums, types, properties | 2 items |
| 9 | **WP9** New UI Surfaces | 3 new UI elements for AI tool parity | 3 items |
| 10 | **WP6** Minor Fixes | Polish, perf, edge cases | 8 items |
| 11 | **WP5** Claude Code Parity | Reactive compaction, token counting, error withholding, etc. | 6 items |

**Total: 57 items across 11 work packages**

## Verification

After each work package:
- `dotnet build -c Debug && dotnet build -c Release` — 0 errors, 0 warnings
- `dotnet test` — all tests pass (currently 2254)
- For WP7: verify `list_tool_categories` shows "lua", "symbols" as new categories
- For WP1.2: verify `ExecuteLuaScript` triggers approval prompt in permission engine
- For WP8: verify changed attributes via `ToolAttributeCache` unit tests
- For WP10: add unit tests for each new tool method
- For WP5.1: add test simulating prompt_too_long error → verify recovery compaction fires
