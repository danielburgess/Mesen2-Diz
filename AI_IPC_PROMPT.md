# Mesen2-Diz Debugger IPC Interface

You have access to a live SNES emulator/debugger (Mesen2-Diz) via a named pipe IPC interface. You can read/write memory, manage labels and comments, control execution, set breakpoints, read disassembly, inspect CPU state, load ROMs, save/load states, and inject controller input in real time.

## Connection

Connect to the named pipe `Mesen2Diz_DebuggerIpc`. The protocol is line-based JSON: send one JSON object per line, receive one JSON response per line.

**Python example:**
```python
import json, struct

# On Windows:
pipe_path = r'\\.\pipe\Mesen2Diz_DebuggerIpc'
pipe = open(pipe_path, 'r+b', buffering=0)

# On Linux:
# Use socket or named pipe client

def send_command(command, **params):
    msg = json.dumps({"command": command, **params}) + "\n"
    pipe.write(msg.encode())
    pipe.flush()
    response = pipe.readline()
    return json.loads(response)
```

**All responses** have the shape:
```json
{"success": true, "data": ...}
```
or on error:
```json
{"success": false, "error": "description"}
```

## Address Format

Addresses can be provided as:
- Hex string with prefix: `"0x8000"` or `"$8000"`
- Decimal integer: `32768`
- Bare hex string: `"8000"` (interpreted as hex if not a valid decimal)

## Memory Types

Common SNES memory types you will use:

| Name | Description |
|------|-------------|
| `SnesMemory` | CPU address space (relative/mapped, $00:0000-$FF:FFFF) |
| `SnesPrgRom` | PRG ROM (absolute, used for labels) |
| `SnesWorkRam` | WRAM (128KB) |
| `SnesSaveRam` | SRAM / battery-backed RAM |
| `SnesVideoRam` | VRAM (64KB) |
| `SnesSpriteRam` | OAM (544 bytes) |
| `SnesCgRam` | Palette RAM (512 bytes) |
| `SnesRegister` | Hardware registers ($2100-$43FF range) |
| `SpcRam` | SPC700 RAM (64KB) |
| `SpcRom` | SPC700 IPL ROM (64 bytes) |

## CPU Types

| Name | Description |
|------|-------------|
| `Snes` | Main 65816 CPU |
| `Spc` | SPC700 audio CPU |
| `Sa1` | SA-1 coprocessor |
| `Gsu` | Super FX |

## Function Categories

When setting labels, you can classify functions with a `category` field:

`None`, `Init`, `MainLoop`, `Interrupt`, `DMA`, `Input`, `Player`, `OAM`, `VRAM`, `Tilemap`, `Palette`, `Scrolling`, `Animation`, `Effects`, `Mode7`, `Music`, `SFX`, `Physics`, `Collision`, `Entity`, `Enemy`, `AI`, `Camera`, `StateMachine`, `GameState`, `Menu`, `HUD`, `LevelLoad`, `Transition`, `Script`, `Dialogue`, `Math`, `RNG`, `Timer`, `Memory`, `Text`, `Save`, `Debug`, `Unused`, `Unknown`, `Helper`

---

## Command Reference

### Labels & Comments

#### setLabel
Create or update a label and/or comment at an address.
```json
{
  "command": "setLabel",
  "address": "0x8000",
  "memoryType": "SnesPrgRom",
  "label": "ResetVector",
  "comment": "Entry point after reset",
  "category": "Init",
  "length": 1
}
```

#### deleteLabel
```json
{"command": "deleteLabel", "address": "0x8000", "memoryType": "SnesPrgRom"}
```

#### getLabel
```json
{"command": "getLabel", "address": "0x8000", "memoryType": "SnesPrgRom"}
```
Returns: `{"address":"008000","memoryType":"SnesPrgRom","label":"ResetVector","comment":"Entry point","length":1,"category":"Init"}`

#### getLabelByName
```json
{"command": "getLabelByName", "name": "ResetVector"}
```

