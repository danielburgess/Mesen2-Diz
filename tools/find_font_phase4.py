#!/usr/bin/env python3
"""Phase 4: Set breakpoint on VRAM write to $4000+ region, trace back to find ROM source.
The font tiles are written to VRAM $4000-$47FF via manual CPU writes to $2118/$2119.
We need to find what ROM address the CPU is reading the font data from."""

import json
import socket
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

    st = ipc.cmd({"command": "getStatus"})
    print(f"Running={st['running']} Paused={st['paused']}")

    # Power cycle for clean start
    print("\n=== Power cycle ===")
    ipc.cmd({"command": "powerCycle"})
    time.sleep(1.0)

    st = ipc.cmd({"command": "getStatus"})
    print(f"After powerCycle: paused={st['paused']}")

    # Strategy: Set a breakpoint on writes to $2118 (VMDATAL) with condition
    # that VRAM address ($2116/$2117) is in font tile range ($4000-$47FF).
    # Problem: we can't condition on $2116/$2117 state directly in breakpoint expression.
    #
    # Alternative: Set breakpoint on $2118 write unconditionally, run until it fires
    # in the font loading timeframe (~4-6s after boot based on MMIO data showing
    # font data bursts at $4042+).
    #
    # Better approach: The bursts at $4042+ had pattern starting with "00 00 00 38..."
    # which is clearly the first non-blank font tile. Let's set a write breakpoint
    # on $2117 and check when value written makes VRAM addr land in $40xx range.
    #
    # Actually simplest: breakpoint on $2118 write with condition checking
    # the value pattern. Or just wait ~4-5s (when boot finishes and font loading starts
    # based on the gap in MMIO data at 5-9s), then set the breakpoint.

    # Let's use a different approach: watch $2116/$2117 writes, and when we see
    # VRAM address set to $4042 (first non-blank font tile), immediately pause
    # and inspect the code doing the write.

    # Actually, use a RAM breakpoint approach:
    # SNES $2117 = VMADDH. When game writes $40 to $2117, VRAM address high byte = $40.
    # Then the next $2118/$2119 writes go to VRAM $40xx.
    # Set breakpoint: write to $2117, condition: A == #$40 (or whatever value)
    # But we don't know the exact instruction sequence...

    # Simplest working approach: run for ~4s (pre-font-load phase), pause,
    # then set breakpoint on $2118 write, resume, catch the font tile write.

    print("\n=== Phase 1: Run past boot, before font load ===")
    ipc.cmd({"command": "resume"})
    time.sleep(4.0)  # Boot phase takes ~4-5s based on our data
    ipc.cmd({"command": "pause"})
    time.sleep(0.2)

    st = ipc.cmd({"command": "isPaused"})
    print(f"Paused after 4s: {st}")

    # Now set breakpoint on $2118 write (VMDATAL)
    print("\n=== Phase 2: Breakpoint on VRAM data write ===")
    r = ipc.cmd({
        "command": "addBreakpoint",
        "address": "0x2118",
        "memoryType": "SnesMemory",
        "breakOnExec": False,
        "breakOnRead": False,
        "breakOnWrite": True,
    })
    print(f"Breakpoint set: {r}")

    # Resume and wait for break
    print("Resuming, waiting for VRAM write breakpoint...")
    ipc.cmd({"command": "resume"})

    # Poll for pause (breakpoint hit)
    for i in range(100):
        time.sleep(0.05)
        st = ipc.cmd({"command": "isPaused"})
        if st.get("paused"):
            print(f"Breakpoint hit after {(i+1)*0.05:.2f}s!")
            break
    else:
        print("Timeout waiting for breakpoint!")
        ipc.cmd({"command": "pause"})

    # Inspect CPU state (values may be hex strings or ints)
    cpu = ipc.cmd({"command": "getCpuState"})
    def h(v):
        return int(v, 16) if isinstance(v, str) else v
    print(f"\nCPU State at break:")
    print(f"  PC=${h(cpu['k']):02X}:{h(cpu['pc']):04X}  A=${h(cpu['a']):04X}  X=${h(cpu['x']):04X}  Y=${h(cpu['y']):04X}")
    print(f"  SP=${h(cpu['sp']):04X}  D=${h(cpu['d']):04X}  DBR=${h(cpu['dbr']):02X}  Flags={cpu['flags']}")

    # Get disassembly around PC
    full_pc = (h(cpu['k']) << 16) | h(cpu['pc'])
    print(f"\n=== Disassembly around ${full_pc:06X} ===")
    r = ipc.cmd({"command": "getDisassembly", "address": f"0x{full_pc:06X}", "rows": 30})
    for line in r[:30] if isinstance(r, list) else []:
        print(f"  {line}")

    # Also get callstack
    print("\n=== Call Stack ===")
    cs = ipc.cmd({"command": "getCallstack"})
    if isinstance(cs, list):
        for frame in cs:
            print(f"  {frame.get('source', '?')} -> {frame.get('target', '?')} (ret={frame.get('returnAddress', '?')})")

    # Step back a few instructions to see the data source
    print("\n=== Step back 10 instructions ===")
    r = ipc.cmd({
        "command": "stepTrace",
        "stepType": "StepBack",
        "stepBackUnit": "Instruction",
        "count": 10,
    })
    states = r.get("states", r) if isinstance(r, dict) else r
    if isinstance(states, list):
        for s in states:
            pc = (h(s.get('k', 0)) << 16) | h(s.get('pc', 0))
            print(f"  PC=${pc:06X}  A=${h(s.get('a',0)):04X}  X=${h(s.get('x',0)):04X}  Y=${h(s.get('y',0)):04X}")

    # Get disassembly at stepped-back location
    cpu2 = ipc.cmd({"command": "getCpuState"})
    full_pc2 = (h(cpu2['k']) << 16) | h(cpu2['pc'])
    print(f"\n=== Disassembly at stepped-back PC ${full_pc2:06X} ===")
    r = ipc.cmd({"command": "getDisassembly", "address": f"0x{full_pc2:06X}", "rows": 40})
    if isinstance(r, list):
        for line in r[:40]:
            print(f"  {line}")
    elif isinstance(r, dict) and "lines" in r:
        for line in r["lines"][:40]:
            addr = line.get("address", "")
            code = line.get("text", line.get("code", ""))
            print(f"  {addr}: {code}")

    # Clean up
    ipc.cmd({"command": "removeBreakpoint", "address": "0x2118", "memoryType": "SnesMemory"})
    print("\nBreakpoint removed. Done.")
    ipc.close()

if __name__ == "__main__":
    main()
