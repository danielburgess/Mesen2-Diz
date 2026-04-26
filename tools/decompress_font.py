#!/usr/bin/env python3
"""Decompress SBD font tiles from ROM and render as PPM.

Reimplements the SNES decompressor at $80E3BD:
- 3-stream compressed tile format
- bp01 stream: 2-bit command codes (8 per 16-bit word)
- bp23 stream: literal 16-bit tile words
- idx stream: 4-bit VRAM copy reference indices (4 per 16-bit word)

Commands:
  0 = literal word from bp23 stream → write to VRAM
  1 = end (done with this tile set)
  2 = read new 4-bit ref from idx stream, copy from VRAM offset
  3 = reuse last ref, copy from VRAM offset
"""

import json
import socket
import sys

PIPE = "/tmp/CoreFxPipe_Mesen2Diz_SBD"

def connect():
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.connect(PIPE)
    buf = b""
    def ipc(cmd):
        nonlocal buf
        sock.sendall((json.dumps(cmd) + "\n").encode())
        while b"\n" not in buf:
            buf += sock.recv(8192)
        line, buf = buf.split(b"\n", 1)
        if line.startswith(b"\xef\xbb\xbf"):
            line = line[3:]
        return json.loads(line).get("data", {})
    return ipc, sock


# VRAM offset table from $80E41A (16 entries, signed 16-bit offsets)
VRAM_OFFSETS = [
    -1, -32, -33, -132, -64, -65, -128, -129,
    -130, -2, -66, -256, -257, -258, -4, -512
]


class BitStream:
    """Reads N-bit values from a byte stream, MSB-first, word-at-a-time."""
    def __init__(self, data, bits_per_value, values_per_word):
        self.data = data
        self.pos = 0
        self.shift_reg = 0
        self.counter = 0
        self.bits = bits_per_value
        self.values_per_word = values_per_word

    def read(self):
        self.counter -= 1
        if self.counter <= 0:
            if self.pos + 1 < len(self.data):
                self.shift_reg = self.data[self.pos] | (self.data[self.pos + 1] << 8)
            self.pos += 2
            self.counter = self.values_per_word

        # Extract top N bits
        result = (self.shift_reg >> (16 - self.bits)) & ((1 << self.bits) - 1)
        self.shift_reg = (self.shift_reg << self.bits) & 0xFFFF
        return result


def decompress_tileset(rom_data, vram_start=0x4000):
    """Decompress one tile set from ROM header+data.
    Returns dict mapping VRAM word address → 16-bit value."""

    # Parse 10-byte header
    off_bp01 = rom_data[0] | (rom_data[1] << 8)
    off_bp23 = rom_data[2] | (rom_data[3] << 8)
    off_idx  = rom_data[4] | (rom_data[5] << 8)
    raw_count = rom_data[6] | (rom_data[7] << 8)
    mode     = rom_data[8] | (rom_data[9] << 8)

    count = raw_count >> 1  # LSR in $E34D

    bp01 = BitStream(rom_data[off_bp01:], 2, 8)   # 2-bit commands, 8 per word
    bp23_data = rom_data[off_bp23:]
    bp23_pos = 0
    idx = BitStream(rom_data[off_idx:], 4, 4)      # 4-bit refs, 4 per word

    # Simulated VRAM
    vram = {}
    vram_ptr = vram_start
    last_ref = 0
    words_written = 0
    cmd_stats = [0, 0, 0, 0]

    for _ in range(count):
        cmd = bp01.read()
        cmd_stats[cmd] += 1

        if cmd == 0:
            # Literal from bp23 stream
            val = bp23_data[bp23_pos] | (bp23_data[bp23_pos + 1] << 8)
            bp23_pos += 2
            vram[vram_ptr] = val

        elif cmd == 1:
            # End
            break

        elif cmd == 2:
            # New ref from idx stream + VRAM copy
            last_ref = idx.read()
            offset = VRAM_OFFSETS[last_ref]
            src = (vram_ptr + offset) & 0xFFFF
            vram[vram_ptr] = vram.get(src, 0)

        elif cmd == 3:
            # Reuse last ref + VRAM copy
            offset = VRAM_OFFSETS[last_ref]
            src = (vram_ptr + offset) & 0xFFFF
            vram[vram_ptr] = vram.get(src, 0)

        vram_ptr += 1
        words_written += 1

    print(f"  Decompressed {words_written} words to VRAM ${vram_start:04X}-${vram_ptr-1:04X}")
    print(f"  Commands: lit={cmd_stats[0]}, end={cmd_stats[1]}, new_ref={cmd_stats[2]}, reuse={cmd_stats[3]}")
    return vram