#### getAllLabels
```json
{"command": "getAllLabels"}
{"command": "getAllLabels", "cpuType": "Snes"}
```

---

### Memory

#### readMemory
Read bytes from any memory region.
```json
{
  "command": "readMemory",
  "memoryType": "SnesMemory",
  "address": "0x7E0000",
  "length": 16
}
```
Returns: `{"address":"7E0000","length":16,"hex":"00 01 02 ...","bytes":[0,1,2,...]}`

#### writeMemory
Write bytes. Provide either `hex` or `values`.
```json
{"command": "writeMemory", "memoryType": "SnesWorkRam", "address": "0x0000", "hex": "FF 00 42"}
{"command": "writeMemory", "memoryType": "SnesWorkRam", "address": "0x0000", "values": [255, 0, 66]}
```

#### getMemorySize
```json
{"command": "getMemorySize", "memoryType": "SnesPrgRom"}
```
Returns: `{"memoryType":"SnesPrgRom","size":2097152}`

---

### CPU State

#### getCpuState
Get full register state. For SNES, returns A, X, Y, SP, D, PC, K (bank), DBR, flags.
```json
{"command": "getCpuState", "cpuType": "Snes"}
```
Returns:
```json
{
  "cpuType": "Snes",
  "a": "0042", "x": "0010", "y": "0000",
  "sp": "01FF", "d": "0000", "pc": "8000",
  "k": "00", "dbr": "00",
  "flags": "Carry, Zero, IndexMode8, MemoryMode8",
  "emulationMode": false,
  "cycleCount": 123456
}
```

#### getProgramCounter
```json
{"command": "getProgramCounter", "cpuType": "Snes"}
```

#### setProgramCounter
```json
{"command": "setProgramCounter", "cpuType": "Snes", "address": "0x008000"}
```

---

### Execution Control

#### pause
```json
{"command": "pause"}
```

#### resume
```json
{"command": "resume"}
```

#### isPaused
```json
{"command": "isPaused"}
```
Returns: `{"paused": true}`

#### step
Step execution. Step types: `Step`, `StepOut`, `StepOver`, `PpuFrame`, `RunToNmi`, `RunToIrq`, `StepBack`
```json
{"command": "step", "cpuType": "Snes", "count": 1, "stepType": "Step"}
{"command": "step", "cpuType": "Snes", "stepType": "StepOver"}
{"command": "step", "cpuType": "Snes", "stepType": "PpuFrame"}
```

---

### Disassembly

#### getDisassembly
Get disassembled code lines starting at an address.
```json
{"command": "getDisassembly", "cpuType": "Snes", "address": "0x8000", "rows": 30}
```
Returns array of:
```json
{
  "address": "008000",
  "absAddress": {"address": "000000", "memoryType": "SnesPrgRom"},
  "text": "SEI",
  "byteCode": "78",
  "comment": "",
  "flags": "..."
}
```

#### searchDisassembly
Find text in disassembly.
```json
{"command": "searchDisassembly", "cpuType": "Snes", "search": "JSR", "startAddress": "0x8000"}
```
Returns: `{"found": true, "address": "00802A"}`

---

### Breakpoints

#### addBreakpoint
```json
{
  "command": "addBreakpoint",
  "address": "0x8000",
  "memoryType": "SnesPrgRom",
  "cpuType": "Snes",
  "breakOnExec": true,
  "breakOnRead": false,
  "breakOnWrite": false,
  "condition": "",
  "enabled": true
}
```

For a data watchpoint (break on write to a RAM address):
```json
{
  "command": "addBreakpoint",
  "address": "0x0100",
  "endAddress": "0x010F",
  "memoryType": "SnesWorkRam",
  "cpuType": "Snes",
  "breakOnWrite": true,
  "breakOnExec": false
}
```

#### removeBreakpoint
```json
{"command": "removeBreakpoint", "address": "0x8000", "memoryType": "SnesPrgRom", "cpuType": "Snes"}
```

