# Auto Assembler Script Templates

## Template 1: Basic Code Cave (Address-Based)
```asm
[ENABLE]
alloc(newmem, 2048, "MODULE.exe")
label(returnhere)
label(originalcode)

newmem:
  // INSERT YOUR CODE HERE
  jmp originalcode

originalcode:
  // PASTE ORIGINAL INSTRUCTIONS HERE
  jmp returnhere

"MODULE.exe"+OFFSET:
  jmp newmem
  // ADD NOP PADDING IF NEEDED
returnhere:

[DISABLE]
"MODULE.exe"+OFFSET:
  // PASTE ORIGINAL BYTES HERE (db XX XX XX ...)
dealloc(newmem)
```

## Template 2: AOB Injection (Update-Resilient)
```asm
[ENABLE]
aobscanmodule(LABEL, MODULE.dll, XX XX ?? XX XX XX ?? XX)
alloc(newmem, 2048, LABEL)
label(returnhere)
label(originalcode)
registersymbol(LABEL)

newmem:
  // INSERT YOUR CODE HERE
  jmp originalcode

originalcode:
  // PASTE ORIGINAL INSTRUCTIONS HERE
  jmp returnhere

LABEL:
  jmp newmem
  // NOP PADDING
returnhere:

[DISABLE]
LABEL:
  db XX XX ?? XX XX XX ?? XX
unregistersymbol(LABEL)
dealloc(newmem)
```

## Template 3: Integer Multiplier
```asm
[ENABLE]
aobscanmodule(LABEL, MODULE.dll, PATTERN_BYTES)
alloc(newmem, 2048, LABEL)
label(returnhere)
registersymbol(LABEL)

newmem:
  imul eax, eax, MULTIPLIER    // Change MULTIPLIER to desired value
  // ORIGINAL INSTRUCTIONS (typically: add [reg+offset], eax or mov [reg+offset], eax)
  jmp returnhere

LABEL:
  jmp newmem
  nop
returnhere:

[DISABLE]
LABEL:
  db ORIGINAL_BYTES
unregistersymbol(LABEL)
dealloc(newmem)
```

## Template 4: Float Multiplier
```asm
[ENABLE]
aobscanmodule(LABEL, MODULE.dll, PATTERN_BYTES)
alloc(newmem, 2048, LABEL)
alloc(multiplier, 8, LABEL)
label(returnhere)
registersymbol(LABEL)

multiplier:
  dd (float)10.0               // Change to desired multiplier

newmem:
  mulss xmm0, [multiplier]    // Multiply float value
  // ORIGINAL INSTRUCTION (e.g., movss [rcx+offset], xmm0)
  jmp returnhere

LABEL:
  jmp newmem
  nop
  nop
returnhere:

[DISABLE]
LABEL:
  db ORIGINAL_BYTES
unregistersymbol(LABEL)
dealloc(newmem)
dealloc(multiplier)
```

## Template 5: NOP (Disable a Write)
```asm
[ENABLE]
aobscanmodule(LABEL, MODULE.dll, PATTERN_BYTES)
registersymbol(LABEL)

LABEL:
  db 90 90 90 90 90           // NOP over the instruction (match byte count!)

[DISABLE]
LABEL:
  db ORIGINAL_BYTES
unregistersymbol(LABEL)
```

## Template 6: Conditional (Player-Only Modification)
```asm
[ENABLE]
aobscanmodule(LABEL, MODULE.dll, PATTERN_BYTES)
alloc(newmem, 2048, LABEL)
label(returnhere)
label(originalcode)
label(isplayer)
registersymbol(LABEL)

newmem:
  // CHECK IF THIS IS THE PLAYER
  // Strategy 1: Compare a unique field (e.g., team ID, entity type)
  cmp dword ptr [REG+TYPE_OFFSET], PLAYER_TYPE_VALUE
  je isplayer
  jmp originalcode

isplayer:
  // YOUR PLAYER-SPECIFIC MODIFICATION
  jmp returnhere

originalcode:
  // ORIGINAL INSTRUCTIONS (unmodified for non-player)
  jmp returnhere

LABEL:
  jmp newmem
  nop
returnhere:

[DISABLE]
LABEL:
  db ORIGINAL_BYTES
unregistersymbol(LABEL)
dealloc(newmem)
```

## Float Hex Quick Reference
| Float Value | Hex (dword) |
|---|---|
| 0.0 | 00000000 |
| 0.5 | 3F000000 |
| 1.0 | 3F800000 |
| 2.0 | 40000000 |
| 10.0 | 41200000 |
| 50.0 | 42480000 |
| 100.0 | 42C80000 |
| 1000.0 | 447A0000 |
| 99999.0 | 47C34F80 |
| -1.0 | BF800000 |
