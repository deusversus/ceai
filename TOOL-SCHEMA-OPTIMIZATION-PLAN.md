# Tool Schema Optimization Plan

**Problem:** The AI agent burns excessive tokens on tool schemas. A simple "hi" message generates 20+ API round-trips at 10-20K input tokens each because every turn re-sends all tool schemas without prompt caching.

**Benchmark:** Claude Code sends ~9-12 non-deferred tool schemas (~3,000 tokens) with API-level `defer_loading` and prompt caching. Our agent sends 39 core tool schemas (~3,900 tokens) with no caching and no deferral — every tool schema is fully serialized every turn.

**Goal:** Reduce per-turn tool schema overhead by ~50%, eliminate unnecessary tool-call loops on trivial messages, and prepare the architecture for prompt caching when providers support it.

---

## 1. Shrink Core Tool Set (39 → ~18 tools)

**Impact:** ~2,000 fewer tokens per turn, every turn, for every model  
**Complexity:** Low — move tool names between sets, no new code  
**Files:** `AiOperatorService.cs` lines 113-141

### Current core (39 tools):
```
Process:    ListProcesses, FindProcess, AttachProcess, InspectProcess, CheckProcessLiveness (5)
Memory:     ReadMemory, WriteMemory, ProbeAddress, BrowseMemory (4)
Scanning:   StartScan, RefineScan, GetScanResults, ResetScan (4)
AddrTable:  ListAddressTable, AddToAddressTable, RemoveFromAddressTable, RefreshAddressTable, FreezeAddress (5)
Context:    GetCurrentContext (1)
Results:    RetrieveToolResult, ListStoredResults (2)
Meta:       request_tools, list_tool_categories, unload_tools (3)
Skills:     load_skill, list_skills, unload_skill, confirm_load_skill, view_skill_reference (5)
Memory AI:  remember, recall_memory, forget_memory (3)
Budget:     get_budget_status (1)
Subagent:   spawn_subagent, plan_task, execute_plan (3)
Phase 5:    switch_model, schedule_task, list_tasks, cancel_task, get_session_info, search_sessions (6)  [Note: 6, not 5 — recount]
```

### Proposed core (~18 tools):
```
Process:    ListProcesses, AttachProcess, InspectProcess (3)  — drop FindProcess (rarely needed unprompted), CheckProcessLiveness (niche)
Memory:     ReadMemory, WriteMemory, ProbeAddress (3)         — drop BrowseMemory (advanced, rarely first-turn)
Scanning:   StartScan, RefineScan, GetScanResults (3)         — drop ResetScan (infrequent)
AddrTable:  ListAddressTable, AddToAddressTable, FreezeAddress (3) — drop Remove/Refresh (second-order ops)
Context:    GetCurrentContext (1)
Results:    RetrieveToolResult (1)                             — drop ListStoredResults (rarely called)
Meta:       request_tools (1)                                 — drop list_tool_categories, unload_tools (see §2)
```

### Newly deferred (moved to categories):
```
"process_management": FindProcess, CheckProcessLiveness
"memory_browsing":    BrowseMemory                               — merge into memory_advanced
"scanning_util":      ResetScan                                  — merge into scanning_advanced
"address_table":      RemoveFromAddressTable, RefreshAddressTable — already a category, add these
"meta":               list_tool_categories, unload_tools, ListStoredResults, get_budget_status
"skills":             load_skill, list_skills, unload_skill, confirm_load_skill, view_skill_reference
"memory_ai":          remember, recall_memory, forget_memory
"planning":           spawn_subagent, plan_task, execute_plan
"session_management": switch_model, schedule_task, list_tasks, cancel_task, get_session_info, search_sessions
```

