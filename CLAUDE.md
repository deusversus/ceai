# CE AI Suite — Project Instructions

## Stack
- .NET 10 / WPF / C# — Windows desktop application
- AI-powered game memory analysis tool (think: AI-enhanced Cheat Engine)
- See @ROADMAP.md for development phases and current status

## Build & Test
- Build both: `dotnet build -c Debug && dotnet build -c Release`
- Test: `dotnet test`
- User runs the Debug build from `bin/Debug/net10.0-windows/`
- NEVER run `dotnet clean` without user confirmation
- After ANY code change: build both configs and fix all errors/warnings before reporting done

## CI Awareness
- Tests run on GitHub Actions runners which are slower than local
- Widen timeouts and use WaitAsync patterns for async tests
- If a test passes locally but fails CI, the test is likely flaky — fix the test, don't suppress it
- Coverage thresholds are enforced; check existing coverage before adding test files

## Implementation Rules
When implementing features, ALWAYS wire up all connections between components (ViewModels, Services, UI). Do not skip wiring steps even if they seem obvious. After implementation, verify all call sites are updated.

## Bug Fixing
When asked to fix something, fix the ACTUAL thing requested. Do not replace, remove, or simplify the existing approach unless explicitly asked. If an embed is broken, fix the embed — don't replace it with a link.

## Quality Checks
After completing any implementation, do a self-audit: re-read the changed files, verify builds pass, and check for issues before reporting done. Do not assume fast completion means correct completion.

### Self-Audit Checklist (run after EVERY feature, especially agent-produced code)
1. **Read every new/changed file** — don't trust agent output blindly. Open and read the actual code.
2. **Thread safety** — any event handler or callback from a background thread that touches ObservableProperty or ObservableCollection MUST be wrapped in `IDispatcherService.Invoke()` or `Dispatcher.Invoke()`. This is not optional.
3. **Async patterns** — no `async void` except event handlers. All Tasks observed or awaited. `ContinueWith` needs fault handlers. CTS disposal must not race with the running task.
4. **Wiring completeness** — for every new command/event/bus, trace the full path: who raises it → who subscribes → what action is taken. Placeholder `_outputLog.Append("received")` is NOT wiring. Actually implement the behavior.
5. **Security surface** — any code that downloads files, loads DLLs, or accepts URLs must enforce HTTPS and verify integrity (SHA256). Path traversal must be blocked (symlinks, junctions, UNC, `..`). No string interpolation into commands or scripts. See @SECURITY.md for the full threat model and trust boundaries.
6. **Exception handling** — no silent `catch { }` without logging. User input parsing catches FormatException/OverflowException with friendly messages. Catch blocks must not swallow OutOfMemoryException or StackOverflowException.
7. **Resource cleanup** — classes with timers, event subscriptions, CTS, or unmanaged resources implement IDisposable. Event += has matching -= in Dispose. Timers stopped/disposed on cleanup.
8. **DI lifetime** — singletons registered in the container must NOT be manually disposed by consumers. Only the container disposes them.
9. **XAML resources** — if a dialog/panel uses a converter or style, verify the resource is declared or inherited. Missing `BoolToVisibilityConverter` = crash on open.
10. **Test depth** — tests that only check nulls and defaults are not real tests. Verify actual behavior: side effects, state transitions, error paths. Async tests use WaitAsync with generous timeouts for CI.
11. **Constructor changes** — when adding a parameter to any constructor, grep ALL call sites (production code AND tests) and update them. `dotnet build` catches production code; test compilation catches test code.
12. **Code duplication** — if the same logic appears 3+ times, extract to a helper (e.g., `LuaBindingHelpers.cs` pattern). Don't copy-paste validation or parsing.
13. **Native code correctness** — register preservation in hooks/trampolines, 32-bit alias masking, instruction boundary validation via Iced, RIP-relative relocation, thread suspension during patching, prologue validation before hooking.
14. **Event subscription lifecycle** — every `+=` has a matching `-=` in Dispose or uses the `EventSubscriptions` helper. Singleton publishers must not prevent GC of subscriber VMs.
15. **CI compatibility** — async tests use `WaitAsync` with generous timeouts. No tight timing assertions, no percentage-based progress checks, no patterns that assume `SynchronizationContext`.

