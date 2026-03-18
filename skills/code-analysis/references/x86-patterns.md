# x86/x64 Assembly Quick Reference for Game Analysis

## Registers

### General Purpose (64-bit / 32-bit / 16-bit / 8-bit)
| 64-bit | 32-bit | 16-bit | 8-bit | Typical Use |
|--------|--------|--------|-------|-------------|
| RAX | EAX | AX | AL | Return values, accumulator |
| RBX | EBX | BX | BL | Preserved across calls (callee-saved) |
| RCX | ECX | CX | CL | Counter, 1st arg (Windows x64) |
| RDX | EDX | DX | DL | 2nd arg (Windows x64) |
| R8 | R8D | R8W | R8B | 3rd arg (Windows x64) |
| R9 | R9D | R9W | R9B | 4th arg (Windows x64) |
| RSI | ESI | SI | SIL | Source pointer |
| RDI | EDI | DI | DIL | Destination pointer |
| RBP | EBP | BP | BPL | Frame pointer |
| RSP | ESP | SP | SPL | Stack pointer |

### SIMD (floating point in games)
| Register | Width | Use |
|----------|-------|-----|
| XMM0-XMM15 | 128-bit | SSE float ops, float args 1-4 (Windows x64) |
| YMM0-YMM15 | 256-bit | AVX operations |

## Common Game Patterns

### Field Access (struct pointer + offset)
```asm
mov eax, [rcx+14h]       ; Read int32 field at offset 0x14
mov [rcx+14h], eax        ; Write int32 field
movss xmm0, [rcx+08h]    ; Read float field at offset 0x8
movss [rcx+08h], xmm0    ; Write float field
```

### Arithmetic on Fields
```asm
add [rcx+14h], eax        ; field += eax
sub [rcx+14h], eax        ; field -= eax
inc dword ptr [rcx+14h]   ; field++
imul eax, [rcx+14h], 5    ; eax = field * 5
```

### Float Arithmetic (SSE)
```asm
addss xmm0, xmm1         ; xmm0 += xmm1 (scalar single float)
subss xmm0, xmm1         ; xmm0 -= xmm1
mulss xmm0, xmm1         ; xmm0 *= xmm1
divss xmm0, xmm1         ; xmm0 /= xmm1
comiss xmm0, xmm1        ; compare floats (sets flags)
```

### Comparisons and Branches
```asm
cmp eax, ebx              ; compare two integers
jl  target                ; jump if less (signed)
jle target                ; jump if less or equal
jg  target                ; jump if greater
jge target                ; jump if greater or equal
je  target                ; jump if equal (zero flag)
jne target                ; jump if not equal
test eax, eax             ; AND without storing — sets ZF if eax == 0
jz  target                ; jump if zero (same as je after test)
jnz target                ; jump if not zero
```

### Function Calls (Windows x64)
```asm
; Arguments: RCX, RDX, R8, R9, then stack
; Float args: XMM0, XMM1, XMM2, XMM3
; Return: RAX (int) or XMM0 (float)
; Caller must allocate 32 bytes shadow space

sub rsp, 28h              ; shadow space + alignment
mov rcx, rdi              ; 1st arg
mov edx, 5                ; 2nd arg
call SomeFunction
add rsp, 28h              ; cleanup
; Result in eax/rax
```

### Virtual Function Calls (C++ vtable)
```asm
mov rax, [rcx]            ; load vtable pointer from object
call [rax+38h]            ; call vtable entry at offset 0x38
                          ; (each entry is 8 bytes on x64)
                          ; offset/8 = vtable index (0x38/8 = 7th virtual method)
```

### Common NOPs
```asm
nop                       ; 1-byte NOP (0x90)
xchg eax, eax             ; 2-byte NOP (66 90)
lea eax, [eax+0]          ; 3-byte NOP
nop dword ptr [rax]       ; multi-byte NOPs (for alignment)
```

### Jump Encoding
```asm
jmp short target          ; 2 bytes: EB + 1-byte signed offset
jmp near target           ; 5 bytes: E9 + 4-byte signed offset (most common for hooks)
jmp [rip+offset]          ; 6 bytes: FF 25 + 4-byte RIP-relative offset
```
A 5-byte JMP can reach ±2GB from the instruction. Code caves allocated nearby always work.

## Identifying Key Structures

### "this" pointer pattern (C++ method call)
```asm
mov rcx, [rsp+28h]        ; load "this" from stack local
call [rax+10h]            ; virtual method call
```
The first argument (RCX on Windows x64) is always `this` in C++ instance methods.

### Array access pattern
```asm
mov eax, [rcx+rdx*4]     ; array[index] for int32 array
                          ; rcx = array base, rdx = index, *4 = element size
movsxd rdx, dword ptr [rcx+rax*4+10h]  ; array[index] with struct base offset 0x10
```

### Switch/case table
```asm
cmp eax, 5                ; check if within case range
ja  default_case          ; jump to default if > 5
lea rcx, [rip+table]      ; load jump table address
movsxd rax, [rcx+rax*4]   ; load relative offset from table
add rax, rcx              ; compute absolute target
jmp rax                   ; dispatch
```
