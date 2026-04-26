#!/usr/bin/env python3
"""
LM3 Text Engine Analyzer
========================
Automated reverse engineering of the SNES text rendering engine via Mesen2-Diz IPC.

Uses MMIO memory watch hook, stepTrace, memory/register injection, and step-back
to systematically discover font tiles, tile maps, control codes, and pointer tables.

Requirements: Mesen2-Diz running with debugger active, ROM loaded, IPC pipe open.
"""

import json
import os
import socket
import struct
import sys
import time
from dataclasses import dataclass, field
from typing import Any, Optional


# ── IPC Client ────────────────────────────────────────────────────────────────

class MesenIPC:
    """Low-level IPC client for Mesen2-Diz named pipe."""

    def __init__(self, pipe_path: str | None = None):
        if pipe_path is None:
            # Auto-detect pipe
            candidates = [f for f in os.listdir("/tmp") if f.startswith("CoreFxPipe_Mesen")]
            if not candidates:
                raise FileNotFoundError("No Mesen IPC pipe found in /tmp")
            pipe_path = f"/tmp/{candidates[0]}"
        self.pipe_path = pipe_path
        self._sock: socket.socket | None = None
        self._buf = b""

    def connect(self):
        self._sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self._sock.connect(self.pipe_path)
        print(f"[IPC] Connected to {self.pipe_path}")

    def close(self):
        if self._sock:
            self._sock.close()
            self._sock = None

    def send(self, cmd: dict) -> dict:
        """Send JSON command, return parsed response."""
        assert self._sock, "Not connected"
        msg = json.dumps(cmd) + "\n"
        self._sock.sendall(msg.encode("utf-8"))

        while b"\n" not in self._buf:
            chunk = self._sock.recv(8192)
            if not chunk:
                raise ConnectionError("Pipe closed")
            self._buf += chunk

        line, self._buf = self._buf.split(b"\n", 1)
        # Strip UTF-8 BOM
        if line.startswith(b"\xef\xbb\xbf"):
            line = line[3:]
        resp = json.loads(line)
        if not resp.get("success"):
            err = resp.get("error", "unknown error")
            raise RuntimeError(f"IPC error: {err}")
        return resp.get("data", {})

    # ── High-level helpers ────────────────────────────────────────────────

    def get_status(self) -> dict:
        return self.send({"command": "getStatus"})

    def pause(self):
        self.send({"command": "pause"})

    def resume(self):
        self.send({"command": "resume"})

    def is_paused(self) -> bool:
        return self.send({"command": "isPaused"})["paused"]

    def save_state(self, slot: int = 1):
        self.send({"command": "saveStateSlot", "slot": slot})

    def load_state(self, slot: int = 1):
        self.send({"command": "loadStateSlot", "slot": slot})

    def get_cpu_state(self, cpu: str = "Snes") -> dict:
        return self.send({"command": "getCpuState", "cpuType": cpu})

    def set_cpu_state(self, **kwargs) -> dict:
        cmd = {"command": "setCpuState"}
        cmd.update(kwargs)
        return self.send(cmd)

    def step_trace(self, count: int = 1, step_type: str = "Step",
                   cpu: str = "Snes", step_back_unit: str = "Instruction") -> list[dict]:
        resp = self.send({
            "command": "stepTrace",
            "cpuType": cpu,
            "count": count,
            "stepType": step_type,
            "stepBackUnit": step_back_unit,
        })
        return resp.get("states", [])

    def step_back(self, count: int = 1) -> list[dict]:
        """Step backward by instruction count. Returns states."""
        return self.step_trace(count=count, step_type="StepBack", step_back_unit="Instruction")

    def read_memory(self, mem_type: str, address: int, length: int = 1) -> list[int]:
        resp = self.send({
            "command": "readMemory",
            "memoryType": mem_type,
            "address": hex(address),
            "length": length,
        })
        return resp.get("bytes", [])

    def write_memory(self, mem_type: str, address: int, values: list[int]):
        self.send({
            "command": "writeMemory",
            "memoryType": mem_type,
            "address": hex(address),
            "values": values,
        })

    def get_disassembly(self, address: int, rows: int = 20, cpu: str = "Snes") -> list:
        resp = self.send({
            "command": "getDisassembly",
            "cpuType": cpu,
            "address": hex(address),
            "rows": rows,
        })
        return resp.get("lines", [])

    def add_breakpoint(self, address: int, mem_type: str = "SnesMemory",
                       cpu: str = "Snes", on_exec: bool = True,
                       on_read: bool = False, on_write: bool = False,
                       end_address: int | None = None,
                       condition: str | None = None) -> dict:
        cmd = {
            "command": "addBreakpoint",
            "address": hex(address),
            "memoryType": mem_type,
            "cpuType": cpu,
            "breakOnExec": on_exec,
            "breakOnRead": on_read,
            "breakOnWrite": on_write,
        }
        if end_address is not None:
            cmd["endAddress"] = hex(end_address)
        if condition:
            cmd["condition"] = condition
        return self.send(cmd)

    def remove_breakpoint(self, address: int, mem_type: str = "SnesMemory",
                          cpu: str = "Snes"):
        self.send({
            "command": "removeBreakpoint",
            "address": hex(address),
            "memoryType": mem_type,
            "cpuType": cpu,
        })

    def clear_breakpoints(self):
        self.send({"command": "clearBreakpoints"})

    def get_absolute_address(self, address: int, mem_type: str = "SnesMemory") -> dict:
        return self.send({
            "command": "getAbsoluteAddress",
            "address": hex(address),
            "memoryType": mem_type,
        })

    def get_ppu_state(self, cpu: str = "Snes") -> dict:
        return self.send({"command": "getPpuState", "cpuType": cpu})

    def evaluate(self, expr: str, cpu: str = "Snes"):
        return self.send({"command": "evaluate", "expression": expr, "cpuType": cpu})

    def set_labels(self, labels: list[dict]) -> dict:
        return self.send({"command": "setLabels", "labels": labels})

    # MMIO watch helpers
    def watch_cpu_memory(self, ranges: list[dict], cpu: str = "Snes",
                         ops: list[str] | None = None) -> dict:
        cmd = {
            "command": "watchCpuMemory",
            "cpuType": cpu,
            "ranges": ranges,
        }
        if ops:
            cmd["ops"] = ops
        return self.send(cmd)

    def poll_memory_events(self, max_events: int = 4096) -> dict:
        return self.send({"command": "pollMemoryEvents", "maxEvents": max_events})

    def clear_memory_watches(self):
        self.send({"command": "clearCpuMemoryWatches"})

    def set_memory_watch_enabled(self, enabled: bool):
        self.send({"command": "setMemoryWatchEnabled", "enabled": enabled})

    def set_controller_input(self, port: int = 0, **buttons):
        self.send({"command": "setControllerInput", "port": port, "buttons": buttons})

    def clear_controller_input(self, port: int = 0):
        self.send({"command": "clearControllerInput", "port": port})

    def step_frames(self, n: int = 1):
        """Step N PPU frames."""
        self.step_trace(count=n, step_type="PpuFrame")

    def resume_until_break(self, timeout: float = 5.0):
        """Resume and wait until paused (breakpoint hit). Polls isPaused."""
        self.resume()
        t0 = time.time()
        while time.time() - t0 < timeout:
            time.sleep(0.01)
            if self.is_paused():
                return True
        self.pause()
        return False


