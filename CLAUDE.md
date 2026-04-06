# CE AI Suite — Project Instructions

## Implementation Rules

When implementing features, ALWAYS wire up all connections between components (ViewModels, Services, UI). Do not skip wiring steps even if they seem obvious. After implementation, verify all call sites are updated.

## Bug Fixing

When asked to fix something, fix the ACTUAL thing requested. Do not replace, remove, or simplify the existing approach unless explicitly asked. If an embed is broken, fix the embed — don't replace it with a link.

## Quality Checks

After completing any implementation, do a self-audit: re-read the changed files, verify builds pass, and check for issues before reporting done. Do not assume fast completion means correct completion.

## .NET / WPF Conventions

For this WPF/.NET project: always run `dotnet build` after changes and fix all errors/warnings before reporting completion. Never make methods static without updating all call sites. Never run `dotnet clean` without user confirmation.

## Code Review & Audits

When running audits or reviews, do DEEP inspection — read actual file contents, don't just grep. Sub-agent audits must verify findings against real code, not just surface-level pattern matching.
