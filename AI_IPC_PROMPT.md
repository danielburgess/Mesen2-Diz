# Mesen2-Diz IPC

## Emulator app location
```
/mnt/crucial/projects/Mesen2-Diz/bin/linux-x64/Release/linux-x64/publish/Mesen
```

## Connection
Pipe name: auto from ROM (e.g. `Mesen2Diz_SuperMetroid`). Override via config.
- Linux: `/tmp/CoreFxPipe_{pipeName}`
- Windows: `\\.\pipe\{pipeName}`
- Default (no ROM): `Mesen2Diz_DebuggerIpc`

Protocol: line-based JSON. One `{"command":"X",...}\n` per request, one JSON response per line.
Response: `{"success":true,"data":...}` or `{"success":false,"error":"..."}`

Use `getIpcInfo` to discover current pipe name + platform path at runtime.

## Address Format
Hex prefix: `"0x8000"`, `"$8000"`. Decimal: `32768`. Bare hex: `"8000"`.
All response addresses: uppercase no prefix (`"008000"`).

## Types

### MemoryType
`SnesMemory` (CPU mapped) | `SnesPrgRom` (absolute, for labels) | `SnesWorkRam` (128K) | `SnesSaveRam` | `SnesVideoRam` (64K) | `SnesSpriteRam` (544B) | `SnesCgRam` (512B) | `SnesRegister` ($2100-$43FF) | `SpcRam` (64K) | `SpcRom` (64B)

### CpuType
`Snes` | `Spc` | `Sa1` | `Gsu`

### FunctionCategory
`None` `Init` `MainLoop` `Interrupt` `DMA` `Input` `Player` `OAM` `VRAM` `Tilemap` `Palette` `Scrolling` `Animation` `Effects` `Mode7` `Music` `SFX` `Physics` `Collision` `Entity` `Enemy` `AI` `Camera` `StateMachine` `GameState` `Menu` `HUD` `LevelLoad` `Transition` `Script` `Dialogue` `Math` `RNG` `Timer` `Memory` `Text` `Save` `Debug` `Unused` `Unknown` `Helper`

### StepType
`Step` `StepOut` `StepOver` `PpuFrame` `RunToNmi` `RunToIrq` `StepBack`

### StepBackUnit (for stepTrace with StepBack)
`Instruction` (default) | `Scanline` | `Frame`

### CheatType
`SnesGameGenie` `SnesProActionReplay` `NesGameGenie` `NesProActionRocky` `NesCustom` `GbGameGenie` `GbGameShark` `PceRaw` `PceAddress` `SmsProActionReplay` `SmsGameGenie`

## Commands

### Labels
| Command | Params | Notes |
|---------|--------|-------|
| `setLabel` | address, memoryType, label?, comment?, category?, length?(1) | Create/update. Returns warning if no category set |
| `setLabels` | labels:[{address, memoryType, label?, comment?, category?, length?},...] | Batch create/update. Returns count + results array |
| `deleteLabel` | address, memoryType | |
| `getLabel` | address, memoryType | Returns null data if not found |
| `getLabelByName` | name | |
| `getAllLabels` | cpuType? | Filter by CPU or get all |

Label names: `^[@_a-zA-Z]+[@_a-zA-Z0-9]*$`. Comments support `\n`.
All labels set via IPC are marked with an IPC flag (visible in label/function list as green dot, sortable).
**Always set a category** — omitting it triggers a warning in the response.

### Memory
| Command | Params | Notes |
|---------|--------|-------|
| `readMemory` | memoryType, address, length?(1) | Max 65536. Returns hex + bytes array |
| `writeMemory` | memoryType, address, hex\|values | hex: `"FF 00 42"`, values: `[255,0,66]` |
| `getMemorySize` | memoryType | |

### CPU State
| Command | Params | Notes |
|---------|--------|-------|
| `getCpuState` | cpuType?(Snes) | SNES: a,x,y,sp,d,pc,k,dbr,flags,emulationMode,cycleCount |
| `setCpuState` | cpuType?(Snes), a?, x?, y?, sp?, d?, dbr?, k?, pc?, flags?, emulationMode? | Partial update — only provided fields change. Returns full state |
| `getProgramCounter` | cpuType?(Snes) | |
| `setProgramCounter` | cpuType?(Snes), address | |

