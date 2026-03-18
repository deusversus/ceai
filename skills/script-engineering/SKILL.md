---
name: script-engineering
description: >
  Expert guide to writing, debugging, and iterating on Auto Assembler (AA) scripts including code
  caves, AOB injection, conditional logic, and common game modification patterns. Load this skill
  when creating, editing, or fixing scripts in the address table.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - scripts
  - auto-assembler
  - injection
  - code-cave
  - trainer
triggers:
  - script
  - auto assembler
  - code cave
  - injection
  - trainer
  - multiplier
  - NOP
  - cheat table
  - AA script
  - modify
  - patch
  - hook
---

# Script Engineering — Auto Assembler Mastery

## Script Lifecycle

### Creating a New Script
1. Write the full AA script with `[ENABLE]` and `[DISABLE]` sections
2. `CreateScriptEntry` to add it to the address table
3. `ValidateScript` to check syntax
4. `ValidateScriptDeep` for semantic validation against live process
5. `EnableScript` to activate
6. Ask user to test in-game
7. If wrong → `DisableScript`, analyze, `EditScript` with fix, repeat

### Editing an Existing Script
1. `ViewScript` to read current source
2. Identify: hook address, what it does, original bytes
3. `Disassemble` at the hook address for surrounding context
4. `EditScript` to replace with improved version
5. Validate and test

## AA Script Structure

### Basic Code Cave (Full Injection)
```asm
[ENABLE]
alloc(newmem, 2048, "ModuleName.exe")
label(returnhere)
label(originalcode)

newmem:
  ; Your custom logic here
  jmp originalcode

originalcode:
  ; Original instructions that were overwritten
  jmp returnhere

"ModuleName.exe"+1A2B3C:
  jmp newmem
  nop                    ; Pad to match original instruction length
  nop
returnhere:

[DISABLE]
"ModuleName.exe"+1A2B3C:
  ; Restore original bytes
  db 48 8B 45 10 89 54 24
dealloc(newmem)
```

### AOB Injection (Version-Resilient)
```asm
[ENABLE]
aobscanmodule(aob_health, GameAssembly.dll, 89 47 ?? 8B 45 ?? 48 8B)
alloc(newmem, 2048, aob_health)
label(returnhere)
label(originalcode)

newmem:
  ; Custom logic
  jmp originalcode

originalcode:
  ; Original instructions
  jmp returnhere

aob_health:
  jmp newmem
  nop
  nop
returnhere:

registersymbol(aob_health)

[DISABLE]
aob_health:
  db 89 47 ?? 8B 45 ?? 48 8B    ; Restore original bytes
unregistersymbol(aob_health)
dealloc(newmem)
```

## Common Modification Patterns

### Health Lock (Prevent Decrease)
```asm
newmem:
  ; Original: sub [rdi+14h], eax (damage subtraction)
  ; Replace: skip the subtraction entirely
  nop                     ; Don't subtract damage
  jmp returnhere
```

### Value Multiplier (e.g., 10× EXP)
```asm
newmem:
  ; Original: add [rcx+20h], eax (EXP gain)
  imul eax, eax, 10      ; Multiply gained EXP by 10
  add [rcx+20h], eax     ; Apply multiplied value
  jmp returnhere
```

### Conditional Modification (Player Only)
```asm
newmem:
  push rbx
  mov rbx, [rcx]          ; Load vtable pointer or entity type
  cmp rbx, playerVtable   ; Compare to known player vtable
  pop rbx
  jne originalcode        ; If not player, don't modify

  ; Player-specific modification
  mov dword ptr [rcx+14h], 0x42C80000  ; Set HP to 100.0 (float)
  jmp returnhere

originalcode:
  ; Execute original code for non-player entities
  movss [rcx+14h], xmm0
  jmp returnhere
```

### One-Hit Kill
```asm
newmem:
  ; Hook at the enemy damage application instruction
  ; Original: sub [rdi+14h], eax
  cmp [rdi+someOffset], playerFlag    ; Check if this is an enemy
  je kill_enemy
  sub [rdi+14h], eax                  ; Normal damage for player
  jmp returnhere

kill_enemy:
  mov dword ptr [rdi+14h], 0          ; Set enemy HP to 0
  jmp returnhere
```

### Infinite Ammo (Prevent Decrement)
```asm
newmem:
  ; Original: dec dword ptr [rbx+10h]
  ; Simply don't decrement
  jmp returnhere
originalcode:
  dec dword ptr [rbx+10h]
  jmp returnhere
```

### Float Value Override
```asm
newmem:
  push rax
  mov eax, 0x42C80000     ; 100.0 as float hex
  mov [rcx+08h], eax       ; Write to HP field
  pop rax
  jmp returnhere
```

## Key Rules

### Instruction Length Matching
The JMP instruction at the hook point is 5 bytes (E9 + 4-byte offset).
- Count the total bytes of instructions you're replacing
- If > 5 bytes: add NOPs after JMP to fill the gap
- If < 5 bytes: you MUST include more instructions in the replacement
- Use `Disassemble` to see exact instruction lengths

### Assert Pattern Validation
Always verify original bytes before patching:
```asm
assert(aob_target, 48 8B 45 10 89)  ; Fails if bytes don't match
```
This prevents scripts from patching wrong locations after game updates.

### Register Preservation
If your code cave uses registers, SAVE and RESTORE them:
```asm
newmem:
  push rax
  push rbx
  ; ... your code using rax and rbx ...
  pop rbx
  pop rax
  ; ... original code ...
  jmp returnhere
```

For XMM registers (floats):
```asm
  sub rsp, 10h
  movdqu [rsp], xmm0
  ; ... use xmm0 ...
  movdqu xmm0, [rsp]
  add rsp, 10h
```

## Iteration Philosophy

- Scripts rarely work perfectly first try
- Check register contents with breakpoints to verify assumptions
- If a multiplier gives wrong values, the hook may be at the wrong stage of computation
- Use `HexDump` to verify original bytes match the assert pattern
- After enabling, ALWAYS ask the user to test in-game
- If not working, gather data (register snapshots, hit log) before guessing

See `references/templates.md` for copy-paste script templates.
