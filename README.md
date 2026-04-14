# Mesen2-Diz

**Mesen2-Diz** is a heavily customized fork of [Mesen2](https://github.com/SourMesen/Mesen2) focused on SNES ROM disassembly, annotation, and reverse-engineering workflows. It integrates [DiztinGUIsh](https://github.com/IsoFrieze/DiztinGUIsh)-compatible project files directly into the emulator debugger, turning Mesen into a full annotation workstation rather than just an emulator.

> **This is not a general-purpose emulator release.** It is a development tool for SNES reverse-engineering built on top of Mesen2's multi-system emulation core. For standard Mesen2 builds, see the [upstream repository](https://github.com/SourMesen/Mesen2).

---

## Overview

Mesen2-Diz extends Mesen2 with four major capability areas:

1. **DiztinGUIsh project integration** — import/export `.diz` / `.dizraw` annotation projects, round-trip CDL coverage, and export asar-compatible SNES assembly.
2. **Annotation workflow tools** — visual ROM map, inline label editing, batch find/replace on comments, function/label category system, and provenance flags that mark which labels were set by external tools.
3. **IPC server for external scripting and AI control** — a line-based JSON pipe server exposing 60+ commands that let external programs (scripts, AI agents, reverse-engineering pipelines) fully drive the emulator and debugger: read/write memory, control execution, set breakpoints, step forward and backward with full CPU state, modify registers, batch-create labels, manage save states, inject controller input, toggle cheats, and more.
4. **Auto-dump on pause** — optional per-ROM folder where every pause produces a screenshot plus RAM dump, for offline analysis by external tools.

## Added Features

### DiztinGUIsh Project Integration

Import and export `.diz` / `.dizraw` DiztinGUIsh project files directly from the Mesen debugger.

- **Import** (`.diz` / `.dizraw`): Loads byte annotations, labels, and comments from a DiztinGUIsh project into Mesen's live label and CDL (Code/Data Logger) systems. Supports gzip-compressed `.diz` and plain-XML `.dizraw` formats.
- **Export** (`.diz` / `.dizraw`): Merges Mesen's live CDL data back into the loaded project and saves it, round-tripping any new coverage data acquired during a session.
- **ASM Export**: Exports a full [asar](https://github.com/RPGHacker/asar)-compatible SNES assembly file from the annotation data. Handles:
  - LoROM upper-bank (`$80xxxx`) org directives and label names
  - Unreached and operand bytes emitted as `db $XX` fallbacks
  - Synthetic `CODE_XXXXXX` labels for unlabeled branch targets
  - `LOOSE_OP_XXXXXX` labels for branches that land inside operand bytes
  - Correct `PER` instruction encoding (raw 16-bit offset, not PC-relative effective address)
  - Address conversion for all map modes supported by DiztinGUIsh (LoROM, HiROM, ExLoROM, ExHiROM, SA-1)

Access via **Debug → Import DiztinGUIsh Project** and **Debug → Export DiztinGUIsh Project / Export ASM**.

---

### Visual ROM Map

A pixel-level visualization of the entire ROM, one pixel per byte, colored by annotation type. Open via **Debug → ROM Map**.

| Color | Byte type |
|---|---|
| Blue | Opcode |
| Light blue | Operand |
| Orange | Data (8-bit) |
| Amber | Data (16-bit) |
| Red-orange | Data (24-bit) |
| Crimson | Data (32-bit) |
| Yellow | Pointer (16-bit) |
| Red | Pointer (24-bit) |
| Dark red | Pointer (32-bit) |
| Green | Graphics |
| Purple | Music |
| Teal | Text |
| Near-black | Empty / unreached |

**Settings panel** (toggle with the gear button):
- **Width (px)**: Set the canvas width in ROM bytes per row.
- **Fit to window**: Automatically set width to fill the available horizontal space at the current zoom level.
- **Legend**: Color key for all byte types.

Hovering over the map shows the ROM offset, SNES address, label name (if any), and byte type in the status bar. The map refreshes automatically when a new ROM is loaded and rebuilds when the canvas width changes. If no DiztinGUIsh project is loaded, it falls back to Mesen's live CDL data.

---

### Inline Label Editing

Edit label names and comments directly in the label list table without opening the edit dialog.

- Click any cell in the **Label** or **Comment** column to edit in place.
- **Enter** — commit and clear focus.
- **Escape** — revert to the stored value.
- **Tab / click away** — commit on focus loss.
- Invalid label names (characters outside `[@_a-zA-Z0-9]`) are silently reverted on commit.
- Toggle via right-click → **Inline Label Editing** (enabled by default). When disabled, the columns revert to read-only display and all editing goes through the standard popup dialog.

---

### Find / Replace in Label Comments

Batch search-and-replace across all label comments. Open via right-click on the label list → **Find/Replace in Comments**.

- Plain-text find and replace across all labels in the project.
- **Match case** option.
- Live match count updates as you type.
- **Replace All** performs a batched update (label change events are suspended during the operation) and auto-saves the workspace.
- The window is non-modal — you can keep it open while working in the debugger.

---

### IPC Server — External Scripting & AI Control

Mesen2-Diz ships with a built-in **named-pipe JSON-RPC server** that exposes the emulator and debugger to external processes. This is the headline feature for reverse-engineering pipelines and AI-assisted workflows: any language that can open a named pipe and speak line-delimited JSON can drive the emulator.

Configure under **Settings → Debugger → IPC Server**. The pipe name defaults to `Mesen2Diz_<RomName>` (e.g. `Mesen2Diz_SuperMetroid`) so multiple instances don't collide. Override in config. Event log viewer at **Debug → IPC Event Log**.

**Platform paths**
- Linux: `/tmp/CoreFxPipe_{pipeName}`
- Windows: `\\.\pipe\{pipeName}`

**Protocol**: one `{"command":"X",...}\n` per request, one `{"success":bool,"data":...}\n` per response. Connections persist across ROM reloads.

**Command categories (60+ total)**:

| Category | Commands |
|---|---|
| Labels | `setLabel`, `setLabels` (batch), `deleteLabel`, `getLabel`, `getLabelByName`, `getAllLabels` |
| Memory | `readMemory`, `writeMemory`, `getMemorySize` |
| CPU state | `getCpuState`, `setCpuState` (partial-update), `getProgramCounter`, `setProgramCounter` |
| Execution | `pause`, `resume`, `isPaused`, `step`, `stepTrace` (synchronous multi-step with per-step CPU state) |
| Disassembly | `getDisassembly`, `searchDisassembly` |
| Breakpoints | `addBreakpoint` (exec/read/write + conditions), `removeBreakpoint`, `getBreakpoints`, `clearBreakpoints` |
| Expressions | `evaluate` (registers, memory reads, arithmetic) |
| Call stack | `getCallstack` |
| CDL | `getCdlData`, `getCdlStatistics`, `getCdlFunctions` |
| Address mapping | `getAbsoluteAddress`, `getRelativeAddress` |
| ROM/status | `getRomInfo`, `getStatus`, `getIpcInfo` |
| Screenshots | `takeScreenshot` |
| Emulator control | `loadRom`, `reloadRom`, `powerCycle`, `powerOff`, `reset` |
| Save states | `saveStateSlot`, `loadStateSlot`, `saveStateFile`, `loadStateFile` |
| Controller input | `setControllerInput`, `clearControllerInput` |
| Emulation settings | `getEmulationSpeed`, `setEmulationSpeed`, `getTurboSpeed`, `setTurboSpeed`, `getRunAheadFrames`, `setRunAheadFrames`, `getConfig` |
| Timing/PPU | `getTimingInfo`, `getPpuState` |
| Cheats | `setCheats`, `clearCheats` |

**Reverse stepping**: `stepTrace` with `stepType: "StepBack"` rewinds execution by instruction, scanline, or frame, returning full CPU state at each point — the debugger records history so external agents can analyze causality.

**Forcing conditions**: any register (A, X, Y, SP, D, DBR, K, PC, flags, emulationMode) can be modified mid-execution via `setCpuState`, and any RAM can be written via `writeMemory`. Combined with `setProgramCounter`, this lets external tools simulate arbitrary entry conditions and test alternate execution paths.

**Provenance tracking**: labels and breakpoints created by IPC clients are tagged with an **IPC flag**, visible as a green dot in the label and function list UIs (and sortable). This keeps human-authored annotations distinct from tool-authored ones.

**AI agent usage**: the file [`AI_IPC_PROMPT.md`](AI_IPC_PROMPT.md) is a compact, machine-readable spec of the entire IPC protocol, intended to be fed directly to an LLM as a system prompt. External AI companions (e.g. running in Claude Code, Cursor, or a custom agent) can read this file and immediately drive the emulator for disassembly, annotation, debugging assistance, and translation work. The built-in AI chat was removed in favor of this external-agent model — one IPC surface, any front-end.

**Copy prompt button**: **Settings → Debugger → IPC Server → Copy AI Prompt** copies the full spec to the clipboard for pasting into an AI tool.

---

### Auto-Dump on Pause

Optional: every time emulation pauses, Mesen2-Diz writes a screenshot (`.png`) and full RAM dump (`.dmp`) to a configurable per-ROM folder. Useful for offline analysis, diffing RAM across pauses, and feeding state snapshots to external tools. Configure under **Settings → Debugger**.

---

## Upstream

Mesen2-Diz is based on [Mesen2](https://github.com/SourMesen/Mesen2) by Sour and inherits its full multi-system emulation core (NES, SNES, Game Boy, Game Boy Advance, PC Engine, SMS/Game Gear, WonderSwan) as well as all standard debugger tools (disassembler, breakpoints, memory viewer, event viewer, tile viewer, etc.).

## Compiling

See [COMPILING.md](COMPILING.md)

## License

Mesen is available under the GPL V3 license. Full text: <http://www.gnu.org/licenses/gpl-3.0.en.html>

Copyright (C) 2014-2025 Sour

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
