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

### CheatType
`SnesGameGenie` `SnesProActionReplay` `NesGameGenie` `NesProActionRocky` `NesCustom` `GbGameGenie` `GbGameShark` `PceRaw` `PceAddress` `SmsProActionReplay` `SmsGameGenie`

## Commands

### Labels
| Command | Params | Notes |
|---------|--------|-------|
| `setLabel` | address, memoryType, label?, comment?, category?, length?(1) | Create/update |
| `deleteLabel` | address, memoryType | |
| `getLabel` | address, memoryType | Returns null data if not found |
| `getLabelByName` | name | |
| `getAllLabels` | cpuType? | Filter by CPU or get all |

Label names: `^[@_a-zA-Z]+[@_a-zA-Z0-9]*$`. Comments support `\n`.

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
| `getProgramCounter` | cpuType?(Snes) | |
| `setProgramCounter` | cpuType?(Snes), address | |

### Execution Control
| Command | Params | Notes |
|---------|--------|-------|
| `pause` | | |
| `resume` | | |
| `isPaused` | | Returns `{"paused":bool}` |
| `step` | cpuType?(Snes), count?(1), stepType?(Step) | |

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

## Reverse Engineering Workflow
1. `getStatus` → confirm ROM loaded
2. `getCdlFunctions` → all known entry points
3. `getAllLabels` → existing annotations
4. `getDisassembly` at each function → read code
5. `setLabel` → name functions, add comments, set category
6. `readMemory` → examine data tables, RAM
7. `addBreakpoint` + `step` + `getCpuState` + `getCallstack` → dynamic analysis
8. `getAbsoluteAddress`/`getRelativeAddress` → address conversion