# ── Analysis Data Types ───────────────────────────────────────────────────────

@dataclass
class VramWrite:
    """A VRAM write event reconstructed from MMIO hook."""
    master_clock: int
    vram_addr: int          # 16-bit VRAM word address
    value: int              # data written
    is_high: bool           # $2119 (high byte) vs $2118 (low byte)

@dataclass
class ControlCodeInfo:
    """Result of probing one byte value through the text dispatch."""
    byte_value: int
    classification: str     # "printable", "control_single", "control_multi", "terminator", "nop"
    handler_addr: int = 0   # PC of handler (first instruction after dispatch branch)
    param_count: int = 0    # additional bytes consumed
    trace_pcs: list[int] = field(default_factory=list)  # PCs visited during trace
    description: str = ""

@dataclass
class AnalysisReport:
    """Full results of text engine analysis."""
    text_entry: int = 0x00BE3B
    text_stream_ptr_addr: int = 0  # RAM address of [$14]
    text_stream_bank: int = 0

    # Font
    font_vram_base: int = 0
    font_rom_source: int = 0
    font_tile_count: int = 0

    # Tile map
    tile_map_bg_layer: int = 0
    tile_map_vram_base: int = 0
    tile_map_width: int = 0
    tile_map_stride: int = 0

    # Control codes
    control_codes: list[ControlCodeInfo] = field(default_factory=list)
    printable_range: tuple[int, int] = (0, 0)

    # Pointer table
    pointer_table_rom: int = 0
    pointer_table_count: int = 0

    # Palette
    text_palette_index: int = 0

    # Labels generated
    labels: list[dict] = field(default_factory=list)

    def to_json(self) -> str:
        d = {
            "textEntry": f"${self.text_entry:06X}",
            "textStreamPtr": f"${self.text_stream_ptr_addr:04X}",
            "font": {
                "vramBase": f"${self.font_vram_base:04X}",
                "romSource": f"${self.font_rom_source:06X}" if self.font_rom_source else None,
                "tileCount": self.font_tile_count,
            },
            "tileMap": {
                "bgLayer": self.tile_map_bg_layer,
                "vramBase": f"${self.tile_map_vram_base:04X}",
                "width": self.tile_map_width,
                "stride": self.tile_map_stride,
            },
            "controlCodes": [
                {
                    "byte": f"${cc.byte_value:02X}",
                    "type": cc.classification,
                    "handler": f"${cc.handler_addr:06X}" if cc.handler_addr else None,
                    "paramCount": cc.param_count,
                    "description": cc.description,
                }
                for cc in self.control_codes
                if cc.classification != "printable"
            ],
            "printableRange": {
                "start": f"${self.printable_range[0]:02X}",
                "end": f"${self.printable_range[1]:02X}",
            },
            "pointerTable": {
                "romAddress": f"${self.pointer_table_rom:06X}" if self.pointer_table_rom else None,
                "entryCount": self.pointer_table_count,
            },
            "palette": {
                "index": self.text_palette_index,
            },
            "labelsApplied": len(self.labels),
        }
        return json.dumps(d, indent=2)