def vram_to_tiles(vram, base_addr, num_tiles, bpp=4):
    """Convert VRAM words to decoded pixel tiles."""
    words_per_tile = (bpp * 8) // 2  # 4bpp=16 words, 2bpp=8 words
    tiles = []
    for t in range(num_tiles):
        tile_start = base_addr + t * words_per_tile
        # Reconstruct bytes from words (low byte, high byte)
        tile_bytes = []
        for w in range(words_per_tile):
            word = vram.get(tile_start + w, 0)
            tile_bytes.append(word & 0xFF)
            tile_bytes.append((word >> 8) & 0xFF)

        pixels = []
        for row in range(8):
            bp0 = tile_bytes[row * 2]
            bp1 = tile_bytes[row * 2 + 1]
            if bpp == 4:
                bp2 = tile_bytes[16 + row * 2] if len(tile_bytes) > 16 else 0
                bp3 = tile_bytes[16 + row * 2 + 1] if len(tile_bytes) > 17 else 0
            else:
                bp2 = bp3 = 0
            row_px = []
            for bit in range(7, -1, -1):
                p = ((bp0 >> bit) & 1) | (((bp1 >> bit) & 1) << 1)
                if bpp == 4:
                    p |= (((bp2 >> bit) & 1) << 2) | (((bp3 >> bit) & 1) << 3)
                row_px.append(p)
            pixels.append(row_px)
        tiles.append(pixels)
    return tiles


def snes_to_rgb(lo, hi):
    c = lo | (hi << 8)
    return ((c & 0x1F) << 3, ((c >> 5) & 0x1F) << 3, ((c >> 10) & 0x1F) << 3)


def main():
    ipc, sock = connect()

    # Tileset $01BC = 1024 tiles (the main font/graphics set)
    # ROM $1BCD9A, compressed, ~7K
    # Header: bp01=+$000A bp23=+$100A idx=+$1B7C count=16384 words = 1024 tiles
    rom_addr = "0x1BCD9A"
    print(f"Reading compressed tileset $01BC from ROM {rom_addr}...")
    # Need enough to cover all 3 streams: idx at +$1B7C, plus idx stream data
    r = ipc({"command": "readMemory", "memoryType": "SnesPrgRom",
             "address": rom_addr, "length": 65536})
    rom = r.get("bytes", [])
    print(f"  ROM bytes: {len(rom)}")

    # Read palette from CGRAM
    r = ipc({"command": "readMemory", "memoryType": "SnesCgRam",
             "address": "0x0000", "length": 512})
    cgram = r.get("bytes", [])

    # Fixed 2bpp grayscale palette (font uses 2bpp-in-4bpp, only 4 colors matter)
    # Color 0 = background (transparent), 1-3 = font shading
    palette = [
        (255, 255, 255),  # 0: white (background/fill)
        (170, 170, 170),  # 1: light gray
        (85, 85, 85),     # 2: dark gray (stroke edge)
        (0, 0, 0),        # 3: black (stroke)
    ] + [(128, 0, 0)] * 12  # 4-15: red = debug (shouldn't appear in 2bpp)
    print(f"  Using fixed 2bpp grayscale palette")

    # Decompress
    print("\nDecompressing tileset $01CD...")
    vram = decompress_tileset(rom, vram_start=0x4000)

    # Figure out how many tiles were written
    if not vram:
        print("No data decompressed!")
        sock.close()
        return

    min_addr = min(vram.keys())
    max_addr = max(vram.keys())
    total_words = max_addr - min_addr + 1
    num_tiles = total_words // 16  # 4bpp: 16 words per tile
    print(f"\n  VRAM range: ${min_addr:04X}-${max_addr:04X} ({total_words} words)")
    print(f"  Tiles: {num_tiles} (4bpp)")

    # Decode tiles
    tiles = vram_to_tiles(vram, min_addr, num_tiles, bpp=4)

    # Render PPM
    cols = 16
    rows = (len(tiles) + cols - 1) // cols
    img_w = cols * 8
    img_h = rows * 8

    outpath = "/mnt/crucial/projects/Mesen2-Diz/tools/sbd_font_from_rom.ppm"
    with open(outpath, "w") as f:
        f.write(f"P3\n{img_w} {img_h}\n255\n")
        for tile_row in range(rows):
            for pixel_row in range(8):
                for tile_col in range(cols):
                    ti = tile_row * cols + tile_col
                    if ti < len(tiles):
                        for px in tiles[ti][pixel_row]:
                            r, g, b = palette[min(px, 15)]
                            f.write(f"{r} {g} {b} ")
                    else:
                        for _ in range(8):
                            f.write("0 0 0 ")
                f.write("\n")

    print(f"\nWrote {outpath} ({img_w}x{img_h}, {len(tiles)} tiles)")

    # Also make PNG
    import subprocess
    png_path = outpath.replace(".ppm", ".png")
    subprocess.run(["magick", outpath, "-scale", "300%", png_path],
                   capture_output=True)
    print(f"Wrote {png_path}")

    sock.close()


if __name__ == "__main__":
    main()
