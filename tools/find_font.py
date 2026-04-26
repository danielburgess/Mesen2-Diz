#!/usr/bin/env python3
"""Phase 1-3: Set MMIO watches, power cycle, run intro, capture DMA events targeting VRAM."""

import json
import socket
import sys
import time

PIPE = "/tmp/CoreFxPipe_Mesen2Diz_SBD"

class IPC:
    def __init__(self, path):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect(path)
        self._buf = b""

    def cmd(self, c):
        self.sock.sendall((json.dumps(c) + "\n").encode())
        while b"\n" not in self._buf:
            chunk = self.sock.recv(8192)
            if not chunk: raise ConnectionError("closed")
            self._buf += chunk
        line, self._buf = self._buf.split(b"\n", 1)
        if line.startswith(b"\xef\xbb\xbf"): line = line[3:]
        r = json.loads(line)
        if not r.get("success"):
            print(f"  ERR: {r.get('error')}")
        return r.get("data", {})

    def close(self):
        self.sock.close()

def main():
    ipc = IPC(PIPE)
    print("Connected.")

    # Check current state
    st = ipc.cmd({"command": "getStatus"})
    print(f"Running={st['running']} Paused={st['paused']}")

    # Phase 2: Power cycle FIRST (this recreates the Debugger, destroying any watches)
    print("\n=== Phase 2: Power cycle ===")
    ipc.cmd({"command": "powerCycle"})
    time.sleep(1.0)  # Give debugger time to reinitialize

    # Game auto-pauses after power cycle — verify
    st = ipc.cmd({"command": "getStatus"})
    print(f"After powerCycle: paused={st['paused']}")

    # Phase 1: Set up MMIO watches AFTER power cycle (debugger was recreated)
    # Watch: VRAM addr ($2116-$2117), VRAM data ($2118-$2119), DMA regs ($4300-$437F)
    print("\n=== Phase 1: Setting MMIO watches (post-powercycle) ===")

    # Bump ring size first
    ipc.cmd({"command": "setMemoryWatchRingSize", "size": 262144})
    print("Ring size: 262144")

    r = ipc.cmd({
        "command": "watchCpuMemory",
        "cpuType": "Snes",
        "ranges": [
            {"start": "0x2115", "end": "0x2119", "ops": ["write"]},  # VRAM ctrl + addr + data
            {"start": "0x4300", "end": "0x437F", "ops": ["write"]},  # DMA regs all channels
            {"start": "0x420B", "end": "0x420C", "ops": ["write"]},  # DMA + HDMA enable
        ]
    })
    print(f"Watches set: {r}")

    ipc.cmd({"command": "setMemoryWatchEnabled", "enabled": True})
    print("Watch enabled.")

    # Drain any stale events
    ipc.cmd({"command": "pollMemoryEvents", "maxEvents": 65536})

    # Resume at normal speed
    print("Resuming at normal speed...")
    ipc.cmd({"command": "setEmulationSpeed", "speed": 100})
    ipc.cmd({"command": "resume"})

    # Phase 3: Poll events over ~18 seconds (covers 8-15s intro window with margin)
    print("\n=== Phase 3: Running intro, polling events ===")
    all_events = []
    start = time.time()
    poll_interval = 1.0
    duration = 20.0  # 20s total to be safe

    while time.time() - start < duration:
        time.sleep(poll_interval)
        elapsed = time.time() - start

        # Check if still running
        st = ipc.cmd({"command": "isPaused"})
        if st.get("paused"):
            print(f"  [{elapsed:.1f}s] Game paused unexpectedly! Resuming...")
            ipc.cmd({"command": "resume"})
            continue

        # Drain events
        r = ipc.cmd({"command": "pollMemoryEvents", "maxEvents": 65536})
        count = r["count"]
        dropped = r["dropped"]
        all_events.extend(r["events"])
        print(f"  [{elapsed:.1f}s] +{count} events (total={len(all_events)}, dropped={dropped})")

    # Pause
    ipc.cmd({"command": "pause"})
    print(f"\nPaused. Total events captured: {len(all_events)}")

    # Phase 3 Analysis: Reconstruct manual VRAM writes
    # Game uses bank $80 code writing $802116-$802119 (CPU manual VRAM writes, not DMA)
    # Mask to 16-bit for register matching
    print("\n=== VRAM Write Stream Analysis ===")

    vram_addr_lo = 0
    vram_addr_hi = 0
    vram_ctrl = 0
    vram_addr_writes = []

    # Track VRAM write bursts: groups of $2118/$2119 writes between $2116/$2117 changes
    # Each burst = one VRAM data transfer to a specific VRAM address
    bursts = []
    current_burst = None

    for e in all_events:
        addr16 = e["address"] & 0xFFFF
        val = e["value"]
        clk = e["masterClock"]

        if addr16 == 0x2115:
            vram_ctrl = val
        elif addr16 == 0x2116:
            vram_addr_lo = val
            vram_addr_writes.append((clk, "lo", val))
            # New VRAM address = new burst
            if current_burst and current_burst["count"] > 0:
                bursts.append(current_burst)
            vram_word = (vram_addr_hi << 8) | vram_addr_lo
            current_burst = {"vram_addr": vram_word, "count": 0, "start_clk": clk, "end_clk": clk, "first_vals": []}
        elif addr16 == 0x2117:
            vram_addr_hi = val
            vram_addr_writes.append((clk, "hi", val))
            if current_burst and current_burst["count"] > 0:
                bursts.append(current_burst)
            vram_word = (vram_addr_hi << 8) | vram_addr_lo
            current_burst = {"vram_addr": vram_word, "count": 0, "start_clk": clk, "end_clk": clk, "first_vals": []}
        elif addr16 == 0x2118:
            if current_burst is None:
                vram_word = (vram_addr_hi << 8) | vram_addr_lo
                current_burst = {"vram_addr": vram_word, "count": 0, "start_clk": clk, "end_clk": clk, "first_vals": []}
            current_burst["count"] += 1
            current_burst["end_clk"] = clk
            if len(current_burst["first_vals"]) < 32:
                current_burst["first_vals"].append(("lo", val))
        elif addr16 == 0x2119:
            if current_burst is None:
                vram_word = (vram_addr_hi << 8) | vram_addr_lo
                current_burst = {"vram_addr": vram_word, "count": 0, "start_clk": clk, "end_clk": clk, "first_vals": []}
            current_burst["count"] += 1
            current_burst["end_clk"] = clk
            if len(current_burst["first_vals"]) < 32:
                current_burst["first_vals"].append(("hi", val))

    if current_burst and current_burst["count"] > 0:
        bursts.append(current_burst)

    print(f"\nTotal VRAM write bursts: {len(bursts)}")
    print(f"{'#':>4}  {'VRAM addr':>10}  {'Writes':>7}  {'First bytes'}")
    print("-" * 80)
    for i, b in enumerate(bursts):
        first_hex = " ".join(f"{v[1]:02X}" for v in b["first_vals"][:16])
        print(f"  {i:3d}  ${b['vram_addr']:04X} (word)  {b['count']:6d}  {first_hex}")

    # Also show DMA transfers (there were 2 CGRAM ones)
    print("\n=== DMA Transfers ===")
    dma_state = {}
    transfers = []
    for e in all_events:
        addr16 = e["address"] & 0xFFFF
        val = e["value"]
        clk = e["masterClock"]
        # Only process actual I/O reg writes (not WRAM mirrors $7Exxxx)
        if e["address"] >= 0x7E0000:
            continue
        if 0x4300 <= addr16 <= 0x437F:
            ch = (addr16 - 0x4300) // 0x10
            off = (addr16 - 0x4300) % 0x10
            dma_state[(ch, off)] = val
        elif addr16 == 0x420B:
            for ch in range(8):
                if not (val & (1 << ch)):
                    continue
                src_lo = dma_state.get((ch, 2), 0)
                src_hi = dma_state.get((ch, 3), 0)
                src_bank = dma_state.get((ch, 4), 0)
                dest = dma_state.get((ch, 1), 0)
                size_lo = dma_state.get((ch, 5), 0)
                size_hi = dma_state.get((ch, 6), 0)
                src_addr = (src_bank << 16) | (src_hi << 8) | src_lo
                size = (size_hi << 8) | size_lo
                if size == 0: size = 65536
                dest_desc = {0x18: "VRAM", 0x19: "VRAM", 0x39: "OAM", 0x22: "CGRAM"}.get(dest, f"${dest:02X}")
                transfers.append({"ch": ch, "src": src_addr, "dest": dest_desc, "size": size, "clk": clk})
                print(f"  ch{ch} src=${src_addr:06X} -> {dest_desc} size={size}")
    if not transfers:
        print("  (none targeting VRAM)")

    # Summary
    print(f"\nTotal events: {len(all_events)}")
    print(f"VRAM addr changes: {len(vram_addr_writes)}")
    print(f"VRAM write bursts: {len(bursts)}")
    print(f"DMA transfers: {len(transfers)}")

    # Save full results for further analysis
    outpath = "/mnt/crucial/projects/Mesen2-Diz/tools/font_search_results.json"
    with open(outpath, "w") as f:
        json.dump({
            "bursts": bursts,
            "dma_transfers": transfers,
            "total_events": len(all_events),
        }, f, indent=2)
    print(f"\nResults saved to {outpath}")

    # Cleanup
    ipc.cmd({"command": "clearCpuMemoryWatches"})
    ipc.cmd({"command": "setEmulationSpeed", "speed": 100})
    print("Cleaned up watches, speed restored.")
    ipc.close()

if __name__ == "__main__":
    main()