### Why each demotion is safe:
- **Skills/memory_ai/planning/session_management**: The model rarely needs these on turn 1. When it does, the system prompt (§2) tells it to call `request_tools`.
- **list_tool_categories**: Replaced by a static list in the system prompt (§2), eliminating a wasted API turn.
- **unload_tools**: Only useful in long sessions; can be loaded with the "meta" category.
- **get_budget_status**: Informational only; almost never called proactively.
- **BrowseMemory/ResetScan**: Second-order operations that follow initial scan/read workflows.

---

## 2. Inject Category Index into System Prompt

**Impact:** Eliminates the `list_tool_categories` round-trip; model knows what to request immediately  
**Complexity:** Low — append a dynamic string to the system prompt  
**Files:** `AiOperatorService.cs` — `BuildChatOptions()` (~line 604) or system prompt constant

### Current system prompt section:
```
═══ TOOLS (PROGRESSIVE) ═══
Core tools are always loaded. For specialized ops, call request_tools(category).
Use list_tool_categories to see what's available and loaded.
Key categories: sessions (save/load/search), scripts, breakpoints, disassembly, hooks.
```

### Proposed replacement:
```
═══ TOOLS (PROGRESSIVE) ═══
Core tools (process, memory, scan, address table, request_tools) are always loaded.
For specialized ops, call request_tools("category_name").

Available categories:
  sessions (6), memory_advanced (7), address_table (8+), scanning_advanced (10),
  breakpoints (14), disassembly (14), hooks (5), scripts (13), snapshots (5),
  safety (5), signatures (2), hotkeys (3), undo (3), transactions (3),
  cheat_tables (2), vision (1), lua (4), utility (1),
  skills (5), memory_ai (3), planning (3), session_management (6), meta (4)

Do NOT call list_tool_categories — this list is authoritative.
```

### Dynamic generation:
Build this string at startup from `ToolCategories.Categories` so it stays in sync automatically. Append the loaded/unloaded state only if the model explicitly requests it (via the deferred `list_tool_categories` tool). This costs ~100 tokens in the system prompt but saves a full API round-trip (~12K+ tokens) every time the model would have called `list_tool_categories`.

---

## 3. Model-Aware Core Set Sizing

**Impact:** Weak models get fewer tools → fewer compulsive tool calls  
**Complexity:** Medium — add model capability tiers, conditional core set  
**Files:** `AiOperatorService.cs`, new `ModelCapabilities` class or enum

### Design:
Define three tiers based on model capability:

```csharp
enum ModelTier { Minimal, Standard, Full }

static ModelTier ClassifyModel(string modelId) => modelId switch
{
    _ when modelId.Contains("nemotron", OrdinalIgnoreCase) => ModelTier.Minimal,
    _ when modelId.Contains("llama", OrdinalIgnoreCase)    => ModelTier.Minimal,
    _ when modelId.Contains("gemma", OrdinalIgnoreCase)    => ModelTier.Minimal,
    _ when modelId.Contains("claude", OrdinalIgnoreCase)   => ModelTier.Full,
    _ when modelId.Contains("gpt-4", OrdinalIgnoreCase)    => ModelTier.Full,
    _ when modelId.Contains("o1", OrdinalIgnoreCase)       => ModelTier.Full,
    _                                                      => ModelTier.Standard,
};
```

| Tier | Core tools | Behavior |
|------|-----------|----------|
| Minimal (~10) | Process (3) + Memory (3) + Scan (3) + request_tools (1) | Bare minimum. No address table, no context, no results paging. |
| Standard (~18) | The proposed core from §1 | Default for most models. |
| Full (~25) | Standard + skills, planning, session_management | For Claude/GPT-4 that handle large tool sets well. |

### When model switches (via `switch_model`):
Recalculate tier and adjust `_tools` accordingly. This already mutates the live `_tools` list, so no new mechanism is needed.

---

## 4. Trivial Message Bypass

**Impact:** Eliminates tool-call loops on "hi", "thanks", "ok"  
**Complexity:** Low — add a check before `BuildChatOptions`  
**Files:** `AgentLoop.cs` — before the main `while` loop or in `BuildChatOptions`