#### getBreakpoints
```json
{"command": "getBreakpoints", "cpuType": "Snes"}
```

#### clearBreakpoints
```json
{"command": "clearBreakpoints"}
```

---

### Expression Evaluation

Evaluate debugger expressions using Mesen's expression syntax. Supports registers (`A`, `X`, `Y`, `PC`, `SP`), memory reads (`[$7E0100]`), comparisons, and arithmetic.

```json
{"command": "evaluate", "expression": "A + X", "cpuType": "Snes"}
{"command": "evaluate", "expression": "[$7E0100]", "cpuType": "Snes"}
```
Returns: `{"expression":"A + X","value":82,"hex":"52","resultType":"Numeric"}`

---

### Call Stack

```json
{"command": "getCallstack", "cpuType": "Snes"}
```
Returns array of: `{"source":"008042","target":"00A000","returnAddress":"008045","flags":"None"}`

---

### Code/Data Log (CDL)

CDL tracks which bytes have been executed as code vs read as data.

#### getCdlData
```json
{"command": "getCdlData", "memoryType": "SnesPrgRom", "address": "0x0000", "length": 16}
```

#### getCdlStatistics
```json
{"command": "getCdlStatistics", "memoryType": "SnesPrgRom"}
```
Returns: `{"codeBytes":12345,"dataBytes":6789,"totalBytes":2097152}`

#### getCdlFunctions
Get all function entry points detected by CDL.
```json
{"command": "getCdlFunctions", "memoryType": "SnesPrgRom"}
```
Returns: `["008000","00A000","00B200",...]`

---

### Address Mapping

Map between CPU-visible (relative) addresses and absolute ROM/RAM offsets.

#### getAbsoluteAddress
Convert a CPU (relative) address to an absolute ROM address.
```json
{"command": "getAbsoluteAddress", "address": "0x8000", "memoryType": "SnesMemory"}
```
Returns: `{"address":"000000","memoryType":"SnesPrgRom"}`

#### getRelativeAddress
Convert an absolute ROM offset to a CPU address.
```json
{"command": "getRelativeAddress", "address": "0x0000", "memoryType": "SnesPrgRom", "cpuType": "Snes"}
```
Returns: `{"address":"008000","memoryType":"SnesMemory"}`

---

### ROM Info & Status

#### getRomInfo
```json
{"command": "getRomInfo"}
```

#### getStatus
```json
{"command": "getStatus"}
```
Returns: `{"running":true,"paused":true,"romLoaded":true,"romPath":"...","consoleType":"Snes","cpuState":{"pc":"008000"}}`

---

### Screenshot

```json
{"command": "takeScreenshot"}
{"command": "takeScreenshot", "path": "/tmp/screen.png"}
```

---

### Emulator Control

Control ROM loading and power state remotely. All of these dispatch on a background task — if you need to guarantee the operation is complete before the next command, follow up with `getStatus` and poll.

#### loadRom
Load a ROM from an absolute file path. Optionally apply an IPS/UPS/BPS patch.
```json
{"command": "loadRom", "path": "/absolute/path/to/game.sfc"}
{"command": "loadRom", "path": "/roms/game.sfc", "patchPath": "/roms/game.ips"}
```

#### reloadRom
Reload the currently-loaded ROM from disk (picks up external edits).
```json
{"command": "reloadRom"}
```

#### powerCycle
Power-cycle the console (cold boot, RAM wiped).
```json
{"command": "powerCycle"}
```

#### powerOff
Stop emulation and unload the ROM.
```json
{"command": "powerOff"}
```

#### reset
Soft reset the console (like pressing the reset button; RAM preserved on most systems).
```json
{"command": "reset"}
```

---

### Save States

Save states capture full emulator state for later restore. Use numbered slots for quick checkpointing, or arbitrary file paths for named snapshots.

#### saveStateSlot
Save to numbered slot. `slot` must be 1-10.
```json
{"command": "saveStateSlot", "slot": 1}
```

