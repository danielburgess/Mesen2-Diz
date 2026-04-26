# Mesen2-Diz

**Mesen2-Diz** is a heavily customized fork of [Mesen2](https://github.com/SourMesen/Mesen2) focused on SNES ROM disassembly, annotation, and reverse-engineering workflows. It integrates [DiztinGUIsh](https://github.com/IsoFrieze/DiztinGUIsh)-compatible project files directly into the emulator debugger.

> **This is not a general-purpose emulator release.** It is a development tool for SNES reverse-engineering built on top of Mesen2's multi-system emulation core. For standard Mesen2 builds, see the [upstream repository](https://github.com/SourMesen/Mesen2).

---

## Overview

Mesen2-Diz extends Mesen2 with four major capability areas:

1. **DiztinGUIsh project integration** — import/export `.diz` / `.dizraw` annotation projects, round-trip CDL coverage, and export asar-compatible SNES assembly.
2. **Annotation workflow tools** — inline label editing, batch find/replace on comments, function/label category system, and provenance flags that mark which labels were set by external tools.
3. **IPC server for external scripting and AI control** — a line-based JSON pipe server exposing 85+ commands that let external programs (scripts, AI agents, reverse-engineering pipelines) fully drive the emulator and debugger. Beyond the basics (memory r/w, execution control, breakpoints, label/save-state/cheat management) the protocol now covers full **SNES + SPC700 + S-DSP** state, PPU/DMA introspection, tilemap and tile-graphics decoding, targeted execution (run-until-VRAM-write), memory search / snapshot / diff, trace log + event wait, and a lock-free **MMIO memory-watch hook** for high-throughput access tracing.
4. **Auto-dump on pause** — optional per-ROM folder where every pause produces a screenshot plus RAM dump, for offline analysis by external tools.

A companion **Claude Code skill** (`snes-reverse-engineering`) is in active development to layer 65816/SPC700 RE methodology, recipes, and a 263-section hardware knowledge base on top of this IPC. The skill drives the emulator through the same protocol documented in [`AI_IPC_PROMPT.md`](AI_IPC_PROMPT.md); the emulator is the runtime, the skill is the brain. It will be released separately when ready.

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

**Command categories (85+ total)**:

| Category | Commands |
|---|---|
| Labels | `setLabel`, `setLabels` (batch), `deleteLabel`, `getLabel`, `getLabelByName`, `getAllLabels` |
| Memory | `readMemory`, `writeMemory`, `getMemorySize` |
| CPU state | `getCpuState` (full SNES + SPC), `setCpuState` (partial-update, SNES + SPC), `getProgramCounter`, `setProgramCounter` |
| Execution | `pause`, `resume`, `isPaused`, `step`, `stepTrace` (synchronous multi-step with per-step CPU state; supports `StepBack` rewinds) |
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
| Timing & PPU | `getTimingInfo`, `getPpuState`, `getBgState`, `getDmaState` |
| PPU memory | `getVram`, `getCgram`, `getOam` (raw bytes + parsed sprites) |
| Tilemap / graphics decode | `getTilemap` (handles double-width/height quadrants), `decodeTiles` (2/4/8bpp planar → indexed + RGBA), `renderBgLayer` (full BG composite) |
| Targeted execution | `runUntilVramWrite` |
| Memory search & diff | `searchMemory` (hex + wildcards), `snapshotMemory`, `diffMemory`, `clearSnapshots` |
| Trace log & event wait | `getTraceLog`, `setTraceLogEnabled`, `waitForEvent` (breakpoint/paused/resumed/frameComplete/romLoaded/stateLoaded/reset) |
| SPC700 / S-DSP | `getSpcState`, `getDspState` (decoded per-voice + global registers) |
| MMIO memory watch hook | `watchCpuMemory`, `addCpuMemoryWatch`, `clearCpuMemoryWatches`, `pollMemoryEvents`, `setMemoryWatchEnabled`, `setMemoryWatchRingSize` |
| Cheats | `setCheats`, `clearCheats` |
| Introspection | `listCommands` (alias `getCommands`) |

**Reverse stepping**: `stepTrace` with `stepType: "StepBack"` rewinds execution by instruction, scanline, or frame, returning full CPU state at each point — the debugger records history so external agents can analyze causality.

**Forcing conditions**: any register (A, X, Y, SP, D, DBR, K, PC, flags, emulationMode) can be modified mid-execution via `setCpuState`, and any RAM can be written via `writeMemory`. Combined with `setProgramCounter`, this lets external tools simulate arbitrary entry conditions and test alternate execution paths.

**Provenance tracking**: labels and breakpoints created by IPC clients are tagged with an **IPC flag**, visible as a green dot in the label and function list UIs (and sortable). This keeps human-authored annotations distinct from tool-authored ones.

**AI agent usage**: the file [`AI_IPC_PROMPT.md`](AI_IPC_PROMPT.md) is a compact, machine-readable spec of the entire IPC protocol, intended to be fed directly to an LLM as a system prompt. External AI companions (e.g. running in Claude Code, Cursor, or a custom agent) can read this file and immediately drive the emulator for disassembly, annotation, debugging assistance, and translation work. The built-in AI chat was removed in favor of this external-agent model — one IPC surface, any front-end.

**Copy prompt button**: **Settings → Debugger → IPC Server → Copy AI Prompt** copies the full spec to the clipboard for pasting into an AI tool.

---

### SPC700 / S-DSP audio reverse-engineering

The IPC protocol now treats the SPC700 audio CPU as a first-class debug target alongside the main 65816. This makes sound-driver RE — which historically required SPC dumps and offline disassembly — a live, scriptable workflow.

- `getCpuState cpuType=Spc` (alias `getSpcState`) returns the full SPC register file: A, X, Y, SP, PC, flags (`SpcFlags`), cycle, dspReg, the four APU mailbox bytes from both sides ($F4-$F7 / $2140-$2143), and per-timer outputs.
- `setCpuState cpuType=Spc` writes back any subset — useful for forcing the SPC into a specific entry condition.
- `stepTrace cpuType=Spc` returns full SPC state per step (synchronous multi-step), and `step cpuType=Spc` supports `StepBack` for reverse-stepping the audio CPU independently of the main 65816.
- `getDspState` snapshots all 128 S-DSP registers plus a decoded view of the eight voices (vol L/R, pitch, SRCN, ADSR1/2, GAIN, ENVX, OUTX, key-on/off, voice-end, PMON/NON/EON) and global state (master vol, echo vol/feedback/delay, FLG bits, sample directory, echo buffer start).
- Memory types `SpcMemory` (logical SPC view including IPL ROM overlay), `SpcRam` (raw 64 KiB ARAM), `SpcRom` (64-byte IPL), and `SpcDspRegisters` are all readable/writable via `readMemory`/`writeMemory`. Breakpoints on SPC code or APU mailbox ports use `memoryType=SpcMemory`.

[`AI_IPC_PROMPT.md`](AI_IPC_PROMPT.md) ships with a "Tracing Audio Subsystems" playbook covering the $2140-$2143 / $F4-$F7 port bridge, a six-step canonical RE flow (capture command stream → break on SPC receiver → walk the dispatcher → snapshot DSP across KON → identify the note table → label as you go), 12 gotchas (latched DSP writes, KON pulse, sticky ENDX, IPL overlay toggling, port OR-race, timer-clear-on-read, PowerCycle wipes SPC, etc.), and ready-to-paste JSON snippets.

---

### MMIO memory watch hook (lock-free SPSC ring)

For "watch every CPU access in this address range for a few frames," breakpoints are too slow — they pause execution. The MMIO watch hook captures matching memory accesses in a native single-producer/single-consumer ring buffer that the IPC client drains via `pollMemoryEvents`.

- Per-CpuType range filters with optional `valueMin`/`valueMax` and `sampleRate=1-in-N` to cut noise on hot ports (e.g. PPU registers).
- Op masks: `read`, `write`, `exec`, `execoperand`, `dmaread`, `dmawrite`, plus `allaccess` / `all` shortcuts. Value is pre-commit for writes.
- Drop-newest overflow with `dropped` and `highWater` counters surfaced in every poll response, so clients can detect saturation and adapt.
- Hot path is **fully bypassed** when `setMemoryWatchEnabled false` — zero cost when no client is listening.
- Ring size is configurable (`setMemoryWatchRingSize`, persisted to config; power-of-two, 1024-4194304).

This is the right tool for "what does the game do during the first vblank after pressing Start," "which writes touch this MMIO register and what value sequence," or "watch all main-CPU writes to $2140-$2143 to capture the audio command stream" — all without ever pausing the emulator.

---

### Companion: `snes-reverse-engineering` Claude Code skill

A separate **Claude Code skill** (`snes-reverse-engineering`) is in active development as the "brain" half of this stack — Mesen2-Diz is the runtime; the skill is the methodology and reference layer that drives it.

- 65816 + SPC700 cheatsheets, address-conversion scripts (LoROM/HiROM/ExHiROM/SA-1, copier-header aware), and a 263-section structured hardware knowledge base (queryable section-by-section to keep context windows lean).
- Investigative recipes for "what code writes to VRAM $X," "identify main loop and NMI handler," "decode a compressed data table," "force a branch to observe the alternate path," "trace the audio driver when track N plays," and more.
- Pre-flight ritual + state-verification helpers wrapped in a Python `MesenClient` IPC client (named-pipe, line-delimited JSON, context-manager cleanup).
- Annotation discipline guidance, error recovery tables, and a "when to stop and ask the user" framework so the agent doesn't grind on ambiguous specs.

The skill talks to Mesen2-Diz exclusively through the IPC protocol documented in [`AI_IPC_PROMPT.md`](AI_IPC_PROMPT.md), so any improvements to either side compose cleanly. The skill will be released separately when ready.

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
