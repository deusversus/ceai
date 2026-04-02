# Claude Code Architecture Analysis — Full Gap Closure Roadmap for CEAI

> Compiled from exhaustive review of ~1900 files in Claude Code's TypeScript source.
> Every item below is achievable and should be implemented.

---

## 1. AGENT LOOP STATE MACHINE

### What Claude Code Does
The core loop in `query.ts:queryLoop()` (lines 241-1729) is a `while(true)` with an **explicit state object** and **named transitions**:

```
State = {
  messages, toolUseContext, maxOutputTokensOverride,
  autoCompactTracking, stopHookActive,
  maxOutputTokensRecoveryCount, hasAttemptedReactiveCompact,
  turnCount, pendingToolUseSummary, transition
}
```

Transitions: `next_turn`, `collapse_drain_retry`, `reactive_compact_retry`, `max_output_tokens_escalate`, `max_output_tokens_recovery`, `stop_hook_blocking`, `token_budget_continuation`.

Each iteration: stream LLM response → execute tools → decide (continue/recover/end) → transition.

### What We Have
`AiOperatorService.SendMessageStreamingAsync()` runs a single `agent.RunAsync()` call. MAF handles the inner tool-call loop, but we have no visibility into or control over transitions, recovery, or multi-turn state.

### What To Build
- **Explicit `AgentLoopState` record** with named transitions replacing MAF's opaque inner loop
- **Take ownership of the tool-call loop** — call the LLM ourselves, dispatch tools ourselves, decide continuation ourselves. MAF's `FunctionInvokingChatClient` is a convenience wrapper that hides the loop; we need the loop exposed.
- **Named recovery transitions**: `CompactionRetry`, `TokenEscalation`, `StopHookBlocking`, `BudgetExhausted`
- **Turn counter** with configurable `maxTurns` limit
- **Diminishing returns detection**: track output tokens per turn; if < 500 for 3+ consecutive turns, inject a nudge or stop

---

## 2. STREAMING TOOL EXECUTION (Execute During LLM Stream)

### What Claude Code Does
`StreamingToolExecutor.ts` (lines 40-362) starts executing tools **as their parameters arrive during the LLM stream**, not after the full response:

- Tools declare `isConcurrencySafe()` — safe tools run in parallel during streaming
- Non-concurrent tools queue until the stream completes
- On stream fallback/discard, in-flight tools get synthetic error results
- Sibling error cancellation: if a Bash tool errors, parallel siblings abort

### What We Have
We wait for the full LLM response, then MAF's `FunctionInvokingChatClient` executes tools sequentially.

### What To Build
- **`StreamingToolExecutor` class** that accepts tool_use blocks as they arrive from the streaming response
- **Concurrency classification** on tools: `[ConcurrencySafe]` attribute on read-only tools (ReadMemory, Disassemble, ListProcesses, HexDump, ListMemoryRegions, etc.)
- **Batched parallel execution**: consecutive safe tools run via `Task.WhenAll`, non-safe tools run serially between batches
- **Configurable max concurrency** (default 10, env var override)
- **Sibling abort**: if a tool errors, abort in-flight siblings in the same batch
- **Synthetic error injection**: if stream falls back or user cancels, generate `tool_result` blocks for orphaned tool_use blocks

---

## 3. ERROR RECOVERY & RESILIENCE

### What Claude Code Does (Complete Catalog)

| Error | Detection | Recovery |
|-------|-----------|----------|
| **Prompt too long (413)** | `isPromptTooLongMessage()` | Stage 1: context-collapse drain. Stage 2: reactive compact (LLM summarization). Stage 3: surface error. |
| **Max output tokens** | `isWithheldMaxOutputTokens()` | Escalate 8k→64k. Then inject "resume mid-thought" message. Max 3 recovery attempts. |
| **429 Rate limit** | Status 429 | Exponential backoff 500ms→32s with 25% jitter. Parse `retry-after` header. |
| **529 Overloaded** | `is529Error()` | Count consecutive 529s. After 3: trigger model fallback. Background queries bail immediately (no retry amplification). |
| **Streaming idle** | 90s watchdog timer | Abort stream, fall back to non-streaming request. |
| **Streaming stall** | 30s between events | Log stall, continue waiting (informational). |
| **Incomplete stream** | No message_start or partial content | Fall back to non-streaming request. |
| **401/403 Auth** | Status codes | Refresh OAuth token, clear auth cache, retry. |
| **ECONNRESET/EPIPE** | Connection errors | Disable HTTP keep-alive, get fresh client, retry. |
| **Max tokens overflow** | 400 "input + max_tokens > context" | Parse error, calculate available context, adjust max_tokens down, retry. |
| **Model fallback** | `FallbackTriggeredError` | Switch to fallback model, rebuild tool context, strip thinking signatures, continue. |
| **Tool execution error** | `is_error` in result | Pass error back to LLM as tool_result. Bash errors cascade to siblings. |
| **User abort (ESC)** | `abortController.signal` | Consume remaining tool results as synthetic errors. Clean up MCP locks. Return `aborted_streaming` or `aborted_tools`. |

