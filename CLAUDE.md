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

## Code Style
- Never make methods static without updating all call sites
- When running audits or reviews: read actual file contents, don't just grep. Sub-agent audits must verify findings against real code, not just surface-level pattern matching.

## Git
- Commit and push without asking when the intent is clear
- Follow existing commit message style (conventional commits: feat/fix/test/docs/ci/perf)
- Routine git operations don't need confirmation

## Fix Issues As They Arise
Always address issues the moment they are noticed. Never defer fixes with the assumption that someone else will catch them later. Presume the next person to encounter the issue may not recognize it or may lack the context to fix it correctly — we are always the best people for the job right now.

## Wiki & Memory
- Memory files: `~/.claude/projects/.../memory/`
- Wiki pages: `memory/wiki/` — synthesized knowledge that grows over time
- When you learn something significant, update existing wiki/memory pages first
- Only create new files for genuinely new topics
- Note contradictions between new information and existing wiki content
- Add `updated:` date to any memory/wiki file you modify