### Execution Control
| Command | Params | Notes |
|---------|--------|-------|
| `pause` | | |
| `resume` | | |
| `isPaused` | | Returns `{"paused":bool}` |
| `step` | cpuType?(Snes), count?(1), stepType?(Step) | Fire-and-forget. For StepBack: count = StepBackType (0=Instruction,1=Scanline,2=Frame) |
| `stepTrace` | cpuType?(Snes), count?(1), stepType?(Step), stepBackUnit?(Instruction) | Returns CPU state after **each** step. Max 500. `states` array in response |

### Disassembly
| Command | Params | Notes |
|---------|--------|-------|
| `getDisassembly` | cpuType?(Snes), address, rows?(20) | Max 500 rows |
| `searchDisassembly` | cpuType?(Snes), search, startAddress?(0) | Returns address or found=false |

### Breakpoints
| Command | Params | Notes |
|---------|--------|-------|
| `addBreakpoint` | address, memoryType, cpuType?(Snes), endAddress?, breakOnExec?(true), breakOnRead?(false), breakOnWrite?(false), condition?, enabled?(true) | |
| `removeBreakpoint` | address, memoryType, cpuType?(Snes) | |
| `getBreakpoints` | cpuType?(Snes) | |
| `clearBreakpoints` | | |

### Expression Evaluation
| Command | Params | Notes |
|---------|--------|-------|
| `evaluate` | expression, cpuType?(Snes) | Supports registers (`A`,`X`,`Y`,`PC`,`SP`), memory reads (`[$7E0100]`), arithmetic |

### Call Stack
| Command | Params | Notes |
|---------|--------|-------|
| `getCallstack` | cpuType?(Snes) | Array of {source,target,returnAddress,flags} |

### Code/Data Log (CDL)
| Command | Params | Notes |
|---------|--------|-------|
| `getCdlData` | memoryType, address, length?(1) | Max 65536 |
| `getCdlStatistics` | memoryType | codeBytes, dataBytes, totalBytes |
| `getCdlFunctions` | memoryType | Array of function entry point addresses |

### Address Mapping
| Command | Params | Notes |
|---------|--------|-------|
| `getAbsoluteAddress` | address, memoryType | CPU→ROM. Null data if unmapped |
| `getRelativeAddress` | address, memoryType, cpuType?(Snes) | ROM→CPU |

### ROM Info & Status
| Command | Params | Notes |
|---------|--------|-------|
| `getRomInfo` | | romPath, format, consoleType, cpuTypes |
| `getStatus` | | running, paused, romLoaded, romPath, consoleType, cpuState |

### Screenshot
| Command | Params | Notes |
|---------|--------|-------|
| `takeScreenshot` | path? | No path = default location |

### Emulator Control
| Command | Params | Notes |
|---------|--------|-------|
| `loadRom` | path, patchPath? | Async — poll `getStatus` |
| `reloadRom` | | |
| `powerCycle` | | Cold boot, RAM wiped |
| `powerOff` | | Stops emulation |
| `reset` | | Soft reset, RAM preserved |

### Save States
| Command | Params | Notes |
|---------|--------|-------|
| `saveStateSlot` | slot(1-10) | |
| `loadStateSlot` | slot(1-10) | |
| `saveStateFile` | path | Absolute path |
| `loadStateFile` | path | |

### Controller Input
| Command | Params | Notes |
|---------|--------|-------|
| `setControllerInput` | port?(0), buttons:{a,b,x,y,l,r,up,down,left,right,select,start} | All bool. Unset=false. **Persists** until changed |
| `clearControllerInput` | port?(0) | Release all |

Tap pattern: set → step PpuFrame ×N → clear.

### Emulation Settings
| Command | Params | Notes |
|---------|--------|-------|
| `getEmulationSpeed` | | 0=unlimited, 100=normal |
| `setEmulationSpeed` | speed(0-5000) | Applied immediately |
| `getTurboSpeed` | | |
| `setTurboSpeed` | speed(0-5000) | |
| `getRunAheadFrames` | | |
| `setRunAheadFrames` | frames(0-10) | |
| `getConfig` | | All emu settings in one call |

### Timing & PPU
| Command | Params | Notes |
|---------|--------|-------|
| `getTimingInfo` | cpuType?(Snes) | fps, masterClock, masterClockRate, frameCount, scanlineCount, firstScanline, cycleCount |
| `getPpuState` | cpuType?(Snes) | SNES: cycle, scanline, hClock, frameCount, forcedBlank, screenBrightness, bgMode, mode1Bg3Priority, mainScreenLayers, subScreenLayers, vramAddress |

### IPC Info
| Command | Params | Notes |
|---------|--------|-------|
| `getIpcInfo` | | pipeName, pipePath, romPath, platform |

