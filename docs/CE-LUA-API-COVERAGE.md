# CE Lua API Coverage -- CE AI Suite

**Date:** 2026-04-08
**Purpose:** Definitive reference for Cheat Engine Lua API compatibility in CE AI Suite

CE AI Suite implements a subset of Cheat Engine's Lua API for script compatibility. Scripts are executed via MoonSharp (Lua 5.2) in a sandboxed environment. This document lists every supported function, known differences from CE behavior, and notable missing functions.

---

## Supported Functions

All functions below are registered as Lua globals in `CeApiBindings.cs` and `CeFormBindings.cs`.

### Memory Read Functions

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `readByte` | `address: string` | `number` | Identical to CE. Address can be hex string, symbol, or module+offset. | Yes |
| `readSmallInteger` | `address: string` | `number` | CE name: `readSmallInteger`. Reads a signed 16-bit integer. | Yes |
| `readInteger` | `address: string` | `number` | Identical to CE. Reads a signed 32-bit integer. | Yes |
| `readQword` | `address: string` | `number` | Identical to CE. Reads a 64-bit integer. Lua numbers are doubles, so precision loss is possible above 2^53. | No |
| `readFloat` | `address: string` | `number` | Identical to CE. Reads a 32-bit IEEE 754 float. | Yes |
| `readDouble` | `address: string` | `number` | Identical to CE. Reads a 64-bit IEEE 754 double. | No |
| `readBytes` | `address: string, count: int` | `table` | Returns a 1-indexed Lua table of byte values (as numbers). CE returns the same format. | Yes |
| `readString` | `address: DynValue, maxLen: DynValue, wide: DynValue` | `string` | Reads a null-terminated string. `maxLen` defaults to 256 if nil. If `wide` is true, reads as UTF-16 (Unicode). CE signature is `readString(address, maxLen, wide)`. | No |

### Memory Write Functions

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `writeByte` | `address: string, value: DynValue` | `void` | Identical to CE. Writes a single byte. | Yes |
| `writeSmallInteger` | `address: string, value: DynValue` | `void` | Identical to CE. Writes a 16-bit integer. | No |
| `writeInteger` | `address: string, value: DynValue` | `void` | Identical to CE. Writes a 32-bit integer. | Yes |
| `writeQword` | `address: string, value: DynValue` | `void` | Identical to CE. Writes a 64-bit integer. | No |
| `writeFloat` | `address: string, value: DynValue` | `void` | Identical to CE. Writes a 32-bit float. | Yes |
| `writeDouble` | `address: string, value: DynValue` | `void` | Identical to CE. Writes a 64-bit double. | No |
| `writeBytes` | `address: string, bytes: table` | `void` | Takes a 1-indexed Lua table of byte values. Identical to CE. | No |

### Address Functions

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `getAddress` | `expression: string` | `number \| nil` | Resolves hex literals, module+offset (`game.dll+0x100`), registered symbols, and pointer chains (`[game.dll+0x100]+0x10`). Returns nil instead of 0 on failure (CE returns 0). | Yes |
| `getModuleBaseAddress` | `moduleName: string` | `number \| nil` | Returns the base address of the named module. Returns nil if not found (CE returns 0). | Yes |

### Symbol Functions

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `registerSymbol` | `name: string, address: number\|string` | `void` | Registers a symbol both as a Lua global and in the Auto Assembler symbol table. CE only registers in its own symbol table. Accepts hex string or number. | Yes |
| `unregisterSymbol` | `name: string` | `void` | Clears the Lua global and removes from AA symbol table. | Yes |

### Process Functions

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `openProcess` | `nameOrPid: string\|number` | `number` | Accepts process name (string) or PID (number). Returns the PID. Throws if process not found (CE shows a dialog). | No |
| `getOpenedProcessID` | _(none)_ | `number \| nil` | Returns the currently attached process ID, or nil if none. CE returns 0 if no process. | Yes |
| `getProcessId` | _(none)_ | `number \| nil` | Alias for `getOpenedProcessID`. Not a standard CE function name but works identically. | Yes |
| `getProcessList` | _(none)_ | `table` | Returns a table of `{id=number, name=string}` entries. CE returns two separate tables (PIDs and names). Structure differs from CE. | Yes |

