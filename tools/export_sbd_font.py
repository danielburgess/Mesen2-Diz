#!/usr/bin/env python3
"""Export SBD font from ROM file directly (no IPC needed).

Reads compressed font tileset $01CD at ROM $1C5FE2, decompresses using
the game's custom 3-stream format, renders as PNG.

Usage: python3 export_sbd_font.py [rom_path] [output.png]
"""

import struct
import subprocess
import sys
import os

ROM_PATH = os.path.expanduser("~/Downloads/SBD.sfc")
FONT_ROM_ADDR = 0x1C5FE2  # Tileset $01CD
VRAM_BASE = 0x4000  # Font tiles destination in VRAM

# VRAM offset table from $80E41A (16 signed 16-bit entries)
VRAM_OFFSETS = [
    -1, -32, -33, -132, -64, -65, -128, -129,
    -130, -2, -66, -256, -257, -258, -4, -512
]


class BitStream:
    """Reads N-bit values from byte stream, MSB-first, word-at-a-time."""
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
        result = (self.shift_reg >> (16 - self.bits)) & ((1 << self.bits) - 1)
        self.shift_reg = (self.shift_reg << self.bits) & 0xFFFF
        return result


def decompress_tileset(rom_data, vram_start=0x4000):
    """Decompress tile set from ROM header+streams.
    Returns dict: VRAM word address -> 16-bit value."""

    # 10-byte header
    off_bp01 = rom_data[0] | (rom_data[1] << 8)
    off_bp23 = rom_data[2] | (rom_data[3] << 8)
    off_idx  = rom_data[4] | (rom_data[5] << 8)
    raw_count = rom_data[6] | (rom_data[7] << 8)
    mode     = rom_data[8] | (rom_data[9] << 8)

    count = raw_count >> 1  # matches LSR at $E34D

    print(f"  Header: bp01=+${off_bp01:04X} bp23=+${off_bp23:04X} idx=+${off_idx:04X}")
    print(f"  raw_count=${raw_count:04X} -> count={count}, mode=${mode:04X}")

    bp01 = BitStream(rom_data[off_bp01:], 2, 8)
    bp23_data = rom_data[off_bp23:]
    bp23_pos = 0
    idx = BitStream(rom_data[off_idx:], 4, 4)

    vram = {}
    vram_ptr = vram_start
    last_ref = 0
    words_written = 0
    cmd_stats = [0, 0, 0, 0]

    for _ in range(count):
        cmd = bp01.read()
        cmd_stats[cmd] += 1

        if cmd == 0:
            val = bp23_data[bp23_pos] | (bp23_data[bp23_pos + 1] << 8)
            bp23_pos += 2
            vram[vram_ptr] = val
        elif cmd == 1:
            break
        elif cmd == 2:
            last_ref = idx.read()
            offset = VRAM_OFFSETS[last_ref]
            src = (vram_ptr + offset) & 0xFFFF
            vram[vram_ptr] = vram.get(src, 0)
        elif cmd == 3:
            offset = VRAM_OFFSETS[last_ref]
            src = (vram_ptr + offset) & 0xFFFF
            vram[vram_ptr] = vram.get(src, 0)

        vram_ptr += 1
        words_written += 1

    print(f"  Decompressed {words_written} words -> VRAM ${vram_start:04X}-${vram_ptr-1:04X}")
    print(f"  Commands: lit={cmd_stats[0]} end={cmd_stats[1]} ref_new={cmd_stats[2]} ref_reuse={cmd_stats[3]}")
    return vram


def vram_to_tiles_4bpp(vram, base_addr, num_tiles):
    """Decode 4bpp SNES tiles from VRAM words. Returns list of 8x8 pixel arrays."""
    tiles = []
    for t in range(num_tiles):
        tile_start = base_addr + t * 16  # 16 words per 4bpp tile
        # Reconstruct byte pairs from words
        tile_bytes = []
        for w in range(16):
            word = vram.get(tile_start + w, 0)
            tile_bytes.append(word & 0xFF)
            tile_bytes.append((word >> 8) & 0xFF)

        pixels = []
        for row in range(8):
            bp0 = tile_bytes[row * 2]
            bp1 = tile_bytes[row * 2 + 1]
            bp2 = tile_bytes[16 + row * 2]
            bp3 = tile_bytes[16 + row * 2 + 1]
            row_px = []
            for bit in range(7, -1, -1):
                p = ((bp0 >> bit) & 1) | (((bp1 >> bit) & 1) << 1)
                p |= (((bp2 >> bit) & 1) << 2) | (((bp3 >> bit) & 1) << 3)
                row_px.append(p)
            pixels.append(row_px)
        tiles.append(pixels)
    return tiles


def vram_to_tiles_2bpp(vram, base_addr, num_tiles):
    """Decode 2bpp SNES tiles from VRAM words. Returns list of 8x8 pixel arrays."""
    tiles = []
    for t in range(num_tiles):
        tile_start = base_addr + t * 8  # 8 words per 2bpp tile
        tile_bytes = []
        for w in range(8):
            word = vram.get(tile_start + w, 0)
            tile_bytes.append(word & 0xFF)
            tile_bytes.append((word >> 8) & 0xFF)

        pixels = []
        for row in range(8):
            bp0 = tile_bytes[row * 2]
            bp1 = tile_bytes[row * 2 + 1]
            row_px = []
            for bit in range(7, -1, -1):
                p = ((bp0 >> bit) & 1) | (((bp1 >> bit) & 1) << 1)
                row_px.append(p)
            pixels.append(row_px)
        tiles.append(pixels)
    return tiles


