#!/usr/bin/env python3
"""Find and export the SBD dialogue font from ROM.

FINDINGS:
  - The dialogue font is tileset $0197 at ROM $1A564A (SNES $B4:D64A)
  - 512 tiles (2bpp) = 128 characters (16x16 pixels each)
  - Tile layout within each 16x16 char: column-major (TL, BL, TR, BR)
  - 1bpp-like encoding (bp0 only, bp1 mirrors or zero)
  - VRAM destination: $4000-$4FFF
  - Tileset table format: [bank, flags, ptr_lo, ptr_hi] at ROM $00536B

  Character map (128 chars, 16 per row):
    Row 0-1: Status abbreviations (SS, EE, AB, BH, LV, FC, etc.)
    Row 2:   !"#$%&'()*+,-./0123456789:;<=>?
    Row 3:   @ABCDEFGHIJKLMNOPQRSTUVWXYZ[yen]^_
    Row 4:   `abcdefghijklmnopqrstuvwxyz{|}~
    Row 5:   wo, a, i, u, e, o, ya, yu, yo, small-tsu, space, aiueo-kakiku-kekosa-shisuseso
    Row 6:   Punctuation + katakana (a-so row)
    Row 7:   Katakana (ta-n) + more kana
    Row 8:   Hiragana (ta-chi-tsu-te-to-na-ni...wa-n)

  Kanji overlay pages (tilesets $01E7-$01FF, 128 tiles = 32 chars each):
    Replace VRAM $4000-$4403 (first 32 chars) with context-specific kanji
    These are swapped in during dialogue to provide scene-relevant kanji

Usage: python3 find_dialogue_font.py [rom_path]
"""

import sys
import os
import subprocess

ROM_PATH = os.path.expanduser("~/Downloads/SBD.sfc")
TILESET_TABLE_ROM = 0x00536B

VRAM_OFFSETS = [
    -1, -32, -33, -132, -64, -65, -128, -129,
    -130, -2, -66, -256, -257, -258, -4, -512
]


class BitStream:
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


def decompress_tileset(rom_data, vram_start=0x4000, max_words=16384, existing_vram=None):
    """Decompress tileset using SBD's custom 3-stream format.
    If existing_vram is provided, overlays onto it (for kanji page swaps)."""
    if len(rom_data) < 10:
        return None
    off_bp01 = rom_data[0] | (rom_data[1] << 8)
    off_bp23 = rom_data[2] | (rom_data[3] << 8)
    off_idx  = rom_data[4] | (rom_data[5] << 8)
    raw_count = rom_data[6] | (rom_data[7] << 8)
    count = raw_count >> 1
    if count == 0 or count > max_words:
        return None
    if off_bp01 >= len(rom_data) or off_bp23 >= len(rom_data) or off_idx >= len(rom_data):
        return None
    if off_bp01 < 10 or off_bp23 < 10 or off_idx < 10:
        return None
    try:
        bp01 = BitStream(rom_data[off_bp01:], 2, 8)
        bp23_data = rom_data[off_bp23:]
        bp23_pos = 0
        idx = BitStream(rom_data[off_idx:], 4, 4)
        vram = dict(existing_vram) if existing_vram else {}
        vram_ptr = vram_start
        last_ref = 0
        for _ in range(count):
            cmd = bp01.read()
            if cmd == 0:
                if bp23_pos + 1 >= len(bp23_data):
                    return None
                val = bp23_data[bp23_pos] | (bp23_data[bp23_pos + 1] << 8)
                bp23_pos += 2
                vram[vram_ptr] = val
            elif cmd == 1:
                break
            elif cmd == 2:
                last_ref = idx.read()
                if last_ref >= len(VRAM_OFFSETS):
                    return None
                offset = VRAM_OFFSETS[last_ref]
                src = (vram_ptr + offset) & 0xFFFF
                vram[vram_ptr] = vram.get(src, 0)
            elif cmd == 3:
                offset = VRAM_OFFSETS[last_ref]
                src = (vram_ptr + offset) & 0xFFFF
                vram[vram_ptr] = vram.get(src, 0)
            vram_ptr += 1
        return vram
    except (IndexError, KeyError):
        return None


def snes_to_rom(bank, addr):
    b = bank & 0x7F
    return b * 0x8000 + (addr - 0x8000) if addr >= 0x8000 else b * 0x8000 + addr


def get_tileset_rom(rom, table_start, ts_id):
    off = table_start + ts_id * 4
    if off + 3 >= len(rom):
        return None, 0, 0
    bank = rom[off]
    ptr = rom[off + 2] | (rom[off + 3] << 8)
    if bank == 0 or ptr < 0x8000:
        return None, bank, ptr
    return snes_to_rom(bank, ptr), bank, ptr