### Auto Assembler Functions

These are only registered when an `IAutoAssemblerEngine` is provided.

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `autoAssemble` | `script: string` | `boolean` | Assembles and enables the given AA script. Returns true/false. CE's `autoAssemble` has additional optional parameters (targetSelf, disableInfo) that are not supported. | Yes |
| `autoAssembleCheck` | `script: string` | `boolean` | Validates (parses) an AA script without executing it. Returns true if valid. Not a standard CE function -- CE uses `autoAssemble` with a check parameter. | Yes |

### Utility Functions

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `print` | `...args` | `void` | Tab-separated output via `OutputWritten` event. Identical to CE behavior. | Yes |
| `sleep` | `ms: int` | `void` | Sleeps the script thread. **Capped at 10 seconds** to prevent hangs. CE has no cap. | Yes |
| `getTickCount` | _(none)_ | `number` | Returns `Environment.TickCount64`. Identical purpose to CE's `getTickCount()`. | Yes |
| `showMessage` | `msg: string` | `void` | Shows a message dialog via `ILuaFormHost`, or falls back to `print()` if no form host. CE always shows a Windows MessageBox. | Yes |
| `inputQuery` | `title: string, prompt: string, default: DynValue` | `string \| nil` | Shows an input dialog. Returns the entered string or nil if cancelled. Requires a form host (throws in headless mode). CE always shows a dialog. | No |
| `stringToHex` | `str: string` | `string` | Converts ASCII string to space-separated hex bytes (e.g., `"AB"` -> `"41 42"`). Identical to CE. | Yes |
| `hexToString` | `hex: string` | `string` | Converts space-separated hex bytes to ASCII string. Identical to CE. | Yes |

### Form/GUI Functions (CeFormBindings)

These are only registered when an `ILuaFormHost` is provided (GUI mode).

| Function | Parameters | Return Type | CE Compatibility Notes | Tested |
|---|---|---|---|---|
| `createForm` | `visible: DynValue` | `table (form)` | Creates a form (400x300, titled "Lua Form"). Returns a table with `show()`, `hide()`, `close()`, `setCaption(str)`, `setSize(w,h)` methods. CE's `createForm` returns an actual Form object with many more properties. | No |
| `createButton` | `formTable: table` | `table (element)` | Creates a button element on the specified form. Returns a table with `setCaption()`, `setPosition(x,y)`, `setSize(w,h)`. Supports `onClick` callback via metatable. CE's version uses Delphi component model. | No |
| `createLabel` | `formTable: table` | `table (element)` | Creates a label on the form. Same method set as button. | No |
| `createEdit` | `formTable: table` | `table (element)` | Creates a text input (edit box). Additional methods: `getText()`, `setText(str)`. | No |
| `createCheckBox` | `formTable: table` | `table (element)` | Creates a checkbox. Additional methods: `getChecked()`, `setChecked(bool)`. | No |
| `createTimer` | `formTable: table, interval: DynValue` | `table (element)` | Creates a timer on the form. Additional methods: `setInterval(ms)`, `setEnabled(bool)`. Supports `onTimer` callback via metatable. CE's `createTimer` works similarly. | No |

**Form element common methods:** All form elements support `setCaption(str)`, `setPosition(x, y)`, `setSize(w, h)`, and callback assignment via `element.onClick = function() ... end` or `element.onTimer = function() ... end`.

---

## Missing Functions

The following are major Cheat Engine Lua API functions that are **not implemented** in CE AI Suite.

### Memory Scan / Foundlist

| Function | Description |
|---|---|
| `AOBScan` | Array of Bytes scan -- scan entire process memory for a byte pattern |
| `AOBScanModule` | AOB scan within a specific module |
| `createMemScan` | Creates a MemScan object for advanced scanning |
| `getCurrentScanItems` | Returns foundlist results from the current scan |
| `getScanResults` | Gets scan result addresses |

