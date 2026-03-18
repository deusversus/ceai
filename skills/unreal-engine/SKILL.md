---
name: unreal-engine
description: >
  Expert knowledge for reverse engineering Unreal Engine 4/5 games. Covers GWorld, GNames,
  GObjects global tables, UObject hierarchy, FName string resolution, and SDK generation
  patterns. Load when the target is a UE4 or UE5 game.
version: "1.0.0"
author: "CE AI Suite"
tags:
  - unreal
  - ue4
  - ue5
  - uobject
  - game-engine
triggers:
  - unreal
  - ue4
  - ue5
  - uobject
  - gworld
  - gnames
  - gobjects
  - fname
  - blueprint
  - UWorld
  - AActor
  - APawn
  - ACharacter
  - APlayerController
---

# Unreal Engine 4/5 Reverse Engineering

## Identifying a UE4/5 Game

Check for these indicators:
- Main executable often named `<Game>-Win64-Shipping.exe`
- `Engine/` directory in game folder
- `.pak` files (UE asset packages)
- Presence of `UE4` or `UnrealEngine` strings in the binary

Module to focus on: the main game executable (not separate DLLs like Unity).

## Core Global Tables

Every UE game has three critical global tables. Finding them unlocks the entire object hierarchy.

### GWorld (UWorld*)
- Pointer to the current world/level context
- From GWorld you can reach ALL actors, pawns, and game state
- Pattern: Usually found near a function that references "World" or level loading

Traversal:
```
GWorld → UWorld
  +0x30  PersistentLevel (ULevel*)
  +0x??  OwningGameInstance (UGameInstance*)
  +0x??  AuthorityGameMode (AGameMode*)
```

### GNames (FNamePool / TNameEntryArray)
- Global string table — every UObject name is stored here
- FName is an index into this pool, not a string pointer
- Allows resolving human-readable names for any object

FName structure:
```
+0x00  int32  ComparisonIndex    ; Index into GNames pool
+0x04  int32  Number             ; Instance number (0 for most objects)
```

### GObjects (FUObjectArray / TUObjectArray)
- Array of ALL UObject instances in the game
- Each entry contains a pointer to the UObject + flags
- Used to enumerate all loaded objects by class

Entry structure:
```
+0x00  UObject*  Object          ; Pointer to the UObject
+0x08  int32     Flags           ; Object flags
+0x0C  int32     ClusterRootIndex
+0x10  int32     SerialNumber
```

## UObject Hierarchy

Every game object inherits from UObject:

```
UObject
├── UField
│   ├── UStruct
│   │   ├── UClass          ← Class definitions
│   │   ├── UFunction       ← Blueprint/C++ functions  
│   │   └── UScriptStruct   ← Struct definitions
│   └── UProperty           ← Field definitions (offset, type, size)
├── AActor                  ← Anything placed in the world
│   ├── APawn               ← Anything that can be possessed
│   │   └── ACharacter      ← Pawn with movement/mesh
│   ├── APlayerController   ← Player's input handler
│   └── AGameMode           ← Game rules
├── UActorComponent         ← Components attached to actors
└── UGameInstance           ← Root game session
```

### UObject Memory Layout
```
+0x00  void**     VTablePtr        ; C++ virtual function table
+0x08  int32      ObjectFlags
+0x0C  int32      InternalIndex    ; Index in GObjects
+0x10  UClass*    ClassPrivate     ; Pointer to this object's UClass
+0x18  FName      NamePrivate      ; Object's FName (index into GNames)
+0x20  UObject*   OuterPrivate     ; Owning object (package/level)
```
Note: Exact offsets vary by UE version. UE5 may add 8-16 bytes compared to UE4.

## Analysis Workflow

### Step 1: Find GWorld
1. Scan for known patterns in the game executable
2. Use `SearchInstructionPattern` looking for references to world initialization
3. Tools like GSpots automate this — patterns are version-specific
4. Alternative: Find a known actor address (via value scanning), then trace back

### Step 2: Traverse the World
```
GWorld → PersistentLevel → Actors array → iterate to find player
```
Actors array is a TArray<AActor*>:
```
+0x00  AActor**  Data    ; Pointer to array of actor pointers
+0x08  int32     Count   ; Number of actors
+0x0C  int32     Max     ; Array capacity
```

### Step 3: Find Player Character
From the actors array, identify the player:
- Check the UClass name (resolve via GNames) for "Character" or player-specific class
- Or: `GWorld → GameInstance → LocalPlayers[0] → PlayerController → Pawn`
- PlayerController+Pawn pointer gives you the controlled character

### Step 4: Map Properties
UE uses UProperty objects to describe fields:
```
UProperty:
  +0x44  int32   Offset_Internal    ; Byte offset within the owning struct
  +0x48  int32   ElementSize
```
Use the class's property chain to enumerate all fields with names and offsets.

## Common Patterns

### Health / Damage
- Usually on a `UHealthComponent` or similar `UActorComponent`
- Access: `ACharacter → Components → UHealthComponent → Health (float)`
- Damage often goes through `TakeDamage` virtual function

### Player Position
```
ACharacter → RootComponent (USceneComponent) → RelativeLocation (FVector)
FVector: float X (+0x00), float Y (+0x04), float Z (+0x08)
```

### Inventory / Items
- Often a `UInventoryComponent` with a TArray of item structs
- Or a `APlayerState` with inventory data

### TArray<T>
```
+0x00  T*      Data     ; Heap-allocated element array
+0x08  int32   Count    ; Current element count
+0x0C  int32   Max      ; Allocated capacity
```

### FString (dynamic string)
```
+0x00  TCHAR*  Data     ; UTF-16 string data on heap
+0x08  int32   Count    ; Length including null terminator
+0x0C  int32   Max      ; Buffer capacity
```

### TMap<K,V>
Complex hash map — elements stored in a TArray with hash buckets.
Use `DissectStructure` to explore rather than manual offset calculation.

## UE5 Differences from UE4
- UObject may have additional fields (WorldPartition support)
- FName pool structure changed (chunked allocation)
- Some offset shifts (+8 to +16 bytes) in common structures
- Same overall hierarchy — patterns transfer with offset adjustments

## ASLR Handling
- Game executable base changes every launch
- Use `module+offset` notation for all addresses
- GWorld, GNames, GObjects offsets are stable per game build
- `ResolveSymbol` handles the conversion at runtime

See `references/traversal.md` for step-by-step object traversal examples.