def render_font_colmajor(vram_dict, out_path, vram_base=0x4000, total_words=4096,
                         cols_chars=16, scale=3):
    """Render VRAM font data as 16x16 characters with column-major tile layout."""
    tile_size = 16  # 2bpp: 16 bytes per 8x8 tile
    linear = bytearray(total_words * 2)
    for addr, val in vram_dict.items():
        off = (addr - vram_base) * 2
        if 0 <= off < len(linear) - 1:
            linear[off] = val & 0xFF
            linear[off + 1] = (val >> 8) & 0xFF

    num_tiles = total_words // 8
    num_chars = num_tiles // 4
    if num_chars == 0:
        return None

    char_rows = (num_chars + cols_chars - 1) // cols_chars
    img_w = cols_chars * 16
    img_h = char_rows * 16
    pal = [(0,0,0), (85,85,85), (170,170,170), (255,255,255)]

    ppm_path = out_path.replace('.png', '.ppm')
    with open(ppm_path, "w") as f:
        f.write(f"P3\n{img_w} {img_h}\n255\n")
        for cr in range(char_rows):
            for py in range(16):
                for cc in range(cols_chars):
                    ch_idx = cr * cols_chars + cc
                    if ch_idx >= num_chars:
                        for _ in range(16):
                            f.write("0 0 0 ")
                        continue
                    base_tile = ch_idx * 4
                    # Column-major: TL(0), BL(1), TR(2), BR(3)
                    if py < 8:
                        left_tile = base_tile      # TL
                        right_tile = base_tile + 2  # TR
                    else:
                        left_tile = base_tile + 1   # BL
                        right_tile = base_tile + 3   # BR
                    row = py % 8
                    for tile_idx in [left_tile, right_tile]:
                        toff = tile_idx * tile_size + row * 2
                        if toff + 1 < len(linear):
                            bp0 = linear[toff]
                            bp1 = linear[toff + 1]
                        else:
                            bp0 = bp1 = 0
                        for bit in range(7, -1, -1):
                            p = ((bp0 >> bit) & 1) | (((bp1 >> bit) & 1) << 1)
                            r, g, b = pal[p]
                            f.write(f"{r} {g} {b} ")
                f.write("\n")

    try:
        subprocess.run(["magick", ppm_path, "-scale", f"{scale*100}%", out_path],
                       capture_output=True, check=True)
        print(f"  Wrote {out_path} ({img_w}x{img_h} * {scale}x)")
        return out_path
    except:
        print(f"  Wrote {ppm_path} ({img_w}x{img_h})")
        return ppm_path


def main():
    rom_path = sys.argv[1] if len(sys.argv) > 1 else ROM_PATH

    print(f"SBD Dialogue Font Finder")
    print(f"========================")
    print(f"ROM: {rom_path}")
    with open(rom_path, "rb") as f:
        rom = f.read()
    print(f"Size: {len(rom)} bytes (${len(rom):X})")

    header_offset = 0
    if len(rom) % 0x8000 == 512:
        header_offset = 512
        print(f"Copier header: {header_offset} bytes")

    tools_dir = os.path.dirname(os.path.abspath(__file__))
    table_start = header_offset + TILESET_TABLE_ROM

    # ================================================================
    # Base dialogue font: tileset $0197
    # ================================================================
    print(f"\n--- Base Dialogue Font: Tileset $0197 ---")
    rom_off, bank, ptr = get_tileset_rom(rom, table_start, 0x0197)
    print(f"  Location: ROM ${rom_off:06X} (SNES ${bank:02X}:{ptr:04X})")

    vram = decompress_tileset(rom[rom_off:])
    n_words = len(vram)
    min_v = min(vram.keys())
    max_v = max(vram.keys())
    print(f"  VRAM: ${min_v:04X}-${max_v:04X} ({n_words} words)")
    print(f"  Content: {n_words // 8} tiles (2bpp) = {n_words // 32} chars (16x16)")
    print(f"  Tile order: column-major (TL, BL, TR, BR per character)")

    out = render_font_colmajor(vram, os.path.join(tools_dir, "sbd_dialogue_font.png"))

    # ================================================================
    # Kanji overlay pages
    # ================================================================
    print(f"\n--- Kanji Overlay Pages ---")
    print(f"  These replace the first 32 chars during dialogue scenes.")
    print(f"  Each provides scene-specific kanji characters.")

    overlay_ids = list(range(0x01E7, 0x0200))
    overlay_count = 0

    for ts_id in overlay_ids:
        rom_off_ov, bank_ov, ptr_ov = get_tileset_rom(rom, table_start, ts_id)
        if rom_off_ov is None:
            continue
        vram_ov = decompress_tileset(rom[rom_off_ov:], existing_vram=vram)
        if not vram_ov:
            continue

        changed = sum(1 for k, v in vram_ov.items()
                      if k in vram and vram[k] != v)
        overlay_count += 1

        if ts_id in [0x01E9, 0x01F4, 0x01F9, 0x01FA, 0x01FE]:
            # Render a few representative overlays
            out_ov = render_font_colmajor(
                vram_ov,
                os.path.join(tools_dir, f"sbd_font_kanji_{ts_id:04X}.png")
            )

    print(f"\n  Total kanji pages: {overlay_count} (tilesets $01E7-$01FF)")

    # ================================================================
    # Summary
    # ================================================================
    print(f"\n{'='*60}")
    print(f"SUMMARY")
    print(f"{'='*60}")
    print(f"""
  DIALOGUE FONT FOUND: Tileset $0197
    ROM address:    $1A564A (SNES $B4:D64A)
    Table entry:    $00536B + $0197*4 = $005DCB
    Table format:   [bank=$B4, flags=$00, ptr_lo=$4A, ptr_hi=$D6]
    VRAM dest:      $4000-$4FFF (4096 words)
    Tile count:     512 tiles (2bpp, 8x8)
    Char count:     128 characters (16x16 pixels each)
    Tile order:     Column-major (TL, BL, TR, BR) per 16x16 char
    Encoding:       1bpp-like in 2bpp format (bp0 only, bp1 mirrors)

  KANJI OVERLAY PAGES: Tilesets $01E7-$01FF ({overlay_count} pages)
    ROM range:      ~$1CFB02-$1D4200 (bank $BA)
    Each page:      128 tiles = 32 chars (16x16)
    VRAM target:    $4000-$4403 (replaces first 32 chars)
    Purpose:        Scene-specific kanji swapped in during dialogue

  OUTPUT FILES:
    sbd_dialogue_font.png      - Base font (128 chars, 16x16)
    sbd_font_kanji_*.png       - Sample composite fonts with kanji pages
""")


if __name__ == "__main__":
    main()