### Memory Record / Address List

| Function | Description |
|---|---|
| `getAddressList` | Returns the cheat table's address list |
| `addresslist_getMemoryRecord` | Get a specific memory record from the address list |
| `memoryrecord_getValue` | Read a memory record's current value |
| `memoryrecord_setValue` | Write a value to a memory record |
| `memoryrecord_getAddress` | Get the address of a memory record |
| `memoryrecord_setActive` | Enable/disable a memory record (freeze) |
| `memoryrecord_getDescription` | Get the description of a memory record |

### Debugger

| Function | Description |
|---|---|
| `debug_setBreakpoint` | Set a hardware/software breakpoint |
| `debug_removeBreakpoint` | Remove a breakpoint |
| `debug_continueFromBreakpoint` | Continue execution after a breakpoint hit |
| `debug_getBreakpointList` | List all active breakpoints |
| `debug_setLastChanceExceptionHandler` | Set an exception handler callback |

> **Note:** CE AI Suite has a separate `BreakpointService` with Lua callback support (`RegisterBreakpointCallback`, `InvokeBreakpointCallbackAsync`), but these are not exposed as standard CE Lua functions.

### Disassembler

| Function | Description |
|---|---|
| `disassemble` | Disassemble an instruction at an address |
| `getInstructionSize` | Get the byte size of an instruction |
| `getPreviousOpcode` | Get the address of the previous instruction |
| `splitDisassembledString` | Parse a disassembly string into components |

### Structure / Class

| Function | Description |
|---|---|
| `createStructure` | Create a structure definition |
| `structure_addElement` | Add an element to a structure |
| `structToString` | Convert a structure definition to string |

### Cheat Table

| Function | Description |
|---|---|
| `getCheatTable` | Get the current cheat table object |
| `loadTable` | Load a .CT cheat table file |
| `saveTable` | Save the current cheat table |

### Thread / Execution

| Function | Description |
|---|---|
| `createThread` | Create a new thread for script execution |
| `injectDLL` | Inject a DLL into the target process |
| `executeCode` | Execute machine code in the target process |
| `allocateSharedMemory` | Allocate memory shared between CE and target |

### Memory Allocation / Protection

| Function | Description |
|---|---|
| `allocateMemory` | Allocate memory in the target process (VirtualAllocEx) |
| `deAllocateMemory` | Free allocated memory in the target process |
| `setMemoryProtection` | Change memory protection flags (VirtualProtectEx) |
| `virtualQueryEx` | Query memory region information |
| `getRegionInfo` | Get memory region info for an address |

### Advanced GUI / Graphics

| Function | Description |
|---|---|
| `createImage` | Create an image control |
| `createMemo` | Create a multi-line text control |
| `createListBox` | Create a list box control |
| `createComboBox` | Create a combo box / dropdown |
| `createTrackBar` | Create a slider/trackbar |
| `createProgressBar` | Create a progress bar |
| `createPanel` | Create a panel container |
| `createGroupBox` | Create a group box |
| `createRadioGroup` | Create a radio button group |
| `createDialog` | Create a dialog form |
| `createMenu` / `createMainMenu` / `createPopupMenu` | Menu creation functions |
| `canvas_*` | Canvas drawing operations (line, rect, text, etc.) |

### D3D / Overlay

| Function | Description |
|---|---|
| `d3dhook_initializeHook` | Initialize Direct3D hook for overlays |
| `d3dhook_createOverlay` | Create a D3D overlay |
| `d3dhook_createTextContainer` | Create text on the D3D overlay |
| `d3dhook_createImageContainer` | Create an image on the D3D overlay |

### Module / Symbol (Extended)

| Function | Description |
|---|---|
| `enumModules` | Enumerate all loaded modules with details |
| `getNameFromAddress` | Get the symbol name for an address |
| `getAddressFromName` | Alias-style lookup (uses symbol/export tables) |
| `getSymbolListFromFile` | Load symbols from a file |

### Miscellaneous