**Retry configuration**: 10 max retries, persistent mode with 5-min max backoff and 6-hour reset cap. Long sleeps chunked every 30s with heartbeat messages to keep session alive.

### What We Have
- Basic error handling in the streaming loop
- No retry logic (we rely on the SDK's default)
- No streaming fallback
- No rate limit backoff
- No model fallback
- No prompt-too-long recovery
- No max-output-tokens escalation

### What To Build
- **`RetryPolicy` class** with exponential backoff, jitter, retry-after header parsing
- **Max 10 retries** with configurable limit
- **429/529 handling**: backoff with header-aware delays
- **Consecutive 529 tracking → model fallback**: after 3 consecutive 529s on primary model, fall back to a secondary model
- **Background query bailout**: don't retry-amplify during capacity cascades
- **Streaming watchdog**: 90s idle timeout → abort and retry non-streaming
- **Streaming-to-non-streaming fallback**: on any streaming error, retry as a single non-streaming request
- **Prompt-too-long recovery**: detect 413/context overflow → trigger compaction → retry
- **Max output tokens escalation**: on first hit, retry with doubled max_tokens. On second hit, inject "resume mid-thought" message.
- **Auth refresh on 401/403**: clear cached token, get fresh client, retry
- **Connection error recovery**: on ECONNRESET/EPIPE, disable keep-alive, get fresh HttpClient, retry
- **Max tokens overflow parsing**: parse "X + Y > Z" from 400 error, calculate available context, adjust max_tokens, retry
- **Heartbeat during long waits**: emit status messages every 30s during retry sleeps so the UI doesn't appear frozen

---

## 4. POST-COMPACTION CONTEXT RESTORATION

### What Claude Code Does
After compacting (summarizing) conversation history (`compact.ts`):
- Restores up to **5 most recently edited files** (5,000 tokens each, 50,000 total budget)
- Restores **active skills** (5,000 tokens each, 25,000 total budget)
- Restores **delta attachments** (agent listings, MCP instructions, deferred tools)
- Strips images before summarization to reduce noise

### What We Have
Our 4-stage compaction pipeline works but doesn't restore anything afterward. After summarization, the agent loses track of what it was working on.

### What To Build
- **Post-compaction restoration step**: after any compaction, re-inject:
  - Last N tool results (the most recent memory reads, disassembly outputs, scan results)
  - Current address table state (compact summary)
  - Current process/module context
  - Active tool categories
- **Per-content-type token budgets**: tool results get 25K, context state gets 10K, etc.
- **Image stripping before summarization**: remove screenshot content blocks before sending to summarizer

---

## 5. NATIVE API CONTEXT MANAGEMENT

### What Claude Code Does
Sends `context_management` config to the Anthropic API:
- `clear_thinking_20251015` — manages thinking block retention
- `clear_tool_uses_20250919` — auto-clears old tool results at 180K input tokens, keeps last 40K

This is **free, server-side compaction** that reduces our token costs without any LLM call.

### What We Have
Nothing — we don't use Anthropic's native context management.

### What To Build
- When using the Anthropic provider, send `context_management` strategies in the API request
- Configure trigger threshold (180K default) and retention target (40K) via `TokenLimits`
- This complements our application-level compaction — the API handles the cheap stuff, we handle the smart stuff

---

## 6. TOOL SYSTEM OVERHAUL

### What Claude Code Does
Each tool is a rich object (`Tool.ts` lines 362-599) with:

| Property | Purpose |
|----------|---------|
| `isConcurrencySafe()` | Can run in parallel with other safe tools |
| `isReadOnly()` | Doesn't modify state |
| `isDestructive()` | Requires extra confirmation |
| `maxResultSizeChars` | Per-tool output cap with auto-spill |
| `checkPermissions()` | Tool-specific permission logic |
| `validateInput()` | Input validation before execution |
| `searchHint` | Keywords for deferred tool discovery |
| `interruptBehavior` | `'cancel'` (discard on ESC) vs `'block'` (queue) |
| `toAutoClassifierInput()` | Compact repr for security classifier |
| `mapToolResultToToolResultBlockParam()` | Custom result formatting |
| `renderToolResultMessage()` | Custom UI rendering |

Tool orchestration (`toolOrchestration.ts`) partitions tool calls into batches based on concurrency safety.

### What We Have
Tools are flat `[Description]` methods returning `Task<string>`. Global `MaxToolResultChars` cap. `ApprovalRequiredAIFunction` wrapper for dangerous tools.

### What To Build
- **`ToolAttribute` hierarchy**:
  ```csharp
  [ConcurrencySafe]    // ReadMemory, Disassemble, ListProcesses, etc.
  [ReadOnly]           // All query/list tools
  [Destructive]        // WriteMemory, SetBreakpoint, etc.
  [MaxResultSize(8000)] // Per-tool output caps
  [SearchHint("memory hex dump bytes")]  // For deferred discovery
  [InterruptBehavior(Cancel)]  // vs Block
  ```
- **Per-tool result size limits** replacing the global `MaxToolResultChars`
  - Disassembly tools: 10,000 chars (they produce structured, dense output)
  - Memory region lists: 3,000 chars
  - Hex dumps: 5,000 chars
  - Scan results: 8,000 chars
- **Input validation layer**: validate before execution (address ranges, scan types, etc.)
- **Tool-specific permission checks**: e.g., WriteMemory could check if the target address is in a known code section and warn
- **Custom result formatters**: tool-specific output formatting for the LLM (compact JSON for data, markdown tables for lists)
- **Batched parallel execution** using the concurrency attributes

---

## 7. DEFERRED TOOL LOADING (ToolSearch)

### What Claude Code Does
With 80+ MCP tools, sending all tool schemas in every API call wastes context. Solution:

- Tools marked `shouldDefer: true` or all MCP tools → sent with `defer_loading: true` in the API
- The **ToolSearch tool** lets the agent discover and load tools on demand
- Three modes: `tst` (always defer), `tst-auto` (defer when tools exceed 10% of context), `standard` (all eager)
- Scoring algorithm: exact name match (12pts) > partial match (6pts) > searchHint match (4pts) > description match (2pts)

### What We Have
Progressive tool loading via categories (`request_tools`, `list_tool_categories`, `unload_tools`). This is actually a strong foundation — we were ahead of the curve here.

### What To Build
- **Adopt `defer_loading` API parameter** when using Anthropic (reduces tool schema tokens)
- **Enhance `request_tools` with keyword search**: instead of only loading by category, allow `request_tools("memory hex dump")` to find tools by keyword across categories
- **Auto-unload idle categories**: if a category hasn't been used for N turns, auto-unload to free context
- **Tool search scoring**: when the agent asks for tools by keyword, rank by name match > description match > category match

---

## 8. HOOK SYSTEM (User-Extensible Lifecycle Events)

### What Claude Code Does
22 hook event types with 4 executable hook formats:

**Hook Events**: PreToolUse, PostToolUse, PostToolUseFailure, PermissionRequest, PermissionDenied, Stop, StopFailure, SessionStart, SessionEnd, SubagentStart, SubagentStop, Notification, UserPromptSubmit, Setup, PreCompact, PostCompact, TeammateIdle, TaskCreated, TaskCompleted, Elicitation, ElicitationResult, InstructionsLoaded, CwdChanged, FileChanged, ConfigChange, WorktreeCreate, WorktreeRemove

**Hook Formats**:
1. **Command** — shell command with optional `if` condition, timeout, async support
2. **Prompt** — LLM prompt evaluated with model override
3. **Agent** — agentic verifier hook (sub-agent)
4. **HTTP** — POST to URL with custom headers

**Key capabilities**:
- Hooks can **modify tool input** (PreToolUse `updatedInput`)
- Hooks can **modify tool output** (PostToolUse `updatedMCPToolOutput`)
- Hooks can **block continuation** (`preventContinuation`)
- Hooks can **provide permission decisions** (`allow`/`deny`/`ask`)
- Hooks can **inject additional context** (`additionalContext`)
- Async hooks run in background; `asyncRewake` hooks wake the agent on exit code 2
- `if` conditions use permission rule syntax for pattern matching (e.g., `"Bash(git *)"`)
- Trust model: ALL hooks require workspace trust acceptance in interactive mode

**Hook sources (priority)**: userSettings > projectSettings > localSettings > policySettings > pluginHook > sessionHook > builtinHook

### What We Have
No hook system. Dangerous tool approval is the only lifecycle interception point.

### What To Build
- **`IAgentHook` interface**:
  ```csharp
  public interface IAgentHook
  {
      string Event { get; }        // "PreToolUse", "PostToolUse", "Stop", etc.
      string? Matcher { get; }     // Tool name pattern
      string? Condition { get; }   // "WriteMemory(0x*)" pattern match
      Task<HookResult> ExecuteAsync(HookInput input, CancellationToken ct);
  }
  ```
- **Core hook events to implement first**:
  - `PreToolUse` — validate/modify tool input, provide permission decision
  - `PostToolUse` — validate/transform tool output, inject context
  - `Stop` — custom logic to decide if the agent should continue or stop
  - `SessionStart` — initialize context, load state
- **Hook configuration in settings.json**:
  ```json
  {
    "hooks": {
      "PreToolUse": [
        { "matcher": "WriteMemory", "type": "command", "command": "validate-write.ps1" }
      ],
      "Stop": [
        { "type": "prompt", "prompt": "Has the user's goal been fully achieved?" }
      ]
    }
  }
  ```
- **Hook result processing**: allow input modification, output modification, permission decisions, continuation blocking
- **Trust model**: hooks from project settings require user trust acceptance on first run

---

## 9. SUBAGENT ARCHITECTURE

### What Claude Code Does
The `AgentTool` spawns child agents with:

- **Built-in types**: Explore (read-only, fast search), Plan (read-only, architectural), General-purpose (full tools), Verification
- **Fork subagent**: parent conversation split — children inherit context with boilerplate rules (no sub-agents, max 500-word report, direct tool use)
- **Worktree isolation**: each agent gets an isolated git working copy
- **Permission bubbling**: fork children use `permissionMode: 'bubble'` to surface approval prompts to parent
- **Tool filtering per agent type**: Explore can't write, Plan can't execute
- **Async lifecycle**: background agents with progress tracking, completion notifications, abort handling
- **Agent definitions**: AGENT.md files with frontmatter (tools, model, effort, maxTurns, isolation, hooks, MCP servers)

### What We Have
Single conversation thread. No subagent concept.

### What To Build
- **`SubAgent` class** that wraps a separate `IChatClient` conversation with:
  - Restricted tool set (configurable per agent type)
  - Own system prompt
  - Own compaction state
  - Progress reporting back to parent
  - Configurable max turns
- **Built-in agent types**:
  - **Explore**: read-only tools only (ReadMemory, Disassemble, ListProcesses, HexDump, etc.). Fast investigation without side effects.
  - **Plan**: no tools, just conversation. For architectural planning and strategy.
  - **Verify**: read-only tools. Verify that a write operation had the intended effect.
  - **Script**: full tools restricted to script generation/validation. For complex scripting tasks.
- **Background agents**: run in parallel with main conversation, report results on completion
- **Agent definitions in settings**: let users define custom agent types with tool restrictions
- **Progress streaming**: subagent progress events flow through the same `AgentStreamEvent` channel
- **Parent-child permission bubbling**: subagent approval requests surface to the main UI

---

## 10. SKILLS / SLASH COMMANDS

### What Claude Code Does
- **SKILL.md files** with YAML frontmatter: description, when_to_use, arguments, allowed-tools, model, effort, context (fork/inline), paths (auto-activate on file match)
- **Directory-based discovery**: `~/.claude/skills/`, `.claude/skills/`
- **Inline vs. forked execution**: inline skills expand into the conversation, forked skills run as sub-agents
- **Argument substitution**: `{arg-name}` syntax in markdown
- **Shell commands in skills**: `!bash` inline execution
- **Dynamic skill discovery**: skills found during file operations auto-register
- **Plugin-provided skills**: plugins register bundled skills
- **Skill tool**: `SkillTool` that loads and executes skills by name

### What We Have
MAF `FileAgentSkillsProvider` auto-discovers skills from a `skills/` directory. We have some skill files. But no frontmatter system, no argument substitution, no forked execution, no dynamic discovery.

### What To Build
- **Enhanced skill frontmatter** in our skill .md files:
  ```yaml
  ---
  description: "Analyze a function's control flow"
  when_to_use: "When user asks about function behavior or control flow"
  arguments: ["address"]
  allowed-tools: ["Disassemble", "FindFunctionBoundaries", "GetCallerGraph"]
  context: fork
  ---
  ```
- **Skill argument substitution**: `{address}` → actual value from user input
- **Forked skill execution**: run skill as a subagent with restricted tool set
- **`/skill` slash command**: invoke skills by name from chat input
- **Skill auto-activation**: when the user's message matches a skill's `when_to_use`, suggest or auto-invoke it
- **User-defined skills**: users can drop SKILL.md files into a `.ceai/skills/` directory

---

## 11. MEMORY SYSTEM (Persistent Cross-Session Knowledge)

### What Claude Code Does
- **MEMORY.md index** (max 200 lines, 25KB) with links to individual memory files
- **Memory types**: user, feedback, project, reference — each with specific save/use guidance
- **Memory frontmatter**: name, description, type, scope
- **Auto-memory**: agent proactively saves memories when it learns about the user
- **Memory in system prompt**: MEMORY.md loaded into every conversation
- **Memory tools**: agent can read/write/update memory files
- **Project-scoped memory**: `~/.claude/projects/{slug}/memory/`

### What We Have
Chat persistence via `AiChatStore`. Session-level context. No cross-session persistent memory.

### What To Build
- **Memory directory**: `%LOCALAPPDATA%/CEAISuite/memory/`
  - `MEMORY.md` — index file loaded into every conversation's system prompt
  - Individual memory files with frontmatter
- **Memory types for our domain**:
  - **user**: user's experience level, preferred analysis style, common targets
  - **feedback**: corrections on approach ("don't hook that address, it crashes")
  - **project**: per-game/per-target investigation notes, known structures, discovered patterns
  - **reference**: external resources, documentation links, community tools
- **Auto-memory in system prompt**: instruct the agent to save memories when it learns something persistent
- **Memory tools**: `SaveMemory(type, name, content)`, `UpdateMemory(file, content)`, `ForgetMemory(file)`
- **Per-target memory scoping**: different memory sets for different games/processes

---

## 12. PLAN MODE

### What Claude Code Does
- `EnterPlanMode` tool transitions the agent to a restricted mode
- Plan written to `.claude/plans/plan.md`
- In plan mode: only read-only tools allowed, no writes
- User reviews plan, provides feedback
- `ExitPlanMode` returns to normal mode with the plan as context
- Plans persist across compaction

### What We Have
No plan mode. The agent acts immediately.

### What To Build
- **`EnterPlanMode` tool**: switches the agent to plan mode with restricted tools
- **Plan file**: stored in `%LOCALAPPDATA%/CEAISuite/plans/{session-id}.md`
- **Plan mode permission context**: only allow read-only tools (Disassemble, ReadMemory, ListProcesses, etc.)
- **Plan display in UI**: show the plan in a side panel, let the user edit/approve
- **`ExitPlanMode` tool**: return to normal mode, inject the approved plan as context
- **Plan-driven execution**: after approval, the agent follows the plan step by step

---

## 13. PROMPT CACHING OPTIMIZATION

### What Claude Code Does
System prompt split into cacheable blocks with `cache_control`:
- **Global scope**: static system prompt cached across all users
- **Org scope**: user-specific context cached per-organization
- **Null scope**: dynamic content (no caching)

Prompt section memoization: expensive computations (git status, CLAUDE.md discovery) cached for conversation duration.

### What We Have
We set `cache_control: ephemeral` on system messages. Basic but functional.

### What To Build
- **Split system prompt into blocks** with different cache scopes:
  - Block 1: Core system prompt (static, cacheable globally)
  - Block 2: Tool category instructions (cacheable per-session)
  - Block 3: Dynamic context — process state, address table (no cache)
- **Section memoization**: cache expensive context computations (process module list, address table summary) and only recompute when invalidated
- **Cache hit tracking**: log cache hit rates to understand cost savings

---

## 14. SESSION MANAGEMENT

### What Claude Code Does
- **Session history API**: paginated event fetching with cursor-based pagination
- **Session resume**: restore conversation state from server
- **Session export**: full transcript with metadata
- **Worktree state persistence**: save/restore worktree session on resume
- **Session ID tracking**: unique per conversation, preserved across compaction

### What We Have
`AiChatStore` persists chats to JSON. `ReplayHistoryInto` restores from file. Basic but functional.

### What To Build
- **Session metadata enrichment**: track per-session:
  - Target process name and version
  - Discovered structures and offsets
  - Investigation timeline (what was tried, what worked)
  - Total token usage and cost
- **Session search**: search across all saved sessions by content
- **Session branching**: fork a session to try a different approach without losing the original
- **Session templates**: save a session as a template for similar investigations
- **Auto-save on compaction**: persist session state whenever compaction triggers

---

## 15. SCHEDULED TASKS / CRON

### What Claude Code Does
- `.claude/scheduled_tasks.json` with cron expressions
- Scheduler polls every 1s with file watcher for changes
- Lock file for multi-session exclusion
- One-shot tasks (`fireAt` timestamp) and recurring tasks
- Jitter to prevent synchronized load spikes
- Missed task detection on startup
- Auto-expiry for recurring tasks (7 days)

### What We Have
Nothing.

### What To Build
- **Scheduled task system** for automated monitoring:
  - "Check if the game updated and my offsets still work" — daily cron
  - "Monitor this memory address and alert me if it changes" — every N seconds
  - "Re-run my signature scan after game update" — on-demand trigger
- **Task persistence**: `%LOCALAPPDATA%/CEAISuite/scheduled_tasks.json`
- **Task UI**: list/create/delete scheduled tasks from the UI
- **Notification system**: alert the user when a scheduled task completes or finds something

---

## 16. MCP (MODEL CONTEXT PROTOCOL) INTEGRATION

### What Claude Code Does
- Hierarchical MCP server configuration: enterprise > project > user > plugin > connector
- 8 transport types: stdio, SSE, HTTP, WebSocket, SDK, IDE variants, proxy
- Automatic reconnection with exponential backoff (max 5 attempts, 1s→30s)
- OAuth/auth cache with 15-minute TTL
- MCP tool annotations: `readOnlyHint`, `destructiveHint`, `openWorldHint` → tool behavior flags
- MCP resources and prompts
- Per-server scoping and deduplication

### What We Have
No MCP support.

### What To Build
- **MCP client implementation**: connect to MCP servers via stdio and SSE transports
- **MCP configuration**: `.ceai/mcp.json` for project-level server configs
- **MCP tool registration**: discover tools from connected servers, register alongside native tools
- **MCP tool annotations**: respect `readOnlyHint`, `destructiveHint` for permission classification
- **Connection lifecycle**: auto-connect on startup, reconnect on failure, auth flow support
- **Use cases**: connect to game-specific MCP servers that provide domain knowledge, pattern databases, or community tools

---

## 17. PERMISSION SYSTEM OVERHAUL

### What Claude Code Does
Multi-layered permission system:

1. **Rule-based**: allow/deny/ask rules with glob patterns (e.g., `"WriteMemory(0x7FF*)"`)
2. **Classifier**: AI-based security classifier for Bash commands
3. **Hook-based**: user hooks intercept and decide permissions
4. **Denial tracking**: after N consecutive denials, escalate to user prompt
5. **Permission modes**: default, plan, acceptEdits, bypassPermissions, dontAsk, auto (classifier), bubble (fork children)
6. **Permission sources**: session > localSettings > userSettings > policySettings > hook > classifier

### What We Have
`ApprovalRequiredAIFunction` wrapper on 6 dangerous tools. Session-level `_sessionTrustedTools` set.

### What To Build
- **Pattern-based permission rules**:
  ```json
  {
    "permissions": {
      "allow": ["ReadMemory(*)", "Disassemble(*)"],
      "ask": ["WriteMemory(*)"],
      "deny": ["WriteMemory(0x0-0xFFFF)"]  // Deny writes to low addresses
    }
  }
  ```
- **Permission modes**: `Default` (ask for dangerous), `ReadOnly` (deny all writes), `Unrestricted` (allow all), `PlanOnly` (read-only + plan)
- **Denial tracking**: if the agent keeps requesting a denied tool, inject a message explaining why it's denied instead of silently failing
- **Address-range permissions**: for memory tools, allow/deny based on address ranges
- **Module-based permissions**: allow writes only to specific modules (e.g., game.exe but not kernel32.dll)
- **Permission persistence**: save permission decisions to settings for future sessions

---

## 18. ABORT / CANCELLATION FLOW

### What Claude Code Does
- User presses ESC → `abortController.signal` with reason `'interrupt'`
- Streaming loop detects abort, consumes remaining tool results as synthetic errors
- Tools check `interruptBehavior`: `'cancel'` (discard immediately) vs `'block'` (finish gracefully)
- MCP lock cleanup on abort
- Distinct return reasons: `aborted_streaming` (during LLM) vs `aborted_tools` (during execution)

### What We Have
Basic `CancellationToken` support but no graceful abort with synthetic results.

### What To Build
- **Graceful abort**: on user cancel, generate synthetic `tool_result` error blocks for in-flight tools
- **Interrupt behavior per tool**: ReadMemory can cancel immediately, but WriteMemory should finish (or rollback via UndoWrite)
- **Abort reason tracking**: distinguish "user cancelled during streaming" from "user cancelled during tool execution"
- **Cleanup on abort**: release any locks, close breakpoint sessions, etc.

---

## 19. STRUCTURED STREAMING BLOCKS

### What Claude Code Does
Yields multiple message types during streaming:
- **System init** — tools/models/commands available
- **Assistant text** — streamed character by character
- **Progress/Attachment** — tool execution progress, file edits, memory discoveries
- **Tombstone** — signal to remove/replace prior messages
- **Tool use summary** — async Haiku summary of tool calls (non-blocking)
- **Result** — success/error with duration, cost, usage

### What We Have
`AgentStreamEvent` with TextDelta, ToolCallStarted, ToolCallCompleted, ApprovalRequested, Completed, Error. Good foundation.

### What To Build
- **ToolProgress event**: emit progress during long-running tools (scan progress %, disassembly instruction count, etc.)
- **Tombstone/Replace event**: allow the agent to replace a previous message (e.g., update scan results as more are found)
- **Tool use summary event**: async summary of what tools did (for the user's benefit, not the LLM's)
- **Attachment events**: structured data attachments (hex dump, disassembly listing, memory map) that render as rich UI components

---

## 20. DYNAMIC CONTEXT INJECTION

### What Claude Code Does
- **System context** (appended to system prompt): git status, cache breaker
- **User context** (prepended as first user message in `<system-reminder>` tags): CLAUDE.md content, current date, memory index
- Context is fetched once and cached per conversation, invalidated on `/clear` or `/compact`

### What We Have
`BuildContextSuffix()` appends process state, address table, scan state. Dynamic context via `IAIContextProvider`. Deduplication to avoid redundant tokens.

### What To Build
- **Split context into cached vs. volatile**:
  - **Cached** (compute once): module list, known structures, investigation history
  - **Volatile** (every turn): current scan state, breakpoint status, recent memory changes
- **Context section memoization**: don't recompute module list every turn — cache it and invalidate only when process reattaches
- **System reminder injection**: prepend volatile context as `<system-reminder>` in user messages rather than system prompt (better for prompt caching)

---

## 21. PLUGIN / EXTENSION SYSTEM

### What Claude Code Does
- Plugin definition: name, description, version, skills, hooks, MCP servers
- Plugin sources: built-in, marketplace, user-defined
- Plugin-provided skills become available as slash commands
- Plugin hooks execute alongside user hooks
- Plugin MCP servers auto-connect
- Plugin availability gating

### What We Have
No plugin system. All functionality is compiled in.

### What To Build
- **Plugin interface**:
  ```csharp
  public interface ICeaiPlugin
  {
      string Name { get; }
      string Description { get; }
      IEnumerable<ToolDefinition> GetTools();
      IEnumerable<IAgentHook> GetHooks();
      IEnumerable<SkillDefinition> GetSkills();
      Task InitializeAsync(IServiceProvider services);
  }
  ```
- **Plugin discovery**: scan `%LOCALAPPDATA%/CEAISuite/plugins/` for plugin assemblies
- **Plugin isolation**: load plugins in separate AssemblyLoadContext for isolation
- **Built-in plugins**: convert existing tool categories into plugin format for consistency
- **Community plugins**: game-specific plugins (Unity helper, Unreal helper, etc.)

---

## 22. FAST MODE / MODEL SWITCHING

### What Claude Code Does
- `/fast` toggles between standard and fast output mode (same model, faster generation)
- Fast mode 429/529: short retry-after → sleep and retry with fast mode. Long retry-after → cooldown, switch to standard speed.
- Overage disabled → permanently disable fast mode
- Model selection per-agent, per-skill, with fallback chain

### What We Have
Model selection in settings. No fast mode toggle. No dynamic model switching.

### What To Build
- **Runtime model switching**: `/model` command to switch models mid-conversation
- **Per-tool model override**: use a cheaper model for tool-use-heavy turns, expensive model for analysis
- **Fallback chain**: if primary model is overloaded, automatically fall back to secondary
- **Cost tracking with model attribution**: track costs per model used

---

## IMPLEMENTATION PRIORITY

### Phase 1 — Core Loop (Highest Impact)
1. Own the agent loop (replace MAF's opaque inner loop)
2. Streaming tool execution
3. Error recovery & retry policy
4. Post-compaction context restoration

### Phase 2 — Tool & Permission System
5. Tool attributes (concurrency, read-only, per-tool result limits)
6. Batched parallel tool execution
7. Pattern-based permission rules
8. Graceful abort with synthetic results

### Phase 3 — Extensibility
9. Hook system (PreToolUse, PostToolUse, Stop)
10. Enhanced skill system with frontmatter
11. Plugin interface
12. MCP client integration

### Phase 4 — Intelligence
13. Subagent architecture
14. Plan mode
15. Persistent memory system
16. Scheduled tasks

### Phase 5 — Optimization
17. Prompt caching optimization
18. Native API context management
19. Deferred tool loading enhancement
20. Dynamic context memoization
21. Fast mode / model switching
22. Session management enrichment

---

## FILE REFERENCE (Claude Code Source)

| Component | File | Key Lines |
|-----------|------|-----------|
| Agent loop | `query.ts` | 241-1729 |
| Query engine | `QueryEngine.ts` | 184-1162 |
| Tool interface | `Tool.ts` | 362-599 |
| Tool orchestration | `services/tools/toolOrchestration.ts` | 19-116 |
| Streaming executor | `services/tools/StreamingToolExecutor.ts` | 40-362 |
| Tool execution | `services/tools/toolExecution.ts` | 337-490 |
| Retry logic | `services/api/withRetry.ts` | 52-548 |
| API client | `services/api/claude.ts` | 752-2654 |
| System prompt | `utils/systemPrompt.ts` | 28-123 |
| Context assembly | `context.ts` | 116-189 |
| Prompt sections | `constants/systemPromptSections.ts` | 1-68 |
| API formatting | `utils/api.ts` | 119-474 |
| Compaction | `services/compact/compact.ts` | 1-150 |
| API compaction | `services/compact/apiMicrocompact.ts` | 64-153 |
| Token budget | `query/tokenBudget.ts` | all |
| Hook schemas | `schemas/hooks.ts` | all |
| Hook execution | `utils/hooks.ts` | 184-4896 |
| Tool hooks | `services/tools/toolHooks.ts` | 39-650 |
| Permission context | `hooks/toolPermission/PermissionContext.ts` | 96-389 |
| Permission types | `types/permissions.ts` | all |
| Permission mode | `utils/permissions/PermissionMode.ts` | 42-142 |
| Agent tool | `tools/AgentTool/AgentTool.tsx` | 239-450 |
| Agent runner | `tools/AgentTool/runAgent.ts` | all |
| Fork subagent | `tools/AgentTool/forkSubagent.ts` | 1-211 |
| Agent definitions | `tools/AgentTool/loadAgentsDir.ts` | all |
| Skills loader | `skills/loadSkillsDir.ts` | all (1086 lines) |
| Skill tool | `tools/SkillTool/SkillTool.ts` | all |
| Tool search | `tools/ToolSearchTool/ToolSearchTool.ts` | 132-471 |
| MCP client | `services/mcp/client.ts` | 1-2020 |
| MCP config | `services/mcp/config.ts` | 62-310 |
| MCP types | `services/mcp/types.ts` | 10-226 |
| Memory system | `memdir/memdir.ts` | all |
| Cron scheduler | `utils/cronScheduler.ts` | all |
| Worktree utils | `utils/worktree.ts` | all |
| Commands registry | `commands.ts` | all (755 lines) |
| Sandbox adapter | `utils/sandbox/sandbox-adapter.ts` | all |
| Error classification | `services/api/errors.ts` | all |
