# Unreal Engine Object Traversal Examples

## Example 1: GWorld → Player Health

### Full pointer chain:
```
Game.exe+GWorldOffset → UWorld*
  → +0x180 OwningGameInstance → UGameInstance*
    → +0x38 LocalPlayers → TArray<ULocalPlayer*>
      → [0] → +0x30 PlayerController → APlayerController*
        → +0x338 Pawn → APawn* (ACharacter*)
          → +0x??? HealthComponent → UHealthComponent*
            → +0x??? Health → float
```

### CE Address Table Setup:
```
Base: Game.exe+0x5A23B40 (GWorld offset — find via pattern scan)
Pointer path: [Base] → +0x180 → +0x38 → +0x0 → +0x30 → +0x338 → +0xComponentOffset → +0xHealthOffset
```

## Example 2: Finding GWorld Pattern

Common GWorld reference pattern (UE4):
```asm
48 8B 05 ?? ?? ?? ??    ; mov rax, [rip+????????]  ← GWorld
48 85 C0                ; test rax, rax
74 ??                   ; jz skip
```
Use `SearchInstructionPattern` or `aobscanmodule` with this signature.
The RIP-relative offset in the `mov` instruction points to GWorld.

## Example 3: Resolving FName to String

Given an FName with ComparisonIndex = 1234:
1. Read GNames base pointer
2. Navigate chunk array: `GNames + (Index / ChunkSize) * 8` → chunk pointer
3. Within chunk: `Chunk + (Index % ChunkSize) * EntryStride` → FNameEntry
4. FNameEntry contains: header (2 bytes) + UTF-8/UTF-16 string data
5. Read the string from offset +2 (UE4) or +4 (UE5)

## Example 4: Iterating World Actors

```
GWorld → +0x30 PersistentLevel (ULevel*)
ULevel → +0x98 Actors (TArray<AActor*>)
  → Read Data pointer (+0x00) and Count (+0x08)
  → For i in 0..Count:
       Read [Data + i*8] → AActor* pointer
       Read AActor+0x18 → FName → resolve to string
       Check if name matches target (e.g., "BP_PlayerCharacter")
```

## Example 5: Component Discovery

Once you have an AActor:
```
AActor → +0x?? OwnedComponents (TArray<UActorComponent*>)
For each component:
  Read component pointer
  Read UObject+0x10 → UClass*
  Read UClass → UObject+0x18 → FName → resolve name
  Find "HealthComponent", "InventoryComponent", etc.
  Then explore that component's properties at known offsets
```

## Offset Discovery Strategy

Offsets change between UE versions and game builds. Strategy:
1. Use known UObject fields (Name at +0x18, Class at +0x10) to verify object pointers
2. Use `DissectStructure` to explore fields around expected offsets
3. Cross-reference with SDK dumps if available (UnrealDumper output)
4. Verify by reading known values (e.g., player name string should be readable)
5. `BrowseMemory` to examine raw hex around object base
