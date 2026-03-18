# Il2Cpp Runtime Structure Layouts

## Core Types

### Il2CppObject (base of all managed objects)
```
Size: 0x10 (16 bytes minimum)
+0x00  Il2CppClass*  klass        ; Pointer to class descriptor
+0x08  MonitorData*  monitor      ; Thread synchronization (usually NULL)
+0x10  [Instance fields start here]
```

### Il2CppClass (type descriptor)
```
+0x00   Il2CppImage*         image
+0x08   void*                gc_desc
+0x10   const char*          name           ; Class name (ASCII string)
+0x18   const char*          namespaze      ; Namespace
+0x20   Il2CppType           byval_arg
+0x28   Il2CppType           this_arg
+0x30   Il2CppClass*         element_class
+0x40   Il2CppClass*         parent         ; Base class
+0x48   Il2CppMethodInfo**   methods
+0xB0   void*                static_fields  ; ← Pointer to static field data
+0xB8   Il2CppFieldInfo*     fields
```
Note: Exact offsets vary by Unity/Il2Cpp version. Use DissectStructure to verify.

### Il2CppString
```
+0x00  Il2CppClass*  klass
+0x08  MonitorData*  monitor
+0x10  int32         length        ; String length in chars
+0x14  char16_t[]    chars         ; UTF-16LE string data
```
Read strings with: `BrowseMemory` at object+0x14, length × 2 bytes.

### Il2CppArray (System.Array)
```
+0x00  Il2CppClass*  klass
+0x08  MonitorData*  monitor
+0x10  Il2CppArrayBounds*  bounds  ; NULL for single-dimension arrays
+0x18  il2cpp_array_size_t  max_length  ; Array length
+0x20  [Element 0]                      ; Array data starts here
```
Element stride = element size. For reference types, stride = 8 (pointer size).

### Il2CppDictionary (System.Collections.Generic.Dictionary<K,V>)
```
+0x10  Il2CppArray*  buckets     ; int32 array of bucket indices
+0x18  Il2CppArray*  entries     ; Entry struct array
+0x20  int32         count       ; Number of entries
+0x24  int32         freeList
+0x28  int32         freeCount
+0x30  IEqualityComparer*  comparer
```

### Entry struct (within entries array)
```
+0x00  int32   hashCode
+0x04  int32   next
+0x08  TKey    key        ; Size depends on TKey type
+0x??  TValue  value      ; Follows key
```

## Common Field Type Sizes

| C# Type | Native Size | Il2Cpp Type |
|---|---|---|
| bool | 1 byte | uint8_t |
| byte | 1 byte | uint8_t |
| short | 2 bytes | int16_t |
| int | 4 bytes | int32_t |
| long | 8 bytes | int64_t |
| float | 4 bytes | float |
| double | 8 bytes | double |
| string | 8 bytes (pointer) | Il2CppString* |
| object ref | 8 bytes (pointer) | Il2CppObject* |
| Vector2 | 8 bytes | float × 2 |
| Vector3 | 12 bytes | float × 3 |
| Vector4 / Quaternion | 16 bytes | float × 4 |

## Pointer Chain Patterns

### Instance Field Access
```
Static reference → Object pointer → +FieldOffset → Value
GameAssembly.dll+0xABCDEF → [ptr] → [ptr]+0x14 → int32 HP value
```

### Singleton Manager Pattern
```
GameAssembly.dll+StaticRef → Manager.instance → Manager+0x20 → Player
Player+0x10 → PlayerStats → Stats+0x14 → HP (float)
```
Total: 4-level pointer chain (common in Unity games)

### Nested Component Access
```
GameAssembly.dll+Ref → GameManager → +0x18 → Player (GameObject)
Player → +0x30 → PlayerHealth (Component) → +0x10 → currentHP
```