# ── Analyzer ──────────────────────────────────────────────────────────────────

class TextEngineAnalyzer:
    """Orchestrates the multi-phase text engine analysis."""

    TEXT_ENTRY = 0x00BE3B
    PPU_WRITE_RANGES = [
        {"start": "0x2100", "end": "0x2100"},   # screen brightness
        {"start": "0x2115", "end": "0x2119"},    # VRAM mode + addr + data
        {"start": "0x2121", "end": "0x2122"},    # CGRAM addr + data
        {"start": "0x212C", "end": "0x212D"},    # main/sub screen
    ]

    def __init__(self, ipc: MesenIPC):
        self.ipc = ipc
        self.report = AnalysisReport()
        self._vram_addr_latch = 0       # current VRAM address from $2116/$2117
        self._vram_increment_mode = 0   # from $2115
        self._cgram_addr = 0

    def run(self):
        """Execute all analysis phases."""
        print("\n" + "=" * 60)
        print("  LM3 Text Engine Analyzer")
        print("=" * 60)

        self.phase1_setup()
        self.phase2_font_discovery()
        self.phase3_tile_map()
        self.phase4_control_codes()
        self.phase5_pointer_table()
        self.phase6_palette()
        self.phase7_labeling()

        print("\n" + "=" * 60)
        print("  Analysis Complete")
        print("=" * 60)

        report_path = "tools/lm3_text_report.json"
        with open(report_path, "w") as f:
            f.write(self.report.to_json())
        print(f"\nReport written to {report_path}")
        print(self.report.to_json())

    # ── Phase 1: Setup ────────────────────────────────────────────────────

    def phase1_setup(self):
        print("\n── Phase 1: Setup ──")

        # Ensure paused
        if not self.ipc.is_paused():
            self.ipc.pause()
            print("  Paused emulation")

        # Save checkpoint
        self.ipc.save_state(1)
        print("  Saved state to slot 1")

        # Set up MMIO watches
        self.ipc.watch_cpu_memory(self.PPU_WRITE_RANGES, ops=["write"])
        self.ipc.set_memory_watch_enabled(True)
        # Flush any stale events
        self.ipc.poll_memory_events()
        print("  MMIO watches active on PPU regs")

        # Set breakpoint at text entry
        self.ipc.clear_breakpoints()
        self.ipc.add_breakpoint(self.TEXT_ENTRY)
        print(f"  Breakpoint at ${self.TEXT_ENTRY:06X}")

        # Check if we're already at text entry
        state = self.ipc.get_cpu_state()
        pc = int(state["pc"], 16)
        if pc == self.TEXT_ENTRY:
            print(f"  Already at text entry ${pc:06X}")
        else:
            # Resume and wait for breakpoint
            print("  Resuming to hit text entry breakpoint...")
            hit = self.ipc.resume_until_break(timeout=10.0)
            if not hit:
                print("  WARNING: Breakpoint not hit within timeout.")
                print("  You may need to trigger dialogue manually.")
                print("  Press Enter when at text entry, or Ctrl+C to abort.")
                input()

        # Record entry state
        state = self.ipc.get_cpu_state()
        pc = int(state["pc"], 16)
        print(f"  At PC=${pc:06X}, A=${int(state['a'], 16):04X}")

        # Read text stream pointer [$14] (direct page relative)
        dp = int(state["d"], 16)
        ptr_bytes = self.ipc.read_memory("SnesMemory", dp + 0x14, 2)
        stream_ptr = ptr_bytes[0] | (ptr_bytes[1] << 8)
        dbr = int(state["dbr"], 16)
        full_addr = (dbr << 16) | stream_ptr

        self.report.text_stream_ptr_addr = dp + 0x14
        self.report.text_stream_bank = dbr
        print(f"  Text stream pointer: ${full_addr:06X} ([$14]=${stream_ptr:04X}, DBR=${dbr:02X})")

        # Read first few bytes of text stream
        preview = self.ipc.read_memory("SnesMemory", full_addr, 16)
        preview_hex = " ".join(f"{b:02X}" for b in preview)
        print(f"  Stream preview: {preview_hex}")

    # ── Phase 2: Font Tile Discovery ──────────────────────────────────────

    def phase2_font_discovery(self):
        print("\n── Phase 2: Font Tile Discovery ──")

        # Flush MMIO events
        self.ipc.poll_memory_events()

        # Step through text rendering — trace enough instructions to see VRAM writes
        print("  Tracing through text render (200 instructions)...")
        states = self.ipc.step_trace(count=200)

        # Collect MMIO events
        poll = self.ipc.poll_memory_events()
        events = poll.get("events", [])
        print(f"  Captured {len(events)} MMIO events")

        # Parse VRAM writes
        vram_writes = self._parse_vram_events(events)
        if vram_writes:
            # Tile map writes are typically to nametable area
            addrs = sorted(set(w.vram_addr for w in vram_writes))
            print(f"  VRAM addresses written: {', '.join(f'${a:04X}' for a in addrs[:10])}")
            if len(addrs) > 10:
                print(f"    ... and {len(addrs) - 10} more")

            # Look for tile index values (low byte writes to $2118)
            tile_indices = [w.value for w in vram_writes if not w.is_high]
            if tile_indices:
                unique_tiles = sorted(set(tile_indices))
                print(f"  Tile indices written: {', '.join(f'${t:02X}' for t in unique_tiles[:20])}")
                self.report.font_tile_count = len(unique_tiles)
        else:
            print("  No VRAM writes detected — font may already be loaded")
            print("  Checking PPU state for tile base...")

        # Get PPU state for BG tile data base
        ppu = self.ipc.get_ppu_state()
        print(f"  PPU: bgMode={ppu.get('bgMode')}, vramAddress=${ppu.get('vramAddress', 0):04X}")

        # Find the STA $2118 instruction in the trace to identify tile data source
        self._find_vram_write_source(states)

    def _parse_vram_events(self, events: list[dict]) -> list[VramWrite]:
        """Parse raw MMIO events into VramWrite records, tracking address latch."""
        writes: list[VramWrite] = []
        for e in events:
            addr = e["address"]
            val = e["value"]
            clock = e["masterClock"]

            if addr == 0x2115:
                self._vram_increment_mode = val
            elif addr == 0x2116:
                self._vram_addr_latch = (self._vram_addr_latch & 0xFF00) | val
            elif addr == 0x2117:
                self._vram_addr_latch = (self._vram_addr_latch & 0x00FF) | (val << 8)
            elif addr == 0x2118:
                writes.append(VramWrite(clock, self._vram_addr_latch, val, is_high=False))
                # Auto-increment if mode says increment on low write
                if (self._vram_increment_mode & 0x80) == 0:
                    inc = [1, 32, 128, 128][(self._vram_increment_mode & 0x03)]
                    self._vram_addr_latch = (self._vram_addr_latch + inc) & 0xFFFF
            elif addr == 0x2119:
                writes.append(VramWrite(clock, self._vram_addr_latch, val, is_high=True))
                # Auto-increment if mode says increment on high write
                if (self._vram_increment_mode & 0x80) != 0:
                    inc = [1, 32, 128, 128][(self._vram_increment_mode & 0x03)]
                    self._vram_addr_latch = (self._vram_addr_latch + inc) & 0xFFFF

        return writes

    def _find_vram_write_source(self, states: list[dict]):
        """Scan trace states for STA $2118/$2119 and identify the data source."""
        for i, s in enumerate(states):
            pc = int(s["pc"], 16)
            # Get disassembly at this PC
            disasm = self.ipc.get_disassembly(pc, rows=1)
            if not disasm:
                continue
            line = disasm[0].get("text", "") if isinstance(disasm[0], dict) else str(disasm[0])
            if "$2118" in line or "$2119" in line:
                print(f"  Found VRAM write at ${pc:06X}: {line.strip()}")
                # Look backward in trace for the LDA that loaded the value
                if i > 0:
                    for j in range(i - 1, max(i - 10, -1), -1):
                        prev_pc = int(states[j]["pc"], 16)
                        prev_disasm = self.ipc.get_disassembly(prev_pc, rows=1)
                        if prev_disasm:
                            prev_line = prev_disasm[0].get("text", "") if isinstance(prev_disasm[0], dict) else str(prev_disasm[0])
                            if "LDA" in prev_line.upper() or "STA" not in prev_line.upper():
                                print(f"    Source: ${prev_pc:06X}: {prev_line.strip()}")
                                break
                break

    # ── Phase 3: Tile Map Analysis ────────────────────────────────────────

    def phase3_tile_map(self):
        print("\n── Phase 3: Tile Map Analysis ──")

        # Restore state, trace a full line of text rendering
        self.ipc.load_state(1)
        time.sleep(0.1)

        # Resume to breakpoint
        self.ipc.add_breakpoint(self.TEXT_ENTRY)
        hit = self.ipc.resume_until_break(timeout=10.0)
        if not hit:
            print("  Could not reach text entry")
            return

        # Flush and trace a longer stretch (full line = ~16 chars × ~50 instr each)
        self.ipc.poll_memory_events()
        print("  Tracing full text line (500 instructions)...")
        states = self.ipc.step_trace(count=500)

        poll = self.ipc.poll_memory_events()
        events = poll.get("events", [])
        vram_writes = self._parse_vram_events(events)

        if not vram_writes:
            print("  No VRAM writes in trace — text may use DMA or buffer")
            return

        # Separate tile map writes (pairs of low+high to same address region)
        # Tile map entries are 16-bit: low byte = tile index, high byte = attributes
        addrs = [w.vram_addr for w in vram_writes if not w.is_high]
        if len(addrs) >= 2:
            # Compute stride between sequential writes
            diffs = [addrs[i+1] - addrs[i] for i in range(len(addrs)-1) if addrs[i+1] > addrs[i]]
            if diffs:
                common_diff = max(set(diffs), key=diffs.count)
                self.report.tile_map_stride = common_diff
                self.report.tile_map_vram_base = min(addrs)
                print(f"  Tile map base: ${min(addrs):04X}")
                print(f"  Write stride: {common_diff} words")
                print(f"  Addresses: {' '.join(f'${a:04X}' for a in addrs[:12])}")

        # Identify BG layer from PPU state
        ppu = self.ipc.get_ppu_state()
        main_layers = ppu.get("mainScreenLayers", 0)
        print(f"  Main screen layers: {main_layers}")
        print(f"  BG mode: {ppu.get('bgMode', '?')}")

    # ── Phase 4: Control Code Enumeration ─────────────────────────────────

    def phase4_control_codes(self):
        print("\n── Phase 4: Control Code Enumeration ──")
        print("  Probing all 256 byte values through text dispatch...")

        # Save clean state at text entry
        self.ipc.load_state(1)
        time.sleep(0.1)
        self.ipc.add_breakpoint(self.TEXT_ENTRY)
        hit = self.ipc.resume_until_break(timeout=10.0)
        if not hit:
            print("  Could not reach text entry — aborting phase 4")
            return

        # Record entry state and stream pointer
        entry_state = self.ipc.get_cpu_state()
        dp = int(entry_state["d"], 16)
        dbr = int(entry_state["dbr"], 16)
        ptr_bytes = self.ipc.read_memory("SnesMemory", dp + 0x14, 2)
        stream_ptr = ptr_bytes[0] | (ptr_bytes[1] << 8)
        stream_addr = (dbr << 16) | stream_ptr

        # Save original byte for restoration
        original_byte = self.ipc.read_memory("SnesMemory", stream_addr, 1)[0]

        # Save state at entry point for step-back baseline
        self.ipc.save_state(2)

        results: list[ControlCodeInfo] = []
        printable_start = -1
        printable_end = -1

        for byte_val in range(256):
            # Inject test byte at stream pointer
            self.ipc.write_memory("SnesMemory", stream_addr, [byte_val])

            # Trace through dispatch
            trace = self.ipc.step_trace(count=50)
            pcs = [int(s["pc"], 16) for s in trace]

            # Classify based on trace behavior
            info = self._classify_byte(byte_val, pcs, trace)
            results.append(info)

            # Step back to entry point for next probe
            self.ipc.load_state(2)

            # Progress
            if byte_val % 32 == 0:
                print(f"  Probed ${byte_val:02X}-${min(byte_val+31, 255):02X}...")

        # Restore original byte
        self.ipc.load_state(2)
        self.ipc.write_memory("SnesMemory", stream_addr, [original_byte])

        # Find printable range
        for r in results:
            if r.classification == "printable":
                if printable_start < 0:
                    printable_start = r.byte_value
                printable_end = r.byte_value

        self.report.printable_range = (
            printable_start if printable_start >= 0 else 0,
            printable_end if printable_end >= 0 else 0
        )
        self.report.control_codes = results

        # Summary
        n_printable = sum(1 for r in results if r.classification == "printable")
        n_control = sum(1 for r in results if r.classification.startswith("control"))
        n_special = sum(1 for r in results if r.classification in ("terminator", "nop"))
        print(f"\n  Results: {n_printable} printable, {n_control} control codes, {n_special} special")

        # Print control codes
        for r in results:
            if r.classification != "printable":
                handler = f"${r.handler_addr:06X}" if r.handler_addr else "---"
                print(f"    ${r.byte_value:02X}: {r.classification:<16} handler={handler}  params={r.param_count}  {r.description}")

        # Enumerate $FF sub-commands if $FF is a prefix
        ff_info = results[0xFF]
        if ff_info.classification == "control_multi":
            self._enumerate_ff_subcmds(stream_addr)

    def _classify_byte(self, byte_val: int, pcs: list[int], trace: list[dict]) -> ControlCodeInfo:
        """Classify a byte value based on its execution trace through the dispatcher."""
        info = ControlCodeInfo(byte_value=byte_val, classification="unknown", trace_pcs=pcs[:10])

        if not pcs:
            info.classification = "unknown"
            return info

        # Known classifications from prior analysis
        known_controls = {
            0x00: ("terminator", "End of text block"),
            0x90: ("control_single", "Newline/scroll"),
            0xCE: ("nop", "Skip/no-op"),
        }
        if byte_val in known_controls:
            info.classification, info.description = known_controls[byte_val]
            if len(pcs) > 2:
                info.handler_addr = pcs[1]  # first branch target
            return info

        # Heuristic: if trace quickly reaches the text entry loop again,
        # it's likely a single-byte control or printable character
        # If trace diverges to a unique address, it's a handler

        # Check if we see the same PC pattern as known printable chars
        # (They all go through writeTextCharacter at $C156)
        WRITE_CHAR = 0x00C156
        if WRITE_CHAR in pcs:
            info.classification = "printable"
            info.handler_addr = WRITE_CHAR
            return info

        # Check for $D0-range (icon/special character: tile = param + $0180)
        if byte_val >= 0xD0 and byte_val < 0xFF:
            # Likely icon handler if it doesn't go to writeTextCharacter
            info.classification = "control_multi" if byte_val == 0xFF else "control_single"
            if len(pcs) > 1:
                info.handler_addr = pcs[1]
            return info

        # Check if it reads additional bytes (multi-byte command)
        # Detect by seeing if the stream pointer gets incremented more than once
        info.classification = "control_single"
        if len(pcs) > 1:
            info.handler_addr = pcs[1]

        return info

    def _enumerate_ff_subcmds(self, stream_addr: int):
        """Enumerate $FF sub-command handlers."""
        print("\n  Enumerating $FF sub-commands...")

        self.ipc.load_state(2)
        sub_results: list[ControlCodeInfo] = []

        for sub_byte in range(256):
            # Inject $FF + sub-command byte
            self.ipc.write_memory("SnesMemory", stream_addr, [0xFF, sub_byte])

            # Longer trace for $FF handlers (deeper dispatch)
            trace = self.ipc.step_trace(count=100)
            pcs = [int(s["pc"], 16) for s in trace]

            info = ControlCodeInfo(
                byte_value=sub_byte,
                classification="ff_subcmd",
                trace_pcs=pcs[:15],
            )
            if len(pcs) > 5:
                # Handler is typically a few instructions after the dispatch
                # Find the first unique PC (not in common dispatch prefix)
                info.handler_addr = pcs[5] if len(pcs) > 5 else 0

            sub_results.append(info)

            # Step back for next probe
            self.ipc.load_state(2)

            if sub_byte % 32 == 0:
                print(f"    Probed $FF ${sub_byte:02X}-${min(sub_byte+31, 255):02X}...")

        # Group by handler address to find distinct handlers
        handlers: dict[int, list[int]] = {}
        for r in sub_results:
            h = r.handler_addr
            if h not in handlers:
                handlers[h] = []
            handlers[h].append(r.byte_value)

        print(f"\n  Found {len(handlers)} distinct $FF sub-command handlers:")
        for h_addr, byte_vals in sorted(handlers.items()):
            if len(byte_vals) <= 4:
                vals = ", ".join(f"${b:02X}" for b in byte_vals)
            else:
                vals = f"${byte_vals[0]:02X}-${byte_vals[-1]:02X} ({len(byte_vals)} values)"
            print(f"    ${h_addr:06X}: {vals}")

    # ── Phase 5: Pointer Table Discovery ──────────────────────────────────

    def phase5_pointer_table(self):
        print("\n── Phase 5: Pointer Table Discovery ──")

        # Get current text pointer
        self.ipc.load_state(1)
        time.sleep(0.1)
        self.ipc.add_breakpoint(self.TEXT_ENTRY)
        hit = self.ipc.resume_until_break(timeout=10.0)
        if not hit:
            print("  Could not reach text entry")
            return

        state = self.ipc.get_cpu_state()
        dp = int(state["d"], 16)
        dbr = int(state["dbr"], 16)
        ptr_bytes = self.ipc.read_memory("SnesMemory", dp + 0x14, 2)
        stream_ptr = ptr_bytes[0] | (ptr_bytes[1] << 8)

        # Get ROM address of current text
        abs_info = self.ipc.get_absolute_address(
            (dbr << 16) | stream_ptr, "SnesMemory"
        )
        if abs_info:
            rom_addr = abs_info.get("address", -1)
            mem_type = abs_info.get("memoryType", "?")
            print(f"  Current text at ROM ${rom_addr:06X} ({mem_type})")

            if rom_addr >= 0:
                # Search backward for pointer table
                # Read 512 bytes before the text data
                search_start = max(0, rom_addr - 512)
                region = self.ipc.read_memory("SnesPrgRom", search_start, 512)

                # Look for 16-bit pointer values that point into the text region
                candidates = []
                for i in range(0, len(region) - 1, 2):
                    ptr = region[i] | (region[i + 1] << 8)
                    # Check if this could be a valid text pointer (points near our text)
                    if abs(ptr - stream_ptr) < 0x2000 and ptr > 0:
                        candidates.append((search_start + i, ptr))

                if candidates:
                    # Find the densest cluster of pointers
                    print(f"  Found {len(candidates)} pointer candidates in search region")
                    if len(candidates) >= 3:
                        # Check for consecutive pointer entries (evenly spaced in ROM)
                        first_rom = candidates[0][0]
                        self.report.pointer_table_rom = first_rom
                        self.report.pointer_table_count = len(candidates)
                        print(f"  Likely pointer table at ROM ${first_rom:06X}")
                        for rom_off, ptr_val in candidates[:10]:
                            print(f"    ROM ${rom_off:06X}: → ${(dbr << 16) | ptr_val:06X}")
                else:
                    print("  No pointer table found in search region")
        else:
            print("  Could not resolve ROM address for text pointer")

    # ── Phase 6: Palette Analysis ─────────────────────────────────────────

    def phase6_palette(self):
        print("\n── Phase 6: Palette Analysis ──")

        # Restore and trace with CGRAM watch active
        self.ipc.load_state(1)
        time.sleep(0.1)
        self.ipc.add_breakpoint(self.TEXT_ENTRY)
        hit = self.ipc.resume_until_break(timeout=10.0)
        if not hit:
            print("  Could not reach text entry")
            return

        # Flush and trace
        self.ipc.poll_memory_events()
        self.ipc.step_trace(count=300)

        poll = self.ipc.poll_memory_events()
        events = poll.get("events", [])

        # Find CGRAM writes
        cgram_writes = [(e["address"], e["value"]) for e in events
                        if e["address"] in (0x2121, 0x2122)]

        if cgram_writes:
            # $2121 = palette address, $2122 = color data
            palette_addrs = [v for a, v in cgram_writes if a == 0x2121]
            if palette_addrs:
                self.report.text_palette_index = palette_addrs[0]
                print(f"  CGRAM address: ${palette_addrs[0]:02X} (palette {palette_addrs[0] // 16})")
                print(f"  Total CGRAM writes: {len(cgram_writes)}")
        else:
            print("  No CGRAM writes during text render (palette already loaded)")

    # ── Phase 7: Labeling ─────────────────────────────────────────────────

    def phase7_labeling(self):
        print("\n── Phase 7: Labeling ──")

        labels: list[dict] = []

        # Add labels for control code handlers
        handler_addrs: set[int] = set()
        for cc in self.report.control_codes:
            if cc.handler_addr and cc.handler_addr not in handler_addrs:
                handler_addrs.add(cc.handler_addr)
                name = f"textCtrl_{cc.byte_value:02X}"
                comment = f"Control code ${cc.byte_value:02X}: {cc.classification}"
                if cc.description:
                    comment += f" — {cc.description}"

                # Get ROM address
                abs_info = self.ipc.get_absolute_address(cc.handler_addr, "SnesMemory")
                if abs_info and abs_info.get("address", -1) >= 0:
                    labels.append({
                        "address": hex(abs_info["address"]),
                        "memoryType": abs_info.get("memoryType", "SnesPrgRom"),
                        "label": name,
                        "comment": comment,
                        "category": "Text",
                    })

        # Tile map labels
        if self.report.tile_map_vram_base:
            labels.append({
                "address": hex(self.report.tile_map_vram_base),
                "memoryType": "SnesVideoRam",
                "label": "textTileMapBase",
                "comment": f"Text window tile map start (stride={self.report.tile_map_stride})",
                "category": "PPU",
            })

        # Pointer table
        if self.report.pointer_table_rom:
            labels.append({
                "address": hex(self.report.pointer_table_rom),
                "memoryType": "SnesPrgRom",
                "label": "textPointerTable",
                "comment": f"Text string pointer table ({self.report.pointer_table_count} entries)",
                "category": "Data",
            })

        if labels:
            resp = self.ipc.set_labels(labels)
            print(f"  Applied {len(labels)} labels")
            self.report.labels = labels
        else:
            print("  No new labels to apply")


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    ipc = MesenIPC()
    ipc.connect()

    try:
        analyzer = TextEngineAnalyzer(ipc)
        analyzer.run()
    except KeyboardInterrupt:
        print("\n\nAborted by user")
    except Exception as e:
        print(f"\nERROR: {e}")
        raise
    finally:
        # Cleanup
        ipc.clear_memory_watches()
        ipc.clear_breakpoints()
        ipc.close()
        print("\nCleanup complete.")


if __name__ == "__main__":
    main()