def render_ppm(tiles, palette, out_path, cols=16, scale=3):
    """Render tiles to PPM, optionally convert to PNG."""
    rows = (len(tiles) + cols - 1) // cols
    img_w = cols * 8
    img_h = rows * 8

    ppm_path = out_path.replace(".png", ".ppm") if out_path.endswith(".png") else out_path
    with open(ppm_path, "w") as f:
        f.write(f"P3\n{img_w} {img_h}\n255\n")
        for tile_row in range(rows):
            for pixel_row in range(8):
                for tile_col in range(cols):
                    ti = tile_row * cols + tile_col
                    if ti < len(tiles):
                        for px in tiles[ti][pixel_row]:
                            r, g, b = palette[min(px, len(palette)-1)]
                            f.write(f"{r} {g} {b} ")
                    else:
                        r, g, b = palette[0]
                        for _ in range(8):
                            f.write(f"{r} {g} {b} ")
                f.write("\n")

    print(f"  Wrote {ppm_path} ({img_w}x{img_h}, {len(tiles)} tiles)")

    # Convert to PNG if magick available
    if out_path.endswith(".png"):
        try:
            subprocess.run(
                ["magick", ppm_path, "-scale", f"{scale*100}%", out_path],
                capture_output=True, check=True
            )
            print(f"  Wrote {out_path} (scaled {scale}x)")
        except (FileNotFoundError, subprocess.CalledProcessError) as e:
            print(f"  PNG conversion failed: {e}")

    return ppm_path


def main():
    rom_path = sys.argv[1] if len(sys.argv) > 1 else ROM_PATH
    out_base = sys.argv[2] if len(sys.argv) > 2 else None

    print(f"Reading ROM: {rom_path}")
    with open(rom_path, "rb") as f:
        rom = f.read()
    print(f"  ROM size: {len(rom)} bytes (${len(rom):X})")

    # Check for copier header (512 bytes)
    header_offset = 0
    if len(rom) % 0x8000 == 512:
        header_offset = 512
        print(f"  Copier header detected, skipping {header_offset} bytes")

    font_data = rom[header_offset + FONT_ROM_ADDR:]
    print(f"\nFont tileset at ROM ${FONT_ROM_ADDR:06X}:")
    print(f"  First 16 bytes: {' '.join(f'{b:02X}' for b in font_data[:16])}")

    # Decompress
    print("\nDecompressing...")
    vram = decompress_tileset(font_data, vram_start=VRAM_BASE)

    if not vram:
        print("ERROR: No data decompressed!")
        return

    min_addr = min(vram.keys())
    max_addr = max(vram.keys())
    total_words = max_addr - min_addr + 1
    num_tiles_4bpp = total_words // 16
    num_tiles_2bpp = total_words // 8

    print(f"\n  VRAM: ${min_addr:04X}-${max_addr:04X} ({total_words} words)")
    print(f"  4bpp: {num_tiles_4bpp} tiles | 2bpp: {num_tiles_2bpp} tiles")

    # Palette: black bg, white/gray font strokes
    pal_bw = [
        (0, 0, 0),       # 0: black (background)
        (85, 85, 85),    # 1: dark gray
        (170, 170, 170), # 2: light gray
        (255, 255, 255), # 3: white (main stroke)
    ] + [(64, 0, 0)] * 12  # 4-15: dark red = debug

    # Game palette from key findings (green/olive intro text)
    pal_game = [
        (0, 0, 0),         # 0: transparent/black
        (96, 248, 80),     # color 17 approx: bright yellow-green
        (96, 248, 80),     # 2
        (96, 248, 80),     # 3
    ] + [(0, 112, 0)] * 12  # rest: dark green

    tools_dir = os.path.dirname(os.path.abspath(__file__))

    # Export 4bpp version
    print("\n--- 4bpp decode ---")
    tiles_4bpp = vram_to_tiles_4bpp(vram, min_addr, num_tiles_4bpp)
    out4 = out_base or os.path.join(tools_dir, "sbd_font_export_4bpp.png")
    render_ppm(tiles_4bpp, pal_bw, out4, cols=16, scale=4)

    # Export 2bpp version (font is likely 2bpp-in-4bpp, only bp0/bp1 matter)
    print("\n--- 2bpp decode ---")
    tiles_2bpp = vram_to_tiles_2bpp(vram, min_addr, num_tiles_2bpp)
    out2 = os.path.join(tools_dir, "sbd_font_export_2bpp.png")
    render_ppm(tiles_2bpp, pal_bw, out2, cols=16, scale=4)

    # Game-colored version
    print("\n--- Game palette (4bpp) ---")
    out_game = os.path.join(tools_dir, "sbd_font_export_game.png")
    render_ppm(tiles_4bpp, pal_game, out_game, cols=16, scale=4)

    print("\nDone.")


if __name__ == "__main__":
    main()