### Design:
On the **first turn only** (state.TurnCount == 0), if the user message is short and non-technical, send the request with `Tools = []` (empty tool list). The model responds conversationally without any tool schemas in context.

```csharp
bool IsTrivialMessage(string message)
{
    if (message.Length > 80) return false;
    // Contains domain-specific keywords → not trivial
    var keywords = new[] { "scan", "memory", "address", "hack", "attach", "process",
                           "breakpoint", "script", "freeze", "find", "search", "read",
                           "write", "hook", "disassemble", "pointer", "0x" };
    var lower = message.ToLowerInvariant();
    return !keywords.Any(k => lower.Contains(k));
}
```

If trivial: set `chatOptions.Tools = []` for that turn. If the model's response then requests tools (shouldn't happen with empty tool list, but defensively), fall through to normal tool-equipped turns.

### Token savings on trivial messages:
- Before: ~3,900 tokens (39 tools) × 20 turns = ~78,000 input tokens for "hi"
- After: ~0 tool tokens × 1 turn = system prompt only (~500 tokens)

---

## 5. Per-Turn Tool Call Cap

**Impact:** Prevents runaway tool loops within a single LLM response  
**Complexity:** Low  
**Files:** `AgentLoop.cs` — tool execution section

### Current behavior:
The model can make unlimited tool calls in a single response. The loop cap (`MaxTurns = 25`) limits API round-trips, but a single round-trip can contain many tool calls.

### Proposed:
Add `MaxToolCallsPerTurn` to `AgentLoopOptions` (default: 8 for Standard/Full tier, 3 for Minimal tier).

If a single LLM response contains more tool calls than the cap, execute only the first N and inject a system message: `"[Tool call limit reached for this turn. Remaining calls deferred. Continue with what you have.]"`