### Cheats
| Command | Params | Notes |
|---------|--------|-------|
| `setCheats` | cheats:[{type,code},...] | See CheatType enum. Replaces all active cheats |
| `clearCheats` | | Remove all |

## Key Rules
- Labels use **absolute** addresses (SnesPrgRom). Use `getAbsoluteAddress` to convert CPU addresses.
- **Pause before** reading CPU state/memory for consistency. Resume when done.
- Controller input **persists** — always clear when done.
- `loadRom`/`powerCycle`/`reset` are async — poll `getStatus`.
- Save state slots: 1-10. File paths: absolute.
- IPC connection persists across ROM reloads by default. No reconnection needed.

## Debugging Techniques

### Reverse Stepping
Execution can be **reversed**. The debugger records history and can step backward:
- `stepType: "StepBack"` with `stepBackUnit: "Instruction"` — undo one instruction
- `stepType: "StepBack"` with `stepBackUnit: "Scanline"` — rewind one PPU scanline
- `stepType: "StepBack"` with `stepBackUnit: "Frame"` — rewind one full frame

Use `stepTrace` to step back N times and receive CPU state at each point. This is invaluable for understanding how a value was computed or how execution reached a particular state.

### Forcing Conditions via CPU State
You can **modify any CPU register, flag, or memory** to force specific execution paths:
- `setCpuState` — change A, X, Y, SP, D, DBR, K, PC, flags, emulationMode (partial: only fields you provide are changed)
- `writeMemory` to SnesWorkRam — modify stack contents, variables, or any RAM
- `setProgramCounter` — jump execution to any address
- Combine: set registers + flags + PC to simulate any entry condition for a function

Example — force a branch: pause, read flags, set/clear the Zero flag via `setCpuState`, step to observe the alternate path.

Example — test a function: set A/X/Y to desired arguments, set PC to function entry, step through to observe behavior.

### Breakpoint-Driven Analysis
Breakpoints trigger asynchronously. The emulator pauses when hit, but the IPC response to `addBreakpoint` returns immediately — it does **not** wait for the break to occur.

Workflow:
1. `addBreakpoint` — set the trap (exec, read, or write; with optional condition expression)
2. `resume` — let emulation run
3. **Poll** `isPaused` or `getStatus` periodically to detect when a break occurs
4. Once paused: `getCpuState`, `getCallstack`, `getDisassembly`, `readMemory` to inspect
5. `stepTrace` to walk through code instruction by instruction with full state at each step
6. `resume` to continue, or step further

Conditional breakpoints use the expression evaluator: registers (`A`, `X`, `Y`, `PC`, `SP`), memory reads (`[$7E0100]`), arithmetic, comparisons. Example: `"condition": "A == #$42 && [$7E0010] > #$00"`.

### Execution State Awareness
- `step` is **fire-and-forget** — it tells the debugger to step, but the step may not complete before the response arrives (the CPU resumes briefly then breaks)
- `stepTrace` is **synchronous** — it steps and reads CPU state in a tight loop, returning all states in one response. Use this for tracing.
- After `step`, poll `isPaused` before reading state. After `stepTrace`, states are already in the response.
- `StepOver` skips subroutine calls (JSR/JSL). `StepOut` runs until the current subroutine returns.

## Annotation Guidelines
- Follow the user's instructions on what to annotate and how to categorize.
- **Always annotate the base ROM** — the original, unmodified ROM file. Annotations describe the original game code, not patched/hacked variants.
- When new discoveries are made (function purpose identified, data table decoded, variable meaning understood), immediately update labels and comments via `setLabel`.
- Use `category` to classify functions (see FunctionCategory enum). This helps organize the codebase.
- Add comments explaining **why**, not just what — "Checks if player is underwater" is better than "Compares A to #$03".
- Label names must match `^[@_a-zA-Z]+[@_a-zA-Z0-9]*$`. Use descriptive names: `Player_CheckCollision`, `LoadTilemap_BG1`, `SFX_PlaySound`.

## Reverse Engineering Workflow
1. `getStatus` → confirm ROM loaded
2. `getCdlFunctions` → all known entry points
3. `getAllLabels` → existing annotations
4. `getDisassembly` at each function → read code
5. `setLabel` → name functions, add comments, set category
6. `readMemory` → examine data tables, RAM
7. `addBreakpoint` + `resume` + poll `isPaused` → wait for condition
8. `stepTrace` → walk through code with full CPU state at each step
9. `setCpuState` / `writeMemory` → force conditions to test alternate paths
10. `step` with `StepBack` → reverse execution to understand causality
11. `getAbsoluteAddress`/`getRelativeAddress` → address conversion
