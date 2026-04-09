# Throughput Optimization Plan — CE AI Suite

## Context

A throughput audit identified ~15 bottlenecks across the backend and UI layers, ranging from synchronous blocking calls that freeze the app, to missing UI virtualization on lists that can hold 10,000+ items. This plan addresses every gap found, ordered by severity and grouped into logical implementation phases.

---

## Phase 1: Critical Blocking Fixes (3 changes)

### 1A. Fix blocking `.Wait()` in disposal
**File:** `src/CEAISuite.Application/AiOperatorService.cs` line ~1790
- Change `Dispose()` to implement `IAsyncDisposable` alongside `IDisposable`
- In `Dispose()`, replace `Task.Run(...).Wait(5s)` with a non-blocking fire-and-forget pattern using a timeout guard
- Add `DisposeAsync()` that properly `await`s both `_mcpManager.DisposeAsync()` and `_pluginHost.DisposeAsync()`
- Update the DI registration / caller to prefer `DisposeAsync` where available

### 1B. Convert Lua engine sync gates to async
**File:** `src/CEAISuite.Engine.Lua/MoonSharpLuaEngine.cs` lines 91, 107, 146
- Convert `SetGlobal()` → `SetGlobalAsync(CancellationToken ct)` using `await _gate.WaitAsync(ct).ConfigureAwait(false)`
- Convert `GetGlobal()` → `GetGlobalAsync(CancellationToken ct)` using same pattern
- Convert `Reset()` → `ResetAsync(CancellationToken ct)` using same pattern
- Keep synchronous overloads that call `_gate.Wait()` with a `[Obsolete]` warning for backward compat if needed
- Update `IScriptEngine` interface (in Engine.Abstractions) to add async signatures
- Update all call sites (grep for `SetGlobal`, `GetGlobal`, `Reset` on engine instances)

### 1C. Static HttpClient in SseMcpTransport
**File:** `src/CEAISuite.Application/AgentLoop/McpClient.cs` line 223
- Replace per-instance `_httpClient = new HttpClient { Timeout = ... }` with a `private static readonly HttpClient`
- Set `Timeout` on the static instance; use per-request `CancellationTokenSource` with timeout for request-level control
- Ensure `Dispose` does NOT dispose the static client

---

## Phase 2: String & Collection Performance (4 changes)

### 2A. StringBuilder for streaming text accumulation
**Files:**
- `src/CEAISuite.Application/AiOperatorService.cs` line 736 (`assistantText += delta.Text`)
- `src/CEAISuite.Application/AiOperatorService.cs` line 800 (`assistantText += "\n" + delta.Text`)
- `src/CEAISuite.Desktop/ViewModels/AiOperatorViewModel.cs` line 238 (`currentTextBlock.Content += delta.Text`)
- Replace `string assistantText = ""` with `var assistantTextSb = new StringBuilder()` and `.Append(delta.Text)` in both AiOperatorService locations
- For the ViewModel, accumulate into a local `StringBuilder`, update `currentTextBlock.Content` from `sb.ToString()` only on batched UI tick (see 2B)

### 2B. Batch dispatcher invokes for TextDelta
**File:** `src/CEAISuite.Desktop/ViewModels/AiOperatorViewModel.cs` lines 226-241
- Replace per-delta `_dispatcher.Invoke()` with a batched approach:
  - Accumulate deltas into a `StringBuilder _pendingText` field
  - Use a `DispatcherTimer` at 16ms (60fps) or `Task.Delay(16)` to flush to UI
  - On flush: set `currentTextBlock.Content = sb.ToString()`, fire `StreamingBlocksUpdated` once