#### loadStateSlot
Load from numbered slot.
```json
{"command": "loadStateSlot", "slot": 1}
```

#### saveStateFile
Save to an absolute file path.
```json
{"command": "saveStateFile", "path": "/tmp/checkpoint.mss"}
```

#### loadStateFile
Load from an absolute file path.
```json
{"command": "loadStateFile", "path": "/tmp/checkpoint.mss"}
```

---

### Controller Input Override

Inject controller input by overriding a specific port. The override **persists** in the emulator until you change it or call `clearControllerInput`. Button values are booleans — missing keys are treated as `false`. All button states in a `setControllerInput` call are applied at once (there is no partial merge; unspecified buttons are released).

Valid ports: `0-7` (port 0 = controller 1, port 1 = controller 2, etc.).

Button keys (all lowercase):
- **Face:** `a`, `b`, `x`, `y`
- **Shoulders:** `l`, `r`
- **D-pad:** `up`, `down`, `left`, `right`
- **System:** `select`, `start`
- **Reserved (system-specific):** `u`, `d`

#### setControllerInput
Press `A` and `Right` on controller 1:
```json
{
  "command": "setControllerInput",
  "port": 0,
  "buttons": {"a": true, "right": true}
}
```

Flat form (without the `buttons` sub-object) is also accepted:
```json
{"command": "setControllerInput", "port": 0, "a": true, "start": true}
```

To release everything on a port (all buttons false), send an empty button set:
```json
{"command": "setControllerInput", "port": 0, "buttons": {}}
```

**To simulate a button tap:** set the button down, wait / step a few frames, then release it. A typical pattern:
```python
send_command("setControllerInput", port=0, buttons={"start": True})
send_command("step", cpuType="Snes", stepType="PpuFrame", count=3)
send_command("setControllerInput", port=0, buttons={})
```

#### clearControllerInput
Release all buttons on a port (equivalent to an empty `setControllerInput`).
```json
{"command": "clearControllerInput", "port": 0}
```

---

## Recommended Workflow for Reverse Engineering

1. **Start**: Call `getStatus` to confirm a ROM is loaded and the debugger is active.
2. **Survey**: Use `getCdlFunctions` to get all known function entry points, then `getAllLabels` to see what's already labeled.
3. **Read code**: Use `getDisassembly` at each function address to read the instructions.
4. **Annotate**: Use `setLabel` to name functions and add comments explaining their purpose. Always set an appropriate `category`.
5. **Investigate data**: Use `readMemory` to examine data tables, RAM state, etc.
6. **Dynamic analysis**: Use `addBreakpoint` to set execution or data breakpoints, `step` to trace execution, `getCpuState` to inspect registers, and `getCallstack` to understand control flow.
7. **Map addresses**: Use `getAbsoluteAddress`/`getRelativeAddress` to convert between CPU and ROM addresses when needed.

## Important Notes

- Labels are set on **absolute** addresses (e.g., `SnesPrgRom`), not relative CPU addresses. Use `getAbsoluteAddress` to convert if needed.
- Label names must match the regex `^[@_a-zA-Z]+[@_a-zA-Z0-9]*$` (letters, digits, underscore, @).
- Comments support multi-line text (use `\n` for newlines).
- The emulator must be **paused** to read consistent CPU state and memory. Use `pause` before inspecting, `resume` when done.
- Memory reads are limited to 65536 bytes per call.
- Disassembly is limited to 500 rows per call.
- All hex addresses in responses are uppercase without a prefix (e.g., `"008000"` not `"0x008000"`).
- `loadRom`, `reloadRom`, `powerCycle`, `powerOff`, and `reset` return immediately — the operation runs asynchronously. Poll `getStatus` if you need to wait for completion.
- Controller input overrides **persist** until changed. Always clear them when done, or the game will see the buttons held forever.
- Save state slots are numbered **1-10** in the API, matching the UI. File paths must be absolute.