This prevents weak models from issuing 15 tool calls in a single response (which they do — each call adds result tokens to the next turn's context).

---

## 6. Same-Turn Tool Loading (Future — Medium Complexity)

**Impact:** Eliminates the wasted round-trip when loading tools  
**Complexity:** Medium-High — requires re-invoking the LLM mid-turn  
**Files:** `AgentLoop.cs`, `ToolExecutor.cs`

### Current behavior:
1. Turn N: Model calls `request_tools("breakpoints")` → gets "13 tools loaded"
2. Turn N+1: Model can now use breakpoint tools (schemas appear in API request)

The model wastes an entire turn just loading tools.

### Proposed (two options):

**Option A — Forced continuation:** When `request_tools` is called, after executing all tool calls in the current batch, immediately start a new LLM turn (without waiting for user input) with the expanded tool list. The model sees its own `request_tools` result AND the new schemas in the same logical flow.

This is essentially what already happens — the agent loop continues if there are tool results. The key is that `_tools` is mutated in-place by `request_tools`, so the next `BuildChatOptions()` call already picks up the new schemas. **Verify this is actually working** — if `BuildChatOptions` is called once at the top of the turn and the tool list is captured by value (not reference), the new tools won't appear until the next turn.

**Option B — Schema injection:** When `request_tools` is called, append the loaded tool schemas as a structured system message in the tool result: `"Loaded 13 breakpoint tools. You can now use: SetBreakpoint, RemoveBreakpoint, ..."`. This doesn't give the model the actual schemas (it still needs the next turn for that), but it lets the model plan its next action without a wasted "what tools do I have now?" turn.

**Recommendation:** Verify Option A first — the live `_tools` reference should already enable same-turn loading if the loop's `BuildChatOptions()` is called fresh each iteration. If it is, this item is already done and just needs documentation.

---

## 7. Prompt Caching Preparation

**Impact:** Up to 90% reduction in cached token costs on supported providers  
**Complexity:** Medium — provider-specific API changes  
**Files:** `ChatClientFactory.cs`, `AgentLoop.cs`, `PromptCacheOptimizer.cs`

### Current state:
`PromptCacheOptimizer` exists and orders sections Static→Session→Volatile, but it only produces a flat string. It doesn't emit provider-specific cache markers.

### Anthropic API:
Already supports `cache_control: { type: 'ephemeral' }` on system prompt blocks and tool definitions. The `IChatClient` abstraction via Microsoft.Extensions.AI may not expose this — investigate whether the Anthropic SDK's native client can be used directly for cache-aware requests.

### OpenAI API:
Automatic prompt caching for messages with stable prefixes. No explicit markers needed — just ensure tool schemas are sent in a consistent order (already true since `_tools` is a stable list).

### OpenRouter:
Passes through to underlying provider's caching. Free models unlikely to support it, but paid models (Claude via OpenRouter, GPT-4 via OpenRouter) may benefit.

### Action items:
1. Audit `PromptCacheOptimizer.Build()` — does it output cache breakpoint markers? If not, extend it.
2. For Anthropic direct: switch from `IChatClient` to native Anthropic SDK for cache-aware calls, or extend the M.E.AI pipeline with a `CacheControlPolicy`.
3. For OpenAI/OpenRouter: ensure tool schema ordering is deterministic (it is — `_tools` is a `List<AITool>`).
4. Add a `PromptCacheHitRate` metric to `TokenBudget` for observability.

---

## 8. Schema Compression for Weak Models

**Impact:** Further reduce per-tool token cost for models that don't need rich descriptions  
**Complexity:** Low  
**Files:** `AiOperatorService.cs` — tool registration

### Design:
For `ModelTier.Minimal`, strip tool descriptions to bare minimum and remove optional parameter descriptions:

```csharp
// Normal: "Read a typed value from process memory."
// Minimal: "Read memory."

// Normal parameter: "Address: hex (0x...), decimal, or symbolic (module+offset)"
// Minimal parameter: "Address"
```

This requires wrapping `AIFunctionFactory.Create()` with a post-processing step that truncates `Description` fields based on model tier. Alternatively, maintain two description sets (full vs minimal) via a custom attribute:

```csharp
[Description("Read a typed value from process memory.")]
[MinimalDescription("Read memory.")]
public string ReadMemory(...)
```

### Estimated savings:
Current average: ~100 tokens/tool × 18 core tools = ~1,800 tokens  
Minimal descriptions: ~30 tokens/tool × 10 core tools = ~300 tokens  
**Savings: ~1,500 tokens/turn for weak models**

---

## Implementation Order

| Priority | Item | Tokens Saved/Turn | Effort |
|----------|------|-------------------|--------|
| **P0** | §4 Trivial message bypass | ~3,900 (entire tool set) on "hi" | 1 hour |
| **P0** | §5 Per-turn tool call cap | Prevents runaway loops | 1 hour |
| **P1** | §1 Shrink core to ~18 | ~2,000 every turn | 2 hours |
| **P1** | §2 Category index in system prompt | ~12,000 (eliminates round-trip) | 30 min |
| **P2** | §3 Model-aware core sizing | ~1,500 for weak models | 3 hours |
| **P2** | §8 Schema compression | ~1,500 for weak models | 2 hours |
| **P3** | §6 Same-turn tool loading | ~15,000 (eliminates round-trip) | 4 hours |
| **P3** | §7 Prompt caching prep | 90% of cached tokens | 6 hours |

### P0 items address the OpenRouter/Nemotron "hi" problem immediately.
### P1 items reduce baseline cost for all models and all conversations.
### P2 items specifically help weak/free models.
### P3 items are architectural improvements for long-term efficiency.

---

## Metrics to Track

After implementation, measure these on the OpenRouter dashboard:

1. **Input tokens per "hi" message** — target: <2,000 (from current 10-20K)
2. **API calls per "hi" message** — target: 1 (from current 20+)
3. **Average input tokens per turn in a real session** — target: <5,000 (from current ~14,000)
4. **Tool-call loops per turn** — target: <3 average (from current unbounded)