- Keep ToolCallStarted/Completed/Error as immediate dispatches (they're low-frequency)

### 2C. ObservableCollection bulk update
**Files:**
- `src/CEAISuite.Desktop/ViewModels/AiOperatorViewModel.cs` lines 610-612 (ChatMessages), 652-654 (ChatHistory)
- Create a helper extension `ReplaceAll<T>(this ObservableCollection<T>, IEnumerable<T>)` that:
  1. Clears without raising per-item events
  2. Adds all items
  3. Raises a single `Reset` notification
- Apply to both `ChatMessages` and `ChatHistory` refresh methods

### 2D. Cap StreamingBlocks growth
**File:** `src/CEAISuite.Desktop/ViewModels/AiOperatorViewModel.cs`
- After streaming completes (line ~319), move finalized blocks into `ChatMessages` and clear `StreamingBlocks`
- This already happens conceptually; verify no leak where old blocks persist

---

## Phase 3: UI Virtualization (1 batch change, many controls)

### 3A. Fix critical chat panel virtualization break
**File:** `src/CEAISuite.Desktop/MainWindow.xaml` lines 2048-2165
- **ChatMessages** (line 2048-2085): Remove wrapping `ScrollViewer` + `StackPanel`. Replace `ItemsControl` with `ListBox` using:
  ```xml
  <ListBox VirtualizingStackPanel.IsVirtualizing="True"
           VirtualizingStackPanel.VirtualizationMode="Recycling"
           ScrollViewer.CanContentScroll="True" ... />
  ```
- **StreamingBlocks** (line 2088-2164): Same treatment — replace `ItemsControl` with virtualized `ListBox`
- Style the ListBox to look identical (remove selection highlight, etc.)

### 3B. Add virtualization to all ListViews/ListBoxes
**File:** `src/CEAISuite.Desktop/MainWindow.xaml` — add these properties to every `ListView`/`ListBox`:
```xml
VirtualizingStackPanel.IsVirtualizing="True"
VirtualizingStackPanel.VirtualizationMode="Recycling"
ScrollViewer.CanContentScroll="True"
```

**High-priority controls (large item counts):**
| Control | Line | Binding | Typical size |
|---------|------|---------|-------------|
| ChatHistory | 1778 | ChatHistory | 50-500 |
| ScanResults | 1011 | Results | 1,000-100,000 |
| Disassembly | 812-859 | Lines | 10,000+ |
| MemoryRegions | 433 | Regions | 1,000+ |
| DiffItems | 1599 | DiffItems | 1,000+ |
| FindResults | 1628 | Results | 10,000+ |
| HitLog | 1505 | HitLog | 1,000+ |

**Medium-priority controls:**
| Control | Line | Binding |
|---------|------|---------|
| Processes | 288 | Processes |
| Modules | 333 | Modules |
| Threads | 380 | Threads |
| Snapshots | 1588 | Snapshots |
| Scripts | 1558 | Scripts |
| PatchHistory | 1687 | PatchHistory |
| JournalEntries | 1710 | JournalEntries |
| CodeCaveHooks | 1529 | CodeCaveHooks |
| Hotkeys | 1653 | Hotkeys |

Also in sub-controls:
- `src/CEAISuite.Desktop/Controls/DataInspectorPanel.xaml` line 21 (Entries)
- `src/CEAISuite.Desktop/MemoryBrowserControl.xaml` lines 185 (Bookmarks), 222 (SpiderNodes TreeView)

### 3C. Replace TextBox with TextBlock for read-only chat content
**File:** `src/CEAISuite.Desktop/MainWindow.xaml` line 2076-2080
- The chat message `TextBox` with `IsReadOnly="True"` and `TextWrapping="Wrap"` is more expensive than a `TextBlock` with `TextWrapping="Wrap"`
- Replace with `TextBlock` — add a context menu with Copy for selectability if needed

---

## Phase 4: ConfigureAwait(false) Sweep (bulk change)

### 4A. CEAISuite.Application (~382 await calls)
Add `.ConfigureAwait(false)` to all `await` statements in:
- `AiOperatorService.cs`
- `AgentLoop/AgentLoop.cs`
- `AgentLoop/McpClient.cs`
- `AgentLoop/ToolExecutor.cs`
- `AgentLoop/ChatHistoryManager.cs`
- `BreakpointService.cs`
- ~~`ProcessWatchdogService.cs`~~ ✅ (completed — all awaits have ConfigureAwait)
- All other async methods in the Application project

**Exclude:** Any `await` followed by UI-thread work (there shouldn't be any in this project)

### 4B. CEAISuite.Persistence.Sqlite (~22 await calls)
- `SqliteInvestigationSessionRepository.cs` — all 22 `await` statements need `.ConfigureAwait(false)`

### ~~4C. CEAISuite.Engine.Windows~~ ✅ N/A
- File contains no async/await calls — nothing to do

---

## Phase 5: Minor Optimizations (2 changes)

### 5A. Process list short-TTL cache
**File:** `src/CEAISuite.Application/AiToolFunctions.cs` lines 78-84
- Add a 2-second `MemoryCache` or simple timestamp + cached result pattern
- Invalidate on process attach/detach events

### 5B. DataInspectorViewModel off-thread computation
**File:** `src/CEAISuite.Desktop/ViewModels/DataInspectorViewModel.cs` lines 21-105
- Move the string formatting loop to `Task.Run()`
- Dispatch the final `Entries` list back to UI thread in one batch

---

## Verification

After each phase:
1. `dotnet build` both Debug and Release — zero errors/warnings
2. Launch the app in Debug, send a chat message, verify streaming renders smoothly
3. Open a large scan result set (1000+ items) — confirm no UI freeze
4. Open disassembly view — confirm smooth scrolling
5. Shut down the app — confirm no 5-second hang
6. Run existing tests: `dotnet test` across all test projects

---

## Files Modified Summary

| File | Phases |
|------|--------|
| `src/CEAISuite.Application/AiOperatorService.cs` | 1A, 2A, 4A |
| `src/CEAISuite.Engine.Lua/MoonSharpLuaEngine.cs` | 1B |
| `src/CEAISuite.Engine.Abstractions/IScriptEngine.cs` | 1B |
| `src/CEAISuite.Application/AgentLoop/McpClient.cs` | 1C, 4A |
| `src/CEAISuite.Desktop/ViewModels/AiOperatorViewModel.cs` | 2A, 2B, 2C, 2D |
| `src/CEAISuite.Desktop/MainWindow.xaml` | 3A, 3B, 3C |
| `src/CEAISuite.Desktop/Controls/DataInspectorPanel.xaml` | 3B |
| `src/CEAISuite.Desktop/MemoryBrowserControl.xaml` | 3B |
| `src/CEAISuite.Application/AgentLoop/AgentLoop.cs` | 4A |
| `src/CEAISuite.Application/AgentLoop/ToolExecutor.cs` | 4A |
| `src/CEAISuite.Application/AgentLoop/ChatHistoryManager.cs` | 4A |
| `src/CEAISuite.Application/BreakpointService.cs` | 4A |
| ~~`src/CEAISuite.Application/ProcessWatchdogService.cs`~~ | ~~4A~~ ✅ done |
| `src/CEAISuite.Persistence.Sqlite/SqliteInvestigationSessionRepository.cs` | 4B |
| ~~`src/CEAISuite.Engine.Windows/WindowsBreakpointEngine.cs`~~ | ~~4C~~ ✅ N/A |
| `src/CEAISuite.Application/AiToolFunctions.cs` | 5A |
| `src/CEAISuite.Desktop/ViewModels/DataInspectorViewModel.cs` | 5B |
| Possibly: helper extension class for RangeObservableCollection | 2C |