For the full per-file/per-feature/per-phase checklist, see `wiki/definition-of-done.md`.
For the structured audit process, see `wiki/verification-protocol.md`.

## Code Style
- Never make methods static without updating all call sites
- When running audits or reviews: read actual file contents, don't just grep. Sub-agent audits must verify findings against real code, not just surface-level pattern matching.

## Sub-Agent Delegation Rules
- After a sub-agent completes work, READ every file it created or modified. Do not commit agent output without reviewing it yourself.
- Brief agents with the specific wiring requirements: dispatcher marshaling, DI lifetime, HTTPS enforcement, XAML resource availability. Agents don't know these patterns unless told.
- If an agent writes tests, verify the tests exercise real behavior, not just "assert not null". Add missing edge cases yourself.
- Agent-produced security-sensitive code (downloads, DLL loading, protocol handlers) requires extra scrutiny — agents routinely miss HTTPS enforcement, checksum verification, and input validation.

## Git
- Commit and push without asking when the intent is clear
- Follow existing commit message style (conventional commits: feat/fix/test/docs/ci/perf)
- Routine git operations don't need confirmation

## Fix Issues As They Arise
Always address issues the moment they are noticed. Never defer fixes with the assumption that someone else will catch them later. Presume the next person to encounter the issue may not recognize it or may lack the context to fix it correctly — we are always the best people for the job right now.

## Search Strategy
Before grepping or exploring, check the wiki first. The code map has the exact file for every class, interface, and tool in the codebase.

IMPORTANT: Wiki and memory files are in the Claude project memory directory, NOT inside the repo.
The base path is: `~/.claude/projects/C--Users-admin-Downloads-CEAI/memory/`
Use that full path (or glob `~/.claude/**/memory/wiki/*.md`) when reading wiki files.

- **Know what you're looking for?** Read `~/.claude/projects/C--Users-admin-Downloads-CEAI/memory/wiki/code-map.md` → then go directly to the source file it points to. Zero wasted tokens.
- **Know the filename but not the path?** Glob for it: `**/*FileName*`
- **Searching for a string in code?** Grep with a targeted path, not the whole repo. Use the code map to narrow to the right project directory.
- **Exploring an unfamiliar area?** Read the architecture and code-map wiki pages first (same directory as above). Then read source files directly. Do not grep broadly hoping to stumble into the answer.
- **Delegating to sub-agents?** Always pass relevant context from the wiki in the agent prompt. Sub-agents have no memory access — they'll waste tokens exploring from scratch unless you brief them.

## Development Framework

Three wiki pages define the full AI-assisted development process. Read them before starting any feature:

- **Work Decomposition** (`wiki/work-decomposition.md`) — how to break features into agent-sized units (400-700 lines each, dependency-ordered, with threat surface analysis)
- **Definition of Done** (`wiki/definition-of-done.md`) — per-file, per-feature, and per-phase completion checklists covering all 15 audit categories
- **Verification Protocol** (`wiki/verification-protocol.md`) — 7 ordered sweep passes replacing ad-hoc audits, with ready-to-use agent prompt templates

These live in the wiki directory: `~/.claude/projects/C--Users-admin-Downloads-CEAI/memory/wiki/`

When delegating to sub-agents, brief them on the relevant framework pages. Agents don't know the DoD or verification protocol unless told.

## Wiki & Memory
IMPORTANT: All memory and wiki files live at `~/.claude/projects/C--Users-admin-Downloads-CEAI/memory/` — NOT inside the repo. Do not look for them in the working directory.
- Wiki pages are under `wiki/` in that directory — synthesized knowledge that grows over time
- **Code map: `wiki/code-map.md`** — file-level index of every class and tool. Check this FIRST.
- When you learn something significant, update existing wiki/memory pages first
- Only create new files for genuinely new topics
- Note contradictions between new information and existing wiki content
- Add `updated:` date to any memory/wiki file you modify
- If you discover the code map is outdated (file moved/renamed/deleted), update it immediately