| Function | Description |
|---|---|
| `beep` | Play a system beep |
| `playSound` | Play a sound file |
| `getScreenWidth` / `getScreenHeight` | Get screen dimensions |
| `messageDialog` | Show a message dialog with custom buttons |
| `speedhack_setSpeed` | Set the speedhack multiplier |
| `getAutoAttachList` | Get the auto-attach process list |
| `closeCE` | Close Cheat Engine |
| `getCEVersion` | Get the CE version string |
| `getComment` | Get a comment for an address |
| `setComment` | Set a comment for an address |
| `shellExecute` | Open a file/URL via shell |
| `writeRegionToFile` / `readRegionFromFile` | Save/load memory regions to/from files |

---

## Sandbox Restrictions

### Lua Standard Library Modules

CE AI Suite uses MoonSharp's `Preset_HardSandbox` plus explicit additions. The following table shows availability:

| Module | Available | Notes |
|---|---|---|
| `string` | Yes | Full string library (`string.format`, `string.find`, `string.gsub`, etc.) |
| `math` | Yes | Full math library (`math.floor`, `math.sin`, `math.random`, etc.) |
| `table` | Yes | Full table library (`table.sort`, `table.insert`, `table.concat`, etc.) |
| `bit32` | Yes | Explicitly added. Bitwise operations (`bit32.band`, `bit32.bor`, `bit32.lshift`, `bit32.rshift`, etc.) |
| `coroutine` | Yes | Explicitly added. Full coroutine support (`coroutine.create`, `coroutine.resume`, `coroutine.yield`, etc.) |
| `print` | Yes | Custom implementation routed through `OutputWritten` event |
| `pcall` / `xpcall` | Yes | Error handling via protected calls |
| `error` | Yes | Raise errors |
| `type` / `tostring` / `tonumber` | Yes | Basic type functions |
| `select` / `unpack` / `pairs` / `ipairs` | Yes | Iteration and vararg utilities |
| `setmetatable` / `getmetatable` | Yes | Metatable manipulation |
| `os` | **Blocked** | No access to OS functions (`os.execute`, `os.clock`, `os.date`, etc.) |
| `io` | **Blocked** | No file I/O (`io.open`, `io.read`, `io.write`, etc.) |
| `loadfile` / `dofile` | **Blocked** | Cannot load external Lua files |
| `require` | **Blocked** | Cannot load external modules |
| `debug` (Lua library) | **Blocked** | No access to the Lua debug library |
| `package` | **Blocked** | No dynamic module loading |

### Execution Limits

| Limit | Default | Configurable |
|---|---|---|
| **Execution timeout** | 30 seconds | Yes, via `executionTimeout` constructor parameter |
| **Instruction limit** | Unlimited (0) | Yes, via `MaxInstructions` property. When set, a debugger hook counts each bytecode instruction and throws `ScriptRuntimeException` once exceeded. |
| **Sleep cap** | 10 seconds max per `sleep()` call | No (hardcoded) |
| **Concurrency** | Single-threaded (semaphore-gated) | No. Only one script executes at a time. |
| **input() function** | Disabled | Always throws `ScriptRuntimeException` |

---

## Migration Guide: Porting CE Lua Scripts to CE AI Suite

### What Works Out of the Box

Scripts that use only the following patterns should work without modification:

- **Basic memory read/write:** `readInteger`, `writeFloat`, `readBytes`, etc.
- **Address resolution:** `getAddress('module.dll+0x100')`, pointer chains `[addr]+offset`
- **Symbol management:** `registerSymbol` / `unregisterSymbol`
- **Auto assembly:** `autoAssemble` with `[ENABLE]`/`[DISABLE]` blocks
- **Basic logic:** All standard Lua control flow, string/math/table libraries, bit32 operations
- **Output:** `print()` for console output
- **Timing:** `sleep()` (capped at 10s), `getTickCount()`

### Common Adjustments Needed

1. **`getProcessList` return format differs.** CE returns two parallel tables (PIDs, names). CE AI Suite returns a single table of `{id=N, name="..."}` objects. Adjust iteration accordingly:
   ```lua
   -- CE style (won't work):
   -- local pids, names = getProcessList()

   -- CE AI Suite style:
   local list = getProcessList()
   for i, p in ipairs(list) do
     print(p.id .. " " .. p.name)
   end
   ```

2. **`getAddress` / `getModuleBaseAddress` return nil on failure.** CE returns 0. Check for nil instead of 0:
   ```lua
   -- CE style:
   -- if getAddress("sym") == 0 then ...

   -- CE AI Suite style:
   if getAddress("sym") == nil then ...
   ```

3. **`sleep()` is capped at 10 seconds.** Scripts with `sleep(60000)` will only sleep for 10 seconds. Restructure long waits into loops if needed.

4. **`autoAssemble` has no `targetSelf` or `disableInfo` parameters.** Only the script string is accepted.

5. **`showMessage` falls back to `print()` in headless mode.** If your script relies on a blocking message dialog, it may not pause execution as expected outside GUI mode.

### Functions That Need Workarounds

| CE Function | Workaround in CE AI Suite |
|---|---|
| `AOBScan` / `AOBScanModule` | Not available. Use the AI operator or engine-level scan APIs outside of Lua. |
| `createForm` (complex GUI) | Basic form support is available (`createForm`, `createButton`, `createLabel`, `createEdit`, `createCheckBox`, `createTimer`). For complex UIs, use the CE AI Suite WPF interface instead. |
| `createImage` / `canvas_*` | No graphical rendering. Use the application UI layer for visualizations. |
| `d3dhook_*` | No D3D overlay support. Use the application's overlay features if available. |
| `debug_setBreakpoint` | Use `BreakpointService` through the application layer. Lua callback registration is available via `RegisterBreakpointCallback`. |
| `disassemble` | Not available. Use the engine-level disassembly APIs outside of Lua. |
| `allocateMemory` / `deAllocateMemory` | Not available directly. Auto Assembler scripts can use `alloc()` directives. |
| `getAddressList` / memory records | No cheat table concept. Use the application's address management features. |
| `os.clock` / `os.date` | Blocked by sandbox. Use `getTickCount()` for timing. |
| `io.open` / file operations | Blocked by sandbox. File operations must be handled at the application layer. |
| `speedhack_setSpeed` | Not available. |

### Architecture Differences

- **No cheat table model.** CE AI Suite does not have a `.CT` file format or cheat table UI. Memory operations are done directly via API calls or through the AI operator.
- **Sandboxed execution.** CE runs Lua with full OS access. CE AI Suite blocks OS, IO, file loading, and the debug library for security.
- **Async-over-sync bridging.** All CE API bindings in CE AI Suite call `.GetAwaiter().GetResult()` internally on async engine operations. This is transparent to Lua scripts but means the calling thread blocks during memory reads/writes.
- **Form rendering.** CE uses Delphi's VCL component system. CE AI Suite renders Lua-created forms via WPF through the `ILuaFormHost` interface. Visual appearance and behavior may differ.

---

## Summary Statistics

| Category | Count |
|---|---|
| **Total registered Lua globals** | 38 (37 unique + 1 alias) |
| **CeApiBindings functions** | 31 (8 memory read, 7 memory write, 4 process, 4 address/symbol, 2 auto assembler, 6 utility) |
| **CeFormBindings functions** | 6 (createForm, createButton, createLabel, createEdit, createCheckBox, createTimer) |
| **Engine-level** | 1 (print) |
| **Functions with test coverage** | 24 (including `print` and `getProcessId` alias) |
| **Functions without test coverage** | 14 (`readQword`, `readDouble`, `readString`, `writeSmallInteger`, `writeQword`, `writeDouble`, `writeBytes`, `inputQuery`, `openProcess`, all 6 form functions -- though `openProcess` is indirectly tested via `getProcessList`) |
| **Major missing CE function categories** | 12+ (scanning, memory records, debugger, disassembler, structures, cheat tables, threads, memory allocation, advanced GUI, D3D, module enumeration, misc) |
